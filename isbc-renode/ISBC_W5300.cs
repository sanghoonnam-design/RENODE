using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Network
{
    // W5300 direct-bus model with opt-in, loopback-only TCP server sockets.
    public sealed class ISBC_W5300 : IWordPeripheral, IKnownSize, IGPIOReceiver, IDisposable
    {
        public ISBC_W5300()
        {
            for(var index = 0; index < sockets.Length; index++)
            {
                sockets[index] = new SocketBuffers();
            }
            Reset();
        }

        public long Size => RegisterSpaceSize;

        public void Reset()
        {
            lock(sync)
            {
                foreach(var socket in sockets)
                {
                    socket.Clear();
                }
                ResetRegisters();
            }
        }

        public void ExposeTcpPort(ushort guestPort, ushort hostPort)
        {
            lock(sync)
            {
                if(guestPort == 0 || hostPort == 0)
                {
                    throw new RecoverableException("TCP port numbers must be between 1 and 65535.");
                }
                if(listeners.TryGetValue(guestPort, out var previous) &&
                    ((IPEndPoint)previous.LocalEndpoint).Port == hostPort)
                {
                    return;
                }

                var listener = new TcpListener(IPAddress.Loopback, hostPort) { ExclusiveAddressUse = true };
                try
                {
                    listener.Start();
                }
                catch(SocketException exception)
                {
                    listener.Stop();
                    throw new RecoverableException($"Cannot listen on 127.0.0.1:{hostPort}: {exception.Message}");
                }
                previous?.Stop();
                listeners[guestPort] = listener;
            }
        }

        public void Dispose()
        {
            lock(sync)
            {
                foreach(var listener in listeners.Values)
                {
                    listener.Stop();
                }
                listeners.Clear();
                foreach(var socket in sockets)
                {
                    socket.Clear();
                }
            }
        }

        private void ResetRegisters()
        {
            Array.Clear(registers, 0, registers.Length);
            Store(Mode, 0xB800); // 16-bit data bus, direct mode.
            Store(ChipId, 0x5300);
            Store(MemoryType, 0x00FF);
            for(var offset = TxMemorySize; offset < MemoryType; offset += 2)
            {
                Store(offset, 0x0808);
            }
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0 && !value)
            {
                Reset();
            }
        }

        public ushort ReadWord(long offset)
        {
            lock(sync)
            {
                if(offset < SocketBase)
                {
                    return Load(offset);
                }
                var index = (int)((offset - SocketBase) / SocketStride);
                var register = (offset - SocketBase) % SocketStride;
                var socketBase = offset - register;
                var socket = sockets[index];
                switch(register)
                {
                    case SocketStatus:
                        if(socket.ReportListening)
                        {
                            socket.ReportListening = false;
                            return Listening;
                        }
                        ServiceSocket(index, socketBase);
                        break;
                    case SocketInterrupt:
                        ServiceSocket(index, socketBase);
                        break;
                    case SocketTxFreeSizeHigh:
                    case SocketTxFreeSizeLow:
                        var freeBytes = Math.Max(0, BufferSize(TxMemorySize, index) - socket.Tx.Count - socket.TxLeft);
                        return register == SocketTxFreeSizeHigh ? (ushort)(freeBytes >> 16) : (ushort)freeBytes;
                    case SocketRxSizeHigh:
                    case SocketRxSizeLow:
                        ServiceSocket(index, socketBase);
                        var rxBytes = 2 * (socket.Rx.Count + socket.RxWordsRead);
                        return register == SocketRxSizeHigh ? (ushort)(rxBytes >> 16) : (ushort)rxBytes;
                    case SocketRxFifo:
                        if(socket.Rx.Count == 0)
                        {
                            return 0;
                        }
                        socket.RxWordsRead++;
                        return FifoOrder(socket.Rx.Dequeue());
                }
                return Load(offset);
            }
        }

        public void WriteWord(long offset, ushort value)
        {
            lock(sync)
            {
                WriteRegister(offset, value);
            }
        }

        private void WriteRegister(long offset, ushort value)
        {
            if(offset == Mode && (value & SoftwareReset) != 0)
            {
                Reset();
                return;
            }
            if(offset == ChipId)
            {
                return;
            }
            if(offset >= SocketBase)
            {
                var register = (offset - SocketBase) % SocketStride;
                switch(register)
                {
                    case SocketCommand:
                        ExecuteCommand((int)((offset - SocketBase) / SocketStride), offset - register, value);
                        return;
                    case SocketTxFifo:
                        var index = (int)((offset - SocketBase) / SocketStride);
                        var tx = sockets[index].Tx;
                        if(tx.Count + sockets[index].TxLeft + 2 <= BufferSize(TxMemorySize, index))
                        {
                            var word = FifoOrder(value);
                            tx.Add((byte)(word >> 8));
                            tx.Add((byte)word);
                        }
                        return;
                    case SocketInterrupt:
                        Store(offset, (ushort)(Load(offset) & ~value));
                        return;
                    case SocketStatus:
                    case SocketTxFreeSizeHigh:
                    case SocketTxFreeSizeLow:
                    case SocketRxSizeHigh:
                    case SocketRxSizeLow:
                    case SocketRxFifo:
                        return;
                }
            }
            Store(offset, value);
        }

        private void ExecuteCommand(int index, long socketBase, ushort command)
        {
            var statusOffset = socketBase + SocketStatus;
            var socket = sockets[index];
            switch(command & 0xFF)
            {
                case Open:
                    socket.Clear();
                    switch(Load(socketBase + SocketMode) & 0x0F)
                    {
                        case Tcp:
                            Store(statusOffset, Init);
                            break;
                        case Udp:
                            Store(statusOffset, UdpOpen);
                            break;
                    }
                    break;
                case Listen:
                    if(Load(statusOffset) == Init)
                    {
                        // The driver's immediate post-LISTEN check must observe LISTEN.
                        socket.ReportListening = true;
                        Store(statusOffset, Listening);
                    }
                    break;
                case Disconnect:
                case Close:
                    socket.Clear();
                    Store(statusOffset, Closed);
                    break;
                case Send:
                    if(socket.Client == null)
                    {
                        SetInterrupt(socketBase, TimedOut);
                        break;
                    }
                    var size = ((uint)Load(socketBase + SocketTxWriteSize) << 16) |
                        Load(socketBase + SocketTxWriteSize + 2);
                    if(size > socket.Tx.Count || socket.TxLeft > 0)
                    {
                        SetInterrupt(socketBase, TimedOut);
                        break;
                    }
                    socket.TxLeft = (int)size;
                    socket.TxOffset = 0;
                    socket.PendingTx = socket.Tx.ToArray();
                    socket.Tx.Clear();
                    FlushTx(socketBase, socket);
                    break;
                case Receive:
                    socket.RxWordsRead = 0;
                    break;
            }
            // The command register reads as zero once the device accepts a command.
        }

        private void ServiceSocket(int index, long socketBase)
        {
            var socket = sockets[index];
            var status = Load(socketBase + SocketStatus);
            try
            {
                if(status == Listening && !socket.ReportListening &&
                    listeners.TryGetValue(Load(socketBase + SocketPort), out var listener) && listener.Pending())
                {
                    var client = listener.AcceptSocket();
                    socket.Client = client;
                    client.Blocking = false;
                    client.NoDelay = true;
                    Store(socketBase + SocketStatus, Established);
                    SetInterrupt(socketBase, Connected);
                    status = Established;
                }

                if(socket.Client == null)
                {
                    return;
                }
                if(socket.TxLeft > 0)
                {
                    FlushTx(socketBase, socket);
                }
                if(status != Established || socket.Client == null || socket.Rx.Count > 0 || socket.RxWordsRead > 0)
                {
                    return;
                }

                if(!socket.Client.Poll(0, SelectMode.SelectRead))
                {
                    return;
                }
                var aligned = (Load(socketBase + SocketMode) & ReceiveAlignment) != 0;
                var capacity = (BufferSize(RxMemorySize, index) - (aligned ? 0 : 2)) & ~1;
                var limit = Math.Min(receiveBuffer.Length, capacity);
                if(limit <= 0)
                {
                    return;
                }
                var count = socket.Client.Receive(receiveBuffer, 0, limit, SocketFlags.None);
                if(count == 0)
                {
                    Store(socketBase + SocketStatus, CloseWait);
                    SetInterrupt(socketBase, Disconnected);
                    return;
                }
                if(!aligned)
                {
                    socket.Rx.Enqueue((ushort)count);
                }
                for(var position = 0; position < count; position += 2)
                {
                    var low = position + 1 < count ? receiveBuffer[position + 1] : 0;
                    socket.Rx.Enqueue((ushort)((receiveBuffer[position] << 8) | low));
                }
                SetInterrupt(socketBase, DataReceived);
            }
            catch(SocketException exception) when(exception.SocketErrorCode == SocketError.WouldBlock)
            {
                // A non-blocking read can race with the host closing the connection.
            }
            catch(SocketException)
            {
                socket.Clear();
                Store(socketBase + SocketStatus, Closed);
                SetInterrupt(socketBase, TimedOut);
            }
        }

        private void FlushTx(long socketBase, SocketBuffers socket)
        {
            try
            {
                if(socket.TxLeft > 0)
                {
                    var sent = socket.Client.Send(socket.PendingTx, socket.TxOffset, socket.TxLeft, SocketFlags.None);
                    socket.TxOffset += sent;
                    socket.TxLeft -= sent;
                }
                if(socket.TxLeft == 0)
                {
                    socket.PendingTx = null;
                    SetInterrupt(socketBase, SendComplete);
                }
            }
            catch(SocketException exception) when(exception.SocketErrorCode == SocketError.WouldBlock)
            {
                // Retry when the firmware polls socket status or interrupt flags.
            }
            catch(SocketException)
            {
                socket.Clear();
                Store(socketBase + SocketStatus, Closed);
                SetInterrupt(socketBase, TimedOut);
            }
        }

        private int BufferSize(int baseOffset, int socket)
        {
            var sizes = Load(baseOffset + (socket & ~1));
            var sizeKiB = ((socket & 1) == 0 ? sizes >> 8 : sizes) & 0xFF;
            return sizeKiB * 1024;
        }

        private ushort FifoOrder(ushort word)
        {
            return (Load(Mode) & FifoSwap) == 0 ? word : (ushort)((word << 8) | (word >> 8));
        }

        private void SetInterrupt(long socketBase, int flag)
        {
            var offset = socketBase + SocketInterrupt;
            Store(offset, (ushort)(Load(offset) | flag));
        }

        private ushort Load(long offset)
        {
            return registers[offset / 2];
        }

        private void Store(long offset, ushort value)
        {
            registers[offset / 2] = value;
        }

        private sealed class SocketBuffers
        {
            public void Clear()
            {
                Client?.Dispose();
                Client = null;
                Rx.Clear();
                Tx.Clear();
                RxWordsRead = 0;
                PendingTx = null;
                TxOffset = 0;
                TxLeft = 0;
                ReportListening = false;
            }

            public Socket Client;
            public byte[] PendingTx;
            public int RxWordsRead;
            public int TxOffset;
            public int TxLeft;
            public bool ReportListening;
            public readonly Queue<ushort> Rx = new Queue<ushort>();
            public readonly List<byte> Tx = new List<byte>();
        }

        private readonly object sync = new object();
        private readonly Dictionary<ushort, TcpListener> listeners = new Dictionary<ushort, TcpListener>();
        private readonly SocketBuffers[] sockets = new SocketBuffers[8];
        private readonly byte[] receiveBuffer = new byte[1400];
        private readonly ushort[] registers = new ushort[RegisterSpaceSize / 2];

        private const int RegisterSpaceSize = 0x400;
        private const int Mode = 0x000;
        private const int ChipId = 0x0FE;
        private const int TxMemorySize = 0x020;
        private const int RxMemorySize = 0x028;
        private const int MemoryType = 0x030;
        private const int SoftwareReset = 0x80;
        private const int FifoSwap = 0x100;
        private const int SocketBase = 0x200;
        private const int SocketStride = 0x40;
        private const int SocketMode = 0x00;
        private const int SocketCommand = 0x02;
        private const int SocketInterrupt = 0x06;
        private const int SocketStatus = 0x08;
        private const int SocketPort = 0x0A;
        private const int SocketTxWriteSize = 0x20;
        private const int SocketTxFreeSizeHigh = 0x24;
        private const int SocketTxFreeSizeLow = 0x26;
        private const int SocketRxSizeHigh = 0x28;
        private const int SocketRxSizeLow = 0x2A;
        private const int SocketTxFifo = 0x2E;
        private const int SocketRxFifo = 0x30;
        private const int ReceiveAlignment = 0x100;
        private const int Tcp = 0x01;
        private const int Udp = 0x02;
        private const int Open = 0x01;
        private const int Listen = 0x02;
        private const int Disconnect = 0x08;
        private const int Close = 0x10;
        private const int Send = 0x20;
        private const int Receive = 0x40;
        private const int Connected = 0x01;
        private const int Disconnected = 0x02;
        private const int DataReceived = 0x04;
        private const int TimedOut = 0x08;
        private const int SendComplete = 0x10;
        private const ushort Closed = 0x00;
        private const ushort Init = 0x13;
        private const ushort Listening = 0x14;
        private const ushort Established = 0x17;
        private const ushort CloseWait = 0x1C;
        private const ushort UdpOpen = 0x22;
    }
}

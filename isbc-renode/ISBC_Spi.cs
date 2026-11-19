using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    // ISBC 보드의 STM32F746 SPI1(IO 확장), SPI4(TMC2660), SPI5(TMC429).
    // 펌웨어 드라이버가 DR을 8비트 packed 방식(FRXTH, 8비트 접근)으로 쓰는데 Renode 기본 STM32SPI 모델은
    // 이 동작을 지원하지 않아서 이 모델로 바꾼다. 펌웨어는 고치지 않는다.
    public sealed class ISBC_Spi : NullRegistrationPointPeripheralContainer<ISPIPeripheral>,
        IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public ISBC_Spi(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            Reset();
        }

        public long Size => 0x400;
        public GPIO IRQ { get; }

        public byte ReadByte(long offset)
        {
            return offset == Data ? PopByte() : (byte)ReadDoubleWord(offset);
        }

        public ushort ReadWord(long offset)
        {
            if(offset != Data)
            {
                return (ushort)ReadDoubleWord(offset);
            }

            var low = PopByte();
            return (ushort)(low | (PopByte() << 8));
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case Control1: return control1;
                case Control2: return control2;
                case Status: return GetStatus();
                case Data: return ReadWord(Data);
                default: return (offset >= 0 && offset < registers.Length * 4) ? registers[offset / 4] : 0;
            }
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset == Data)
            {
                Exchange(value);
                return;
            }
            WriteDoubleWord(offset, value);
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset == Data)
            {
                Exchange((byte)value);
                Exchange((byte)(value >> 8));
                return;
            }
            WriteDoubleWord(offset, value);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case Data:
                    WriteWord(Data, (ushort)value);
                    break;
                case Control1:
                    if((control1 & Enabled) != 0 && (value & Enabled) == 0)
                    {
                        RegisteredPeripheral?.FinishTransmission();
                    }
                    control1 = value;
                    break;
                case Control2:
                    control2 = value;
                    UpdateIrq();
                    break;
                default:
                    if(offset >= 0 && offset < registers.Length * 4)
                    {
                        registers[offset / 4] = value;
                    }
                    break;
            }
        }

        public override void Reset()
        {
            control1 = 0;
            control2 = 0x700;
            Array.Clear(registers, 0, registers.Length);
            received.Clear();
            IRQ.Unset();
        }

        private void Exchange(byte value)
        {
            if((control1 & Enabled) == 0)
            {
                return;
            }
            var peripheral = RegisteredPeripheral;
            var response = peripheral == null ? (byte)0xFF : peripheral.Transmit(value);
            if(received.Count < 4)
            {
                received.Enqueue(response);
            }
            UpdateIrq();
        }

        private byte PopByte()
        {
            var value = received.Count > 0 ? received.Dequeue() : (byte)0;
            UpdateIrq();
            return value;
        }

        private uint GetStatus()
        {
            var threshold = (control2 & QuarterThreshold) != 0 ? 1 : 2;
            var fifoLevel = (uint)Math.Min(3, received.Count) << 9;
            return TxEmpty | fifoLevel | (received.Count >= threshold ? RxReady : 0u);
        }

        private void UpdateIrq()
        {
            var status = GetStatus();
            IRQ.Set(((control2 & (1u << 7)) != 0 && (status & TxEmpty) != 0)
                || ((control2 & (1u << 6)) != 0 && (status & RxReady) != 0));
        }

        private uint control1;
        private uint control2;
        private readonly uint[] registers = new uint[9];
        private readonly Queue<byte> received = new Queue<byte>();
        private const long Control1 = 0x00, Control2 = 0x04, Status = 0x08, Data = 0x0C;
        private const uint Enabled = 1u << 6, QuarterThreshold = 1u << 12, RxReady = 1, TxEmpty = 2;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;

namespace Antmicro.Renode.Peripherals.SPI
{
    // ISBC PCB_2 IO 블록 (회로도 sheet 14, 15). SPI1에 MCP23S17 두 개가 붙어 있다.
    //   U16 입력용: IN_NCS  = PA8 (GPIO 0), 포토커플러 입력 IN0~IN15 (J11)
    //   U17 출력용: OUT_NCS = PA4 (GPIO 1), 포토커플러 + TBD62783 출력 OUT0~OUT15 (J12)
    //   IO_NRST = PB2 (GPIO 2), 두 칩 공통 리셋(Low = 리셋)
    // 보드 밖 연결:
    //   입력 IN0~15 = GPIO 16~31 (true = 입력 ON, 포토커플러에 전류가 흘러 MCP 핀이 Low)
    //   출력 OUT0~15 = Connections 0~15 (true = 출력 ON, MCP 핀을 출력 Low로 둔 상태)
    // 기구물 모델은 이 IO 블록 아래에 등록해(@ io) 출력을 받고 입력을 넣는다.
    public sealed class ISBC_IoExpander : ISPIPeripheral, IGPIOReceiver, INumberedGPIOOutput,
        IPeripheralContainer<IGPIOReceiver, NullRegistrationPoint>
    {
        public ISBC_IoExpander(IMachine machine)
        {
            this.machine = machine;
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < PinCount; ++i)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;
            Reset();
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public void Register(IGPIOReceiver peripheral, NullRegistrationPoint registrationPoint)
        {
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
            attached.Add(peripheral);
        }

        public void Unregister(IGPIOReceiver peripheral)
        {
            machine.UnregisterAsAChildOf(this, peripheral);
            attached.Remove(peripheral);
        }

        public IEnumerable<NullRegistrationPoint> GetRegistrationPoints(IGPIOReceiver peripheral)
        {
            return attached.Contains(peripheral) ? new[] { NullRegistrationPoint.Instance } : new NullRegistrationPoint[0];
        }

        public IEnumerable<IRegistered<IGPIOReceiver, NullRegistrationPoint>> Children =>
            attached.Select(p => Registered.Create(p, NullRegistrationPoint.Instance)).ToList();

        // 모니터에서 입력 ON/OFF: sysbus.spi1.io SetInput 3 true
        public void SetInput(int input, bool on)
        {
            CheckPin(input);
            OnGPIO(FirstInputGpio + input, on);
        }

        public bool GetInput(int input)
        {
            CheckPin(input);
            lock(sync)
            {
                return (inputsOn & (1 << input)) != 0;
            }
        }

        public bool GetOutput(int output)
        {
            CheckPin(output);
            lock(sync)
            {
                return (outputsOn & (1 << output)) != 0;
            }
        }

        // 입력/출력 16점 상태를 비트로 본다(비트 n = IN n / OUT n, 1 = ON).
        public ushort Inputs { get { lock(sync) { return inputsOn; } } }
        public ushort Outputs { get { lock(sync) { return outputsOn; } } }

        public byte Transmit(byte data)
        {
            byte result;
            ushort changed, state;
            lock(sync)
            {
                result = Exchange(data);
                changed = UpdateOutputs();
                state = outputsOn;
            }
            FireOutputs(changed, state);
            return result;
        }

        public void FinishTransmission()
        {
            // 칩 선택은 GPIO 에지로 나뉜다.
        }

        public void OnGPIO(int number, bool value)
        {
            ushort changed = 0, state;
            lock(sync)
            {
                if(number == ResetPin)
                {
                    resetHeld = !value;
                    if(resetHeld)
                    {
                        ResetChips();
                        changed = UpdateOutputs();
                    }
                }
                else if(number == InputChip || number == OutputChip)
                {
                    var isSelected = !value;
                    if(selected[number] != isSelected)
                    {
                        selected[number] = isSelected;
                        byteIndex = 0;
                    }
                }
                else if(number >= FirstInputGpio && number < FirstInputGpio + PinCount)
                {
                    var mask = (ushort)(1 << (number - FirstInputGpio));
                    inputsOn = value ? (ushort)(inputsOn | mask) : (ushort)(inputsOn & ~mask);
                }
                state = outputsOn;
            }
            FireOutputs(changed, state);
        }

        public void Reset()
        {
            ushort changed, state;
            lock(sync)
            {
                resetHeld = false;
                Array.Clear(selected, 0, selected.Length);
                ResetChips();
                // 입력은 보드 밖 신호라서 리셋해도 그대로 둔다.
                changed = UpdateOutputs();
                state = outputsOn;
            }
            FireOutputs(changed, state);
        }

        private byte Exchange(byte data)
        {
            if(resetHeld || selected[InputChip] == selected[OutputChip])
            {
                return 0xFF;
            }

            var chip = selected[InputChip] ? InputChip : OutputChip;
            var registers = chips[chip];
            switch(byteIndex++)
            {
                case 0:
                    validOpcode = (data & 0xFE) == 0x40;
                    reading = (data & 1) != 0;
                    return 0;
                case 1:
                    register = data;
                    return 0;
                default:
                    if(!validOpcode || register >= registers.Length)
                    {
                        return 0xFF;
                    }

                    var result = reading ? Read(chip, register) : (byte)0;
                    if(!reading)
                    {
                        Write(chip, register, data);
                    }
                    if((registers[IoControl] & SequentialOperationDisable) == 0)
                    {
                        register = (register + 1) % registers.Length;
                    }
                    return result;
            }
        }

        private byte Read(int chip, int address)
        {
            var registers = chips[chip];
            if(address == Gpio || address == Gpio + 1)
            {
                var port = address - Gpio;
                var direction = registers[Direction + port];
                // 입력 ON이면 포토커플러가 MCP 핀을 Low로 당긴다. 출력 칩의 핀은 풀업으로 High.
                var pins = chip == InputChip ? (byte)(~inputsOn >> (8 * port)) : (byte)0xFF;
                return (byte)(((pins ^ registers[Polarity + port]) & direction)
                    | (registers[OutputLatch + port] & ~direction));
            }
            return registers[address];
        }

        private void Write(int chip, int address, byte data)
        {
            var registers = chips[chip];
            if(address == IoControl || address == IoControl + 1)
            {
                registers[IoControl] = registers[IoControl + 1] = data;
                return;
            }
            if(address == Gpio || address == Gpio + 1)
            {
                address += OutputLatch - Gpio;
            }
            registers[address] = data;
        }

        // 출력 칩 핀이 출력 Low이면 해당 OUT이 ON. 바뀐 비트를 돌려준다.
        private ushort UpdateOutputs()
        {
            var regs = chips[OutputChip];
            var direction = regs[Direction] | (regs[Direction + 1] << 8);
            var latch = regs[OutputLatch] | (regs[OutputLatch + 1] << 8);
            var now = (ushort)~(direction | latch);
            var changed = (ushort)(now ^ outputsOn);
            outputsOn = now;
            return changed;
        }

        // 연결된 기구물이 IO 블록을 다시 부를 수 있으므로 잠금 밖에서 알린다.
        private void FireOutputs(ushort changed, ushort state)
        {
            foreach(var pin in Enumerable.Range(0, PinCount).Where(p => (changed & (1 << p)) != 0))
            {
                Connections[pin].Set((state & (1 << pin)) != 0);
            }
        }

        private void ResetChips()
        {
            foreach(var registers in chips)
            {
                Array.Clear(registers, 0, registers.Length);
                registers[Direction] = registers[Direction + 1] = 0xFF;
            }
            byteIndex = 0;
        }

        private static void CheckPin(int pin)
        {
            if(pin < 0 || pin >= PinCount)
            {
                throw new RecoverableException($"IO 번호는 0~{PinCount - 1}입니다.");
            }
        }

        private readonly IMachine machine;
        private readonly List<IGPIOReceiver> attached = new List<IGPIOReceiver>();
        private readonly object sync = new object();
        private readonly bool[] selected = new bool[2];
        private readonly byte[][] chips = { new byte[0x16], new byte[0x16] };
        private ushort inputsOn, outputsOn;
        private bool resetHeld, validOpcode, reading;
        private int byteIndex, register;

        private const int PinCount = 16, FirstInputGpio = 16;
        private const int InputChip = 0, OutputChip = 1, ResetPin = 2;
        private const int Direction = 0x00, Polarity = 0x02, IoControl = 0x0A;
        private const int Gpio = 0x12, OutputLatch = 0x14, SequentialOperationDisable = 1 << 5;
    }
}

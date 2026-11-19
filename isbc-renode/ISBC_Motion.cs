using System;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.SPI
{
    // ISBC PCB_2 U6 TMC429 (회로도 sheet 13): 3축 스텝 펄스 발생기. SPI5, NCS = PF6(GPIO 0).
    // 축 0/1의 STEP/DIR은 U7/U8 TMC2660으로 나간다. 모터에 무엇이 달려 있는지는 보드가 아니라
    // 앱 쪽 기구물 모델이 정한다. 기구물 모델은 GetMotorSteps로 실제로 나간 펄스 수를 읽고,
    // SetStop으로 리미트 스위치(REF) 상태를 넣는다.
    public sealed class ISBC_Tmc429 : ISPIPeripheral, IGPIOReceiver
    {
        public ISBC_Tmc429(IMachine machine)
        {
            this.machine = machine;
            for(var i = 0; i < axes.Length; ++i)
            {
                axes[i] = new Axis();
            }
            Reset();
        }

        public int AxisCount => axes.Length;

        // 펌웨어가 읽는 XACTUAL.
        public int GetPosition(int axis)
        {
            lock(sync)
            {
                Advance();
                return ToPulse(axes[axis].Position);
            }
        }

        // 전원 투입 후 실제로 나간 누적 펄스 수. 펌웨어가 XACTUAL을 다시 써도 바뀌지 않는다.
        public double GetMotorSteps(int axis)
        {
            lock(sync)
            {
                Advance();
                return axes[axis].Steps;
            }
        }

        public bool IsMoving(int axis)
        {
            lock(sync)
            {
                Advance();
                return Velocity(axis) != 0;
            }
        }

        // TMC2660 EN 핀 상태. 드라이버가 꺼진 축은 움직이지 않는다.
        public void SetDriveEnabled(int axis, bool enabled)
        {
            lock(sync)
            {
                Advance();
                axes[axis].Enabled = enabled;
            }
        }

        public bool IsDriveEnabled(int axis)
        {
            lock(sync)
            {
                return axes[axis].Enabled;
            }
        }

        // 리미트 스위치 입력. positive = true는 + 방향 이동을 막는 스위치, false는 - 방향 스위치.
        // 상태 바이트의 RSx 비트는 + 방향 스위치를 보고한다.
        public void SetStop(int axis, bool positive, bool active)
        {
            lock(sync)
            {
                Advance();
                if(positive)
                {
                    axes[axis].StopPositive = active;
                }
                else
                {
                    axes[axis].StopNegative = active;
                }
            }
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0)
            {
                lock(sync)
                {
                    selected = !value;
                    index = 0;
                }
            }
        }

        public byte Transmit(byte data)
        {
            lock(sync)
            {
                if(!selected || index >= 4)
                {
                    return 0xFF;
                }
                Advance();
                if(index == 0)
                {
                    command = data;
                    payload = 0;
                    response = ReadRegister(data);
                    index++;
                    return StatusByte();
                }

                payload = (payload << 8) | data;
                var result = (byte)(response >> (8 * (3 - index)));
                index++;
                if(index == 4 && (command & 1) == 0)
                {
                    WriteRegister(command, payload);
                }
                return result;
            }
        }

        public void FinishTransmission()
        {
            // NCS GPIO가 4바이트 단위 전송을 나눈다.
        }

        public void Reset()
        {
            lock(sync)
            {
                Array.Clear(registers, 0, registers.Length);
                foreach(var axis in axes)
                {
                    axis.Reset();
                }
                selected = false;
                index = 0;
                lastUpdate = Now;
            }
        }

        private int ReadRegister(byte header)
        {
            var channel = (header >> 5) & 3;
            var address = header & 0x1E;
            if(channel < axes.Length)
            {
                if(address == ActualPosition)
                {
                    return ToPulse(axes[channel].Position) & PositionMask;
                }
                if(address == ActualVelocity)
                {
                    return ToPulse(Velocity(channel) / PulsesPerVelocityUnit) & 0xFFF;
                }
            }
            return registers[(header & 0x7E) >> 1];
        }

        private void WriteRegister(byte header, int value)
        {
            registers[(header & 0x7E) >> 1] = value;
            var channel = (header >> 5) & 3;
            if(channel >= axes.Length)
            {
                return;
            }
            var axis = axes[channel];
            switch(header & 0x1E)
            {
                case ActualPosition:
                    axis.Offset = Signed(value, 24) - axis.Steps;
                    break;
                case TargetPosition:
                    axis.Target = Signed(value, 24);
                    break;
                case MaxVelocity:
                    axis.MaxVelocity = Math.Min(value & 0xFFF, 2047);
                    break;
                case TargetVelocity:
                    axis.TargetVelocity = Signed(value & 0xFFF, 12);
                    break;
                case RampMode:
                    axis.Mode = value & 3;
                    break;
            }
        }

        private byte StatusByte()
        {
            var result = 0;
            for(var axis = 0; axis < axes.Length; ++axis)
            {
                if(Math.Abs(axes[axis].Target - axes[axis].Position) < 0.5)
                {
                    result |= 1 << (2 * axis);
                }
                if(axes[axis].StopPositive)
                {
                    result |= 2 << (2 * axis);
                }
            }
            return (byte)result;
        }

        private void Advance()
        {
            var now = Now;
            var elapsed = Math.Max(0, now - lastUpdate);
            lastUpdate = now;
            if(elapsed == 0)
            {
                return;
            }
            for(var channel = 0; channel < axes.Length; channel++)
            {
                var axis = axes[channel];
                var movement = Velocity(channel) * elapsed;
                if(axis.Mode < VelocityMode)
                {
                    movement = Math.Sign(movement) * Math.Min(Math.Abs(movement), Math.Abs(axis.Target - axis.Position));
                }
                axis.Steps += movement;
            }
        }

        private double Velocity(int channel)
        {
            var axis = axes[channel];
            if(!axis.Enabled || axis.Mode == HoldMode)
            {
                return 0;
            }
            var speed = axis.Mode == VelocityMode
                ? Math.Clamp(axis.TargetVelocity, -axis.MaxVelocity, axis.MaxVelocity)
                : Math.Abs(axis.Target - axis.Position) < 0.0001
                    ? 0 : Math.Sign(axis.Target - axis.Position) * axis.MaxVelocity;
            if((speed > 0 && axis.StopPositive) || (speed < 0 && axis.StopNegative))
            {
                return 0;
            }
            return speed * PulsesPerVelocityUnit;
        }

        private double Now => (double)machine.ElapsedVirtualTime.TimeElapsed.TotalSeconds;
        private static int ToPulse(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
        private static int Signed(int value, int bits) => (value ^ (1 << (bits - 1))) - (1 << (bits - 1));

        private sealed class Axis
        {
            public double Position => Steps + Offset;
            public double Steps;
            public double Offset;
            public int Target;
            public int TargetVelocity;
            public int MaxVelocity;
            public int Mode;
            public bool Enabled;
            public bool StopPositive;
            public bool StopNegative;

            public void Reset()
            {
                Steps = Offset = 0;
                Target = TargetVelocity = 0;
                MaxVelocity = 1000;
                Mode = HoldMode;
                Enabled = false;
                // 리미트 스위치는 칩 밖 신호라서 칩 리셋으로 바뀌지 않는다.
            }
        }

        private readonly IMachine machine;
        private readonly object sync = new object();
        private readonly Axis[] axes = new Axis[3];
        private readonly int[] registers = new int[64];
        private double lastUpdate;
        private bool selected;
        private int index, payload, response;
        private byte command;
        private const int TargetPosition = 0x00, ActualPosition = 0x02, MaxVelocity = 0x06;
        private const int TargetVelocity = 0x08, ActualVelocity = 0x0A, RampMode = 0x14;
        private const int VelocityMode = 2, HoldMode = 3, PositionMask = 0xFFFFFF;
        private const double PulsesPerVelocityUnit = 122;
    }

    // ISBC PCB_2 U7/U8 TMC2660 (회로도 sheet 13): 축 0/1 모터 드라이버. SPI4.
    // GPIO 0 = NCS0(PE3), 1 = NCS1(PE4), 2 = EN0(PE0), 3 = EN1(PE1). EN은 Low일 때 켜진다.
    // 펌웨어가 상태 폴링에서 쓰는 StandStill 비트만 TMC429 축 동작에 맞춰 돌려준다.
    // 과전류·온도 등 진단 비트는 정상(0)으로 둔다.
    public sealed class ISBC_Tmc2660 : ISPIPeripheral, IGPIOReceiver
    {
        public ISBC_Tmc2660(ISBC_Tmc429 controller)
        {
            this.controller = controller;
        }

        public void OnGPIO(int number, bool value)
        {
            if(number >= 2 && number <= 3)
            {
                controller.SetDriveEnabled(number - 2, !value);
                return;
            }
            if(number >= 0 && number <= 1)
            {
                selected[number] = !value;
                index = 0;
            }
        }

        public byte Transmit(byte data)
        {
            if(selected[0] == selected[1] || index >= 3)
            {
                return 0xFF;
            }
            if(index == 0)
            {
                var axis = selected[0] ? 0 : 1;
                response = controller.IsMoving(axis) ? 0 : StandStill;
            }
            return (byte)(response >> (8 * (2 - index++)));
        }

        public void FinishTransmission()
        {
            // 축마다 NCS GPIO가 따로 있다.
        }

        public void Reset()
        {
            Array.Clear(selected, 0, selected.Length);
            index = 0;
            response = 0;
        }

        private readonly ISBC_Tmc429 controller;
        private readonly bool[] selected = new bool[2];
        private int index, response;
        private const int StandStill = 0x80 << 4;
    }
}

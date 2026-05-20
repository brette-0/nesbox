using System.Runtime.CompilerServices;

namespace nesbox.Emulator;

internal static partial class System {
    internal static class APU {
        internal static /* pcm */ float GetPCMSample() {
            var p1 = Program.AudioProcessor.ProcessPulse1Level(Pulse1.GetLevel());
            var p2 = Program.AudioProcessor.ProcessPulse2Level(Pulse2.GetLevel());
            var t  = Program.AudioProcessor.ProcessTriangleLevel(Triangle.GetLevel());
            var n  = Program.AudioProcessor.ProcessNoiseLevel(Noise.GetLevel());
            var p  = Program.AudioProcessor.ProcessPCMLevel(PCM.GetLevel());

            var pulseSum = p1 + p2;
            var pulseOut = pulseSum > 0 ? 95.88f          / (8128f / pulseSum                              + 100f) : 0f;
            var tnpOut   = (t | n | p) is not 0 ? 159.79f / (1f    / (t / 8227f + n / 12241f + p / 22638f) + 100f) : 0f;

            return (Program.isFamicom
                ? ((API.IFamicomCartridge)Program.Cartridge).ModifyAPUSignal(pulseOut + tnpOut)
                : pulseOut + tnpOut) * 2f - 1f;
        }


        internal static void Step() {
            _clockFlipFlop ^= true;

            if (_pendingFrameIRQClear && !_clockFlipFlop) {
                _pendingFrameIRQClear = false;
                FrameIRQAsserted = false;
            }

            if (_irqWindow > 0 && --_irqWindow is > 0 and <= 3 && !IRQInhibit)
                FrameIRQAsserted = true;

            if (_irqFlagWindow > 0) _irqFlagWindow--;


            switch (_resetFrameCounter) {
                case > 4:
                    break;

                case 0:
                    _resetFrameCounter = 0xff;
                    _frameCounter      = 0;
                    if (UsingFiveStep) {
                        Pulse1.QuarterFrame();
                        Pulse2.QuarterFrame();
                        Pulse1.HalfFrame();
                        Pulse2.HalfFrame();
                        Triangle.QuarterFrame();
                        Triangle.HalfFrame();
                        Noise.QuarterFrame();
                        Noise.HalfFrame();
                    }
                    break;

                default:
                    _resetFrameCounter--;
                    break;
            }

            switch (++_frameCounter) {
                case S1:
                case S3:
                    Pulse1.QuarterFrame();
                    Pulse2.QuarterFrame();
                    Triangle.QuarterFrame();
                    Noise.QuarterFrame();
                    break;

                case S2:
                    Pulse1.HalfFrame();
                    Pulse2.HalfFrame();
                    Triangle.HalfFrame();
                    Noise.HalfFrame();
                    goto case S1;

                case S4 - 1:
                    if (UsingFiveStep) break;
                    if (!IRQInhibit) {
                        if (_frameCounterOddReset) {
                            _irqWindow = 5;
                        } else {
                            FrameIRQAsserted = true;
                            _irqWindow = 3;
                        }
                    } else {
                        _irqFlagWindow = 4;
                    }
                    break;

                case S4:
                    if (UsingFiveStep) break;
                    _frameCounter = unchecked((ushort)(-1));
                    goto case S2;

               case S5:
                   if (!UsingFiveStep) break;
                   _frameCounter = 0;
                   goto case S2;
            }

            if (IOAssertion && _clockFlipFlop) {
                Program.Controller1?.OnWrite();
                Program.Controller2?.OnWrite();
            }

            Pulse1.Step();
            Pulse2.Step();
            Triangle.Step();
            Noise.Step();
            PCM.Step();
        }

        internal static class PCM {
            private static readonly ushort[] rateTable = {
                0x01AC, 0x017C, 0x0154, 0x0140,
                0x011E, 0x00FE, 0x00E2, 0x00D6,
                0x00BE, 0x00A0, 0x008E, 0x0080,
                0x006A, 0x0054, 0x0048, 0x0036
            };

            internal static byte GetLevel() => enabled ? (byte)(outputLevel & 0x7f) : (byte)0;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {
                if (enableDelay > 0 && --enableDelay is 0) enabled = true;

                if (timerCounter is not 0) {
                    timerCounter--;
                    return;
                }

                timerCounter = (ushort)(rateTable[rateIndex] - 1);

                if (!Silence) {
                    switch ((shiftReg & 1) is not 0, outputLevel) {
                        case (true,  < 126): outputLevel += 2; break;
                        case (false, > 1):   outputLevel -= 2; break;
                    }
                }

                shiftReg >>= 1;

                if (bitsRemaining is 0) {
                    bitsRemaining = 7;

                    if (bufferEmpty) {
                        Silence = true;
                    } else {
                        Silence     = false;
                        shiftReg    = sampleBuffer;
                        bufferEmpty = true;
                    }
                } else bitsRemaining--;

                if (!bufferEmpty || bytesRemaining is 0 || dmaRequested || inDMA) return;
                dmaRequested = true;
                dmaDelayCount = 1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4010_DMC() {
                DMC_IRQ_Enabled = (Data       & 0x80) is 0x80;
                Loop            = (Data       & 0x40) is 0x40;
                rateIndex       = (byte)(Data & 0x0f);
                if (!DMC_IRQ_Enabled) IRQFlag = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4011_DMC() {
                outputLevel = (byte)(Data & 0x7f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4012_DMC() {
                SampleAddress = (ushort)(0xc000 | (Data << 6));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4013_DMC() {
                SampleLength = (ushort)((Data << 4) | 1);
            }

            internal static bool   IRQFlag;
            internal static ushort currentAddress;
            internal static byte   sampleBuffer;
            internal static ushort timerCounter;
            internal static byte   outputLevel;
            internal static byte   shiftReg;
            internal static byte   rateIndex;
            internal static byte   bitsRemaining;
            internal static bool   bufferEmpty;
            internal static ushort bytesRemaining;
            internal static bool   Silence;
            internal static ushort SampleAddress;
            internal static ushort SampleLength;
            internal static bool   DMC_IRQ_Enabled;
            internal static bool   Loop;
            internal static bool   enabled;
            internal static byte   enableDelay;

            internal static bool   inDMA;
            internal static bool   dmaRequested;
            private  static byte   dmaCycleCount;
            internal static byte   dmaDelayCount;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void DMA_Step() {
                if (!dmaRequested && !inDMA) return;

                if (dmaRequested && !inDMA) {
                    inDMA = true;
                    dmaRequested = false;
                    dmaCycleCount = dmaDelayCount > 0 ? (byte)4 : (byte)3;
                    dmaDelayCount = 0;
                    RDY = true;
                    return;
                }

                if (--dmaCycleCount is not 0) return;

                var sample = Program.Cartridge.ReadByte(currentAddress);
                OpenBus = sample;
                sampleBuffer = sample;
                bufferEmpty = false;

                currentAddress = (ushort)((currentAddress + 1) | 0x8000);
                bytesRemaining--;

                if (bytesRemaining is 0) {
                    if (Loop) {
                        currentAddress = SampleAddress;
                        bytesRemaining = SampleLength;
                    } else if (DMC_IRQ_Enabled) {
                        IRQFlag = true;
                    }
                }

                inDMA = false;
                if (!PPU.inDMA) RDY = false;
            }
        }

        internal static class Noise {
            private static readonly ushort[] PeriodTable = {
                4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068
            };

            private static readonly byte[] lengthTable = {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };

            internal static byte GetLevel() =>
                !enabled || Length is 0 || (lfsr & 1) is not 0
                    ? (byte)0
                    : (byte)((constantVolume ? Volume : envDecay) & 0x0f);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {
                if (!_clockFlipFlop || !enabled) return;

                if (timerCounter is 0) {
                    timerCounter = PeriodTable[periodIndex];

                    var bit0     = (ushort)( lfsr                    & 1);
                    var tap      = (ushort)((lfsr >> (mode ? 6 : 1)) & 1);
                    var feedback = (ushort)(bit0 ^ tap);

                    lfsr >>= 1;
                    lfsr |= (ushort)(feedback << 14);
                } else timerCounter--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void QuarterFrame() {
                if (envStart) {
                    envStart   = false;
                    envDecay   = 15;
                    envDivider = Volume;
                    return;
                }

                if (envDivider is 0) {
                    envDivider = Volume;
                    if (envDecay > 0) envDecay--;
                    else if (Halt) envDecay = 15;
                } else envDivider--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void HalfFrame() {
                if (Length > 0 && !Halt) Length--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400C_Noise() {
                Halt           = (Data       & 0x20) is 0x20;
                constantVolume = (Data       & 0x10) is 0x10;
                Volume         = (byte)(Data & 0x0f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400E_Noise() {
                mode        = (Data       & 0x80) is 0x80;
                periodIndex = (byte)(Data & 0x0f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400F_Noise() {
                lengthIndex = (byte)((Data >> 3) & 0x1F);
                Length      = enabled ? lengthTable[lengthIndex] : (byte)0;
                envStart    = true;
            }

            private static bool   envStart;
            private static byte   envDivider;
            private static byte   envDecay;
            private static bool   mode;
            private static ushort lfsr = 1;
            private static byte   periodIndex;
            private static ushort timerCounter;
            private static bool   constantVolume;
            private static byte   lengthIndex;

            internal static bool enabled;
            internal static bool Halt;
            internal static byte Length;
            internal static byte Volume;
        }

        internal static class Triangle {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {
                if (!enabled) return;

                if (timerCounter is 0) {
                    timerCounter = Timer;

                    if (Length is not 0 && linearCounter is not 0) {
                        sequencer = (byte)((sequencer + 1) & 31);
                    }
                } else {
                    timerCounter--;
                }
            }

            internal static byte GetLevel() {
                if (!enabled || Length == 0 || linearCounter == 0) return 0;
                var s  = sequencer & 0x1f;
                return (byte)(s < 0x10 ? 0x0f - s : s - 0x10);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void QuarterFrame() {
                if (linearReloadFlag) linearCounter = reloadValue;
                else if (linearCounter is not 0) linearCounter--;

                if (!control) linearReloadFlag = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void HalfFrame() {
                if (Length > 0 && !control) Length--;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4008_Triangle() {
                control     = (Data       & 0x80) is 0x80;
                reloadValue = (byte)(Data & 0x7f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400A_Triangle() {
                Timer &= 0xff00;
                Timer |= Data;
            }

            private static readonly byte[] LengthTable =
            {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400B_Triangle() {
                Length =  enabled ? LengthTable[(Data >> 3) & 0x1f] : (byte)0;
                Timer  &= 0x00ff;
                Timer  |= (ushort)((Data & 0x07) << 8);

                linearReloadFlag = true;
            }


            private static ushort timerCounter;
            private static byte   sequencer;
            private static byte   linearCounter;
            private static bool   linearReloadFlag;
            private static bool   control;
            private static byte   reloadValue;

            internal static byte   Length;
            internal static ushort Timer;
            internal static bool   enabled;
        }

        internal class PulseChannel {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Step() {
                if (!_clockFlipFlop || !enabled) return;

                if (timerCounter is 0) {
                    timerCounter = Timer;
                    seq          = (byte)((seq + 1) & 7);
                    return;
                }

                timerCounter--;
            }

            private static readonly byte[] DutyTable = [0b01000000, 0b01100000, 0b01111000, 0b10011111];

            internal byte GetLevel() {
                if (!enabled || Length is 0 || Timer < 8) return 0;

                if (Shift is not 0) {
                    var change = Timer >> Shift;
                    var target = Negate
                        ? this == Pulse1 ? (Timer - change - 1) : Timer - change
                        : Timer + change;

                    if (target > 0x7ff) return 0;
                }

                var dutyBit = (DutyTable[Duty] >> seq) & 1;
                if (dutyBit is 0) return 0;
                return ConstantVolume ? Volume : envDecay;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void QuarterFrame() {
                if (envStart) {
                    envStart   = false;
                    envDecay   = 15;
                    envDivider = Volume;
                    return;
                }

                if (envDivider is 0) {
                    envDivider = Volume;
                    if (envDecay > 0) envDecay--;
                    else if (Halt) envDecay = 15;
                } else {
                    envDivider--;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void HalfFrame() {
                if (Length > 0 && !Halt) Length--;

                var divZero = sweepDivider is 0;
                if (divZero) {
                    if (SweepEnable && Shift is not 0) {
                        var target = (ushort)(
                            Negate
                                ? this == Pulse1
                                    ? Timer - (Timer >> Shift) - 1
                                    : Timer - (Timer >> Shift)
                                : Timer + (Timer >> Shift)
                        );

                        if (Timer < 8 || target > 0x7FF) goto sweepReloadCheck;
                        Timer = target;
                    }
                }

                sweepReloadCheck:
                if (sweepReload || divZero) {
                    sweepDivider = Period;
                    sweepReload  = false;
                } else {
                    sweepDivider--;
                }
            }


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4000_PulseX() {
                Duty           = (byte)(Data >> 6);
                Halt           = (Data       & 0x20) is 0x20;
                ConstantVolume = (Data       & 0x10) is 0x10;
                Volume         = (byte)(Data & 0x0f);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4001_PulseX() {
                SweepEnable = (Data & 0x80) is 0x80;
                Period      = (byte)((Data & 0x70) >> 4);
                Negate      = (Data       & 0x08) is 0x08;
                Shift       = (byte)(Data & 0x07);
                sweepReload = true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4002_PulseX() {
                Timer &= 0xff00;
                Timer |= Data;
            }

            private static readonly byte[] LengthTable =
            {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void W4003_PulseX() {
                var lengthIndex = (Data >> 3) & 0x1F;
                Length = enabled ? LengthTable[lengthIndex] : (byte)0;

                Timer &= 0x00FF;
                Timer |= (ushort)((Data & 0x07) << 8);

                envStart = true;
                seq      = 0;
            }

            internal bool  enabled;

            private ushort timerCounter;
            private byte   seq;

            private bool   envStart;
            private byte   envDivider;
            private byte   envDecay;

            private bool   sweepReload;
            private byte   sweepDivider;

            private  byte   Duty;
            private  bool   Halt;
            private  bool   ConstantVolume;
            private  byte   Volume;
            private  bool   SweepEnable;
            private  byte   Period;
            private  bool   Negate;
            private  byte   Shift;
            internal  ushort Timer;
            internal byte   Length;
        }

        internal static class Registers {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4000_Pulse1() => Pulse1.W4000_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4004_Pulse2() => Pulse2.W4000_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4001_Pulse1() => Pulse1.W4001_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4005_Pulse2() => Pulse2.W4001_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4002_Pulse1() => Pulse1.W4002_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4006_Pulse2() => Pulse2.W4002_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4003_Pulse1() => Pulse1.W4003_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4007_Pulse2() => Pulse2.W4003_PulseX();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4008_Triangle() => Triangle.W4008_Triangle();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400A_Triangle() => Triangle.W400A_Triangle();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400B_Triangle() => Triangle.W400B_Triangle();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400C_Noise() => Noise.W400C_Noise();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400E_Noise() => Noise.W400E_Noise();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W400F_Noise() => Noise.W400F_Noise();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4010_DMC() => PCM.W4010_DMC();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4011_DMC() => PCM.W4011_DMC();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4012_DMC() => PCM.W4012_DMC();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4013_DMC() => PCM.W4013_DMC();



            internal static void W4015_Status() {
                var pcmEnable    = (Data & 0x10) is 0x10;
                Noise.enabled    = (Data & 0x08) is 0x08;
                Triangle.enabled = (Data & 0x04) is 0x04;
                Pulse2.enabled   = (Data & 0x02) is 0x02;
                Pulse1.enabled   = (Data & 0x01) is 0x01;

                if (!Pulse1.enabled)   Pulse1.Length = 0;
                if (!Pulse2.enabled)   Pulse2.Length = 0;
                if (!Triangle.enabled) Triangle.Length = 0;
                if (!Noise.enabled)    Noise.Length = 0;
                PCM.IRQFlag = false;
                if (pcmEnable) {
                    PCM.enableDelay = 4;
                    if (PCM.bytesRemaining is 0) {
                        PCM.currentAddress = PCM.SampleAddress;
                        PCM.bytesRemaining = PCM.SampleLength;
                    }

                    if (!PCM.bufferEmpty || PCM.bytesRemaining is 0 || PCM.dmaRequested || PCM.inDMA) return;
                    PCM.dmaRequested = true;
                    PCM.dmaDelayCount = 1;
                } else {
                    PCM.enabled = false;
                    PCM.enableDelay = 0;
                    PCM.dmaDelayCount = 0;
                    PCM.bytesRemaining  = 0;
                    PCM.bufferEmpty     = true;
                    PCM.Silence         = true;
                    if (PCM.inDMA || PCM.dmaRequested) {
                        PCM.inDMA = false;
                        PCM.dmaRequested = false;
                        if (!PPU.inDMA) RDY = false;
                    }
                }
            }

            internal static void R4015_Status() {
                var resp = (byte)(Data & 0x20); // bit 5 is open bus (internal data bus, not external)

                resp |= (byte)(Pulse1.Length      is not 0 ? 0x01 : 0);
                resp |= (byte)(Pulse2.Length      is not 0 ? 0x02 : 0);
                resp |= (byte)(Triangle.Length    is not 0 ? 0x04 : 0);
                resp |= (byte)(Noise.Length       is not 0 ? 0x08 : 0);
                resp |= (byte)(PCM.bytesRemaining      > 0 ? 0x10 : 0);
                resp |= (byte)(FrameIRQAsserted || _irqFlagWindow is > 0 and <= 2 ? 0x40 : 0); // bit 6: frame IRQ pending
                resp |= (byte)(PCM.IRQFlag                 ? 0x80 : 0); // bit 7: DMC IRQ pending
                Data =  resp;

                _pendingFrameIRQClear = true;
            }

            internal static void W4017_FrameCounter() {
                UsingFiveStep = (Data & 0x80) is 0x80;
                IRQInhibit    = (Data & 0x40) is 0x40;
                if (IRQInhibit) FrameIRQAsserted = false;

                _frameCounterOddReset = !_clockFlipFlop;
                _resetFrameCounter = (byte)(_clockFlipFlop ? 3 : 2);
            }
        }

        internal static bool FrameIRQAsserted;
        internal static bool _pendingFrameIRQClear;
        internal static byte _irqWindow;
        internal static byte _irqFlagWindow;
        internal static bool _frameCounterOddReset;
        internal static byte _resetFrameCounter;
        internal static bool UsingFiveStep;
        internal static bool IRQInhibit;

        private const ushort S1 = 7457;
        private const ushort S2 = 14913;
        private const ushort S3 = 22371;
        private const ushort S4 = 29829;
        private const ushort S5 = 37281;



        internal static bool   _clockFlipFlop;
        private  static ushort _frameCounter;
    }
}

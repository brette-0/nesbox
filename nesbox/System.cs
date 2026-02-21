using System.Diagnostics;
using System.Runtime.CompilerServices;
using nesbox.CPU;
namespace nesbox;

internal static class System {
    internal static class PPU {
        // TODO: Add OAM DMA for DMC DMA to interrupt it
        
        private const ulong DOTS_PER_SCANLINE = 341;
        private const ulong VBLANK_SET_DOT    = 241 * DOTS_PER_SCANLINE + 1; // 82182
        private const ulong VBLANK_CLEAR_DOT  = 261 * DOTS_PER_SCANLINE + 1; // 89002


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Step() {
            switch (virtualTime % DOTS_PER_FRAME) {
                case VBLANK_SET_DOT:
                    inVblank = true;

                    if (NMIEnabled) {
                        NMIAsserted = true;
                    }
                    break;
                
                case VBLANK_CLEAR_DOT:
                    inVblank = false;
                    break;
            }
        }

        internal static class Registers {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W2000_PPUCRTL() {
                var NMIAlreadyEnabled = NMIEnabled;
                NMIEnabled = (Data & 0x80) is 0x80;

                if (!NMIAlreadyEnabled && NMIEnabled && inVblank) NMIAsserted = true;
            }
        }
        
        internal static class OAM {
            internal static void W4014_OAMDMA() {
                inDMA        = true;
                oamHaltCycle = true;
                dmaPage      = Data;
                dmaIndex     = 0;
                dmaLatch     = 0;
                dmaGetPhase  = true;

                dmaAlign    = virtualTime % 6 is 0;     // aligned if on an even cpu cycle
            }
            
            internal static void DMA() {
                if (!inDMA || APU.PCM.inDMA) return;
                if (oamHaltCycle) {
                    oamHaltCycle = false;
                    RDY          = true;
                    return;
                }
                
                if (dmaAlign) {
                    dmaAlign = false;
                    return;
                }
        
                if (dmaGetPhase) {
                    var addr = (ushort)((dmaPage << 8) | dmaIndex);
                    Memory.Read(addr, out dmaLatch);
                } else {
                    OAMBuffer[OAMAddress++] = dmaLatch;
                    dmaIndex++;

                    if (dmaIndex is 0) {
                        inDMA = false;
                        RDY   = false;
                        return;
                    }
                }
        
                dmaGetPhase ^= true;
            }
        }
        
        private static bool dmaAlign;
        private static byte dmaLatch;
        private static byte dmaPage;
        private static byte dmaIndex;
        private static bool dmaGetPhase;

        internal static bool   NMIEnabled;
        internal static bool   inDMA;
        internal static bool   inVblank;
        internal static bool   oamHaltCycle = false;
        internal static byte   OAMAddress;
        internal static byte   OAMData;
        internal static byte[] OAMBuffer = new byte[256];
    }
    
    internal static class APU {
        internal static /* pcm */ float GetPCMSample() {
            var p1 = Pulse1.GetLevel();
            var p2 = Pulse2.GetLevel();
            var t  = Triangle.GetLevel();
            var n  = Noise.GetLevel();
            var p  = PCM.GetLevel();

            var pulseSum = p1 + p2;
            var pulseOut = pulseSum > 0 ? 95.88f          / (8128f / pulseSum                              + 100f) : 0f;
            var tnpOut   = (t | n | p) is not 0 ? 159.79f / (1f    / (t / 8227f + n / 12241f + p / 22638f) + 100f) : 0f;

            return (Program.isFamicom
                ? ((API.IFamicomCartridge)Program.Cartridge).ModifyAPUSignal(pulseOut + tnpOut)
                : pulseOut + tnpOut) * 2f - 1f;
        }
        
        
        internal static void Step() {
            _clockFlipFlop ^= true;

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
                    }
                    break;
                
                default:
                    _resetFrameCounter--;
                    break;
            }

            switch (++_frameCounter) {
                case S1:
                case S1 + S2:
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
                    
                case S4:
                    if (UsingFiveStep) break;
                    _frameCounter = 0;
                    if (!IRQInhibit) FrameIRQAsserted = true;
                    goto case S2;
                    
               case S5:
                   if (!UsingFiveStep) break;
                   _frameCounter = 0;
                   if (!IRQInhibit) FrameIRQAsserted = true;
                   goto case S2;
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
                if (!enabled) return;
                
                // work
                if (timerCounter is not 0) {
                    timerCounter--;
                    return;
                }

                timerCounter = rateTable[rateIndex];

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
                        Silence     = false; // TODO: check what sets this, seems sub-optimal
                        shiftReg    = sampleBuffer;
                        bufferEmpty = true;
                    }
                } else bitsRemaining--;

                if (!bufferEmpty || bytesRemaining is 0) return;
                // TODO: use /RDY to halt || EMUALTE THIS CORRECTLY
                sampleBuffer = Memory.DMC_Read(currentAddress);
                bufferEmpty  = false;

                currentAddress = (ushort)(++currentAddress | 0x8000);
                bytesRemaining--;

                if (bytesRemaining is not 0) return;
                if (Loop) {
                    currentAddress = SampleAddress;
                    bytesRemaining = SampleLength;
                } else if (DMC_IRQ_Enabled) {
                    IRQFlag = true;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void W4010_DMC() {
                DMC_IRQ_Enabled = (Data       & 0x80) is 0x80;
                Loop            = (Data       & 0x40) is 0x40;
                rateIndex       = (byte)(Data & 0x0f);
                if (DMC_IRQ_Enabled) {
                    // Supress IRQ
                }
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
            
            internal static bool   inDMA;
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

                if (SweepEnable && Shift is not 0) {
                    var change = Timer >> Shift;
                    var target = Negate
                        ? this == Pulse1 ? (Timer - change - 1) : Timer - change
                        : Timer + change;

                    if (target > 0x7ff) return 0;
                }
                
                if (DutyTable[Duty] >> seq is 0) return 0;
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
            private  ushort Timer;
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
                PCM.enabled      = (Data & 0x10) is 0x10;
                Noise.enabled    = (Data & 0x08) is 0x08;
                Triangle.enabled = (Data & 0x04) is 0x04;
                Pulse2.enabled   = (Data & 0x02) is 0x02;
                Pulse1.enabled   = (Data & 0x01) is 0x01;

                if (!Pulse1.enabled)   Pulse1.Length = 0;
                if (!Pulse2.enabled)   Pulse2.Length = 0;
                if (!Triangle.enabled) Triangle.Length = 0;
                if (!Noise.enabled)    Noise.Length = 0;
                if (PCM.enabled) {
                    if (PCM.bytesRemaining is 0) {
                        PCM.currentAddress = PCM.SampleAddress;
                        PCM.bytesRemaining = PCM.SampleLength;
                    }

                    if (!PCM.bufferEmpty || PCM.bytesRemaining is 0) return;
                    PCM.sampleBuffer   = Memory.DMC_Read(PCM.currentAddress);
                    PCM.bufferEmpty    = false;
                    PCM.currentAddress = (ushort)(++PCM.currentAddress | 0x8000);
                    PCM.bytesRemaining--;

                    if (PCM.bytesRemaining is not 0) return;
                    if (PCM.Loop) {
                        PCM.currentAddress = PCM.SampleAddress;
                        PCM.bytesRemaining = PCM.SampleLength;
                    } else if (PCM.DMC_IRQ_Enabled) PCM.IRQFlag = true;
                } else {
                    PCM.bytesRemaining  = 0;
                    PCM.bufferEmpty     = true;
                    PCM.Silence         = true;
                    PCM.IRQFlag         = false;
                }
            }

            internal static void R4015_Status() {
                FrameIRQAsserted = false;
                PCM.IRQFlag      = false;
                var resp = (byte)(Data & 0xe0); // preserve open bus TODO: make more accurate much later
                
                resp |= (byte)(Pulse1.Length   is not 0 ? 0x01 : 0);
                resp |= (byte)(Pulse2.Length   is not 0 ? 0x02 : 0);
                resp |= (byte)(Triangle.Length is not 0 ? 0x04 : 0);
                resp |= (byte)(Noise.Length    is not 0 ? 0x08 : 0);
                resp |= (byte)(PCM.bytesRemaining   > 0 ? 0x10 : 0);
                resp |= (byte)(IRQInhibit               ? 0x40 : 0);
                resp |= (byte)(PCM.DMC_IRQ_Enabled      ? 0x80 : 0);
                Data =  resp;
            }

            internal static void W4017_FrameCounter() {
                UsingFiveStep = (Data & 0x80) is 0x80;
                IRQInhibit    = (Data & 0x40) is 0x40;

                _resetFrameCounter = (byte)(_clockFlipFlop ? 4 : 3);
            }
        }

        internal static bool FrameIRQAsserted;
        internal static byte _resetFrameCounter;
        internal static bool UsingFiveStep;
        internal static bool IRQInhibit;

        private const ushort S1 = 3729;
        private const ushort S2 = 7457;
        private const ushort S4 = 14915;
        private const ushort S5 = 18641;
        

        
        private  static bool   _clockFlipFlop;
        private  static ushort _frameCounter;
    }
    
    internal static class Memory {
        private const ushort PPUCTRL   = 0x2000;
        private const ushort PPUMASK   = 0x2001;
        private const ushort PPUSTATUS = 0x2002;
        private const ushort OAMADDR   = 0x2003;
        private const ushort OAMDATA   = 0x2004;
        private const ushort PPUSCROLL = 0x2005;
        private const ushort PPUADDR   = 0x2006;
        private const ushort PPUDATA   = 0x2007;

        private const ushort PULSE1_ENVELOPE  = 0x4000;
        private const ushort PULSE1_SWEEP     = 0x4001;
        private const ushort PULSE1_TIMER     = 0x4002;
        private const ushort PULSE1_COUNTER   = 0x4003;
        private const ushort PULSE2_ENVELOPE  = 0x4004;
        private const ushort PULSE2_SWEEP     = 0x4005;
        private const ushort PULSE2_TIMER     = 0x4006;
        private const ushort PULSE2_COUNTER   = 0x4007;
        private const ushort TRIANGLE_COUNTER = 0x4008;
        private const ushort TRIANGLE_TIMER   = 0x400a;
        private const ushort TRIANGLE_LINEAR  = 0x400b;
        private const ushort NOISE_ENVELOPE   = 0x400c;
        private const ushort NOISE_MODE       = 0x400e;
        private const ushort NOISE_COUNTER    = 0x400f;
        private const ushort DMC_MODE         = 0x4010;
        private const ushort DMC_LOAD         = 0x4011;
        private const ushort DMC_ASAMPLE      = 0x4012;
        private const ushort DMC_LSAMPLE      = 0x4013;

        private const ushort OAMDMA           = 0x4014;
        private const ushort CHANNELSTATUS    = 0x4015;
        private const ushort IODEVICE1        = 0x4016;
        private const ushort IODEVICE2        = 0x4017;
        private const ushort FRAMECOUNTER     = 0x4017;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CPU_Read() => Read(Address, out Data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte DMC_Read(ushort address) {
            
            #if RELEASE         // if you break anything, this should stop hardware acting impossibly
            address |= 0xc000;
            #endif

            return Program.Cartridge.ReadByte(address);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Read(ushort address, out byte data) {
            if (address < 0x2000) {
                data = SystemRAM[address & 0x7ff];
                goto SendReadToCart;
            }
            
            if (address < 0x4000) {
                switch (address & 0x2007) {
                    case PPUCTRL:   data = (byte)(address >> 8); goto SendReadToCart;
                    case PPUMASK:   throw new NotImplementedException($"[CPU] [Memory] [PPU] PPUMASK Not Implemented PC={PC}");
                    case PPUSTATUS: throw new NotImplementedException($"[CPU] [Memory] [PPU] PPUSTATUS Not Implemented PC={PC}");
                    case OAMADDR:   throw new NotImplementedException($"[CPU] [Memory] [PPU] OAMADDR Not Implemented PC={PC}");
                    case OAMDATA:   throw new NotImplementedException($"[CPU] [Memory] [PPU] OAMDATA Not Implemented PC={PC}");
                    case PPUSCROLL: throw new NotImplementedException($"[CPU] [Memory] [PPU] PPUSCROLL Not Implemented PC={PC}");
                    case PPUADDR:   throw new NotImplementedException($"[CPU] [Memory] [PPU] PPUADDR Not Implemented PC={PC}");
                    case PPUDATA:   throw new NotImplementedException($"[CPU] [Memory] [PPU] PPUDATA Not Implemented PC={PC}");
                    default:
                        Console.WriteLine("[CPU] [Memory] [PPU] Your programmer does not know how to use a mask");
                        Quit = true;
                        data = 0x00;
                        return;
                }
            }

            if (address > 0x4020) {
                data = Program.Cartridge.ReadByte(address);
                goto SendReadToCart;
            }

            switch (address) {
                case CHANNELSTATUS: APU.Registers.R4015_Status(); data = Data; goto SendReadToCart;
                case IODEVICE1:     data = Program.Controller1?.OnRead() ?? 0; goto SendReadToCart;
                case IODEVICE2:     data = Program.Controller2?.OnRead() ?? 0; goto SendReadToCart;
                
                default: data = (byte)(address >> 8); goto SendReadToCart;
            }
            
            SendReadToCart:
            Program.Cartridge.ProgramRead(address);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CPU_Write() {
            switch (Address) {
                case < 0x2000:
                    SystemRAM[Address & 0x7ff] = Data;
                    break;
                
                case < 0x4000:
                    switch (Address & 0x2007) {
                        case PPUCTRL:   PPU.Registers.W2000_PPUCRTL(); goto SendReadToCart;
                        case PPUMASK:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUSTATUS: throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case OAMADDR:   throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"); break;
                        case OAMDATA:   throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"); break;
                        case PPUSCROLL: throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUADDR:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUDATA:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                    }
                    break;
                
                case PULSE1_ENVELOPE:  APU.Registers.W4000_Pulse1();   goto SendReadToCart;
                case PULSE2_ENVELOPE:  APU.Registers.W4004_Pulse2();   goto SendReadToCart;
                case PULSE1_COUNTER:   APU.Registers.W4001_Pulse1();   goto SendReadToCart;
                case PULSE2_COUNTER:   APU.Registers.W4005_Pulse2();   goto SendReadToCart;
                case PULSE1_SWEEP:     APU.Registers.W4002_Pulse1();   goto SendReadToCart;
                case PULSE2_SWEEP:     APU.Registers.W4006_Pulse2();   goto SendReadToCart;
                case PULSE1_TIMER:     APU.Registers.W4003_Pulse1();   goto SendReadToCart;
                case PULSE2_TIMER:     APU.Registers.W4007_Pulse2();   goto SendReadToCart;
                case TRIANGLE_COUNTER: APU.Registers.W4008_Triangle(); goto SendReadToCart;
                case TRIANGLE_TIMER:   APU.Registers.W400A_Triangle(); goto SendReadToCart;
                case TRIANGLE_LINEAR:  APU.Registers.W400B_Triangle(); goto SendReadToCart;
                case NOISE_ENVELOPE:   APU.Registers.W400C_Noise();    goto SendReadToCart;
                case NOISE_MODE:       APU.Registers.W400E_Noise();    goto SendReadToCart;
                case NOISE_COUNTER:    APU.Registers.W400F_Noise();    goto SendReadToCart;
                case DMC_MODE:         APU.Registers.W4010_DMC();      goto SendReadToCart;
                case DMC_LOAD:         APU.Registers.W4011_DMC();      goto SendReadToCart;
                case DMC_ASAMPLE:      APU.Registers.W4012_DMC();      goto SendReadToCart;
                case DMC_LSAMPLE:      APU.Registers.W4013_DMC();      goto SendReadToCart;
                case OAMDMA:           PPU.OAM.W4014_OAMDMA();         goto SendReadToCart;;
                case CHANNELSTATUS:    APU.Registers.W4015_Status();   goto SendReadToCart;;
                case IODEVICE1:
                    if ((Data & 1) is 0) break;
                    Program.Controller1?.OnWrite();
                    Program.Controller2?.OnWrite();
                    break;
                case FRAMECOUNTER:     APU.Registers.W4017_FrameCounter(); goto SendReadToCart;
                    
                case > 0x4020:
                    Program.Cartridge.CPUWrite();
                    break;
                
                default:
                    // TODO: open bus, nothing to write to "Actually, remember how we write to PPUSTATUS to precharge"
                    goto SendReadToCart;
                    break;
            }
            SendReadToCart:
            Program.Cartridge.ProgramRead(Address);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Push() {
            ref var s = ref Register.S;
            ADL = s;
            ADH = 0x01;
            DriveAddressPins();
            CPU_Write();
            s--;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Pull() {
            ref var s = ref Register.S;
            ADL = s;
            ADH = 0x01;
            DriveAddressPins();
            
            CPU_Read();
            s++;;
        }

        internal static byte[] SystemRAM = new byte[0x800];
    }
    
    internal const ulong DOTS_PER_FRAME = 89_342;
    
    /// <summary>
    /// Begin CPU Emulation
    /// </summary>
    internal static void Initialize() {
        Program.Threads.System = new Thread(RunCPU){IsBackground = false};
        Program.Threads.System.Start();
    }
    
    private static void RunCPU() {
        Console.WriteLine("[CPU] System is Running");

        Register.AC = (byte)Random.Shared.Next();
        Register.X  = (byte)Random.Shared.Next();
        Register.Y  = (byte)Random.Shared.Next();
        Register.S  = 0xfd;
        Register.i  = true;

        Reset    = true;
        OpHandle = StepReset;

        const double fps = 60.0988d;
        var frameTimeSeconds = 1.0 / fps;

        var freq = Stopwatch.Frequency;

        long frameDeadlineTick = 0;
        var lastThrottle = float.NaN;

        ulong frames = 0, lateFrames = 0;
        long nextPrint = 0;
        double worstLateMs = 0;

        var untilNextSample = 1d / SamplingFrequency;
        
        while (!Quit) {
            PPU.Step();
            Link.TriggerClockDrivenImplementations();
            if (virtualTime % 3 is 0) {
                Step();
                APU.Step();
                PPU.OAM.DMA();
                if (Quit) return;
            }

            if ((untilNextSample -= 1d / dotsPerSecond) <= 0d) {
                untilNextSample += 1d / SamplingFrequency;
                SampleBuffer.Add(APU.GetPCMSample());
            }

            if (virtualTime % DOTS_PER_FRAME is 0 && Throttle > 0f) {
                var frameStartTick = Stopwatch.GetTimestamp();

                var effectiveFrameSeconds = frameTimeSeconds / Throttle;
                var frameTicks = (long)(effectiveFrameSeconds * freq);
                if (frameTicks < 1) frameTicks = 1;

                if (frameDeadlineTick == 0 || Throttle != lastThrottle) {
                    lastThrottle = Throttle;
                    frameDeadlineTick = frameStartTick + frameTicks;
                }

                Renderer.Present();

                var workEndTick = Stopwatch.GetTimestamp();

                if (workEndTick > frameDeadlineTick) {
                    lateFrames++;

                    var lateMs = (workEndTick - frameDeadlineTick) * 1000.0 / freq;
                    if (lateMs > worstLateMs) worstLateMs = lateMs;

                    if (Program.Config.Strict) {
                        Console.WriteLine("[CPU] Unable to compute in time");
                        Quit = true;
                        return;
                    }

                    var behindTicks = workEndTick              - frameDeadlineTick;
                    var missed      = behindTicks / frameTicks + 1;   // number of boundaries missed
                    frameDeadlineTick += missed * frameTicks;
                } else {
                    while (Stopwatch.GetTimestamp() < frameDeadlineTick) {
                        Thread.Yield();
                    }

                    frameDeadlineTick += frameTicks;

                    var afterWait = Stopwatch.GetTimestamp();
                    if (afterWait > frameDeadlineTick) {
                        frameDeadlineTick = afterWait + frameTicks;
                    }
                }

                frames++;

                var now = Stopwatch.GetTimestamp();
                if (nextPrint == 0) nextPrint = now + freq;
                if (now >= nextPrint) {
                    Console.WriteLine($"[CPU] thr={Throttle:0.###} fps={frames:0} late={lateFrames} worstLateMs={worstLateMs:0.###}");
                    frames = 0;
                    lateFrames = 0;
                    worstLateMs = 0;
                    do nextPrint += freq; while (nextPrint <= now);
                }
            }

            ++virtualTime;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step() {
        if (RDY) return;
        if (cycle is 0) {
            if (Reset) {
                Console.WriteLine("[CPU] Resetting CPU");
                OpHandle = StepReset;
                goto HandleInstruction;
            }
            
            if (NMIAsserted) {
                NMIAsserted = false;
                Vector      = Vectors.NMI;
                OpHandle    = Interrupt;
                goto HandleInstruction;
            }

            if (CPU_IRQ || APU.FrameIRQAsserted || APU.PCM.IRQFlag) {
                if (Register.i) goto FetchInstruction;
                
                Vector     = Vectors.IRQ;
                OpHandle   = Interrupt;
                Register.i = true;          // set interrupt mask (we are in an interrupt)
                goto HandleInstruction;
            }
            
            FetchInstruction:
            AD          = PC;
            DriveAddressPins();
            
            Memory.CPU_Read();
            PC++;
            Register.IR = Data;
            OpHandle    = OpCodes.GetOpcodeSolver(Register.IR);
            cycle++;
            return;
        }
        
        HandleInstruction:
            OpHandle();
            cycle++;
    }

    private static void StepReset() {
        switch (cycle) {
            case 0:
                AD = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                break;
            
            case 1:
                ADL = 0x01;
                ADH = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                break;
            
            case 2:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                break;
            
            case 3:
                ADL = 0x01;
                ADH = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                Register.i = true;
                break;
            
            case 4:
                AD = 0xfffc;
                DriveAddressPins();
                Memory.CPU_Read();
                DB = Data;
                break;
            
            case 5:
                AD = 0xfffd;
                DriveAddressPins();
                Memory.CPU_Read();
                PCL   = DB;
                PCH   = Data;
                cycle = 0xff;
                Reset = false;
                break;
            
            default:
                Console.WriteLine("[CPU] StepReset on incorrect cycle");
                Quit = true;
                break;
        }
    }

    private static void Interrupt() {
        switch (cycle) {
            case 0:
                AD        = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                return;
            
            case 1:
                Data = PCH;
                Memory.Push();
                break;
            
            case 2:
                Data = PCL;
                Memory.Push();
                break;
            
            case 3:
                Data =
                    (byte)((Register.c ? 1 : 0) << 0 |
                           (Register.z ? 1 : 0) << 1 |
                           (Register.i ? 1 : 0) << 2 |
                           (Register.d ? 1 : 0) << 3 |
                           0                         |
                           32                        |
                           (Register.v ? 1 : 0) << 6 |
                           (Register.n ? 1 : 0) << 7);
                Memory.Push();
                Register.i = true;
                break;
            
            case 4:
                AD = Vector;
                DriveAddressPins();
                Memory.CPU_Read();
                DB  = Data;
                PCL = DB;
                break;
            
            case 5:
                AD = (ushort)(Vector + 1);
                DriveAddressPins();
                Memory.CPU_Read();
                PCH   = Data;
                cycle = 0xff;
                break;
            
            default:
                Console.WriteLine("[CPU] StepIRQ on incorrect cycle");
                Quit = true;
                break;
        }
    }


    private  static ushort Vector;
    internal static bool   CPU_IRQ;
    private  static bool   NMIAsserted;
    private static  bool   Reset;
    
    private static  Action OpHandle;
    internal static byte   cycle;

    internal static ushort Address;
    internal static byte   Data;
    internal static byte   DB;
    internal static byte   PCL;
    internal static byte   PCH;
    internal static byte   ADL;
    internal static byte   ADH;

    internal static ushort AD {
        get => (ushort)((ADH << 8) | ADL);
        set {
            ADH = (byte)(value >> 8);
            ADL = (byte)(value & 0xff);
        }
    }
    
    internal static ushort PC {
        get => (ushort)((PCH << 8) | PCL);
        set {
            PCH = (byte)(value >> 8);
            PCL = (byte)(value & 0xff);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DriveAddressPins()
    {
        Address = (ushort)((ADH << 8) | ADL);
    }
    
    internal static class Register {
        // Registers
        internal static byte IR = 0x00; // instruction register (stores the current operation code)
        internal static byte X  = 0x00;
        internal static byte Y  = 0x00; // index registers
        internal static byte S  = 0x00; // stack pointer
        internal static byte AC = 0x00; // accumulator
        
        // flags
        internal static bool c = false;
        internal static bool z = false;
        internal static bool i = false;
        internal static bool d = false;
        internal static bool b = false;
        internal static bool v = false;
        internal static bool n = false;
    }

    internal static class Vectors {
        internal const ushort NMI   = 0xfffa;
        internal const ushort Reset = 0xfffc;
        internal const ushort IRQ   = 0xfffe;
    }

    private const ulong BaseClockSpeed = 1_789_773ul;
    private const ulong  dotsPerSecond  = 5_369_318ul;

    internal static bool   RDY;
    private  const  double SECONDS_PER_FRAME    = 0d;
    internal static float  Throttle             = float.NegativeInfinity;
    internal static ulong  virtualTime          = 0;
    internal static bool   Quit                 = false;
    internal static uint   SamplingFrequency    = 48_000;
    internal static double SamplingCoefficiient = 0f;
    internal static bool   fetchOnNext;
    internal static List<float> SampleBuffer = [];
    internal static APU.PulseChannel Pulse1 = new();
    internal static APU.PulseChannel Pulse2 = new();
}
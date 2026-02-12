using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave.SampleProviders;
using nesbox.CPU;
namespace nesbox;

internal static class System {
    internal static class APU {
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
                    break;
                
                case S2:
                    Pulse1.HalfFrame();
                    Pulse2.HalfFrame();
                    Triangle.HalfFrame();
                    goto case S1;
                    
                case S4:
                    if (UsingFiveStep) break;
                    _frameCounter = 0;
                    // consider IRQ
                    goto case S2;
                    
               case S5:
                   if (!UsingFiveStep) break;
                   _frameCounter = 0;
                   // consider IRQ
                   goto case S2;
            }
            
            Pulse1.Step();
            Pulse2.Step();
            Triangle.Step();
            Noise.Step();
            PCM.Step();
        }




        internal static class PCM {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Step() {

            }
            
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void QuarterFrame() {
                
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void HalfFrame() {
                
            }


            internal static bool enabled;
        }
        
        internal static class Noise {
            private static readonly ushort[] PeriodTable = {
                4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068
            };
            
            private static readonly byte[] lengthTable = {
                10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,
                12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
            };
            
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
                Length   = enabled ? lengthTable[lengthIndex] : (byte)0;
                envStart = true;
            }
            
            private static bool   envStart;
            private static byte   envDivider;
            private static byte   envDecay;
            private static bool   mode;
            private static ushort lfsr;
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

            internal static void W4015_Status() {
                // DMC.enabled      = (Data & 0x10) is 0x10;
                // Noise.enabled    = (Data & 0x08) is 0x80;
                Triangle.enabled = (Data & 0x04) is 0x04;
                Pulse2.enabled   = (Data & 0x02) is 0x02;
                Pulse1.enabled   = (Data & 0x01) is 0x01;

                if (!Pulse1.enabled)   Pulse1.Length = 0;
                if (!Pulse2.enabled)   Pulse2.Length = 0;
                if (!Triangle.enabled) Triangle.enabled = false;
            }

            internal static void R4015_Status() {
                var resp = (byte)(Data & 0xe0); // preserve open bus TODO: make more accurate much later
                
                resp |= (byte)(Pulse1.Length   is not 0 ? 0x01 : 0);
                resp |= (byte)(Pulse2.Length   is not 0 ? 0x02 : 0);
                resp |= (byte)(Triangle.Length is not 0 ? 0x04 : 0);
                
                Data =  resp;
            }

            internal static void W4017_FrameCounter() {
                UsingFiveStep = (Data & 0x80) is 0x80;
                IRQInhibit    = (Data & 0x40) is 0x40;

                _resetFrameCounter = (byte)(_clockFlipFlop ? 4 : 3);
            }
        }

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
        internal static byte DMC_Read(ushort address) {
            
            #if RELEASE         // if you break anything, this should stop hardware acting impossibly
            address |= 0xc000;
            #endif

            return Program.Cartridge.ReadByte(address);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CPU_Read() {
            if (Address < 0x2000) {
                Data = SystemRAM[Address & 0x7ff];
                goto SendReadToCart;
            }
            
            if (Address < 0x4000) {
                switch (Address & 0x2007) {
                    case PPUCTRL:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case PPUMASK:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case PPUSTATUS: throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case OAMADDR:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case OAMDATA:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case PPUSCROLL: throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case PPUADDR:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    case PPUDATA:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented");
                    default:
                        Console.WriteLine("[CPU] [Memory] [PPU] Your programmer does not know how to use a mask");
                        Quit = true;
                        return;
                }
            }

            if (Address > 0x4020) {
                Data = Program.Cartridge.CPUReadByte();
                goto SendReadToCart;
            }

            switch (Address) {
                case PULSE1_ENVELOPE:  throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE2_ENVELOPE:  throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE1_COUNTER:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE2_COUNTER:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE1_SWEEP:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE2_SWEEP:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE1_TIMER:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case PULSE2_TIMER:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case TRIANGLE_COUNTER: throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case TRIANGLE_TIMER:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case TRIANGLE_LINEAR:  throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case NOISE_ENVELOPE:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case NOISE_MODE:       throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case NOISE_COUNTER:    throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case DMC_MODE:         throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case DMC_LOAD:         throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case DMC_ASAMPLE:      throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case DMC_LSAMPLE:      throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case OAMDMA:           throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case CHANNELSTATUS:    throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented");
                case IODEVICE1:        Data = Program.Controller1?.OnRead() ?? 0; goto SendReadToCart;
                case IODEVICE2:        Data = Program.Controller2?.OnRead() ?? 0; goto SendReadToCart;
                
                default: Data = (byte)(Address >> 8); goto SendReadToCart;
            }
            
            SendReadToCart:
            Program.Cartridge.CPURead();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CPU_Write() {
            switch (Address) {
                case < 0x2000:
                    SystemRAM[Address & 0x7ff] = Data;
                    break;
                
                case < 0x4000:
                    switch (Address & 0b0010_0111) {
                        case PPUCTRL:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUMASK:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUSTATUS: throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case OAMADDR:   throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"); break;
                        case OAMDATA:   throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"); break;
                        case PPUSCROLL: throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUADDR:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                        case PPUDATA:   throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"); break;
                    }
                    break;
                
                case PULSE1_ENVELOPE:  throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE2_ENVELOPE:  throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE1_COUNTER:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE2_COUNTER:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE1_SWEEP:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE2_SWEEP:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE1_TIMER:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case PULSE2_TIMER:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case TRIANGLE_COUNTER: throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case TRIANGLE_TIMER:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case TRIANGLE_LINEAR:  throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case NOISE_ENVELOPE:   throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case NOISE_MODE:       throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case NOISE_COUNTER:    throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case DMC_MODE:         throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case DMC_LOAD:         throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case DMC_ASAMPLE:      throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case DMC_LSAMPLE:      throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"); break;
                case OAMDMA:           throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case CHANNELSTATUS:    throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                case IODEVICE1:
                    if ((Data & 1) is 0) break;
                    Program.Controller1?.OnWrite();
                    Program.Controller2?.OnWrite();
                    break;
                case FRAMECOUNTER:     throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"); break;
                    
                case > 0x4020:
                    Program.Cartridge.CPUWrite();
                    break;
                
                default:
                    // TODO: open bus, nothing to write to "Actually, remember how we write to PPUSTATUS to precharge"
                    break;
            }
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
    
    internal const int DOTS_PER_FRAME = 89_342;
    
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

        while (!Quit) {
            Link.TriggerClockDrivenImplementations();

            if (virtualTime % 3 is 0) {
                Step();
                if (Quit) return;
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
        if (cycle is 0) {
            if (Reset) {
                Console.WriteLine("[CPU] Resetting CPU");
                OpHandle = StepReset;
                goto HandleInstruction;
            }
            
            if (NMIPending) {
                Vector   = Vectors.NMI;
                OpHandle = Interrupt;
                goto HandleInstruction;
            }

            if (IRQPending) {
                if (Register.i) goto HandleInstruction;;

                Vector   = Vectors.IRQ;
                OpHandle = Interrupt;
                goto HandleInstruction;
            }
            
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
        #if DEBUG
        Console.WriteLine($"Resetting CPU : {cycle} / 6");
        #endif
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
                ADL = 0x01;
                ADH = Register.S;
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
                           (0 << 4)                  |
                           (1 << 5)                  |
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
                PCL = DB;
                break;
            
            default:
                Console.WriteLine("[CPU] StepIRQ on incorrect cycle");
                Quit = true;
                break;
        }
    }


    private  static ushort Vector;
    internal static bool   IRQPending;
    private  static bool   NMIPending;
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

    private  const  double SECONDS_PER_FRAME  = 0d;
    internal static float  Throttle           = float.NegativeInfinity;
    internal static ulong  virtualTime        = 0;
    internal static bool   Quit               = false;


    internal static APU.PulseChannel Pulse1 = new();
    internal static APU.PulseChannel Pulse2 = new();
}
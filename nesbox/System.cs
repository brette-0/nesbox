using System.Diagnostics;
using System.Runtime.CompilerServices;

using static nesbox.CPU.OpCodes;

namespace nesbox;

internal static class System {
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

        
        
        internal static void Read() {
            CPU.Data = CPU.Address switch {
                < 0x2000 => SystemRAM[CPU.Address & 0x7ff],
                < 0x4000 => (CPU.Address & 0b0010_0111) switch {
                    PPUCTRL   => throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"),
                    PPUMASK   => throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"),
                    PPUSTATUS => throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"),
                    OAMADDR   => throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"),
                    OAMDATA   => throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"),
                    PPUSCROLL => throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"),
                    PPUADDR   => throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"),
                    PPUDATA   => throw new NotImplementedException("[CPU] [Memory] [PPU] Not Implemented"),
                },
                
                PULSE1_ENVELOPE  => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE2_ENVELOPE  => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE1_COUNTER   => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE2_COUNTER   => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE1_SWEEP     => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE2_SWEEP     => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE1_TIMER     => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                PULSE2_TIMER     => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                TRIANGLE_COUNTER => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                TRIANGLE_TIMER   => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                TRIANGLE_LINEAR  => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                NOISE_ENVELOPE   => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                NOISE_MODE       => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                NOISE_COUNTER    => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                DMC_MODE         => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                DMC_LOAD         => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                DMC_ASAMPLE      => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                DMC_LSAMPLE      => throw new NotImplementedException("[CPU] [Memory] [OAM] Not Implemented"),
                OAMDMA           => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                CHANNELSTATUS    => throw new NotImplementedException("[CPU] [Memory] [APU] Not Implemented"),
                IODEVICE1        => Program.Controller1?.OnRead() ?? 0,
                IODEVICE2        => Program.Controller2?.OnRead() ?? 0,
                >= 0x4020        => Program.Cartridge.CPUReadByte(),
                
                _ => (byte)(CPU.Address >> 8) // open bus
            };
            Program.Cartridge.CPURead();
        }
        
        internal static void Write() {
            switch (CPU.Address) {
                case < 0x2000:
                    SystemRAM[CPU.Address & 0x7fff] = CPU.Data;
                    break;
                
                case < 0x4000:
                    switch (CPU.Address & 0b0010_0111) {
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
                    if ((CPU.Data & 1) is 0) break;
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

        internal static void Push() {
            ref var s = ref CPU.Register.S;
            CPU.ADL = s;
            CPU.ADH = 0x01;
            CPU.DriveAddressPins();
            Write();
            s--;
        }
        
        internal static void Pull() {
            ref var s = ref CPU.Register.S;
            CPU.ADL = s;
            CPU.ADH = 0x01;
            CPU.DriveAddressPins();
            
            Read();
            s++;;
        }

        private static byte[] SystemRAM = new byte[0x800];
    }
    
    internal const int DOTS_PER_FRAME = 89_342;
    
    internal static class CPU {
        /// <summary>
        /// Begin CPU Emulation
        /// </summary>
        internal static void Initialize() {
            Program.Threads.System = new Thread(__Initialize){IsBackground = false};
            Program.Threads.System.Start();
        }
        
        private static void __Initialize() {
            Register.AC = (byte)Random.Shared.Next();
            Register.X  = (byte)Random.Shared.Next();
            Register.Y  = (byte)Random.Shared.Next();

            var sw = Stopwatch.StartNew();

            const double fps       = 60.0988d;
            const double frameTime = 1 / fps;
        
            var nextFrameDeadLine = sw.Elapsed.TotalSeconds + frameTime;
        
            while (!Quit) {
                Link.TriggerClockDrivenImplementations();
                // PPU step dot
                if (virtualTime % 3 is 0) {
                    Step();
                    // APU step cycle
                }

                if (_throttle is not 0f) {
                    // only present video when throttling
                    // TODO: On init disable throttle, show UI "Throttling"
                    //       this will indicate user should use breakpoints or lua
                    if (virtualTime % DOTS_PER_FRAME is 0) {
                        Renderer.Present();
                    
                        var effectiveFrameTime = frameTime / _throttle;
                        var now                = sw.Elapsed.TotalSeconds;
                        var remaining          = nextFrameDeadLine - now;

                        if (remaining < 0) {
                            if (Program.Config.Strict) {
                                Console.WriteLine("[CPU] Unable to compute in time");
                                Quit = true;
                                return;  
                            } 
                            
                            Console.WriteLine($"[CPU] Program is running behind schedule {MathF.Abs((float)remaining)}s");
                            remaining = 0;
                        }
                    
                        Thread.Sleep(TimeSpan.FromSeconds(remaining));
                        nextFrameDeadLine += effectiveFrameTime;
                    }
                } else {
                    nextFrameDeadLine = sw.Elapsed.TotalSeconds + frameTime;
                }
            
                ++virtualTime;
            }
        }

        private static void Step() {
            switch (cycle) {
                case 0:
                    if (NMIPending) {
                        Vector   = Vectors.NMI;
                        OpHandle = Interrupt;
                        break;
                    }

                    if (IRQPending) {
                        if (Register.i) break;

                        Vector   = Vectors.IRQ;
                        OpHandle = Interrupt;
                        break;
                    }
                    
                    AD          = PC;
                    DriveAddressPins();
                
                    Memory.Read();
                    PC++;
                    Register.IR = Data;
                    OpHandle    = GetOpcodeSolver(Register.IR);
                    break;
                
                case > 0:
                    OpHandle();
                    break;
            }
            
            cycle++;
        }

        private static void Interrupt() {
            switch (cycle) {
                case 0:
                    AD        = PC;
                    DriveAddressPins();
                    Memory.Read();
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
                    Memory.Read();
                    DB  = Data;
                    PCL = DB;
                    break;
                
                case 5:
                    AD = (ushort)(Vector + 1);
                    DriveAddressPins();
                    Memory.Read();
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
        
        private static  Action OpHandle;
        internal static sbyte  cycle;

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
    }

    private  const  double SECONDS_PER_FRAME = 0d;
    private  static float  _throttle         = 0f;
    internal static ulong virtualTime        = 0;
    internal static bool  Quit               = false;
}
using System.Diagnostics;
using System.Runtime.CompilerServices;
using nesbox.CPU;
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
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Read() {
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
        internal static void Write() {
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
            Write();
            s--;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Pull() {
            ref var s = ref Register.S;
            ADL = s;
            ADH = 0x01;
            DriveAddressPins();
            
            Read();
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

            var sw = Stopwatch.StartNew();

            const double fps       = 60.0988d;
            const double frameTime = 1 / fps;
        
            var nextFrameDeadLine = sw.Elapsed.TotalSeconds + frameTime;
        
            while (!Quit) {
                Link.TriggerClockDrivenImplementations();
                // PPU step dot
                if (virtualTime % 3 is 0) {
                    Step();
                    if (Quit) return;
                    // APU step cycle
                }

                if (virtualTime % DOTS_PER_FRAME is 0) {
                    if (Throttle > 0f) {
                        Renderer.Present();
                    
                        var effectiveFrameTime = frameTime / Throttle;
                        var now                = sw.Elapsed.TotalSeconds;
                        var remaining          = nextFrameDeadLine - now;

                        if (remaining < 0) {
                            if (Program.Config.Strict) {
                                Console.WriteLine("[CPU] Unable to compute in time");
                                Quit = true;
                                return;  
                            } 
                            
                            Console.WriteLine($"[CPU] Program is running behind schedule {MathF.Abs((float)remaining)}s");
                            nextFrameDeadLine += effectiveFrameTime;
                        } else {
                            Console.WriteLine($"[CPU] Ahead of Schedule by {remaining}");
                            Thread.Sleep(TimeSpan.FromSeconds(remaining));
                            nextFrameDeadLine += effectiveFrameTime;
                        }    
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
                
                Memory.Read();
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
                    Memory.Read();
                    break;
                
                
                case 1:
                    ADL = 0x01;
                    ADH = Register.S;
                    DriveAddressPins();
                    Memory.Read();
                    Register.S--;
                    break;
                
                case 2:
                    ADL = 0x01;
                    ADH = Register.S;
                    DriveAddressPins();
                    Memory.Read();
                    Register.S--;
                    break;
                
                case 3:
                    ADL = 0x01;
                    ADH = Register.S;
                    DriveAddressPins();
                    Memory.Read();
                    Register.S--;
                    Register.i = true;
                    break;
                
                case 4:
                    AD = 0xfffc;
                    DriveAddressPins();
                    Memory.Read();
                    DB = Data;
                    break;
                
                case 5:
                    AD = 0xfffd;
                    DriveAddressPins();
                    Memory.Read();
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

}
using System.Diagnostics;
using System.Runtime.CompilerServices;
using nesbox.CPU;
using SDL3;

using static nesbox.CPU.OpCodes;

namespace nesbox;

internal static class System {
    internal static class Memory {
        internal static byte Read() => Read(CPU.Address);
        internal static byte Read(ushort offset) {
            throw new NotImplementedException();
        }
        
        internal static byte Write() => Write(CPU.Address, CPU.Data);

        internal static byte Write(ushort offset, byte value) {
            throw new NotImplementedException();
        }

        internal static void Push() {
            ref var s = ref CPU.Register.S;
            Write((ushort)(0x100 | s), CPU.Data);
            s--;
        }
        
        internal static void Pull() {
            ref var s = ref CPU.Register.S;
            var ctx = Read((ushort)(0x100 | s));
            s++;
            CPU.Data = ctx;
        }
    }

    internal static void Initialize() {
        cart = new Implementation();
        cart.Initialize();
        CPU.Initialize();
        PPU.Initialize();
    }
    
    
    /// <summary>
    /// Contains all members that concern PPU
    /// </summary>
    private static class PPU {
        /// <summary>
        /// Begin PPU Emulation
        /// </summary>
        internal static void Initialize() {
            _thread = new Thread(__Initialize) {
                IsBackground = false
            };
            _thread.Start();
        }

        private static void __Initialize() {
            SDL.Init(SDL.InitFlags.Video);
            
            _window   = SDL.CreateWindow("PPU OUT", 256, 240, 0);

            if (_window is 0) {
                Console.WriteLine($"[SDL3] Create Window Failed: {SDL.GetError()}");
                Quit = true;
                return;
            }
            
            _renderer = SDL.CreateRenderer(_window, null);
            if (_renderer is 0) {
                Console.WriteLine($"[SDL3] Create Renderer Failed: {SDL.GetError()}");
                Quit = true;
                return;
            }
            
            if (!(_SDL3VSYNCSupported = SDL.SetRenderVSync(_renderer, 1))) {
                Console.WriteLine($"[SDL3] VSync not supported: {SDL.GetError()}");
            }
            
            
            Console.WriteLine("PPU OUT init");
            Lifetime();
        }

        private static void Lifetime() {
            SDL.Event ev;
            var       running = true;
            
            while (running) {
                while (SDL.PollEvent(out ev)) {
                    switch ((SDL.EventType)ev.Type) {
                        case SDL.EventType.Quit:
                            running = false;
                            break;
                    }
                }
                
                SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
                SDL.RenderClear(_renderer);
                SDL.RenderPresent(_renderer);
                
                if (!_SDL3VSYNCSupported) {
                    Thread.Sleep(1);
                }
            }
            
            Console.WriteLine("PPU OUT exit");
            
            SDL.DestroyRenderer(_renderer);
            SDL.DestroyWindow(_window);
            SDL.Quit();
            Quit = true;
        }

        internal static void Present() {
            
        }

        private static bool    _SDL3VSYNCSupported;
        private static nint    _window;
        private static nint    _renderer;
        private static Thread? _thread;

        internal const int DOTS_PER_FRAME = 89_342;
    }
    
    internal static class CPU {
        /// <summary>
        /// Begin CPU Emulation
        /// </summary>
        internal static void Initialize() {
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

                if (_throttle) {
                    // only present video when throttling
                    // TODO: On init disable throttle, show UI "Throttling"
                    //       this will indicate user should use breakpoints or lua
                    if (virtualTime % PPU.DOTS_PER_FRAME is 0) {
                        PPU.Present();
                    
                        var now       = sw.Elapsed.TotalSeconds;
                        var remaining = nextFrameDeadLine - now;

                        if (remaining < 0) {
                            Console.WriteLine("[CPU] Unable to compute in time");
                            Quit = true;
                            return;
                        }
                    
                        Thread.Sleep(TimeSpan.FromSeconds(remaining));
                        nextFrameDeadLine += frameTime;
                    }
                } else {
                    nextFrameDeadLine = sw.Elapsed.TotalSeconds + frameTime;
                }
            
                ++virtualTime;
            }
        }

        private static void Step() {
            if (cycle is 0) {
                Register.IR = Memory.Read(PC);
                PC++;
                OpHandle    = GetOpcodeSolver(Register.IR);
                return;
            }

            OpHandle();
            cycle++;
        }


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
    }
    
    private static Implementation cart;

    private  const  double SECONDS_PER_FRAME = 0d;
    private  static bool  _throttle          = false;
    internal static ulong virtualTime        = 0;
    internal static bool  Quit               = false;
}
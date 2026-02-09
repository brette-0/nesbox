namespace nesbox;

using EList;

internal static class Program {
    internal static void Main(string[] __args) {
        var args = new EList<string>(__args);
        var next = new EList<string>();
        while (args.MoveNext()) {
            switch (args.Current) {
                case "-h" or "--help":
                    // help message
                    break;
                
                case "-t" or "--throttle":
                    if (!args.MoveNext()) {
                        Console.WriteLine("[EMU] No throttle speed");
                        return;
                    }

                    if (!float.TryParse(args.Current, out System.Throttle)) {
                        Console.WriteLine("[EMU] Passed Argument for throttle must be a float");
                    }
                    
                    break;
                
                case "--2c02":  // ntsc
                    // use 2c02
                    break;
                    
                case "---2c07":  // pal
                    // use 2c07
                    break;
                
                case "--2c05":   // twin
                    // use 2c05
                    break;
                
                case "--pal":   // 312 scanlines (PAL behavior) | OAM refresh 265-310 | 3.2 dots per CPU cycle
                               // 3.2 dots per pixel           | all black border hides NTSC border
                               // black top 1 scanline, left 2 pixels and right 2 pixels
                               // cpu sync (DMA)               | APU rates
                    break;
                
                case "--ntsc":   // 262 scanlines (NTSC behavior)
                    break;
                
                case "--palc":   // reading controllers too fast counts as 1 read
                    break;
                
                default:
                    next.Add(args.Current);
                    break;
            }
        }
        
        Implementation.Initialize(next); if (System.Quit) {
            return;
        }
        Renderer.Initialize(); if (System.Quit) {
            return;
        }

        while (!Renderer.RendererReady) { }         // wait for renderer to become ready
        System.Initialize(); if (System.Quit) {
            return;
        }
        
        while (!System.Quit) {
            // open lua terminal (used to change behavior during runtime to replace UI)
            Semaphore.Wait();
            // TODO: Setup Semaphore such that it does work (eventually)
        }
    }

    internal static class Threads {
        internal static Thread? Renderer;
        internal static Thread? System;
    }
    
    internal static readonly SemaphoreSlim Semaphore = new SemaphoreSlim(0);

    internal static API.ICartridge? Cartridge;
    internal static API.IIO?        Controller1;
    internal static API.IIO?        Controller2;
    
    internal static class Config {
        internal static bool Strict;
    }
}
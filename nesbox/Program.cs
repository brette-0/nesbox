namespace nesbox;

using EList;

internal static class Program {
    internal static void Main(string[] __args) {
        var args = new EList<string>(__args);
        var next = new EList<string>();

        bool? _isFamicom = null;
        
        while (args.MoveNext()) {
            switch (args.Current) {
                case "-h" or "--help":
                    // help message
                    break;
                
                case "--sample-at":
                    // audio sampling frequency
                    break;
                
                case "-t" or "--throttle":
                    if (!args.MoveNext()) {
                        Console.WriteLine("[EMU] No throttle speed");
                        return;
                    }

                    if (!float.TryParse(args.Current, out System.Throttle)) {
                        Console.WriteLine("[EMU] Passed Argument for throttle must be a float");
                        return;
                    }
                    
                    break;
                
                case "--famicom":   // famicom system (FDS, no EXP, Cart Audio)
                    if (_isFamicom is not null) {
                        Console.WriteLine("[EMU] Target Hardware is already set");
                        return;
                    }
                    _isFamicom = true;
                    break;
                
                case "--nes":       // nes system (no FDS, has EXP, no Cart Audio)
                    if (_isFamicom is not null) {
                        Console.WriteLine("[EMU] Target Hardware is already set");
                        return;
                    }
                    _isFamicom = false;
                    break;
                
                case "--2c02":  // ntsc
                    // use 2c02
                    break;
                    
                case "--2c07":  // pal
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

        if (_isFamicom is null) {
            Console.WriteLine("[EMU] Unsure what system to emulate");
            return;
        }

        isFamicom = _isFamicom is true;

        var binds = new API.Implementation.ImplHandshake();

        Implementation.Initialize(ref binds, next); if (System.Quit) {
            return;
        }
        
        if (binds.audio is null) {
            Console.WriteLine("[EMU] No Audio Processor");
            return; 
        }

        if (binds.cartridge is null) {
            Console.WriteLine("[EMU] No Cartridge implementation");
            return; 
        }
        
        if (binds.memoryInit is null) {
            Console.WriteLine("[EMU] No Memory Init Implementation");
            return; 
        }
        
        if (binds.shader is null) {
            Console.WriteLine("[EMU] No Shader Implementation");
            return; 
        }
        
        Console.WriteLine("[EMU] Implementation Handshake Complete");
        
        Cartridge      = binds.cartridge;
        AudioProcessor = binds.audio;
        _memoryInit    = binds.memoryInit;
        _shader        = binds.shader;

        switch (Cartridge is API.IFamicomCartridge, isFamicom) {
            case (true, true):
            case (false, false):
                break;
            
            default:
                Console.WriteLine("[EMU] Cartridge does not belong to system");
                return;
        }
        
        Renderer.Initialize(); if (System.Quit) {
            return;
        }

        while (!Renderer.RendererReady) { }         // wait for renderer to become ready
        Audio.Initialize(); if (System.Quit) {
            return;
        }
        System.Initialize(); if (System.Quit) {
            return;
        }

        System.Memory.Initialize(_memoryInit);
        
        while (!System.Quit) {
            var didWork = Debug.Debugger.PumpAsync().GetAwaiter().GetResult();
            if (!didWork) Thread.Sleep(10);
        }
    }

    internal static class Threads {
        internal static Thread? Renderer;
        internal static Thread? System;
    }

    internal static float                    AudioVolume = 1f;
    internal static API.Audio.IEnhancedAudio AudioProcessor { get; private set; } = null!;
    internal static API.ICartridge           Cartridge      { get; private set; } = null!;
    internal static API.IO?                  Controller1;
    internal static API.IO?                  Controller2;
    internal static bool                     isFamicom;
    private static  Func<byte>               _memoryInit = null!;
    private static  API.Graphics.Shader      _shader     = null!;

    internal static class Config {
        internal static bool Strict;
    }
}
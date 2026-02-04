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
                    // throttle speed = args.Current
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
        
        Implementation.Initialize(next);
        System.Initialize();
        while (!System.Quit) {
            
        }
    }

    internal static API.ICartridge Cartridge;
    
    internal static class Config {
        internal static bool Strict;
    }
}
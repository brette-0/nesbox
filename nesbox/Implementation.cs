using EList;

using nesbox.Mappers;
using nesbox.IO;
using nesbox.Debug;

namespace nesbox;

/*
 *  Hello user, thanks for choosing nesbox!
 *  You decide what hardware exists and how it works by programming it.
 *  All nesbox does is provide an API for its working emulator and offer a debug suite.
 *
 *  For more information on how to get started visit https://www.website.com!
 */


internal static class Implementation {
    internal static API.ICartridge Initialize(EList<string> args) {
        var cartridge = new BULLCART(ref args);
        StandardController_NTSCU? Controller1 = null;
        Link.Subscribe.ControllerToPort(0, ref Controller1);
        API.Debugging.IDebugFile<int>? dbgFile = null;
        var                            port = 0;
        
        while (args.MoveNext()) {
            switch (args.Current) {
                case "--debuggingPort":
                    if (args.MoveNext()) {
                        if (!int.TryParse(args.Current, out port)) {
                            Console.WriteLine("[CART] Debugging port is not integer");
                            System.Quit = true;
                        }
                        if (System.Quit) break;
                    }

                    Console.WriteLine("[CART] No argument supplied for Debugging Port");
                    System.Quit = true;
                    break;

                case "--debugFile":
                    if (args.MoveNext()) {
                        dbgFile = new Ld65Dbg<int>(args.Current);
                        if (System.Quit) break;
                    }
                    
                    Console.WriteLine("[CART] No argument supplied for Debug File");
                    System.Quit = true;
                    break;
                
                default:
                    Console.WriteLine($"[CART] Unexpected Argument {args.Current}");
                    System.Quit = true;
                    break;
            }
        }

        switch (dbgFile is null, port is 0) {
            case (true, false):
                Console.WriteLine($"[CART] No debug file passed, cannot debug");
                System.Quit = true;
                break;
            
            case (false, true):
                Console.WriteLine($"[CART] No debug port passed, cannot debug");
                System.Quit = true;
                break;
        }

        return cartridge;
    }



}
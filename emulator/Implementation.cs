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
        while (args.MoveNext()) {
            switch (args.Current) {
                case "--debugFile":
                    if (args.MoveNext()) {
                        Program.DebugFile = new Ld65Dbg(args.Current);
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

        return cartridge;
    }
}
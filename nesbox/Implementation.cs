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
    internal static void Initialize(ref API.Implementation.ImplHandshake binds, EList<string> args) {
        API.Implementation.SetupSimple(ref binds);
        API.Implementation.SetupIO<StandardController_NTSCU, StandardController_NTSCU>();
        args = API.Implementation.SetupDebug<Ld65Dbg>(args);

        var cartridge = new BULLCART(ref args);
        
        if (args.Count > 0) {
            Console.WriteLine($"[IMPL] Unexpected Argument {args.Current}");
            System.Quit = true;
        }

        binds.cartridge  = cartridge;
    }
}
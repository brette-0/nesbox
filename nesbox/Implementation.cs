using EList;

using nesbox.Mappers;
using nesbox.IO;

namespace nesbox;

/*
 *  Hello user, thanks for choosing nesbox!
 *  You decide what hardware exists and how it works by programming it.
 *  All nesbox does is provide an API for its working emulator and offer a debug suite.
 *
 *  For more information on how to get started visit https://www.website.com!
 */


internal static class Implementation {
    internal static void Initialize(EList<string> args) {
        Program.Cartridge = new NROM(ref args);
        StandardController_NTSCU? Controller1 = null;
        Link.Subscribe.ControllerToPort(0, ref Controller1);
        while (args.MoveNext()) {
            switch (args.Current) {
                default:
                    Console.WriteLine($"[CART] Unexpected Argument {args.Current}");
                    break;
            }
        }
    }
}
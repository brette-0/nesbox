using EList;

namespace nesbox;

/*
 *  Hello user, thanks for choosing nesbox!
 *  You decide what hardware exists and how it works by programming it.
 *  All nesbox does is provide an API for its working emulator and offer a debug suite.
 *
 *  For more information on how to get started visit https://nesbox.readthedocs.io/en/latest/!
 */


internal static class Implementation {
    internal static void Initialize(ref API.Implementation.ImplHandshake binds, EList<string> args) {
        API.Implementation.SetupSimple(ref binds);
        API.Implementation.SetupIO<IO.StandardController_NTSCU, IO.StandardController_NTSCU>();
        API.Implementation.SetupDebug<Debug.Ld65Dbg>(ref args);

        var cartridge = new Mappers.BULLCART(ref args);
        binds.cartridge  = cartridge;
    }
}
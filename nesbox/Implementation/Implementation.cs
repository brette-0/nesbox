using EList;
using SDL3;

namespace nesbox.Implementation;

/*
 *  Hello user, thanks for choosing nesbox!
 *  You decide what hardware exists and how it works by programming it.
 *  All nesbox does is provide an API for its working emulator and offer a debug suite.
 *
 *  For more information on how to get started visit https://nesbox.readthedocs.io/en/latest/!
 */


public static class Implementation {
    internal static void Initialize(ref API.Implementation.ImplHandshake binds, EList<string> args) {
        API.Implementation.SetupSimple(ref binds);
        API.Implementation.SetupIO<IO.StandardController_NTSCU, IO.StandardController_NTSCU>();

        API.Input.InputManager.BindButtons(0, [
            SDL.Scancode.L,
            SDL.Scancode.K,
            SDL.Scancode.E,
            SDL.Scancode.J,
            SDL.Scancode.W,
            SDL.Scancode.S,
            SDL.Scancode.A,
            SDL.Scancode.D,
        ]);

        API.Implementation.SetupDebug<Debug.LlvmMosElf>(ref args);

        var cartridge = new Mappers.NROM(ref args);
        binds.cartridge  = cartridge;
    }
}
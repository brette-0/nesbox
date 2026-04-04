using System.Runtime.CompilerServices;

namespace nesbox.IO;

using SDL3;

// ============================================================================
//  Standard NES/Famicom Controller — SDL3 Gamepad Implementation
// ============================================================================
//
//  ACCURACY NOTES — platform differences:
//
//  NTSC NES (US):
//    - Detachable controllers via front DB-9 ports.
//    - After 8 reads the shift register is empty and D0 floats high through
//      the controller cable — reads return 1.
//    - Bits 1-4 of $4016/$4017 are expansion port (return 0 on stock console).
//    - Strobe: CPU writes $01 then $00 to $4016. While strobe bit is high the
//      shift register continuously reloads. State is latched on the 1→0
//      (falling edge) transition.
//
//  Famicom (JP):
//    - Controllers are hardwired (not detachable).
//    - Controller 2 physically LACKS Start and Select buttons.
//    - After 8 reads D0 is pulled to ground — reads return 0 (opposite of NES).
//    - Controller 2 has a microphone readable as bit 2 of $4016.
//    - Front expansion port feeds bits 1-4 of $4017 for peripherals.
//
//  PAL NES:
//    - Electrically identical to NTSC NES for controller wiring.
//    - Hardware read-deduplication: if $4016/$4017 is read twice within ~3 CPU
//      cycles (e.g. DPCM DMA conflict), the second read is suppressed by the
//      latch circuit. This is not yet implemented; see --palc flag.
//
//  CURRENT IMPLEMENTATION:
//    - Models NTSC NES behavior (returns 1 after 8 reads).
//    - Gamepad handles are opened/closed by the Renderer thread (which pumps
//      SDL events). This class reads button state from those shared handles.
//    - TODO: Famicom mode — return 0 after 8 reads, mask Start/Select on port 2.
//    - TODO: PAL read-dedup.
//    - TODO: Falling-edge strobe (System.cs currently only calls OnWrite on $01).
//    - TODO: Microphone (Famicom controller 2, bit 2 of $4016).
// ============================================================================

public class StandardController_NTSCU : API.IO {
    
    public override void SetIndex(byte Index) => _port = Index;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void OnTick() {
        if (!System.IOAssertion) return;
        // Gamepad handles are managed by the Renderer thread via SDL events.
        // We just read button state here — GetGamepadButton is thread-safe.
        var gp = _port is 0 ? Renderer.Gamepad0 : Renderer.Gamepad1;
        if (gp is 0) { _shift = 0; _readCount = 0; return; }

        // NES button order in the shift register (shift out D0 first):
        //   Read 1: A       Read 5: Up
        //   Read 2: B       Read 6: Down
        //   Read 3: Select  Read 7: Left
        //   Read 4: Start   Read 8: Right
        _shift = 0;
        
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.South))     _shift |= 0x01; // A
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.East))      _shift |= 0x02; // B
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.Back))      _shift |= 0x04; // Select
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.Start))     _shift |= 0x08; // Start
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadUp))    _shift |= 0x10; // Up
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadDown))  _shift |= 0x20; // Down
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadLeft))  _shift |= 0x40; // Left
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadRight)) _shift |= 0x80; // Right

        _readCount = 0;
    }
    

    public override byte OnRead() {
        // NTSC NES: after 8 reads, D0 floats high → returns 1.
        // Famicom: after 8 reads, D0 is grounded → returns 0.
        // TODO: check Program.isFamicom for correct post-8 behavior.
        if (_readCount >= 8) return 1;

        var bit = (byte)(_shift & 1);
        _shift >>= 1;
        _readCount++;
        return bit;
    }

    private byte _port;
    private byte _shift;
    private byte _readCount;
}

namespace nesbox.IO;

using Vortice.XInput;

public class StandardController_NTSCU : API.IIO {
    public StandardController_NTSCU() { }

    public void SetIndex(byte Index) => index = Index;

    public void OnWrite() {
        XInput.GetState(index, out var state);
        inputs = 0;
        foreach (var signal in new[] {
            (state.Gamepad.Buttons & GamepadButtons.DPadRight) != 0, 
            (state.Gamepad.Buttons & GamepadButtons.DPadLeft)  != 0, 
            (state.Gamepad.Buttons & GamepadButtons.DPadDown)  != 0,
            (state.Gamepad.Buttons & GamepadButtons.DPadUp)    != 0,
            (state.Gamepad.Buttons & GamepadButtons.Start)     != 0,
            (state.Gamepad.Buttons & GamepadButtons.Guide)     != 0,
            (state.Gamepad.Buttons & GamepadButtons.X)         != 0,
            (state.Gamepad.Buttons & GamepadButtons.A)         != 0
        }) inputs = (byte)((inputs << 1) | (signal ? 1 : 0));
        
    }

    public bool OnRead() {
        var signal = (inputs & 1) is 1;
        inputs >>= 1;
        shift++;
        return signal;
    }

    private byte index  = 0;
    private byte inputs = 0;
    private byte shift  = 0;
}
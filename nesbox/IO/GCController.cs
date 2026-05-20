using System.Runtime.CompilerServices;
using SDL3;
using nesbox.Emulator;
namespace nesbox.IO;

/*
 *  This is a WIP controller idea. Do not use it, it doesn't work unless you're the guy working with me on it
 */

// ReSharper disable once InconsistentNaming
internal sealed class GCController : API.IIO, API.IClockDriven {
    public GCController() {
        Link.Subscribe.OnTick(this);
        _controllerIntervalTime = API.Helper.MillisecondsToTicks(4f);
    }
    
    public byte OnRead() {
        if (_taskLatch) {
            switch (_modeSelect) {
                case ModeSelect.Report:
                    _shift >>= 1;
                    _shift |=  Emulator.System.IOAssertion ? 0x01 : (ulong)0x00;
                    if (--_taskLength is 0) {
                        _taskLatch   = false;
                    }
                    
                    return (byte)(_shift & 1);
                

                case ModeSelect.Behavior:
                    _pollingModeBuffer <<= 1;
                    _pollingModeBuffer |=  (byte)(Emulator.System.IOAssertion ? 0x01 : 0x00);
                    if (--_taskLength is not 0) return 0;
                    _pollingMode = (PollingMode)_pollingModeBuffer;
                    _taskLatch   = false;
                    return 0;

                case ModeSelect.Rumble:
                    return 0;

                case ModeSelect.Invert:
                    _flipBuffer <<= 1;
                    if (--_taskLength is not 0) return 0;
                    _flip      = _flipBuffer;
                    _taskLatch = false;
                    return 0;
                
                case ModeSelect.Legacy:
                    break;
                
                case ModeSelect.LegacySetup:
                    break;

                case ModeSelect.End:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (Emulator.System.IOAssertion) {
            _modeSelect = (ModeSelect)((int)++_modeSelect % (int)ModeSelect.End);
        } else if (_modeSelect is ModeSelect.Legacy) {
            // TODO: return bit            
        }
        
        return 0;
    }
    
    public void SetIndex(byte index) => _port = index;
    public void OnWrite() {
        throw new NotImplementedException();
    }

    private enum ModeSelect : byte {
        Legacy,
        Report,
        Behavior,
        Rumble,
        Invert,
        LegacySetup,
        End,
    }
    
    [Flags]
    private enum PollingMode : byte {
        LtoC            = 0x01,  // Sends actuation of L to C
        CtoL            = 0x02,  // Sends actuation of C to L
        DToL            = 0x04,  // sends signal of d-pad to L if actuated, else L wins
        DToC            = 0x08,  // sends signal of d-pad to C if actuated, else C wins
        UnifiedTrigger  = 0x10,
        NoTriggers      = 0x20,
        NoCStick        = 0x40,
        NoLStick        = 0x80,
    }

    [Flags]
    private enum LegacyPollingMode : byte {
        X_IS_TURBO_A    = 0x01,
        Y_IS_TURBO_B    = 0x02,
    }


    // adaptor => console
    private ulong             _flipBuffer;
    private ulong             _flip;
    private byte              _pollingModeBuffer;
    private byte              _taskLength;
    private PollingMode       _pollingMode;      // mode of behavior
    private LegacyPollingMode _legacyPollingMode;
    private ModeSelect        _modeSelect;
    private bool              _taskLatch; // latch onto task
    private byte              _port;
    
    // adaptor => controller
    private ulong  _shift;

    private ulong _deltaTicks;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public  void        OnTick() {
        // real time response time sim
        _deltaTicks += 1;
        if (_deltaTicks < _controllerIntervalTime) return;
        _deltaTicks = 0;

        ulong report = 0;
        
        var gp = _port < API.Input.InputManager.Gamepads.Length ? API.Input.InputManager.Gamepads[_port] : 0;
        if (gp is 0) { _shift = 0; _taskLength = 0; return; }
        
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.South))                 report |= 0x001; // B
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.East))                  report |= 0x002; // Y
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.Back))                  report |= 0x004; // Select
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.Start))                 report |= 0x008; // Start
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadUp))                report |= 0x010; // Up
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadDown))              report |= 0x020; // Down
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadLeft))              report |= 0x040; // Left
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadRight))             report |= 0x080; // Right
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.North))                 report |= 0x100; // A
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.West))                  report |= 0x200; // X
        if (SDL.GetGamepadAxis  (gp, SDL.GamepadAxis.  LeftTrigger ) is not 0) report |= 0x400; // L atomic
        if (SDL.GetGamepadAxis  (gp, SDL.GamepadAxis.  RightTrigger) is not 0) report |= 0x800; // R atomic

        report |= 0x1000;   // signature (not a nes/snes controller)
        
        report |= (uint)(ProcessTrigger(gp, SDL.GamepadAxis.LeftTrigger)  << 13);
        report |= (uint)(ProcessTrigger(gp, SDL.GamepadAxis.RightTrigger) << 21);

        var (lx, ly) =  ProcessStick(gp, (SDL.GamepadAxis.LeftX, SDL.GamepadAxis.LeftY));
        var (cx, cy) =  ProcessStick(gp, (SDL.GamepadAxis.RightX, SDL.GamepadAxis.RightY));
        report       |= (ulong)lx << 29;
        report       |= (ulong)ly << 37;
        report       |= (ulong)cx << 45;
        report       |= (ulong)cy << 53;
        
        // flip bits
        report ^= _flip;

        if (_pollingMode.HasFlag(PollingMode.LtoC) && cx is 0 && cy is 0) {
            (cx, cy) = (lx, ly);
        }
        
        if ( _pollingMode.HasFlag(PollingMode.CtoL) && 
            !_pollingMode.HasFlag(PollingMode.LtoC) && 
            cx is 0 && 
            cy is 0) {
            (lx, ly) = (cx, cy);
        }

        CopyPadToStick(PollingMode.DToL, ref lx, ref ly);
        CopyPadToStick(PollingMode.DToC, ref cx, ref cy);
                    
        // ready for reading
    }
    
     // ReSharper disable once SeparateLocalFunctionsWithJumpStatement
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CopyPadToStick(PollingMode stick, ref byte x, ref byte y) {
            if (!_pollingMode.HasFlag(stick)                ||
                x is not 0) return;
            
            var dpad = (byte)((_shift >> 4) & 0x0f);
            if (dpad is 0) return;  // doesn't replace L unless in use
                
            dpad &= (byte)((_shift & 0b1010) is 0b1010 ? 0b0101 : 0b1111);
            dpad &= (byte)((_shift & 0b0101) is 0b0101 ? 0b1010 : 0b1111);

            var (a, m) = dpad switch {
                0b0001 => (0x00, 0xff),
                0b0010 => (0x80, 0xff),
                0b0100 => (0xc0, 0xff),
                0b1000 => (0x40, 0xff),
                0b0101 => (0xe0, 256 * Math.Sqrt(2)),
                0b1001 => (0x20, 256 * Math.Sqrt(2)),
                0b0110 => (0xa0, 256 * Math.Sqrt(2)),
                0b1010 => (0x60, 256 * Math.Sqrt(2)),
                _      => throw new ArgumentOutOfRangeException()
            };

            (x, y) = ((byte)a, (byte)m);
        }
        
        // resolution scaling for triggers
        // ReSharper disable once SeparateLocalFunctionsWithJumpStatement
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ProcessTrigger(nint GamePad, SDL.GamepadAxis axis) {
            var  capture  = SDL.GetGamepadAxis(GamePad, axis);
            byte capture8 = 0;
            for (var i = 0; i < 8; i++) {
                capture8 |=  (byte)((capture >> (2 * i + 1)) & 1);
                capture8 <<= 1;
            }

            return capture8;
        }
        
        // resolution scaling for sticks
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (byte, byte) ProcessStick(nint GamePad, (SDL.GamepadAxis x, SDL.GamepadAxis y) axes) {
            var x = SDL.GetGamepadAxis(GamePad, axes.x);
            var y = SDL.GetGamepadAxis(GamePad, axes.y);
            (byte x8, byte y8) = (0, 0);
            for (var i = 0; i < 8; i++) {
                x8 |=  (byte)((x >> (2 * i + 1)) & 1);
                x8 <<= 1;
                y8 |=  (byte)((y >> (2 * i + 1)) & 1);
                y8 <<= 1;
            }
            
            return (x8, y8);
        }

    private static ulong _controllerIntervalTime;
}
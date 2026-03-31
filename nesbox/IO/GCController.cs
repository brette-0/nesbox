using System.Runtime.CompilerServices;
using SDL3;

namespace nesbox.IO;

/*
 *  This is a WIP controller idea. Do not use it, it doesn't work unless you're the guy working with me on it
 */

// ReSharper disable once InconsistentNaming
internal sealed class GCController : API.IIO, API.IClockDriven {
    public GCController() {
        Link.Subscribe.OnTick(this);
    }
    
    public void OnWrite() {
        if (_taskLatch) {
            switch (_modeSelect) {
                case ModeSelect.Report:
                    // poll bits into wide
                    if (!_pollReady) return;
                    _pollReady   = false;
                    _delayCycles = 32_214;
 
                    return;

                case ModeSelect.Behavior:
                    _pollingModeBuffer <<= 1;
                    _pollingModeBuffer |=  1;
                    if (--_taskLength is not 0) return;
                    _pollingMode = (PollingMode)_pollingModeBuffer;
                    _taskLatch   = false;
                    return;

                case ModeSelect.Skip:
                    _pollingMaskBuffer <<= 1;
                    _pollingMaskBuffer |=  1;
                    _nInputs++;
                    if (--_taskLength is not 0) return;
                    _pollingMask = _pollingMaskBuffer;
                    _taskLatch   = false;
                    return;

                case ModeSelect.Rumble:
                    break;

                case ModeSelect.Invert:
                    _flipBuffer <<= 1;
                    _flipBuffer |=  1;
                    if (--_taskLength is not 0) return;
                    _flip      = _flipBuffer;
                    _taskLatch = false;
                    return;

                case ModeSelect.End:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        } else {
            _taskLatch = true;
            switch (_modeSelect) {
                case ModeSelect.Report:
                    _taskLength = _nInputs;
                    return;

                case ModeSelect.Behavior:
                    _taskLength = 8;
                    return;

                case ModeSelect.Skip:
                    _taskLength = 61;
                    _nInputs    = 0;
                    return;

                case ModeSelect.Rumble:
                    return;

                case ModeSelect.Invert:
                    _taskLength = _nInputs;
                    return;

                case ModeSelect.End:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
    public byte OnRead() {
        if (_taskLatch) {
            switch (_modeSelect) {
                case ModeSelect.Report:
                    if (!_pollReady) return 0;
                    
                    _inputs >>= 1;
                    if (--_taskLength is 0) {
                        _taskLatch   = false;
                    }
                    
                    return (byte)(_inputs & 1);
                

                case ModeSelect.Behavior:
                    _pollingModeBuffer <<= 1;
                    if (--_taskLength is not 0) return 0;
                    _pollingMode = (PollingMode)_pollingModeBuffer;
                    _taskLatch   = false;
                    return 0;

                case ModeSelect.Skip:
                    _pollingMaskBuffer <<= 1;
                    if (--_taskLength is not 0) return 0;
                    _pollingMask = _pollingMaskBuffer;
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

                case ModeSelect.End:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        _modeSelect = (ModeSelect)((int)++_modeSelect % (int)ModeSelect.End);
        return 0;
    }
    
    public void SetIndex(byte index) => _port = index;

    private enum ModeSelect : byte {
        Report,
        Behavior,
        Skip,
        Rumble,
        Invert,
        
        End,
    }
    
    [Flags]
    private enum PollingMode : byte {
        LtoC     = 0x01,  // Sends actuation of L to C
        CtoL     = 0x02,  // Sends actuation of C to L
        DToL     = 0x04,  // sends signal of d-pad to L if actuated, else L wins
        DToC     = 0x08,  // sends signal of d-pad to C if actuated, else C wins
        LPrecalc = 0x10,  // precalculates L trig angles in 8bit degrees
        CPrecalc = 0x20,  // precalculates C trig angles in 8bit degrees
    }


    // adaptor => console
    private int         _flipBuffer;
    private int         _flip;
    private byte        _nInputs;           // the amount of bits to report
    private int         _inputs;            // built result on writing when mode select is report
    private int         _pollingMaskBuffer; // set when building mask
    private int         _pollingMask;       // set by completing buffer, fetched when using active
    private byte        _pollingModeBuffer;
    private byte        _taskLength;
    private PollingMode _pollingMode;      // mode of behavior
    private ModeSelect  _modeSelect;
    private bool        _taskLatch; // latch onto task
    private byte        _port;
    
    // adaptor => controller
    private bool   _pollReady;
    private ushort _delayCycles;
    private ulong  _shift;
    private byte   _readCount;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public  void        OnTick() {
        if (_pollReady) return;
        if (--_delayCycles != 0) return;

        ulong report = 0;
        
        var gp = _port is 0 ? Renderer.Gamepad0 : Renderer.Gamepad1;
        if (gp is 0) { _shift = 0; _readCount = 0; return; }
        
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.South))                 _shift |= 0x001; // B
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.East))                  _shift |= 0x002; // Y
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.Back))                  _shift |= 0x004; // Select
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.Start))                 _shift |= 0x008; // Start
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadUp))                _shift |= 0x010; // Up
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadDown))              _shift |= 0x020; // Down
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadLeft))              _shift |= 0x040; // Left
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.DPadRight))             _shift |= 0x080; // Right
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.North))                 _shift |= 0x100; // A
        if (SDL.GetGamepadButton(gp, SDL.GamepadButton.West))                  _shift |= 0x200; // X
        if (SDL.GetGamepadAxis  (gp, SDL.GamepadAxis.  LeftTrigger ) is not 0) _shift |= 0x400; // L atomic
        if (SDL.GetGamepadAxis  (gp, SDL.GamepadAxis.  RightTrigger) is not 0) _shift |= 0x800; // R atomic

        _shift |= 0x1000;   // signature (not a nes/snes controller)
        
        _shift |= (uint)(ProcessTrigger(SDL.GamepadAxis.LeftTrigger) << 13);
        _shift |= (uint)(ProcessTrigger(SDL.GamepadAxis.RightTrigger) << 21);

        var (lx, ly) = ProcessStick((SDL.GamepadAxis.LeftX, SDL.GamepadAxis.LeftY));
        var (cx, cy) = ProcessStick((SDL.GamepadAxis.RightX, SDL.GamepadAxis.RightY));

        if (_pollingMode.HasFlag(PollingMode.LtoC) && cx is 0 && cy is 0) {
            (cx, cy) = (lx, ly);
        }
        
        if ( _pollingMode.HasFlag(PollingMode.CtoL) && 
            !_pollingMode.HasFlag(PollingMode.LtoC) && 
            cx is 0 && 
            cy is 0) {
            (lx, ly) = (cx, cy);
        }
        
        if (_pollingMode.HasFlag(PollingMode.LPrecalc)) {
            lx     =  (byte)Math.Atan2(lx, ly);
            _shift |= (ulong)lx << 29;
            ly     =  (byte)Math.Sqrt(lx * lx + ly * ly);
            _shift |= (ulong)lx << 37;
        } else {
            _shift |= (ulong)lx << 29;
            _shift |= (ulong)ly << 37;
        }


        if (_pollingMode.HasFlag(PollingMode.CPrecalc)) {
            cx     =  (byte)Math.Atan2(cx, cy);
            _shift |= (ulong)cx << 45;
            cy     =  (byte)Math.Sqrt(cx * cx + cy * cy);
            _shift |= (ulong)cy << 37;
        } else {
            _shift |= (ulong)cx << 45;
            _shift |= (ulong)cy << 53;
        }

        CopyPadToStick(PollingMode.DToL, ref lx, ref ly);
        CopyPadToStick(PollingMode.DToC, ref cx, ref cy);
        
        // copy wanted bits into second by skipping bits clear in mask
        _inputs = 0;
        for (var s = 0; s < _nInputs; s++) {
            if ((_pollingMask & 1 << s) is 0) continue;
            report  |=  (report   >> s) & 1;
            _inputs <<= 1;
        }
                    
        // flip bits
        _inputs ^= _flip;
                    
        // ready for reading
        _pollReady = true;

        // ReSharper disable once SeparateLocalFunctionsWithJumpStatement
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CopyPadToStick(PollingMode stick, ref byte x, ref byte y) {
            if (!_pollingMode.HasFlag(PollingMode.LPrecalc) ||
                !_pollingMode.HasFlag(stick)                ||
                lx is not 0) return;
            
            var dpad = (byte)((_shift >> 4) & 0x0f);
            if (dpad is 0) return;  // doesn't replace L unless in use
                
            dpad &= (byte)((_shift & 0b1010) is 0b1010 ? 0b0101 : 0b1111);
            dpad &= (byte)((_shift & 0b0101) is 0b0101 ? 0b1010 : 0b1111);

            var (a, m) = dpad switch {
                0b0001 => (0x00, 0xff),
                0b0010 => (0x80, 0xff),
                0b0100 => (0xc0, 0xff),
                0b1000 => (0x40, 0xff),
                0b0101 => (0xe0, Math.Sqrt(2)),
                0b1001 => (0x20, Math.Sqrt(2)),
                0b0110 => (0xa0, Math.Sqrt(2)),
                0b1010 => (0x60, Math.Sqrt(2)),
                _      => throw new ArgumentOutOfRangeException()
            };

            (x, y) = ((byte)a, (byte)m);
        }
        
        // ReSharper disable once SeparateLocalFunctionsWithJumpStatement
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        byte ProcessTrigger(SDL.GamepadAxis axis) {
            var  capture = SDL.GetGamepadAxis(gp, axis);
            byte capture8 = 0;
            for (var i = 0; i < 8; i++) {
                capture8 |=  (byte)((capture >> (2 * i + 1)) & 1);
                capture8 <<= 1;
            }

            return capture8;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        (byte, byte) ProcessStick((SDL.GamepadAxis x, SDL.GamepadAxis y) axes) {
            var  x  = SDL.GetGamepadAxis(gp, axes.x);
            var  y  = SDL.GetGamepadAxis(gp, axes.y);
            (byte x8, byte y8) = (0, 0);
            for (var i = 0; i < 8; i++) {
                x8 |=  (byte)((x >> (2 * i + 1)) & 1);
                x8 <<= 1;
                y8 |=  (byte)((y >> (2 * i + 1)) & 1);
                y8 <<= 1;
            }
            
            return (x8, y8);
        }
    }
}
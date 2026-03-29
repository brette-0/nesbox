namespace nesbox.IO;

/*
 *  This is a WIP controller idea, do not use it it doesn't work unless your the guy working with me on it
 */

internal sealed class GCController : API.IIO {
    public void OnWrite() {
        if (_taskLatch) {
            switch (_modeSelect) {
                case ModeSelect.REPORT:
                    // poll bits into wide
                    var report = 0;

                    // copy wanted bits into second by skippings bits clear in mask
                    _inputs = 0;
                    for (var s = 0; s < _nInputs; s++) {
                        if ((_pollingMask & 1 << s) is 0) continue;
                        _inputs |=  (report   >> s) & 1;
                        _inputs <<= 1;
                    }
                    
                    // flip bits
                    _inputs ^= _flip;
                    
                    // ready for reading
                    break;

                case ModeSelect.BEHAVIOR:
                    _pollingModeBuffer <<= 1;
                    _pollingModeBuffer |=  1;
                    if (--_taskLength is 0) {
                        _pollingMode = (PollingMode)_pollingModeBuffer;
                        _taskLatch   = false;
                    }
                    break;

                case ModeSelect.SKIP:
                    _pollingMaskBuffer <<= 1;
                    _pollingMaskBuffer |=  1;
                    _nInputs++;
                    if (--_taskLength is 0) {
                        _pollingMask = _pollingMaskBuffer;
                        _taskLatch   = false;
                    }
                    break;

                case ModeSelect.RUMBLE:
                    break;

                case ModeSelect.INVERT:
                    _flipBuffer <<= 1;
                    _flipBuffer |=  1;
                    if (--_taskLength is 0) {
                        _flip = _flipBuffer;
                        _taskLatch   = false;
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        } else {
            _taskLatch = true;
            switch (_modeSelect) {
                case ModeSelect.REPORT:
                    _taskLength = _nInputs;
                    break;

                case ModeSelect.BEHAVIOR:
                    _taskLength = 8;
                    break;

                case ModeSelect.SKIP:
                    _taskLength = 27;
                    _nInputs    = 0;
                    break;

                case ModeSelect.RUMBLE:
                    break;

                case ModeSelect.INVERT:
                    _taskLength = _nInputs;
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        throw new NotImplementedException();
    }
    public byte OnRead() {
        if (_taskLatch) {
            switch (_modeSelect) {
                case ModeSelect.REPORT:
                    var report = _inputs & 1;
                    _inputs >>= 1;
                    if (--_taskLength is 0) {
                        _taskLatch   = false;
                    }
                    
                    return (byte)report;
                

                case ModeSelect.BEHAVIOR:
                    _pollingModeBuffer <<= 1;
                    if (--_taskLength is 0) {
                        _pollingMode = (PollingMode)_pollingModeBuffer;
                        _taskLatch   = false;
                    }
                    return 0;

                case ModeSelect.SKIP:
                    _pollingMaskBuffer <<= 1;
                    if (--_taskLength is 0) {
                        _pollingMask = _pollingMaskBuffer;
                        _taskLatch   = false;
                    }
                    return 0;

                case ModeSelect.RUMBLE:
                    return 0;

                case ModeSelect.INVERT:
                    _flipBuffer <<= 1;
                    if (--_taskLength is not 0) return 0;
                    _flip      = _flipBuffer;
                    _taskLatch = false;
                    return 0;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        _modeSelect = (ModeSelect)((int)++_modeSelect % (int)ModeSelect.END);
        
        throw new NotImplementedException();
    }
    
    public void SetIndex(byte Index) => _port = Index;

    private enum ModeSelect : byte {
        REPORT,
        BEHAVIOR,
        SKIP,
        RUMBLE,
        INVERT,
        
        END,
    }
    
    [Flags]
    private enum PollingMode : byte {
        LCSync     = 0x01,  // Sends acuation of either L or C to unactuated, else C wins.
        DToL       = 0x02,  // sends signal of d-pad to L if actuated, else L wins
        DToC       = 0x04,  // sends signal of d-pad to C if actuated, else C wins
        LPrecalc   = 0x08,  // precalculates L trig angles in 8bit degrees
        CPrecalc   = 0x10,  // precalculates C trig angles in 8bit degrees
        LAtomic    = 0x20,  // converts L trigger out to atomic 1/0 value
        CAtomic    = 0x40   // converts C trigger out to atomic 1/0 value
    }
    
    // report order: LLLL_LLLL_CCCC_CCCC_llrr_ABXY_ZSs


    private int  _flipBuffer;
    private int  _flip;
    private byte _nInputs;           // the amount of bits to report
    private byte _shift;
    private int  _inputs;           // built result on writing when mode select is report
    private int  _pollingMaskBuffer;    // set when building mask
    private int  _pollingMask;      // set by completing buffer, fetched when using active
    private byte _pollingModeBuffer;
    private byte _taskLength;
    private PollingMode _pollingMode;      // mode of behavior
    private ModeSelect  _modeSelect;
    private bool        _taskLatch; // latch onto task
    private byte        _port;
}
using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace nesbox.Emulator;

// TODO: convert Memory module IO writes to set an internal state, revise IO response to R/W semantics and remove strobing

internal static partial class System {
    internal const ulong DOTS_PER_FRAME = 89_342;

    /// <summary>
    /// Begin CPU Emulation
    /// </summary>
    internal static void Initialize() {
        Program.Threads.System = new Thread(RunSystem){IsBackground = false};
        Program.Threads.System.Start();
    }

    private static void RunSystem() {
        Console.WriteLine("[CPU] System is Running");

        Register.AC = (byte)Random.Shared.Next();
        Register.X  = (byte)Random.Shared.Next();
        Register.Y  = (byte)Random.Shared.Next();
        Register.S  = 0xfd;
        Register.i  = true;

        Reset    = true;
        OpHandle = StepReset;
        PPU.warmupEndDot = virtualTime + PPU.WARMUP_DOTS;

        const double fps = 60.0988d;
        var frameTimeSeconds = 1.0 / fps;

        var freq = Stopwatch.Frequency;

        long frameDeadlineTick = 0;
        var lastThrottle = float.NaN;

        ulong frames = 0, lateFrames = 0;
        long nextPrint = 0;
        double worstLateMs = 0;

        var untilNextSample = 1d / SamplingFrequency;
        var secondsPerDot   = 1d / dotsPerSecond;
        int cpuDiv = 2;

        DoNotProgress:
        while (!Quit) {
            if (Debug.Debugger.debugging) {
                Debug.Debugger.ResumeEvent.Wait(1000);
                Debug.Debugger.ResumeEvent.Reset();
                goto DoNotProgress;
            } else {
                PPU.Step();
                Link.TriggerClockDrivenImplementations();
                if (++cpuDiv >= 3) {
                    cpuDiv = 0;
                    Step();
                    APU.Step();
                    APU.PCM.DMA_Step();
                    PPU.OAM.DMA();
                    if (Quit) return;
                }

                if (!Program.NoAudio && (untilNextSample -= secondsPerDot) <= 0d) {
                    untilNextSample  += 1d / SamplingFrequency;
                    SampleBuffer.Add(Program.AudioVolume *
                                     Program.AudioProcessor.PostProcessSample(APU.GetPCMSample()));
                }
            }

            if (PPU.FrameComplete) {
                PPU.FrameComplete = false;
                // Publish the just-finished frame to the renderer thread and
                // drain audio. These are independent of pacing — they must
                // happen every frame regardless of Throttle, otherwise we
                // produce no video and silence.
                Renderer.Present();

                if (!Program.NoAudio && Throttle == 1f)
                    Audio.Drain(SampleBuffer);
                else if (!Program.NoAudio)
                    SampleBuffer.Clear();

                frames++;

                // Pacing wait — only when the user requested throttling.
                // Without it we run flat-out and the CPU will pin a core.
                if (Throttle > 0f) {
                    var frameStartTick = Stopwatch.GetTimestamp();

                    var effectiveFrameSeconds = frameTimeSeconds / Throttle;
                    var frameTicks = (long)(effectiveFrameSeconds * freq);
                    if (frameTicks < 1) frameTicks = 1;

                    if (frameDeadlineTick == 0 || Throttle != lastThrottle) {
                        lastThrottle      = Throttle;
                        frameDeadlineTick = frameStartTick + frameTicks;
                    }

                    var workEndTick = Stopwatch.GetTimestamp();

                    if (workEndTick > frameDeadlineTick) {
                        lateFrames++;

                        var lateMs = (workEndTick - frameDeadlineTick) * 1000.0 / freq;
                        if (lateMs > worstLateMs) worstLateMs = lateMs;

                        if (Program.Config.Strict) {
                            Console.WriteLine("[CPU] Unable to compute in time");
                            Quit = true;
                            return;
                        }

                        var behindTicks = workEndTick              - frameDeadlineTick;
                        var missed      = behindTicks / frameTicks + 1;
                        frameDeadlineTick += missed * frameTicks;
                    } else {
                        while (Stopwatch.GetTimestamp() < frameDeadlineTick) {
                            Thread.Yield();
                        }

                        frameDeadlineTick += frameTicks;

                        var afterWait = Stopwatch.GetTimestamp();
                        if (afterWait > frameDeadlineTick) {
                            frameDeadlineTick = afterWait + frameTicks;
                        }
                    }
                }

                var now = Stopwatch.GetTimestamp();
                if (nextPrint == 0) nextPrint = now + freq;
                if (now >= nextPrint) {
                    #if DEBUG
                    Console.WriteLine($"[CPU] thr={Throttle:0.###} fps={frames:0} late={lateFrames} worstLateMs={worstLateMs:0.###}");
                    #endif
                    frames      = 0;
                    lateFrames  = 0;
                    worstLateMs = 0;
                    do nextPrint += freq; while (nextPrint <= now);
                }
            }

            ++virtualTime;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Step() {
        if (RDY) {
            if (cycle > 0) _dmaHaltedDuringInstruction = true;
            return;
        }
        if (cycle is 0) {
            _dmaHaltedDuringInstruction = false;
            if (Reset) {
                Console.WriteLine("[CPU] Resetting CPU");
                OpHandle = StepReset;
                goto HandleInstruction;
            }

            // ── NMI service ──────────────────────────────────
            // Fire a pending NMI BEFORE latching new edges.
            if (_nmiPending) {
                _nmiPending = false;
                NMIAsserted = false;
                Vector      = Vectors.NMI;
                OpHandle    = Interrupt;
                goto HandleInstruction;
            }

            // ── NMI latch (at cycle 0, after service check) ─
            if (NMIAsserted && !_nmiPending) {
                _nmiPending = true;
            }

            if (_irqDetected) {
                _irqDetected = false;
                if (!prevInterruptInhibit) {
                    Vector     = Vectors.IRQ;
                    OpHandle   = Interrupt;
                    goto HandleInstruction;
                }
            }

            FetchInstruction:
            _irqDetected = CPU_IRQ || APU.FrameIRQAsserted || APU.PCM.IRQFlag;
            prevInterruptInhibit = Register.i;
            AD          = PC;
            DriveAddressPins();

            Memory.CPU_Read();
            PC++;
            Register.IR = Data;
            OpHandle    = OpCodes.GetOpcodeSolver(Register.IR);

            // Check for breakpoints on the instruction we just fetched.
            // PC has already been incremented so the instruction address is PC-1.
            // This is the only moment the address is stable and mappable to a source line.
            if (Debug.Debugger.CheckBreakpoint((ushort)(PC - 1))) {
                Debug.Debugger.debugging = true;
                Debug.Debugger.ResumeEvent.Set();
            }

            cycle++;
            return;
        }

        HandleInstruction:
            OpHandle();

            // ── NMI latch (non-cycle-0, post-execute) ───────────
            // Latch NMIAsserted → _nmiPending AFTER OpHandle runs,
            // but only on non-last cycles (cycle != 0xFF).
            //
            //  • PPU-sourced NMI on non-last cycle:
            //    NMIAsserted was set before CPU.Step entered.
            //    Latched here → fires at NEXT cycle-0.
            //    Result: 1-instruction-boundary delay. ✓
            //
            //  • PPU-sourced NMI on last cycle (cycle == 0xFF):
            //    NOT latched here — falls through to the cycle-0
            //    latch on the next CPU.Step.  Fires one cycle-0
            //    later → 2-instruction-boundary delay. ✓
            //
            //  • CPU-sourced NMI (W2000 write during OpHandle):
            //    NMIAsserted set inside OpHandle → caught here
            //    if not last cycle; otherwise deferred. ✓
            //
            //  • During interrupt/reset handlers: skip — NMI
            //    hijack is handled in Interrupt case 5.
            if (NMIAsserted && !_nmiPending
                && cycle != 0xFF
                && OpHandle != Interrupt
                && OpHandle != StepReset) {
                _nmiPending = true;
            }

            #if DEBUG
            if (cycle is 0xff && OpHandle != Interrupt && OpHandle != StepReset) {
                //Console.WriteLine($"{PC:x4}: {OpCodes.Mnemonics[Register.IR]} {Address:x4} {Data:x2}");
            }
            #endif
            cycle++;
    }

    private static void StepReset() {
        switch (cycle) {
            case 0:
                AD = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                break;

            case 1:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                break;

            case 2:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                break;

            case 3:
                ADH = 0x01;
                ADL = Register.S;
                DriveAddressPins();
                Memory.CPU_Read();
                Register.S--;
                Register.i = true;
                break;

            case 4:
                AD = 0xfffc;
                DriveAddressPins();
                Memory.CPU_Read();
                DB = Data;
                break;

            case 5:
                AD = 0xfffd;
                DriveAddressPins();
                Memory.CPU_Read();
                PCL   = DB;
                PCH   = Data;
                cycle = 0xff;
                Reset = false;
                prevInterruptInhibit = true;
                break;

            default:
                Console.WriteLine("[CPU] StepReset on incorrect cycle");
                Quit = true;
                break;
        }
    }

    private static void Interrupt() {
        switch (cycle) {
            case 0:
                AD        = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                return;

            case 1:
                AD        = PC;
                DriveAddressPins();
                Memory.CPU_Read();
                break;

            case 2:
                Data = PCH;
                Memory.Push();
                break;

            case 3:
                Data = PCL;
                Memory.Push();
                break;

            case 4:
                Data =
                    (byte)((Register.c ? 1 : 0) << 0 |
                           (Register.z ? 1 : 0) << 1 |
                           (Register.i ? 1 : 0) << 2 |
                           (Register.d ? 1 : 0) << 3 |
                           0                         |
                           32                        |
                           (Register.v ? 1 : 0) << 6 |
                           (Register.n ? 1 : 0) << 7);
                Memory.Push();
                Register.i = true;
                break;

            case 5:
                if (NMIAsserted) {
                    NMIAsserted = false;
                    Vector      = Vectors.NMI;
                }
                AD = Vector;
                DriveAddressPins();
                Memory.CPU_Read();
                DB  = Data;
                PCL = DB;
                break;

            case 6:
                AD = (ushort)(Vector + 1);
                DriveAddressPins();
                Memory.CPU_Read();
                PCH   = Data;
                cycle = 0xff;
                prevInterruptInhibit = true;
                break;

            default:
                Console.WriteLine("[CPU] StepIRQ on incorrect cycle");
                Quit = true;
                break;
        }
    }


    internal static ushort Vector;
    internal static bool   CPU_IRQ;
    internal static bool   NMIAsserted;
    /// <summary>
    /// Models the real 6502 NMI polling delay: NMI is sampled at each
    /// instruction boundary and serviced one boundary later.  An edge
    /// that arrives during the last cycle of instruction N is not
    /// detected until instruction N+1's penultimate-cycle poll, so the
    /// CPU takes NMI only after N+1 completes.
    /// </summary>
    internal static bool   _nmiPending;
    private static  bool   Reset;
    internal static bool   prevInterruptInhibit;
    internal static bool   _irqDetected;


    internal static Action OpHandle;
    internal static byte   cycle;
    internal static bool   _dmaHaltedDuringInstruction;

    internal static ushort Address;
    internal static byte   Data;
    internal static byte   OpenBus;
    internal static byte   DB;
    internal static byte   PCL;
    internal static byte   PCH;
    internal static byte   ADL;
    internal static byte   ADH;

    internal static ushort AD {
        get => (ushort)((ADH << 8) | ADL);
        set {
            ADH = (byte)(value >> 8);
            ADL = (byte)(value & 0xff);
        }
    }

    internal static ushort PC {
        get => (ushort)((PCH << 8) | PCL);
        set {
            PCH = (byte)(value >> 8);
            PCL = (byte)(value & 0xff);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DriveAddressPins()
    {
        Address = (ushort)((ADH << 8) | ADL);
    }

    internal static class Register {
        // Registers
        internal static byte IR = 0x00; // instruction register (stores the current operation code)
        internal static byte X  = 0x00;
        internal static byte Y  = 0x00; // index registers
        internal static byte S  = 0x00; // stack pointer
        internal static byte AC = 0x00; // accumulator

        // flags
        internal static bool c = false;
        internal static bool z = false;
        internal static bool i = false;
        internal static bool d = false;
        internal static bool b = false;
        internal static bool v = false;
        internal static bool n = false;
    }

    internal static class Vectors {
        internal const ushort NMI   = 0xfffa;
        internal const ushort Reset = 0xfffc;
        internal const ushort IRQ   = 0xfffe;
    }

    private const ulong  dotsPerSecond  = 5_369_318ul;

    internal static bool   RDY;
    private  const  double SECONDS_PER_FRAME    = 0d;
    internal static float  Throttle             = float.NegativeInfinity;
    internal static ulong  virtualTime          = 0;
    internal static bool   Quit                 = false;
    internal static uint   SamplingFrequency    = 48_000;
    internal static double SamplingCoefficiient = 0f;
    internal static bool   fetchOnNext;
    internal static List<float> SampleBuffer = [];
    internal static APU.PulseChannel Pulse1 = new();
    internal static APU.PulseChannel Pulse2 = new();

    internal static bool IOAssertion;
    // Debug variables
    private static string _mnemonic = string.Empty;
}

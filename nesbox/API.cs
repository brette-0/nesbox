using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using nesbox.Debug;
using SDL3;

namespace nesbox;

using EList;

/// <summary>
/// Contains all methods that may interface components designed by user with the emulator
/// </summary>
public static class API {
    public static class Graphics {
        public interface Shader {
            internal SDL3.SDL.Color Recolour(SDL3.SDL.Color c);    
        }
    }

    public static class Helper {
        private const double PpuFrequencyHz = 236250000.0   / 11.0 / 4.0;
       
        /// <summary>
        /// should be used for diagnostic information primarily
        /// </summary>
        /// <param name="ticks">the amount of ticks to convert into time</param>
        /// <returns></returns>
        public static double TicksToMilliseconds(ulong ticks) {
            return ticks * 1000.0 / PpuFrequencyHz;
        }
        
        /// <summary>
        /// should be used to convert real time into discrete time
        /// </summary>
        /// <param name="milliseconds">real time to convert to discrete time</param>
        /// <returns></returns>
        public static ulong MillisecondsToTicks(double milliseconds) {
            return (ulong)(milliseconds * PpuFrequencyHz / 1000.0);
        }
    }
    
    public static class Implementation {
        public ref struct ImplHandshake {
            internal ICartridge?           cartridge;
            internal Audio.IEnhancedAudio? audio;
            internal Func<byte>?           memoryInit;
            internal Graphics.Shader?      shader;
        }

        private sealed class NoShader : Graphics.Shader {
            [Pure]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public SDL.Color Recolour(SDL.Color c) => c;
        }
        
        public static void SetupSimple(ref ImplHandshake handshake) {
            handshake.audio = new UnenhancedAudio();
            handshake.memoryInit = () => (byte)Random.Shared.Next();
            handshake.shader = new NoShader();
        }

        public static void SetupIO<T1, T2>() where T1 : IIO, new() where T2 : IIO, new() {
            var port1 = default(T1);
            var port2 = default(T2);
            Link.Subscribe.ControllerToPort(0, ref port1);
            Link.Subscribe.ControllerToPort(1, ref port2);
            if (port1 is IClockDriven icd1) {
                Link.Subscribe.OnTick(icd1);
            }

            if (port2 is IClockDriven icd2) {
                Link.Subscribe.OnTick(icd2);
            }
        }

        public static void SetupDebug<T>(ref EList<string> args
            ) where T : Debugging.IDebugFile {
            var returnArgs = new EList<string>();

            Debugging.IDebugFile? dbgFile = null;
            var                       port = 0;
            
            while (args.MoveNext()) {
                switch (args.Current) {
                    case "--debugPort":
                        if (args.MoveNext()) {
                            if (!int.TryParse(args.Current, out port)) {
                                Console.WriteLine("[IMPL] Debug port is not integer");
                                Emulator.System.Quit = true;
                            }
                            if (Emulator.System.Quit) return;
                                   Console.WriteLine("[IMPL] Setting up Init"); break;
                        }

                        Console.WriteLine("[IMPL] No argument supplied for Debugging Port");
                        Emulator.System.Quit = true;
                        break;

                    case "--debugFile":
                        if (args.MoveNext()) {
                            dbgFile = T.Create(args.Current);
                            Debugger.SourceRoot = Path.GetDirectoryName(
                                Path.GetFullPath(args.Current)) ?? string.Empty;
                            if (Emulator.System.Quit) return;
                            break;
                        }
                    
                        Console.WriteLine("[IMPL] No argument supplied for Debug File");
                        Emulator.System.Quit = true;
                        break;
                
                    default:
                        returnArgs.Add(args.Current);
                        break;
                }
            }

            args = returnArgs;
            
            switch (dbgFile is null, port is 0) {
                case (true, false):
                    Console.WriteLine("[IMPL] No debug file passed, cannot debug");
                    Emulator.System.Quit = true;
                    break;
            
                case (false, true):
                    Console.WriteLine("[IMPL] No debug port passed, cannot debug");
                    Emulator.System.Quit = true;
                    break;
            
                case (false, false):
                    Debugger.BeginDebugging(dbgFile!);
                    break;
            }
        }



        private sealed class UnenhancedAudio : Audio.IEnhancedAudio {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ProcessPulse1Level  (byte   level  ) => level;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ProcessPulse2Level  (byte   level  ) => level;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ProcessTriangleLevel(byte    level ) => level;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ProcessNoiseLevel   (byte    level ) => level;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ProcessPCMLevel     (byte    level ) => level;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float PostProcessSample  (float   sample) => sample;

            public void Configure(EList<string> args) { }
        }
    }

    /// <summary>
    /// Type of component that is signalled per PPU dot.
    /// </summary>
    internal interface IClockDriven {
        void OnTick();
    }

    private static void GetFile(string fp, ref byte[] fileObject, string taskName) {
        if (Program.Threads.System is not null) {
            Console.WriteLine("[EMU] Will not read files while emulating");
            Emulator.System.Quit = true;
            return;
        }
        
        try {
            fileObject   = File.ReadAllBytes(fp);
            #if DEBUG
            Console.WriteLine($"Found file for {taskName} at {fp}");
            #endif
        } catch (Exception e) {
            switch (e) {
                case FileNotFoundException:
                    Console.WriteLine($"[EMU] File for {taskName} not found");
                    break;
                
                default:
                    Console.WriteLine("[EMU] Unknown File IO error");
                    break;
            }
        }
    }
    
    internal static void GetProgramROM(string fp, ref byte[] ProgramROM) => GetFile(fp, ref ProgramROM, "Program ROM");
    internal static void GetCharacterROM(string fp, ref byte[] CharacterROM) => GetFile(fp, ref CharacterROM, "Character ROM");

    public interface IIO {
        public byte     OnRead();
        public void     SetIndex(byte index);
        public void     OnWrite();
    }

    public sealed class HasIRQLine {
        public void SetIRQLine(bool assertion) => Emulator.System.CPU_IRQ = assertion;
        public void DeassertIRQ()              => Emulator.System.CPU_IRQ = false;
        public void AssertIRQ()                => Emulator.System.CPU_IRQ = true;
    }

    internal interface ICartridge {
        /// <summary>
        /// Expects information from CPU Address, is contextualized as a read
        /// </summary>
        public void ProgramRead(ushort address);

        /// <summary>
        /// Expects information from CPU Address, is contextualized as a write
        /// </summary>
        public void CPUWrite();

        /// <summary>
        /// Expects information from PPU Address, is contextualized as a read
        /// </summary>
        public void PPURead();

        /// <summary>
        /// Expects information from PPU Address, is contextualized as a write
        /// </summary>
        public void PPUWrite();

        /// <summary>
        /// This should not trigger internal hardware for on-reads, but should only return the information at the location
        /// CPURead will always be invoked immediately after
        /// </summary>
        /// <returns></returns>
        [Pure] public byte CPUReadByte();

        [Pure] public byte PPUReadByte();

        [Pure] public byte ReadByte(ushort address);

        /// <summary>
        /// This function converts CPU space location into ROM space location
        /// </summary>
        /// <param name="address">address in CPU space to find ROM space location for</param>
        /// <returns>ROM space location of address in CPU space</returns>
        public int GetROMLocation(ushort address);

        public byte[] ProgramROM   { get; set; }
        public byte[] CharacterROM { get; set; }

        public bool PPUA10_11(bool a10, bool a11);

        // ------------------------------------------------------------------
        // PPU H-decoder snoop signals.
        //
        // The PPU fires these as its horizontal decoder advances. The cart
        // sees the current PPU address bus (System.PPU.Registers.Address) at
        // the moment each one fires, which is how mappers like MMC2/MMC3/MMC5
        // implement bank-switching and scanline IRQs.
        //
        // Each one fires once per fetch — the cart can read the bus during
        // the fetch and apply side effects (CHR bank swap, IRQ counter
        // increment, etc.) without the PPU caring.
        // ------------------------------------------------------------------

        /// <summary>/F_NT fires: background nametable fetch.
        /// Bus holds $2000 | (v &amp; 0x0FFF).</summary>
        public void F_NT();

        /// <summary>F_AT fires: background attribute fetch.
        /// Bus holds $23C0 | (v &amp; 0x0C00) | ((v &gt;&gt; 4) &amp; 0x38) | ((v &gt;&gt; 2) &amp; 0x07).</summary>
        public void F_AT();

        /// <summary>F_TA fires: background pattern low plane fetch.</summary>
        public void F_TA();

        /// <summary>F_TB fires: background pattern high plane fetch.
        /// MMC2/MMC4 snoop this to switch CHR banks on specific tile fetches.</summary>
        public void F_TB();

        /// <summary>OBJ_READ phase 1: sprite "garbage" NT fetch.
        /// MMC5 uses this to know which of the 8 sprite slots is being fetched.</summary>
        public void OBJ_NT();

        /// <summary>OBJ_READ phase 2: sprite pattern low plane fetch.</summary>
        public void OBJ_TA();

        /// <summary>OBJ_READ phase 3: sprite pattern high plane fetch.</summary>
        public void OBJ_TB();

        /// <summary>/A12 line rose. MMC3 increments its scanline IRQ counter here,
        /// gated by a ~16 PPU cycle low-time filter.</summary>
        public void A12_Rise();

        /// <summary>/A12 line fell.</summary>
        public void A12_Fall();
    }

    internal interface IFamicomCartridge : ICartridge {
        public float ModifyAPUSignal(float signal);
    }
    
    internal interface INESCartridge : ICartridge {
        public bool EXPO { get; set; }
        public bool EXP1 { get; set; }
        public bool EXP2 { get; set; }
        public bool EXP3 { get; set; }
        public bool EXP4 { get; set; }
        public bool EXP5 { get; set; }
        public bool EXP6 { get; set; }
        public bool EXP7 { get; set; }
        public bool EXP8 { get; set; }
        public bool EXP9 { get; set; }

    }

    public static class Audio {
        public interface IEnhancedAudio {
            byte ProcessPulse1Level(byte level);
            byte ProcessPulse2Level(byte level);
            byte ProcessTriangleLevel(byte level);
            byte ProcessNoiseLevel(byte level);
            byte ProcessPCMLevel(byte level);

            float PostProcessSample(float sample);
            void  Configure(EList<string> args);
        }
    }

    public static class Debugging {
        public interface IDebugFile {
            static abstract IDebugFile Create(string path);
            
            /// <summary>
            /// Lines are a file location with an index within the file, they may point to an address in memory
            /// </summary>
            IDictionary<nint, ILine> Lines { get; }

            /// <summary>
            /// Done by offset to span as offset implicit to length hunk with scope for proper symbol resolution
            /// </summary>
            IReadOnlyList<ISpan> Spans { get; }

            /// <summary>
            /// Evaluates a conditional breakpoint expression in the context of a debug address.
            /// The expression is parsed according to this debug file's assembler culture
            /// (ca65: '$xx' hex, '::' scope separator, 'cpu[i]'/'program[i]'/'character[i]' for memory).
            /// </summary>
            /// <param name="expression">Raw expression string from the IDE breakpoint condition.</param>
            /// <param name="romAddress">
            ///   ROM address where the breakpoint fired; drives lexical scope resolution so that
            ///   unqualified symbol names resolve outward from the innermost enclosing scope.
            /// </param>
            /// <param name="cpuRead">
            ///   Side-effect-free peek into CPU address space (RAM + cartridge).
            ///   Must NOT trigger PPU/APU hardware; for hardware register addresses, returning
            ///   the high byte of the address is acceptable.
            /// </param>
            /// <param name="programRead">
            ///   Side-effect-free read from the cartridge PRG-ROM byte array.
            ///   Index is into the raw ProgramROM array; caller handles bounds safety.
            /// </param>
            /// <param name="characterRead">
            ///   Side-effect-free read from the cartridge CHR-ROM byte array.
            ///   Index is into the raw CharacterROM array; caller handles bounds safety.
            /// </param>
            /// <param name="regRead">
            ///   Returns the current value of a named CPU register or flag, or null if unrecognised.
            ///   Names are architecture-specific (e.g. "A", "X", "Y", "S", "PC", "N", "Z", "C", ...).
            /// </param>
            /// <returns>
            ///   true  — breakpoint should fire (expression is non-zero, or evaluation errored).<br/>
            ///   false — breakpoint should be skipped (expression evaluated to zero).
            /// </returns>
            bool EvaluateCondition(string     expression,
                                   nint       romAddress,
                                   Func<ushort, byte> cpuRead,
                                   Func<int,   byte>  programRead,
                                   Func<int,   byte>  characterRead,
                                   Func<string, int?> regRead);

            /// <summary>
            /// Evaluates an expression and returns its raw integer value, or null if evaluation
            /// fails. Uses the same assembler-culture-aware pre-processor as EvaluateCondition
            /// so register names, symbols, $hex literals and cpu[]/program[]/character[] all work.
            /// </summary>
            int? EvaluateExpression(string     expression,
                                    nint       romAddress,
                                    Func<ushort, byte> cpuRead,
                                    Func<int,   byte>  programRead,
                                    Func<int,   byte>  characterRead,
                                    Func<string, int?> regRead);

            /// <summary>
            /// Validates a condition expression at breakpoint-registration time.
            /// Does a dry-run pre-process + expression evaluation with dummy zero values
            /// so the IDE gets early feedback on syntax problems without side-effects.
            /// </summary>
            /// <param name="expression">Raw IDE condition string.</param>
            /// <param name="error">
            ///   Human-readable error message, or null when the expression is valid.
            /// </param>
            /// <returns>true when the expression is syntactically valid; false otherwise.</returns>
            bool ValidateCondition(string expression, out string? error);
        }
        
        public interface ISpan {
            public int    Start  { get; set; }
            public int    Length { get; set; }
            public IScope Scope  { get; set; }
        }
        
        public interface ISymbol {
            public string name  { get; set; }
            public int    value { get; set; }
        }
        
        public interface IScope {
            public IReadOnlyList<ISymbol> symbols  { get; set; }
        }
        
        internal struct Breakpoint {
            private int    address;
            private string expression;
        }
        
        public interface ILine {
            public string fp   { get; set; }
            public int    line { get; set; }
        }
    }

    public static class Input {
        internal struct ButtonBinding {
            internal bool IsKeyboard;
            internal SDL.Scancode Key;
            internal SDL.GamepadButton Button;
        }

        internal struct AxisBinding {
            internal SDL.GamepadAxis Axis;
        }

        internal static class InputManager {

            static readonly Dictionary<(byte port, int id), ButtonBinding> _buttons = new();
            static readonly Dictionary<(byte port, int id), AxisBinding>   _axes    = new();

            internal static nint[] Gamepads = [];

            // --- Query (called by IO code) ---

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static bool GetButton(byte port, int id) {
                if (!_buttons.TryGetValue((port, id), out var binding)) return false;

                if (binding.IsKeyboard) {
                    var state = SDL.GetKeyboardState(out _);
                    return state[(int)binding.Key];
                }

                if (port >= Gamepads.Length) return false;
                var gp = Gamepads[port];
                return gp is not 0 && SDL.GetGamepadButton(gp, binding.Button);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static short GetAxis(byte port, int id) {
                if (!_axes.TryGetValue((port, id), out var binding)) return 0;
                if (port >= Gamepads.Length) return 0;
                var gp = Gamepads[port];
                return gp is 0 ? (short)0 : SDL.GetGamepadAxis(gp, binding.Axis);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static byte ReduceResolution(short value, int bits) {
                byte result = 0;
                for (var i = 0; i < bits; i++) {
                    result |= (byte)(((value >> (15 - i)) & 1) << (bits - 1 - i));
                }
                return result;
            }

            // --- Single bind ---

            internal static void BindButton(byte port, int id, SDL.GamepadButton button) {
                _buttons[(port, id)] = new ButtonBinding { IsKeyboard = false, Button = button };
            }

            internal static void BindButton(byte port, int id, SDL.Scancode key) {
                _buttons[(port, id)] = new ButtonBinding { IsKeyboard = true, Key = key };
            }

            internal static void BindAxis(byte port, int id, SDL.GamepadAxis axis) {
                _axes[(port, id)] = new AxisBinding { Axis = axis };
            }

            internal static void Unbind(byte port, int id) {
                _buttons.Remove((port, id));
                _axes.Remove((port, id));
            }

            // --- Bulk bind (array index = button/axis ID) ---

            internal static void BindButtons(byte port, SDL.GamepadButton[] buttons) {
                for (var i = 0; i < buttons.Length; i++)
                    BindButton(port, i, buttons[i]);
            }

            internal static void BindButtons(byte port, SDL.Scancode[] keys) {
                for (var i = 0; i < keys.Length; i++)
                    BindButton(port, i, keys[i]);
            }

            internal static void BindAxes(byte port, SDL.GamepadAxis[] axes) {
                for (var i = 0; i < axes.Length; i++)
                    BindAxis(port, i, axes[i]);
            }

            // --- Gamepad handle management (called by Renderer) ---

            internal static void OnGamepadAdded(nint gp) {
                for (var i = 0; i < Gamepads.Length; i++) {
                    if (Gamepads[i] is 0) {
                        Gamepads[i] = gp;
                        Console.WriteLine($"[IO] Gamepad connected to slot {i}: {SDL.GetGamepadName(gp) ?? "Unknown"}");
                        return;
                    }
                }

                var old = Gamepads;
                Gamepads = new nint[old.Length + 1];
                old.CopyTo(Gamepads, 0);
                Gamepads[^1] = gp;
                Console.WriteLine($"[IO] Gamepad connected to slot {Gamepads.Length - 1}: {SDL.GetGamepadName(gp) ?? "Unknown"}");
            }

            internal static void OnGamepadRemoved(uint which) {
                for (var i = 0; i < Gamepads.Length; i++) {
                    if (Gamepads[i] is not 0 && SDL.GetGamepadID(Gamepads[i]) == which) {
                        Console.WriteLine($"[IO] Gamepad disconnected from slot {i}");
                        SDL.CloseGamepad(Gamepads[i]);
                        Gamepads[i] = 0;
                        return;
                    }
                }
            }
        }

    }
}

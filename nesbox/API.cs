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

        public static void SetupIO<T1, T2>() where T1 : IO, new() where T2 : IO, new() {
            var port1 = default(T1);
            var port2 = default(T2);
            Link.Subscribe.ControllerToPort(0, ref port1);
            Link.Subscribe.ControllerToPort(1, ref port2);
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
                                System.Quit = true;
                            }
                            if (System.Quit) return;
                                   Console.WriteLine("[IMPL] Setting up Init"); break;
                        }

                        Console.WriteLine("[IMPL] No argument supplied for Debugging Port");
                        System.Quit = true;
                        break;

                    case "--debugFile":
                        if (args.MoveNext()) {
                            dbgFile = T.Create(args.Current);
                            Debugger.SourceRoot = Path.GetDirectoryName(
                                Path.GetFullPath(args.Current)) ?? string.Empty;
                            if (System.Quit) return;
                            break;
                        }
                    
                        Console.WriteLine("[IMPL] No argument supplied for Debug File");
                        System.Quit = true;
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
                    System.Quit = true;
                    break;
            
                case (false, true):
                    Console.WriteLine("[IMPL] No debug port passed, cannot debug");
                    System.Quit = true;
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
            System.Quit = true;
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

    public abstract class IO : IClockDriven {
        public abstract byte OnRead();
        public abstract void SetIndex(byte index);
        public abstract void OnTick();
    }

    public sealed class HasIRQLine {
        public void SetIRQLine(bool assertion) => System.CPU_IRQ = assertion;
        public void DeassertIRQ()              => System.CPU_IRQ = false;
        public void AssertIRQ()                => System.CPU_IRQ = true;
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
}

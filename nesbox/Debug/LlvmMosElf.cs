using System.Text;
using System.Text.RegularExpressions;

namespace nesbox.Debug;

using SER = StringExpressionEvaluator.StringExpressionEvaluator;

// ══════════════════════════════════════════════════════════════════════════════
//  LlvmMosElf — IDebugFile implementation for llvm-mos ELF objects.
//
//  Parses just enough of ELF32 + DWARF to feed Debugger.cs with:
//    · address → (source file, line) mapping  (from .debug_line)
//    · symbol  → address mapping              (from .symtab)
//
//  Mirrors Ld65Dbg's expression-evaluation pipeline so users get the same
//  REPL semantics (CPU registers, cpu[]/program[]/character[] peeks, hex
//  literals) regardless of which assembler/compiler produced the binary.
//
//  llvm-mos emits ELF32 little-endian for the 6502; we hard-fail on anything
//  else so we don't accidentally try to debug an x86 binary.
//
//  Scope-aware lexical variables (DWARF DIEs from .debug_info) are NOT
//  parsed yet — every symbol is treated as global. Function-scoped locals
//  will need a DIE walker; see the comment block at the bottom for what
//  that future pass would add.
// ══════════════════════════════════════════════════════════════════════════════

public sealed class LlvmMosElf : API.Debugging.IDebugFile {

    // ---------- private interface implementations ----------

    private sealed class SymbolImpl : API.Debugging.ISymbol {
        public string name  { get; set; }
        public int    value { get; set; }
        internal SymbolImpl(string name, int value) { this.name = name; this.value = value; }
    }

    private sealed class ScopeImpl : API.Debugging.IScope {
        public IReadOnlyList<API.Debugging.ISymbol> symbols { get; set; }
        internal ScopeImpl(List<API.Debugging.ISymbol> symbols) { this.symbols = symbols; }
    }

    private sealed class SpanImpl : API.Debugging.ISpan {
        public int                  Start  { get; set; }
        public int                  Length { get; set; }
        public API.Debugging.IScope Scope  { get; set; }
        internal SpanImpl(int start, int length, API.Debugging.IScope scope) {
            Start = start; Length = length; Scope = scope;
        }
    }

    private sealed class LineImpl : API.Debugging.ILine {
        public string fp   { get; set; }
        public int    line { get; set; }
        internal LineImpl(string fp, int line) { this.fp = fp; this.line = line; }
    }

    // ---------- IDebugFile ----------

    public static API.Debugging.IDebugFile Create(string path) => new LlvmMosElf(path);

    IDictionary<nint, API.Debugging.ILine> API.Debugging.IDebugFile.Lines => _lines;
    IReadOnlyList<API.Debugging.ISpan>     API.Debugging.IDebugFile.Spans => _spans;

    private readonly Dictionary<nint, API.Debugging.ILine> _lines        = [];
    private readonly List<API.Debugging.ISpan>             _spans        = [];
    internal readonly SortedSet<nint>                      SequenceEnds  = [];

    // ---------- Symbol tables (mirrors Ld65Dbg shape) ----------

    private readonly Dictionary<string, int> _globalSymbols =
        new(StringComparer.Ordinal);   // C identifiers are case-sensitive

    // ══════════════════════════════════════════════════════════════════════════
    //  Constructor — opens the ELF, walks its sections, and fills _lines /
    //  _globalSymbols.
    // ══════════════════════════════════════════════════════════════════════════

    public LlvmMosElf(string filepath) {
        var elfDir = Path.GetDirectoryName(Path.GetFullPath(filepath)) ?? Environment.CurrentDirectory;
        var bytes  = File.ReadAllBytes(filepath);

        // ── ELF header sanity ───────────────────────────────────────────────
        if (bytes.Length < 52
            || bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
            throw new FormatException($"'{filepath}' is not an ELF file");

        int eiClass = bytes[4];   // 1 = ELF32
        int eiData  = bytes[5];   // 1 = little-endian

        if (eiClass != 1) throw new FormatException("Only ELF32 is supported (llvm-mos emits ELF32)");
        if (eiData  != 1) throw new FormatException("Only little-endian ELF is supported");

        uint shoff      = ReadU32(bytes, 0x20);
        ushort shentsize = ReadU16(bytes, 0x2E);
        ushort shnum    = ReadU16(bytes, 0x30);
        ushort shstrndx = ReadU16(bytes, 0x32);

        if (shoff == 0 || shnum == 0)
            throw new FormatException("ELF has no section table");

        // ── Section headers ─────────────────────────────────────────────────
        var sections = new SectionHeader[shnum];
        for (int i = 0; i < shnum; i++) {
            int o = (int)(shoff + (uint)i * shentsize);
            sections[i] = new SectionHeader(
                ReadU32(bytes, o + 0x00),  // sh_name
                ReadU32(bytes, o + 0x04),  // sh_type
                ReadU32(bytes, o + 0x10),  // sh_offset
                ReadU32(bytes, o + 0x14),  // sh_size
                ReadU32(bytes, o + 0x18),  // sh_link
                ReadU32(bytes, o + 0x24)); // sh_entsize
        }

        // .shstrtab is referenced by index in the ELF header — use it to name sections.
        var shstrtab = sections[shstrndx];
        string SectionName(SectionHeader s) =>
            ReadCString(bytes, (int)(shstrtab.Offset + s.NameOffset));

        // Bucket sections we care about.
        SectionHeader? sLine     = null;
        SectionHeader? sSymtab   = null;
        SectionHeader? sStrtab   = null;
        SectionHeader? sDebugStr = null;
        SectionHeader? sDebugLineStr = null;

        for (int i = 0; i < shnum; i++) {
            var name = SectionName(sections[i]);
            switch (name) {
                case ".debug_line":     sLine         = sections[i]; break;
                case ".symtab":         sSymtab       = sections[i]; break;
                case ".strtab":         sStrtab       = sections[i]; break;
                case ".debug_str":      sDebugStr     = sections[i]; break;
                case ".debug_line_str": sDebugLineStr = sections[i]; break;
            }
        }

        // ── Symbol table → _globalSymbols ───────────────────────────────────
        if (sSymtab is { } st && sStrtab is { } stStr) {
            // Elf32_Sym is 16 bytes: name(4) value(4) size(4) info(1) other(1) shndx(2)
            int symEnt = st.EntSize == 0 ? 16 : (int)st.EntSize;
            int symCount = (int)(st.Size / (uint)symEnt);
            for (int i = 0; i < symCount; i++) {
                int o          = (int)(st.Offset + (uint)i * symEnt);
                uint nameOff   = ReadU32(bytes, o + 0);
                uint value     = ReadU32(bytes, o + 4);
                byte info      = bytes[o + 12];
                ushort shndx   = ReadU16(bytes, o + 14);

                int symType    = info & 0xF;       // STT_NOTYPE=0 OBJECT=1 FUNC=2 SECTION=3 FILE=4
                if (symType is 3 or 4) continue;   // skip section / file pseudo-symbols
                if (shndx == 0) continue;          // SHN_UNDEF
                if (nameOff == 0) continue;

                var name = ReadCString(bytes, (int)(stStr.Offset + nameOff));
                if (name.Length == 0) continue;

                // Last definition wins — matches ELF semantics for global symbols.
                _globalSymbols[name] = (int)value;
            }
            Console.WriteLine($"[ELF] {_globalSymbols.Count} symbols");
        } else {
            Console.WriteLine("[ELF] No .symtab — symbols unavailable for expressions");
        }

        // ── .debug_line → _lines ────────────────────────────────────────────
        if (sLine is { } sl) {
            ParseDebugLine(
                bytes,
                sl,
                sDebugStr,
                sDebugLineStr,
                elfDir);

            // Summarise the line table by file so missing CUs are obvious.
            var byFile = new Dictionary<string, (int count, long lo, long hi)>();
            foreach (var kv in _lines) {
                var fn = Path.GetFileName(kv.Value.fp);
                if (!byFile.TryGetValue(fn, out var prev))
                    prev = (0, long.MaxValue, long.MinValue);
                byFile[fn] = (prev.count + 1,
                    Math.Min(prev.lo, (long)kv.Key),
                    Math.Max(prev.hi, (long)kv.Key));
            }
            Console.WriteLine($"[ELF] {_lines.Count} line records across {byFile.Count} files:");
            foreach (var (fn, (cnt, lo, hi)) in byFile)
                Console.WriteLine($"       {fn}: {cnt} lines  ${lo:X4}–${hi:X4}");
        } else {
            Console.WriteLine("[ELF] No .debug_line — no source-level stepping");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IDebugFile.EvaluateCondition / EvaluateExpression / ValidateCondition
    //
    //  Same pipeline as Ld65Dbg minus the ca65-specific FQN walk: SER, after
    //  $→0x normalisation and the cpu[]/program[]/character[] safe-peek
    //  substitution, handles plain C identifiers natively.
    // ══════════════════════════════════════════════════════════════════════════

    bool API.Debugging.IDebugFile.EvaluateCondition(
        string             expression,
        nint               romAddress,
        Func<ushort, byte> cpuRead,
        Func<int,    byte> programRead,
        Func<int,    byte> characterRead,
        Func<string, int?> regRead)
    {
        try {
            var symbols   = BuildSerSymbols(regRead);
            var processed = Preprocess(expression, symbols, cpuRead, programRead, characterRead);

            if (!SER.TryEvaluate(ref processed, out var result, symbols)) {
                Console.WriteLine(
                    $"[DBG] Condition ERROR — SER could not evaluate, breakpoint FORCED.\n" +
                    $"      Raw:       '{expression}'\n" +
                    $"      Processed: '{processed}'");
                return true;
            }
            return result != 0;
        } catch (Exception ex) {
            Console.WriteLine($"[DBG] Condition ERROR — exception during eval, breakpoint FORCED: {ex.Message}");
            return true;
        }
    }

    int? API.Debugging.IDebugFile.EvaluateExpression(
        string             expression,
        nint               romAddress,
        Func<ushort, byte> cpuRead,
        Func<int,    byte> programRead,
        Func<int,    byte> characterRead,
        Func<string, int?> regRead)
    {
        try {
            var symbols   = BuildSerSymbols(regRead);
            var processed = Preprocess(expression, symbols, cpuRead, programRead, characterRead);
            if (!SER.TryEvaluate(ref processed, out var result, symbols)) return null;
            return result;
        } catch {
            return null;
        }
    }

    bool API.Debugging.IDebugFile.ValidateCondition(string expression, out string? error) {
        try {
            var dummy = new Dictionary<string, SER.SerUnion<int>>(
                _globalSymbols.Count + RegisterNames.Length + 4, StringComparer.Ordinal);

            foreach (var name in RegisterNames) dummy.TryAdd(name, new SER.SerUnion<int>(0));
            foreach (var kv   in _globalSymbols) dummy[kv.Key]   = new SER.SerUnion<int>(0);

            var processed = Preprocess(expression, dummy, _ => 0, _ => 0, _ => 0);

            if (!SER.TryEvaluate(ref processed, out _, dummy)) {
                error = $"Could not evaluate '{processed}' — check for unresolved identifiers. " +
                        $"Supported: == != > < >= <= && || + - * / % & | ^ ! >> << >>>";
                return false;
            }
            error = null;
            return true;
        } catch (Exception ex) {
            error = ex.Message;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SER symbol dict + pre-processor
    // ══════════════════════════════════════════════════════════════════════════

    private Dictionary<string, SER.SerUnion<int>> BuildSerSymbols(Func<string, int?> regRead) {
        var dict = new Dictionary<string, SER.SerUnion<int>>(
            _globalSymbols.Count + RegisterNames.Length, StringComparer.Ordinal);

        // Registers first so user symbols can shadow them.
        foreach (var name in RegisterNames) {
            var v = regRead(name);
            if (v.HasValue) dict.TryAdd(name, new SER.SerUnion<int>(v.Value));
        }

        foreach (var kv in _globalSymbols)
            dict[kv.Key] = new SER.SerUnion<int>(kv.Value);

        return dict;
    }

    private static readonly string[] RegisterNames =
        ["A", "X", "Y", "S", "SP", "PC", "P", "N", "V", "B", "D", "I", "Z", "C"];

    private string Preprocess(
        string                                raw,
        Dictionary<string, SER.SerUnion<int>> symbols,
        Func<ushort, byte>                    cpuRead,
        Func<int,    byte>                    programRead,
        Func<int,    byte>                    characterRead)
    {
        // Step 1 — $HHHH → 0xHHHH so SER understands ca65-style hex too.
        var s = HexLiteralRegex.Replace(raw, m => "0x" + m.Groups[1].Value);

        // Step 2 — iteratively peel cpu[]/program[]/character[] memory peeks.
        const int maxPasses = 10;
        for (int pass = 0; pass < maxPasses; pass++) {
            var after = SubstituteMemArrays(s, symbols, cpuRead, programRead, characterRead);
            if (after == s) break;
            s = after;
        }
        return s;
    }

    private static readonly Regex HexLiteralRegex =
        new(@"(?<![0-9A-Za-z_])\$([0-9A-Fa-f]+)", RegexOptions.Compiled);

    // Same algorithm as Ld65Dbg.SubstituteMemArrays — peel innermost bracket
    // expression that SER can evaluate now; caller iterates until stable.
    private static string SubstituteMemArrays(
        string                                s,
        Dictionary<string, SER.SerUnion<int>> symbols,
        Func<ushort, byte>                    cpuRead,
        Func<int,    byte>                    programRead,
        Func<int,    byte>                    characterRead)
    {
        foreach (var kw in MemArrayKeywords) {
            int searchFrom = 0;
            while (true) {
                int kwPos = s.IndexOf(kw + "[", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (kwPos < 0) break;

                if (kwPos > 0 && (char.IsLetterOrDigit(s[kwPos - 1]) || s[kwPos - 1] == '_')) {
                    searchFrom = kwPos + 1; continue;
                }

                int bracketOpen  = kwPos + kw.Length;
                int contentStart = bracketOpen + 1;

                int depth = 1;
                int i     = contentStart;
                while (i < s.Length && depth > 0) {
                    if      (s[i] == '[') depth++;
                    else if (s[i] == ']') depth--;
                    if (depth > 0) i++;
                }
                if (depth != 0) { searchFrom = kwPos + 1; continue; }

                int bracketClose = i;
                var indexExpr    = s[contentStart..bracketClose];
                var exprCopy     = indexExpr;
                if (!SER.TryEvaluate(ref exprCopy, out var index, symbols)) {
                    searchFrom = kwPos + 1; continue;
                }

                byte value = kw.ToLowerInvariant() switch {
                    "cpu"       => cpuRead((ushort)(index & 0xFFFF)),
                    "program"   => programRead(index),
                    "character" => characterRead(index),
                    _           => 0
                };

                int tokenLen = kw.Length + 1 + (bracketClose - contentStart) + 1;
                s = s[..kwPos] + value.ToString() + s[(kwPos + tokenLen)..];
                return s;
            }
        }
        return s;
    }

    private static readonly string[] MemArrayKeywords = ["cpu", "program", "character"];

    // ══════════════════════════════════════════════════════════════════════════
    //  DWARF .debug_line parser
    //
    //  Handles DWARF v3, v4, v5 (most common for llvm-mos output).  Each
    //  compilation unit emits its own line program; we walk all of them and
    //  drop every emitted row into _lines keyed by ROM (== CPU) address.
    // ══════════════════════════════════════════════════════════════════════════

    private void ParseDebugLine(
        byte[]         bytes,
        SectionHeader  sLine,
        SectionHeader? sDebugStr,
        SectionHeader? sDebugLineStr,
        string         elfDir)
    {
        int p   = (int)sLine.Offset;
        int end = (int)(sLine.Offset + sLine.Size);

        while (p < end) {
            // unit_length: 32-bit, or 0xFFFFFFFF + 64-bit (DWARF64 — rare, unsupported).
            uint unitLen32 = ReadU32(bytes, p); p += 4;
            if (unitLen32 == 0xFFFFFFFFu)
                throw new FormatException(".debug_line is DWARF64 — unsupported");
            int unitEnd = p + (int)unitLen32;

            try {
            ushort version = ReadU16(bytes, p); p += 2;

            byte addressSize = 4;   // default for DWARF<5
            if (version >= 5) {
                addressSize         = bytes[p++];
                _ = /* segment_size */  bytes[p++];
            }

            uint headerLen = ReadU32(bytes, p); p += 4;
            int  prologueEnd = p + (int)headerLen;

            byte minInstLen  = bytes[p++];
            byte maxOpsPerInst = version >= 4 ? bytes[p++] : (byte)1;
            byte defaultIsStmt = bytes[p++];
            sbyte lineBase   = (sbyte)bytes[p++];
            byte  lineRange  = bytes[p++];
            byte  opcodeBase = bytes[p++];

            // standard_opcode_lengths[opcode_base - 1]
            var stdLens = new byte[opcodeBase];
            for (int i = 1; i < opcodeBase; i++) stdLens[i] = bytes[p++];

            // Directories + files: format differs between DWARF v3/v4 and v5.
            string[] dirs;
            (string name, int dirIndex)[] files;

            if (version <= 4) {
                // include_directories: NUL-terminated strings, list ends with empty string.
                var dirList = new List<string> { "" };   // index 0 = compilation dir (unknown here)
                while (p < prologueEnd && bytes[p] != 0) {
                    var d = ReadCString(bytes, p);
                    p += d.Length + 1;
                    dirList.Add(d);
                }
                p++; // skip terminating NUL

                // file_names: NUL-terminated name + ULEB dir + ULEB mtime + ULEB size, ends with NUL.
                var fileList = new List<(string, int)> { ("", 0) };  // index 0 unused in v3/v4
                while (p < prologueEnd && bytes[p] != 0) {
                    var name = ReadCString(bytes, p);
                    p += name.Length + 1;
                    int dirIdx = (int)ReadULeb(bytes, ref p);
                    _ = ReadULeb(bytes, ref p);   // mtime
                    _ = ReadULeb(bytes, ref p);   // size
                    fileList.Add((name, dirIdx));
                }
                p++; // terminating NUL

                dirs  = dirList.ToArray();
                files = fileList.ToArray();
            } else {
                // DWARF 5: directory_entry_format + directories, then file_name_entry_format + file_names.
                dirs  = ReadDwarf5Entries(bytes, ref p, sDebugStr, sDebugLineStr, isFiles: false, out var _);
                files = ReadDwarf5Files (bytes, ref p, sDebugStr, sDebugLineStr, dirs);
            }

            // Move past prologue to the line program.
            p = prologueEnd;

            // ── State machine ──────────────────────────────────────────────
            // DWARF v5: file indices are 0-based, initial file is 0.
            // DWARF v3/v4: file indices are 1-based, initial file is 1;
            //              index 0 is a dummy ("", 0) we added above.
            long address    = 0;
            int  opIndex    = 0;
            int  fileIdx    = version >= 5 ? 0 : 1;
            int  lineNo     = 1;
            bool isStmt     = defaultIsStmt != 0;
            bool endSeq     = false;

            void EmitRow() {
                if (lineNo < 0) return;
                if (fileIdx < 0 || fileIdx >= files.Length) return;
                var (fname, dirIdx) = files[fileIdx];
                if (string.IsNullOrEmpty(fname)) return;

                string dir = dirIdx >= 0 && dirIdx < dirs.Length ? dirs[dirIdx] : "";
                string full = ResolveSourcePath(dir, fname, elfDir);

                _lines.TryAdd((nint)(int)address, new LineImpl(full, lineNo));
            }

            int initFileIdx = fileIdx;

            while (p < unitEnd) {
                byte op = bytes[p++];

                if (op == 0) {
                    // Extended opcode: ULEB length, sub-opcode.
                    uint extLen = ReadULeb(bytes, ref p);
                    if (extLen == 0) continue;
                    int  subEnd = p + (int)extLen;
                    byte sub    = bytes[p++];

                    switch (sub) {
                        case 1: // DW_LNE_end_sequence
                            SequenceEnds.Add((nint)(int)address);
                            address = 0; opIndex = 0; fileIdx = initFileIdx; lineNo = 1;
                            isStmt = defaultIsStmt != 0; endSeq = false;
                            break;

                        case 2: // DW_LNE_set_address
                            address = addressSize == 8 ? (long)ReadU64(bytes, p) : ReadU32(bytes, p);
                            opIndex = 0;
                            break;

                        case 3: // DW_LNE_define_file (legacy)
                            // Skip — modern compilers don't emit this.
                            break;

                        case 4: // DW_LNE_set_discriminator
                            _ = ReadULeb(bytes, ref p);
                            break;
                    }
                    p = subEnd;
                } else if (op < opcodeBase) {
                    // Standard opcode.
                    switch (op) {
                        case 1: // DW_LNS_copy
                            EmitRow();
                            break;

                        case 2: { // DW_LNS_advance_pc
                            uint adv = ReadULeb(bytes, ref p);
                            address += minInstLen * (long)((opIndex + adv) / maxOpsPerInst);
                            opIndex  = (int)((opIndex + adv) % maxOpsPerInst);
                            break;
                        }

                        case 3: // DW_LNS_advance_line
                            lineNo += (int)ReadSLeb(bytes, ref p);
                            break;

                        case 4: // DW_LNS_set_file
                            fileIdx = (int)ReadULeb(bytes, ref p);
                            break;

                        case 5: // DW_LNS_set_column
                            _ = ReadULeb(bytes, ref p);
                            break;

                        case 6: // DW_LNS_negate_stmt
                            isStmt = !isStmt;
                            break;

                        case 7: // DW_LNS_set_basic_block — no state needed here
                            break;

                        case 8: { // DW_LNS_const_add_pc — same advance as special opcode 255.
                            int  adjusted = 255 - opcodeBase;
                            int  opAdv    = adjusted / lineRange;
                            address += minInstLen * ((opIndex + opAdv) / maxOpsPerInst);
                            opIndex  = (opIndex + opAdv) % maxOpsPerInst;
                            break;
                        }

                        case 9: // DW_LNS_fixed_advance_pc
                            address += ReadU16(bytes, p); p += 2;
                            opIndex  = 0;
                            break;

                        case 10: // DW_LNS_set_prologue_end
                        case 11: // DW_LNS_set_epilogue_begin
                            break;

                        case 12: // DW_LNS_set_isa
                            _ = ReadULeb(bytes, ref p);
                            break;

                        default:
                            // Unknown standard opcode — skip its operands using stdLens.
                            for (int j = 0; j < stdLens[op]; j++) _ = ReadULeb(bytes, ref p);
                            break;
                    }
                } else {
                    // Special opcode.
                    int adjusted    = op - opcodeBase;
                    int opAdvance   = adjusted / lineRange;
                    address += minInstLen * ((opIndex + opAdvance) / maxOpsPerInst);
                    opIndex  = (opIndex + opAdvance) % maxOpsPerInst;
                    lineNo  += lineBase + (adjusted % lineRange);
                    EmitRow();
                }
            }

            } catch (Exception ex) {
                Console.WriteLine($"[ELF] .debug_line CU parse error (skipping): {ex.Message}");
            }

            p = unitEnd;
        }
    }

    // ── DWARF 5 file/directory entry table readers ───────────────────────────

    private static string[] ReadDwarf5Entries(
        byte[] bytes, ref int p,
        SectionHeader? sDebugStr, SectionHeader? sDebugLineStr,
        bool isFiles, out int[] dirIndexes)
    {
        byte formatCount = bytes[p++];
        var formats = new (uint contentCode, uint formCode)[formatCount];
        for (int i = 0; i < formatCount; i++) {
            formats[i].contentCode = ReadULeb(bytes, ref p);
            formats[i].formCode    = ReadULeb(bytes, ref p);
        }

        uint count = ReadULeb(bytes, ref p);
        var  paths = new string[count];
        dirIndexes = new int[count];

        for (uint i = 0; i < count; i++) {
            string entryPath = "";
            int    entryDir  = 0;
            foreach (var (contentCode, formCode) in formats) {
                var val = ReadDwarf5FormValue(bytes, ref p, formCode, sDebugStr, sDebugLineStr);
                switch (contentCode) {
                    case 1: entryPath = val.str ?? ""; break;   // DW_LNCT_path
                    case 2: entryDir  = (int)val.num; break;     // DW_LNCT_directory_index
                    // 3=timestamp 4=size 5=MD5 — ignored
                }
            }
            paths[i]      = entryPath;
            dirIndexes[i] = entryDir;
        }
        return paths;
    }

    private static (string name, int dirIndex)[] ReadDwarf5Files(
        byte[] bytes, ref int p,
        SectionHeader? sDebugStr, SectionHeader? sDebugLineStr,
        string[] dirs)
    {
        var paths = ReadDwarf5Entries(bytes, ref p, sDebugStr, sDebugLineStr, isFiles: true, out var dirIndexes);
        var result = new (string, int)[paths.Length];
        for (int i = 0; i < paths.Length; i++)
            result[i] = (paths[i], dirIndexes[i]);
        return result;
    }

    private static (string? str, ulong num) ReadDwarf5FormValue(
        byte[] bytes, ref int p, uint formCode,
        SectionHeader? sDebugStr, SectionHeader? sDebugLineStr)
    {
        // Only the forms the line table actually uses for path/dir entries.
        switch (formCode) {
            case 0x08: { // DW_FORM_string — inline NUL-terminated
                var s = ReadCString(bytes, p);
                p += s.Length + 1;
                return (s, 0);
            }
            case 0x1f: { // DW_FORM_line_strp — offset into .debug_line_str
                uint off = ReadU32(bytes, p); p += 4;
                if (sDebugLineStr is { } ls)
                    return (ReadCString(bytes, (int)(ls.Offset + off)), off);
                return ("", 0);
            }
            case 0x0e: { // DW_FORM_strp — offset into .debug_str
                uint off = ReadU32(bytes, p); p += 4;
                if (sDebugStr is { } ds)
                    return (ReadCString(bytes, (int)(ds.Offset + off)), off);
                return ("", 0);
            }
            case 0x21: { // DW_FORM_strx — ULEB index into .debug_str_offsets (not supported here)
                _ = ReadULeb(bytes, ref p);
                return ("", 0);
            }
            case 0x26:   // DW_FORM_strx1
                p += 1; return ("", 0);
            case 0x27:   // DW_FORM_strx2
                p += 2; return ("", 0);
            case 0x28:   // DW_FORM_strx3
                p += 3; return ("", 0);
            case 0x29:   // DW_FORM_strx4
                p += 4; return ("", 0);

            case 0x0b:   // DW_FORM_data1
                return (null, bytes[p++]);
            case 0x05:   // DW_FORM_data2
                { ushort v = ReadU16(bytes, p); p += 2; return (null, v); }
            case 0x06:   // DW_FORM_data4
                { uint   v = ReadU32(bytes, p); p += 4; return (null, v); }
            case 0x07:   // DW_FORM_data8
                { ulong  v = ReadU64(bytes, p); p += 8; return (null, v); }
            case 0x0f:   // DW_FORM_udata
                return (null, ReadULeb(bytes, ref p));

            case 0x1e: { // DW_FORM_data16 — skip
                p += 16; return (null, 0);
            }

            default:
                throw new FormatException($".debug_line DWARF5 form 0x{formCode:X} not supported");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Source-path resolution
    //
    //  DWARF stores paths relative to the compilation directory.  We don't
    //  have that here unless DWARF5 emitted dirs[0] explicitly.  Walk up from
    //  the directory of the .elf file looking for a real file, like Ld65Dbg.
    // ══════════════════════════════════════════════════════════════════════════

    private static string ResolveSourcePath(string dir, string file, string elfDir) {
        string raw = string.IsNullOrEmpty(dir)
            ? file
            : Path.Combine(dir, file);
        raw = raw.Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(raw) && File.Exists(raw)) return Path.GetFullPath(raw);

        // Build candidate relative paths from most-specific to least-specific.
        // Handles cross-compiled ELFs where DWARF contains absolute paths from
        // the build machine (e.g. /home/user/project/src/main.c on a Windows host).
        var segments = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var probe = elfDir;
        while (probe is not null) {
            for (int skip = 0; skip < segments.Length; skip++) {
                var rel       = string.Join(Path.DirectorySeparatorChar, segments[skip..]);
                var candidate = Path.GetFullPath(Path.Combine(probe, rel));
                if (File.Exists(candidate)) return candidate;
            }
            probe = Path.GetDirectoryName(probe);
        }

        return Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(elfDir, raw));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ELF / DWARF byte readers
    // ══════════════════════════════════════════════════════════════════════════

    private readonly record struct SectionHeader(
        uint NameOffset, uint Type, uint Offset, uint Size, uint Link, uint EntSize);

    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint   ReadU32(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ulong  ReadU64(byte[] b, int o) =>
        ReadU32(b, o) | ((ulong)ReadU32(b, o + 4) << 32);

    private static string ReadCString(byte[] b, int o) {
        int e = o;
        while (e < b.Length && b[e] != 0) e++;
        return Encoding.UTF8.GetString(b, o, e - o);
    }

    private static uint ReadULeb(byte[] b, ref int p) {
        uint result = 0; int shift = 0;
        while (true) {
            byte by = b[p++];
            result |= (uint)(by & 0x7F) << shift;
            if ((by & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) throw new FormatException("ULEB128 too large");
        }
    }

    private static int ReadSLeb(byte[] b, ref int p) {
        int result = 0; int shift = 0; byte by;
        do {
            by = b[p++];
            result |= (by & 0x7F) << shift;
            shift += 7;
            if (shift >= 35) throw new FormatException("SLEB128 too large");
        } while ((by & 0x80) != 0);
        // Sign-extend.
        if (shift < 32 && (by & 0x40) != 0) result |= -(1 << shift);
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Future work
    //
    //  Function-scoped locals: walk .debug_info DIEs (using .debug_abbrev to
    //  decode them) and emit a SpanImpl per DW_TAG_subprogram with its
    //  contained DW_TAG_variable / DW_TAG_formal_parameter DIEs in the scope.
    //  Then BuildSerSymbols can do the same scope-chain walk Ld65Dbg does.
    //
    //  C++ Itanium name demangling: would make _globalSymbols readable for
    //  C++ source, but for plain C output (the common llvm-mos case) names
    //  are already legible.
    // ══════════════════════════════════════════════════════════════════════════
}

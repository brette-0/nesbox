using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace nesbox.Debug;

using SER = StringExpressionEvaluator.StringExpressionEvaluator;

public sealed class Ld65Dbg<T> : API.Debugging.IDebugFile<T> where T : IBinaryInteger<T> {

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
        public int                  Start   { get; set; }
        public int                  Length  { get; set; }
        public API.Debugging.IScope Scope   { get; set; }
        /// <summary>-1 when no scope covers this span.</summary>
        internal int                ScopeId { get; }
        internal SpanImpl(int start, int length, API.Debugging.IScope scope, int scopeId) {
            Start = start; Length = length; Scope = scope; ScopeId = scopeId;
        }
    }

    private sealed class LineImpl : API.Debugging.ILine {
        public string fp   { get; set; }
        public int    line { get; set; }
        internal LineImpl(string fp, int line) { this.fp = fp; this.line = line; }
    }

    // ---------- IDebugFile<T> ----------

    IDictionary<T, API.Debugging.ILine> API.Debugging.IDebugFile<T>.Lines => _lines;
    IReadOnlyList<API.Debugging.ISpan>  API.Debugging.IDebugFile<T>.Spans => _spans;

    private readonly Dictionary<T, API.Debugging.ILine> _lines = [];
    private readonly List<API.Debugging.ISpan>          _spans = [];

    // ---------- enums ----------
    public enum AddrSize  { Zeropage, Absolute, Long }
    public enum SegPerm   { Ro, Rw }
    public enum ScopeType { Global, File, Scope, Struct, Enum }
    public enum SymKind   { Equ, Imp, Lab }

    // ---------- raw parsed records ----------
    public readonly record struct VersionRec(int Major, int Minor);
    public readonly record struct InfoRec(int Csym, int File, int Lib, int Line, int Mod, int Scope, int Seg, int Span, int Sym, int Type);
    public readonly record struct FileRec(int Id, string Name, long Size, long MTime, int Mod);
    public readonly record struct ModRec(int Id, string Name, int File, int? Lib);
    public readonly record struct LibRec(int Id, string Name);
    public readonly record struct SegRec(int Id, string Name, long Start, long Size, AddrSize AddrSize, SegPerm Type, int? Bank, string? OutputName, long? OutputOffs);
    public readonly record struct SpanRec(int Id, int Seg, long Start, long Size, int? Type);
    public readonly record struct ScopeRec(int Id, string Name, int Mod, long? Size, ScopeType? Type, int? Parent, int? Sym, int[]? Spans);
    public readonly record struct LineRec(int Id, int File, long Line, int? Type, int? Count, int[]? Spans);
    public readonly record struct SymRec(int Id, string Name, AddrSize AddrSize, SymKind Type, long? Val, long? Size, int? Seg, int? Scope, int? Parent, int[]? Defs, int[]? Refs);
    public readonly record struct TypeRec(int Id, string Val);

    public VersionRec Version   { get; private set; }
    public InfoRec    Info      { get; private set; }
    public FileRec[]  Files     { get; private set; } = [];
    public ModRec[]   Mods      { get; private set; } = [];
    public LibRec[]   Libs      { get; private set; } = [];
    public SegRec[]   Segs      { get; private set; } = [];
    public SpanRec[]  RawSpans  { get; private set; } = [];
    public ScopeRec[] Scopes    { get; private set; } = [];
    public LineRec[]  RawLines  { get; private set; } = [];
    public SymRec[]   Syms      { get; private set; } = [];
    public TypeRec[]  Types     { get; private set; } = [];

    // ---------- symbol-lookup tables (for EvaluateCondition) ----------

    // Symbols grouped by scope ID for scope-chain walk.
    private readonly Dictionary<int, List<SymbolImpl>> _symsByScope = [];

    // All fully-qualified symbol lookup: "scope::sub::name" or "::name" (global).
    // Used by the ca65 FQN pre-processor step.
    private readonly Dictionary<string, int> _fqnSymbols =
        new(StringComparer.OrdinalIgnoreCase);

    // Short-name symbols in the global (anonymous) scope — last-resort fallback.
    private readonly Dictionary<string, int> _globalSymbols =
        new(StringComparer.OrdinalIgnoreCase);

    // scope ID → parent scope ID
    private readonly Dictionary<int, int>    _scopeParent = [];

    // scope ID → FQN path string, e.g. "" / "foo" / "foo::bar"
    private readonly Dictionary<int, string> _scopeFqn    = [];

    // ---------- constructor ----------

    public Ld65Dbg(string filepath) {
        var files  = new List<FileRec>();
        var mods   = new List<ModRec>();
        var libs   = new List<LibRec>();
        var segs   = new List<SegRec>();
        var spans  = new List<SpanRec>();
        var scopes = new List<ScopeRec>();
        var lines  = new List<LineRec>();
        var syms   = new List<SymRec>();
        var types  = new List<TypeRec>();

        VersionRec? version = null;
        InfoRec?    info    = null;

        int lineNo = 0;
        foreach (var raw in File.ReadLines(filepath)) {
            lineNo++;
            var line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#') continue;

            int    tab  = line.IndexOf('\t');
            string rec  = (tab >= 0 ? line[..tab] : line).Trim().ToLowerInvariant();
            string rest = tab >= 0 ? line[(tab + 1)..] : "";
            var    kv   = ParseKv(rest, lineNo);

            switch (rec) {
                case "version":
                    version = new VersionRec(ReqInt(kv, "major", lineNo), ReqInt(kv, "minor", lineNo));
                    break;

                case "info":
                    info = new InfoRec(
                        ReqInt(kv, "csym",  lineNo), ReqInt(kv, "file",  lineNo),
                        ReqInt(kv, "lib",   lineNo), ReqInt(kv, "line",  lineNo),
                        ReqInt(kv, "mod",   lineNo), ReqInt(kv, "scope", lineNo),
                        ReqInt(kv, "seg",   lineNo), ReqInt(kv, "span",  lineNo),
                        ReqInt(kv, "sym",   lineNo), ReqInt(kv, "type",  lineNo));
                    break;

                case "file":
                    files.Add(new FileRec(
                        ReqInt(kv,  "id",    lineNo), ReqStr(kv,  "name",  lineNo),
                        ReqLong(kv, "size",  lineNo), ReqLong(kv, "mtime", lineNo),
                        ReqInt(kv,  "mod",   lineNo)));
                    break;

                case "mod":
                    mods.Add(new ModRec(
                        ReqInt(kv, "id",   lineNo), ReqStr(kv, "name", lineNo),
                        ReqInt(kv, "file", lineNo), OptInt(kv, "lib",  lineNo)));
                    break;

                case "lib":
                    libs.Add(new LibRec(ReqInt(kv, "id", lineNo), ReqStr(kv, "name", lineNo)));
                    break;

                case "seg":
                    segs.Add(new SegRec(
                        ReqInt(kv,  "id",      lineNo), ReqStr(kv,  "name",      lineNo),
                        ReqLong(kv, "start",   lineNo), ReqLong(kv, "size",      lineNo),
                        ReqEnum<AddrSize>(kv, "addrsize", lineNo),
                        ReqEnum<SegPerm>(kv,  "type",     lineNo),
                        OptInt(kv,  "bank",    lineNo), OptStr(kv,  "outputname"),
                        OptLong(kv, "outputoffs", lineNo)));
                    if ((OptStr(kv, "outputname") is null) != (OptLong(kv, "outputoffs", lineNo) is null))
                        throw new FormatException($"Line {lineNo}: outputname and outputoffs must appear together");
                    break;

                case "span":
                    spans.Add(new SpanRec(
                        ReqInt(kv,  "id",    lineNo), ReqInt(kv,  "seg",  lineNo),
                        ReqLong(kv, "start", lineNo), ReqLong(kv, "size", lineNo),
                        OptInt(kv,  "type",  lineNo)));
                    break;

                case "scope":
                    scopes.Add(new ScopeRec(
                        ReqInt(kv, "id",     lineNo), ReqStr(kv,  "name", lineNo),
                        ReqInt(kv, "mod",    lineNo), OptLong(kv, "size", lineNo),
                        OptEnum<ScopeType>(kv, "type", lineNo),
                        OptInt(kv, "parent", lineNo), OptInt(kv, "sym",   lineNo),
                        OptIntListPlus(kv, "span", lineNo)));
                    break;

                case "line":
                    lines.Add(new LineRec(
                        ReqInt(kv,  "id",    lineNo), ReqInt(kv,  "file",  lineNo),
                        ReqLong(kv, "line",  lineNo), OptInt(kv,  "type",  lineNo),
                        OptInt(kv,  "count", lineNo), OptIntListPlus(kv, "span", lineNo)));
                    break;

                case "sym": {
                    int? symScope  = OptInt(kv, "scope",  lineNo);
                    int? symParent = OptInt(kv, "parent", lineNo);
                    if ((symScope is null) == (symParent is null))
                        throw new FormatException($"Line {lineNo}: sym must have exactly one of scope= or parent=");
                    syms.Add(new SymRec(
                        ReqInt(kv, "id",   lineNo), ReqStr(kv, "name", lineNo),
                        ReqEnum<AddrSize>(kv, "addrsize", lineNo),
                        ReqEnum<SymKind>(kv,  "type",     lineNo),
                        OptLong(kv, "val",  lineNo), OptLong(kv, "size",   lineNo),
                        OptInt(kv,  "seg",  lineNo), symScope, symParent,
                        OptIntListPlus(kv, "def", lineNo),
                        OptIntListPlus(kv, "ref", lineNo)));
                    break;
                }

                case "type":
                    types.Add(new TypeRec(ReqInt(kv, "id", lineNo), ReqStr(kv, "val", lineNo)));
                    break;

                default:
                    throw new FormatException($"Line {lineNo}: unknown record '{rec}'");
            }
        }

        Version  = version ?? throw new FormatException("Missing version record");
        Info     = info    ?? throw new FormatException("Missing info record");
        Files    = files.ToArray();
        Mods     = mods.ToArray();
        Libs     = libs.ToArray();
        Segs     = segs.ToArray();
        RawSpans = spans.ToArray();
        Scopes   = scopes.ToArray();
        RawLines = lines.ToArray();
        Syms     = syms.ToArray();
        Types    = types.ToArray();

        // ---------- Build IDebugFile interface collections ----------

        var segById  = Segs.ToDictionary(s => s.Id);
        var spanById = RawSpans.ToDictionary(s => s.Id);
        var fileById = Files.ToDictionary(f => f.Id);

        // ── Scope parent chain ──────────────────────────────────────────────
        var scopeById = Scopes.ToDictionary(s => s.Id);
        foreach (var sc in Scopes)
            if (sc.Parent.HasValue) _scopeParent[sc.Id] = sc.Parent.Value;

        // ── Fully-qualified scope paths ─────────────────────────────────────
        foreach (var sc in Scopes) {
            var parts = new List<string>();
            var cur   = sc;
            while (true) {
                if (!string.IsNullOrEmpty(cur.Name)) parts.Insert(0, cur.Name);
                if (!cur.Parent.HasValue) break;
                if (!scopeById.TryGetValue(cur.Parent.Value, out var par)) break;
                cur = par;
            }
            _scopeFqn[sc.Id] = string.Join("::", parts);
        }

        // ── Symbol tables ───────────────────────────────────────────────────
        foreach (var sym in Syms) {
            if (sym.Val is null) continue;
            var val     = (int)sym.Val.Value;
            var scopeId = sym.Scope ?? sym.Parent ?? 0;

            if (!_symsByScope.TryGetValue(scopeId, out var list))
                _symsByScope[scopeId] = list = [];
            list.Add(new SymbolImpl(sym.Name, val));

            var fqn    = _scopeFqn.TryGetValue(scopeId, out var path) ? path : "";
            var fqnKey = string.IsNullOrEmpty(fqn) ? $"::{sym.Name}" : $"{fqn}::{sym.Name}";
            _fqnSymbols.TryAdd(fqnKey, val);

            if (string.IsNullOrEmpty(fqn))
                _globalSymbols.TryAdd(sym.Name, val);
        }

        // ── IScope / ISpan objects ──────────────────────────────────────────
        var scopeImpls = new Dictionary<int, ScopeImpl>();
        foreach (var sc in Scopes) {
            _symsByScope.TryGetValue(sc.Id, out var symList);
            scopeImpls[sc.Id] = new ScopeImpl(
                symList?.Cast<API.Debugging.ISymbol>().ToList() ?? []);
        }

        var spanToScope = new Dictionary<int, int>();
        foreach (var sc in Scopes) {
            if (sc.Spans is null) continue;
            foreach (var spanId in sc.Spans)
                spanToScope.TryAdd(spanId, sc.Id);
        }

        var emptyScope = new ScopeImpl([]);

        foreach (var sp in RawSpans) {
            int rawScopeId = spanToScope.TryGetValue(sp.Id, out var sid) ? sid : -1;
            var scope      = rawScopeId >= 0 && scopeImpls.TryGetValue(rawScopeId, out var si)
                             ? (API.Debugging.IScope)si : emptyScope;
            var start      = (int)(segById.TryGetValue(sp.Seg, out var seg) ? seg.Start + sp.Start : sp.Start);
            _spans.Add(new SpanImpl(start, (int)sp.Size, scope, rawScopeId));
        }

        _spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        // ── Line map ────────────────────────────────────────────────────────
        foreach (var lr in RawLines) {
            if (lr.Spans is null || lr.Spans.Length == 0)      continue;
            if (!spanById.TryGetValue(lr.Spans[0], out var sp)) continue;
            if (!segById.TryGetValue(sp.Seg,        out var seg)) continue;
            if (!fileById.TryGetValue(lr.File,       out var f))  continue;

            var romAddr = (T)(object)(int)(seg.Start + sp.Start);
            _lines.TryAdd(romAddr, new LineImpl(f.Name, (int)lr.Line));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IDebugFile<T>.EvaluateCondition
    //
    //  Pipeline (all ca65-specific syntax is normalised before SER sees the expr):
    //
    //   1. $hex → 0xhex            (ca65 hex literals → SER-friendly 0x form)
    //   2. FQN ::  → decimal       (ca65 scope qualifiers resolved from _fqnSymbols)
    //   3. cpu[] / program[] / character[] → decimal byte literal  (safe peeks)
    //      Steps 2-3 are iterated until stable (handles nested references).
    //   4. SER.TryEvaluate with a scalar symbol dict built from:
    //        – lexically-scoped ca65 symbols (innermost scope wins via TryAdd)
    //        – global/anonymous-scope symbols (fallback)
    //        – CPU registers and flags (via regRead, lowest priority)
    //
    //  Returns true (fire) on error so breakpoints fail safe.
    // ══════════════════════════════════════════════════════════════════════════

    bool API.Debugging.IDebugFile<T>.EvaluateCondition(
        string             expression,
        T                  romAddress,
        Func<ushort, byte> cpuRead,
        Func<int,    byte> programRead,
        Func<int,    byte> characterRead,
        Func<string, int?> regRead)
    {
        try {
            var romAddr = (int)(object)romAddress;

            // Build the scalar SER symbol dict.
            var symbols = BuildSerSymbols(romAddr, regRead);

            // Pre-process ca65-specific syntax so SER gets a plain arithmetic expression.
            var processed = Preprocess(expression, symbols, cpuRead, programRead, characterRead);

            bool ok = SER.TryEvaluate(ref processed, out var result, symbols);

            if (!ok) {
                // SER could not evaluate — fire the breakpoint so the user sees something
                // is wrong rather than silently skipping.
                Console.WriteLine(
                    $"[DBG] Condition ERROR — SER could not evaluate, breakpoint FORCED.\n" +
                    $"      Raw:       '{expression}'\n" +
                    $"      Processed: '{processed}'");
                return true;   // force break so the user knows their condition is broken
            }

            return result != 0;
        } catch (Exception ex) {
            // Exception during evaluation — force break and scream.
            Console.WriteLine($"[DBG] Condition ERROR — exception during eval, breakpoint FORCED: {ex.Message}");
            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IDebugFile<T>.EvaluateExpression
    //
    //  Same pipeline as EvaluateCondition but returns the raw integer result
    //  rather than a bool.  Returns null when evaluation fails.
    // ══════════════════════════════════════════════════════════════════════════

    int? API.Debugging.IDebugFile<T>.EvaluateExpression(
        string             expression,
        T                  romAddress,
        Func<ushort, byte> cpuRead,
        Func<int,    byte> programRead,
        Func<int,    byte> characterRead,
        Func<string, int?> regRead)
    {
        try {
            var romAddr = (int)(object)romAddress;
            var symbols = BuildSerSymbols(romAddr, regRead);
            var processed = Preprocess(expression, symbols, cpuRead, programRead, characterRead);
            if (!SER.TryEvaluate(ref processed, out var result, symbols)) return null;
            return result;
        } catch {
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Symbol dict construction
    // ══════════════════════════════════════════════════════════════════════════

    private Dictionary<string, SER.SerUnion<int>> BuildSerSymbols(
        int romAddress, Func<string, int?> regRead)
    {
        var dict = new Dictionary<string, SER.SerUnion<int>>(
            _globalSymbols.Count + 20, StringComparer.OrdinalIgnoreCase);

        // 1. CPU registers and flags — lowest priority, added first so symbols can shadow them.
        foreach (var name in RegisterNames) {
            var v = regRead(name);
            if (v.HasValue) dict.TryAdd(name, new SER.SerUnion<int>(v.Value));
        }

        // 2. Global (anonymous-scope) symbols — middle priority.
        foreach (var kv in _globalSymbols)
            dict[kv.Key] = new SER.SerUnion<int>(kv.Value);

        // 3. Lexical scope chain — highest priority (innermost scope overrides outer).
        int? scopeId = FindScopeForAddress(romAddress);
        while (scopeId.HasValue && scopeId.Value >= 0) {
            if (_symsByScope.TryGetValue(scopeId.Value, out var list))
                foreach (var sym in list)
                    dict[sym.name] = new SER.SerUnion<int>(sym.value);  // overwrite; inner wins

            scopeId = _scopeParent.TryGetValue(scopeId.Value, out var pid) ? pid : null;
        }

        return dict;
    }

    // Canonical register/flag names the regRead delegate understands.
    private static readonly string[] RegisterNames =
        ["A", "X", "Y", "S", "SP", "PC", "P", "N", "V", "B", "D", "I", "Z", "C"];

    // ══════════════════════════════════════════════════════════════════════════
    //  IDebugFile<T>.ValidateCondition
    //
    //  Dry-run: pre-process the expression with a dummy symbol dict (all values
    //  zero) and call SER.TryEvaluate.  Returns false + error message when:
    //    · the pre-processor cannot substitute an FQN (unknown symbol)
    //    · SER returns false (unsupported operator, unresolved identifier)
    //    · an exception is thrown during processing
    // ══════════════════════════════════════════════════════════════════════════

    bool API.Debugging.IDebugFile<T>.ValidateCondition(string expression, out string? error) {
        try {
            // Build a dummy symbol dict: every known name maps to zero.
            var dummy = new Dictionary<string, SER.SerUnion<int>>(
                _globalSymbols.Count + RegisterNames.Length + 4,
                StringComparer.OrdinalIgnoreCase);

            foreach (var name in RegisterNames)
                dummy.TryAdd(name, new SER.SerUnion<int>(0));
            foreach (var kv in _globalSymbols)
                dummy[kv.Key] = new SER.SerUnion<int>(0);
            foreach (var list in _symsByScope.Values)
                foreach (var sym in list)
                    dummy.TryAdd(sym.name, new SER.SerUnion<int>(0));

            var processed = Preprocess(expression, dummy,
                _ => 0,   // cpuRead — always 0
                _ => 0,   // programRead — always 0
                _ => 0);  // characterRead — always 0

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
    //  Pre-processing pipeline
    // ══════════════════════════════════════════════════════════════════════════

    private string Preprocess(
        string                               raw,
        Dictionary<string, SER.SerUnion<int>> symbols,
        Func<ushort, byte>                   cpuRead,
        Func<int,    byte>                   programRead,
        Func<int,    byte>                   characterRead)
    {
        // Step 1 — ca65 hex literals:  $1A2B  →  0x1A2B
        //   SER understands 0x-prefixed hex natively; $ is a ca65-ism.
        var s = HexLiteralRegex.Replace(raw, m => "0x" + m.Groups[1].Value);

        // Steps 2 & 3 — iterate to handle nested / interleaved references.
        //   e.g. cpu[foo::index]  needs FQN resolved before the array peel.
        const int maxPasses = 10;
        for (int pass = 0; pass < maxPasses; pass++) {
            var after2 = SubstituteFqn(s);
            var after3 = SubstituteMemArrays(after2, symbols, cpuRead, programRead, characterRead);
            if (after3 == s) break;     // stable — nothing more to substitute
            s = after3;
        }

        return s;
    }

    // Matches ca65-style $HHHH hex literals that are not part of a larger identifier.
    // Group 1 captures the hex digits.
    private static readonly Regex HexLiteralRegex =
        new(@"(?<![0-9A-Za-z_])\$([0-9A-Fa-f]+)", RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // Step 2: FQN :: substitution
    //   Handles:  module::symbol    ::globalSym    a::b::c
    //   Replaces matched FQN tokens with their decimal integer values.
    // ------------------------------------------------------------------
    private string SubstituteFqn(string s) {
        if (!s.Contains("::")) return s;    // fast exit — most expressions won't have FQN

        var sb  = new StringBuilder(s.Length);
        int pos = 0;

        while (pos < s.Length) {
            // Leading "::" (global-scope root)
            bool global = pos + 1 < s.Length && s[pos] == ':' && s[pos + 1] == ':';
            // Or start of identifier that might be followed by "::"
            bool ident  = char.IsLetter(s[pos]) || s[pos] == '_';

            if (!global && !ident) {
                sb.Append(s[pos++]);
                continue;
            }

            // Read the full "[ :: ] name ( :: name )*" token.
            int    start     = pos;
            bool   hasScoper = global;
            var    parts     = new List<string>(4);

            if (global) pos += 2;

            // Read first/only name segment.
            if (pos >= s.Length || (!char.IsLetter(s[pos]) && s[pos] != '_')) {
                // Dangling "::" with no identifier after — emit as-is.
                sb.Append(s, start, pos - start);
                continue;
            }

            parts.Add(ReadIdent(s, ref pos));

            // Read additional "::" segments.
            while (pos + 1 < s.Length && s[pos] == ':' && s[pos + 1] == ':') {
                int peek = pos + 2;
                if (peek >= s.Length || (!char.IsLetter(s[peek]) && s[peek] != '_')) break;
                hasScoper = true;
                pos       = peek;
                parts.Add(ReadIdent(s, ref pos));
            }

            // Only treat this as a FQN token if it actually contained "::".
            if (!hasScoper || parts.Count == 0) {
                sb.Append(s, start, pos - start);
                continue;
            }

            // Build the lookup key the way the constructor stored them.
            var path = global
                ? $"::{string.Join("::", parts)}"
                : string.Join("::", parts);

            if (_fqnSymbols.TryGetValue(path, out var val)) {
                sb.Append(val);
            } else {
                // Unknown FQN — leave the original text so SER can produce a useful error.
                sb.Append(s, start, pos - start);
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Step 3: cpu[] / program[] / character[] substitution
    //   Finds the first innermost occurrence of one of the three keywords
    //   followed by "[expr]", evaluates expr with SER, does the safe peek,
    //   and replaces the whole token with the byte value as a decimal literal.
    //   Returns (result, changed) so the caller can iterate.
    // ------------------------------------------------------------------
    private static string SubstituteMemArrays(
        string                               s,
        Dictionary<string, SER.SerUnion<int>> symbols,
        Func<ushort, byte>                   cpuRead,
        Func<int,    byte>                   programRead,
        Func<int,    byte>                   characterRead)
    {
        // Find the leftmost occurrence of any of our keywords followed by '['.
        // We work left-to-right and replace one at a time, then the caller retries.
        foreach (var kw in MemArrayKeywords) {
            int kwPos = s.IndexOf(kw + "[", StringComparison.OrdinalIgnoreCase);
            if (kwPos < 0) continue;

            // Ensure the keyword isn't a suffix of a longer identifier.
            // (e.g. "notcpu[" should not match "cpu[")
            if (kwPos > 0 && (char.IsLetterOrDigit(s[kwPos - 1]) || s[kwPos - 1] == '_'))
                continue;

            int bracketOpen  = kwPos + kw.Length;          // index of '['
            int contentStart = bracketOpen + 1;             // index after '['

            // Find the matching ']' — track nesting depth for nested brackets.
            int depth = 1;
            int i     = contentStart;
            while (i < s.Length && depth > 0) {
                if      (s[i] == '[') depth++;
                else if (s[i] == ']') depth--;
                if (depth > 0) i++;
            }

            if (depth != 0) continue;   // unmatched bracket — skip

            int bracketClose = i;       // index of matching ']'
            var indexExpr    = s[contentStart..bracketClose];

            // Evaluate the index expression with SER.
            // Pass a copy — SER takes expr by ref and may normalise it.
            var exprCopy = indexExpr;
            if (!SER.TryEvaluate(ref exprCopy, out var index, symbols)) {
                // Cannot evaluate index yet (perhaps a nested memory array still
                // needs to be substituted in a later pass) — skip this occurrence.
                continue;
            }

            // Do the safe, side-effect-free memory read.
            byte value = kw.ToLowerInvariant() switch {
                "cpu"       => cpuRead((ushort)(index & 0xFFFF)),
                "program"   => programRead(index),
                "character" => characterRead(index),
                _           => 0
            };

            // Replace "keyword[indexExpr]" with the decimal byte value.
            int  tokenLen = kw.Length + 1 + (bracketClose - contentStart) + 1;
            //               ^kw       ^[   ^content len                   ^]
            s = s[..kwPos] + value.ToString() + s[(kwPos + tokenLen)..];
            return s;   // one substitution per call; caller iterates
        }

        return s;   // no substitution made
    }

    private static readonly string[] MemArrayKeywords = ["cpu", "program", "character"];

    // ══════════════════════════════════════════════════════════════════════════
    //  Scope helpers
    // ══════════════════════════════════════════════════════════════════════════

    private int? FindScopeForAddress(int romAddress) {
        int lo = 0, hi = _spans.Count - 1, found = -1;
        while (lo <= hi) {
            int mid = (lo + hi) / 2;
            if (_spans[mid].Start <= romAddress) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return null;
        var span = (SpanImpl)_spans[found];
        return romAddress < span.Start + span.Length ? span.ScopeId : null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Small helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Reads an identifier from <paramref name="s"/> starting at <paramref name="pos"/>.</summary>
    private static string ReadIdent(string s, ref int pos) {
        int start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_'))
            pos++;
        return s[start..pos];
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  .dbg file parsing helpers (unchanged)
    // ══════════════════════════════════════════════════════════════════════════

    private static Dictionary<string, string> ParseKv(string s, int lineNo) {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i  = 0;
        while (true) {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;

            int    k0  = i;
            while (i < s.Length && s[i] != '=' && s[i] != ',') i++;
            if (i >= s.Length || s[i] != '=') throw new FormatException($"Line {lineNo}: expected key=value");
            string key = s[k0..i].Trim().ToLowerInvariant();
            i++;

            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            string val;
            if (i < s.Length && s[i] == '"') {
                i++;
                var sb = new global::System.Text.StringBuilder();
                while (i < s.Length) {
                    char c = s[i++];
                    if (c == '"') break;
                    if (c == '\\' && i < s.Length) sb.Append(s[i++]);
                    else sb.Append(c);
                }
                val = sb.ToString();
            } else {
                int v0 = i;
                while (i < s.Length && s[i] != ',') i++;
                val = s[v0..i].Trim();
            }

            if (!kv.TryAdd(key, val)) throw new FormatException($"Line {lineNo}: duplicate key '{key}'");

            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i    >= s.Length) break;
            if (s[i] != ',') throw new FormatException($"Line {lineNo}: expected ','");
            i++;
        }
        return kv;
    }

    private static string  ReqStr(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? v : throw new FormatException($"Line {lineNo}: missing '{key}'");
    private static string? OptStr(Dictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var v) ? v : null;
    private static int     ReqInt(Dictionary<string, string> kv, string key, int lineNo)
        => (int)ReqLong(kv, key, lineNo);
    private static int?    OptInt(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? (int)ParseLong(v, lineNo, key) : null;
    private static long    ReqLong(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? ParseLong(v, lineNo, key) : throw new FormatException($"Line {lineNo}: missing '{key}'");
    private static long?   OptLong(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? ParseLong(v, lineNo, key) : null;

    private static long ParseLong(string v, int lineNo, string key) {
        v = v.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (v.StartsWith("$", StringComparison.Ordinal))
            return long.Parse(v[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;
        throw new FormatException($"Line {lineNo}: bad number for {key}='{v}'");
    }

    private static TEnum  ReqEnum<TEnum>(Dictionary<string, string> kv, string key, int lineNo) where TEnum : struct, Enum {
        if (!kv.TryGetValue(key, out var v)) throw new FormatException($"Line {lineNo}: missing '{key}'");
        return ParseEnum<TEnum>(v, lineNo, key);
    }
    private static TEnum? OptEnum<TEnum>(Dictionary<string, string> kv, string key, int lineNo) where TEnum : struct, Enum
        => kv.TryGetValue(key, out var v) ? ParseEnum<TEnum>(v, lineNo, key) : null;

    private static TEnum ParseEnum<TEnum>(string v, int lineNo, string key) where TEnum : struct, Enum {
        string norm = v.Trim().ToLowerInvariant();
        norm = char.ToUpperInvariant(norm[0]) + norm[1..];
        if (Enum.TryParse<TEnum>(norm, ignoreCase: true, out var e)) return e;
        throw new FormatException($"Line {lineNo}: bad enum for {key}='{v}'");
    }

    private static int[]? OptIntListPlus(Dictionary<string, string> kv, string key, int lineNo) {
        if (!kv.TryGetValue(key, out var v)) return null;
        var parts = v.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arr   = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            arr[i] = (int)ParseLong(parts[i], lineNo, key);
        return arr;
    }
}

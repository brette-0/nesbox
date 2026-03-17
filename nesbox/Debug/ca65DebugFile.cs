using System.Globalization;
using System.Numerics;

namespace nesbox.Debug;

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

        // Group symbols by the scope ID they declare membership in
        var symsByScope = new Dictionary<int, List<API.Debugging.ISymbol>>();
        foreach (var sym in Syms) {
            if (sym.Val is null) continue;
            var scopeId = sym.Scope ?? sym.Parent ?? 0;
            if (!symsByScope.TryGetValue(scopeId, out var list))
                symsByScope[scopeId] = list = [];
            list.Add(new SymbolImpl(sym.Name, (int)sym.Val.Value));
        }

        // Build one ScopeImpl per ScopeRec, carrying its resolved symbol list
        var scopeImpls = new Dictionary<int, ScopeImpl>();
        foreach (var sc in Scopes) {
            symsByScope.TryGetValue(sc.Id, out var symList);
            scopeImpls[sc.Id] = new ScopeImpl(symList ?? []);
        }

        // Invert ScopeRec.Spans[] to get span ID → scope ID
        var spanToScope = new Dictionary<int, int>();
        foreach (var sc in Scopes) {
            if (sc.Spans is null) continue;
            foreach (var spanId in sc.Spans)
                spanToScope.TryAdd(spanId, sc.Id);
        }

        var emptyScope = new ScopeImpl([]);

        // Build _spans as a list sorted ascending by Start for binary search
        foreach (var sp in RawSpans) {
            var scope = spanToScope.TryGetValue(sp.Id, out var sid) && scopeImpls.TryGetValue(sid, out var si)
                ? (API.Debugging.IScope)si
                : emptyScope;
            var start = (int)(segById.TryGetValue(sp.Seg, out var seg) ? seg.Start + sp.Start : sp.Start);
            _spans.Add(new SpanImpl(start, (int)sp.Size, scope));
        }

        _spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Build _lines keyed by ROM address
        // Each LineRec references spans; the first span gives the primary ROM address
        foreach (var lr in RawLines) {
            if (lr.Spans is null || lr.Spans.Length == 0)     continue;
            if (!spanById.TryGetValue(lr.Spans[0], out var sp))  continue;
            if (!segById.TryGetValue(sp.Seg,        out var seg)) continue;
            if (!fileById.TryGetValue(lr.File,       out var f))  continue;

            var romAddr = (T)(object)(int)(seg.Start + sp.Start);
            _lines.TryAdd(romAddr, new LineImpl(f.Name, (int)lr.Line));
        }
    }

    // ---------- parsing helpers ----------

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

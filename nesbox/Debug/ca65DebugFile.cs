using System.Globalization;
using System.Numerics;

namespace nesbox.Debug;

public sealed class Ld65Dbg<T> : API.Debugging.IDebugFile<T> where T : IBinaryInteger<T> {
    private IDictionary<T, API.Debugging.ILine>   _lines;
    private IDictionary<int, API.Debugging.ISpan> _spans;

    // ---------- enums ----------
    public enum AddrSize { Zeropage, Absolute, Long }
    public enum SegPerm { Ro, Rw }
    public enum ScopeType { Global, File, Scope, Struct, Enum }
    public enum SymKind { Equ, Imp, Lab } // ld65 uses: equ/imp/lab

    // ---------- records ----------
    public readonly record struct VersionRec(int Major, int Minor);
    public readonly record struct InfoRec(int    Csym,  int File, int Lib, int Line, int Mod, int Scope, int Seg, int Span, int Sym, int Type);

    public readonly record struct FileRec(int Id, string Name, long Size, long MTime, int Mod);
    public readonly record struct ModRec(int  Id, string Name, int  File, int? Lib);
    public readonly record struct LibRec(int  Id, string Name);

    public readonly record struct SegRec(
        int  Id,   string  Name,       long  Start, long Size, AddrSize AddrSize, SegPerm Type,
        int? Bank, string? OutputName, long? OutputOffs);

    public readonly record struct SpanRec(int Id, int Seg, long Start, long Size, int? Type);

    public readonly record struct ScopeRec(
        int Id, string Name, int Mod, long? Size, ScopeType? Type, int? Parent, int? Sym, int[]? Spans);

    // FIX: line span can be a '+' list (or absent)
    public readonly record struct LineRec(
        int Id, int File, long Line, int? Type, int? Count, int[]? Spans);

    public readonly record struct SymRec(
        int    Id,   string Name, AddrSize AddrSize, SymKind Type,
        long?  Val,  long?  Size, int?     Seg,      int?    Scope, int? Parent,
        int[]? Defs, int[]? Refs);

    public readonly record struct TypeRec(int Id, string Val);

    // ---------- outputs ----------
    public VersionRec Version { get; }
    public InfoRec    Info    { get; }

    public FileRec[]                                                 Files   { get; }
    public ModRec[]                                                  Mods    { get; }
    public LibRec[]                                                  Libs    { get; }
    public SegRec[]                                                  Segs    { get; }

    IDictionary<T, API.Debugging.ILine> API.Debugging.IDebugFile<T>.Lines   => _lines;
    public IReadOnlyList<API.Debugging.ISymbol>                     Symbols { get; }


    IDictionary<int, API.Debugging.ISpan> API.Debugging.IDebugFile<T>.Spans => _spans;

    public SpanRec[]                                                 Spans   { get; }
    public ScopeRec[]                                                Scopes  { get; }
    public LineRec[]                                                 Lines   { get; }
    public SymRec[]                                                  Syms    { get; }
    public TypeRec[]                                                 Types   { get; }

    public Ld65Dbg(string filepath)
    {
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
        foreach (var raw in File.ReadLines(filepath))
        {
            lineNo++;
            var line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#') continue;

            int    tab  = line.IndexOf('\t');
            string rec  = (tab >= 0 ? line[..tab] : line).Trim().ToLowerInvariant();
            string rest = tab >= 0 ? line[(tab + 1)..] : "";

            var kv = ParseKv(rest, lineNo);

            switch (rec)
            {
                case "version":
                    version = new VersionRec(
                        ReqInt(kv, "major", lineNo),
                        ReqInt(kv, "minor", lineNo));
                    break;

                case "info":
                    info = new InfoRec(
                        ReqInt(kv, "csym",  lineNo),
                        ReqInt(kv, "file",  lineNo),
                        ReqInt(kv, "lib",   lineNo),
                        ReqInt(kv, "line",  lineNo),
                        ReqInt(kv, "mod",   lineNo),
                        ReqInt(kv, "scope", lineNo),
                        ReqInt(kv, "seg",   lineNo),
                        ReqInt(kv, "span",  lineNo),
                        ReqInt(kv, "sym",   lineNo),
                        ReqInt(kv, "type",  lineNo));
                    break;

                case "file":
                    files.Add(new FileRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Name: ReqStr(kv, "name", lineNo),
                        Size: ReqLong(kv,  "size",  lineNo),
                        MTime: ReqLong(kv, "mtime", lineNo),
                        Mod: ReqInt(kv, "mod", lineNo)));
                    break;

                case "mod":
                    mods.Add(new ModRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Name: ReqStr(kv, "name", lineNo),
                        File: ReqInt(kv, "file", lineNo),
                        Lib: OptInt(kv, "lib", lineNo)));
                    break;

                case "lib":
                    libs.Add(new LibRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Name: ReqStr(kv, "name", lineNo)));
                    break;

                case "seg":
                    segs.Add(new SegRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Name: ReqStr(kv, "name", lineNo),
                        Start: ReqLong(kv, "start", lineNo),
                        Size: ReqLong(kv,  "size",  lineNo),
                        AddrSize: ReqEnum<AddrSize>(kv, "addrsize", lineNo),
                        Type: ReqEnum<SegPerm>(kv, "type", lineNo),
                        Bank: OptInt(kv, "bank", lineNo),
                        OutputName: OptStr(kv, "outputname"),
                        OutputOffs: OptLong(kv, "outputoffs", lineNo)
                    ));
                    // paired constraint if either exists
                    if ((OptStr(kv, "outputname") is null) != (OptLong(kv, "outputoffs", lineNo) is null))
                        throw new FormatException($"Line {lineNo}: outputname and outputoffs must appear together");
                    break;

                case "span":
                    spans.Add(new SpanRec(
                        Id: ReqInt(kv,  "id",  lineNo),
                        Seg: ReqInt(kv, "seg", lineNo),
                        Start: ReqLong(kv, "start", lineNo),
                        Size: ReqLong(kv,  "size",  lineNo),
                        Type: OptInt(kv, "type", lineNo)));
                    break;

                case "scope":
                    scopes.Add(new ScopeRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Name: ReqStr(kv, "name", lineNo),
                        Mod: ReqInt(kv, "mod", lineNo),
                        Size: OptLong(kv, "size", lineNo),
                        Type: OptEnum<ScopeType>(kv, "type", lineNo),
                        Parent: OptInt(kv, "parent", lineNo),
                        Sym: OptInt(kv,    "sym",    lineNo),
                        Spans: OptIntListPlus(kv, "span", lineNo)));
                    break;

                case "line":
                    lines.Add(new LineRec(
                        Id: ReqInt(kv,   "id",   lineNo),
                        File: ReqInt(kv, "file", lineNo),
                        Line: ReqLong(kv, "line", lineNo),
                        Type: OptInt(kv,  "type",  lineNo),
                        Count: OptInt(kv, "count", lineNo),
                        Spans: OptIntListPlus(kv, "span", lineNo))); // FIX
                    break;

                case "sym":
                    int? scope  = OptInt(kv, "scope",  lineNo);
                    int? parent = OptInt(kv, "parent", lineNo);
                    if ((scope is null) == (parent is null))
                        throw new FormatException($"Line {lineNo}: sym must have exactly one of scope= or parent=");

                    syms.Add(new SymRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Name: ReqStr(kv, "name", lineNo),
                        AddrSize: ReqEnum<AddrSize>(kv, "addrsize", lineNo),
                        Type: ReqEnum<SymKind>(kv, "type", lineNo),
                        Val: OptLong(kv,  "val",  lineNo),
                        Size: OptLong(kv, "size", lineNo),
                        Seg: OptInt(kv, "seg", lineNo),
                        Scope: scope,
                        Parent: parent,
                        Defs: OptIntListPlus(kv, "def", lineNo),
                        Refs: OptIntListPlus(kv, "ref", lineNo)
                    ));
                    break;

                case "type":
                    types.Add(new TypeRec(
                        Id: ReqInt(kv, "id", lineNo),
                        Val: ReqStr(kv, "val", lineNo)));
                    break;

                // your test file had csym=0 so none present; add later if needed
                default:
                    throw new FormatException($"Line {lineNo}: unknown record '{rec}'");
            }
        }

        Version = version ?? throw new FormatException("Missing version record");
        Info    = info    ?? throw new FormatException("Missing info record");

        Files  = files.ToArray();
        Mods   = mods.ToArray();
        Libs   = libs.ToArray();
        Segs   = segs.ToArray();
        Spans  = spans.ToArray();
        Scopes = scopes.ToArray();
        Lines  = lines.ToArray();
        Syms   = syms.ToArray();
        Types  = types.ToArray();
    }

    // ---------- parsing helpers ----------
    private static Dictionary<string, string> ParseKv(string s, int lineNo)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i  = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;

            int k0 = i;
            while (i < s.Length && s[i]  != '=' && s[i] != ',') i++;
            if (i    >= s.Length || s[i] != '=') throw new FormatException($"Line {lineNo}: expected key=value");
            string key = s[k0..i].Trim().ToLowerInvariant();
            i++; // '='

            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            string val;
            if (i < s.Length && s[i] == '"')
            {
                i++;
                var sb = new global::System.Text.StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') break;
                    if (c == '\\' && i < s.Length) sb.Append(s[i++]);
                    else sb.Append(c);
                }
                val = sb.ToString();
            }
            else
            {
                int v0 = i;
                while (i < s.Length && s[i] != ',') i++;
                val = s[v0..i].Trim();
            }

            if (!kv.TryAdd(key, val))
                throw new FormatException($"Line {lineNo}: duplicate key '{key}'");

            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i    >= s.Length) break;
            if (s[i] != ',') throw new FormatException($"Line {lineNo}: expected ','");
            i++;
        }
        return kv;
    }

    private static string ReqStr(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? v : throw new FormatException($"Line {lineNo}: missing '{key}'");

    private static string? OptStr(Dictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var v) ? v : null;

    private static int ReqInt(Dictionary<string, string> kv, string key, int lineNo)
        => (int)ReqLong(kv, key, lineNo);

    private static int? OptInt(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? (int)ParseLong(v, lineNo, key) : null;

    private static long ReqLong(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? ParseLong(v, lineNo, key) : throw new FormatException($"Line {lineNo}: missing '{key}'");

    private static long? OptLong(Dictionary<string, string> kv, string key, int lineNo)
        => kv.TryGetValue(key, out var v) ? ParseLong(v, lineNo, key) : null;

    private static long ParseLong(string v, int lineNo, string key)
    {
        v = v.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (v.StartsWith("$", StringComparison.Ordinal))
            return long.Parse(v[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;
        throw new FormatException($"Line {lineNo}: bad number for {key}='{v}'");
    }

    private static T ReqEnum<T>(Dictionary<string, string> kv, string key, int lineNo) where T : struct, Enum
    {
        if (!kv.TryGetValue(key, out var v)) throw new FormatException($"Line {lineNo}: missing '{key}'");
        return ParseEnum<T>(v, lineNo, key);
    }

    private static T? OptEnum<T>(Dictionary<string, string> kv, string key, int lineNo) where T : struct, Enum
        => kv.TryGetValue(key, out var v) ? ParseEnum<T>(v, lineNo, key) : null;

    private static T ParseEnum<T>(string v, int lineNo, string key) where T : struct, Enum
    {
        // ld65 values are lowercase keywords; map to PascalCase enum names
        string norm = v.Trim().ToLowerInvariant();
        norm = char.ToUpperInvariant(norm[0]) + norm[1..];
        if (Enum.TryParse<T>(norm, ignoreCase: true, out var e)) return e;
        throw new FormatException($"Line {lineNo}: bad enum for {key}='{v}'");
    }

    private static int[]? OptIntListPlus(Dictionary<string, string> kv, string key, int lineNo)
    {
        if (!kv.TryGetValue(key, out var v)) return null;
        var parts = v.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arr   = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            arr[i] = (int)ParseLong(parts[i], lineNo, key);
        return arr;
    }

    public int GetSymbol(string sym) {
        throw new NotImplementedException();
    }
    public IDictionary<string, int> GetSymbols(IReadOnlyList<string> syms) {
        throw new NotImplementedException();
    }
}
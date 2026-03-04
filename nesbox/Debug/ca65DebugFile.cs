using System.Globalization;

namespace nesbox.Debug;

public class ca65DebugFile : API.IDebugFile {
    public ca65DebugFile(string fp) {
        var lines = global::System.IO.File.ReadAllLines(fp);

        if (lines[0] is not "info\tversion major=2,minor=0") goto fail;

        var elem = lines[1].Split('\t');
        if (elem[0] is not "info") goto fail;

        Parse(elem[1], out var info);
        string? meta;
        
        if (!info.TryGetValue("csym",  out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nCSymbols))  goto fail;
        if (!info.TryGetValue("file",  out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nFiles))     goto fail;
        if (!info.TryGetValue("lib",   out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nLibs))      goto fail;
        if (!info.TryGetValue("line",  out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nLines))     goto fail;
        if (!info.TryGetValue("mod",   out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nMods))      goto fail;
        if (!info.TryGetValue("scope", out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nScopes))    goto fail;
        if (!info.TryGetValue("seg",   out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nSegments))  goto fail;
        if (!info.TryGetValue("span",  out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nSpans))     goto fail;
        if (!info.TryGetValue("sym",   out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nSymbols))   goto fail;
        if (!info.TryGetValue("type",  out meta))                            goto fail;
        if (!int.TryParse(meta, NumberStyles.Integer, null, out nTypes))     goto fail;

        var i = 2;
        for (var f = 0; f < nFiles; f++) {
            elem = lines[i + f].Split('\t');
            if (elem[0] is not "file") goto fail;
            Parse(elem[1], out var file);

            var fileObj = new File();
            if (!file.TryGetValue("id",    out meta))                               goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out fileObj.id))    goto fail;
            if (fileObj.id != f) goto fail;
            if (!file.TryGetValue("name",  out meta))                               goto fail;
            if (meta.Length < 3) goto fail;
            fileObj.name = meta[1..1];
            if (!file.TryGetValue("size",  out meta))                               goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out fileObj.size))  goto fail;
            if (!file.TryGetValue("mtime", out meta))                               goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out fileObj.mtime)) goto fail;
            if (!file.TryGetValue("mod",   out meta))                               goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out fileObj.mod))   goto fail;
            
            Files.Add(fileObj);
        } i += nFiles;

        for (var l = 0; l < nLines; l++) {
            elem = lines[i + l].Split('\t');
            if (elem[0] is not "line") goto fail;
            
            Parse(elem[1], out var line);
            var lineObj = new Line();
            if (!line.TryGetValue("id",    out meta))                                      goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out lineObj.id))           goto fail;
            if (lineObj.id != l) goto fail;
            if (!line.TryGetValue("file",  out meta))                                      goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out lineObj.file))         goto fail;
            if (!line.TryGetValue("line",   out meta))                                     goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out lineObj.line))         goto fail;
            if (line.TryGetValue("type", out meta)) {
                if (!int.TryParse(meta, NumberStyles.Integer, null, out var lineObjType))  goto fail;
                lineObj.type = lineObjType;
                if (!line.TryGetValue("count",   out meta))                                goto fail;
                if (!int.TryParse(meta, NumberStyles.Integer, null, out var lineObjCount)) goto fail;
                lineObj.count = lineObjCount;
            }
            if (!line.TryGetValue("span",   out meta))                                     continue;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out var lineObjSpan))      goto fail;
            lineObj.span = lineObjSpan;
            Lines.Add(lineObj);
        } i += nLines;

        for (var m = 0; m < nMods; m++) {
            elem = lines[i + m].Split('\t');
            if (elem[0] is not "mod") goto fail;
            var modObj = new Mod();
            
            Parse(elem[1], out var mod);
            if (!mod.TryGetValue("id",   out meta))                                      goto fail;
            if (modObj.id != m) goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out modObj.id))          goto fail;
            if (!mod.TryGetValue("name", out meta))                                      goto fail;
            if (meta.Length < 3) goto fail;
            modObj.name = meta[1..1];
            if (!mod.TryGetValue("file", out meta))                                      goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out modObj.file))        goto fail;
            Mods.Add(modObj);
        } i += nMods;

        for (var s = 0; s < nSegments; s++) {
            elem = lines[i + s].Split('\t');
            if (elem[0] is not "seg") goto fail;
            var segObj = new Segment();
            
            Parse(elem[1], out var seg);
            if (!int.TryParse(meta, NumberStyles.Integer, null, out segObj.id))               goto fail;
            if (!seg.TryGetValue("id",     out meta))                                         goto fail;
            if (segObj.id != s) goto fail;
            if (!seg.TryGetValue("name",     out meta))                                       goto fail;
            if (meta.Length < 3) goto fail;
            segObj.name = meta[1..1];
            if (!seg.TryGetValue("start",    out meta))                                       goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out segObj.start))            goto fail;
            if (!seg.TryGetValue("size",     out meta))                                       goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out segObj.size))             goto fail;
            if (!seg.TryGetValue("addrsize", out meta))                                       goto fail;
            segObj.addressSize = meta switch {
                "absolute" => AddressSize.Absolute,
                "zeropage" => AddressSize.ZeroPage,
                _          => AddressSize.Fail
            };
            if (segObj.addressSize is AddressSize.Fail) goto fail;
            if (!seg.TryGetValue("type", out meta))                                           goto fail;
            segObj.type = meta switch {
                "rw" => SegmentType.RW,
                "ro" => SegmentType.RO,
                _    => SegmentType.Fail
            };
            if (segObj.type is SegmentType.Fail) goto fail;
            if (seg.TryGetValue("oname",      out meta)) {
                if (segObj.type is not SegmentType.RO) goto fail;
                if (meta.Length < 3) goto fail;
                segObj.outFileName = meta[1..1];
                if (!seg.TryGetValue("ooffs", out meta))                                      goto fail;
                if (!int.TryParse(meta, NumberStyles.Integer, null, out var segObjOutOffset)) goto fail;
                segObj.outOffset = segObjOutOffset;
            }
            Segments.Add(segObj);
        } i += nSegments;

        for (var s = 0; s < nSpans; s++) {
            elem = lines[i + s].Split('\t');
            if (elem[0] is not "span") goto fail;
            var spanObj = new Span();
            
            Parse(elem[1], out var span);
            if (!span.TryGetValue("id",   out meta))                                  goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out spanObj.id))      goto fail;
            if (spanObj.id != s) goto fail;
            if (!span.TryGetValue("seg",   out meta))                                 goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out spanObj.segment)) goto fail;
            if (!span.TryGetValue("start",   out meta))                               goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out spanObj.start))   goto fail;
            if (!span.TryGetValue("size",   out meta))                                goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out spanObj.size))    goto fail;
            if (!span.TryGetValue("type",   out meta))                                goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out var spanObjType)) goto fail;
            spanObj.type = spanObjType;
            Spans.Add(spanObj);
        } i += nSpans;

        var hasFoundTopLevel = false;
        for (var s = 0; s < nScopes; s++) {
            elem = lines[i + s].Split('\t');
            if (elem[0] is not "scope") goto fail;
            
            var scopeObj = new Scope();
            Parse(elem[1], out var scope);
            if (!scope.TryGetValue("id",   out meta))                                  goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out scopeObj.id))      goto fail;
            if (scopeObj.id != s) goto fail;
            if (!scope.TryGetValue("name",     out meta))                              goto fail;
            if (meta is @"""" && !hasFoundTopLevel) {
                scopeObj.name    = string.Empty;
                hasFoundTopLevel = true;
            } else {
                if (meta.Length < 3) goto fail;
                scopeObj.name = meta[1..1];
            }
            if (!scope.TryGetValue("mod",   out meta))                                 goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out scopeObj.mod))     goto fail;
            if (!scope.TryGetValue("name",     out meta))                              goto fail;
            scopeObj.type = meta switch {
                "scope" => ScopeType.Scope,

                _ => ScopeType.Scope
            };
            if (scopeObj.type is ScopeType.Fail) goto fail;
            if (!scope.TryGetValue("size",   out meta))                                goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out scopeObj.size))    goto fail;
            if (!scope.TryGetValue("parent", out meta))                                goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out scopeObj.parent))  goto fail;
            if (!scope.TryGetValue("sym",   out meta))                                 goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out scopeObj.sym))     goto fail;
            if (!scope.TryGetValue("span",  out meta))                                 goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out scopeObj.span))    goto fail;
            Scopes.Add(scopeObj);
        } i += nScopes;

        for (var s = 0; s < nSymbols; s++) {
            
        } i += nSymbols;

        for (var t = 0; t < nTypes; t++) {
            elem = lines[i + t].Split('\t');
            if (elem[0] is not "type") goto fail;
            
            var typeObj = new Type();
            Parse(elem[1], out var type);
            if (!type.TryGetValue("id",   out meta))                            goto fail;
            if (!int.TryParse(meta, NumberStyles.Integer, null, out typeObj.id)) goto fail;
            if (typeObj.id != t) goto fail;
            if (!type.TryGetValue("val",     out meta))                           goto fail;
            if (meta.Length < 3) goto fail;
            typeObj.value = meta[1..1];
            Types.Add(typeObj);
        }

        if (CSymbols.Count != nCSymbols ||
            Files.Count    != nFiles    ||
            Lines.Count    != nLines    ||
            Mods.Count     != nMods     ||
            Segments.Count != nSegments ||
            Spans.Count    != nSpans    ||
            Scopes.Count   != nScopes   ||
            Symbols.Count  != nSymbols  ||
            Types.Count    != nTypes) goto fail;
        
        Console.WriteLine("[DBG] Successfully parsed ca65 debug file");
        return;
            
        fail: System.Quit = true;
    }

    private static void Parse(string line, out Dictionary<string, string> parsed) {
        parsed = new();
        foreach (var field in line.Split(',')) {
            parsed[field[..field.IndexOf('=')]] = field[(field.IndexOf('=') + 1)..];
        }
    }

    public enum AddressSize : byte {
        Absolute,
        ZeroPage,
        
        Fail = 255
    }
    
    public enum SegmentType : byte {
        RW,
        RO,
        
        Fail = 255
    }

    public enum ScopeType : byte {
        Scope,
        
        
        Fail = 255
    }

    public enum SymbolType : byte {
        Label,
        Equal,
        
        Fail = 255
    }

    public struct CSymbol {
        
    }
    
    public struct Type {
        public int    id;
        public string value;
    }

    public struct Symbol {
        public int         id;
        public string      name;
        public AddressSize addressSize;
        public int         parent;
        public int         define;
        public int         reference;
        public int         value;
        public int         segment;
        public SymbolType  type;
        public int?        scope;
    }

    public struct Scope {
        public int       id;
        public string    name;
        public int       mod;
        public ScopeType type;
        public int       size;
        public int       parent;
        public int       sym;
        public int       span;
    }

    public struct Span {
        public int  id;
        public int  segment;
        public int  start;
        public int  size;
        public int? type;
    }

    public struct Segment {
        public int         id;
        public string      name;
        public int         start;
        public int         size;
        public AddressSize addressSize;
        public SegmentType type;
        public string?     outFileName;
        public int?        outOffset;
    }

    public struct Mod {
        public int    id;
        public string name;
        public int    file;
    }
    
    public struct Line {
        public int  id;
        public int  file;
        public int  line;
        public int? type;
        public int? count;
        public int? span;
    }

    public struct File {
        public int    id;
        public string name;
        public int    size;
        public int    mtime;
        public int    mod;
    }
    
    // meta
    public int nCSymbols;
    public int nFiles;
    public int nLibs;
    public int nLines;
    public int nMods;
    public int nScopes;
    public int nSegments;
    public int nSpans;
    public int nSymbols;
    public int nTypes;
    
    // content
    public readonly List<CSymbol> CSymbols = [];
    public readonly List<File>    Files    = [];
    public readonly List<Line>    Lines    = [];
    public readonly List<Mod>     Mods     = [];
    public readonly List<Segment> Segments = [];
    public readonly List<Span>    Spans    = [];
    public readonly List<Scope>   Scopes   = [];
    public readonly List<Symbol>  Symbols  = [];
    public readonly List<Type>    Types    = [];


    public int GetSymbol() {
        throw new NotImplementedException();
    }
    public int GetAddressMapping() {
        throw new NotImplementedException();
    }
}
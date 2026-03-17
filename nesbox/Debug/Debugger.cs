using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nesbox.Debug;
using SER = StringExpressionEvaluator.StringExpressionEvaluator;
using IO  = global::System.IO;

// ---------------------------------------------------------------------------
// AOT-safe JSON source-generation context.
// ---------------------------------------------------------------------------
[JsonSerializable(typeof(AttachRequestArguments))]
[JsonSerializable(typeof(DapMessage))]
[JsonSerializable(typeof(DapResponse))]
[JsonSerializable(typeof(DapEvent))]
[JsonSerializable(typeof(SetBreakpointsRequestArguments))]
[JsonSerializable(typeof(VariablesRequestArguments))]
[JsonSerializable(typeof(CapabilitiesBody))]
[JsonSerializable(typeof(InitializedEventBody))]
[JsonSerializable(typeof(StoppedEventBody))]
[JsonSerializable(typeof(SetBreakpointsResponseBody))]
[JsonSerializable(typeof(StackTraceResponseBody))]
[JsonSerializable(typeof(ScopesResponseBody))]
[JsonSerializable(typeof(VariablesResponseBody))]
[JsonSerializable(typeof(DapBreakpoint))]
[JsonSerializable(typeof(DapStackFrame))]
[JsonSerializable(typeof(DapSource))]
[JsonSerializable(typeof(DapScope))]
[JsonSerializable(typeof(DapVariable))]
[JsonSerializable(typeof(DapBreakpointSource))]
[JsonSerializable(typeof(List<DapBreakpoint>))]
[JsonSerializable(typeof(List<DapStackFrame>))]
[JsonSerializable(typeof(List<DapScope>))]
[JsonSerializable(typeof(List<DapVariable>))]
[JsonSerializable(typeof(List<DapBreakpointSource>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class DapJsonContext : JsonSerializerContext { }

// ---------------------------------------------------------------------------
// DAP wire types
// ---------------------------------------------------------------------------

internal sealed record DapMessage(
    [property: JsonPropertyName("seq")]     int          Seq,
    [property: JsonPropertyName("type")]    string       Type,
    [property: JsonPropertyName("command")] string?      Command,
    [property: JsonPropertyName("event")]   string?      Event,
    [property: JsonPropertyName("body")]    JsonElement? Body
);

internal sealed record DapResponse(
    [property: JsonPropertyName("seq")]         int          Seq,
    [property: JsonPropertyName("type")]        string       Type,
    [property: JsonPropertyName("request_seq")] int          RequestSeq,
    [property: JsonPropertyName("success")]     bool         Success,
    [property: JsonPropertyName("command")]     string       Command,
    [property: JsonPropertyName("body")]        JsonElement? Body
);

internal sealed record DapEvent(
    [property: JsonPropertyName("seq")]   int          Seq,
    [property: JsonPropertyName("type")]  string       Type,
    [property: JsonPropertyName("event")] string       EventName,
    [property: JsonPropertyName("body")]  JsonElement? Body
);

internal sealed record AttachRequestArguments(
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int?    Port
);

internal sealed record DapBreakpointSource(
    [property: JsonPropertyName("line")]      int     Line,
    [property: JsonPropertyName("condition")] string? Condition
);

internal sealed record SetBreakpointsRequestArguments(
    [property: JsonPropertyName("source")]      DapSource                  Source,
    [property: JsonPropertyName("breakpoints")] List<DapBreakpointSource>? Breakpoints
);

internal sealed record VariablesRequestArguments(
    [property: JsonPropertyName("variablesReference")] int VariablesReference
);

internal sealed record CapabilitiesBody(
    [property: JsonPropertyName("supportsConditionalBreakpoints")]        bool SupportsConditionalBreakpoints,
    [property: JsonPropertyName("supportsConfigurationDoneRequest")]      bool SupportsConfigurationDoneRequest,
    [property: JsonPropertyName("supportsSingleThreadExecutionRequests")] bool SupportsSingleThreadExecutionRequests
);

internal sealed record InitializedEventBody;

internal sealed record StoppedEventBody(
    [property: JsonPropertyName("reason")]            string Reason,
    [property: JsonPropertyName("threadId")]          int    ThreadId,
    [property: JsonPropertyName("allThreadsStopped")] bool   AllThreadsStopped
);

internal sealed record DapBreakpoint(
    [property: JsonPropertyName("verified")] bool    Verified,
    [property: JsonPropertyName("line")]     int?    Line,
    [property: JsonPropertyName("message")]  string? Message
);

internal sealed record SetBreakpointsResponseBody(
    [property: JsonPropertyName("breakpoints")] List<DapBreakpoint> Breakpoints
);

internal sealed record DapSource(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string? Path
);

internal sealed record DapStackFrame(
    [property: JsonPropertyName("id")]     int        Id,
    [property: JsonPropertyName("name")]   string     Name,
    [property: JsonPropertyName("source")] DapSource? Source,
    [property: JsonPropertyName("line")]   int        Line,
    [property: JsonPropertyName("column")] int        Column
);

internal sealed record StackTraceResponseBody(
    [property: JsonPropertyName("stackFrames")] List<DapStackFrame> StackFrames,
    [property: JsonPropertyName("totalFrames")] int                 TotalFrames
);

internal sealed record DapScope(
    [property: JsonPropertyName("name")]               string Name,
    [property: JsonPropertyName("variablesReference")] int    VariablesReference,
    [property: JsonPropertyName("expensive")]          bool   Expensive
);

internal sealed record ScopesResponseBody(
    [property: JsonPropertyName("scopes")] List<DapScope> Scopes
);

internal sealed record DapVariable(
    [property: JsonPropertyName("name")]               string Name,
    [property: JsonPropertyName("value")]              string Value,
    [property: JsonPropertyName("type")]               string Type,
    [property: JsonPropertyName("variablesReference")] int    VariablesReference
);

internal sealed record VariablesResponseBody(
    [property: JsonPropertyName("variables")] List<DapVariable> Variables
);

// ---------------------------------------------------------------------------
// Debugger — step engine + async TCP DAP server, no dedicated thread.
//
// PumpAsync() is called by the main thread (Program.cs) in its idle loop
// alongside the future Lua/CLI services. Returns true if a message was
// processed, false if idle — the caller sleeps only when nothing was done,
// avoiding timer allocations when messages are actively flowing.
// The emu thread (System.cs) is unaware of DAP — it waits on ResumeEvent
// while Debugger.debugging is true, which is set/cleared by DAP commands.
// ---------------------------------------------------------------------------
public static class Debugger {

    internal struct SourceAddress {
        internal string fp;
        internal int    line;
    }

    // -----------------------------------------------------------------------
    // Called once at startup to begin listening. No thread is created.
    // -----------------------------------------------------------------------
    internal static void BeginDebugging(API.Debugging.IDebugFile<int> debugFile) {
        _debugFile = debugFile;

        foreach (var kv in debugFile.Lines)
            SourceCodeReferences.TryAdd(kv.Key, new SourceAddress { fp = kv.Value.fp, line = kv.Value.line });

        Console.WriteLine($"[DAP] Mapped {SourceCodeReferences.Count} source lines");

        _listener   = new TcpListener(IPAddress.Loopback, DapPort);
        _listener.Start();
        _acceptTask = _listener.AcceptTcpClientAsync();
        Console.WriteLine($"[DAP] Listening on 127.0.0.1:{DapPort}");
        Console.WriteLine($"[DAP] Waiting for IDE...");

        // Block until configurationDone — ensures the handshake completes
        // before the emu thread starts.
        while (!_readyEvent.IsSet) {
            if (!PumpAsync().GetAwaiter().GetResult())
                global::System.Threading.Thread.Sleep(10);
        }

        // Grace period: keep pumping briefly after configurationDone so any
        // setBreakpoints the IDE sends immediately after are processed before
        // the emu thread starts executing.
        var grace = global::System.Diagnostics.Stopwatch.StartNew();
        while (grace.ElapsedMilliseconds < 200) {
            if (!PumpAsync().GetAwaiter().GetResult())
                global::System.Threading.Thread.Sleep(10);
        }

        Console.WriteLine($"[DAP] IDE ready");
    }

    // -----------------------------------------------------------------------
    // Called by the main thread each iteration of its idle loop (Program.cs).
    // Returns true if a DAP message was processed, false if nothing was ready.
    // The caller sleeps via Thread.Sleep only on false — no timer allocations
    // occur when messages are flowing, no spin occurs when idle.
    // -----------------------------------------------------------------------
    internal static async Task<bool> PumpAsync() {
        if (_acceptTask is null) return false;
        if (_acceptTask is { IsCompleted: true }) {
            try {
                var client  = await _acceptTask;
                _stream     = client.GetStream();
                _seq        = 0;
                Console.WriteLine("[DAP] Client connected");
            } catch (Exception ex) {
                Console.WriteLine($"[DAP] Accept error: {ex.Message}");
            }
            _acceptTask = _listener!.AcceptTcpClientAsync();
        }

        if (_stream is null || !_stream.DataAvailable) return false;

        try {
            var msg = await ReadMessageAsync(_stream);
            if (msg is null) { Disconnect(); return false; }
            await DispatchAsync(msg);
            return true;
        } catch (Exception ex) when (ex is IOException or SocketException) {
            Console.WriteLine("[DAP] Client disconnected");
            Disconnect();
            return false;
        } catch (Exception ex) {
            Console.WriteLine($"[DAP] Error: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Step engine
    // -----------------------------------------------------------------------

    internal static void StepOnce() {
        Step(notifyStep: true);
        Renderer.Present();
    }

    internal static void StepOver() {
        if (System.Register.IR is 0x20 /* jsr */) {
            _lastSp = System.Register.S;
            while (System.Register.S != _lastSp) {
                if (Step(notifyStep: false)) return;    // breakpoint hit; Step() already notified
            }
        } else if (_lastLineNumber > _currentLineNumber) {
            while (_lastLineNumber < _currentLineNumber) {
                if (Step(notifyStep: false)) return;
            }
        } else {
            Step(notifyStep: false);
        }
        NotifyStopped("step");
        Renderer.Present();
    }

    // Returns true if a breakpoint was hit (stopped event already sent).
    // notifyStep: true  → fire stopped("step") on the no-breakpoint path,
    //             false → caller (StepOver) sends the event after the loop.
    private static bool Step(bool notifyStep) {
        if (System.cycle is 0) {
            _lastLineNumber    = _currentLineNumber;
            _currentRomAddress = Program.Cartridge.GetROMLocation(System.PC);
            _currentLineNumber = SourceCodeReferences[_currentRomAddress].line;
        }

        System.Step();
        ++System.virtualTime;   // bump virtual time (emu thread sleeping while debugging)

        bool breakpointHit;
        lock (_breakPointLock) {
            var bp = BreakPoints.Find(t => t!.Value.pos == _currentLineNumber);
            if (bp is not null && bp.Value.expr is not null)
                RefreshSymbols(_currentRomAddress);
            if (bp is not null) {
                if (bp.Value.expr is null) {
                    breakpointHit = true;
                } else {
                    var expr = bp.Value.expr;
                    breakpointHit = !SER.TryEvaluate(ref expr, out var result, Symbols) || result is not 0;
                }
            } else {
                breakpointHit = false;
            }
        }

        if (breakpointHit) {
            Console.WriteLine($"[IDE] Breakpoint hit at ${System.PC:X4} line {_currentLineNumber}");
            NotifyStopped("breakpoint");
            return true;
        }

        if (notifyStep) NotifyStopped("step");
        return false;
    }

    // -----------------------------------------------------------------------
    // DAP message framing
    // -----------------------------------------------------------------------

    private static async Task<DapMessage?> ReadMessageAsync(NetworkStream stream) {
        var headerBuf = new byte[4096];
        int headerLen = 0;

        while (true) {
            int b = stream.ReadByte();
            if (b < 0) return null;
            headerBuf[headerLen++] = (byte)b;
            if (headerLen                   >= 4
                && headerBuf[headerLen - 4] == '\r'
                && headerBuf[headerLen - 3] == '\n'
                && headerBuf[headerLen - 2] == '\r'
                && headerBuf[headerLen - 1] == '\n')
                break;
        }

        var header = Encoding.ASCII.GetString(headerBuf, 0, headerLen);
        var clLine = header.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault(l => l.StartsWith("Content-Length:",
                                StringComparison.OrdinalIgnoreCase));
        if (clLine is null) return null;

        var contentLen = int.Parse(clLine.Split(':')[1].Trim());
        var bodyBytes  = new byte[contentLen];
        var read       = 0;
        while (read < contentLen)
            read += await stream.ReadAsync(bodyBytes.AsMemory(read, contentLen - read));

        return JsonSerializer.Deserialize(bodyBytes, DapJsonContext.Default.DapMessage);
    }

    // -----------------------------------------------------------------------
    // DAP request dispatch
    // -----------------------------------------------------------------------

    private static async Task DispatchAsync(DapMessage msg) {
        switch (msg.Command) {
            case "attach": {
                var args = msg.Body?.Deserialize(DapJsonContext.Default.AttachRequestArguments);
                Console.WriteLine($"[IDE] Attach from {args?.Host ?? "unknown"}:{args?.Port?.ToString() ?? "?"}");
                await WriteRawResponseAsync(msg, null);
                break;
            }

            case "launch":
                Console.WriteLine("[IDE] Launch (treated as attach)");
                await WriteRawResponseAsync(msg, null);
                break;

            case "initialize":
                Console.WriteLine("[IDE] Handshake: initialize");
                await WriteRawResponseAsync(msg,
                    JsonSerializer.SerializeToElement(
                        new CapabilitiesBody(true, true, true),
                        DapJsonContext.Default.CapabilitiesBody));
                await WriteEventAsync("initialized",
                    JsonSerializer.SerializeToElement(
                        new InitializedEventBody(),
                        DapJsonContext.Default.InitializedEventBody));
                Console.WriteLine("[IDE] Handshake: initialized event sent");
                break;

            case "configurationDone":
                Console.WriteLine("[IDE] Handshake complete");
                debugging = false;
                _readyEvent.Set();
                await WriteRawResponseAsync(msg, null);
                break;

            case "setBreakpoints": {
                if (msg.Body is not { } bodyEl) { await WriteRawResponseAsync(msg, null); break; }
                var args = bodyEl.Deserialize(DapJsonContext.Default.SetBreakpointsRequestArguments);
                if (args is null)               { await WriteRawResponseAsync(msg, null); break; }

                var srcPath = args.Source.Path ?? args.Source.Name ?? string.Empty;
                Console.WriteLine($"[IDE] setBreakpoints received: src={srcPath} count={args.Breakpoints?.Count ?? 0}");
                Console.WriteLine($"[DAP] ide path resolved : {IO.Path.GetFullPath(srcPath)}");
                if (_debugFile?.Lines.Count > 0)
                    Console.WriteLine($"[DAP] dbg path sample  : {IO.Path.GetFullPath(_debugFile.Lines.First().Value.fp)}");

                lock (_breakPointLock) {
                    BreakPoints.RemoveAll(bp => bp is not null
                        && ResolveSourceForLine(bp.Value.pos) == srcPath);
                }

                var results = new List<DapBreakpoint>();
                foreach (var src in args.Breakpoints ?? []) {
                    bool verified     = false;
                    int  resolvedLine = src.Line;

                    if (_debugFile is not null) {
                        var match = _debugFile.Lines.FirstOrDefault(kv =>
                            string.Equals(IO.Path.GetFullPath(kv.Value.fp),
                                          IO.Path.GetFullPath(srcPath),
                                          StringComparison.OrdinalIgnoreCase)
                            && kv.Value.line == src.Line);
                        verified     = match.Value is not null;
                        resolvedLine = verified ? match.Value!.line : src.Line;
                    }

                    if (verified || _debugFile is null) {
                        lock (_breakPointLock) { BreakPoints.Add((resolvedLine, src.Condition)); }
                        results.Add(new DapBreakpoint(Verified: true,  Line: resolvedLine, Message: null));
                        var cond = src.Condition is not null ? $" condition={src.Condition}" : string.Empty;
                        Console.WriteLine($"[IDE] Breakpoint set: {IO.Path.GetFileName(srcPath)}:{resolvedLine}{cond}");
                    } else {
                        results.Add(new DapBreakpoint(Verified: false, Line: src.Line, Message: "No code at this line"));
                        Console.WriteLine($"[IDE] Breakpoint rejected: {IO.Path.GetFileName(srcPath)}:{src.Line} (no code at line)");
                    }
                }

                await WriteRawResponseAsync(msg,
                    JsonSerializer.SerializeToElement(
                        new SetBreakpointsResponseBody(results),
                        DapJsonContext.Default.SetBreakpointsResponseBody));
                break;
            }

            case "setExceptionBreakpoints":
                Console.WriteLine("[IDE] setExceptionBreakpoints (no-op)");
                await WriteRawResponseAsync(msg, null);
                break;

            case "threads":
                await WriteFrameAsync(JsonSerializer.Serialize(
                    new DapResponse(NextSeq(), "response", msg.Seq, true, "threads",
                        JsonDocument.Parse("""{"threads":[{"id":1,"name":"6502"}]}""").RootElement),
                    DapJsonContext.Default.DapResponse));
                break;

            case "stackTrace": {
                var sa    = SourceCodeReferences[Program.Cartridge.GetROMLocation(System.PC)];
                var frame = new DapStackFrame(1, $"${System.PC:X4}",
                    new DapSource(IO.Path.GetFileName(sa.fp), sa.fp), sa.line, 1);
                await WriteRawResponseAsync(msg,
                    JsonSerializer.SerializeToElement(
                        new StackTraceResponseBody([frame], TotalFrames: 1),
                        DapJsonContext.Default.StackTraceResponseBody));
                break;
            }

            case "scopes":
                await WriteRawResponseAsync(msg,
                    JsonSerializer.SerializeToElement(
                        new ScopesResponseBody([new DapScope("CPU Registers", 1, false)]),
                        DapJsonContext.Default.ScopesResponseBody));
                break;

            case "variables": {
                var args = msg.Body?.Deserialize(DapJsonContext.Default.VariablesRequestArguments);
                var vars = args?.VariablesReference is 1 ? BuildRegisterVariables() : [];
                await WriteRawResponseAsync(msg,
                    JsonSerializer.SerializeToElement(
                        new VariablesResponseBody(vars),
                        DapJsonContext.Default.VariablesResponseBody));
                break;
            }

            case "next":
                Console.WriteLine($"[IDE] Step over at ${System.PC:X4}");
                await WriteRawResponseAsync(msg, null);
                StepOver();
                break;

            case "stepIn":
                Console.WriteLine($"[IDE] Step into at ${System.PC:X4}");
                await WriteRawResponseAsync(msg, null);
                StepOnce();
                break;

            case "continue":
                Console.WriteLine("[IDE] Continue");
                debugging = false;
                ResumeEvent.Set();
                await WriteRawResponseAsync(msg, null);
                await WriteEventAsync("continued", null);
                break;

            case "pause":
                Console.WriteLine($"[IDE] Pause requested at ${System.PC:X4}");
                debugging = true;
                await WriteRawResponseAsync(msg, null);
                NotifyStopped("pause");
                break;

            case "writeMemory": {
                var addr = msg.Body?.TryGetProperty("memoryReference", out var mr) is true ? mr.GetString() : "?";
                Console.WriteLine($"[IDE] Memory write requested at {addr} (not implemented)");
                await WriteRawResponseAsync(msg, null, success: false);
                break;
            }

            case "disconnect":
            case "terminate":
                Console.WriteLine("[IDE] Disconnected");
                debugging = false;
                ResumeEvent.Set();
                await WriteRawResponseAsync(msg, null);
                Disconnect();
                break;

            default:
                Console.WriteLine($"[IDE] Unhandled command: {msg.Command}");
                await WriteRawResponseAsync(msg, null, success: false);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Send helpers — all writes go through WriteFrameAsync
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Send helpers — all writes go through WriteFrameAsync.
    // Bodies are passed as JsonElement? — callers must serialize with the
    // specific typed serializer from DapJsonContext before passing here.
    // This avoids AOT-unsafe polymorphic object serialization entirely.
    // -----------------------------------------------------------------------

    private static Task WriteRawResponseAsync(DapMessage req, JsonElement? body, bool success = true) =>
        WriteFrameAsync(JsonSerializer.Serialize(
            new DapResponse(NextSeq(), "response", req.Seq, success,
                req.Command ?? string.Empty, body),
            DapJsonContext.Default.DapResponse));

    private static Task WriteEventAsync(string eventName, JsonElement? body) =>
        WriteFrameAsync(JsonSerializer.Serialize(
            new DapEvent(NextSeq(), "event", eventName, body),
            DapJsonContext.Default.DapEvent));

    private static void NotifyStopped(string reason) =>
        WriteEventAsync("stopped",
            JsonSerializer.SerializeToElement(
                new StoppedEventBody(reason, ThreadId: 1, AllThreadsStopped: true),
                DapJsonContext.Default.StoppedEventBody))
        .GetAwaiter().GetResult();

    private static async Task WriteFrameAsync(string json) {
        if (_stream is null) return;
        var body   = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _stream.WriteAsync(header.AsMemory());
        await _stream.WriteAsync(body.AsMemory());
        await _stream.FlushAsync();
    }

    private static int NextSeq() => Interlocked.Increment(ref _seq);

    // -----------------------------------------------------------------------
    // Register variables
    // -----------------------------------------------------------------------

    private static List<DapVariable> BuildRegisterVariables() {
        // P is not a real register field — derive it from the individual flag bools.
        var p = (byte)(
            (System.Register.n ? 1 : 0) << 7 |
            (System.Register.v ? 1 : 0) << 6 |
            1                           << 5 | // unused bit, always 1
            (System.Register.b ? 1 : 0) << 4 |
            (System.Register.d ? 1 : 0) << 3 |
            (System.Register.i ? 1 : 0) << 2 |
            (System.Register.z ? 1 : 0) << 1 |
            (System.Register.c ? 1 : 0) << 0
        );
        return [
            Reg("A",  $"${System.Register.AC:X2}",          "byte"),
            Reg("X",  $"${System.Register.X:X2}",           "byte"),
            Reg("Y",  $"${System.Register.Y:X2}",           "byte"),
            Reg("S",  $"${System.Register.S:X2}",           "byte"),
            Reg("PC", $"${System.PC:X4}",                   "word"),
            Reg("P",  $"${p:X2}",                           "byte"),
            Reg("N",  System.Register.n ? "1" : "0",        "bool"),
            Reg("V",  System.Register.v ? "1" : "0",        "bool"),
            Reg("B",  System.Register.b ? "1" : "0",        "bool"),
            Reg("D",  System.Register.d ? "1" : "0",        "bool"),
            Reg("I",  System.Register.i ? "1" : "0",        "bool"),
            Reg("Z",  System.Register.z ? "1" : "0",        "bool"),
            Reg("C",  System.Register.c ? "1" : "0",        "bool"),
        ];
    }

    private static DapVariable Reg(string name, string value, string type) =>
        new(name, value, type, VariablesReference: 0);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Resolves the symbol table visible at a given ROM address by binary
    // searching IDebugFile.Spans (sorted ascending by Start) for the
    // containing span, then reading its scope's symbol list.
    // Called before evaluating any conditional breakpoint expression.
    private static void RefreshSymbols(int romAddress) {
        Symbols.Clear();
        if (_debugFile is null) return;

        var spans = _debugFile.Spans;
        if (spans.Count is 0) return;

        // Binary search for the last span whose Start <= romAddress
        int lo = 0, hi = spans.Count - 1, found = -1;
        while (lo <= hi) {
            int mid = (lo + hi) / 2;
            if (spans[mid].Start <= romAddress) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (found < 0) return;
        var span = spans[found];
        if (romAddress >= span.Start + span.Length) return; // not actually inside this span

        foreach (var sym in span.Scope.symbols)
            Symbols[sym.name] = new SER.SerUnion<int>(sym.value);
    }

    private static string ResolveSourceForLine(int line) {
        foreach (var kv in SourceCodeReferences)
            if (kv.Value.line == line) return kv.Value.fp;
        return string.Empty;
    }

    private static void Disconnect() {
        try { _stream?.Close(); } catch { /* ignored */ }
        _stream = null;
    }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private static          Dictionary<string, SER.SerUnion<int>> Symbols              = [];
    private static          List<(int pos, string? expr)?>        BreakPoints          = [];
    private static readonly object                                 _breakPointLock      = new();
    private static readonly Dictionary<int, SourceAddress>        SourceCodeReferences = [];
    private static          int                                    _lastLineNumber;
    private static          int                                    _currentLineNumber;
    private static          int                                    _currentRomAddress;
    private static          byte                                   _lastSp;

    private static TcpListener?     _listener;
    private static Task<TcpClient>? _acceptTask;
    private static NetworkStream?   _stream;
    private static int              _seq;

    internal static API.Debugging.IDebugFile<int>? _debugFile;
    internal static bool          debugging;
    private  const  int           DapPort = 4711;

    // Signalled by the main thread when debugging is set to false,
    // so the emu thread wakes immediately rather than waiting up to 1s.
    internal static readonly ManualResetEventSlim ResumeEvent = new(false);
    private  static readonly ManualResetEventSlim _readyEvent = new(false);

    // -----------------------------------------------------------------------
    // Called from the emu thread at instruction fetch (cycle 0) during normal
    // emulation. Returns true if the address maps to an active breakpoint,
    // in which case the caller sets debugging = true.
    // Lock-guarded because BreakPoints may be written by the main thread
    // (setBreakpoints) concurrently — a structural race on List<T> would
    // corrupt the list, not just return a stale result.
    // Uses TryGetValue because during live emulation the CPU can legitimately
    // be at an address with no debug info (interrupt vectors, RAM, mapper regs).
    // -----------------------------------------------------------------------
    internal static bool CheckBreakpoint(ushort cpuAddress) {
        var romLocation = Program.Cartridge.GetROMLocation(cpuAddress);
        if (!SourceCodeReferences.TryGetValue(romLocation, out var sa)) return false;
        if (BreakPoints.Count > 0)
            Console.WriteLine($"[DAP] Mapped fetch cpu=${cpuAddress:X4} rom=${romLocation:X4} line={sa.line} file={IO.Path.GetFileName(sa.fp)}");
        lock (_breakPointLock) {
            var bp = BreakPoints.Find(t => t!.Value.pos == sa.line);
            if (bp is null) return false;
            if (bp.Value.expr is null) return true;     // unconditional
            var expr = bp.Value.expr;
            RefreshSymbols(romLocation);
            if (!SER.TryEvaluate(ref expr, out var result, Symbols)) return true;
            return result is not 0;
        }
    }
}

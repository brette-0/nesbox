using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IO = System.IO;

namespace nesbox.Debug;
using SER = StringExpressionEvaluator.StringExpressionEvaluator;

// ---------------------------------------------------------------------------
// AOT-safe JSON source-generation context.
// ---------------------------------------------------------------------------
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
    [property: JsonPropertyName("seq")]         int     Seq,
    [property: JsonPropertyName("type")]        string  Type,
    [property: JsonPropertyName("request_seq")] int     RequestSeq,
    [property: JsonPropertyName("success")]     bool    Success,
    [property: JsonPropertyName("command")]     string  Command,
    [property: JsonPropertyName("body")]        object? Body
);

internal sealed record DapEvent(
    [property: JsonPropertyName("seq")]   int     Seq,
    [property: JsonPropertyName("type")]  string  Type,
    [property: JsonPropertyName("event")] string  EventName,
    [property: JsonPropertyName("body")]  object? Body
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
    [property: JsonPropertyName("reason")] string Reason
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
// alongside the future Lua/CLI services. The emu thread (System.cs) is
// unaware of DAP — it simply sleeps via Thread.Sleep(1000) while
// Debugger.debugging is true, which is set/cleared by DAP commands.
// ---------------------------------------------------------------------------
public static class Debugger {

    internal struct SourceAddress {
        internal string fp;
        internal int    line;
    }

    // -----------------------------------------------------------------------
    // Called once at startup to begin listening. No thread is created.
    // -----------------------------------------------------------------------
    internal static void BeginDebugging() {
        _listener   = new TcpListener(IPAddress.Loopback, DapPort);
        _listener.Start();
        _acceptTask = _listener.AcceptTcpClientAsync();
        Console.WriteLine($"[DAP] Listening on 127.0.0.1:{DapPort}");
    }

    // -----------------------------------------------------------------------
    // Called by the main thread each iteration of its idle loop (Program.cs).
    // Handles exactly one pending action per call:
    //   • an incoming connection, or
    //   • one DAP message from a connected client, or
    //   • a short yield if nothing is ready yet.
    // -----------------------------------------------------------------------
    internal static async Task PumpAsync() {
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

        if (_stream is null) {
            await Task.Delay(10);
            return;
        }

        try {
            if (!_stream.DataAvailable) {
                await Task.Delay(10);
                return;
            }

            var msg = await ReadMessageAsync(_stream);
            if (msg is null) { Disconnect(); return; }

            await DispatchAsync(msg);
        } catch (Exception ex) when (ex is IOException or SocketException) {
            Console.WriteLine("[DAP] Client disconnected");
            Disconnect();
        } catch (Exception ex) {
            Console.WriteLine($"[DAP] Error: {ex.Message}");
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
            _currentLineNumber = SourceCodeReferences[Program.Cartridge.GetROMLocation(System.PC)].line;
        }

        System.Step();
        ++System.virtualTime;   // bump virtual time (emu thread sleeping while debugging)

        if (BreakPoints.Find(t => t!.Value.pos == _currentLineNumber) is { } breakpoint) {
            if (breakpoint.expr is null) {
                Console.WriteLine($"[IDE] Breakpoint hit at ${System.PC:X4} line {_currentLineNumber}");
                NotifyStopped("breakpoint");
                return true;
            }
            if (!SER.TryEvaluate(ref breakpoint.expr, out var result, Symbols)) {
                Console.WriteLine($"[IDE] Breakpoint hit at ${System.PC:X4} line {_currentLineNumber}");
                NotifyStopped("breakpoint");
                return true;
            }
            if (result is not 0) {
                Console.WriteLine($"[IDE] Breakpoint hit at ${System.PC:X4} line {_currentLineNumber}");
                NotifyStopped("breakpoint");
                return true;
            }
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
            case "initialize":
                Console.WriteLine("[IDE] Handshake: initialize");
                await SendResponseAsync(msg, new CapabilitiesBody(
                    SupportsConditionalBreakpoints:        true,
                    SupportsConfigurationDoneRequest:      true,
                    SupportsSingleThreadExecutionRequests: true
                ));
                await SendEventAsync("initialized", new InitializedEventBody());
                Console.WriteLine("[IDE] Handshake: initialized event sent");
                break;

            case "configurationDone":
                Console.WriteLine("[IDE] Handshake complete, execution paused at entry");
                debugging = true;
                await SendResponseAsync(msg, null);
                NotifyStopped("entry");
                break;

            case "setBreakpoints": {
                if (msg.Body is not { } bodyEl) { await SendResponseAsync(msg, null); break; }
                var args = bodyEl.Deserialize(DapJsonContext.Default.SetBreakpointsRequestArguments);
                if (args is null)               { await SendResponseAsync(msg, null); break; }

                var srcPath = args.Source.Path ?? args.Source.Name ?? string.Empty;
                BreakPoints.RemoveAll(bp => bp is not null
                    && ResolveSourceForLine(bp.Value.pos) == srcPath);

                var results = new List<DapBreakpoint>();
                foreach (var src in args.Breakpoints ?? []) {
                    bool verified     = false;
                    int  resolvedLine = src.Line;

                    if (_debugFile is not null) {
                        var fileRec = _debugFile.Files.FirstOrDefault(f =>
                            string.Equals(Path.GetFullPath(f.Name),
                                          Path.GetFullPath(srcPath),
                                          StringComparison.OrdinalIgnoreCase));

                        var lineRec = _debugFile.Lines.FirstOrDefault(
                            l => l.File == fileRec.Id && l.Line == src.Line);

                        verified     = lineRec.Id >= 0;
                        resolvedLine = (int)lineRec.Line;
                    }

                    if (verified || _debugFile is null) {
                        BreakPoints.Add((resolvedLine, src.Condition));
                        results.Add(new DapBreakpoint(Verified: true,  Line: resolvedLine, Message: null));
                        var cond = src.Condition is not null ? $" condition={src.Condition}" : string.Empty;
                        Console.WriteLine($"[IDE] Breakpoint set: {Path.GetFileName(srcPath)}:{resolvedLine}{cond}");
                    } else {
                        results.Add(new DapBreakpoint(Verified: false, Line: src.Line, Message: "No code at this line"));
                        Console.WriteLine($"[IDE] Breakpoint rejected: {Path.GetFileName(srcPath)}:{src.Line} (no code at line)");
                    }
                }

                await SendResponseAsync(msg, new SetBreakpointsResponseBody(results));
                break;
            }

            case "threads":
                // DAP requires a threads response to complete the handshake.
                // The 6502 has no thread model — one CPU, one execution context.
                await WriteFrameAsync(JsonSerializer.Serialize(
                    new DapResponse(NextSeq(), "response", msg.Seq, true, "threads",
                        JsonDocument.Parse("""{"threads":[{"id":1,"name":"6502"}]}""").RootElement),
                    DapJsonContext.Default.DapResponse));
                break;

            case "stackTrace": {
                var sa    = SourceCodeReferences[Program.Cartridge.GetROMLocation(System.PC)];
                var frame = new DapStackFrame(
                    Id:     1,
                    Name:   $"${System.PC:X4}",
                    Source: new DapSource(Path.GetFileName(sa.fp), sa.fp),
                    Line:   sa.line,
                    Column: 1
                );
                await SendResponseAsync(msg, new StackTraceResponseBody([frame], TotalFrames: 1));
                break;
            }

            case "scopes":
                await SendResponseAsync(msg, new ScopesResponseBody([
                    new DapScope("CPU Registers", VariablesReference: 1, Expensive: false)
                ]));
                break;

            case "variables": {
                var args = msg.Body?.Deserialize(DapJsonContext.Default.VariablesRequestArguments);
                var vars = args?.VariablesReference is 1 ? BuildRegisterVariables() : [];
                await SendResponseAsync(msg, new VariablesResponseBody(vars));
                break;
            }

            case "next":
                Console.WriteLine($"[IDE] Step over at ${System.PC:X4}");
                await SendResponseAsync(msg, null);
                StepOver();
                break;

            case "stepIn":
                Console.WriteLine($"[IDE] Step into at ${System.PC:X4}");
                await SendResponseAsync(msg, null);
                StepOnce();
                break;

            case "continue":
                Console.WriteLine("[IDE] Continue");
                debugging = false;
                await SendResponseAsync(msg, null);
                await SendEventAsync("continued", null);
                break;

            case "pause":
                Console.WriteLine($"[IDE] Pause requested at ${System.PC:X4}");
                debugging = true;
                await SendResponseAsync(msg, null);
                NotifyStopped("pause");
                break;

            case "writeMemory": {
                var addr = msg.Body?.TryGetProperty("memoryReference", out var mr) is true ? mr.GetString() : "?";
                Console.WriteLine($"[IDE] Memory write requested at {addr} (not implemented)");
                await WriteFrameAsync(JsonSerializer.Serialize(
                    new DapResponse(NextSeq(), "response", msg.Seq, false, "writeMemory", null),
                    DapJsonContext.Default.DapResponse));
                break;
            }

            case "disconnect":
            case "terminate":
                Console.WriteLine("[IDE] Disconnected");
                debugging = false;
                await SendResponseAsync(msg, null);
                Disconnect();
                break;

            default:
                Console.WriteLine($"[IDE] Unhandled command: {msg.Command}");
                await WriteFrameAsync(JsonSerializer.Serialize(
                    new DapResponse(NextSeq(), "response", msg.Seq, false,
                                    msg.Command ?? string.Empty, null),
                    DapJsonContext.Default.DapResponse));
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Send helpers — all writes go through WriteFrameAsync
    // -----------------------------------------------------------------------

    private static Task SendResponseAsync(DapMessage req, object? body) =>
        WriteFrameAsync(JsonSerializer.Serialize(
            new DapResponse(NextSeq(), "response", req.Seq, true,
                req.Command ?? string.Empty, body),
            DapJsonContext.Default.DapResponse));

    private static Task SendEventAsync(string eventName, object? body) =>
        WriteFrameAsync(JsonSerializer.Serialize(
            new DapEvent(NextSeq(), "event", eventName, body),
            DapJsonContext.Default.DapEvent));

    // NotifyStopped is called from the synchronous step engine, which is itself
    // called from DispatchAsync on the main thread — so GetAwaiter().GetResult()
    // is safe here, there is no async context above us to deadlock against.
    private static void NotifyStopped(string reason) =>
        SendEventAsync("stopped", new StoppedEventBody(reason))
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
    private static readonly Dictionary<int, SourceAddress>        SourceCodeReferences = [];
    private static          int                                    _lastLineNumber;
    private static          int                                    _currentLineNumber;
    private static          byte                                   _lastSp;

    private static TcpListener?     _listener;
    private static Task<TcpClient>? _acceptTask;
    private static NetworkStream?   _stream;
    private static int              _seq;

    internal static Ld65Dbg<int>? _debugFile;
    internal static bool          debugging;
    private  const  int           DapPort = 4711;
}

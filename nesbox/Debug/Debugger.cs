using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nesbox.Debug;
using SER = StringExpressionEvaluator.StringExpressionEvaluator;
using IO  = global::System.IO;

// ---------------------------------------------------------------------------
// AOT-safe JSON source-generation context — incoming messages only.
// All outgoing messages use Utf8JsonWriter directly.
// ---------------------------------------------------------------------------
[JsonSerializable(typeof(DapMessage))]
internal partial class DapJsonContext : JsonSerializerContext { }

internal sealed record DapMessage(
    [property: JsonPropertyName("seq")]       int          Seq,
    [property: JsonPropertyName("type")]      string       Type,
    [property: JsonPropertyName("command")]   string?      Command,
    [property: JsonPropertyName("event")]     string?      Event,
    [property: JsonPropertyName("arguments")] JsonElement? Body
);

// ---------------------------------------------------------------------------
// Debugger
// ---------------------------------------------------------------------------
public static class Debugger {

    internal struct SourceAddress {
        internal string fp   = string.Empty;
        internal int    line = 0;
        internal SourceAddress(string fp, int line) { this.fp = fp; this.line = line; }
    }

    // -----------------------------------------------------------------------
    // BeginDebugging — blocks until configurationDone so breakpoints are
    // registered before the emu thread starts.
    // -----------------------------------------------------------------------
    internal static void BeginDebugging(API.Debugging.IDebugFile<int> debugFile) {
        _debugFile = debugFile;

        foreach (var kv in debugFile.Lines)
            SourceCodeReferences.TryAdd(kv.Key, new SourceAddress(kv.Value.fp, kv.Value.line));

        Console.WriteLine($"[DAP] Mapped {SourceCodeReferences.Count} source lines");

        _listener   = new TcpListener(IPAddress.Loopback, DapPort);
        _listener.Start();
        _acceptTask = _listener.AcceptTcpClientAsync();
        Console.WriteLine($"[DAP] Listening on 127.0.0.1:{DapPort}");
        Console.WriteLine($"[DAP] Waiting for IDE...");

        while (!_readyEvent.IsSet) {
            if (!PumpAsync().GetAwaiter().GetResult())
                global::System.Threading.Thread.Sleep(10);
        }

        Console.WriteLine($"[DAP] IDE ready");
    }

    // -----------------------------------------------------------------------
    // PumpAsync — called by the main loop every iteration.
    // -----------------------------------------------------------------------
    internal static async Task<bool> PumpAsync() {
        if (_acceptTask is null) return false;
        if (_acceptTask is { IsCompleted: true }) {
            try {
                var client = await _acceptTask;
                _stream    = client.GetStream();
                _seq       = 0;
                Console.WriteLine("[DAP] Client connected");
            } catch (Exception ex) {
                Console.WriteLine($"[DAP] Accept error: {ex.Message}");
            }
            _acceptTask = _listener!.AcceptTcpClientAsync();
        }

        // Emu thread sets _pendingStop when a breakpoint fires during live emulation.
        // Main thread sends the stopped event — properly awaited.
        var pending = _pendingStop;
        if (pending is not null) {
            _pendingStop = null;
            Console.WriteLine($"[IDE] {pending} — notifying IDE");
            await WriteStoppedEventAsync(pending);
            return true;
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
    // Step engine — called from main thread via DAP next/stepIn commands.
    // -----------------------------------------------------------------------

    internal static async Task StepOnceAsync() {
        Step();
        Renderer.Present();
        await WriteStoppedEventAsync("step");
    }

    internal static async Task StepOverAsync() {
        if (System.Register.IR is 0x20 /* jsr */) {
            _lastSp = System.Register.S;
            while (System.Register.S != _lastSp) {
                if (StepCheckBreak()) return;
            }
        } else if (_lastLineNumber > _currentLineNumber) {
            while (_lastLineNumber < _currentLineNumber) {
                if (StepCheckBreak()) return;
            }
        } else {
            StepCheckBreak();
        }
        Renderer.Present();
        await WriteStoppedEventAsync("step");
    }

    // Returns true if a breakpoint was hit during stepping (stopped event already queued).
    private static bool StepCheckBreak() {
        Step();
        bool hit;
        lock (_breakPointLock) {
            var bp = BreakPoints.Find(t => t!.Value.pos == _currentLineNumber);
            if (bp is null) { hit = false; }
            else if (bp.Value.expr is null) { hit = true; }
            else {
                var expr = bp.Value.expr;
                RefreshSymbols(_currentRomAddress);
                hit = !SER.TryEvaluate(ref expr, out var result, Symbols) || result is not 0;
            }
        }
        if (hit) {
            _pendingStop = "breakpoint";
            Renderer.Present();
        }
        return hit;
    }

    private static void Step() {
        if (System.cycle is 0) {
            _lastLineNumber    = _currentLineNumber;
            _currentRomAddress = Program.Cartridge.GetROMLocation(System.PC);
            if (SourceCodeReferences.TryGetValue(_currentRomAddress, out var sa))
                _currentLineNumber = sa.line;
        }
        System.Step();
        ++System.virtualTime;
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
            if (headerLen >= 4
                && headerBuf[headerLen - 4] == '\r'
                && headerBuf[headerLen - 3] == '\n'
                && headerBuf[headerLen - 2] == '\r'
                && headerBuf[headerLen - 1] == '\n')
                break;
        }

        var header     = Encoding.ASCII.GetString(headerBuf, 0, headerLen);
        var clLine     = header.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault(l => l.StartsWith("Content-Length:",
                                    StringComparison.OrdinalIgnoreCase));
        if (clLine is null) return null;

        var contentLen = int.Parse(clLine.Split(':')[1].Trim());
        var bodyBytes  = new byte[contentLen];
        var read       = 0;
        while (read < contentLen)
            read += await stream.ReadAsync(bodyBytes.AsMemory(read, contentLen - read));

        Console.WriteLine($"[DAP RX] {Encoding.UTF8.GetString(bodyBytes)}");
        return JsonSerializer.Deserialize(bodyBytes, DapJsonContext.Default.DapMessage);
    }

    // -----------------------------------------------------------------------
    // DAP request dispatch
    // -----------------------------------------------------------------------

    private static async Task DispatchAsync(DapMessage msg) {
        switch (msg.Command) {
            case "attach":
            case "launch":
                Console.WriteLine($"[IDE] {msg.Command}");
                await WriteRawResponseAsync(msg, null);
                break;

            case "initialize":
                Console.WriteLine("[IDE] Handshake: initialize");
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteBoolean("supportsConditionalBreakpoints",        true);
                    w.WriteBoolean("supportsConfigurationDoneRequest",      true);
                    w.WriteBoolean("supportsSingleThreadExecutionRequests", true);
                }));
                await WriteEventAsync("initialized", null);
                Console.WriteLine("[IDE] initialized event sent");
                break;

            case "configurationDone":
                Console.WriteLine("[IDE] Handshake complete");
                _readyEvent.Set();
                await WriteRawResponseAsync(msg, null);
                break;

            case "setBreakpoints": {
                if (msg.Body is not { } body) { await WriteRawResponseAsync(msg, null); break; }

                var srcPath = body.TryGetProperty("source", out var src)
                    ? (src.TryGetProperty("path", out var p) ? p.GetString() : null)
                      ?? (src.TryGetProperty("name", out var n) ? n.GetString() : null)
                      ?? string.Empty
                    : string.Empty;

                var srcFile = IO.Path.GetFileName(srcPath);
                Console.WriteLine($"[IDE] setBreakpoints: file={srcFile}");

                _idePaths[srcFile] = srcPath;

                lock (_breakPointLock) {
                    BreakPoints.RemoveAll(bp => bp is not null
                        && string.Equals(
                            IO.Path.GetFileName(ResolveSourceForLine(bp.Value.pos)),
                            srcFile,
                            StringComparison.OrdinalIgnoreCase));
                }

                var bpResults = new List<(bool verified, int line, string? message)>();

                if (body.TryGetProperty("breakpoints", out var bpArr)) {
                    foreach (var bp in bpArr.EnumerateArray()) {
                        int     line      = bp.TryGetProperty("line",      out var l) ? l.GetInt32()  : 0;
                        string? condition = bp.TryGetProperty("condition", out var c) ? c.GetString() : null;
                        bool    verified  = false;
                        int     resolved  = line;

                        if (_debugFile is not null) {
                            var match = _debugFile.Lines.FirstOrDefault(kv =>
                                string.Equals(IO.Path.GetFileName(kv.Value.fp),
                                              srcFile,
                                              StringComparison.OrdinalIgnoreCase)
                                && kv.Value.line == line);
                            verified = match.Value is not null;
                            resolved = verified ? match.Value!.line : line;
                        }

                        if (verified || _debugFile is null) {
                            lock (_breakPointLock) { BreakPoints.Add((resolved, condition)); }
                            bpResults.Add((true, resolved, null));
                            Console.WriteLine($"[IDE] Breakpoint set: {srcFile}:{resolved}");
                        } else {
                            bpResults.Add((false, line, "No code at this line"));
                            Console.WriteLine($"[IDE] Breakpoint rejected: {srcFile}:{line}");
                        }
                    }
                }

                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteStartArray("breakpoints");
                    foreach (var (verified, line, message) in bpResults) {
                        w.WriteStartObject();
                        w.WriteBoolean("verified", verified);
                        w.WriteNumber("line",      line);
                        if (message is not null) w.WriteString("message", message);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }));
                break;
            }

            case "setExceptionBreakpoints":
                await WriteRawResponseAsync(msg, null);
                break;

            case "threads":
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteStartArray("threads");
                    w.WriteStartObject();
                    w.WriteNumber("id",   1);
                    w.WriteString("name", "6502");
                    w.WriteEndObject();
                    w.WriteEndArray();
                }));
                break;

            case "stackTrace": {
                SourceCodeReferences.TryGetValue(_currentRomAddress, out var sa);
                var fileName = IO.Path.GetFileName(sa.fp);
                var fullPath = _idePaths.TryGetValue(fileName, out var ip) ? ip : sa.fp;
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteStartArray("stackFrames");
                    w.WriteStartObject();
                    w.WriteNumber("id",     1);
                    w.WriteString("name",   $"${System.PC:X4}");
                    w.WriteNumber("line",   sa.line);
                    w.WriteNumber("column", 1);
                    w.WriteStartObject("source");
                    w.WriteString("name",   fileName);
                    w.WriteString("path",   fullPath);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteNumber("totalFrames", 1);
                }));
                break;
            }

            case "scopes":
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteStartArray("scopes");
                    w.WriteStartObject();
                    w.WriteString("name",               "CPU Registers");
                    w.WriteNumber("variablesReference", 1);
                    w.WriteBoolean("expensive",         false);
                    w.WriteEndObject();
                    w.WriteEndArray();
                }));
                break;

            case "variables": {
                int varRef = msg.Body?.TryGetProperty("variablesReference", out var vr) is true ? vr.GetInt32() : 0;
                var vars   = varRef is 1 ? BuildRegisterVariables() : [];
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteStartArray("variables");
                    foreach (var v in vars) {
                        w.WriteStartObject();
                        w.WriteString("name",               v.Name);
                        w.WriteString("value",              v.Value);
                        w.WriteString("type",               v.Type);
                        w.WriteNumber("variablesReference", v.VariablesReference);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }));
                break;
            }

            case "next":
                Console.WriteLine($"[IDE] Step over at ${System.PC:X4}");
                await WriteRawResponseAsync(msg, null);
                await StepOverAsync();
                break;

            case "stepIn":
                Console.WriteLine($"[IDE] Step into at ${System.PC:X4}");
                await WriteRawResponseAsync(msg, null);
                await StepOnceAsync();
                break;

            case "continue":
                Console.WriteLine("[IDE] Continue");
                debugging = false;
                ResumeEvent.Set();
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteBoolean("allThreadsContinued", true);
                }));
                await WriteEventAsync("continued", BuildJson(w => {
                    w.WriteNumber("threadId",            1);
                    w.WriteBoolean("allThreadsContinued", true);
                }));
                break;

            case "pause":
                Console.WriteLine($"[IDE] Pause at ${System.PC:X4}");
                debugging = true;
                await WriteRawResponseAsync(msg, null);
                await WriteStoppedEventAsync("pause");
                break;

            case "writeMemory":
                await WriteRawResponseAsync(msg, null, success: false);
                break;

            case "disconnect":
            case "terminate":
                Console.WriteLine("[IDE] Disconnected");
                debugging = false;
                ResumeEvent.Set();
                await WriteRawResponseAsync(msg, null);
                Disconnect();
                break;

            default:
                Console.WriteLine($"[IDE] Unhandled: {msg.Command}");
                await WriteRawResponseAsync(msg, null, success: false);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Send helpers — all output via Utf8JsonWriter, zero AOT reflection.
    // -----------------------------------------------------------------------

    private static JsonElement BuildJson(Action<Utf8JsonWriter> write) {
        var ms = new global::System.IO.MemoryStream();
        var w  = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        write(w);
        w.WriteEndObject();
        w.Flush();
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    private static Task WriteRawResponseAsync(DapMessage req, JsonElement? body, bool success = true) {
        var ms = new global::System.IO.MemoryStream();
        var w  = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteNumber("seq",         NextSeq());
        w.WriteString("type",        "response");
        w.WriteNumber("request_seq", req.Seq);
        w.WriteBoolean("success",    success);
        w.WriteString("command",     req.Command ?? string.Empty);
        if (body is { } b) { w.WritePropertyName("body"); b.WriteTo(w); }
        else                  w.WriteNull("body");
        w.WriteEndObject();
        w.Flush();
        return WriteFrameAsync(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static Task WriteEventAsync(string eventName, JsonElement? body) {
        var ms = new global::System.IO.MemoryStream();
        var w  = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteNumber("seq",   NextSeq());
        w.WriteString("type",  "event");
        w.WriteString("event", eventName);
        if (body is { } b) { w.WritePropertyName("body"); b.WriteTo(w); }
        else                  w.WriteNull("body");
        w.WriteEndObject();
        w.Flush();
        return WriteFrameAsync(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static Task WriteStoppedEventAsync(string reason) =>
        WriteEventAsync("stopped", BuildJson(w => {
            w.WriteString("reason",             reason);
            w.WriteNumber("threadId",           1);
            w.WriteBoolean("allThreadsStopped", true);
            w.WriteBoolean("preserveFocusHint", false);
        }));

    private static async Task WriteFrameAsync(string json) {
        if (_stream is null) return;
        Console.WriteLine($"[DAP TX] {json}");
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

    private static List<(string Name, string Value, string Type, int VariablesReference)> BuildRegisterVariables() {
        var p = (byte)(
            (System.Register.n ? 1 : 0) << 7 |
            (System.Register.v ? 1 : 0) << 6 |
            1                           << 5 |
            (System.Register.b ? 1 : 0) << 4 |
            (System.Register.d ? 1 : 0) << 3 |
            (System.Register.i ? 1 : 0) << 2 |
            (System.Register.z ? 1 : 0) << 1 |
            (System.Register.c ? 1 : 0) << 0
        );
        return [
            ("A",  $"${System.Register.AC:X2}", "byte", 0),
            ("X",  $"${System.Register.X:X2}",  "byte", 0),
            ("Y",  $"${System.Register.Y:X2}",  "byte", 0),
            ("S",  $"${System.Register.S:X2}",  "byte", 0),
            ("PC", $"${System.PC:X4}",           "word", 0),
            ("P",  $"${p:X2}",                  "byte", 0),
            ("N",  System.Register.n ? "1" : "0", "bool", 0),
            ("V",  System.Register.v ? "1" : "0", "bool", 0),
            ("B",  System.Register.b ? "1" : "0", "bool", 0),
            ("D",  System.Register.d ? "1" : "0", "bool", 0),
            ("I",  System.Register.i ? "1" : "0", "bool", 0),
            ("Z",  System.Register.z ? "1" : "0", "bool", 0),
            ("C",  System.Register.c ? "1" : "0", "bool", 0),
        ];
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void RefreshSymbols(int romAddress) {
        Symbols.Clear();
        if (_debugFile is null) return;
        var spans = _debugFile.Spans;
        if (spans.Count is 0) return;
        int lo = 0, hi = spans.Count - 1, found = -1;
        while (lo <= hi) {
            int mid = (lo + hi) / 2;
            if (spans[mid].Start <= romAddress) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return;
        var span = spans[found];
        if (romAddress >= span.Start + span.Length) return;
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

    private static          Dictionary<string, SER.SerUnion<int>>  Symbols              = [];
    private static          List<(int pos, string? expr)?>         BreakPoints          = [];
    private static readonly object                                  _breakPointLock      = new();
    private static readonly Dictionary<int, SourceAddress>         SourceCodeReferences = [];
    private static readonly Dictionary<string, string>             _idePaths            = [];
    private static          int                                     _lastLineNumber;
    private static          int                                     _currentLineNumber;
    private static          int                                     _currentRomAddress;
    private static          byte                                    _lastSp;

    private static TcpListener?     _listener;
    private static Task<TcpClient>? _acceptTask;
    private static NetworkStream?   _stream;
    private static int              _seq;

    internal static API.Debugging.IDebugFile<int>? _debugFile;
    internal static bool                            debugging;
    private  const  int                             DapPort = 4711;

    internal static readonly ManualResetEventSlim ResumeEvent  = new(false);
    private  static readonly ManualResetEventSlim _readyEvent  = new(false);

    // Set by CheckBreakpoint (emu thread) or StepCheckBreak (main thread).
    // Read and cleared by PumpAsync (main thread).
    private static volatile string? _pendingStop;

    // -----------------------------------------------------------------------
    // CheckBreakpoint — called from the emu thread at every instruction fetch.
    // -----------------------------------------------------------------------
    internal static bool CheckBreakpoint(ushort cpuAddress) {
        var romLocation = Program.Cartridge.GetROMLocation(cpuAddress);
        if (!SourceCodeReferences.TryGetValue(romLocation, out var sa)) return false;
        lock (_breakPointLock) {
            var bp = BreakPoints.Find(t => t!.Value.pos == sa.line);
            if (bp is null) return false;
            bool hit;
            if (bp.Value.expr is null) {
                hit = true;
            } else {
                var expr = bp.Value.expr;
                RefreshSymbols(romLocation);
                hit = !SER.TryEvaluate(ref expr, out var result, Symbols) || result is not 0;
            }
            if (hit) {
                _currentRomAddress = romLocation;
                _currentLineNumber = sa.line;
                _pendingStop       = "breakpoint";
            }
            return hit;
        }
    }
}

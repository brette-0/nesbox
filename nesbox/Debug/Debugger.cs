using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nesbox.Debug;
using IO = global::System.IO;

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
    // Safe memory reads — none of these trigger emulated hardware.
    //
    //  CpuPeek  — CPU address space, no side-effects.
    //    · $0000–$1FFF  → System RAM (2 KB, mirrored).
    //    · $2000–$401F  → Hardware registers.  Reading PPU/APU registers has
    //                     side-effects and some are not yet implemented, so we
    //                     return the high byte of the address (open-bus style)
    //                     exactly as the user requested.
    //    · $4020–$FFFF  → Cartridge space.  ReadByte is marked [Pure] in the
    //                     ICartridge interface — no side-effects guaranteed.
    //
    //  ProgramPeek   — raw PRG-ROM byte array, bounds-safe via modulo.
    //  CharacterPeek — raw CHR-ROM byte array; returns 0 for CHR-RAM carts
    //                  that have no CHR-ROM.
    // -----------------------------------------------------------------------

    internal static byte CpuPeek(ushort address) {
        if (address < 0x2000)
            return System.Memory.SystemRAM[address & 0x7FF];

        if (address < 0x4020)
            // Hardware register range — return high byte (no side-effects).
            return (byte)(address >> 8);

        // Cartridge space — [Pure] read, no emulated side-effects.
        return Program.Cartridge.ReadByte(address);
    }

    internal static byte ProgramPeek(int index) {
        var rom = Program.Cartridge.ProgramROM;
        if (rom is null || rom.Length == 0) return 0;
        // Safe modulo for any (including negative) index.
        index = ((index % rom.Length) + rom.Length) % rom.Length;
        return rom[index];
    }

    internal static byte CharacterPeek(int index) {
        var chr = Program.Cartridge.CharacterROM;
        if (chr is null || chr.Length == 0) return 0;
        index = ((index % chr.Length) + chr.Length) % chr.Length;
        return chr[index];
    }

    // -----------------------------------------------------------------------
    // Register / flag resolver.
    // Returns null for any name the evaluator doesn't recognise so SER can
    // produce a proper "undefined identifier" error rather than silently
    // substituting 0.
    // -----------------------------------------------------------------------
    internal static int? ReadRegister(string name) => name.ToUpperInvariant() switch {
        "A"       => System.Register.AC,
        "X"       => System.Register.X,
        "Y"       => System.Register.Y,
        "S" or
        "SP"      => System.Register.S,
        "PC"      => System.PC,
        "P"       => (byte)(
                        (System.Register.c ? 1 : 0) << 0 |
                        (System.Register.z ? 1 : 0) << 1 |
                        (System.Register.i ? 1 : 0) << 2 |
                        (System.Register.d ? 1 : 0) << 3 |
                        (System.Register.b ? 1 : 0) << 4 |
                        1                           << 5 |   // unused bit, always 1
                        (System.Register.v ? 1 : 0) << 6 |
                        (System.Register.n ? 1 : 0) << 7),
        // Individual flags — 0 or 1.
        "N"       => System.Register.n ? 1 : 0,
        "V"       => System.Register.v ? 1 : 0,
        "B"       => System.Register.b ? 1 : 0,
        "D"       => System.Register.d ? 1 : 0,
        "I"       => System.Register.i ? 1 : 0,
        "Z"       => System.Register.z ? 1 : 0,
        "C"       => System.Register.c ? 1 : 0,
        _         => null
    };

    // -----------------------------------------------------------------------
    // EvalCondition — thin wrapper that delegates to IDebugFile.
    // Returns true (fire breakpoint) on any error so we fail safe.
    // -----------------------------------------------------------------------
    private static bool EvalCondition(string expr, int romAddress) {
        if (_debugFile is null) return true;
        return _debugFile.EvaluateCondition(
            expr,
            romAddress,
            CpuPeek,
            ProgramPeek,
            CharacterPeek,
            ReadRegister);
    }

    // -----------------------------------------------------------------------
    // BeginDebugging — blocks until configurationDone.
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

        var pending = _pendingStop;
        if (pending is not null) {
            _pendingStop = null;
            if (pending == "breakpoint") Console.WriteLine($"[BP] Hit at ${System.PC:X4}");
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
    // Step engine
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

    private static bool StepCheckBreak() {
        Step();
        bool hit;
        lock (_breakPointLock) {
            var bp = BreakPoints.Find(t => t!.Value.pos == _currentLineNumber);
            if (bp is null) {
                hit = false;
            } else if (bp.Value.expr is null) {
                hit = true;
            } else {
                hit = EvalCondition(bp.Value.expr, _currentRomAddress);
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

        return JsonSerializer.Deserialize(bodyBytes, DapJsonContext.Default.DapMessage);
    }

    // -----------------------------------------------------------------------
    // DAP request dispatch
    // -----------------------------------------------------------------------

    private static async Task DispatchAsync(DapMessage msg) {
        switch (msg.Command) {
            case "attach":
            case "launch":
                await WriteRawResponseAsync(msg, null);
                break;

            case "initialize":
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteBoolean("supportsConditionalBreakpoints",        true);
                    w.WriteBoolean("supportsConfigurationDoneRequest",      true);
                    w.WriteBoolean("supportsSingleThreadExecutionRequests", true);
                    w.WriteBoolean("supportsEvaluateForHovers",             true);
                    w.WriteBoolean("supportsReadMemoryRequest",             true);
                    w.WriteBoolean("supportsWriteMemoryRequest",            true);
                }));
                await WriteEventAsync("initialized", null);
                break;

            case "configurationDone":
                Console.WriteLine("[DAP] IDE ready — starting emulation");
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

                        // Validate the condition expression early via ValidateCondition.
                        // This catches both thrown exceptions AND SER returning false
                        // (which happens for unsupported operators such as '==').
                        string? conditionError = null;
                        if (condition is not null && _debugFile is not null) {
                            if (!_debugFile.ValidateCondition(condition, out conditionError))
                                Console.WriteLine($"[IDE] Condition validation failed: {conditionError}");
                        }

                        if (verified || _debugFile is null) {
                            lock (_breakPointLock) { BreakPoints.Add((resolved, condition)); }
                            bpResults.Add((conditionError is null, resolved, conditionError));
                            Console.WriteLine($"[BP] {srcFile}:{resolved}" +
                                              (condition is null ? "" : $" if ({condition})"));
                        } else {
                            bpResults.Add((false, line, "No code at this line"));
                            Console.WriteLine($"[BP] {srcFile}:{line} — no code at this line");
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
                await WriteRawResponseAsync(msg, null);
                await StepOverAsync();
                break;

            case "stepIn":
                await WriteRawResponseAsync(msg, null);
                await StepOnceAsync();
                break;

            case "continue":
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
                debugging = true;
                await WriteRawResponseAsync(msg, null);
                await WriteStoppedEventAsync("pause");
                break;

            case "evaluate": {
                var expression = msg.Body?.TryGetProperty("expression", out var ep) is true
                    ? ep.GetString() ?? string.Empty : string.Empty;

                // ── Assignment detection ──────────────────────────────────────
                // Check for   lhs = rhs   before touching SER (= is Scan/list in SER).
                // A lone '=' is one that is not preceded by !, > or < and not followed by =.
                var assignResult = TryHandleAssignment(expression.Trim());
                if (assignResult is not null) {
                    await WriteRawResponseAsync(msg, BuildJson(w => {
                        w.WriteString("result",             assignResult);
                        w.WriteNumber("variablesReference", 0);
                    }));
                    break;
                }

                // ── Expression evaluation ─────────────────────────────────────
                int? value = null;
                if (_debugFile is not null) {
                    value = _debugFile.EvaluateExpression(
                        expression, _currentRomAddress,
                        CpuPeek, ProgramPeek, CharacterPeek, ReadRegister);
                } else {
                    value = ReadRegister(expression.Trim());
                }

                if (value.HasValue) {
                    // Show decimal, hex byte, and hex word so the user gets context.
                    var v   = value.Value;
                    var res = $"{v}  (${(byte)v:X2})" + (v > 0xFF ? $"  (${v:X4})" : "");
                    await WriteRawResponseAsync(msg, BuildJson(w => {
                        w.WriteString("result",             res);
                        w.WriteNumber("variablesReference", 0);
                    }));
                } else {
                    await WriteRawResponseAsync(msg, BuildJson(w => {
                        w.WriteString("result",             $"Could not evaluate '{expression}'");
                        w.WriteNumber("variablesReference", 0);
                    }), success: false);
                }
                break;
            }

            case "readMemory": {
                if (msg.Body is not { } rmBody) { await WriteRawResponseAsync(msg, null, success: false); break; }
                var rmRef    = rmBody.TryGetProperty("memoryReference", out var rmr) ? rmr.GetString() : null;
                var rmOffset = rmBody.TryGetProperty("offset",          out var rmo) ? rmo.GetInt32()  : 0;
                var rmCount  = rmBody.TryGetProperty("count",           out var rmc) ? rmc.GetInt32()  : 0;

                if (!TryParseAddress(rmRef, out int rmBase) || rmCount <= 0) {
                    await WriteRawResponseAsync(msg, null, success: false);
                    break;
                }

                int rmStart = rmBase + rmOffset;
                var rmBytes = new byte[rmCount];
                for (int i = 0; i < rmCount; i++)
                    rmBytes[i] = CpuPeek((ushort)((rmStart + i) & 0xFFFF));

                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteString("address",        $"0x{rmStart:X}");
                    w.WriteNumber("unreadableBytes", 0);
                    w.WriteString("data",            Convert.ToBase64String(rmBytes));
                }));
                break;
            }

            case "writeMemory": {
                if (msg.Body is not { } wmBody) { await WriteRawResponseAsync(msg, null, success: false); break; }
                var wmRef    = wmBody.TryGetProperty("memoryReference", out var wmr) ? wmr.GetString() : null;
                var wmOffset = wmBody.TryGetProperty("offset",          out var wmo) ? wmo.GetInt32()  : 0;
                var wmData   = wmBody.TryGetProperty("data",            out var wmd) ? wmd.GetString() : null;

                if (!TryParseAddress(wmRef, out int wmBase) || wmData is null) {
                    await WriteRawResponseAsync(msg, null, success: false);
                    break;
                }

                byte[] wmBytes;
                try   { wmBytes = Convert.FromBase64String(wmData); }
                catch { await WriteRawResponseAsync(msg, null, success: false); break; }

                int wmStart   = wmBase + wmOffset;
                int wmWritten = 0;
                for (int i = 0; i < wmBytes.Length; i++) {
                    int cpuAddr = wmStart + i;
                    // Only write to System RAM ($0000–$1FFF) — safe, no hardware side-effects.
                    if (cpuAddr is >= 0 and < 0x2000) {
                        System.Memory.SystemRAM[cpuAddr & 0x7FF] = wmBytes[i];
                        wmWritten++;
                    }
                }

                Console.WriteLine($"[MEM] Write ${wmStart:X4} ×{wmWritten} bytes");
                await WriteRawResponseAsync(msg, BuildJson(w => {
                    w.WriteNumber("offset",       0);
                    w.WriteNumber("bytesWritten", wmWritten);
                }));
                break;
            }

            case "disconnect":
            case "terminate":
                debugging = false;
                ResumeEvent.Set();
                await WriteRawResponseAsync(msg, null);
                Disconnect();
                break;

            default:
                Console.WriteLine($"[DAP] Unhandled command: {msg.Command}");
                await WriteRawResponseAsync(msg, null, success: false);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Send helpers
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
        var body   = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _stream.WriteAsync(header.AsMemory());
        await _stream.WriteAsync(body.AsMemory());
        await _stream.FlushAsync();
    }

    private static int NextSeq() => Interlocked.Increment(ref _seq);

    // -----------------------------------------------------------------------
    // Register variables (IDE "CPU Registers" scope pane)
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

    // -----------------------------------------------------------------------
    // Assignment handler for the REPL.
    //
    //  Supported forms:
    //    A = expr          register / flag write
    //    cpu[$300] = expr  System RAM write (safe, no side-effects)
    //
    //  Returns a result string on success, null if this isn't an assignment
    //  (caller should fall through to expression evaluation).
    // -----------------------------------------------------------------------
    private static string? TryHandleAssignment(string s) {
        // Find a lone '=' — not preceded by !, >, < and not followed by =.
        int eq = -1;
        for (int i = 0; i < s.Length; i++) {
            if (s[i] != '=') continue;
            bool followedByEq  = i + 1 < s.Length && s[i + 1] == '=';
            bool precededBySym = i > 0 && (s[i - 1] is '!' or '>' or '<');
            if (!followedByEq && !precededBySym) { eq = i; break; }
        }
        if (eq < 0) return null;    // no assignment operator — caller evaluates

        var lhs = s[..eq].Trim();
        var rhs = s[(eq + 1)..].Trim();
        if (lhs.Length == 0 || rhs.Length == 0) return null;

        // Evaluate the RHS.  Use _currentRomAddress so scoped symbols resolve.
        int? rhsValue = _debugFile is not null
            ? _debugFile.EvaluateExpression(rhs, _currentRomAddress,
                CpuPeek, ProgramPeek, CharacterPeek, ReadRegister)
            : ReadRegister(rhs)   // fallback: maybe it's a register name
              ?? (TryParseAddress(rhs, out int parsed) ? parsed : null);

        if (rhsValue is null) return $"Cannot evaluate RHS '{rhs}'";

        int val = rhsValue.Value;

        // ── cpu[$addr] = value ──────────────────────────────────────────
        if (lhs.StartsWith("cpu[", StringComparison.OrdinalIgnoreCase) && lhs.EndsWith("]")) {
            var addrExpr = lhs[4..^1].Trim();
            int? addrVal = _debugFile is not null
                ? _debugFile.EvaluateExpression(addrExpr, _currentRomAddress,
                    CpuPeek, ProgramPeek, CharacterPeek, ReadRegister)
                : (TryParseAddress(addrExpr, out int ap) ? ap : null);

            if (addrVal is null) return $"Cannot evaluate address '{addrExpr}'";

            int addr = addrVal.Value & 0xFFFF;
            if (addr < 0x2000) {
                System.Memory.SystemRAM[addr & 0x7FF] = (byte)(val & 0xFF);
                return $"cpu[${addr:X4}] = ${val & 0xFF:X2}";
            }
            return $"Cannot write to ${addr:X4} — only System RAM ($0000–$1FFF) is writable";
        }

        // ── Register / flag write ─────────────────────────────────────────
        var result = WriteRegister(lhs, val);
        return result;
    }

    // Writes a value to a named CPU register or flag.
    // Returns a confirmation string on success, or an error string if the name
    // is not recognised.
    private static string WriteRegister(string name, int value) {
        switch (name.ToUpperInvariant()) {
            case "A":        System.Register.AC = (byte)(value & 0xFF); break;
            case "X":        System.Register.X  = (byte)(value & 0xFF); break;
            case "Y":        System.Register.Y  = (byte)(value & 0xFF); break;
            case "S": case "SP": System.Register.S = (byte)(value & 0xFF); break;
            case "PC":       System.PC = (ushort)(value & 0xFFFF); break;
            case "N":        System.Register.n = value != 0; break;
            case "V":        System.Register.v = value != 0; break;
            case "B":        System.Register.b = value != 0; break;
            case "D":        System.Register.d = value != 0; break;
            case "I":        System.Register.i = value != 0; break;
            case "Z":        System.Register.z = value != 0; break;
            case "C":        System.Register.c = value != 0; break;
            default:
                return $"Unknown register or target '{name}'. " +
                       $"Registers: A X Y S PC  Flags: N V B D I Z C  Memory: cpu[$addr]";
        }
        return $"{name.ToUpperInvariant()} = ${value & 0xFF:X2}";
    }

    // Parses a memory address string — accepts decimal, 0xHEX, or $HEX.
    private static bool TryParseAddress(string? s, out int address) {
        address = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], global::System.Globalization.NumberStyles.HexNumber, null, out address);
        if (s.StartsWith("$"))
            return int.TryParse(s[1..], global::System.Globalization.NumberStyles.HexNumber, null, out address);
        return int.TryParse(s, out address);
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

            bool hit = bp.Value.expr is null
                ? true                                           // unconditional
                : EvalCondition(bp.Value.expr, romLocation);    // conditional

            if (hit) {
                _currentRomAddress = romLocation;
                _currentLineNumber = sa.line;
                _pendingStop       = "breakpoint";
            }
            return hit;
        }
    }
}

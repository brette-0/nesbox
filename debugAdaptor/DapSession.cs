using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace dap;

public class DapSession(TcpClient client, StreamWriter log) {

    public async Task RunAsync() {
        using var reader = new StreamReader(_stream, leaveOpen: true);
        _writer = new StreamWriter(_stream, leaveOpen: true);
        _writer.AutoFlush = true;

        while (true) {
            var contentLength = await ReadHeaderAsync(reader);
            if (contentLength < 0) break;
            
            var buffer = new char[contentLength];
            await reader.ReadAsync(buffer, 0, contentLength);
            
            var json = new string(buffer);
            
            await _log.WriteLineAsync($"[IN] {json}");
            var response = await HandleMessageAsync(json);

            if (response is null) continue;
            var responseJson = JsonSerializer.Serialize(response);
            var message      = $"Content-Length: {responseJson.Length}\r\n\r\n{responseJson}";
            await _log.WriteLineAsync(message);
            await _writer.FlushAsync();
        }
    }

    private static async Task<int> ReadHeaderAsync(StreamReader reader) {
        var     contentLength = -1;
        while (await reader.ReadLineAsync() is { } line) {
            if (line.StartsWith("Content-Length:")) contentLength = int.Parse(line.Split(':')[1].Trim());
            else if (line is "") return contentLength;
        }
        
        return -1;
    }

    private static async Task<DapResponse?> HandleMessageAsync(string json) {
        var req = JsonSerializer.Deserialize<DapRequest>(json);
        if (req is null) {
            await Console.Error.WriteLineAsync("Invalid DapRequest");
            return null;
        }
        
        switch (req.Command) {
            case "initialize":
                break;
            
            case "launch":
                break;
            
            case "configurationDone":
                break;
            
            case "setBreakpoints":
                break;
            
            case "threads":
                break;
            
            case "scopes":
                break;
            
            case "variables":
                break;
            
            case "evaluate":
                break;
            
            case "continue":
                break;
            
            case "next":    // step
                break;
            
            case "stepIn":
                break;
            
            case "stepOut":
                break;
            
            case "pause":
                break;
            
            case "disconnect":
                break;
            
            default:
                await Console.Error.WriteLineAsync($"Unknown command: {req.Command}");
                return null;
        }
        
        return null;
    }

    private async Task WriteMessageAsync(string json) {
        var framed = $"Content-Length: {json.Length}\r\n\r\n{json}";
        await _log.WriteLineAsync($"[OUT] {json}");
        await _writer.WriteLineAsync(framed);
        await _writer.FlushAsync();
    }
    
    private async Task SendEventAsync(string eventName, object? body) {
        var evt = new DapEvent(_seq++, "event", eventName, body);
        await WriteMessageAsync(JsonSerializer.Serialize(evt));
    }
    
 
    private static   int          _seq = 1;
    private          Process?     _emulatorProcess;
    private readonly TcpClient    _client = client;
    private readonly StreamWriter _log    = log;
    private          StreamWriter _writer = null!;
    private readonly NetworkStream _stream = client.GetStream();
}
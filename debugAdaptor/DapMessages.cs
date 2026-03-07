using System.Text.Json;
using System.Text.Json.Serialization;

namespace dap;

public record DapMessage(
    [property: JsonPropertyName("seq")]  int    Seq,
    [property: JsonPropertyName("type")] string Type  // "request" | "response" | "event"
);

public record DapRequest(
    int                                                    Seq, string Type,
    [property: JsonPropertyName("command")]   string       Command,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments
) : DapMessage(Seq, Type);

public record DapResponse(
    int                                                 Seq, string Type,
    [property: JsonPropertyName("request_seq")] int     RequestSeq,
    [property: JsonPropertyName("success")]     bool    Success,
    [property: JsonPropertyName("command")]     string  Command,
    [property: JsonPropertyName("body")]        object? Body    = null,
    [property: JsonPropertyName("message")]     string? Message = null
) : DapMessage(Seq, Type);

public record DapEvent(
    int                                           Seq, string Type,
    [property: JsonPropertyName("event")] string  Event,
    [property: JsonPropertyName("body")]  object? Body = null
) : DapMessage(Seq, Type);
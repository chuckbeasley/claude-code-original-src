namespace ClaudeCode.Mcp.JsonRpc;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Represents a JSON-RPC 2.0 request message sent from client to server.
/// </summary>
public record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>
/// Represents a JSON-RPC 2.0 response message received from the server.
/// Exactly one of <see cref="Result"/> or <see cref="Error"/> will be non-null
/// on a conforming response.
/// </summary>
public record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

/// <summary>
/// Represents a JSON-RPC 2.0 error object embedded in a failed response.
/// </summary>
public record JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

/// <summary>
/// Represents a JSON-RPC 2.0 notification (a request with no <c>id</c> field and no expected response).
/// </summary>
public record JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

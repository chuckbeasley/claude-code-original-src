namespace ClaudeCode.Tools.McpTool;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Mcp;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="McpInvokeTool"/>.</summary>
public record McpInvokeInput
{
    /// <summary>The logical name of the MCP server to invoke.</summary>
    [JsonPropertyName("server")]
    public required string Server { get; init; }

    /// <summary>The name of the tool on the MCP server to call.</summary>
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    /// <summary>Optional JSON arguments to pass to the tool. When omitted, no arguments are sent.</summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

/// <summary>Strongly-typed output for <see cref="McpInvokeTool"/>.</summary>
/// <param name="Server">The MCP server that was targeted.</param>
/// <param name="Tool">The tool name that was targeted.</param>
/// <param name="Result">The textual result returned by the server, or a placeholder.</param>
/// <param name="IsError">Whether the MCP server flagged the result as an error.</param>
public record McpInvokeOutput(string Server, string Tool, string Result, bool IsError);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Generic invocation tool for any MCP server tool.
/// Identifies the target server by name, locates it via <see cref="McpServerManager"/>,
/// and forwards the call along with optional JSON arguments.
/// Returns an error result when the MCP server manager is not available or the
/// named server is not connected.
/// </summary>
public sealed class McpInvokeTool : Tool<McpInvokeInput, McpInvokeOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            server    = new { type = "string", description = "The logical name of the MCP server to call." },
            tool      = new { type = "string", description = "The name of the tool to invoke on the MCP server." },
            arguments = new { description = "Optional JSON object of arguments to pass to the tool." },
        },
        required = new[] { "server", "tool" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "MCPTool";

    /// <inheritdoc/>
    public override string? SearchHint => "invoke a tool on a connected MCP server";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Invokes a named tool on a connected MCP server. " +
            "Requires `server` (the logical server name) and `tool` (the server-side tool name). " +
            "Optional `arguments` is a JSON object forwarded verbatim to the server.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `MCPTool` to call any tool exposed by an MCP server. " +
            "Provide `server` (the server's logical name as configured), `tool` (the exact tool name), " +
            "and optionally `arguments` (a JSON object). " +
            "The tool returns the server's response content and an `isError` flag.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "MCPTool";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        var server = input.Value.TryGetProperty("server", out var s) ? s.GetString() : null;
        var tool   = input.Value.TryGetProperty("tool",   out var t) ? t.GetString() : null;

        if (server is not null && tool is not null)
            return $"Calling {server}/{tool}";

        return "Calling MCP tool";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override McpInvokeInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<McpInvokeInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise McpInvokeInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(McpInvokeOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsError
            ? $"[MCP Error] {result.Server}/{result.Tool}: {result.Result}"
            : result.Result;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        McpInvokeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Server))
            return Task.FromResult(ValidationResult.Failure("The 'server' field must not be empty or whitespace."));

        if (string.IsNullOrWhiteSpace(input.Tool))
            return Task.FromResult(ValidationResult.Failure("The 'tool' field must not be empty or whitespace."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<McpInvokeOutput>> ExecuteAsync(
        McpInvokeInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        if (context.McpManager is not McpServerManager manager)
            return Error(input, "MCP server manager is not available.");

        var client = manager.GetClient(input.Server);
        if (client is null || !client.IsAlive)
            return Error(input, $"MCP server '{input.Server}' is not connected.");

        var result = await client.CallToolAsync(input.Tool, input.Arguments, ct).ConfigureAwait(false);
        var content = ExtractText(result.Content);

        return new ToolResult<McpInvokeOutput>
        {
            Data = new McpInvokeOutput(input.Server, input.Tool, content, result.IsError),
        };
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static ToolResult<McpInvokeOutput> Error(McpInvokeInput input, string msg)
        => new() { Data = new McpInvokeOutput(input.Server, input.Tool, msg, IsError: true) };

    /// <summary>
    /// Extracts plain text from an MCP content block (array of typed blocks, plain string, or raw JSON).
    /// </summary>
    private static string ExtractText(JsonElement? content)
    {
        if (content is null)
            return string.Empty;

        if (content.Value.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.Value.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt))
                {
                    if (sb.Length > 0)
                        sb.AppendLine();
                    sb.Append(txt.GetString());
                }
            }
            return sb.ToString();
        }

        if (content.Value.ValueKind == JsonValueKind.String)
            return content.Value.GetString() ?? string.Empty;

        return content.Value.GetRawText();
    }
}

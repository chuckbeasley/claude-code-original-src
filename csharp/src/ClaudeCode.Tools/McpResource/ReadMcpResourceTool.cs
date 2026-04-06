namespace ClaudeCode.Tools.McpResource;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Mcp;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="ReadMcpResourceTool"/>.</summary>
public record ReadMcpResourceInput
{
    /// <summary>The logical name of the MCP server that owns the resource.</summary>
    [JsonPropertyName("server")]
    public required string Server { get; init; }

    /// <summary>The URI of the resource to read, as reported by the server's resource listing.</summary>
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

/// <summary>Strongly-typed output for <see cref="ReadMcpResourceTool"/>.</summary>
/// <param name="Server">The MCP server that was targeted.</param>
/// <param name="Uri">The URI of the resource that was requested.</param>
/// <param name="Content">The resource content, or a placeholder when not yet available.</param>
public record ReadMcpResourceOutput(string Server, string Uri, string Content);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Reads the content of a specific resource exposed by an MCP server.
/// This is a read-only, concurrency-safe tool.
/// Returns an error message when the MCP server manager is unavailable,
/// the named server is not connected, or the resource cannot be read.
/// </summary>
public sealed class ReadMcpResourceTool : Tool<ReadMcpResourceInput, ReadMcpResourceOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            server = new { type = "string", description = "The logical name of the MCP server that owns the resource." },
            uri    = new { type = "string", description = "The URI of the resource to read." },
        },
        required = new[] { "server", "uri" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "ReadMcpResource";

    /// <inheritdoc/>
    public override string? SearchHint => "read a resource from an MCP server by URI";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Reads the content of a resource from an MCP server. " +
            "Requires `server` (the server's logical name) and `uri` (the resource URI).");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `ReadMcpResource` to retrieve the content of a resource from a connected MCP server. " +
            "First use `ListMcpResources` to discover available URIs. " +
            "Provide `server` and `uri`; the tool returns the resource content as a string.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "ReadMcpResource";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("uri", out var uri) &&
            uri.ValueKind == JsonValueKind.String)
        {
            return $"Reading resource '{uri.GetString()}'";
        }

        return "Reading MCP resource";
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override ReadMcpResourceInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<ReadMcpResourceInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise ReadMcpResourceInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(ReadMcpResourceOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Content;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        ReadMcpResourceInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Server))
            return Task.FromResult(ValidationResult.Failure("The 'server' field must not be empty or whitespace."));

        if (string.IsNullOrWhiteSpace(input.Uri))
            return Task.FromResult(ValidationResult.Failure("The 'uri' field must not be empty or whitespace."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<ReadMcpResourceOutput>> ExecuteAsync(
        ReadMcpResourceInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        if (context.McpManager is not McpServerManager manager)
        {
            return new ToolResult<ReadMcpResourceOutput>
            {
                Data = new ReadMcpResourceOutput(input.Server, input.Uri,
                    "MCP server manager is not available."),
            };
        }

        var client = manager.GetClient(input.Server);
        if (client is null || !client.IsAlive)
        {
            return new ToolResult<ReadMcpResourceOutput>
            {
                Data = new ReadMcpResourceOutput(input.Server, input.Uri,
                    $"MCP server '{input.Server}' is not connected."),
            };
        }

        var content = await client.ReadResourceAsync(input.Uri, ct).ConfigureAwait(false);

        if (content is null)
        {
            return new ToolResult<ReadMcpResourceOutput>
            {
                Data = new ReadMcpResourceOutput(input.Server, input.Uri,
                    $"Failed to read resource '{input.Uri}' from server '{input.Server}'. " +
                    "The server may not support resources/read, or the URI may be invalid."),
            };
        }

        return new ToolResult<ReadMcpResourceOutput>
        {
            Data = new ReadMcpResourceOutput(input.Server, input.Uri, content.Text),
        };
    }
}

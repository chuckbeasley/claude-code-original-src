namespace ClaudeCode.Tools.McpResource;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;
using ClaudeCode.Mcp;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="ListMcpResourcesTool"/>.</summary>
public record ListMcpResourcesInput
{
    /// <summary>
    /// Optional server name to restrict the listing to a single server.
    /// When omitted, resources from all connected servers are listed.
    /// </summary>
    [JsonPropertyName("server")]
    public string? Server { get; init; }
}

/// <summary>Strongly-typed output for <see cref="ListMcpResourcesTool"/>.</summary>
/// <param name="Message">Human-readable description of the listing result or placeholder.</param>
public record ListMcpResourcesOutput(string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Lists resources exposed by one or all connected MCP servers.
/// Falls back to listing available tools when a server does not support resources.
/// </summary>
public sealed class ListMcpResourcesTool : Tool<ListMcpResourcesInput, ListMcpResourcesOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            server = new { type = "string", description = "Optional logical server name to filter results." },
        },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "ListMcpResources";

    /// <inheritdoc/>
    public override string? SearchHint => "list resources from connected MCP servers";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Lists resources exposed by connected MCP servers. " +
            "An optional `server` filter restricts output to a single server.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `ListMcpResources` to discover what resources (URIs) are available from MCP servers. " +
            "Optionally pass `server` to filter by a specific server name. " +
            "Combine with `ReadMcpResource` to retrieve the content of a discovered resource.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "ListMcpResources";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("server", out var server) &&
            server.ValueKind == JsonValueKind.String)
        {
            return $"Listing resources from '{server.GetString()}'";
        }

        return "Listing MCP resources";
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
    public override ListMcpResourcesInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<ListMcpResourcesInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise ListMcpResourcesInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(ListMcpResourcesOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<ListMcpResourcesOutput>> ExecuteAsync(
        ListMcpResourcesInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        if (context.McpManager is not McpServerManager manager)
            return new() { Data = new ListMcpResourcesOutput("MCP server manager is not available.") };

        var clients = manager.GetAll();
        if (clients.Count == 0)
            return new() { Data = new ListMcpResourcesOutput("No MCP servers connected.") };

        var sb = new StringBuilder();
        foreach (var (name, client) in clients)
        {
            if (input.Server is not null &&
                !name.Equals(input.Server, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!client.IsAlive)
                continue;

            sb.AppendLine($"Server: {name}");

            var resources = await client.ListResourcesAsync(ct).ConfigureAwait(false);
            if (resources.Count > 0)
            {
                foreach (var resource in resources)
                {
                    var desc = resource.Description is not null ? $" — {resource.Description}" : "";
                    var mime = resource.MimeType is not null ? $" [{resource.MimeType}]" : "";
                    sb.AppendLine($"  Resource: {resource.Name}{mime}{desc}");
                    sb.AppendLine($"    URI: {resource.Uri}");
                }
            }
            else
            {
                // Server doesn't expose resources — fall back to listing tools.
                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                if (tools.Count > 0)
                {
                    sb.AppendLine("  (No resources; available tools:)");
                    foreach (var tool in tools)
                        sb.AppendLine($"  Tool: {tool.Name} — {tool.Description ?? "(no description)"}");
                }
                else
                {
                    sb.AppendLine("  (No resources or tools)");
                }
            }

            sb.AppendLine();
        }

        var message = sb.Length > 0
            ? sb.ToString().TrimEnd()
            : "No resources found.";

        return new() { Data = new ListMcpResourcesOutput(message) };
    }
}

namespace ClaudeCode.Tools.LSP;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="LSPTool"/>.</summary>
public record LSPInput
{
    /// <summary>
    /// The LSP action to perform.
    /// Valid values: <c>"diagnostics"</c>, <c>"hover"</c>, <c>"definition"</c>,
    /// <c>"references"</c>, <c>"completion"</c>.
    /// </summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>The file path to query against the language server.</summary>
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary>Zero-based line number within the file (required for hover/definition/references/completion).</summary>
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    /// <summary>Zero-based character offset on the line (required for hover/definition/references/completion).</summary>
    [JsonPropertyName("character")]
    public int? Character { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="LSPTool"/>.</summary>
/// <param name="Message">The result or status message.</param>
public record LSPOutput(string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// LSP tool that launches a language server process on demand and routes requests
/// over stdio JSON-RPC 2.0 using <see cref="LspServerProcess"/>.
/// Supported actions: diagnostics, hover, definition, references, completion.
/// </summary>
public sealed class LSPTool : Tool<LSPInput, LSPOutput>
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    private static readonly string[] ValidActions =
        ["diagnostics", "hover", "definition", "references", "completion"];

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[] { "diagnostics", "hover", "definition", "references", "completion" },
                description = "The LSP action to perform",
            },
            file_path = new { type = "string",  description = "File path to query" },
            line      = new { type = "integer", description = "Zero-based line number" },
            character = new { type = "integer", description = "Zero-based character offset" },
        },
        required = new[] { "action", "file_path" },
    });

    // -----------------------------------------------------------------------
    // Static server cache — servers are reused for the process lifetime.
    // -----------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, LspServerProcess> _serverCache = new();
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "LSP";

    /// <inheritdoc/>
    public override string[] Aliases => ["lsp", "language_server"];

    /// <inheritdoc/>
    public override string? SearchHint => "language server diagnostics hover definition references completion";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Queries a Language Server Protocol (LSP) backend for diagnostics, hover info, " +
            "go-to-definition, references, or completions. " +
            "Launches the appropriate language server on demand and communicates over stdio JSON-RPC 2.0.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `LSP` to query a language server. Provide `action` " +
            "(`diagnostics`, `hover`, `definition`, `references`, or `completion`) and `file_path`. " +
            "Supply `line` and `character` for position-based actions. " +
            "The server is auto-detected from the file extension, or configure via " +
            "`LSP_SERVER_COMMAND` environment variable or `.claude/lsp-servers.json`.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "LSP";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("action", out var action) &&
            action.ValueKind == JsonValueKind.String &&
            input.Value.TryGetProperty("file_path", out var filePath) &&
            filePath.ValueKind == JsonValueKind.String)
        {
            return $"LSP {action.GetString()} on {Path.GetFileName(filePath.GetString())}";
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        LSPInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Action))
            return Task.FromResult(ValidationResult.Failure("action must not be empty."));

        if (!Array.Exists(ValidActions, a => string.Equals(a, input.Action, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(ValidationResult.Failure(
                $"action must be one of: {string.Join(", ", ValidActions)}."));

        if (string.IsNullOrWhiteSpace(input.FilePath))
            return Task.FromResult(ValidationResult.Failure("file_path must not be empty."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override LSPInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<LSPInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize LSPInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(LSPOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<LSPOutput>> ExecuteAsync(
        LSPInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var filePath = Path.GetFullPath(input.FilePath, context.Cwd);

        if (!File.Exists(filePath))
            return new() { Data = new LSPOutput($"File not found: {filePath}") };

        // Determine which LSP server to use for this file.
        var serverInfo = await GetServerCommandAsync(filePath, context.Cwd, ct).ConfigureAwait(false);
        if (serverInfo is null)
        {
            var ext = Path.GetExtension(filePath);
            return new()
            {
                Data = new LSPOutput(
                    $"No LSP server configured for '{ext}' files.\n\n" +
                    "Available servers (must be installed and on PATH):\n" +
                    "  .py           → pylsp\n" +
                    "  .ts / .js     → typescript-language-server --stdio\n" +
                    "  .rs           → rust-analyzer\n" +
                    "  .cs           → omnisharp -lsp\n" +
                    "  .go           → gopls\n\n" +
                    "Alternatively:\n" +
                    "  • Set the LSP_SERVER_COMMAND environment variable, or\n" +
                    "  • Create .claude/lsp-servers.json: " +
                    "{ \"servers\": { \".py\": \"pylsp\", \".ts\": \"typescript-language-server --stdio\" } }"),
            };
        }

        var (command, args) = serverInfo.Value;
        var cacheKey = $"{command} {string.Join(" ", args)}|{context.Cwd}";

        // Acquire (or start) the server for this project directory.
        LspServerProcess server;
        try
        {
            server = await GetOrCreateServerAsync(cacheKey, command, args, context.Cwd, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new() { Data = new LSPOutput($"Failed to start LSP server '{command}': {ex.Message}") };
        }

        // Ensure the target file has been opened in the server session.
        var fileUri = LspServerProcess.ToFileUri(filePath);
        try
        {
            await server.EnsureFileOpenedAsync(filePath, fileUri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new() { Data = new LSPOutput($"Failed to open file in LSP server: {ex.Message}") };
        }

        // Send the LSP request.
        var lspMethod = MapActionToLspMethod(input.Action);
        var lspParams  = BuildLspParams(input.Action, fileUri, input.Line ?? 0, input.Character ?? 0);

        JsonObject result;
        try
        {
            result = await server.SendRequestAsync(lspMethod, lspParams, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new() { Data = new LSPOutput($"LSP request '{lspMethod}' failed: {ex.Message}") };
        }

        // Pretty-print the JSON response.
        var formatted = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new() { Data = new LSPOutput(formatted) };
    }

    // -----------------------------------------------------------------------
    // Server cache management
    // -----------------------------------------------------------------------

    private static async Task<LspServerProcess> GetOrCreateServerAsync(
        string cacheKey, string command, string[] args, string cwd, CancellationToken ct)
    {
        // Fast path: existing running server.
        if (_serverCache.TryGetValue(cacheKey, out var cached) && !cached.HasExited)
            return cached;

        // Slow path: create or replace.
        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock.
            if (_serverCache.TryGetValue(cacheKey, out cached) && !cached.HasExited)
                return cached;

            // Remove any stale (exited) entry.
            _serverCache.TryRemove(cacheKey, out _);

            var server = await LspServerProcess.StartAsync(command, args, cwd, ct).ConfigureAwait(false);
            _serverCache[cacheKey] = server;
            return server;
        }
        finally { _cacheLock.Release(); }
    }

    // -----------------------------------------------------------------------
    // Command detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the LSP server command and arguments for the given file path.
    /// Resolution order: <c>LSP_SERVER_COMMAND</c> env var →
    /// <c>.claude/lsp-servers.json</c> → built-in extension defaults.
    /// Returns <see langword="null"/> when no mapping exists for the file's extension.
    /// </summary>
    private static async ValueTask<(string command, string[] args)?> GetServerCommandAsync(
        string filePath, string cwd, CancellationToken ct)
    {
        // 1. Environment variable override.
        var envCmd = Environment.GetEnvironmentVariable("LSP_SERVER_COMMAND");
        if (!string.IsNullOrWhiteSpace(envCmd))
            return ParseCommand(envCmd);

        // 2. Project-local configuration in .claude/lsp-servers.json.
        var configPath = Path.Combine(cwd, ".claude", "lsp-servers.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (doc.RootElement.TryGetProperty("servers", out var serversEl) &&
                    serversEl.TryGetProperty(ext, out var cmdEl) &&
                    cmdEl.GetString() is string customCmd)
                {
                    return ParseCommand(customCmd);
                }
            }
            catch { /* malformed config — fall through to default detection */ }
        }

        // 3. Built-in defaults keyed on file extension.
        var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
        return fileExt switch
        {
            ".py"                              => ("pylsp", Array.Empty<string>()),
            ".ts" or ".tsx" or ".js" or ".jsx" => ("typescript-language-server", ["--stdio"]),
            ".rs"                              => ("rust-analyzer", Array.Empty<string>()),
            ".cs"                              => ("omnisharp", ["-lsp"]),
            ".go"                              => ("gopls", Array.Empty<string>()),
            _                                  => ((string, string[])?)null,
        };
    }

    /// <summary>Splits a command string (e.g. <c>"typescript-language-server --stdio"</c>) into executable + args.</summary>
    private static (string command, string[] args) ParseCommand(string cmdStr)
    {
        var parts = cmdStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? (parts[0], Array.Empty<string>())
            : (parts[0], parts[1..]);
    }

    // -----------------------------------------------------------------------
    // LSP method / params builders
    // -----------------------------------------------------------------------

    /// <summary>Maps a tool <paramref name="action"/> to its LSP method name.</summary>
    private static string MapActionToLspMethod(string action) =>
        action.ToLowerInvariant() switch
        {
            "hover"       => "textDocument/hover",
            "definition"  => "textDocument/definition",
            "references"  => "textDocument/references",
            "completion"  => "textDocument/completion",
            "diagnostics" => "textDocument/diagnostic",
            var other     => other, // pass-through for any future extensions
        };

    /// <summary>
    /// Constructs the LSP <c>params</c> object for the given action and position.
    /// </summary>
    private static JsonObject BuildLspParams(string action, string fileUri, int line, int character)
    {
        var textDoc = new JsonObject { ["uri"] = fileUri };

        return action.ToLowerInvariant() switch
        {
            "diagnostics" => new JsonObject { ["textDocument"] = textDoc },
            "references"  => new JsonObject
            {
                ["textDocument"] = textDoc,
                ["position"]     = new JsonObject { ["line"] = line, ["character"] = character },
                ["context"]      = new JsonObject { ["includeDeclaration"] = true },
            },
            _ => new JsonObject
            {
                ["textDocument"] = textDoc,
                ["position"]     = new JsonObject { ["line"] = line, ["character"] = character },
            },
        };
    }
}

namespace ClaudeCode.Tools.McpAuth;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Configuration.Settings;
using ClaudeCode.Core.Tools;
using ClaudeCode.Mcp;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for <see cref="McpAuthTool"/>.</summary>
public record McpAuthInput
{
    /// <summary>The logical name of the MCP server for which to initiate OAuth authentication.</summary>
    [JsonPropertyName("server")]
    public required string Server { get; init; }
}

/// <summary>Strongly-typed output for <see cref="McpAuthTool"/>.</summary>
/// <param name="Server">The MCP server that was targeted.</param>
/// <param name="Message">Human-readable status message from the authentication flow.</param>
public record McpAuthOutput(string Server, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Initiates an OAuth authentication flow for a named MCP server.
/// In the current implementation, a placeholder is returned because the OAuth
/// flow requires browser integration and credential storage that are handled
/// at the CLI/UI layer rather than within the tool itself.
/// </summary>
public sealed class McpAuthTool : Tool<McpAuthInput, McpAuthOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            server = new { type = "string", description = "The logical name of the MCP server to authenticate against." },
        },
        required = new[] { "server" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "McpAuth";

    /// <inheritdoc/>
    public override string? SearchHint => "authenticate with an MCP server via OAuth";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Initiates an OAuth authentication flow for the named MCP server. " +
            "The CLI layer opens a browser window to complete the authorization code exchange.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `McpAuth` when an MCP server requires authentication before its tools or resources " +
            "can be accessed. Provide `server` (the logical server name). " +
            "The tool triggers the OAuth flow; the CLI will open a browser for the user to authorize.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "McpAuth";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null)
            return null;

        if (input.Value.TryGetProperty("server", out var server) &&
            server.ValueKind == JsonValueKind.String)
        {
            return $"Authenticating with '{server.GetString()}'";
        }

        return "Authenticating with MCP server";
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override McpAuthInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<McpAuthInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise McpAuthInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(McpAuthOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"[McpAuth:{result.Server}] {result.Message}";
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        McpAuthInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Server))
            return Task.FromResult(ValidationResult.Failure("The 'server' field must not be empty or whitespace."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<McpAuthOutput>> ExecuteAsync(
        McpAuthInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        if (context.McpManager is not McpServerManager manager)
        {
            return new ToolResult<McpAuthOutput>
            {
                Data = new McpAuthOutput(input.Server,
                    "MCP server manager is not available. Cannot initiate OAuth flow."),
            };
        }

        var client = manager.GetClient(input.Server);
        if (client is not null && client.IsAlive)
        {
            return new ToolResult<McpAuthOutput>
            {
                Data = new McpAuthOutput(input.Server,
                    $"MCP server '{input.Server}' is already connected and authenticated."),
            };
        }

        // Get server config to find the auth endpoint.
        // Servers configured with an "authorizationUrl" in their settings can use this flow.
        // Otherwise, the tool reports instructions.
        var authUrl = GetAuthorizationUrl(input.Server, context);
        if (authUrl is null)
        {
            return new ToolResult<McpAuthOutput>
            {
                Data = new McpAuthOutput(input.Server,
                    $"No OAuth configuration found for server '{input.Server}'. " +
                    "Add 'authorizationUrl', 'tokenUrl', and 'clientId' to the server's " +
                    "entry in settings.json under 'mcpServers'."),
            };
        }

        try
        {
            // Look up tokenUrl and clientId from settings / context.
            string? tokenUrl = null;
            string? clientId = null;
            if (context.McpManager is ClaudeCode.Mcp.McpServerManager mgr)
            {
                var serverConfig = mgr.GetServerEntryConfig(input.Server);
                if (serverConfig?.TokenUrl is not null)
                    tokenUrl = serverConfig.TokenUrl;
                if (serverConfig?.ClientId is not null)
                    clientId = serverConfig.ClientId;
            }

            // Fall back to env vars when settings did not supply values.
            var tokenUrlEnv = $"MCP_{input.Server.ToUpperInvariant().Replace("-", "_")}_TOKEN_URL";
            var clientIdEnv = $"MCP_{input.Server.ToUpperInvariant().Replace("-", "_")}_CLIENT_ID";
            tokenUrl ??= Environment.GetEnvironmentVariable(tokenUrlEnv);
            clientId ??= Environment.GetEnvironmentVariable(clientIdEnv);

            var tokenJson = await RunPkceFlowAsync(authUrl, tokenUrl, clientId, ct).ConfigureAwait(false);
            PersistToken(input.Server, tokenJson);
            return new ToolResult<McpAuthOutput>
            {
                Data = new McpAuthOutput(input.Server,
                    $"Successfully authenticated with '{input.Server}'. " +
                    "Token has been stored. Reconnect the server to use the new credentials."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult<McpAuthOutput>
            {
                Data = new McpAuthOutput(input.Server, $"Authentication failed: {ex.Message}"),
            };
        }
    }

    private static string? GetAuthorizationUrl(string server, ToolUseContext context)
    {
        // 1. Try env var convention: MCP_{SERVER}_AUTH_URL
        var envKey = $"MCP_{server.ToUpperInvariant().Replace("-", "_")}_AUTH_URL";
        var envUrl = Environment.GetEnvironmentVariable(envKey);
        if (envUrl is not null) return envUrl;

        // 2. Try setting from McpServerManager / settings via context.
        if (context.McpManager is ClaudeCode.Mcp.McpServerManager mgr)
        {
            var client = mgr.GetClient(server);
            if (client is not null)
            {
                // The auth URL is stored in the manager's server config metadata.
                // Check if there is an auth URL set in the McpServerMetadata dictionary.
                var authUrl = mgr.GetAuthorizationUrl(server);
                if (authUrl is not null) return authUrl;
            }
        }

        return null;
    }

    private static async Task<string> RunPkceFlowAsync(string authorizationEndpoint, string? tokenUrl, string? clientId, CancellationToken ct)
    {
        // Generate PKCE verifier and challenge.
        var verifierBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        // Start local redirect listener on a random free port.
        var listener = new System.Net.HttpListener();
        var port = GetFreePort();
        var redirectUri = $"http://localhost:{port}/callback";
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        // Build the authorization URL.
        var state = System.Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var authUrl = $"{authorizationEndpoint}" +
            $"?response_type=code" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}";

        // Open the browser.
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true }); }
        catch { /* Non-fatal */ }

        Console.Error.WriteLine($"Opening browser for OAuth authorization. If it doesn't open, visit:\n{authUrl}");

        // Wait for the redirect callback.
        var contextTask = listener.GetContextAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), ct);
        var completed = await Task.WhenAny(contextTask, timeoutTask).ConfigureAwait(false);

        if (completed == timeoutTask)
        {
            listener.Stop();
            throw new TimeoutException("OAuth authorization timed out after 5 minutes.");
        }

        var httpContext = await contextTask.ConfigureAwait(false);
        var code = httpContext.Request.QueryString["code"]
            ?? throw new InvalidOperationException("No authorization code in callback.");

        // Respond to the browser.
        var response = httpContext.Response;
        var html = "<html><body><h1>Authorization complete. You can close this tab.</h1></body></html>";
        var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = htmlBytes.Length;
        await response.OutputStream.WriteAsync(htmlBytes, ct).ConfigureAwait(false);
        response.Close();
        listener.Stop();

        // If we have a token URL, exchange the code for a token.
        if (!string.IsNullOrWhiteSpace(tokenUrl))
        {
            using var tokenHttpClient = new System.Net.Http.HttpClient();
            var tokenParams = new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = codeVerifier,
            };
            if (!string.IsNullOrWhiteSpace(clientId))
                tokenParams["client_id"] = clientId;

            using var formContent = new System.Net.Http.FormUrlEncodedContent(tokenParams);
            var tokenTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tokenTimeout.CancelAfter(TimeSpan.FromSeconds(30));

            var tokenResponse = await tokenHttpClient
                .PostAsync(tokenUrl, formContent, tokenTimeout.Token)
                .ConfigureAwait(false);

            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (tokenResponse.IsSuccessStatusCode)
                return tokenBody; // full token response JSON

            // Fall through if token exchange fails; return code as fallback.
            Console.Error.WriteLine($"[McpAuth] Token exchange failed ({(int)tokenResponse.StatusCode}): {tokenBody}");
        }

        // No token URL or exchange failed — return the auth code for manual exchange.
        return System.Text.Json.JsonSerializer.Serialize(new { code, codeVerifier, redirectUri });
    }

    private static void PersistToken(string server, string tokenJson)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "mcp-tokens.json");

        Dictionary<string, System.Text.Json.JsonElement> tokens;
        if (File.Exists(file))
        {
            try
            {
                var existing = File.ReadAllText(file);
                tokens = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(existing)
                    ?? new Dictionary<string, System.Text.Json.JsonElement>();
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[McpAuth] Token store is corrupted, resetting: {ex.Message}");
                tokens = new Dictionary<string, JsonElement>();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpAuth] Failed to load token store: {ex.Message}");
                tokens = new Dictionary<string, JsonElement>();
            }
        }
        else
        {
            tokens = new Dictionary<string, System.Text.Json.JsonElement>();
        }

        tokens[server] = System.Text.Json.JsonSerializer.SerializeToElement(
            System.Text.Json.JsonDocument.Parse(tokenJson).RootElement);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(tokens));
    }

    private static string Base64UrlEncode(byte[] bytes)
        => System.Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

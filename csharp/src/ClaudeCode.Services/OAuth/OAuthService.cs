namespace ClaudeCode.Services.OAuth;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// OAuth 2.0 token response returned by the authorization server.
/// </summary>
/// <param name="AccessToken">The bearer access token.</param>
/// <param name="RefreshToken">Optional refresh token; <see langword="null"/> when the server did not issue one.</param>
/// <param name="ExpiresIn">Seconds until the access token expires (from <paramref name="ObtainedAt"/>).</param>
/// <param name="TokenType">Token type string (typically <c>"Bearer"</c>).</param>
/// <param name="ObtainedAt">The UTC instant at which this token was received.</param>
public record OAuthTokenResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresIn,
    string TokenType,
    DateTimeOffset ObtainedAt)
{
    /// <summary>
    /// Returns <see langword="true"/> when the access token is within 60 seconds of its expiry time.
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ObtainedAt.AddSeconds(ExpiresIn - 60);
}

/// <summary>
/// Full PKCE (Proof Key for Code Exchange) OAuth 2.0 authorization-code flow implementation.
/// Supports code-verifier/challenge generation, browser launch, local callback listener,
/// token exchange, and token refresh.
/// </summary>
public sealed class OAuthService
{
    // Single shared HttpClient for the process lifetime; safe for concurrent use.
    private static readonly HttpClient _http = new();

    // -------------------------------------------------------------------------
    // PKCE helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a cryptographically random PKCE code verifier (32 random bytes, base64url encoded).
    /// </summary>
    public string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Derives the PKCE code challenge from <paramref name="verifier"/> using SHA-256 (S256 method).
    /// </summary>
    /// <param name="verifier">The code verifier returned by <see cref="GenerateCodeVerifier"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="verifier"/> is <see langword="null"/>.</exception>
    public string GenerateCodeChallenge(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    // -------------------------------------------------------------------------
    // URL builder
    // -------------------------------------------------------------------------

    /// <summary>
    /// Constructs the OAuth 2.0 authorization URL with all required PKCE query parameters.
    /// </summary>
    /// <param name="baseUrl">The authorization endpoint base URL (e.g. <c>https://example.com/oauth/authorize</c>).</param>
    /// <param name="clientId">The registered OAuth client ID.</param>
    /// <param name="redirectUri">The local redirect URI that will receive the callback.</param>
    /// <param name="scopes">The requested OAuth scopes.</param>
    /// <param name="codeChallenge">The PKCE code challenge (output of <see cref="GenerateCodeChallenge"/>).</param>
    /// <param name="state">An opaque CSRF-protection state value.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    public string BuildAuthUrl(
        string baseUrl,
        string clientId,
        string redirectUri,
        IEnumerable<string> scopes,
        string codeChallenge,
        string state)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(codeChallenge);
        ArgumentNullException.ThrowIfNull(state);

        var scopeString = string.Join(" ", scopes);
        var queryParts = new[]
        {
            $"response_type={Uri.EscapeDataString("code")}",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scopeString)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            $"code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
        };

        return $"{baseUrl.TrimEnd('/')}?{string.Join("&", queryParts)}";
    }

    // -------------------------------------------------------------------------
    // Callback listener
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens an <see cref="HttpListener"/> on <c>http://localhost:{port}/</c> and returns a
    /// <see cref="Task{TResult}"/> that completes with the authorization code once the browser
    /// redirects to the local callback URL.
    /// </summary>
    /// <param name="port">The localhost port number (1–65535) to listen on.</param>
    /// <param name="ct">Cancellation token; cancels the blocking wait for the browser redirect.</param>
    /// <returns>The authorization code extracted from the callback query string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="port"/> is not in [1, 65535].</exception>
    /// <exception cref="InvalidOperationException">Thrown when the callback does not contain a <c>code</c> parameter.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled before the callback arrives.</exception>
    public async Task<string> StartCallbackListener(int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");

        var prefix = $"http://localhost:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();  // binds the port synchronously — listener is accepting before first await

        try
        {
            // Register cancellation: stopping the listener causes GetContextAsync to fault.
            using var reg = ct.Register(() =>
            {
                if (listener.IsListening)
                    listener.Stop();
            });

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw; // unreachable but satisfies the compiler
            }

            var code = context.Request.QueryString["code"];
            var errorParam = context.Request.QueryString["error"];

            // Always send a browser-visible acknowledgement response.
            var html = code is not null
                ? "<html><body><h2>Authorization successful. You may close this tab.</h2></body></html>"
                : $"<html><body><h2>Authorization failed: {System.Net.WebUtility.HtmlEncode(errorParam ?? "unknown error")}. You may close this tab.</h2></body></html>";

            var responseBytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBytes.Length;
            context.Response.StatusCode = code is not null ? 200 : 400;

            try
            {
                await context.Response.OutputStream.WriteAsync(responseBytes, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                context.Response.Close();
            }

            if (code is null)
            {
                throw new InvalidOperationException(
                    $"OAuth callback did not contain an authorization code. " +
                    $"Error: {errorParam ?? "unknown"}");
            }

            return code;
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
        }
    }

    // -------------------------------------------------------------------------
    // Token exchange
    // -------------------------------------------------------------------------

    /// <summary>
    /// Exchanges an authorization code for an access token via a form-encoded POST to <paramref name="tokenUrl"/>.
    /// </summary>
    /// <param name="tokenUrl">The token endpoint URL.</param>
    /// <param name="clientId">The registered OAuth client ID.</param>
    /// <param name="code">The authorization code received from the callback.</param>
    /// <param name="codeVerifier">The original PKCE code verifier corresponding to the challenge sent at authorization.</param>
    /// <param name="redirectUri">The same redirect URI that was used in the authorization request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">Thrown when the token endpoint returns a non-success status.</exception>
    public async Task<OAuthTokenResponse> ExchangeCodeForToken(
        string tokenUrl,
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tokenUrl);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(codeVerifier);
        ArgumentNullException.ThrowIfNull(redirectUri);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        });

        var obtainedAt = DateTimeOffset.UtcNow;
        using var response = await _http.PostAsync(tokenUrl, form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseTokenResponse(json, obtainedAt);
    }

    /// <summary>
    /// Refreshes an expired access token using a refresh token grant.
    /// </summary>
    /// <param name="tokenUrl">The token endpoint URL.</param>
    /// <param name="clientId">The registered OAuth client ID.</param>
    /// <param name="refreshToken">The refresh token previously issued by the authorization server.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">Thrown when the token endpoint returns a non-success status.</exception>
    public async Task<OAuthTokenResponse> RefreshAccessToken(
        string tokenUrl,
        string clientId,
        string refreshToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tokenUrl);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(refreshToken);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });

        var obtainedAt = DateTimeOffset.UtcNow;
        using var response = await _http.PostAsync(tokenUrl, form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseTokenResponse(json, obtainedAt);
    }

    // -------------------------------------------------------------------------
    // Full flow
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the complete PKCE OAuth 2.0 authorization-code flow:
    /// generates verifier and challenge, builds the authorization URL, opens the user's
    /// default browser, waits for the local callback, and exchanges the code for tokens.
    /// </summary>
    /// <param name="authBaseUrl">The authorization endpoint URL (browser is directed here).</param>
    /// <param name="tokenUrl">The token exchange endpoint URL.</param>
    /// <param name="clientId">The registered OAuth client ID.</param>
    /// <param name="scopes">The requested OAuth scopes.</param>
    /// <param name="port">Local port for the redirect callback listener (default: 54321).</param>
    /// <param name="ct">Cancellation token; cancels the browser wait or token exchange.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="port"/> is not in [1, 65535].</exception>
    public async Task<OAuthTokenResponse> StartOAuthFlow(
        string authBaseUrl,
        string tokenUrl,
        string clientId,
        IEnumerable<string> scopes,
        int port = 54321,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authBaseUrl);
        ArgumentNullException.ThrowIfNull(tokenUrl);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(scopes);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");

        // Materialize the scope enumerable once before use.
        var scopeList = scopes as IList<string> ?? scopes.ToList();

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var redirectUri = $"http://localhost:{port}/";

        var authUrl = BuildAuthUrl(authBaseUrl, clientId, redirectUri, scopeList, challenge, state);

        // Start the callback listener BEFORE opening the browser.
        // listener.Start() runs synchronously before the first await inside StartCallbackListener,
        // so the port is bound by the time Process.Start is called.
        var codeTask = StartCallbackListener(port, ct);

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var code = await codeTask.ConfigureAwait(false);

        return await ExchangeCodeForToken(tokenUrl, clientId, code, verifier, redirectUri, ct)
            .ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static OAuthTokenResponse ParseTokenResponse(string json, DateTimeOffset obtainedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var atElement) ||
            atElement.GetString() is not string accessToken)
        {
            throw new InvalidOperationException("Token response is missing a valid 'access_token' field.");
        }

        string? refreshToken = null;
        if (root.TryGetProperty("refresh_token", out var rtElement) &&
            rtElement.ValueKind != JsonValueKind.Null)
        {
            refreshToken = rtElement.GetString();
        }

        var expiresIn = root.TryGetProperty("expires_in", out var eiElement) &&
                        eiElement.TryGetInt32(out var ei)
            ? ei
            : 3600;

        var tokenType = root.TryGetProperty("token_type", out var ttElement)
            ? ttElement.GetString() ?? "Bearer"
            : "Bearer";

        return new OAuthTokenResponse(accessToken, refreshToken, expiresIn, tokenType, obtainedAt);
    }
}

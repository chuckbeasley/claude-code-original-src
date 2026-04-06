namespace ClaudeCode.Services.SettingsSync;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;
using ClaudeCode.Services.Api;

/// <summary>
/// Syncs a subset of local settings with the Anthropic CCR backend on startup.
/// Both the download (remote → local) and upload (local → remote) directions are
/// individually gated by the <c>download-user-settings</c> and
/// <c>upload-user-settings</c> feature flags respectively.
/// </summary>
public sealed class SettingsSyncService
{
    // Keys present in the remote settings object that we are allowed to sync.
    // "model" and "apiKeyHelper" live in SettingsJson.
    // "theme" and "preferredNotifChannel" live in GlobalConfig.
    private static readonly string[] SyncKeys =
        ["model", "apiKeyHelper", "theme", "preferredNotifChannel"];

    /// <summary>Maximum serialised request-body size permitted for an upload.</summary>
    private const int MaxPayloadBytes = 500 * 1024; // 500 KB

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigProvider _configProvider;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Initialises a new instance of <see cref="SettingsSyncService"/>.
    /// </summary>
    /// <param name="httpClientFactory">
    /// Factory used to create <see cref="HttpClient"/> instances.
    /// The named client <c>"SettingsSync"</c> is requested; register it in DI
    /// with any desired timeouts or handlers.
    /// </param>
    /// <param name="configProvider">
    /// Provides access to the merged local settings and global config, and
    /// exposes a <see cref="IConfigProvider.Reload"/> method so in-memory state
    /// is refreshed after a successful download.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="httpClientFactory"/> or
    /// <paramref name="configProvider"/> is <see langword="null"/>.
    /// </exception>
    public SettingsSyncService(
        IHttpClientFactory httpClientFactory,
        IConfigProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(configProvider);

        _httpClientFactory = httpClientFactory;
        _configProvider = configProvider;
    }

    /// <summary>
    /// Syncs settings with the remote CCR backend.
    /// Gated on feature flags <c>upload-user-settings</c> and
    /// <c>download-user-settings</c>; returns immediately if neither flag is
    /// enabled. Designed for fire-and-forget use from startup; awaiting is
    /// optional. All exceptions are swallowed internally.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        var doUpload   = FeatureFlags.IsEnabled("upload-user-settings");
        var doDownload = FeatureFlags.IsEnabled("download-user-settings");

        if (!doUpload && !doDownload)
            return;

        try
        {
            // Resolve API credentials the same way AnthropicClient does.
            var providerConfig = ApiProviderFactory.Detect(
                _configProvider.GlobalConfig.PrimaryApiKey);

            var baseUrl = providerConfig.BaseUrl.TrimEnd('/');
            var token   = providerConfig.ApiKey;

            // Without a bearer token we cannot authenticate against the CCR backend.
            if (string.IsNullOrEmpty(token))
                return;

            var httpClient = _httpClientFactory.CreateClient("SettingsSync");

            if (doDownload)
                await DownloadAndMergeAsync(httpClient, baseUrl, token, ct).ConfigureAwait(false);

            if (doUpload)
                await UploadAsync(httpClient, baseUrl, token, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fire-and-forget path: swallow all unexpected exceptions so a
            // transient network error never surfaces to the user on startup.
        }
    }

    // -------------------------------------------------------------------------
    // Private — download direction
    // -------------------------------------------------------------------------

    private async Task DownloadAndMergeAsync(
        HttpClient httpClient,
        string baseUrl,
        string token,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/api/claude_code/user_settings");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return;

            var body = await response.Content
                .ReadAsStringAsync(ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("settings", out var settingsElement))
                return;

            // Collect only SyncKeys that appear in the remote response.
            var remoteValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in SyncKeys)
            {
                if (!settingsElement.TryGetProperty(key, out var valElem))
                    continue;

                // Skip null and non-string remote values — we only sync string fields.
                if (valElem.ValueKind != JsonValueKind.String)
                    continue;

                var remoteValue = valElem.GetString();
                if (remoteValue is null)
                    continue;

                remoteValues[key] = remoteValue;
            }

            if (remoteValues.Count == 0)
                return;

            await PatchUserSettingsFileAsync(remoteValues).ConfigureAwait(false);

            // Refresh in-memory state so the rest of the session sees the new values.
            _configProvider.Reload();
        }
        catch
        {
            // Non-200 status, network failure, or malformed JSON — swallow silently.
        }
    }

    /// <summary>
    /// Reads the user <c>settings.json</c> file, applies <paramref name="patches"/>
    /// for keys whose value has changed, and writes the result back.
    /// All other keys in the file are preserved untouched.
    /// </summary>
    private static async Task PatchUserSettingsFileAsync(
        Dictionary<string, string> patches)
    {
        var path = ConfigPaths.UserSettingsPath;

        // Load the existing file as a generic key→JsonElement map, or start empty.
        Dictionary<string, JsonElement> existing = [];
        if (File.Exists(path))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson)
                           ?? [];
            }
            catch
            {
                // Malformed or unreadable file — start fresh rather than aborting.
                existing = [];
            }
        }

        var merged     = new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal);
        var hasChanges = false;

        foreach (var (key, value) in patches)
        {
            // Skip if the file already contains the same string value.
            if (merged.TryGetValue(key, out var current)
                && current.ValueKind == JsonValueKind.String
                && current.GetString() == value)
            {
                continue;
            }

            merged[key] = JsonSerializer.SerializeToElement(value);
            hasChanges   = true;
        }

        if (!hasChanges)
            return;

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var outputJson = JsonSerializer.Serialize(merged, JsonWriteOptions);
        await File.WriteAllTextAsync(path, outputJson).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private — upload direction
    // -------------------------------------------------------------------------

    private async Task UploadAsync(
        HttpClient httpClient,
        string baseUrl,
        string token,
        CancellationToken ct)
    {
        try
        {
            var settings     = _configProvider.Settings;
            var globalConfig = _configProvider.GlobalConfig;

            // Build payload from only the SyncKeys that have non-null local values.
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in SyncKeys)
            {
                var value = GetLocalValue(key, settings, globalConfig);
                if (value is not null)
                    payload[key] = value;
            }

            if (payload.Count == 0)
                return;

            var bodyJson = JsonSerializer.Serialize(new { settings = payload });

            // Guard against sending an unexpectedly large payload.
            if (Encoding.UTF8.GetByteCount(bodyJson) > MaxPayloadBytes)
                return;

            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/api/claude_code/user_settings");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            // Assign content to the request; HttpRequestMessage.Dispose() will
            // clean up the content, so we must not wrap content in a using statement.
            request.Content = content;

            using var response = await httpClient
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            // Non-200 responses are silently ignored per spec.
            _ = response;
        }
        catch
        {
            // Network failure, serialisation error, or cancellation — swallow silently.
        }
    }

    // -------------------------------------------------------------------------
    // Private — helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the local string value for the given sync key, reading from the
    /// appropriate config record.
    /// </summary>
    private static string? GetLocalValue(
        string key,
        SettingsJson settings,
        GlobalConfig globalConfig)
    {
        return key switch
        {
            "model"                 => settings.Model,
            "apiKeyHelper"          => settings.ApiKeyHelper,
            "theme"                 => globalConfig.Theme,
            "preferredNotifChannel" => globalConfig.PreferredNotifChannel,
            _                       => null,
        };
    }
}

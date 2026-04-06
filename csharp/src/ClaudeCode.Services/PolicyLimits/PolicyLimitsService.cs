namespace ClaudeCode.Services.PolicyLimits;

using System.Text.Json;
using System.Text.Json.Serialization;

using ClaudeCode.Configuration;
using ClaudeCode.Services.Api;

// ---------------------------------------------------------------------------
// File-scoped DTOs — not visible outside this file
// ---------------------------------------------------------------------------

file sealed class PolicyLimitsApiResponse
{
    [JsonPropertyName("restrictions")]
    public Dictionary<string, PolicyRestrictionEntry>? Restrictions { get; init; }
}

file sealed class PolicyRestrictionEntry
{
    [JsonPropertyName("allowed")]
    public bool Allowed { get; init; }
}

file sealed class PolicyLimitsCache
{
    [JsonPropertyName("restrictions")]
    public Dictionary<string, bool>? Restrictions { get; init; }
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// <summary>
/// Fetches and caches policy restriction rules from the Anthropic backend.
/// Results are cached locally under <c>~/.claude/policy-limits.json</c> and refreshed hourly.
/// </summary>
/// <remarks>
/// The service always fails open: <see cref="IsPolicyAllowed"/> returns <see langword="true"/>
/// for unknown policies and while initialization is in progress.
/// </remarks>
public sealed class PolicyLimitsService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigProvider _configProvider;

    // volatile: reference-type assignments are atomic; volatile ensures cross-thread visibility
    // without a lock on the hot path (IsPolicyAllowed). The dictionary is never mutated after
    // being assigned — we always replace the reference with a freshly-built instance.
    private volatile string? _etag;
    private volatile Dictionary<string, bool> _restrictions = new();

    private Timer? _pollTimer;
    private readonly string _cachePath; // ~/.claude/policy-limits.json
    private readonly TaskCompletionSource<bool> _initComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const int PollIntervalSeconds = 3600;
    private const int InitTimeoutSeconds = 30;

    /// <summary>
    /// Initializes a new instance of <see cref="PolicyLimitsService"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to create <see cref="System.Net.Http.HttpClient"/> instances.</param>
    /// <param name="configProvider">Provides access to merged settings and global config.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="httpClientFactory"/> or <paramref name="configProvider"/> is <see langword="null"/>.
    /// </exception>
    public PolicyLimitsService(IHttpClientFactory httpClientFactory, IConfigProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(configProvider);

        _httpClientFactory = httpClientFactory;
        _configProvider = configProvider;
        _cachePath = Path.Combine(ConfigPaths.ClaudeHomeDir, "policy-limits.json");
    }

    /// <summary>
    /// Starts the service: loads from the local file cache, then fetches from the API.
    /// Signals initialization complete when the fetch succeeds, fails, or times out after
    /// <c>30</c> seconds. Starts an hourly background polling timer.
    /// </summary>
    /// <param name="ct">Cancellation token; cancelling aborts only the initial fetch,
    /// not the subsequent polling.</param>
    public async Task InitAsync(CancellationToken ct = default)
    {
        // Populate from disk cache immediately so IsPolicyAllowed can serve data
        // without waiting for the network round-trip.
        LoadFromCache();

        // Perform the initial API fetch with a hard 30-second timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(InitTimeoutSeconds));

        try
        {
            await FetchAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Timeout, cancellation, or network error — fail open.
        }
        finally
        {
            _initComplete.TrySetResult(true);
        }

        // Start hourly background polling (fire-and-forget).
        _pollTimer = new Timer(
            callback: _ => _ = PollBackgroundAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(PollIntervalSeconds),
            period: TimeSpan.FromSeconds(PollIntervalSeconds));
    }

    /// <summary>
    /// Returns whether the named policy is allowed.
    /// </summary>
    /// <param name="policy">
    /// The policy identifier to look up (e.g. <c>allow_product_feedback</c>).
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the policy is explicitly allowed, if the service has
    /// not yet completed initialization, or if the policy is unknown.
    /// <see langword="false"/> only when the service has initialized and the policy is
    /// explicitly restricted.
    /// </returns>
    public bool IsPolicyAllowed(string policy)
    {
        if (!_initComplete.Task.IsCompleted) return true; // fail open before init
        if (_restrictions.TryGetValue(policy, out var allowed)) return allowed;
        return true; // fail open for unknown policies
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _pollTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Background polling wrapper — swallows all exceptions so the
    /// <see cref="Timer"/> callback is never broken by transient failures.
    /// </summary>
    private async Task PollBackgroundAsync()
    {
        try
        {
            await FetchAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: polling errors must never surface to the timer thread.
        }
    }

    /// <summary>
    /// Performs a single GET request to the policy limits endpoint and updates
    /// <see cref="_restrictions"/> and <see cref="_etag"/> on a successful response.
    /// </summary>
    private async Task FetchAsync(CancellationToken ct)
    {
        var oauthToken = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                         ?? _configProvider.GlobalConfig.PrimaryApiKey;

        if (string.IsNullOrEmpty(oauthToken))
            return; // No credential available — skip without throwing.

        var baseUrl = (Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
                       ?? ApiConstants.DefaultBaseUrl).TrimEnd('/');

        var url = $"{baseUrl}/api/claude_code/policy_limits";

        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {oauthToken}");

        // Read current ETag under volatile — safe single-read capture.
        var currentEtag = _etag;
        if (currentEtag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", currentEtag);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        // 304 Not Modified — existing data remains valid.
        if ((int)response.StatusCode == 304)
            return;

        // Any non-success status other than 304 — fail open, keep existing data.
        if (!response.IsSuccessStatusCode)
            return;

        // Capture ETag from response for the next conditional request.
        var newEtag = response.Headers.ETag?.Tag;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PolicyLimitsApiResponse>(json, JsonOptions);

        if (parsed?.Restrictions is null)
            return; // Empty or unexpected body — keep existing data.

        // Flatten { "policyName": { "allowed": bool } } → { "policyName": bool }.
        var newRestrictions = new Dictionary<string, bool>(
            parsed.Restrictions.Count, StringComparer.Ordinal);

        foreach (var (key, entry) in parsed.Restrictions)
            newRestrictions[key] = entry.Allowed;

        // Atomic volatile writes — readers in IsPolicyAllowed capture a consistent
        // snapshot because we never mutate either dictionary after this point.
        _restrictions = newRestrictions;
        if (newEtag is not null)
            _etag = newEtag;

        await SaveToCacheAsync(newRestrictions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the on-disk cache into <see cref="_restrictions"/>.
    /// Missing or corrupt files are silently ignored.
    /// </summary>
    private void LoadFromCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            var json = File.ReadAllText(_cachePath);
            var cache = JsonSerializer.Deserialize<PolicyLimitsCache>(json, JsonOptions);

            if (cache?.Restrictions is null || cache.Restrictions.Count == 0)
                return;

            _restrictions = new Dictionary<string, bool>(
                cache.Restrictions, StringComparer.Ordinal);
        }
        catch
        {
            // Corrupt or inaccessible cache — fail open with empty restrictions.
        }
    }

    /// <summary>
    /// Persists <paramref name="restrictions"/> to the local file cache.
    /// Write failures are silently ignored.
    /// </summary>
    private async Task SaveToCacheAsync(Dictionary<string, bool> restrictions, CancellationToken ct)
    {
        try
        {
            var cacheDir = Path.GetDirectoryName(_cachePath);
            if (cacheDir is not null && !Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            var cache = new PolicyLimitsCache { Restrictions = restrictions };
            var json = JsonSerializer.Serialize(cache, JsonOptions);

            await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);
        }
        catch
        {
            // Write failures are non-fatal.
        }
    }
}

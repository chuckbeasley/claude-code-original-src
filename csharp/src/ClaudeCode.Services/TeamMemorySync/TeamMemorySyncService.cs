namespace ClaudeCode.Services.TeamMemorySync;

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeCode.Configuration;

/// <summary>
/// Syncs team memory between the local ~/.claude/memory/team/ directory and
/// the remote CCR backend. Gated on FeatureFlags.IsEnabled("teammem").
/// Mirrors src/services/teamMemorySync.ts.
/// </summary>
public sealed class TeamMemorySyncService : IAsyncDisposable
{
    private readonly HttpClient         _http;
    private readonly string             _baseUrl;
    private readonly string             _apiKey;
    private readonly string             _teamMemDir;

    private readonly Dictionary<string, string> _remoteHashes  = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim              _pushLock       = new(1, 1);
    private FileSystemWatcher?                  _watcher;
    private System.Threading.Timer?             _debounce;
    private string?                             _etag;

    private const int  MaxBatchBytes = 200 * 1024;   // 200 KB
    private const long DebounceMs    = 2_000;

    public TeamMemorySyncService(HttpClient http, string baseUrl, string apiKey, string teamMemDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http       = http;
        _baseUrl    = baseUrl.TrimEnd('/');
        _apiKey     = apiKey;
        _teamMemDir = teamMemDir;
    }

    /// <summary>
    /// Pulls team memory from the remote server (ETag-cached) and sets up
    /// a FileSystemWatcher to push local changes. Gated on "teammem" flag.
    /// </summary>
    public async Task InitAsync(CancellationToken ct = default)
    {
        if (!FeatureFlags.IsEnabled("teammem")) return;
        if (!Directory.Exists(_teamMemDir)) Directory.CreateDirectory(_teamMemDir);

        await PullAsync(ct);
        StartWatcher();
    }

    private async Task PullAsync(CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/claude_code/team_memory");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            if (_etag is not null)
                req.Headers.TryAddWithoutValidation("If-None-Match", _etag);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified) return;
            if (!resp.IsSuccessStatusCode) return;

            _etag = resp.Headers.ETag?.Tag;
            var body = await resp.Content.ReadAsStringAsync(ct);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
            if (entries is null) return;

            foreach (var (key, value) in entries)
            {
                var path = Path.Combine(_teamMemDir, SanitiseKey(key));
                await File.WriteAllTextAsync(path, value, ct);
                _remoteHashes[key] = ComputeHash(value);
            }
        }
        catch { /* fail-open */ }
    }

    private async Task PushAsync(CancellationToken ct = default)
    {
        if (!await _pushLock.WaitAsync(0, ct)) return;
        try
        {
            var changed = new Dictionary<string, string>();
            long batchSize = 0;

            foreach (var file in Directory.EnumerateFiles(_teamMemDir, "*.md"))
            {
                var key     = Path.GetFileNameWithoutExtension(file);
                var content = await File.ReadAllTextAsync(file, ct);

                // Block uploads containing secrets.
                if (SecretScanner.Scan(content).Count > 0) continue;

                var hash = ComputeHash(content);
                if (_remoteHashes.TryGetValue(key, out var prev) && prev == hash) continue;

                batchSize += Encoding.UTF8.GetByteCount(content);
                if (batchSize > MaxBatchBytes) break;

                changed[key] = content;
                _remoteHashes[key] = hash;
            }

            if (changed.Count == 0) return;

            var body = JsonSerializer.Serialize(changed);
            var req  = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/api/claude_code/team_memory")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            using var resp = await _http.SendAsync(req, ct);
            // Ignore non-success — next push will retry.
        }
        catch { /* fail-open */ }
        finally { _pushLock.Release(); }
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_teamMemDir, "*.md")
        {
            NotifyFilter           = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents    = true,
            IncludeSubdirectories  = false,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
    }

    private void OnFileChanged(object _, FileSystemEventArgs __)
    {
        // Debounce: reset timer on each event, fire after 2 s of quiet.
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(
            _ => _ = PushAsync(),
            null,
            DebounceMs,
            System.Threading.Timeout.Infinite);
    }

    private static string SanitiseKey(string key)
    {
        // Strip any path-separator characters so keys can't escape the team dir.
        var safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
        return safe + ".md";
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash  = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        await _pushLock.WaitAsync();
        _pushLock.Dispose();
    }
}

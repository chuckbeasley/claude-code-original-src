namespace ClaudeCode.Configuration;

using ClaudeCode.Configuration.Settings;

/// <summary>
/// Provides access to the merged application settings and global config.
/// </summary>
public interface IConfigProvider
{
    /// <summary>Gets the current merged settings from all sources.</summary>
    SettingsJson Settings { get; }

    /// <summary>Gets the current global config (persisted session state).</summary>
    GlobalConfig GlobalConfig { get; }

    /// <summary>
    /// Reloads settings and global config from disk.
    /// </summary>
    /// <param name="cwd">
    /// If provided, changes the working directory used for project-scoped lookups
    /// before reloading.
    /// </param>
    void Reload(string? cwd = null);

    /// <summary>Raised after settings have been reloaded.</summary>
    event Action? SettingsChanged;
}

/// <summary>
/// DI-friendly implementation of <see cref="IConfigProvider"/>.
/// Watches the project's <c>.claude/</c> directory for JSON changes and
/// automatically reloads settings with a 500 ms debounce.
/// </summary>
public sealed class ConfigProvider : IConfigProvider, IDisposable
{
    private readonly SettingsLoader _loader = new();
    private string _cwd;
    private SettingsJson _settings;
    private GlobalConfig _globalConfig;
    private FileSystemWatcher? _watcher;

    // Debounce timer is a field so it stays alive between watcher events
    // and can be safely disposed when the next event fires.
    private Timer? _debounce;

    // Guards Reload() against overlapping calls from the debounce timer and
    // an explicit caller.
    private readonly object _reloadLock = new();

    /// <summary>
    /// Initialises the provider and performs an initial load from disk.
    /// Also begins watching the project <c>.claude/</c> directory if it exists.
    /// </summary>
    /// <param name="cwd">
    /// The current working directory used to locate project-scoped settings files.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cwd"/> is null or whitespace.
    /// </exception>
    public ConfigProvider(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        _cwd = cwd;
        _settings = _loader.LoadMergedSettings(cwd);
        _globalConfig = _loader.LoadGlobalConfig();
        SetupFileWatcher();
    }

    /// <inheritdoc/>
    public SettingsJson Settings => _settings;

    /// <inheritdoc/>
    public GlobalConfig GlobalConfig => _globalConfig;

    /// <inheritdoc/>
    public event Action? SettingsChanged;

    /// <inheritdoc/>
    public void Reload(string? cwd = null)
    {
        lock (_reloadLock)
        {
            if (cwd is not null)
                _cwd = cwd;

            _settings = _loader.LoadMergedSettings(_cwd);
            _globalConfig = _loader.LoadGlobalConfig();
        }

        SettingsChanged?.Invoke();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _debounce?.Dispose();
        _watcher?.Dispose();
        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private void SetupFileWatcher()
    {
        var claudeDir = Path.Combine(_cwd, ".claude");
        if (!Directory.Exists(claudeDir))
            return;

        _watcher = new FileSystemWatcher(claudeDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Dispose the previous debounce timer and start a new 500 ms window.
        // All accesses to _debounce happen on the thread-pool threads that the
        // FileSystemWatcher uses, which are serialised by the watcher itself for
        // a single watcher instance, so no additional lock is needed here.
        var previous = Interlocked.Exchange(ref _debounce, null);
        previous?.Dispose();

        _debounce = new Timer(
            callback: _ => Reload(),
            state: null,
            dueTime: 500,
            period: Timeout.Infinite);
    }
}

namespace ClaudeCode.Services.Plugins;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeCode.Configuration.Settings;
using ClaudeCode.Core.Tools;

/// <summary>
/// Discovers and loads plugin definitions from the user's plugin directory
/// (~/.claude/plugins/) and the project's .claude/plugins/ directory.
/// Each plugin is a directory containing a plugin.json manifest.
/// </summary>
public sealed class PluginLoader
{
    private static readonly string GlobalPluginDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "plugins");

    /// <summary>
    /// Loads all plugin manifests from the global and project-local plugin directories.
    /// </summary>
    /// <param name="cwd">Current working directory (used to find .claude/plugins/).</param>
    /// <returns>List of loaded plugin entries; empty if no plugins found.</returns>
    public List<PluginEntry> LoadAll(string cwd)
    {
        var entries = new List<PluginEntry>();

        // Global: ~/.claude/plugins/
        LoadFromDirectory(GlobalPluginDir, entries);

        // Project-local: <cwd>/.claude/plugins/
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var localDir = Path.Combine(cwd, ".claude", "plugins");
            LoadFromDirectory(localDir, entries);
        }

        return entries;
    }

    /// <summary>
    /// Loads all plugins from <see cref="LoadAll"/>, then registers their tools into
    /// <paramref name="registry"/>. Script-based and assembly-based entry points are both handled.
    /// </summary>
    /// <param name="cwd">Current working directory.</param>
    /// <param name="registry">The tool registry to populate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is <see langword="null"/>.</exception>
    public void LoadAndRegisterAll(string cwd, ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var entries = LoadAll(cwd);
        foreach (var entry in entries)
            RegisterPluginTools(entry, registry);
    }

    /// <summary>
    /// Inspects <paramref name="entry"/>'s manifest and registers any tools it exposes
    /// into <paramref name="registry"/>.
    /// </summary>
    /// <param name="entry">The loaded plugin entry to process.</param>
    /// <param name="registry">The tool registry to populate.</param>
    /// <exception cref="ArgumentNullException">Thrown when either parameter is <see langword="null"/>.</exception>
    public void RegisterPluginTools(PluginEntry entry, ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(registry);

        var entryPoint = entry.Manifest.EntryPoint;
        if (string.IsNullOrWhiteSpace(entryPoint))
            return;

        var ext = Path.GetExtension(entryPoint).ToLowerInvariant();

        if (ext is ".sh" or ".ps1" or ".bat")
        {
            var scriptPath = Path.Combine(entry.Directory, entryPoint);
            var tool = new ScriptPluginTool(entry.Name, entry.Description, scriptPath);
            registry.Register(tool);
        }
        else if (ext is ".dll")
        {
            var assemblyPath = Path.Combine(entry.Directory, entryPoint);
            LoadAndRegisterAssemblyTools(assemblyPath, registry);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static void LoadFromDirectory(string dir, List<PluginEntry> entries)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var pluginDir in Directory.EnumerateDirectories(dir))
        {
            var manifestPath = Path.Combine(pluginDir, "plugin.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest is null)
                    continue;

                entries.Add(new PluginEntry(
                    Name: manifest.Name ?? Path.GetFileName(pluginDir),
                    Version: manifest.Version ?? "0.0.0",
                    Description: manifest.Description ?? string.Empty,
                    Directory: pluginDir,
                    Manifest: manifest));
            }
            catch
            {
                // Malformed manifest — skip silently.
            }
        }
    }

    private static void LoadAndRegisterAssemblyTools(string assemblyPath, ToolRegistry registry)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(assemblyPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[plugin] Warning: failed to load assembly '{assemblyPath}': {ex.Message}");
            return;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException rtle)
        {
            // Partial load — work with whatever types were successfully resolved.
            types = rtle.Types.Where(t => t is not null).ToArray()!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[plugin] Warning: failed to enumerate types in '{assemblyPath}': {ex.Message}");
            return;
        }

        var iToolType = typeof(ITool);
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            if (!iToolType.IsAssignableFrom(type))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is ITool instance)
                    registry.Register(instance);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[plugin] Warning: failed to instantiate '{type.FullName}' from '{assemblyPath}': {ex.Message}");
            }
        }
    }
}

/// <summary>A loaded plugin with its resolved directory path.</summary>
/// <param name="Name">Plugin name from manifest or directory name.</param>
/// <param name="Version">Plugin version string.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Directory">Absolute path to the plugin directory.</param>
/// <param name="Manifest">The raw deserialized manifest.</param>
public record PluginEntry(
    string Name,
    string Version,
    string Description,
    string Directory,
    PluginManifest Manifest);

/// <summary>Declares a slash command provided by a plugin.</summary>
public record PluginCommandDefinition
{
    /// <summary>Command name without leading slash (e.g. "deploy").</summary>
    public string? Name { get; init; }

    /// <summary>One-line description shown in /help.</summary>
    public string? Description { get; init; }

    /// <summary>Script filename relative to the plugin directory (e.g. "deploy.sh").</summary>
    public string? Script { get; init; }

    /// <summary>Usage hint shown in /help (optional).</summary>
    public string? Usage { get; init; }
}

/// <summary>JSON manifest structure for a Claude Code plugin.</summary>
public sealed class PluginManifest
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? EntryPoint { get; init; }
    public List<string>? Skills { get; init; }
    public Dictionary<string, JsonElement>? Config { get; init; }

    /// <summary>Optional list of slash commands contributed by this plugin.</summary>
    public List<PluginCommandDefinition>? Commands { get; init; }
}

/// <summary>
/// An <see cref="ITool"/> wrapper that delegates execution to a script file
/// (.sh, .ps1, or .bat). The model passes a single string argument that is
/// forwarded to the script as a command-line argument.
/// </summary>
internal sealed class ScriptPluginTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            input = new
            {
                type = "string",
                description = "Arguments to pass to the plugin script"
            }
        }
    });

    private const int ScriptTimeoutMs = 30_000;

    private readonly string _toolName;
    private readonly string _description;
    private readonly string _scriptPath;

    /// <summary>
    /// Initializes a new <see cref="ScriptPluginTool"/>.
    /// </summary>
    /// <param name="pluginName">The raw plugin name (spaces and special chars are normalised).</param>
    /// <param name="description">Human-readable description forwarded to the model.</param>
    /// <param name="scriptPath">Absolute path to the script file.</param>
    internal ScriptPluginTool(string pluginName, string description, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(pluginName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(scriptPath);

        // Normalise: lowercase, replace spaces with underscores, prefix.
        var normalised = pluginName.ToLowerInvariant().Replace(' ', '_');
        _toolName = $"plugin_{normalised}";
        _description = description;
        _scriptPath = scriptPath;
    }

    /// <inheritdoc/>
    public string Name => _toolName;

    /// <inheritdoc/>
    public JsonElement GetInputSchema() => _inputSchema;

    /// <inheritdoc/>
    public Task<string> GetDescriptionAsync(CancellationToken ct = default) =>
        Task.FromResult(_description);

    /// <inheritdoc/>
    public Task<string> GetPromptAsync(CancellationToken ct = default) =>
        Task.FromResult(
            $"Use the `{_toolName}` tool to invoke the '{_toolName}' plugin. " +
            "Pass any required arguments as the `input` string.");

    /// <inheritdoc/>
    public string UserFacingName(JsonElement? input = null) => _toolName;

    /// <inheritdoc/>
    public async Task<string> ExecuteRawAsync(
        JsonElement input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        var args = string.Empty;
        if (input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty("input", out var inputProp) &&
            inputProp.ValueKind == JsonValueKind.String)
        {
            args = inputProp.GetString() ?? string.Empty;
        }

        var psi = BuildScriptProcessInfo(_scriptPath, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ScriptTimeoutMs);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            return stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return $"Error: script '{_scriptPath}' timed out after {ScriptTimeoutMs / 1000}s.";
        }
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private static ProcessStartInfo BuildScriptProcessInfo(string scriptPath, string args)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => new ProcessStartInfo("pwsh", $"-File \"{scriptPath}\" {args}"),
            ".bat" => new ProcessStartInfo("cmd", $"/c \"{scriptPath}\" {args}"),
            // .sh — use bash on all platforms (available via Git Bash / WSL on Windows)
            _ => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new ProcessStartInfo("bash", $"\"{scriptPath}\" {args}")
                : new ProcessStartInfo("bash", $"\"{scriptPath}\" {args}")
        };
    }
}

namespace ClaudeCode.Commands;

using System.Diagnostics;
using ClaudeCode.Services.Plugins;
using Spectre.Console;

/// <summary>
/// A <see cref="SlashCommand"/> that delegates execution to a script file.
/// Added to the command registry when a plugin manifest declares a <c>commands</c> entry.
/// </summary>
/// <remarks>
/// This class lives in <c>ClaudeCode.Commands</c> rather than <c>ClaudeCode.Services</c>
/// to avoid the circular project reference that would arise if Services referenced Commands
/// (Commands already references Services).
/// </remarks>
public sealed class ScriptPluginCommand : SlashCommand
{
    private const int TimeoutMs = 30_000;

    private readonly string _name;
    private readonly string _description;
    private readonly string _scriptPath;
    private readonly string _pluginDir;

    /// <summary>
    /// Initializes a new <see cref="ScriptPluginCommand"/> from a plugin command definition.
    /// </summary>
    /// <param name="def">The command definition from the plugin manifest.</param>
    /// <param name="pluginDir">Absolute path to the plugin directory; used to resolve the script path.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="def"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="pluginDir"/> is null/whitespace, or
    /// <paramref name="def"/> is missing <c>Name</c> or <c>Script</c>.
    /// </exception>
    public ScriptPluginCommand(PluginCommandDefinition def, string pluginDir)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDir);

        if (string.IsNullOrWhiteSpace(def.Name))
            throw new ArgumentException("Plugin command definition must have a Name.", nameof(def));
        if (string.IsNullOrWhiteSpace(def.Script))
            throw new ArgumentException("Plugin command definition must have a Script.", nameof(def));

        _name = def.Name.StartsWith('/') ? def.Name : $"/{def.Name}";
        _description = def.Description ?? $"Plugin command: {_name}";
        _scriptPath = Path.Combine(pluginDir, def.Script);
        _pluginDir = pluginDir;
    }

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override string Description => _description;

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(
        CommandContext ctx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!File.Exists(_scriptPath))
        {
            ctx.WriteMarkup(
                $"[red]Plugin command '{_name}': script '{Path.GetFileName(_scriptPath)}' not found in {Markup.Escape(_pluginDir)}[/]");
            return true;
        }

        var args = ctx.Args.Length > 0 ? string.Join(" ", ctx.Args) : string.Empty;
        var psi = BuildProcessInfo(_scriptPath, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow  = true;

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutMs);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout))
                ctx.Write(stdout.TrimEnd());

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                ctx.WriteMarkup($"[red][error][/] {Markup.Escape(stderr.TrimEnd())}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            ctx.WriteMarkup($"[yellow]Plugin command '{_name}' timed out after {TimeoutMs / 1000}s.[/]");
        }

        return true;
    }

    private static ProcessStartInfo BuildProcessInfo(string scriptPath, string args)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => new ProcessStartInfo("pwsh", $"-File \"{scriptPath}\" {args}"),
            ".bat" => new ProcessStartInfo("cmd", $"/c \"{scriptPath}\" {args}"),
            _      => new ProcessStartInfo("bash", $"\"{scriptPath}\" {args}"),
        };
    }
}

/// <summary>
/// Extension methods for <see cref="PluginLoader"/> that expose slash-command loading.
/// Defined in <c>ClaudeCode.Commands</c> (not <c>ClaudeCode.Services</c>) to avoid the
/// circular project reference: Commands → Services is already established; Services → Commands
/// would be circular.
/// </summary>
public static class PluginLoaderCommandExtensions
{
    /// <summary>
    /// Loads all plugins and returns a <see cref="ScriptPluginCommand"/> for each valid
    /// command definition found in their manifests. Skips entries missing Name or Script.
    /// </summary>
    /// <param name="loader">The plugin loader instance.</param>
    /// <param name="cwd">Current working directory used to locate project-local plugins.</param>
    /// <returns>
    /// A lazily-evaluated sequence of <see cref="SlashCommand"/> instances, one per valid
    /// <see cref="PluginCommandDefinition"/> found across all loaded plugin manifests.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="loader"/> is <see langword="null"/>.</exception>
    public static IEnumerable<SlashCommand> LoadCommands(
        this PluginLoader loader,
        string cwd)
    {
        ArgumentNullException.ThrowIfNull(loader);

        var entries = loader.LoadAll(cwd);
        foreach (var entry in entries)
        {
            if (entry.Manifest.Commands is null) continue;

            foreach (var def in entry.Manifest.Commands)
            {
                if (string.IsNullOrWhiteSpace(def.Name) || string.IsNullOrWhiteSpace(def.Script))
                {
                    Console.Error.WriteLine(
                        $"[plugin] Warning: command in plugin '{entry.Name}' missing Name or Script — skipped.");
                    continue;
                }

                yield return new ScriptPluginCommand(def, entry.Directory);
            }
        }
    }
}

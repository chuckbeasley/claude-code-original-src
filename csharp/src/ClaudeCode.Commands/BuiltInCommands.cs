namespace ClaudeCode.Commands;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClaudeCode.Configuration;
using ClaudeCode.Core.State;
using ClaudeCode.Services.Api;
using ClaudeCode.Services.Session;
using Spectre.Console;

/// <summary>Displays a table of all registered commands and their descriptions.</summary>
public sealed class HelpCommand : SlashCommand
{
    private readonly CommandRegistry _registry;

    /// <summary>Initializes a new <see cref="HelpCommand"/> with the registry it will enumerate.</summary>
    /// <param name="registry">The command registry. Must not be <see langword="null"/>.</param>
    public HelpCommand(CommandRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc/>
    public override string Name => "/help";

    /// <inheritdoc/>
    public override string[] Aliases => ["/h", "/?"];

    /// <inheritdoc/>
    public override string Description => "Show available commands";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var allCmds = _registry.GetAll().OrderBy(c => c.Name).ToList();
        var pluginNames = ctx.PluginCommandNames ?? [];

        var builtIn = allCmds.Where(c => !pluginNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var plugin  = allCmds.Where(c => pluginNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Command");
        table.AddColumn("Description");

        foreach (var cmd in builtIn)
            table.AddRow(cmd.Name.EscapeMarkup(), cmd.Description.EscapeMarkup());

        AnsiConsole.Write(table);

        if (plugin.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[grey]Plugin Commands[/]");
            var pt = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            pt.AddColumn("Command");
            pt.AddColumn("Description");
            foreach (var cmd in plugin)
                pt.AddRow(cmd.Name.EscapeMarkup(), cmd.Description.EscapeMarkup());
            AnsiConsole.Write(pt);
        }

        return Task.FromResult(true);
    }
}

/// <summary>Clears the terminal screen.</summary>
public sealed class ClearCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/clear";

    /// <inheritdoc/>
    public override string Description => "Clear the screen";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.ClearScreen?.Invoke();
        return Task.FromResult(true);
    }
}

/// <summary>Displays per-model token usage and session cost.</summary>
public sealed class CostCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/cost";

    /// <inheritdoc/>
    public override string Description => "Show session cost and token usage";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.CostTracker is not { } tracker)
            return Task.FromResult(true);

        ctx.WriteMarkup($"[grey]{tracker.FormatUsageSummary().EscapeMarkup()}[/]");

        var usage = tracker.GetModelUsage();
        if (usage.Count == 0)
            return Task.FromResult(true);

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Model");
        table.AddColumn("Input");
        table.AddColumn("Output");
        table.AddColumn("Cost");

        foreach (var (modelId, u) in usage)
        {
            table.AddRow(
                modelId.EscapeMarkup(),
                u.InputTokens.ToString("N0"),
                u.OutputTokens.ToString("N0"),
                ClaudeCode.Services.Api.CostTracker.FormatCost(u.CostUsd));
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>Shows the current model ID and conversation message count.</summary>
public sealed class ModelCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/model";

    /// <inheritdoc/>
    public override string Description => "Show current model";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.CurrentModel is not null)
        {
            var display = ClaudeCode.Services.Api.ModelResolver.GetDisplayName(ctx.CurrentModel);
            ctx.WriteMarkup(
                $"[grey]Current model: {display.EscapeMarkup()} ({ctx.CurrentModel.EscapeMarkup()})[/]");
        }

        ctx.WriteMarkup($"[grey]Conversation: {ctx.ConversationMessageCount} messages[/]");
        return Task.FromResult(true);
    }
}

/// <summary>Prints a snapshot of the current session state.</summary>
public sealed class StatusCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/status";

    /// <inheritdoc/>
    public override string Description => "Show session status";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup($"[grey]Working directory: {ctx.Cwd.EscapeMarkup()}[/]");

        if (ctx.CurrentModel is not null)
        {
            ctx.WriteMarkup(
                $"[grey]Model: {ClaudeCode.Services.Api.ModelResolver.GetDisplayName(ctx.CurrentModel).EscapeMarkup()}[/]");
        }

        ctx.WriteMarkup($"[grey]Messages: {ctx.ConversationMessageCount}[/]");

        if (ctx.CostTracker is not null)
            ctx.WriteMarkup($"[grey]{ctx.CostTracker.FormatUsageSummary().EscapeMarkup()}[/]");

        return Task.FromResult(true);
    }
}

/// <summary>Prints the application version string.</summary>
public sealed class VersionCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/version";

    /// <inheritdoc/>
    public override string[] Aliases => ["/v"];

    /// <inheritdoc/>
    public override string Description => "Show version";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var version = ctx.Version ?? "unknown";
        ctx.Write($"{version} (ClaudeCode C#)");
        return Task.FromResult(true);
    }
}

/// <summary>Signals the REPL to exit the interactive session.</summary>
public sealed class ExitCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/exit";

    /// <inheritdoc/>
    public override string[] Aliases => ["/quit", "/q"];

    /// <inheritdoc/>
    public override string Description => "Exit the session";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.RequestExit?.Invoke();
        return Task.FromResult(true);
    }
}

/// <summary>Runs <c>git diff --stat</c> in the working directory and prints the result.</summary>
public sealed class DiffCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/diff";

    /// <inheritdoc/>
    public override string Description => "Show git diff of current changes";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "diff --stat")
            {
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[yellow]Could not start git process.[/]");
                return true;
            }

            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
                ctx.WriteMarkup("[grey]No changes detected.[/]");
            else
                ctx.Write(output.TrimEnd());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }
}

/// <summary>
/// Shows <c>git status --short</c> in the working directory and prompts the user
/// to ask Claude to create a commit.
/// </summary>
public sealed class CommitCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/commit";

    /// <inheritdoc/>
    public override string Description => "Create a git commit (interactive)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var psi = new System.Diagnostics.ProcessStartInfo("git", "status --short")
        {
            WorkingDirectory = ctx.Cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[yellow]Could not run git[/]");
                return true;
            }

            var status = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(status))
            {
                ctx.WriteMarkup("[grey]No changes to commit.[/]");
                return true;
            }

            ctx.Write(status.TrimEnd());
            ctx.WriteMarkup("[yellow]Use the prompt to ask Claude to create a commit for you.[/]");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }
}

/// <summary>Summarizes older conversation messages to reduce context window usage.</summary>
public sealed class CompactCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/compact";

    /// <inheritdoc/>
    public override string Description => "Compact conversation context to reduce token usage";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.CompactFunc is null)
        {
            ctx.WriteMarkup("[yellow]Compaction not available.[/]");
            return true;
        }

        ctx.WriteMarkup("[grey]Compacting conversation...[/]");

        var result = await ctx.CompactFunc(ct).ConfigureAwait(false);

        if (result is ClaudeCode.Services.Compact.CompactionResult r && r.MessagesRemoved > 0)
        {
            ctx.WriteMarkup(
                $"[green]Compacted:[/] removed {r.MessagesRemoved} messages, " +
                $"~{r.OriginalTokenEstimate:N0} \u2192 ~{r.CompactedTokenEstimate:N0} tokens");
        }
        else
        {
            ctx.WriteMarkup("[grey]Nothing to compact.[/]");
        }

        return true;
    }
}

/// <summary>Shows configuration paths and current model setting.</summary>
public sealed class ConfigCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/config";

    /// <inheritdoc/>
    public override string Description => "Show configuration info";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // /config features  — show all flags and their sources
        if (ctx.Args.Length > 0 && ctx.Args[0].Equals("features", StringComparison.OrdinalIgnoreCase))
        {
            var flags = ClaudeCode.Configuration.FeatureFlags.GetAll(null);

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn("Flag");
            table.AddColumn("Enabled");
            table.AddColumn("Source");

            foreach (var (flag, value, source) in flags)
            {
                var valueMarkup = value ? "[green]true[/]" : "[grey]false[/]";
                table.AddRow(flag.EscapeMarkup(), valueMarkup, source.EscapeMarkup());
            }

            AnsiConsole.Write(table);
            return Task.FromResult(true);
        }

        ctx.WriteMarkup($"[grey]Working directory: {ctx.Cwd.EscapeMarkup()}[/]");
        ctx.WriteMarkup(
            $"[grey]Config home: {ClaudeCode.Configuration.ConfigPaths.ClaudeHomeDir.EscapeMarkup()}[/]");
        ctx.WriteMarkup(
            $"[grey]User settings: {ClaudeCode.Configuration.ConfigPaths.UserSettingsPath.EscapeMarkup()}[/]");

        if (ctx.CurrentModel is not null)
            ctx.WriteMarkup($"[grey]Model: {ctx.CurrentModel.EscapeMarkup()}[/]");

        return Task.FromResult(true);
    }
}

/// <summary>
/// Presents a numbered list of recent sessions and restores the user-selected one
/// into the active conversation history.
/// </summary>
public sealed class ResumeCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/resume";

    /// <inheritdoc/>
    public override string Description => "Resume a previous session";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var store = new SessionStore();
        var sessions = await store.ListRecentAsync(10, ct).ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            ctx.WriteMarkup("[grey]No saved sessions found.[/]");
            return true;
        }

        // Display session list as a numbered table.
        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("#");
        table.AddColumn("Date");
        table.AddColumn("Model");
        table.AddColumn("Messages");
        table.AddColumn("Summary");
        table.AddColumn("Tags");

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var tagsCell = s.Tags is { Count: > 0 }
                ? "[grey]" + string.Join(' ', s.Tags.Select(t => "#" + t.EscapeMarkup())) + "[/]"
                : string.Empty;
            table.AddRow(
                (i + 1).ToString(),
                s.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                ClaudeCode.Services.Api.ModelResolver.GetDisplayName(s.Model).EscapeMarkup(),
                s.MessageCount.ToString(),
                (s.Summary ?? string.Empty).EscapeMarkup(),
                tagsCell);
        }

        AnsiConsole.Write(table);

        // Prompt user to select a session by number, or 0 to cancel.
        var choice = AnsiConsole.Prompt(
            new TextPrompt<int>("[blue]Session number (0 to cancel):[/]")
                .DefaultValue(0)
                .Validate(n => n >= 0 && n <= sessions.Count
                    ? Spectre.Console.ValidationResult.Success()
                    : Spectre.Console.ValidationResult.Error("Invalid session number")));

        if (choice == 0)
            return true;

        var selected = sessions[choice - 1];
        var session = await store.LoadAsync(selected.Id, ct).ConfigureAwait(false);

        if (session is null)
        {
            ctx.WriteMarkup("[red]Failed to load session.[/]");
            return true;
        }

        if (ctx.RestoreSession is not null)
        {
            ctx.RestoreSession(session.Messages);
            ctx.WriteMarkup($"[green]Restored session with {session.Messages.Count} messages.[/]");
        }
        else
        {
            ctx.WriteMarkup("[yellow]Session restore not available in this context.[/]");
        }

        return true;
    }
}

/// <summary>Displays a table of all memories saved in the per-project memory store.</summary>
public sealed class MemoryCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/memory";

    /// <inheritdoc/>
    public override string Description => "View saved memories";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var store = new ClaudeCode.Services.Memory.MemoryStore(ctx.Cwd);
        var entries = store.LoadAll();

        if (entries.Count == 0)
        {
            ctx.WriteMarkup("[grey]No memories saved. Claude will create memories automatically as you work.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Name");
        table.AddColumn("Type");
        table.AddColumn("Description");

        foreach (var entry in entries)
            table.AddRow(entry.Name.EscapeMarkup(), entry.Type.EscapeMarkup(), entry.Description.EscapeMarkup());

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>Lists all skills discovered in project and global <c>.claude/skills/</c> directories.</summary>
public sealed class SkillsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/skills";

    /// <inheritdoc/>
    public override string Description => "List available skills";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var skills = ClaudeCode.Services.Skills.SkillLoader.LoadSkills(ctx.Cwd);

        if (skills.Count == 0)
        {
            ctx.WriteMarkup("[grey]No skills found. Add .md files to .claude/skills/ to create skills.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Name");
        table.AddColumn("Description");

        foreach (var skill in skills)
            table.AddRow(skill.Name.EscapeMarkup(), (skill.Description ?? string.Empty).EscapeMarkup());

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

// =============================================================================
// Commands appended below — batch of 12
// =============================================================================

/// <summary>
/// Toggles fast mode for the session. Fast mode instructs the engine to prefer
/// lower-latency responses over exhaustive reasoning.
/// </summary>
public sealed class FastCommand : SlashCommand
{
    private static bool _fastMode;

    /// <inheritdoc/>
    public override string Name => "/fast";

    /// <inheritdoc/>
    public override string Description => "Toggle fast mode";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        _fastMode = !_fastMode;

        if (_fastMode)
        {
            var fastModel = "claude-haiku-4-5-20251001";
            ctx.SwitchModel?.Invoke(fastModel);
            ctx.WriteMarkup($"[green]Fast mode enabled — switched to {fastModel.EscapeMarkup()}.[/]");
            ctx.WriteMarkup("[grey]Use /fast again to return to the previous model.[/]");
        }
        else
        {
            var defaultModel = ClaudeCode.Services.Api.ModelResolver.Resolve();
            ctx.SwitchModel?.Invoke(defaultModel);
            ctx.WriteMarkup($"[grey]Fast mode disabled — switched back to {defaultModel.EscapeMarkup()}.[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays the current permission mode and the allow / deny / ask rule lists
/// loaded from settings.
/// </summary>
public sealed class PermissionsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/permissions";

    /// <inheritdoc/>
    public override string Description => "Show current permission mode and rules";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ConfigProvider is not ClaudeCode.Configuration.IConfigProvider config)
        {
            ctx.WriteMarkup("[grey]Permission settings not available.[/]");
            return Task.FromResult(true);
        }

        var perms = config.Settings.Permissions;
        var mode = perms?.DefaultMode ?? "Default";
        ctx.WriteMarkup($"[grey]Permission mode:[/] [yellow]{mode.EscapeMarkup()}[/]");

        void PrintList(string label, IReadOnlyList<string>? items)
        {
            if (items is null || items.Count == 0)
            {
                ctx.WriteMarkup($"[grey]{label}: (none)[/]");
                return;
            }

            ctx.WriteMarkup($"[grey]{label}:[/]");
            foreach (var item in items)
                ctx.WriteMarkup($"  [grey]- {item.EscapeMarkup()}[/]");
        }

        PrintList("Allow", perms?.Allow);
        PrintList("Deny", perms?.Deny);
        PrintList("Ask", perms?.Ask);

        return Task.FromResult(true);
    }
}

/// <summary>
/// Toggles plan mode on or off. In plan mode the engine restricts itself to
/// read-only operations and presents a written plan before executing changes.
/// </summary>
public sealed class PlanCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/plan";

    /// <inheritdoc/>
    public override string Description => "Toggle plan mode";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ClaudeCode.Tools.PlanMode.PlanModeState.IsActive =
            !ClaudeCode.Tools.PlanMode.PlanModeState.IsActive;

        var state = ClaudeCode.Tools.PlanMode.PlanModeState.IsActive
            ? "[green]enabled[/]"
            : "[grey]disabled[/]";

        ctx.WriteMarkup($"[grey]Plan mode:[/] {state}");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Lists all tasks currently tracked in <see cref="ClaudeCode.Core.Tasks.TaskStoreState"/>,
/// rendered as a table with per-status icons.
/// </summary>
public sealed class TasksCommand : SlashCommand
{
    private const string IconPending = "o";
    private const string IconInProgress = ">";
    private const string IconCompleted = "v";
    private const string IconDeleted = "x";

    /// <inheritdoc/>
    public override string Name => "/tasks";

    /// <inheritdoc/>
    public override string Description => "List tracked tasks";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var tasks = ClaudeCode.Core.Tasks.TaskStoreState.Tasks.Values
            .OrderBy(t => t.Id)
            .ToList();

        if (tasks.Count == 0)
        {
            ctx.WriteMarkup("[grey]No tasks. Use the TaskCreate tool to add tasks.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("St");
        table.AddColumn("ID");
        table.AddColumn("Subject");
        table.AddColumn("Owner");

        foreach (var task in tasks)
        {
            var (icon, color) = task.Status switch
            {
                "in_progress" => (IconInProgress, "yellow"),
                "completed"   => (IconCompleted,  "green"),
                "deleted"     => (IconDeleted,    "red"),
                _             => (IconPending,    "grey"),
            };

            table.AddRow(
                $"[{color}]{icon}[/]",
                task.Id.EscapeMarkup(),
                task.Subject.EscapeMarkup(),
                (task.Owner ?? string.Empty).EscapeMarkup());
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows the current UI theme and switches it via the session's <see cref="CommandContext.SwitchTheme"/>
/// delegate. Supported themes: default, dark, light, dracula, solarized.
/// </summary>
public sealed class ThemeCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/theme";

    /// <inheritdoc/>
    public override string Description => "Show or switch UI theme (dark / light)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length == 0)
        {
            ctx.WriteMarkup($"[grey]Current theme: {(ctx.CurrentTheme ?? "default").EscapeMarkup()}[/]");
            ctx.WriteMarkup("[grey]Available themes: default, dark, light, dracula, solarized[/]");
            return Task.FromResult(true);
        }

        var theme = ctx.Args[0].ToLowerInvariant();
        ctx.SwitchTheme?.Invoke(theme);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Toggles vim-style normal/insert mode keybindings in the REPL input loop.
/// The actual keybinding handling is wired in <see cref="ClaudeCode.Cli.Repl.ReplSession"/>.
/// </summary>
public sealed class VimCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/vim";

    /// <inheritdoc/>
    public override string Description => "Toggle vim mode (normal/insert keybindings)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ReplModeFlags.VimMode = !ReplModeFlags.VimMode;
        var state = ReplModeFlags.VimMode ? "[green]enabled[/]" : "[grey]disabled[/]";
        ctx.WriteMarkup($"[grey]Vim mode:[/] {state}");

        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays all hooks configured in the merged settings, grouped by event name.
/// </summary>
public sealed class HooksCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/hooks";

    /// <inheritdoc/>
    public override string Description => "Show configured hooks from settings";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ConfigProvider is not ClaudeCode.Configuration.IConfigProvider config)
        {
            ctx.WriteMarkup("[grey]Settings not available.[/]");
            return Task.FromResult(true);
        }

        var hooks = config.Settings.Hooks;

        if (config.Settings.DisableAllHooks == true)
            ctx.WriteMarkup("[yellow]All hooks are currently disabled (disableAllHooks = true).[/]");

        if (hooks is null || hooks.Count == 0)
        {
            ctx.WriteMarkup("[grey]No hooks configured.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Event");
        table.AddColumn("Matcher");
        table.AddColumn("Type");
        table.AddColumn("Command / Prompt / URL");

        foreach (var (eventName, matchers) in hooks.OrderBy(kv => kv.Key))
        {
            if (matchers is null)
                continue;

            foreach (var matcher in matchers)
            {
                var matcherLabel = matcher.Matcher ?? "(any)";
                var commands = matcher.Commands ?? [];

                if (commands.Count == 0)
                {
                    table.AddRow(
                        eventName.EscapeMarkup(),
                        matcherLabel.EscapeMarkup(),
                        "[grey]—[/]",
                        "[grey](no commands)[/]");
                    continue;
                }

                foreach (var cmd in commands)
                {
                    var (type, detail) = cmd switch
                    {
                        ClaudeCode.Configuration.Settings.BashHookCommand b =>
                            ("bash", b.Command),
                        ClaudeCode.Configuration.Settings.PromptHookCommand p =>
                            ("prompt", p.Prompt),
                        ClaudeCode.Configuration.Settings.HttpHookCommand h =>
                            ("http", h.Url),
                        _ => ("unknown", string.Empty),
                    };

                    table.AddRow(
                        eventName.EscapeMarkup(),
                        matcherLabel.EscapeMarkup(),
                        type.EscapeMarkup(),
                        detail.EscapeMarkup());
                }
            }
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Manages MCP (Model Context Protocol) server configuration.
/// Subcommands: <c>list</c> (default), <c>add &lt;name&gt; &lt;command&gt; [args…]</c>,
/// <c>remove &lt;name&gt;</c>, <c>enable &lt;name&gt;</c>, <c>disable &lt;name&gt;</c>.
/// Changes are persisted to <c>.claude/settings.local.json</c> in the working directory.
/// </summary>
public sealed class McpCommand : SlashCommand
{
    private static readonly JsonSerializerOptions _mcpWriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions _mcpReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc/>
    public override string Name => "/mcp";

    /// <inheritdoc/>
    public override string[] Aliases => ["/mcp list"];

    /// <inheritdoc/>
    public override string Description => "Manage MCP servers: list | add <name> <cmd> [args…] | remove <name> | enable <name> | disable <name>";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var sub = ctx.Args.Length > 0 ? ctx.Args[0].ToLowerInvariant() : "list";

        return sub switch
        {
            "list"    => await McpListAsync(ctx, ct).ConfigureAwait(false),
            "add"     => await McpAddAsync(ctx, ct).ConfigureAwait(false),
            "remove"  => await McpRemoveAsync(ctx, ct).ConfigureAwait(false),
            "enable"  => await McpSetEnabledAsync(ctx, true, ct).ConfigureAwait(false),
            "disable" => await McpSetEnabledAsync(ctx, false, ct).ConfigureAwait(false),
            "allow"   => McpAllow(ctx),
            "deny"    => McpDeny(ctx),
            _         => McpShowUsage(ctx, sub),
        };
    }

    // -----------------------------------------------------------------------
    // Subcommand handlers
    // -----------------------------------------------------------------------

    private static Task<bool> McpListAsync(CommandContext ctx, CancellationToken ct)
    {
        if (ctx.ConfigProvider is not ClaudeCode.Configuration.IConfigProvider config)
        {
            ctx.WriteMarkup("[grey]MCP configuration not available.[/]");
            return Task.FromResult(true);
        }

        var servers = config.Settings.McpServers;
        if (servers is null || servers.Count == 0)
        {
            ctx.WriteMarkup("[grey]No MCP servers configured.[/]");
            ctx.WriteMarkup("[grey]Use [blue]/mcp add <name> <command>[/][grey] to add one.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Name");
        table.AddColumn("Transport");
        table.AddColumn("Command / URL");
        table.AddColumn("Status");

        foreach (var (name, entry) in servers.OrderBy(kv => kv.Key))
        {
            var transport = entry.Type ?? "stdio";
            var detail = transport is "sse" or "http" or "websocket"
                ? entry.Url ?? "(no url)"
                : string.IsNullOrWhiteSpace(entry.Command)
                    ? "(no command)"
                    : entry.Args is { Length: > 0 }
                        ? $"{entry.Command} {string.Join(' ', entry.Args)}"
                        : entry.Command;

            var statusMarkup = (entry.Disabled == true || entry.Enabled == false)
                ? "[grey]disabled[/]"
                : "[green]enabled[/]";

            table.AddRow(
                name.EscapeMarkup(),
                transport.EscapeMarkup(),
                (detail ?? string.Empty).EscapeMarkup(),
                statusMarkup);
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }

    private static async Task<bool> McpAddAsync(CommandContext ctx, CancellationToken ct)
    {
        // /mcp add <name> <command> [args…]
        if (ctx.Args.Length < 3)
        {
            ctx.WriteMarkup("[yellow]Usage: /mcp add <name> <command> [args…][/]");
            return true;
        }

        var serverName = ctx.Args[1];
        var command    = ctx.Args[2];
        var args       = ctx.Args.Length > 3 ? ctx.Args[3..] : [];

        var settings = await McpLoadLocalAsync(ctx.Cwd, ct).ConfigureAwait(false);
        var servers  = new Dictionary<string, ClaudeCode.Configuration.Settings.McpServerEntryJson>(
            settings.McpServers ?? []);

        if (servers.ContainsKey(serverName))
        {
            ctx.WriteMarkup(
                $"[yellow]Server '{serverName.EscapeMarkup()}' already exists. " +
                "Remove it first or choose a different name.[/]");
            return true;
        }

        servers[serverName] = new ClaudeCode.Configuration.Settings.McpServerEntryJson
        {
            Type    = "stdio",
            Command = command,
            Args    = args.Length > 0 ? args : null,
        };

        await McpSaveLocalAsync(ctx.Cwd, settings with { McpServers = servers }, ct)
            .ConfigureAwait(false);

        ctx.WriteMarkup(
            $"[green]Added[/] MCP server '[blue]{serverName.EscapeMarkup()}[/]' " +
            $"→ [grey]{command.EscapeMarkup()}[/]");
        return true;
    }

    private static async Task<bool> McpRemoveAsync(CommandContext ctx, CancellationToken ct)
    {
        if (ctx.Args.Length < 2)
        {
            ctx.WriteMarkup("[yellow]Usage: /mcp remove <name>[/]");
            return true;
        }

        var serverName = ctx.Args[1];
        var settings   = await McpLoadLocalAsync(ctx.Cwd, ct).ConfigureAwait(false);
        var servers    = new Dictionary<string, ClaudeCode.Configuration.Settings.McpServerEntryJson>(
            settings.McpServers ?? []);

        if (!servers.Remove(serverName))
        {
            ctx.WriteMarkup($"[yellow]Server '{serverName.EscapeMarkup()}' not found in local settings.[/]");
            return true;
        }

        await McpSaveLocalAsync(ctx.Cwd, settings with { McpServers = servers }, ct)
            .ConfigureAwait(false);

        ctx.WriteMarkup($"[green]Removed[/] MCP server '[blue]{serverName.EscapeMarkup()}[/]'.");
        return true;
    }

    private static async Task<bool> McpSetEnabledAsync(
        CommandContext ctx, bool enable, CancellationToken ct)
    {
        if (ctx.Args.Length < 2)
        {
            var verb = enable ? "enable" : "disable";
            ctx.WriteMarkup($"[yellow]Usage: /mcp {verb} <name>[/]");
            return true;
        }

        var serverName = ctx.Args[1];
        var settings   = await McpLoadLocalAsync(ctx.Cwd, ct).ConfigureAwait(false);
        var servers    = new Dictionary<string, ClaudeCode.Configuration.Settings.McpServerEntryJson>(
            settings.McpServers ?? []);

        if (!servers.TryGetValue(serverName, out var entry))
        {
            ctx.WriteMarkup($"[yellow]Server '{serverName.EscapeMarkup()}' not found in local settings.[/]");
            return true;
        }

        // McpServerEntryJson.Enabled has a mutable setter; mutate in-place then re-save.
        entry.Enabled = enable;

        await McpSaveLocalAsync(ctx.Cwd, settings with { McpServers = servers }, ct)
            .ConfigureAwait(false);

        var stateMarkup = enable ? "[green]enabled[/]" : "[grey]disabled[/]";
        ctx.WriteMarkup($"MCP server '[blue]{serverName.EscapeMarkup()}[/]' is now {stateMarkup}.");
        return true;
    }

    // -----------------------------------------------------------------------
    // Settings I/O helpers
    // -----------------------------------------------------------------------

    private static async Task<ClaudeCode.Configuration.Settings.SettingsJson> McpLoadLocalAsync(
        string cwd, CancellationToken ct)
    {
        var path = ClaudeCode.Configuration.ConfigPaths.LocalSettingsPath(cwd);
        if (!File.Exists(path))
            return new ClaudeCode.Configuration.Settings.SettingsJson();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ClaudeCode.Configuration.Settings.SettingsJson>(
                       json, _mcpReadOpts)
                   ?? new ClaudeCode.Configuration.Settings.SettingsJson();
        }
        catch
        {
            return new ClaudeCode.Configuration.Settings.SettingsJson();
        }
    }

    private static async Task McpSaveLocalAsync(
        string cwd,
        ClaudeCode.Configuration.Settings.SettingsJson settings,
        CancellationToken ct)
    {
        var path = ClaudeCode.Configuration.ConfigPaths.LocalSettingsPath(cwd);
        var dir  = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, _mcpWriteOpts);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool McpAllow(CommandContext ctx)
    {
        // /mcp allow <server> <tool>
        if (ctx.Args.Length < 3)
        {
            ctx.WriteMarkup("[yellow]Usage: /mcp allow <server> <tool>[/]");
            return true;
        }

        if (ctx.McpAllow is null)
        {
            ctx.WriteMarkup("[grey]MCP channel permissions not available.[/]");
            return true;
        }

        var server = ctx.Args[1];
        var tool   = ctx.Args[2];
        ctx.McpAllow(server, tool);
        ctx.WriteMarkup(
            $"[green]Allowed tool '{tool.EscapeMarkup()}' from server '{server.EscapeMarkup()}'.[/]");
        return true;
    }

    private static bool McpDeny(CommandContext ctx)
    {
        // /mcp deny <server> <tool>
        if (ctx.Args.Length < 3)
        {
            ctx.WriteMarkup("[yellow]Usage: /mcp deny <server> <tool>[/]");
            return true;
        }

        if (ctx.McpDeny is null)
        {
            ctx.WriteMarkup("[grey]MCP channel permissions not available.[/]");
            return true;
        }

        var server = ctx.Args[1];
        var tool   = ctx.Args[2];
        ctx.McpDeny(server, tool);
        ctx.WriteMarkup(
            $"[red]Denied tool '{tool.EscapeMarkup()}' from server '{server.EscapeMarkup()}'.[/]");
        return true;
    }

    private static bool McpShowUsage(CommandContext ctx, string sub)
    {
        ctx.WriteMarkup(
            $"[yellow]Unknown subcommand '{sub.EscapeMarkup()}'. " +
            "Usage: /mcp [list | add <name> <cmd> [args…] | remove <name> | enable <name> | disable <name> | allow <server> <tool> | deny <server> <tool>][/]");
        return true;
    }
}

/// <summary>
/// Runs <c>git ls-files</c> in the working directory and displays up to the first 50 tracked files.
/// </summary>
public sealed class FilesCommand : SlashCommand
{
    private const int MaxFiles = 50;

    /// <inheritdoc/>
    public override string Name => "/files";

    /// <inheritdoc/>
    public override string Description => "List git-tracked files in the working directory (max 50)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[yellow]Could not start git process.[/]");
                return true;
            }

            var allOutput = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(allOutput))
            {
                ctx.WriteMarkup("[grey]No tracked files found (not a git repo or empty index).[/]");
                return true;
            }

            var lines = allOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var displayed = lines.Take(MaxFiles).ToArray();

            var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
            table.AddColumn("File");

            foreach (var line in displayed)
                table.AddRow(line.EscapeMarkup());

            AnsiConsole.Write(table);

            if (lines.Length > MaxFiles)
                ctx.WriteMarkup($"[grey]... and {lines.Length - MaxFiles} more (showing first {MaxFiles})[/]");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }
}

/// <summary>
/// Creates the standard <c>.claude/</c> directory structure in the working directory,
/// including template <c>settings.json</c> and <c>CLAUDE.md</c> files, if they do not
/// already exist.
/// </summary>
public sealed class InitCommand : SlashCommand
{
    private const string SettingsTemplate = """
        {
          "model": null,
          "permissions": {
            "allow": [],
            "deny": []
          },
          "hooks": {}
        }
        """;

    private const string ClaudeMdTemplate = """
        # Project Instructions

        Add project-specific instructions for Claude here.
        These are loaded automatically when Claude Code starts in this directory.
        """;

    /// <inheritdoc/>
    public override string Name => "/init";

    /// <inheritdoc/>
    public override string Description => "Initialise .claude/ directory structure in the working directory";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var claudeDir = Path.Combine(ctx.Cwd, ".claude");
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        var claudeMdPath = Path.Combine(ctx.Cwd, "CLAUDE.md");

        var createdDir = false;
        if (!Directory.Exists(claudeDir))
        {
            Directory.CreateDirectory(claudeDir);
            createdDir = true;
        }

        var createdSettings = false;
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, SettingsTemplate);
            createdSettings = true;
        }

        var createdMd = false;
        if (!File.Exists(claudeMdPath))
        {
            File.WriteAllText(claudeMdPath, ClaudeMdTemplate);
            createdMd = true;
        }

        if (!createdDir && !createdSettings && !createdMd)
        {
            ctx.WriteMarkup("[grey].claude/ structure already exists — nothing to do.[/]");
            return Task.FromResult(true);
        }

        if (createdDir)
            ctx.WriteMarkup($"[green]Created[/] [grey]{claudeDir.EscapeMarkup()}[/]");

        if (createdSettings)
            ctx.WriteMarkup($"[green]Created[/] [grey]{settingsPath.EscapeMarkup()}[/]");

        if (createdMd)
            ctx.WriteMarkup($"[green]Created[/] [grey]{claudeMdPath.EscapeMarkup()}[/]");

        return Task.FromResult(true);
    }
}

/// <summary>
/// Directs users to the GitHub Issues page for submitting feedback or bug reports.
/// </summary>
public sealed class FeedbackCommand : SlashCommand
{
    private const string FeedbackUrl = "https://github.com/anthropics/claude-code/issues";

    /// <inheritdoc/>
    public override string Name => "/feedback";

    /// <inheritdoc/>
    public override string Description => "Show feedback and bug report link";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[grey]To report bugs or share feedback, please open an issue at:[/]");
        ctx.WriteMarkup($"[blue underline]{FeedbackUrl}[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Copies the last assistant response to the system clipboard.
/// Full clipboard integration requires platform-specific wiring (Phase 16+);
/// this command informs the user when that wiring is not yet available.
/// </summary>
public sealed class CopyCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/copy";

    /// <inheritdoc/>
    public override string Description => "Copy last assistant response to clipboard";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.LastAssistantResponse is { Length: > 0 } text)
        {
            try
            {
                // Use OSC 52 escape sequence to copy to clipboard (works in most modern terminals).
                var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                Console.Write($"\x1b]52;c;{b64}\x07");
                ctx.WriteMarkup("[green]Copied last response to clipboard (via OSC 52).[/]");
            }
            catch
            {
                ctx.WriteMarkup("[yellow]Clipboard copy via OSC 52 failed. The last response was:[/]");
                ctx.Write(text.Length > 500 ? text[..500] + "..." : text);
            }
        }
        else
        {
            ctx.WriteMarkup("[grey]No assistant response to copy yet.[/]");
        }

        return Task.FromResult(true);
    }
}

// =============================================================================
// 12 additional commands — Phase: branch/add-dir/context/doctor/env/export/
//                           login/logout/session/share/review/pr_comments
// =============================================================================

/// <summary>
/// Displays current git branches via <c>git branch -vv</c>.
/// When a branch name argument is supplied, runs <c>git checkout &lt;branch&gt;</c> instead.
/// </summary>
public sealed class BranchCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/branch";

    /// <inheritdoc/>
    public override string Description => "List git branches, or checkout a branch: /branch <name>";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Determine operation: checkout when an argument is supplied, list otherwise.
        var (gitArgs, isCheckout) = ctx.Args.Length > 0
            ? ($"checkout {ctx.Args[0]}", true)
            : ("branch -vv", false);

        if (isCheckout)
            ctx.WriteMarkup($"[grey]Checking out branch '{ctx.Args[0].EscapeMarkup()}'...[/]");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", gitArgs)
            {
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[yellow]Could not start git process.[/]");
                return true;
            }

            // Read both streams concurrently to avoid deadlocking on full pipe buffers.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout))
                ctx.Write(stdout.TrimEnd());

            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                ctx.WriteMarkup($"[yellow]{stderr.TrimEnd().EscapeMarkup()}[/]");
            else if (isCheckout && proc.ExitCode == 0)
                ctx.WriteMarkup($"[green]Switched to branch '{ctx.Args[0].EscapeMarkup()}'.[/]");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }
}

/// <summary>
/// Accepts a directory path argument, validates that it exists, and confirms it
/// has been acknowledged as an additional working directory for context.
/// </summary>
public sealed class AddDirCommand : SlashCommand
{
    private static readonly List<string> _extraDirs = [];

    /// <summary>Returns the list of extra directories registered via /add-dir.</summary>
    public static IReadOnlyList<string> ExtraDirectories => _extraDirs.AsReadOnly();

    /// <inheritdoc/>
    public override string Name => "/add-dir";

    /// <inheritdoc/>
    public override string Description => "Add a directory as additional working context: /add-dir <path>";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length == 0)
        {
            ctx.WriteMarkup("[yellow]Usage: /add-dir <path>[/]");
            return Task.FromResult(true);
        }

        // Join all args to support paths that contain spaces.
        var rawPath = string.Join(' ', ctx.Args);

        // Resolve relative paths against the session's working directory.
        var resolvedPath = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.GetFullPath(rawPath, ctx.Cwd);

        if (!Directory.Exists(resolvedPath))
        {
            ctx.WriteMarkup($"[red]Directory not found: {resolvedPath.EscapeMarkup()}[/]");
            return Task.FromResult(true);
        }

        if (!_extraDirs.Contains(resolvedPath, StringComparer.OrdinalIgnoreCase))
        {
            _extraDirs.Add(resolvedPath);
            ctx.WriteMarkup($"[green]Added directory: {resolvedPath.EscapeMarkup()}[/]");
            ctx.WriteMarkup("[grey]CLAUDE.md in this directory will be included in the system prompt.[/]");
        }
        else
        {
            ctx.WriteMarkup($"[grey]Directory already added: {resolvedPath.EscapeMarkup()}[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows conversation context information: message count, estimated token usage
/// (using a chars/4 heuristic when no tracker data is available), and the
/// approximate percentage of the context window consumed.
/// </summary>
public sealed class ContextCommand : SlashCommand
{
    // Documented context window for current Sonnet-class models.
    private const int ContextWindowTokens = 200_000;

    // Rough average characters per turn used for the heuristic estimate.
    private const int EstimatedCharsPerMessage = 800;

    /// <inheritdoc/>
    public override string Name => "/context";

    /// <inheritdoc/>
    public override string Description => "Show conversation context info (messages, tokens, window %)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var messageCount = ctx.ConversationMessageCount;

        // Prefer precise totals from the cost tracker; fall back to chars/4 heuristic.
        int estimatedTokens;
        if (ctx.CostTracker is { } tracker)
        {
            estimatedTokens = tracker.GetModelUsage()
                .Values
                .Sum(u => u.InputTokens + u.OutputTokens);
        }
        else
        {
            // chars/4 heuristic applied per-message.
            estimatedTokens = messageCount * EstimatedCharsPerMessage / 4;
        }

        var windowPct = ContextWindowTokens > 0
            ? (double)estimatedTokens / ContextWindowTokens * 100.0
            : 0.0;

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Messages", messageCount.ToString("N0"));
        table.AddRow("Estimated tokens", estimatedTokens.ToString("N0"));
        table.AddRow("Context window", $"{ContextWindowTokens:N0} tokens");
        table.AddRow("Window used", $"{windowPct:F1}%");

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Runs environment diagnostics: checks that essential tools are on PATH, that
/// configuration files exist, and that the API key is configured. Results are
/// rendered as a colour-coded checklist table.
/// </summary>
public sealed class DoctorCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/doctor";

    /// <inheritdoc/>
    public override string Description => "Run environment diagnostics (tools, config, API key)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var checks = new List<(string Label, bool Ok, string Detail)>();

        // Tool availability.
        checks.Add(await CheckToolAsync("git",  "--version", ct).ConfigureAwait(false));
        checks.Add(await CheckToolAsync("node", "--version", ct).ConfigureAwait(false));
        checks.Add(await CheckPythonAsync(ct).ConfigureAwait(false));
        checks.Add(await CheckToolAsync("rg",   "--version", ct).ConfigureAwait(false));

        // Config file presence.
        AddFileCheck(checks, "User settings",  ClaudeCode.Configuration.ConfigPaths.UserSettingsPath);
        AddFileCheck(checks, "Global config",  ClaudeCode.Configuration.ConfigPaths.GlobalConfigPath);
        AddFileCheck(checks, "Project settings (optional)",
            ClaudeCode.Configuration.ConfigPaths.ProjectSettingsPath(ctx.Cwd));

        // API key.
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var apiKeySet = !string.IsNullOrWhiteSpace(apiKey);
        checks.Add(("ANTHROPIC_API_KEY", apiKeySet, apiKeySet ? "Set" : "Not set"));

        // Render.
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Check");
        table.AddColumn("Status");
        table.AddColumn("Detail");

        foreach (var (label, ok, detail) in checks)
            table.AddRow(label.EscapeMarkup(), ok ? "[green]OK[/]" : "[red]FAIL[/]", detail.EscapeMarkup());

        AnsiConsole.Write(table);
        return true;
    }

    private static void AddFileCheck(List<(string, bool, string)> checks, string label, string path)
    {
        var exists = File.Exists(path);
        checks.Add((label, exists, exists ? path : $"Not found: {path}"));
    }

    /// <summary>Checks that <paramref name="tool"/> is reachable on PATH by running it with <paramref name="versionArg"/>.</summary>
    private static async Task<(string Label, bool Ok, string Detail)> CheckToolAsync(
        string tool, string versionArg, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(tool, versionArg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return (tool, false, "Could not start process");

            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            // Use first non-empty line as the version string.
            var version = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim()
                ?? "available";

            return (tool, proc.ExitCode == 0, version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (tool, false, "Not found on PATH");
        }
    }

    /// <summary>
    /// Checks Python availability, trying <c>python</c> first then <c>python3</c>
    /// to handle both Windows and Unix PATH conventions.
    /// </summary>
    private static async Task<(string Label, bool Ok, string Detail)> CheckPythonAsync(CancellationToken ct)
    {
        var (_, ok, detail) = await CheckToolAsync("python", "--version", ct).ConfigureAwait(false);
        if (ok)
            return ("python", true, detail);

        var (_, ok3, detail3) = await CheckToolAsync("python3", "--version", ct).ConfigureAwait(false);
        return ("python", ok3, ok3 ? detail3 : "Not found on PATH (tried python and python3)");
    }
}

/// <summary>
/// Displays environment variables relevant to ClaudeCode.
/// The <c>ANTHROPIC_API_KEY</c> value is masked for security; all other listed
/// variables are shown verbatim.
/// </summary>
public sealed class EnvCommand : SlashCommand
{
    private const string ApiKeyVar    = "ANTHROPIC_API_KEY";
    private const string ModelVar     = "ANTHROPIC_MODEL";
    private const string BaseUrlVar   = "ANTHROPIC_BASE_URL";

    /// <inheritdoc/>
    public override string Name => "/env";

    /// <inheritdoc/>
    public override string Description => "Show relevant environment variables";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Variable");
        table.AddColumn("Value");

        // Known fixed vars — API key is always masked.
        AddRow(table, ApiKeyVar,  MaskApiKey(Environment.GetEnvironmentVariable(ApiKeyVar)));
        AddRow(table, ModelVar,   Environment.GetEnvironmentVariable(ModelVar));
        AddRow(table, BaseUrlVar, Environment.GetEnvironmentVariable(BaseUrlVar));

        // Any CLAUDE_CODE_* vars found in the process environment.
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            if (key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
                AddRow(table, key, entry.Value?.ToString());
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }

    private static void AddRow(Table table, string name, string? value) =>
        table.AddRow(name.EscapeMarkup(), (value is null ? "[grey](not set)[/]" : value.EscapeMarkup()));

    /// <summary>
    /// Masks all but the last four characters of an API key so the suffix
    /// is visible for identification without exposing the secret.
    /// </summary>
    private static string MaskApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "(not set)";

        return key.Length <= 4
            ? new string('*', key.Length)
            : string.Concat(new string('*', key.Length - 4), key.AsSpan(key.Length - 4));
    }
}

/// <summary>
/// Exports the current conversation as a Markdown document written to a
/// timestamped file (<c>conversation-{yyyyMMdd-HHmmss}.md</c>) in <c>ctx.Cwd</c>.
/// </summary>
public sealed class ExportCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/export";

    /// <inheritdoc/>
    public override string Description => "Export conversation to a Markdown file in the working directory";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var timestamp  = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filename   = $"conversation-{timestamp}.md";
        var outputPath = Path.Combine(ctx.Cwd, filename);

        var markdown = BuildMarkdown(ctx, shareNote: null);
        await File.WriteAllTextAsync(outputPath, markdown, System.Text.Encoding.UTF8, ct)
            .ConfigureAwait(false);

        ctx.WriteMarkup($"[green]Exported:[/] {outputPath.EscapeMarkup()}");
        return true;
    }

    /// <summary>
    /// Builds a Markdown document from the available <see cref="CommandContext"/> fields.
    /// When <paramref name="shareNote"/> is non-null, it is included as a blockquote reminder.
    /// </summary>
    internal static string BuildMarkdown(CommandContext ctx, string? shareNote)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Conversation Export");
        sb.AppendLine();
        sb.AppendLine($"**Exported:** {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Working directory:** {ctx.Cwd}");

        if (ctx.CurrentModel is not null)
        {
            var displayName = ClaudeCode.Services.Api.ModelResolver.GetDisplayName(ctx.CurrentModel);
            sb.AppendLine($"**Model:** {displayName} (`{ctx.CurrentModel}`)");
        }

        sb.AppendLine($"**Messages:** {ctx.ConversationMessageCount}");

        if (ctx.CostTracker is not null)
            sb.AppendLine($"**Cost:** {ctx.CostTracker.FormatUsageSummary()}");

        var activeTags = TagCommand.ActiveTags;
        if (activeTags.Count > 0)
            sb.AppendLine($"**Tags:** {string.Join(", ", activeTags.Select(t => "#" + t))}");

        if (shareNote is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"> **Note:** {shareNote}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (ctx.ConversationMessages is { Count: > 0 } msgs)
        {
            sb.AppendLine();
            sb.AppendLine("## Conversation");
            sb.AppendLine();
            foreach (var msg in msgs)
            {
                var roleLabel = msg.Role == "assistant" ? "### Assistant" : "### User";
                sb.AppendLine(roleLabel);
                sb.AppendLine();
                // Content can be a text string or an array of blocks.
                if (msg.Content.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    sb.AppendLine(msg.Content.GetString());
                }
                else if (msg.Content.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var block in msg.Content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                            && block.TryGetProperty("text", out var textEl))
                        {
                            sb.AppendLine(textEl.GetString());
                        }
                    }
                }
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine(
                "_Full message history is not available via this export. " +
                "Use `/session` to see session details or scroll up in your terminal._");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Checks whether <c>ANTHROPIC_API_KEY</c> is set. If it is, reports that the
/// session is already authenticated. If not, prints step-by-step instructions
/// for setting the key on the three major platforms.
/// </summary>
public sealed class LoginCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/login";

    /// <inheritdoc/>
    public override string Description => "Check authentication status or show API key setup instructions";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Check if already logged in
        var keychain = new ClaudeCode.Services.Keychain.KeychainService();
        var existingKey = await keychain.GetAsync("anthropic-api-key").ConfigureAwait(false);
        if (existingKey is not null)
        {
            AnsiConsole.MarkupLine("[green]Already logged in.[/] API key is stored in keychain.");
            AnsiConsole.MarkupLine("Run [blue]/logout[/] first to log in with a different account.");
            return true;
        }

        // Check for API key in env first
        var envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (envKey is { Length: > 10 })
        {
            await keychain.SetAsync("anthropic-api-key", envKey).ConfigureAwait(false);
            AnsiConsole.MarkupLine("[green]API key from environment saved to keychain.[/]");
            return true;
        }

        // OAuth flow
        AnsiConsole.MarkupLine("[blue]Starting OAuth login...[/]");
        AnsiConsole.MarkupLine("Your browser will open. Log in to claude.ai and authorize ClaudeCode.");
        try
        {
            var oauth = new ClaudeCode.Services.OAuth.OAuthService();
            var tokens = await oauth.StartOAuthFlow(
                authBaseUrl: "https://claude.ai/oauth/authorize",
                tokenUrl: "https://claude.ai/oauth/token",
                clientId: "claude-code-cli",
                scopes: ["read", "write"],
                port: 54321,
                ct: ct).ConfigureAwait(false);
            await keychain.SetAsync("anthropic-api-key", tokens.AccessToken).ConfigureAwait(false);
            if (tokens.RefreshToken is not null)
                await keychain.SetAsync("anthropic-refresh-token", tokens.RefreshToken).ConfigureAwait(false);
            AnsiConsole.MarkupLine("[green]Login successful.[/] API key saved to keychain.");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]OAuth flow failed:[/] {ex.Message}");
            AnsiConsole.MarkupLine("You can also set [blue]ANTHROPIC_API_KEY[/] environment variable directly.");
        }

        return true;
    }
}

/// <summary>
/// Signals that the API key should be treated as cleared for the remainder of
/// this process lifetime and sets a process-scoped flag that the host REPL can
/// inspect to gate further API calls. Environment variables set outside the
/// process are not modified and will persist after the session ends.
/// </summary>
public sealed class LogoutCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/logout";

    /// <inheritdoc/>
    public override string Description => "Clear API key for the current session";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var keychain = new ClaudeCode.Services.Keychain.KeychainService();
        await keychain.DeleteAsync("anthropic-api-key").ConfigureAwait(false);
        await keychain.DeleteAsync("anthropic-refresh-token").ConfigureAwait(false);
        // Clear env var for this process
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        AnsiConsole.MarkupLine("[yellow]Logged out.[/] API key removed from keychain.");

        return true;
    }
}

/// <summary>
/// Refreshes the stored OAuth access token using the persisted refresh token.
/// Retrieves the refresh token from the OS keychain, calls the OAuth refresh endpoint,
/// and stores the new access token back in the keychain.
/// </summary>
public sealed class OAuthRefreshCommand : SlashCommand
{
    private const string TokenUrl = "https://claude.ai/oauth/token";
    private const string ClientId = "claude-code-cli";

    /// <inheritdoc/>
    public override string Name => "/oauth-refresh";

    /// <inheritdoc/>
    public override string Description => "Refresh the stored OAuth access token using the saved refresh token";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var keychain = new ClaudeCode.Services.Keychain.KeychainService();
        var refreshToken = await keychain.GetAsync("anthropic-refresh-token").ConfigureAwait(false);

        if (refreshToken is null)
        {
            AnsiConsole.MarkupLine("[red]No refresh token found in keychain.[/]");
            AnsiConsole.MarkupLine("Run [blue]/login[/] to authenticate first.");
            return true;
        }

        AnsiConsole.MarkupLine("[blue]Refreshing access token...[/]");
        try
        {
            var oauth = new ClaudeCode.Services.OAuth.OAuthService();
            var tokens = await oauth.RefreshAccessToken(TokenUrl, ClientId, refreshToken, ct)
                .ConfigureAwait(false);

            await keychain.SetAsync("anthropic-api-key", tokens.AccessToken).ConfigureAwait(false);

            if (tokens.RefreshToken is not null)
                await keychain.SetAsync("anthropic-refresh-token", tokens.RefreshToken).ConfigureAwait(false);

            // Propagate the new token into the current process environment.
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", tokens.AccessToken);

            AnsiConsole.MarkupLine("[green]Access token refreshed and saved to keychain.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Token refresh failed:[/] {ex.Message}");
            AnsiConsole.MarkupLine("Run [blue]/login[/] to re-authenticate.");
        }

        return true;
    }
}

/// <summary>
/// Displays current session information in a table: a derived session identifier,
/// working directory, message count, approximate start time, model, and cost.
/// </summary>
public sealed class SessionCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/session";

    /// <inheritdoc/>
    public override string Description => "Show current session info";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Produce a stable-looking 8-hex-digit session token derived from the cwd,
        // so repeated calls within the same working directory show the same value.
        var cwdHash  = (uint)ctx.Cwd.GetHashCode();
        var sessionId = cwdHash.ToString("x8");

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Field");
        table.AddColumn("Value");

        table.AddRow("Session ID",         sessionId);
        table.AddRow("Working directory",  ctx.Cwd);
        table.AddRow("Messages",           ctx.ConversationMessageCount.ToString("N0"));
        table.AddRow("Session time (UTC)", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " (approx)");

        if (ctx.CurrentModel is not null)
        {
            var display = ClaudeCode.Services.Api.ModelResolver.GetDisplayName(ctx.CurrentModel);
            table.AddRow("Model", $"{display} ({ctx.CurrentModel})");
        }

        if (ctx.CostTracker is not null)
            table.AddRow("Cost", ctx.CostTracker.FormatUsageSummary());

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Exports the conversation to a shareable Markdown file — identical to
/// <see cref="ExportCommand"/> but includes a prominent reminder to redact
/// sensitive information before sharing.
/// </summary>
public sealed class ShareCommand : SlashCommand
{
    private const string RedactionReminder =
        "Review this file carefully and redact any sensitive information " +
        "(API keys, passwords, file paths, proprietary code) before sharing.";

    /// <inheritdoc/>
    public override string Name => "/share";

    /// <inheritdoc/>
    public override string Description => "Export conversation as shareable Markdown with a redaction reminder";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var timestamp  = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filename   = $"conversation-{timestamp}-share.md";
        var outputPath = Path.Combine(ctx.Cwd, filename);

        var markdown = ExportCommand.BuildMarkdown(ctx, shareNote: RedactionReminder);
        await File.WriteAllTextAsync(outputPath, markdown, System.Text.Encoding.UTF8, ct)
            .ConfigureAwait(false);

        ctx.WriteMarkup($"[green]Shareable export written to:[/] {outputPath.EscapeMarkup()}");
        ctx.WriteMarkup($"[yellow]Reminder:[/] {RedactionReminder.EscapeMarkup()}");
        return true;
    }
}

/// <summary>
/// Runs <c>git diff --cached</c> to show staged changes, falling back to
/// <c>git diff</c> when nothing is staged. Displays the diff output and
/// suggests asking Claude to review the changes.
/// </summary>
public sealed class ReviewCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/review";

    /// <inheritdoc/>
    public override string Description => "Show staged (or unstaged) git diff and suggest a Claude review";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            // Prefer staged changes; fall back to the full working-tree diff.
            var diff = await RunGitAsync("diff --cached", ctx.Cwd, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(diff))
            {
                ctx.WriteMarkup("[grey]No staged changes found — showing unstaged diff instead.[/]");
                diff = await RunGitAsync("diff", ctx.Cwd, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(diff))
            {
                ctx.WriteMarkup("[grey]No changes detected in the working tree.[/]");
                return true;
            }

            ctx.Write(diff.TrimEnd());
            ctx.Write(string.Empty);
            ctx.WriteMarkup(
                "[yellow]Tip:[/] Ask Claude: " +
                "\"Please review these changes and highlight any bugs, style issues, or improvements.\"");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }

    private static async Task<string> RunGitAsync(string gitArgs, string cwd, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", gitArgs)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null)
            return string.Empty;

        var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }
}

/// <summary>
/// Runs <c>git log --oneline -10</c> to show recent commits, then suggests
/// that the user ask Claude to review the pull request or recent changes.
/// </summary>
public sealed class PrCommentsCommand : SlashCommand
{
    private const int MaxCommits = 10;

    /// <inheritdoc/>
    public override string Name => "/pr_comments";

    /// <inheritdoc/>
    public override string Description => "Show recent git commits and suggest a PR review with Claude";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "git", $"log --oneline -{MaxCommits}")
            {
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[yellow]Could not start git process.[/]");
                return true;
            }

            // Read streams concurrently to avoid pipe-buffer deadlocks.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var errMsg = string.IsNullOrWhiteSpace(stderr) ? "unknown git error" : stderr.TrimEnd();
                ctx.WriteMarkup($"[yellow]git log failed: {errMsg.EscapeMarkup()}[/]");
                return true;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                ctx.WriteMarkup("[grey]No commits found in this repository.[/]");
                return true;
            }

            ctx.WriteMarkup($"[grey]Recent {MaxCommits} commits:[/]");
            ctx.Write(stdout.TrimEnd());
            ctx.Write(string.Empty);
            ctx.WriteMarkup(
                "[yellow]Tip:[/] Ask Claude: " +
                "\"Review the changes in this PR — summarize what changed, " +
                "flag potential issues, and suggest improvements.\"");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }
}

// =============================================================================
// 19 additional commands — Phase: privacy/rate-limit/release-notes/reload-plugins/
// remote-env/rename/rewind/sandbox-toggle/security-review/stats/stickers/
// terminal-setup/upgrade/usage/voice/bridge/brief/tag/thinkback
// =============================================================================

/// <summary>
/// Displays the current privacy settings: what data is transmitted to Anthropic
/// and what is stored locally on disk.
/// </summary>
public sealed class PrivacySettingsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/privacy-settings";

    /// <inheritdoc/>
    public override string Description => "Show current privacy settings (what is sent to Anthropic vs stored locally)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[grey]Privacy Settings[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Sent to Anthropic:[/]");
        ctx.WriteMarkup("  [grey]- Conversation messages (user and assistant turns)[/]");
        ctx.WriteMarkup("  [grey]- Tool call inputs and outputs[/]");
        ctx.WriteMarkup("  [grey]- System prompt (including CLAUDE.md content)[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Stored locally on disk:[/]");
        ctx.WriteMarkup("  [grey]- Session history (~/.claude/sessions/)[/]");
        ctx.WriteMarkup("  [grey]- Memories (~/.claude/memory/ and .claude/memory/)[/]");
        ctx.WriteMarkup("  [grey]- Settings (.claude/settings.json, ~/.claude/settings.json)[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]See https://www.anthropic.com/privacy for Anthropic's full privacy policy.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays options available when the session is rate limited: wait for reset,
/// switch to a different model, or upgrade the plan.
/// </summary>
public sealed class RateLimitOptionsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/rate-limit-options";

    /// <inheritdoc/>
    public override string Description => "Show options when rate limited (wait, switch model, upgrade plan)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[yellow]Rate limit reached. Available options:[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("  [grey]1. Wait[/]         Rate limits reset after a short window (typically 1 minute).");
        ctx.WriteMarkup("  [grey]2. Switch model[/] Use /model to see available models; haiku has higher limits.");
        ctx.WriteMarkup("  [grey]3. Upgrade plan[/] See /upgrade for Claude Max plan details and higher limits.");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Tip: Use /usage to check your current usage tier.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays the current application version and directs the user to the online
/// release notes for the full changelog.
/// </summary>
public sealed class ReleaseNotesCommand : SlashCommand
{
    private const string ReleaseNotesUrl = "https://github.com/anthropics/claude-code/releases";

    /// <inheritdoc/>
    public override string Name => "/release-notes";

    /// <inheritdoc/>
    public override string Description => "Show current version and a link to release notes";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var version = ctx.Version ?? "unknown";
        ctx.WriteMarkup($"[grey]ClaudeCode C# version:[/] [yellow]{version.EscapeMarkup()}[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Release notes and changelog:[/]");
        ctx.WriteMarkup($"[blue underline]{ReleaseNotesUrl}[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Triggers a plugin reload cycle: re-scans plugin directories and registers discovered tools.
/// Delegates the actual reload work to the <see cref="CommandContext.ReloadPlugins"/> action
/// supplied by <c>ReplSession</c>.
/// </summary>
public sealed class ReloadPluginsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/reload-plugins";

    /// <inheritdoc/>
    public override string Description => "Reload all plugins";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ReloadPluginsAndCommands is { } reload)
        {
            reload();
            ctx.WriteMarkup("[green]Plugins and plugin commands reloaded.[/]");
        }
        else if (ctx.ReloadPlugins is { } reloadTools)
        {
            reloadTools();
            ctx.WriteMarkup("[green]Plugin tools reloaded.[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]Plugin reload not available in this context.[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays or describes the current default remote environment configuration.
/// Full remote-env management requires Phase 16+ infrastructure.
/// </summary>
public sealed class RemoteEnvCommand : SlashCommand
{
    private const string RemoteEnvVar = "CLAUDE_CODE_REMOTE_ENV";

    /// <inheritdoc/>
    public override string Name => "/remote-env";

    /// <inheritdoc/>
    public override string Description => "Show or configure the default remote environment";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var current = Environment.GetEnvironmentVariable(RemoteEnvVar);

        if (!string.IsNullOrWhiteSpace(current))
        {
            ctx.WriteMarkup($"[grey]Remote environment:[/] [yellow]{current.EscapeMarkup()}[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]Remote environment:[/] [grey](not configured)[/]");
            ctx.Write(string.Empty);
            ctx.WriteMarkup($"[grey]Set {RemoteEnvVar} to configure a default remote environment.[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Renames the current conversation. The new name is accepted as a command
/// argument and stored for the lifetime of the process.
/// </summary>
public sealed class RenameCommand : SlashCommand
{
    private static string? _conversationName;

    /// <inheritdoc/>
    public override string Name => "/rename";

    /// <inheritdoc/>
    public override string Description => "Rename the current conversation: /rename <new name>";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length == 0)
        {
            if (_conversationName is not null)
                ctx.WriteMarkup($"[grey]Current name:[/] [yellow]{_conversationName.EscapeMarkup()}[/]");
            else
                ctx.WriteMarkup("[grey]No name set. Usage: /rename <new name>[/]");

            return Task.FromResult(true);
        }

        _conversationName = string.Join(' ', ctx.Args);
        ctx.WriteMarkup($"[green]Conversation renamed to:[/] [yellow]{_conversationName.EscapeMarkup()}[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Maintains a rolling list of file-content snapshots captured immediately before each
/// file edit and lets the user select one to restore. Snapshots are recorded automatically
/// via <see cref="ClaudeCode.Core.Events.FileEditEvents.BeforeEdit"/>; the static
/// constructor wires that subscription so recording begins as soon as this class is first
/// referenced (which happens when <c>BuildCommandRegistry()</c> instantiates it).
/// </summary>
public sealed class RewindCommand : SlashCommand
{
    private const int MaxSnapshots = 20;

    private static readonly List<(string Path, string Content, DateTime Ts)> _snapshots = [];
    private static readonly object _snapshotsLock = new();

    // Static constructor: subscribe to the BeforeEdit event once per process lifetime.
    static RewindCommand()
    {
        ClaudeCode.Core.Events.FileEditEvents.BeforeEdit += RecordSnapshot;
    }

    /// <summary>
    /// Records a pre-edit snapshot of <paramref name="path"/>. Invoked by the
    /// <see cref="ClaudeCode.Core.Events.FileEditEvents.BeforeEdit"/> event.
    /// Keeps at most <see cref="MaxSnapshots"/> entries; older entries are discarded.
    /// </summary>
    /// <param name="path">The absolute path of the file about to be modified.</param>
    /// <param name="content">The current (pre-edit) content of the file.</param>
    public static void RecordSnapshot(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        lock (_snapshotsLock)
        {
            _snapshots.Add((path, content, DateTime.UtcNow));
            while (_snapshots.Count > MaxSnapshots)
                _snapshots.RemoveAt(0);
        }
    }

    /// <inheritdoc/>
    public override string Name => "/rewind";

    /// <inheritdoc/>
    public override string Description => "Restore a file to a pre-edit snapshot (undo a file change)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        List<(string Path, string Content, DateTime Ts)> snapshots;
        lock (_snapshotsLock)
            snapshots = [.. _snapshots];

        if (snapshots.Count == 0)
        {
            ctx.WriteMarkup("[grey]No snapshots recorded this session.[/]");
            ctx.WriteMarkup("[grey]Snapshots are captured automatically before each file edit.[/]");
            return true;
        }

        // Show all snapshotted files in a table (most-recent first).
        var reversed = snapshots.AsEnumerable().Reverse().Take(MaxSnapshots).ToList();

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("#");
        table.AddColumn("Time");
        table.AddColumn("File");
        table.AddColumn("Chars");

        for (int i = 0; i < reversed.Count; i++)
        {
            var s = reversed[i];
            table.AddRow(
                (i + 1).ToString(),
                s.Ts.ToLocalTime().ToString("HH:mm:ss"),
                Path.GetFileName(s.Path).EscapeMarkup(),
                s.Content.Length.ToString("N0"));
        }

        AnsiConsole.Write(table);

        var confirm = AnsiConsole.Confirm("Restore all snapshots?", defaultValue: false);
        if (!confirm)
        {
            ctx.WriteMarkup("[grey]Rewind cancelled.[/]");
            return true;
        }

        // Restore every snapshot; track success and failure counts.
        int restored = 0;
        int failed   = 0;

        foreach (var snap in reversed)
        {
            try
            {
                await File.WriteAllTextAsync(snap.Path, snap.Content, ct).ConfigureAwait(false);
                restored++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.WriteMarkup($"[red]Failed to restore {snap.Path.EscapeMarkup()}:[/] {ex.Message.EscapeMarkup()}");
                failed++;
            }
        }

        ctx.WriteMarkup(
            $"[green]Restored {restored} file(s).[/]" +
            (failed > 0 ? $" [red]{failed} failed.[/]" : string.Empty));

        return true;
    }
}

/// <summary>
/// Toggles sandbox mode, which isolates command execution in a restricted
/// environment to prevent unintended side effects during tool use.
/// When enabled, sets <c>CLAUDE_SANDBOX=1</c> so <see cref="ClaudeCode.Tools.Bash.BashTool"/>
/// wraps commands with <c>bwrap</c> (Linux) or <c>sandbox-exec</c> (macOS).
/// </summary>
public sealed class SandboxToggleCommand : SlashCommand
{
    private static bool _sandboxEnabled;

    /// <summary>Whether sandbox mode is currently enabled.</summary>
    public static bool IsSandboxEnabled => _sandboxEnabled;

    /// <inheritdoc/>
    public override string Name => "/sandbox-toggle";

    /// <inheritdoc/>
    public override string Description => "Toggle sandbox mode (command execution isolation)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        _sandboxEnabled = !_sandboxEnabled;

        // Propagate the toggle into SandboxModeState so BashTool.BuildProcessArgs sees it.
        ClaudeCode.Core.State.SandboxModeState.SetEnabled(_sandboxEnabled);

        // Also propagate to the environment for any other consumers.
        if (_sandboxEnabled)
            Environment.SetEnvironmentVariable("CLAUDE_SANDBOX", "1");
        else
            Environment.SetEnvironmentVariable("CLAUDE_SANDBOX", null);

        var state = _sandboxEnabled ? "[green]enabled[/]" : "[grey]disabled[/]";
        ctx.WriteMarkup($"[grey]Sandbox mode:[/] {state}");
        ctx.Write(string.Empty);

        if (_sandboxEnabled)
        {
            var profile = GetActiveProfile();
            ctx.WriteMarkup($"[grey]Active profile:[/] [blue]{profile.EscapeMarkup()}[/]");
            ctx.WriteMarkup("[grey]Shell commands now run in an isolated environment.[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]Commands run with your normal user permissions.[/]");
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Returns a human-readable description of the sandbox profile that will be applied
    /// on the current platform when <c>CLAUDE_SANDBOX=1</c> is active.
    /// </summary>
    private static string GetActiveProfile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "none (Windows — no sandbox wrapping available)";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return IsBwrapOnPath()
                ? "bwrap (filesystem isolation)"
                : "none (bwrap not found on PATH)";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "sandbox-exec (network isolation)";

        return "unknown";
    }

    private static bool IsBwrapOnPath()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("which", "bwrap")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(500);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// Runs <c>git diff</c> and displays a security-focused review prompt, suggesting
/// that the user ask Claude to inspect the diff for vulnerabilities.
/// </summary>
public sealed class SecurityReviewCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/security-review";

    /// <inheritdoc/>
    public override string Description => "Run git diff and prompt a security-focused review with Claude";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "diff")
            {
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[yellow]Could not start git process.[/]");
                return true;
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stdout))
            {
                ctx.WriteMarkup("[grey]No changes detected in the working tree.[/]");
                return true;
            }

            ctx.Write(stdout.TrimEnd());
            ctx.Write(string.Empty);
            ctx.WriteMarkup("[yellow]Security Review Prompt:[/]");
            ctx.WriteMarkup(
                "[grey]Ask Claude: \"Review the above diff for security vulnerabilities, " +
                "including injection risks, insecure defaults, exposed secrets, " +
                "improper input validation, and unsafe deserialization.\"[/]");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Git not available: {ex.Message.EscapeMarkup()}[/]");
        }

        return true;
    }
}

/// <summary>
/// Displays detailed usage statistics: session count from saved sessions,
/// total accumulated cost, and the average message count per session.
/// </summary>
public sealed class StatsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/stats";

    /// <inheritdoc/>
    public override string Description => "Show detailed usage statistics (sessions, cost, messages)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var store = new SessionStore();
        // List up to 1000 sessions to produce accurate aggregate stats.
        var sessions = await store.ListRecentAsync(1000, ct).ConfigureAwait(false);

        var sessionCount  = sessions.Count;
        var totalCostUsd  = sessions.Sum(s => s.CostUsd);
        var totalMessages = sessions.Sum(s => s.MessageCount);
        var avgMessages   = sessionCount > 0 ? (double)totalMessages / sessionCount : 0.0;

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Saved sessions",         sessionCount.ToString("N0"));
        table.AddRow("Total accumulated cost", ClaudeCode.Services.Api.CostTracker.FormatCost(totalCostUsd));
        table.AddRow("Total messages",         totalMessages.ToString("N0"));
        table.AddRow("Avg messages / session", $"{avgMessages:F1}");

        if (ctx.CostTracker is not null)
        {
            table.AddRow("Current session cost",
                ClaudeCode.Services.Api.CostTracker.FormatCost(ctx.CostTracker.TotalCostUsd));
        }

        AnsiConsole.Write(table);

        // Append tool usage report for the current session when available.
        if (ctx.ToolUsageSummary is not null)
        {
            var report = ctx.ToolUsageSummary.BuildReport();
            if (!string.IsNullOrEmpty(report))
            {
                AnsiConsole.MarkupLine("\n[bold]Tool Usage This Session[/]");
                ctx.Write(report);
            }
        }

        return true;
    }
}

/// <summary>
/// Displays a fun message about ordering official Claude Code stickers.
/// </summary>
public sealed class StickersCommand : SlashCommand
{
    private const string StickersUrl = "https://www.anthropic.com/";

    /// <inheritdoc/>
    public override string Name => "/stickers";

    /// <inheritdoc/>
    public override string Description => "Show info about Claude Code stickers";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[yellow]Claude Code Stickers[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Want official Claude Code stickers? Check out Anthropic's website:[/]");
        ctx.WriteMarkup($"[blue underline]{StickersUrl}[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Show your Claude Code pride! Great for laptops, water bottles, and keyboards.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays terminal-specific setup instructions for popular terminal emulators,
/// including iTerm2 Option+Enter and Windows Terminal configuration.
/// </summary>
public sealed class TerminalSetupCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/terminal-setup";

    /// <inheritdoc/>
    public override string Description => "Show terminal-specific setup instructions";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[grey]Terminal Setup Instructions[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[yellow]iTerm2 (macOS)[/]");
        ctx.WriteMarkup("  [grey]- Option+Enter sends a newline without submitting.[/]");
        ctx.WriteMarkup("  [grey]- Enable in: Preferences > Profiles > Keys > Left Option key: Esc+[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[yellow]Windows Terminal[/]");
        ctx.WriteMarkup("  [grey]- Use Shift+Enter to insert a newline without submitting.[/]");
        ctx.WriteMarkup("  [grey]- For best colour support, set COLORTERM=truecolor in your profile.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[yellow]VS Code Integrated Terminal[/]");
        ctx.WriteMarkup("  [grey]- Works out of the box; TERM is set to xterm-256color.[/]");
        ctx.WriteMarkup("  [grey]- For ligature support, configure \"editor.fontLigatures\" in settings.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[yellow]General[/]");
        ctx.WriteMarkup("  [grey]- Ensure your terminal supports 256-colour or truecolor output.[/]");
        ctx.WriteMarkup("  [grey]- A Nerd Font is recommended for glyph support.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows information about upgrading to Claude Max for higher rate limits
/// and additional features.
/// </summary>
public sealed class UpgradeCommand : SlashCommand
{
    private const string UpgradeUrl = "https://claude.ai/upgrade";

    /// <inheritdoc/>
    public override string Name => "/upgrade";

    /// <inheritdoc/>
    public override string Description => "Show info about upgrading to Claude Max";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[yellow]Upgrade to Claude Max[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Claude Max provides:[/]");
        ctx.WriteMarkup("  [grey]- Higher rate limits for API usage[/]");
        ctx.WriteMarkup("  [grey]- Priority access during peak times[/]");
        ctx.WriteMarkup("  [grey]- Access to the latest model versions[/]");
        ctx.WriteMarkup("  [grey]- Increased context window options[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Upgrade at:[/]");
        ctx.WriteMarkup($"[blue underline]{UpgradeUrl}[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays current plan usage information, including the rate limit tier
/// and a per-model usage breakdown from the cost tracker.
/// </summary>
public sealed class UsageCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/usage";

    /// <inheritdoc/>
    public override string Description => "Show current plan usage and model usage breakdown";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[grey]Usage Information[/]");
        ctx.Write(string.Empty);

        var rlInfo = ctx.CostTracker?.LastRateLimitInfo;
        if (rlInfo is not null)
        {
            ctx.WriteMarkup("[grey]Rate limits (from last API response):[/]");
            if (rlInfo.RequestsLimit.HasValue || rlInfo.RequestsRemaining.HasValue)
                ctx.WriteMarkup($"[grey]  Requests: {rlInfo.RequestsRemaining?.ToString() ?? "?"} remaining / {rlInfo.RequestsLimit?.ToString() ?? "?"} limit[/]");
            if (rlInfo.TokensLimit.HasValue || rlInfo.TokensRemaining.HasValue)
                ctx.WriteMarkup($"[grey]  Tokens: {rlInfo.TokensRemaining?.ToString() ?? "?"} remaining / {rlInfo.TokensLimit?.ToString() ?? "?"} limit[/]");
            if (rlInfo.InputTokensLimit.HasValue || rlInfo.InputTokensRemaining.HasValue)
                ctx.WriteMarkup($"[grey]  Input tokens: {rlInfo.InputTokensRemaining?.ToString() ?? "?"} remaining / {rlInfo.InputTokensLimit?.ToString() ?? "?"} limit[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]Rate limits:[/] [yellow](not yet available — make a request first)[/]");
        }
        ctx.Write(string.Empty);

        if (ctx.CostTracker is not { } tracker)
        {
            ctx.WriteMarkup("[grey]No usage data available for this session.[/]");
            return Task.FromResult(true);
        }

        ctx.WriteMarkup($"[grey]{tracker.FormatUsageSummary().EscapeMarkup()}[/]");
        ctx.Write(string.Empty);

        var usage = tracker.GetModelUsage();
        if (usage.Count == 0)
        {
            ctx.WriteMarkup("[grey]No model usage recorded yet.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Model");
        table.AddColumn("Input");
        table.AddColumn("Output");
        table.AddColumn("Cost");

        foreach (var (modelId, u) in usage)
        {
            table.AddRow(
                modelId.EscapeMarkup(),
                u.InputTokens.ToString("N0"),
                u.OutputTokens.ToString("N0"),
                ClaudeCode.Services.Api.CostTracker.FormatCost(u.CostUsd));
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Toggles voice mode (TTS/STT). When enabled, assistant responses are spoken
/// aloud via the platform's native TTS engine. Voice input via the Whisper CLI
/// is reported when detected. Detected TTS engine is shown on activation.
/// </summary>
public sealed class VoiceCommand : SlashCommand
{
    /// <summary>Whether voice mode (TTS playback) is currently active.</summary>
    public static bool VoiceEnabled => ReplModeFlags.VoiceMode;

    /// <inheritdoc/>
    public override string Name => "/voice";

    /// <inheritdoc/>
    public override string Description => "Toggle voice mode (TTS/STT support)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ReplModeFlags.VoiceMode = !ReplModeFlags.VoiceMode;

        if (ReplModeFlags.VoiceMode)
        {
            var engine = DetectTtsEngine();
            if (engine is null)
            {
                // Revert — no usable engine found.
                ReplModeFlags.VoiceMode = false;
                ctx.WriteMarkup("[red]No TTS engine detected — voice mode not enabled.[/]");
                ctx.WriteMarkup("[grey]  Windows: PowerShell/SAPI is built-in.[/]");
                ctx.WriteMarkup("[grey]  macOS:   'say' is built-in.[/]");
                ctx.WriteMarkup("[grey]  Linux:   Install 'espeak' or 'spd-say'.[/]");
                return true;
            }

            ctx.WriteMarkup($"[green]Voice mode enabled — TTS engine: {engine.EscapeMarkup()}[/]");

            // Report Whisper availability for voice input.
            if (IsCommandAvailable("whisper"))
            {
                ctx.WriteMarkup("[green]Whisper CLI detected — voice input is available.[/]");
                ctx.WriteMarkup("[grey](Audio capture requires arecord / sox / ffmpeg on the PATH)[/]");
            }
            else
            {
                ctx.WriteMarkup("[grey]Whisper CLI not found — voice input unavailable (text input only).[/]");
                ctx.WriteMarkup("[grey]Install with: pip install openai-whisper[/]");
            }

            // Speak a short confirmation so the user can verify TTS is working.
            await SpeakAsync("Voice mode enabled", ct).ConfigureAwait(false);

            // STT: start voice input when voice feature flag is enabled.
            if (ClaudeCode.Configuration.FeatureFlags.IsEnabled("voice"))
                ctx.ToggleVoiceInput?.Invoke(true);
            else
                ctx.WriteMarkup("[grey]Voice input (STT) available with: CLAUDE_FEATURE_VOICE=1[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]Voice mode disabled.[/]");

            // STT: stop voice input.
            if (ClaudeCode.Configuration.FeatureFlags.IsEnabled("voice"))
                ctx.ToggleVoiceInput?.Invoke(false);
        }

        return true;
    }

    /// <summary>
    /// Speaks <paramref name="text"/> using the platform's native TTS engine.
    /// No-ops silently when TTS is unavailable, voice mode is off, or the text is blank.
    /// </summary>
    /// <param name="text">Text to speak. Ignored when <see langword="null"/> or whitespace.</param>
    /// <param name="ct">Cancellation token. Re-thrown on cancellation.</param>
    public static async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !ReplModeFlags.VoiceMode) return;

        try
        {
            // Clip to 500 characters — TTS of full responses is unusably slow.
            var safe = text.Length > 500 ? text[..500] : text;

            ProcessStartInfo psi;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use PowerShell -EncodedCommand to invoke System.Speech without adding a
                // Windows-only NuGet package to this cross-platform project.
                var safePs = safe.Replace("'", "''");  // PowerShell single-quote escape
                var script = "Add-Type -AssemblyName System.Speech; " +
                             $"(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak('{safePs}')";
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi = new ProcessStartInfo("say") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(safe);
            }
            else
            {
                // Linux: prefer espeak, fall through to spd-say.
                var tts = IsCommandAvailable("espeak") ? "espeak" : "spd-say";
                psi = new ProcessStartInfo(tts) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(safe);
            }

            using var proc = Process.Start(psi);
            if (proc is null) return;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* TTS process unavailable or failed — silently skip */ }
    }

    /// <summary>
    /// Returns a human-readable name for the available TTS engine on the current platform,
    /// or <see langword="null"/> when no TTS engine is detectable.
    /// </summary>
    internal static string? DetectTtsEngine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows PowerShell / SAPI (System.Speech)";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return IsCommandAvailable("say") ? "macOS say" : null;

        // Linux
        if (IsCommandAvailable("espeak")) return "Linux espeak";
        if (IsCommandAvailable("spd-say")) return "Linux spd-say";
        return null;
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var finder = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            using var proc = Process.Start(new ProcessStartInfo(finder, command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(1_000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// Manages the WebSocket bridge server that IDE extensions (VS Code, JetBrains) use to
/// communicate with the running REPL session. Supports start, stop, and status subcommands.
/// </summary>
/// <remarks>
/// Usage: <c>/bridge start | stop | status</c>
/// </remarks>
public sealed class BridgeCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/bridge";

    /// <inheritdoc/>
    public override string Description => "Manage the IDE bridge WebSocket server: /bridge start|stop|status";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var sub = ctx.Args.Length > 0 ? ctx.Args[0].ToLowerInvariant() : "status";

        switch (sub)
        {
            case "start":
                if (ctx.BridgeStart is null)
                {
                    ctx.WriteMarkup("[red]Bridge infrastructure is not available in this session.[/]");
                    return true;
                }
                await ctx.BridgeStart(ct).ConfigureAwait(false);
                if (ctx.BridgeGetStatus is { } gs)
                {
                    var (running, port, token) = gs();
                    if (running)
                    {
                        ctx.WriteMarkup($"[green]Bridge server started on [bold]ws://localhost:{port}[/][/]");
                        ctx.WriteMarkup($"[grey]Bridge token: [bold]{token.EscapeMarkup()}[/][/]");
                        ctx.WriteMarkup("[grey]Provide this token to the IDE extension as the bearer token.[/]");
                    }
                }
                break;

            case "stop":
                if (ctx.BridgeStop is null)
                {
                    ctx.WriteMarkup("[red]Bridge infrastructure is not available in this session.[/]");
                    return true;
                }
                await ctx.BridgeStop().ConfigureAwait(false);
                ctx.WriteMarkup("[grey]Bridge server stopped.[/]");
                break;

            case "status":
            default:
                if (ctx.BridgeGetStatus is { } getStatus)
                {
                    var (running, port, token) = getStatus();
                    if (running)
                    {
                        ctx.WriteMarkup("[green]Bridge server is running.[/]");
                        ctx.WriteMarkup($"  [grey]Address:[/] [yellow]ws://localhost:{port}[/]");
                        ctx.WriteMarkup($"  [grey]Token:   [/][yellow]{token.EscapeMarkup()}[/]");
                    }
                    else
                    {
                        ctx.WriteMarkup("[grey]Bridge server is [bold]not[/bold] running.[/]");
                        ctx.WriteMarkup("[grey]Start with:[/] [yellow]/bridge start[/]");
                    }
                }
                else
                {
                    ctx.WriteMarkup("[grey]Bridge infrastructure is not wired in this session.[/]");
                }
                break;
        }

        return true;
    }
}

/// <summary>
/// Toggles brief mode. When brief mode is on, Claude is instructed to prefer
/// concise responses over exhaustive explanations.
/// </summary>
public sealed class BriefCommand : SlashCommand
{
    /// <summary>Whether brief mode is currently enabled.</summary>
    public static bool IsBriefModeEnabled => ClaudeCode.Core.State.ReplModeFlags.BriefMode;

    /// <inheritdoc/>
    public override string Name => "/brief";

    /// <inheritdoc/>
    public override string Description => "Toggle brief mode (more concise responses)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ClaudeCode.Core.State.ReplModeFlags.BriefMode = !ClaudeCode.Core.State.ReplModeFlags.BriefMode;

        if (ClaudeCode.Core.State.ReplModeFlags.BriefMode)
        {
            ctx.WriteMarkup("[green]Brief mode enabled — Claude will aim for concise responses.[/]");
            ctx.WriteMarkup("[grey](Brief mode injects a conciseness instruction into the next prompt.)[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]Brief mode disabled — normal response length restored.[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Manages session tags. When a tag name argument is supplied the tag is stored;
/// when invoked with no arguments, all current tags are listed.
/// </summary>
public sealed class TagCommand : SlashCommand
{
    private static readonly List<string> _tags = [];

    /// <summary>Gets a read-only view of the active session tags.</summary>
    public static IReadOnlyList<string> ActiveTags => _tags.AsReadOnly();

    /// <inheritdoc/>
    public override string Name => "/tag";

    /// <inheritdoc/>
    public override string Description => "Add a tag to the session, or list all tags: /tag [<name>]";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length == 0)
        {
            if (_tags.Count == 0)
            {
                ctx.WriteMarkup("[grey]No tags. Use /tag <name> to add a tag.[/]");
            }
            else
            {
                ctx.WriteMarkup("[grey]Session tags:[/]");
                foreach (var tag in _tags)
                    ctx.WriteMarkup($"  [yellow]{tag.EscapeMarkup()}[/]");
            }

            return Task.FromResult(true);
        }

        var newTag = string.Join(' ', ctx.Args);

        if (_tags.Contains(newTag, StringComparer.OrdinalIgnoreCase))
        {
            ctx.WriteMarkup($"[grey]Tag already exists:[/] [yellow]{newTag.EscapeMarkup()}[/]");
        }
        else
        {
            _tags.Add(newTag);
            ctx.WriteMarkup($"[green]Tag added:[/] [yellow]{newTag.EscapeMarkup()}[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Aggregates statistics across all saved sessions and displays a "year in review"-style
/// summary covering total sessions, messages, cost, tool usage, and model preferences.
/// Full sessions are loaded (up to <see cref="MaxFullSessionsForToolAnalysis"/>) for tool
/// use analysis; metadata is used for all other aggregations.
/// </summary>
public sealed class ThinkbackCommand : SlashCommand
{
    /// <summary>Maximum number of full sessions to load when counting tool-use blocks.</summary>
    private const int MaxFullSessionsForToolAnalysis = 100;

    /// <inheritdoc/>
    public override string Name => "/thinkback";

    /// <inheritdoc/>
    public override string Description => "Show cross-session statistics and year-in-review summary";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var store = new SessionStore();
        var allMeta = await store.ListRecentAsync(maxCount: 10_000, ct).ConfigureAwait(false);

        if (allMeta.Count == 0)
        {
            ctx.WriteMarkup("[grey]No saved sessions found.[/]");
            var sessionsDir = Path.Combine(ConfigPaths.ClaudeHomeDir, "sessions");
            ctx.WriteMarkup($"[grey]Sessions are saved in: {sessionsDir.EscapeMarkup()}[/]");
            return true;
        }

        // ── Metadata-based aggregation ────────────────────────────────────────
        var totalMessages = allMeta.Sum(s => s.MessageCount);
        var totalCost     = allMeta.Sum(s => s.CostUsd);
        var avgLength     = allMeta.Average(s => s.MessageCount);

        var busiestDayGroup = allMeta
            .GroupBy(s => s.CreatedAt.Date)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        var favoriteModelGroup = allMeta
            .Where(s => !string.IsNullOrWhiteSpace(s.Model))
            .GroupBy(s => s.Model, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        // ── Current session live tokens (not stored in metadata) ─────────────
        var currentIn  = ctx.CostTracker?.TotalInputTokens ?? 0;
        var currentOut = ctx.CostTracker?.TotalOutputTokens ?? 0;

        // ── Full-session load for tool-use counts (limited) ───────────────────
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var analysisLimit = Math.Min(allMeta.Count, MaxFullSessionsForToolAnalysis);
        foreach (var meta in allMeta.Take(analysisLimit))
        {
            ct.ThrowIfCancellationRequested();
            var session = await store.LoadAsync(meta.Id, ct).ConfigureAwait(false);
            if (session is null) continue;
            CountToolUse(session.Messages, toolCounts);
        }

        // ── Display ───────────────────────────────────────────────────────────
        ctx.WriteMarkup("[bold yellow]Thinkback — Session Analytics[/]");
        ctx.Write(string.Empty);

        var statsTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
        statsTable.AddColumn("[grey]Metric[/]");
        statsTable.AddColumn("[grey]Value[/]");

        statsTable.AddRow("Total sessions",      allMeta.Count.ToString("N0"));
        statsTable.AddRow("Total messages",      totalMessages.ToString("N0"));
        statsTable.AddRow("Total cost",          $"${totalCost:F4}");
        statsTable.AddRow("Avg session length",  $"{avgLength:F1} messages");

        if (currentIn > 0 || currentOut > 0)
            statsTable.AddRow("Current session tokens", $"{currentIn:N0} in / {currentOut:N0} out");

        if (busiestDayGroup is not null)
            statsTable.AddRow(
                "Busiest day",
                $"{busiestDayGroup.Key:yyyy-MM-dd} ({busiestDayGroup.Count()} sessions)");

        if (favoriteModelGroup is not null)
            statsTable.AddRow(
                "Favorite model",
                $"{favoriteModelGroup.Key.EscapeMarkup()} ({favoriteModelGroup.Count()} sessions)");

        AnsiConsole.Write(statsTable);

        // Top tools
        if (toolCounts.Count > 0)
        {
            ctx.Write(string.Empty);
            ctx.WriteMarkup($"[grey](Tool analysis from last {analysisLimit} session(s))[/]");

            var toolTable = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
            toolTable.AddColumn("Tool");
            toolTable.AddColumn("Uses");

            foreach (var (name, count) in toolCounts.OrderByDescending(kv => kv.Value).Take(10))
                toolTable.AddRow(name.EscapeMarkup(), count.ToString("N0"));

            AnsiConsole.Write(toolTable);
        }

        return true;
    }

    /// <summary>
    /// Scans <paramref name="messages"/> for assistant messages that contain
    /// <c>tool_use</c> content blocks and increments the per-name counter in
    /// <paramref name="toolCounts"/>.
    /// </summary>
    private static void CountToolUse(
        IEnumerable<MessageParam> messages,
        Dictionary<string, int> toolCounts)
    {
        foreach (var msg in messages)
        {
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
            if (msg.Content.ValueKind != JsonValueKind.Array) continue;

            foreach (var block in msg.Content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl)) continue;
                if (typeEl.GetString() != "tool_use") continue;
                if (!block.TryGetProperty("name", out var nameEl)) continue;

                var name = nameEl.GetString();
                if (name is null) continue;

                toolCounts[name] = toolCounts.GetValueOrDefault(name) + 1;
            }
        }
    }
}

// =============================================================================
// 20 additional commands — advisor/agents/btw/chrome/color/commit-push-pr/
//                           desktop/effort/extra-usage/heapdump/ide/
//                           init-verifiers/insights/install-github-app/
//                           install-slack-app/keybindings/mobile/output-style/
//                           passes/plugin
// =============================================================================

/// <summary>
/// Shows or sets the advisor model used for background guidance.
/// Accepts a model name as an optional argument; persists to a process-wide static field.
/// </summary>
public sealed class AdvisorCommand : SlashCommand
{
    private static string? _advisorModel;

    /// <summary>Gets the active advisor model name, or <see langword="null"/> when none is configured.</summary>
    public static string? ActiveAdvisorModel => _advisorModel;

    /// <inheritdoc/>
    public override string Name => "/advisor";

    /// <inheritdoc/>
    public override string Description => "Show or set the advisor model: /advisor [model-name]";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length > 0)
        {
            _advisorModel = ctx.Args[0];
            ctx.WriteMarkup($"[grey]Advisor model set to:[/] [green]{_advisorModel.EscapeMarkup()}[/]");
        }
        else if (_advisorModel is not null)
        {
            ctx.WriteMarkup($"[grey]Current advisor model:[/] [yellow]{_advisorModel.EscapeMarkup()}[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]No advisor model configured. Use /advisor <model-name> to set one.[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Lists agent definitions discovered in <c>.claude/agents/</c> (project and global)
/// and renders them as a table.
/// </summary>
public sealed class AgentsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/agents";

    /// <inheritdoc/>
    public override string Description => "List agent definitions from .claude/agents/";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var agents = ClaudeCode.Tools.Agent.AgentDefinitionLoader.LoadFromDirectory(ctx.Cwd);

        if (agents.Count == 0)
        {
            ctx.WriteMarkup("[grey]No agents found. Add .md files to .claude/agents/ to define agents.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Name");
        table.AddColumn("Model");
        table.AddColumn("Description");

        foreach (var agent in agents)
        {
            table.AddRow(
                agent.Name.EscapeMarkup(),
                (agent.Model ?? "(inherited)").EscapeMarkup(),
                (agent.Description ?? string.Empty).EscapeMarkup());
        }

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Accepts a side-question as an argument and confirms it will be appended to the next prompt.
/// </summary>
public sealed class BtwCommand : SlashCommand
{
    private static string? _pendingBtw;

    /// <summary>
    /// Sets the pending BTW question. Called via the <see cref="CommandContext.SetPendingBtw"/> delegate
    /// wired in <c>ReplSession</c>.
    /// </summary>
    public static void SetPendingBtwValue(string value) => _pendingBtw = value;

    /// <summary>
    /// Returns the pending BTW question and clears it so it is only consumed once.
    /// Returns <see langword="null"/> when no question is pending.
    /// </summary>
    public static string? ConsumePendingBtw()
    {
        var val = _pendingBtw;
        _pendingBtw = null;
        return val;
    }

    /// <inheritdoc/>
    public override string Name => "/btw";

    /// <inheritdoc/>
    public override string Description => "Append a side-question to the next prompt: /btw <question>";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length == 0)
        {
            ctx.WriteMarkup("[yellow]Usage: /btw <question>[/]");
            ctx.WriteMarkup("[grey]Example: /btw why is this pattern preferred over the alternative?[/]");
            return Task.FromResult(true);
        }

        var question = string.Join(' ', ctx.Args);
        ctx.SetPendingBtw?.Invoke(question);
        ctx.WriteMarkup($"[grey]Noted: \"{question.EscapeMarkup()}\"[/]");
        ctx.WriteMarkup("[grey]This question will be appended to your next prompt.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows Claude-in-Chrome integration status and setup instructions for the browser extension.
/// </summary>
public sealed class ChromeCommand : SlashCommand
{
    private const string ExtensionUrl =
        "https://chromewebstore.google.com/detail/claude-for-google-chrome/ghbmfigpdcfhblpkpijmipghebjnfmni";

    /// <inheritdoc/>
    public override string Name => "/chrome";

    /// <inheritdoc/>
    public override string Description => "Show Claude-in-Chrome integration status and setup instructions";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Claude in Chrome[/]");
        ctx.WriteMarkup("[grey]The Claude for Chrome extension lets you use Claude directly in your browser.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Setup steps:[/]");
        ctx.WriteMarkup("[grey]  1. Install the extension from the Chrome Web Store:[/]");
        ctx.WriteMarkup($"     [blue underline]{ExtensionUrl}[/]");
        ctx.WriteMarkup("[grey]  2. Sign in with your Anthropic account in the extension.[/]");
        ctx.WriteMarkup("[grey]  3. Click the Claude icon in your browser toolbar to start.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows or sets the prompt bar color. Accepts a color name argument.
/// Color preference is stored as a process-wide static field.
/// </summary>
public sealed class ColorCommand : SlashCommand
{
    private static string _promptColor = "cyan";

    private static readonly HashSet<string> ValidColors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "red", "green", "blue", "yellow", "cyan", "magenta", "white",
        };

    /// <summary>Gets the active prompt color name. Defaults to "blue" when not explicitly set.</summary>
    public static string ActivePromptColor => _promptColor ?? "blue";

    /// <inheritdoc/>
    public override string Name => "/color";

    /// <inheritdoc/>
    public override string Description => "Show or set prompt bar color: /color [red|green|blue|yellow|cyan|magenta|white]";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length > 0)
        {
            var requested = ctx.Args[0];
            if (!ValidColors.Contains(requested))
            {
                var valid = string.Join(", ", ValidColors.Order());
                ctx.WriteMarkup(
                    $"[yellow]Unknown color '{requested.EscapeMarkup()}'. Valid options: {valid.EscapeMarkup()}[/]");
                return Task.FromResult(true);
            }

            _promptColor = requested.ToLowerInvariant();
            ctx.WriteMarkup($"[grey]Prompt color set to:[/] [{_promptColor}]{_promptColor}[/]");
        }
        else
        {
            ctx.WriteMarkup($"[grey]Current prompt color:[/] [{_promptColor}]{_promptColor}[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Runs the full <c>git add -A &amp;&amp; git commit &amp;&amp; git push &amp;&amp; gh pr create</c>
/// pipeline, reporting the outcome of each step.
/// </summary>
public sealed class CommitPushPrCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/commit-push-pr";

    /// <inheritdoc/>
    public override string Description => "Stage, commit, push, and open a PR in sequence";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var steps = new[]
        {
            ("git", "add -A",    "Staging all changes"),
            ("git", "commit",    "Creating commit"),
            ("git", "push",      "Pushing to remote"),
            ("gh",  "pr create", "Opening pull request"),
        };

        foreach (var (exe, args, label) in steps)
        {
            ctx.WriteMarkup($"[grey]{label}...[/]");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = ctx.Cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null)
                {
                    ctx.WriteMarkup($"[red]Could not start '{exe}'.[/]");
                    return true;
                }

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(stdout))
                    ctx.Write(stdout.TrimEnd());

                if (proc.ExitCode != 0)
                {
                    var msg = string.IsNullOrWhiteSpace(stderr) ? "non-zero exit" : stderr.TrimEnd();
                    ctx.WriteMarkup($"[red]{label} failed:[/] {msg.EscapeMarkup()}");
                    return true;
                }

                ctx.WriteMarkup($"[green]{label} succeeded.[/]");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.WriteMarkup($"[red]{label} error:[/] {ex.Message.EscapeMarkup()}");
                return true;
            }
        }

        return true;
    }
}

/// <summary>
/// Shows instructions for continuing the current session in the Claude Desktop application.
/// </summary>
public sealed class DesktopCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/desktop";

    /// <inheritdoc/>
    public override string Description => "Show instructions for continuing in Claude Desktop";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Continue in Claude Desktop[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]To continue this session in the Claude Desktop app:[/]");
        ctx.WriteMarkup("[grey]  1. Download Claude Desktop from https://claude.ai/download[/]");
        ctx.WriteMarkup("[grey]  2. Sign in with the same Anthropic account.[/]");
        ctx.WriteMarkup("[grey]  3. Your conversation history and projects sync automatically.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Note: local file access and tool use require the CLI or Desktop app.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows or sets the effort level for the session.
/// Accepts "low", "medium", "high", or "max" as an argument.
/// </summary>
public sealed class EffortCommand : SlashCommand
{
    private static string _effortLevel = "medium";

    private static readonly HashSet<string> ValidLevels =
        new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high", "max" };

    /// <summary>Gets the active effort level. Defaults to "medium" when not explicitly set.</summary>
    public static string ActiveEffortLevel => _effortLevel ?? "medium";

    /// <inheritdoc/>
    public override string Name => "/effort";

    /// <inheritdoc/>
    public override string Description => "Show or set effort level: /effort [low|medium|high|max]";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Args.Length > 0)
        {
            var requested = ctx.Args[0];
            if (!ValidLevels.Contains(requested))
            {
                ctx.WriteMarkup(
                    $"[yellow]Unknown effort level '{requested.EscapeMarkup()}'. Valid options: low, medium, high, max.[/]");
                return Task.FromResult(true);
            }

            _effortLevel = requested.ToLowerInvariant();
            ctx.WriteMarkup($"[grey]Effort level set to:[/] [green]{_effortLevel}[/]");
        }
        else
        {
            ctx.WriteMarkup($"[grey]Current effort level:[/] [yellow]{_effortLevel}[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows information about extra usage options when you are rate-limited,
/// including available plans and a pricing link.
/// </summary>
public sealed class ExtraUsageCommand : SlashCommand
{
    private const string PricingUrl = "https://www.anthropic.com/pricing";

    /// <inheritdoc/>
    public override string Name => "/extra-usage";

    /// <inheritdoc/>
    public override string Description => "Show extra usage options when rate-limited";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Extra Usage Options[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]If you are hitting rate limits, you have several options:[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]  1. [bold]Upgrade your plan[/] — higher tiers have greater rate limits.[/]");
        ctx.WriteMarkup("[grey]  2. [bold]Add Usage Credits[/] — purchase additional API usage on top of your plan.[/]");
        ctx.WriteMarkup("[grey]  3. [bold]Wait for reset[/] — limits reset on a rolling window basis.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]For pricing details and plan comparison, visit:[/]");
        ctx.WriteMarkup($"[blue underline]{PricingUrl}[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Notes that heap dump is not applicable to the .NET runtime (this was a Node.js concept)
/// and suggests the <c>dotnet-dump</c> global tool instead.
/// </summary>
public sealed class HeapdumpCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/heapdump";

    /// <inheritdoc/>
    public override string Description => "Show heap dump guidance for the .NET runtime";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[yellow]/heapdump is not applicable to the .NET runtime.[/]");
        ctx.WriteMarkup("[grey]This command originated in the Node.js version of Claude Code.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]For .NET memory diagnostics, use the dotnet-dump global tool:[/]");
        ctx.WriteMarkup("[grey]  Install:  dotnet tool install --global dotnet-dump[/]");
        ctx.WriteMarkup("[grey]  Collect:  dotnet-dump collect --process-id <PID>[/]");
        ctx.WriteMarkup("[grey]  Analyze:  dotnet-dump analyze <dump-file>[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]See: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows IDE integration status and setup instructions for Visual Studio Code and JetBrains IDEs.
/// </summary>
public sealed class IdeCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/ide";

    /// <inheritdoc/>
    public override string Description => "Show IDE integration status and setup instructions";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]IDE Integration[/]");
        ctx.Write(string.Empty);

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("IDE");
        table.AddColumn("Extension / Plugin");
        table.AddColumn("Marketplace URL");

        table.AddRow(
            "VS Code",
            "Claude Code (official)",
            "https://marketplace.visualstudio.com/items?itemName=Anthropic.claude-code");

        table.AddRow(
            "JetBrains",
            "Claude Code for JetBrains",
            "https://plugins.jetbrains.com/plugin/26148-claude-code");

        AnsiConsole.Write(table);

        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]After installing the extension, open the Claude Code panel from the sidebar.[/]");
        ctx.WriteMarkup("[grey]The extension connects to this CLI session automatically.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Creates a verifier skill template at <c>.claude/skills/verify.md</c> if it does not already exist.
/// </summary>
public sealed class InitVerifiersCommand : SlashCommand
{
    private const string VerifierTemplate =
        "---\n" +
        "name: verify\n" +
        "description: Run verification steps after task completion\n" +
        "---\n\n" +
        "# Verifier Skill\n\n" +
        "Run the following checks after completing any significant task:\n\n" +
        "1. **Build** — ensure the solution compiles without errors or warnings.\n" +
        "2. **Tests** — run the full test suite and confirm all tests pass.\n" +
        "3. **Lint** — check for style violations or code analysis warnings.\n" +
        "4. **Review** — re-read the changed files for obvious issues before declaring done.\n\n" +
        "Report any failures before claiming the task is complete.\n";

    /// <inheritdoc/>
    public override string Name => "/init-verifiers";

    /// <inheritdoc/>
    public override string Description => "Create a verifier skill template at .claude/skills/verify.md";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var skillsDir = Path.Combine(ctx.Cwd, ".claude", "skills");
        var verifyPath = Path.Combine(skillsDir, "verify.md");

        if (File.Exists(verifyPath))
        {
            ctx.WriteMarkup($"[grey]Verifier skill already exists: {verifyPath.EscapeMarkup()}[/]");
            return Task.FromResult(true);
        }

        try
        {
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(verifyPath, VerifierTemplate);
            ctx.WriteMarkup($"[green]Created[/] [grey]{verifyPath.EscapeMarkup()}[/]");
            ctx.WriteMarkup("[grey]Edit the file to customize your verification steps.[/]");
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Failed to create verifier skill:[/] {ex.Message.EscapeMarkup()}");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows session statistics: total cost, message count, and task summary.
/// Reads from <see cref="ClaudeCode.Services.Api.CostTracker"/> and
/// <see cref="ClaudeCode.Core.Tasks.TaskStoreState"/>.
/// </summary>
public sealed class InsightsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/insights";

    /// <inheritdoc/>
    public override string Description => "Show session statistics: cost, messages, task counts";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Session Insights[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup($"[grey]Messages in conversation:[/] [yellow]{ctx.ConversationMessageCount}[/]");

        if (ctx.CostTracker is { } tracker)
            ctx.WriteMarkup($"[grey]Session cost:[/] [yellow]{tracker.FormatUsageSummary().EscapeMarkup()}[/]");
        else
            ctx.WriteMarkup("[grey]Session cost: (no cost tracker available)[/]");

        var tasks = ClaudeCode.Core.Tasks.TaskStoreState.Tasks;
        var total = tasks.Count;
        var completed = tasks.Values.Count(t =>
            string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var inProgress = tasks.Values.Count(t =>
            string.Equals(t.Status, "in_progress", StringComparison.OrdinalIgnoreCase));

        ctx.WriteMarkup(
            $"[grey]Tasks — total:[/] [yellow]{total}[/]  " +
            $"[grey]completed:[/] [green]{completed}[/]  " +
            $"[grey]in-progress:[/] [yellow]{inProgress}[/]");

        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows instructions for installing the Claude GitHub Actions app on a repository.
/// </summary>
public sealed class InstallGithubAppCommand : SlashCommand
{
    private const string AppUrl = "https://github.com/apps/claude";

    /// <inheritdoc/>
    public override string Name => "/install-github-app";

    /// <inheritdoc/>
    public override string Description => "Show instructions for installing the Claude GitHub Actions app";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Install Claude GitHub App[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]The Claude GitHub App enables AI-powered code review in pull requests.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Setup steps:[/]");
        ctx.WriteMarkup("[grey]  1. Visit the GitHub Marketplace listing:[/]");
        ctx.WriteMarkup($"     [blue underline]{AppUrl}[/]");
        ctx.WriteMarkup("[grey]  2. Click \"Install\" and select your organization or account.[/]");
        ctx.WriteMarkup("[grey]  3. Choose which repositories to grant access to.[/]");
        ctx.WriteMarkup("[grey]  4. Add the workflow file to your repo as instructed in the app docs.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Once installed, Claude will review PRs automatically on each push.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows instructions for installing the Claude Slack application in a workspace.
/// </summary>
public sealed class InstallSlackAppCommand : SlashCommand
{
    private const string SlackAppUrl = "https://www.anthropic.com/claude-for-slack";

    /// <inheritdoc/>
    public override string Name => "/install-slack-app";

    /// <inheritdoc/>
    public override string Description => "Show instructions for installing the Claude Slack app";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Install Claude for Slack[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Claude for Slack lets you interact with Claude directly in your workspace.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Setup steps:[/]");
        ctx.WriteMarkup("[grey]  1. Visit the Claude for Slack page:[/]");
        ctx.WriteMarkup($"     [blue underline]{SlackAppUrl}[/]");
        ctx.WriteMarkup("[grey]  2. Click \"Add to Slack\" and authorize the app for your workspace.[/]");
        ctx.WriteMarkup("[grey]  3. Invite @Claude to any channel with: /invite @Claude[/]");
        ctx.WriteMarkup("[grey]  4. Mention @Claude in a message to start a conversation.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Displays the default keybindings and the path to the keybindings configuration file.
/// </summary>
public sealed class KeybindingsCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/keybindings";

    /// <inheritdoc/>
    public override string Description => "Show current keybindings and config file path";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var keybindingsPath = Path.Combine(
            ClaudeCode.Configuration.ConfigPaths.ClaudeHomeDir, "keybindings.json");

        ctx.WriteMarkup("[bold]Keybindings[/]");
        ctx.Write(string.Empty);

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Key");
        table.AddColumn("Action");

        table.AddRow("Enter",       "Submit prompt");
        table.AddRow("Shift+Enter", "New line in prompt");
        table.AddRow("Ctrl+C",      "Cancel current operation");
        table.AddRow("Ctrl+L",      "Clear screen");
        table.AddRow("Up / Down",   "Navigate prompt history");
        table.AddRow("Tab",         "Autocomplete command or path");
        table.AddRow("Ctrl+R",      "Search prompt history");
        table.AddRow("Escape",      "Cancel multi-line input");

        AnsiConsole.Write(table);

        ctx.Write(string.Empty);
        ctx.WriteMarkup($"[grey]Keybindings config:[/] {keybindingsPath.EscapeMarkup()}");
        ctx.WriteMarkup("[grey](Create this file to override defaults.)[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows instructions for continuing the session on the Claude mobile application,
/// including a QR code placeholder.
/// </summary>
public sealed class MobileCommand : SlashCommand
{
    private const string MobileUrl = "https://claude.ai/mobile";

    /// <inheritdoc/>
    public override string Name => "/mobile";

    /// <inheritdoc/>
    public override string Description => "Show instructions for continuing on the Claude mobile app";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Continue on Mobile[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Download the Claude app to continue on your phone or tablet.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]  iOS:     https://apps.apple.com/app/claude-ai/id6473753684[/]");
        ctx.WriteMarkup("[grey]  Android: https://play.google.com/store/apps/details?id=com.anthropic.claude[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup($"[grey]Or visit:[/] [blue underline]{MobileUrl}[/]");
        ctx.Write(string.Empty);

        // Render an ASCII QR code for the mobile URL.
        try
        {
            using var qrGenerator = new QRCoder.QRCodeGenerator();
            using var qrData = qrGenerator.CreateQrCode(MobileUrl, QRCoder.QRCodeGenerator.ECCLevel.M);
            using var qrCode = new QRCoder.AsciiQRCode(qrData);
            var asciiArt = qrCode.GetGraphic(1, drawQuietZones: true);

            ctx.WriteMarkup("[grey]Scan QR code:[/]");
            ctx.Write(asciiArt);
        }
        catch
        {
            ctx.WriteMarkup("[grey][QR code generation unavailable][/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Informs the user that <c>/output-style</c> has been deprecated and directs them to <c>/config</c>.
/// </summary>
public sealed class OutputStyleCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/output-style";

    /// <inheritdoc/>
    public override string Description => "Deprecated — use /config instead";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[yellow]/output-style is deprecated.[/]");
        ctx.WriteMarkup("[grey]Use [bold]/config[/] to view and manage configuration options.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows promotional information about sharing free weeks of Claude with others.
/// </summary>
public sealed class PassesCommand : SlashCommand
{
    private const string PassesUrl = "https://claude.ai/referral";

    /// <inheritdoc/>
    public override string Name => "/passes";

    /// <inheritdoc/>
    public override string Description => "Show info about sharing free weeks of Claude with others";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Share Claude with Friends[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]You can share a free week of Claude Pro with your contacts.[/]");
        ctx.WriteMarkup("[grey]When they sign up using your link, both of you receive a bonus week.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Find your referral link and manage passes at:[/]");
        ctx.WriteMarkup($"[blue underline]{PassesUrl}[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Lists plugins discovered in the global (<c>~/.claude/plugins/</c>) and
/// project-local (<c>.claude/plugins/</c>) directories.
/// Each plugin directory must contain a <c>plugin.json</c> manifest.
/// </summary>
public sealed class PluginCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/plugin";

    /// <inheritdoc/>
    public override string Description => "List plugins from .claude/plugins/ directories";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var loader = new ClaudeCode.Services.Plugins.PluginLoader();
        var plugins = loader.LoadAll(ctx.Cwd);

        if (plugins.Count == 0)
        {
            ctx.WriteMarkup("[grey]No plugins found.[/]");
            ctx.WriteMarkup(
                "[grey]Add plugin directories with a plugin.json manifest to " +
                "~/.claude/plugins/ or .claude/plugins/.[/]");
            return Task.FromResult(true);
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Name");
        table.AddColumn("Version");
        table.AddColumn("Description");

        foreach (var plugin in plugins.OrderBy(p => p.Name))
            table.AddRow(
                plugin.Name.EscapeMarkup(),
                plugin.Version.EscapeMarkup(),
                plugin.Description.EscapeMarkup());

        AnsiConsole.Write(table);
        return Task.FromResult(true);
    }
}

// =============================================================================
// 6 previously missing public commands:
// ultrareview, statusline, thinkback-play, usage-report,
// context-report (contextNonInteractive), install
// =============================================================================

/// <summary>
/// Launches a remote cloud-based code review session via claude.ai that
/// finds and verifies bugs in the current branch (~10–20 minutes).
/// Requires a GitHub repository and a claude.ai account with Extra Usage enabled.
/// </summary>
public sealed class UltrareviewCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/ultrareview";

    /// <inheritdoc/>
    public override string Description => "Launch a remote cloud-based code review (requires claude.ai)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var branch  = await RunGitAsync("rev-parse --abbrev-ref HEAD", ctx.Cwd, ct);
        var remote  = await RunGitAsync("remote get-url origin", ctx.Cwd, ct);

        if (branch is null)
        {
            ctx.WriteMarkup("[red]Ultrareview requires a git repository.[/]");
            ctx.WriteMarkup("[grey]Navigate to a git repository directory and try again.[/]");
            return true;
        }

        var isGitHub = remote?.Contains("github.com", StringComparison.OrdinalIgnoreCase) == true;

        ctx.WriteMarkup("[bold yellow]Ultrareview — Remote Cloud Code Review[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup($"[grey]Branch:[/] [yellow]{branch.EscapeMarkup()}[/]");
        if (remote is not null)
            ctx.WriteMarkup($"[grey]Remote:[/] [yellow]{remote.EscapeMarkup()}[/]");
        ctx.Write(string.Empty);

        if (!isGitHub)
        {
            ctx.WriteMarkup("[red]Ultrareview requires a GitHub repository.[/]");
            ctx.WriteMarkup("[grey]No GitHub remote ('origin') was detected for this directory.[/]");
            return true;
        }

        ctx.WriteMarkup("[grey]Ultrareview launches a cloud session on [bold]claude.ai[/bold] that:[/]");
        ctx.WriteMarkup("[grey]  • Analyses your branch diff against main/master[/]");
        ctx.WriteMarkup("[grey]  • Finds and verifies potential bugs[/]");
        ctx.WriteMarkup("[grey]  • Takes approximately 10–20 minutes[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Requirements:[/]");
        ctx.WriteMarkup("[grey]  • claude.ai account with [bold]Extra Usage[/bold] enabled[/]");
        ctx.WriteMarkup("[grey]  • Sufficient account balance (minimum $10)[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Visit [link=https://claude.ai]https://claude.ai[/link] to start an Ultrareview session.[/]");
        return true;
    }

    private static async Task<string?> RunGitAsync(string args, string cwd, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Configures the terminal status line by inspecting the current setting and
/// guiding the user to launch a <c>statusline-setup</c> sub-agent.
/// In the original Claude Code this command type is 'prompt', meaning it passes
/// a prefilled LLM prompt. In the C# REPL it shows config info and instructions.
/// </summary>
public sealed class StatuslineCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/statusline";

    /// <inheritdoc/>
    public override string Description => "Configure the terminal status line";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.WriteMarkup("[bold]Status Line Configuration[/]");
        ctx.Write(string.Empty);

        // Show the current statusLine value from settings (stored in ExtensionData).
        string? currentValue = null;
        if (ctx.ConfigProvider is ClaudeCode.Configuration.IConfigProvider config)
        {
            if (config.Settings.ExtensionData?.TryGetValue("statusLine", out var slEl) == true &&
                slEl.ValueKind == System.Text.Json.JsonValueKind.String)
                currentValue = slEl.GetString();
        }

        if (currentValue is not null)
        {
            ctx.WriteMarkup("[grey]Current status line:[/]");
            ctx.WriteMarkup($"  [yellow]{currentValue.EscapeMarkup()}[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]No status line configured.[/]");
        }

        ctx.Write(string.Empty);

        var prompt = ctx.Args.Length > 0
            ? string.Join(" ", ctx.Args)
            : "Configure my statusLine from my shell PS1 configuration";

        ctx.WriteMarkup("[grey]To set up the status line, ask Claude:[/]");
        ctx.WriteMarkup($"  [italic]\"{prompt.EscapeMarkup()}\"[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Or set [bold]statusLine[/bold] directly in [bold].claude/settings.json[/bold]:[/]");
        ctx.WriteMarkup("[grey]  { \"statusLine\": \"your-status-format\" }[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]The statusline-setup sub-agent reads your shell config files and[/]");
        ctx.WriteMarkup("[grey]writes the statusLine value to ~/.claude/settings.json automatically.[/]");
        return Task.FromResult(true);
    }
}

/// <summary>
/// Plays the Thinkback animation. If a Thinkback plugin is installed under
/// <c>~/.claude/plugins/thinkback/</c> or <c>{cwd}/.claude/plugins/thinkback/</c>,
/// its entry-point script is executed. Otherwise the current session messages are
/// replayed to the console with a short delay between each for a playback effect.
/// </summary>
public sealed class ThinkbackPlayCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/thinkback-play";

    /// <inheritdoc/>
    public override string Description => "Play the Thinkback animation or replay session messages";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Resolve plugin directory: project-local takes precedence over global.
        var globalPluginDir = Path.Combine(ConfigPaths.ClaudeHomeDir, "plugins", "thinkback");
        var localPluginDir  = Path.Combine(ctx.Cwd, ".claude", "plugins", "thinkback");

        var pluginDir = Directory.Exists(localPluginDir) ? localPluginDir
                      : Directory.Exists(globalPluginDir) ? globalPluginDir
                      : null;

        if (pluginDir is not null)
        {
            ctx.WriteMarkup($"[green]Thinkback plugin found at {pluginDir.EscapeMarkup()}[/]");
            await RunThinkbackPluginAsync(pluginDir, ctx, ct).ConfigureAwait(false);
            return true;
        }

        // No plugin — replay current session messages.
        var messages = ctx.ConversationMessages;
        if (messages is null || messages.Count == 0)
        {
            ctx.WriteMarkup("[grey]No messages in the current session to replay.[/]");
            ctx.WriteMarkup("[grey]Install the Thinkback plugin by adding scripts to:[/]");
            ctx.WriteMarkup($"[grey]  ~/.claude/plugins/thinkback/[/]");
            return true;
        }

        ctx.WriteMarkup("[bold yellow]Thinkback Replay[/]");
        ctx.Write(string.Empty);

        var displayed = 0;
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();

            var roleLabel = string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "[green]Claude[/]"
                : "[blue]You[/]";

            var text = ExtractText(msg.Content);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var display = text.Length > 120 ? string.Concat(text.AsSpan(0, 120), "...") : text;
            ctx.WriteMarkup($"{roleLabel}: {display.EscapeMarkup()}");
            displayed++;

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        ctx.Write(string.Empty);
        ctx.WriteMarkup($"[grey]Replay complete — {displayed} message(s) shown.[/]");
        return true;
    }

    /// <summary>
    /// Locates the entry-point script inside <paramref name="pluginDir"/> (via
    /// <c>manifest.json</c> or by scanning for .sh/.ps1/.bat files) and spawns it.
    /// </summary>
    private static async Task RunThinkbackPluginAsync(
        string pluginDir,
        CommandContext ctx,
        CancellationToken ct)
    {
        // Resolve entry point: prefer manifest.json, fall back to first script found.
        string? entryPoint = null;
        var manifestPath = Path.Combine(pluginDir, "manifest.json");

        if (File.Exists(manifestPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("entryPoint", out var ep))
                    entryPoint = ep.GetString();
            }
            catch { /* fall through to script discovery */ }
        }

        if (entryPoint is null)
        {
            entryPoint = Directory.GetFiles(pluginDir)
                .Select(Path.GetFileName)
                .FirstOrDefault(f => f is not null &&
                    (f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)));
        }

        if (entryPoint is null)
        {
            ctx.WriteMarkup("[yellow]Thinkback plugin found but no runnable entry point detected.[/]");
            ctx.WriteMarkup("[grey]Add an 'entryPoint' key to manifest.json, or add a .sh/.ps1 script.[/]");
            return;
        }

        var scriptPath = Path.Combine(pluginDir, entryPoint);
        if (!File.Exists(scriptPath))
        {
            ctx.WriteMarkup($"[red]Entry point not found: {scriptPath.EscapeMarkup()}[/]");
            return;
        }

        ProcessStartInfo psi;
        if (entryPoint.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = pluginDir,
            };
        }
        else if (entryPoint.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = pluginDir,
            };
        }
        else
        {
            // .sh or unknown — run via bash
            psi = new ProcessStartInfo("bash", $"\"{scriptPath}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = pluginDir,
            };
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[red]Failed to start the Thinkback plugin process.[/]");
                return;
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Thinkback plugin error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Extracts a plain-text string from a <see cref="JsonElement"/> that represents
    /// a message content value (either a raw string or an array of content blocks).
    /// </summary>
    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// Outputs a non-interactive usage report in Markdown format showing token
/// consumption, cost, and context window percentage. Suitable for scripting
/// and non-interactive session output. Equivalent to the TS <c>usageReport</c> command.
/// </summary>
public sealed class UsageReportCommand : SlashCommand
{
    private const int ContextWindowTokens = 200_000;

    /// <inheritdoc/>
    public override string Name => "/usage-report";

    /// <inheritdoc/>
    public override string[] Aliases => ["/extraUsageNonInteractive"];

    /// <inheritdoc/>
    public override string Description => "Output a non-interactive usage report in Markdown format";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var sb = new StringBuilder();
        sb.AppendLine("## Usage Report");
        sb.AppendLine();

        if (ctx.CurrentModel is not null)
            sb.AppendLine($"**Model:** {ctx.CurrentModel}");

        if (ctx.CostTracker is { } tracker)
        {
            var totalIn   = tracker.TotalInputTokens;
            var totalOut  = tracker.TotalOutputTokens;
            var windowPct = ContextWindowTokens > 0
                ? (double)totalIn / ContextWindowTokens * 100.0
                : 0.0;

            sb.AppendLine($"**Input tokens:** {totalIn:N0}");
            sb.AppendLine($"**Output tokens:** {totalOut:N0}");
            sb.AppendLine($"**Total tokens:** {(totalIn + totalOut):N0}");
            sb.AppendLine($"**Estimated cost:** ${tracker.TotalCostUsd:F4}");
            sb.AppendLine($"**Context window:** {ContextWindowTokens:N0} tokens");
            sb.AppendLine($"**Context used:** {windowPct:F1}%");

            var usage = tracker.GetModelUsage();
            if (usage.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("### Per-model breakdown");
                sb.AppendLine();
                sb.AppendLine("| Model | Input | Output | Cost |");
                sb.AppendLine("|-------|-------|--------|------|");
                foreach (var (model, u) in usage.OrderByDescending(x => x.Value.InputTokens + x.Value.OutputTokens))
                    sb.AppendLine($"| {model} | {u.InputTokens:N0} | {u.OutputTokens:N0} | ${u.CostUsd:F4} |");
            }
        }
        else
        {
            sb.AppendLine($"**Messages:** {ctx.ConversationMessageCount:N0}");
            sb.AppendLine("*Detailed token usage not available in this session.*");
        }

        ctx.Write(sb.ToString());
        return Task.FromResult(true);
    }
}

/// <summary>
/// Detailed context window usage report in Markdown format.
/// Non-interactive variant of <c>/context</c> — suitable for scripting and
/// piped output. Equivalent to the TS <c>contextNonInteractive</c> command.
/// </summary>
public sealed class ContextNonInteractiveCommand : SlashCommand
{
    private const int ContextWindowTokens = 200_000;

    /// <inheritdoc/>
    public override string Name => "/context-report";

    /// <inheritdoc/>
    public override string[] Aliases => ["/contextNonInteractive"];

    /// <inheritdoc/>
    public override string Description => "Detailed context window usage in Markdown (non-interactive)";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var sb = new StringBuilder();
        sb.AppendLine("## Context Usage");
        sb.AppendLine();

        if (ctx.CurrentModel is not null)
            sb.AppendLine($"**Model:** {ctx.CurrentModel}");

        int estimatedTokens;
        if (ctx.CostTracker is { } tracker)
        {
            estimatedTokens = tracker.GetModelUsage()
                .Values
                .Sum(u => u.InputTokens + u.OutputTokens);
        }
        else
        {
            // chars/4 heuristic
            estimatedTokens = ctx.ConversationMessageCount * 800 / 4;
        }

        var windowPct = ContextWindowTokens > 0
            ? (double)estimatedTokens / ContextWindowTokens * 100.0
            : 0.0;

        sb.AppendLine($"**Tokens:** {estimatedTokens:N0} / {ContextWindowTokens:N0} ({windowPct:F1}%)");
        sb.AppendLine($"**Messages:** {ctx.ConversationMessageCount:N0}");
        sb.AppendLine();

        // Estimated category breakdown (heuristic splits).
        sb.AppendLine("### Estimated usage by category");
        sb.AppendLine();
        sb.AppendLine("| Category | Tokens | % of window |");
        sb.AppendLine("|----------|--------|-------------|");

        var systemTokens = (int)(estimatedTokens * 0.10);
        var toolTokens   = (int)(estimatedTokens * 0.05);
        var msgTokens    = estimatedTokens - systemTokens - toolTokens;
        var freeTokens   = Math.Max(0, ContextWindowTokens - estimatedTokens);

        double Pct(int t) => ContextWindowTokens > 0 ? t * 100.0 / ContextWindowTokens : 0;

        sb.AppendLine($"| System prompt | {systemTokens:N0} | {Pct(systemTokens):F1}% |");
        sb.AppendLine($"| Tools         | {toolTokens:N0} | {Pct(toolTokens):F1}% |");
        sb.AppendLine($"| Messages      | {msgTokens:N0} | {Pct(msgTokens):F1}% |");
        sb.AppendLine($"| Free space    | {freeTokens:N0} | {Pct(freeTokens):F1}% |");

        if (ctx.CostTracker is not null)
        {
            sb.AppendLine();
            sb.AppendLine("### Cost");
            sb.AppendLine($"**Total:** ${ctx.CostTracker.TotalCostUsd:F4}");
        }

        ctx.Write(sb.ToString());
        return Task.FromResult(true);
    }
}

/// <summary>
/// Shows current installation status of the Claude Code CLI binary and
/// guides the user through updating to a newer version.
/// Mirrors the TS <c>install</c> command (a CLI subcommand in the original,
/// exposed as a slash command in the C# port).
/// </summary>
public sealed class InstallCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/install";

    /// <inheritdoc/>
    public override string Description => "Show installation status and update guidance";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var version = ctx.Version ?? "unknown";
        var exePath = Environment.ProcessPath ?? "(unknown)";
        var onPath  = IsOnPath();

        ctx.WriteMarkup("[bold]Claude Code Installation[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup($"[grey]Version:[/]      [yellow]{version.EscapeMarkup()}[/]");
        ctx.WriteMarkup($"[grey]Executable:[/]   [grey]{exePath.EscapeMarkup()}[/]");
        ctx.WriteMarkup(onPath
            ? "[grey]PATH status:[/]  [green]claude is on PATH[/]"
            : "[grey]PATH status:[/]  [yellow]claude is not found on PATH[/]");
        ctx.Write(string.Empty);

        ctx.WriteMarkup("[grey]To update to the latest version:[/]");
        if (OperatingSystem.IsWindows())
        {
            ctx.WriteMarkup("[grey]  Download the latest release from Claude Code releases[/]");
            ctx.WriteMarkup("[grey]  and replace the executable at the path shown above.[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]  curl -fsSL https://claude.ai/install.sh | sh[/]");
        }

        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Release notes: [link=https://github.com/anthropics/claude-code/releases]https://github.com/anthropics/claude-code/releases[/link][/]");
        return Task.FromResult(true);
    }

    private static bool IsOnPath()
    {
        try
        {
            var binaryName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
            return pathDirs.Any(dir => File.Exists(Path.Combine(dir, binaryName)));
        }
        catch { return false; }
    }
}

/// <summary>
/// Enables, disables, or shows the status of coordinator multi-agent orchestration mode.
/// When enabled, the coordinator system prompt is appended to every turn and the first
/// user message is wrapped with coordinator context.
/// </summary>
public sealed class CoordinatorCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/coordinator";

    /// <inheritdoc/>
    public override string Description => "Manage coordinator mode: /coordinator on|off|status";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var subcommand = ctx.Args.Length > 0 ? ctx.Args[0].ToLowerInvariant() : "status";

        switch (subcommand)
        {
            case "on":
                Environment.SetEnvironmentVariable("CLAUDE_CODE_COORDINATOR_MODE", "true");
                ctx.WriteMarkup("[green]Coordinator mode enabled.[/] The coordinator system prompt will be active on the next session.");
                break;

            case "off":
                Environment.SetEnvironmentVariable("CLAUDE_CODE_COORDINATOR_MODE", null);
                ctx.WriteMarkup("[grey]Coordinator mode disabled.[/]");
                break;

            case "status":
            default:
                var isActive = ClaudeCode.Services.Coordinator.CoordinatorMode.IsEnabled;
                ctx.WriteMarkup(isActive
                    ? "[green]Coordinator mode is ON.[/] Sub-agent orchestration system prompt is active."
                    : "[grey]Coordinator mode is OFF.[/] Run [blue]/coordinator on[/] or set CLAUDE_CODE_COORDINATOR_MODE=true to enable.");
                break;
        }

        return Task.FromResult(true);
    }
}

// =============================================================================
// New commands: /summary, /ultraplan, /issue, /onboarding
// =============================================================================

/// <summary>
/// Generates an AI-powered summary of the current conversation by making a one-shot
/// API call with a summarisation prompt fed the last N conversation messages.
/// Displays the result in a Spectre.Console panel and persists the session via
/// <see cref="SessionStore"/>.
/// </summary>
public sealed class SummaryCommand : SlashCommand
{
    private const int MaxContextMessages = 40;
    private const int SummaryMaxTokens = 512;

    /// <inheritdoc/>
    public override string Name => "/summary";

    /// <inheritdoc/>
    public override string Description => "Generate an AI summary of the current conversation";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var messages = ctx.ConversationMessages;
        if (messages is null || messages.Count == 0)
        {
            ctx.WriteMarkup("[grey]No conversation to summarise yet.[/]");
            return true;
        }

        if (ctx.AnthropicClient is null)
        {
            ctx.WriteMarkup("[yellow]API client not available for summary generation.[/]");
            return true;
        }

        // Take the last N messages so the request stays within a reasonable context.
        var recentMessages = messages.Count > MaxContextMessages
            ? messages.Skip(messages.Count - MaxContextMessages).ToList()
            : messages.ToList();

        // Append the summarisation instruction as a final user message.
        recentMessages.Add(new ClaudeCode.Services.Api.MessageParam
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement("Summarize this conversation in 3-5 sentences."),
        });

        var model = ctx.CurrentModel ?? ClaudeCode.Services.Api.ModelResolver.Resolve();
        ctx.WriteMarkup("[grey]Generating summary...[/]");

        var textBuilder = new StringBuilder();
        try
        {
            var request = new ClaudeCode.Services.Api.MessageRequest
            {
                Model = model,
                Messages = recentMessages,
                MaxTokens = SummaryMaxTokens,
            };

            await foreach (var evt in ctx.AnthropicClient.StreamMessageAsync(request, ct).ConfigureAwait(false))
            {
                if (evt.EventType != "content_block_delta")
                    continue;

                var textChunk = ExtractTextDelta(evt.Data);
                if (textChunk is not null)
                    textBuilder.Append(textChunk);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Summary generation failed:[/] {ex.Message.EscapeMarkup()}");
            return true;
        }

        var summaryText = textBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(summaryText))
        {
            ctx.WriteMarkup("[yellow]No summary text returned.[/]");
            return true;
        }

        // Display in a rounded panel.
        var panel = new Panel(summaryText.EscapeMarkup())
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Header("[blue]Session Summary[/]");
        AnsiConsole.Write(panel);

        // Persist the current session state to the store (best-effort).
        if (ctx.SessionId is not null)
        {
            try
            {
                var store = new SessionStore();
                await store.SaveAsync(
                    ctx.SessionId,
                    messages.ToList(),
                    model,
                    ctx.Cwd,
                    ctx.CostTracker?.TotalCostUsd ?? 0,
                    ct: ct).ConfigureAwait(false);
            }
            catch
            {
                // Save is best-effort — never fail the summary display.
            }
        }

        return true;
    }

    /// <summary>
    /// Attempts to extract a text delta string from a raw SSE <c>content_block_delta</c>
    /// event data payload. Returns <see langword="null"/> when the payload is not a
    /// <c>text_delta</c> or cannot be parsed.
    /// </summary>
    private static string? ExtractTextDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("type", out var typeEl)
                && typeEl.GetString() == "text_delta"
                && delta.TryGetProperty("text", out var textEl))
            {
                return textEl.GetString();
            }
        }
        catch { /* malformed payload — safe to skip */ }
        return null;
    }
}

/// <summary>
/// Toggles "ultraplan mode" for the current session. When active, a structured planning
/// instruction is appended to the system prompt on <em>every</em> turn, causing the model
/// to output a goal analysis, step-by-step approach, risk assessment, and success criteria
/// before responding to any request.
/// </summary>
/// <remarks>
/// Active state is stored in <see cref="ClaudeCode.Core.State.ReplModeFlags.UltraplanActive"/>
/// so that <see cref="ClaudeCode.Services.Engine.QueryEngine"/> can inject the prompt
/// without a circular project dependency. <see cref="IsActive"/> and
/// <see cref="GetSystemPromptAddition"/> are exposed as static helpers for callers that
/// need to inspect the active state at runtime.
/// </remarks>
public sealed class UltraPlanCommand : SlashCommand
{
    /// <summary>Gets whether ultraplan mode is currently active.</summary>
    public static bool IsActive => ClaudeCode.Core.State.ReplModeFlags.UltraplanActive;

    /// <summary>
    /// Returns the structured-planning system prompt text when ultraplan mode is active,
    /// or <see langword="null"/> when inactive.
    /// </summary>
    public static string? GetSystemPromptAddition() =>
        ClaudeCode.Core.State.ReplModeFlags.UltraplanActive
            ? ClaudeCode.Core.State.ReplModeFlags.UltraplanSystemPrompt
            : null;

    /// <inheritdoc/>
    public override string Name => "/ultraplan";

    /// <inheritdoc/>
    public override string[] Aliases => ["/ultraplan-toggle"];

    /// <inheritdoc/>
    public override string Description => "Toggle ULTRAPLAN mode — prepend a structured planning prompt to every turn";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ClaudeCode.Core.State.ReplModeFlags.UltraplanActive =
            !ClaudeCode.Core.State.ReplModeFlags.UltraplanActive;

        if (ClaudeCode.Core.State.ReplModeFlags.UltraplanActive)
        {
            ctx.WriteMarkup("[green]ULTRAPLAN mode enabled.[/]");
            ctx.WriteMarkup("[grey]Every turn will be prefixed with a structured planning instruction:[/]");
            ctx.WriteMarkup("[grey]  1. Goal analysis  2. Step-by-step approach  3. Risks  4. Success criteria[/]");
        }
        else
        {
            ctx.WriteMarkup("[grey]ULTRAPLAN mode disabled.[/]");
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// Creates a GitHub issue pre-populated with the current conversation context.
/// Requires the <c>gh</c> CLI to be installed and authenticated.
/// After creation, opens the issue URL in the default browser.
/// </summary>
public sealed class IssueCommand : SlashCommand
{
    private const int MaxBodyChars = 2000;

    /// <inheritdoc/>
    public override string Name => "/issue";

    /// <inheritdoc/>
    public override string Description => "Create a GitHub issue from the current conversation (requires gh CLI)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Verify gh CLI is available.
        if (!IsGhAvailable())
        {
            ctx.WriteMarkup("[yellow]gh CLI not found.[/] Install from https://cli.github.com/ and run [blue]gh auth login[/].");
            return true;
        }

        // Parse explicit --title and --body flags from args.
        var (parsedTitle, parsedBody) = ParseIssueArgs(ctx.Args);

        // When no args are provided, derive title and body from the last assistant response;
        // fall back to conversation history when the response is absent.
        string title;
        string body;
        if (parsedTitle is null && parsedBody is null && !string.IsNullOrEmpty(ctx.LastAssistantResponse))
        {
            body  = BuildBodyFromText(ctx.LastAssistantResponse);
            title = BuildTitleFromText(ctx.LastAssistantResponse);
        }
        else
        {
            title = parsedTitle ?? BuildIssueTitle(ctx);
            body  = parsedBody  ?? BuildIssueBody(ctx);
        }

        ctx.WriteMarkup("[grey]Creating GitHub issue...[/]");

        try
        {
            var psi = new ProcessStartInfo("gh", $"issue create --title \"{EscapeArg(title)}\" --body \"{EscapeArg(body)}\"")
            {
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                ctx.WriteMarkup("[red]Failed to start gh process.[/]");
                return true;
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            if (proc.ExitCode != 0)
            {
                ctx.WriteMarkup($"[red]gh issue create failed (exit {proc.ExitCode}):[/]");
                if (!string.IsNullOrEmpty(stderr))
                    ctx.Write(stderr);
                return true;
            }

            // stdout contains the issue URL.
            var url = stdout;
            ctx.WriteMarkup($"[green]Issue created:[/] [blue underline]{url.EscapeMarkup()}[/]");

            // Open in default browser.
            if (!string.IsNullOrEmpty(url))
                OpenUrl(url);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Error creating issue:[/] {ex.Message.EscapeMarkup()}");
        }

        return true;
    }

    private static string BuildIssueTitle(CommandContext ctx)
    {
        if (ctx.ConversationMessages is { Count: > 0 } msgs)
        {
            foreach (var msg in msgs)
            {
                if (!string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
                var text = ExtractIssueText(msg.Content);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var first = text.Split('\n')[0].Trim();
                return first.Length > 80 ? first[..80] : first;
            }
        }

        return $"Issue from ClaudeCode session — {DateTime.UtcNow:yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// Parses <c>--title</c> and <c>--body</c> flag values from the raw args array.
    /// Returns <see langword="null"/> for each flag that was not present.
    /// </summary>
    private static (string? Title, string? Body) ParseIssueArgs(string[] args)
    {
        string? title = null;
        string? body = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--title", StringComparison.OrdinalIgnoreCase))
                title = args[i + 1];
            else if (args[i].Equals("--body", StringComparison.OrdinalIgnoreCase))
                body = args[i + 1];
        }
        return (title, body);
    }

    /// <summary>Truncates <paramref name="text"/> to a single-line title of at most 80 chars,
    /// using the first sentence (up to the first period) as the basis.</summary>
    private static string BuildTitleFromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.Split('\n')[0].Trim();
        var dotIdx = firstLine.IndexOf('.');
        var sentence = dotIdx > 0 ? firstLine[..(dotIdx + 1)] : firstLine;
        return sentence.Length > 80 ? sentence[..80] : sentence;
    }

    /// <summary>Truncates <paramref name="text"/> to <see cref="MaxBodyChars"/> characters.</summary>
    private static string BuildBodyFromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > MaxBodyChars ? text[..MaxBodyChars] + "\n\n_(truncated)_" : text;
    }

    private static string BuildIssueBody(CommandContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Conversation context");
        sb.AppendLine();

        if (ctx.ConversationMessages is { Count: > 0 } msgs)
        {
            foreach (var msg in msgs)
            {
                var role = string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "**Claude**"
                    : "**User**";
                var text = ExtractIssueText(msg.Content);
                if (string.IsNullOrWhiteSpace(text)) continue;
                sb.AppendLine($"{role}: {text.Trim()}");
                sb.AppendLine();

                if (sb.Length >= MaxBodyChars) break;
            }
        }

        var result = sb.ToString();
        return result.Length > MaxBodyChars ? result[..MaxBodyChars] + "\n\n_(truncated)_" : result;
    }

    private static string ExtractIssueText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
        }

        return string.Empty;
    }

    private static bool IsGhAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("gh", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { /* best-effort — browser open failure is non-fatal */ }
    }

    /// <summary>Escapes double-quotes for shell argument embedding.</summary>
    private static string EscapeArg(string s) => s.Replace("\"", "\\\"");
}

/// <summary>
/// Interactive first-run setup wizard. Checks the API key, prints a welcome
/// message with version info and a list of key commands, then writes
/// <c>~/.claude/onboarding-complete</c> to prevent repeated prompts.
/// </summary>
public sealed class OnboardingCommand : SlashCommand
{
    private static string OnboardingMarkerPath =>
        Path.Combine(ConfigPaths.ClaudeHomeDir, "onboarding-complete");

    /// <inheritdoc/>
    public override string Name => "/onboarding";

    /// <inheritdoc/>
    public override string Description => "Run the interactive first-run setup wizard";

    /// <inheritdoc/>
    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        AnsiConsole.Write(new Rule("[blue]ClaudeCode — First-Run Setup[/]").RuleStyle("grey"));
        ctx.Write(string.Empty);

        // 1. Version info
        var version = ctx.Version ?? "unknown";
        ctx.WriteMarkup($"[grey]Version:[/] [white]{version.EscapeMarkup()}[/]");
        ctx.Write(string.Empty);

        // 2. API key check
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            ctx.WriteMarkup("[yellow]ANTHROPIC_API_KEY is not set.[/]");
            ctx.WriteMarkup("[grey]Set it with:[/]");
            ctx.WriteMarkup("[grey]  export ANTHROPIC_API_KEY=sk-ant-...[/]");

            var confirmed = AnsiConsole.Confirm("[grey]Continue setup without an API key?[/]", defaultValue: false);
            if (!confirmed)
            {
                ctx.WriteMarkup("[grey]Setup cancelled. Set ANTHROPIC_API_KEY and run /onboarding again.[/]");
                return Task.FromResult(true);
            }
        }
        else
        {
            var masked = apiKey.Length > 8
                ? apiKey[..4] + new string('*', apiKey.Length - 8) + apiKey[^4..]
                : "****";
            ctx.WriteMarkup($"[green]API key found:[/] [grey]{masked}[/]");
        }

        ctx.Write(string.Empty);

        // 3. Key commands overview
        ctx.WriteMarkup("[grey]Key commands to get started:[/]");

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("Command");
        table.AddColumn("What it does");

        table.AddRow("/help",     "List all available commands");
        table.AddRow("/model",    "Switch the active model");
        table.AddRow("/init",     "Initialise .claude/ in the current directory");
        table.AddRow("/mcp list", "List configured MCP servers");
        table.AddRow("/rewind",   "Undo a file edit (restore from snapshot)");
        table.AddRow("/summary",  "Summarise the current conversation");
        table.AddRow("/exit",     "End the session");

        AnsiConsole.Write(table);
        ctx.Write(string.Empty);

        // 4. Working directory
        ctx.WriteMarkup($"[grey]Working directory:[/] [white]{ctx.Cwd.EscapeMarkup()}[/]");
        ctx.Write(string.Empty);

        // 5. Mark onboarding complete
        try
        {
            Directory.CreateDirectory(ConfigPaths.ClaudeHomeDir);
            File.WriteAllText(OnboardingMarkerPath, DateTime.UtcNow.ToString("O"));
            ctx.WriteMarkup("[green]Setup complete.[/] [grey]This message will not appear again.[/]");
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[yellow]Could not write onboarding marker:[/] {ex.Message.EscapeMarkup()}");
        }

        return Task.FromResult(true);
    }
}
/// <summary>
/// Guides the user through connecting their GitHub account to Claude Code on the web
/// (cloud remote sessions). Collects a GitHub Personal Access Token, validates it via
/// the CCR backend, and optionally opens the browser to claude.ai/code.
/// </summary>
public sealed class RemoteSetupCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/remote-setup";

    /// <inheritdoc/>
    public override string Description => "Connect your GitHub account to Claude Code (web/cloud setup)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        AnsiConsole.Write(new Rule("[blue]Claude Code \u2014 Remote / Web Setup[/]").RuleStyle("grey"));
        ctx.Write(string.Empty);

        ctx.WriteMarkup("[grey]This wizard connects your GitHub account so Claude Code can run in the cloud.[/]");
        ctx.WriteMarkup("[grey]You need a GitHub Personal Access Token (classic) with the [white]repo[/] scope.[/]");
        ctx.Write(string.Empty);

        var accessToken = Environment.GetEnvironmentVariable("CLAUDE_CLAUDE_AI_ACCESS_TOKEN")
                       ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            ctx.WriteMarkup("[yellow]You are not signed in to Claude.[/]");
            ctx.WriteMarkup("[grey]Run [blue]/login[/] first, then re-run /remote-setup.[/]");
            return true;
        }

        ctx.WriteMarkup("[grey](Create a PAT at [blue]https://github.com/settings/tokens[/])[/]");
        ctx.Write(string.Empty);

        var pat = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey]GitHub PAT:[/]")
                .PromptStyle("white")
                .Secret('*'));

        if (string.IsNullOrWhiteSpace(pat))
        {
            ctx.WriteMarkup("[yellow]No token entered \u2014 setup cancelled.[/]");
            return true;
        }

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Validating GitHub token\u2026", async _ =>
                    await ImportGithubTokenAsync(accessToken, pat, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            ctx.WriteMarkup("[red]Authentication failed.[/] [grey]Run /login again.[/]");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            ctx.WriteMarkup("[red]Invalid GitHub token.[/] [grey]Ensure it has the [white]repo[/] scope.[/]");
            return true;
        }
        catch (OperationCanceledException) { ctx.WriteMarkup("[yellow]Request cancelled.[/]"); return true; }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Backend unreachable:[/] {ex.Message.EscapeMarkup()}");
            return true;
        }

        ctx.WriteMarkup("[green]GitHub account connected successfully.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Open [blue]https://claude.ai/code[/] to start a remote session.[/]");

        if (AnsiConsole.Confirm("[grey]Open the browser now?[/]", defaultValue: true))
        {
            try { Process.Start(new ProcessStartInfo("https://claude.ai/code") { UseShellExecute = true }); }
            catch { ctx.WriteMarkup("[grey]Navigate to [blue]https://claude.ai/code[/] manually.[/]"); }
        }

        return true;
    }

    private static async Task ImportGithubTokenAsync(string accessToken, string githubPat, CancellationToken ct)
    {
        const string ImportUrl = "https://api.anthropic.com/v1/code/github/import-token";
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        http.DefaultRequestHeaders.Add("anthropic-beta", "ccr-byoc-2025-07-29");
        var body = JsonSerializer.Serialize(new { token = githubPat });
        using var content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json");
        (await http.PostAsync(ImportUrl, content, ct).ConfigureAwait(false)).EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Examines failing CI checks on the current pull request via <c>gh pr checks</c>
/// and emits a structured prompt for Claude to fix each failure.
/// </summary>
public sealed class AutofixPrCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/autofix-pr";

    /// <inheritdoc/>
    public override string Description => "Analyze failing PR checks and generate a Claude fix prompt";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        AnsiConsole.Write(new Rule("[blue]Auto-Fix PR Checks[/]").RuleStyle("grey"));
        ctx.Write(string.Empty);

        if (!IsToolOnPath("gh"))
        {
            ctx.WriteMarkup("[yellow]GitHub CLI ([blue]gh[/]) is not installed or not on PATH.[/]");
            ctx.WriteMarkup("[grey]Install from [blue]https://cli.github.com[/] and run [blue]gh auth login[/].[/]");
            return true;
        }

        string prNumber;
        try
        {
            prNumber = (await RunGhAsync(
                ["pr", "view", "--json", "number", "-q", ".number"], ctx.Cwd, ct)
                .ConfigureAwait(false)).Trim();
        }
        catch
        {
            ctx.WriteMarkup("[yellow]No pull request found for the current branch.[/]");
            ctx.WriteMarkup("[grey]Create one with [blue]gh pr create[/] first.[/]");
            return true;
        }

        if (string.IsNullOrWhiteSpace(prNumber))
        {
            ctx.WriteMarkup("[yellow]Could not detect the current PR number.[/]");
            return true;
        }

        ctx.WriteMarkup($"[grey]Fetching checks for PR [white]#{prNumber.EscapeMarkup()}[/]\u2026[/]");

        string checksJson;
        try
        {
            checksJson = await RunGhAsync(
                ["pr", "checks", prNumber, "--json", "name,state,conclusion,detailsUrl,description"],
                ctx.Cwd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Failed to fetch PR checks:[/] {ex.Message.EscapeMarkup()}");
            return true;
        }

        List<PrCheck> allChecks;
        try
        {
            allChecks = JsonSerializer.Deserialize<List<PrCheck>>(checksJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            ctx.WriteMarkup("[red]Could not parse check results.[/]");
            return true;
        }

        var failing = allChecks
            .Where(c => c.Conclusion is "failure" or "timed_out" or "cancelled"
                     || c.State    is "FAILURE"   or "ERROR")
            .ToList();

        if (failing.Count == 0)
        {
            ctx.WriteMarkup("[green]All checks are passing![/] [grey]Nothing to fix.[/]");
            return true;
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Red);
        table.AddColumn("Check");
        table.AddColumn("Status");
        table.AddColumn("Details");

        foreach (var check in failing)
        {
            var status = (check.Conclusion ?? check.State ?? "failed").EscapeMarkup();
            var detail = string.IsNullOrWhiteSpace(check.Description)
                ? "-" : check.Description.EscapeMarkup();
            table.AddRow(check.Name.EscapeMarkup(), $"[red]{status}[/]", detail);
        }

        AnsiConsole.Write(table);
        ctx.Write(string.Empty);

        var failureList = string.Join(", ", failing.Select(f => f.Name));
        var sb = new StringBuilder();
        sb.AppendLine($"The following CI checks are failing on PR #{prNumber}: {failureList}.");
        sb.AppendLine();
        sb.AppendLine("Please:");
        sb.AppendLine("1. Investigate the root cause of each failure");
        sb.AppendLine("2. Fix the code so all failing checks pass");
        sb.AppendLine("3. Do NOT change unrelated code");
        sb.AppendLine("4. Run any relevant tests locally to verify the fix");

        foreach (var check in failing.Take(3).Where(c => !string.IsNullOrWhiteSpace(c.DetailsUrl)))
        {
            sb.AppendLine();
            sb.AppendLine($"Check '{check.Name}' details: {check.DetailsUrl}");
        }

        var prompt = sb.ToString().TrimEnd();

        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Generated fix prompt:[/]");
        ctx.Write(string.Empty);
        AnsiConsole.Write(new Panel(prompt).Header("[grey]Fix Prompt[/]").BorderColor(Color.Grey));

        if (ctx.SubmitTurn is not null)
            await ctx.SubmitTurn(prompt, ct).ConfigureAwait(false);
        else
            AnsiConsole.Write(new Panel(prompt).Header("[grey]Fix Prompt — paste this into your next message[/]").BorderColor(Color.Grey));

        return true;
    }

    private static bool IsToolOnPath(string tool)
    {
        try
        {
            var which = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            using var proc = Process.Start(new ProcessStartInfo(which, tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(500);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string> RunGhAsync(string[] args, string cwd, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("gh")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"gh exited {proc.ExitCode}: {err.Trim()}");
        }
        return stdout;
    }

    private sealed record PrCheck(
        string Name,
        string? State,
        string? Conclusion,
        string? DetailsUrl,
        string? Description);
}

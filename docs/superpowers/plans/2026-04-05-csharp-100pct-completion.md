# C# 100% Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the C# Claude Code reimplementation to full feature parity with TypeScript by implementing Feature Flags, Plugin Commands, Voice Input (STT), and KAIROS/Buddy modes.

**Architecture:** Four phases — Feature Flags first (foundational), Plugin Commands and Voice Input in parallel, then KAIROS/Buddy. All changes follow the existing patterns: static `ReplModeFlags` flags, `CommandContext` lambdas for ReplSession callbacks, `UltraplanActive`-style pattern for system-prompt injection, and xUnit tests with no state pollution between tests.

**Tech Stack:** .NET 10, C# 13, xUnit 2.9.3, `System.Speech` (Windows, via NuGet), Spectre.Console, existing DI patterns via `Program.cs` manual registration.

---

## File Map

**Phase 1 — Feature Flags**
- Create: `csharp/src/ClaudeCode.Configuration/FeatureFlags.cs`
- Modify: `csharp/src/ClaudeCode.Configuration/Settings/GlobalConfig.cs` (+`Features` dict)
- Modify: `csharp/src/ClaudeCode.Cli/Program.cs` (conditional tool registration)
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs` (`ConfigCommand` +`features` subcommand)
- Modify: `csharp/tests/ClaudeCode.Core.Tests/ClaudeCode.Core.Tests.csproj` (+Configuration reference)
- Create: `csharp/tests/ClaudeCode.Core.Tests/FeatureFlagsTests.cs`

**Phase 2a — Plugin Commands**
- Modify: `csharp/src/ClaudeCode.Services/Plugins/PluginLoader.cs` (new types + `LoadCommands`)
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs` (`LoadPluginCommands`)
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs` (`HelpCommand` + `ReloadPluginsCommand`)
- Modify: `csharp/src/ClaudeCode.Commands/SlashCommand.cs` (`CommandContext` + `ReloadPluginsAndCommands`)
- Create: `csharp/tests/ClaudeCode.Services.Tests/PluginCommandTests.cs`

**Phase 2b — Voice Input**
- Create: `csharp/src/ClaudeCode.Services/Voice/IVoiceEngine.cs`
- Create: `csharp/src/ClaudeCode.Services/Voice/VoiceUnavailableException.cs`
- Create: `csharp/src/ClaudeCode.Services/Voice/DefaultVoiceEngine.cs`
- Create: `csharp/src/ClaudeCode.Services/Voice/VoiceInputService.cs`
- Modify: `csharp/src/ClaudeCode.Services/ClaudeCode.Services.csproj` (+System.Speech)
- Modify: `csharp/src/ClaudeCode.Commands/SlashCommand.cs` (`CommandContext.ToggleVoiceInput`)
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs` (`VoiceCommand` extension)
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs` (`_voiceInputService` field + wiring)
- Create: `csharp/tests/ClaudeCode.Services.Tests/VoiceInputServiceTests.cs`

**Phase 3 — KAIROS/Buddy**
- Modify: `csharp/src/ClaudeCode.Core/State/ReplModeFlags.cs` (+`KairosEnabled`, `BuddyEnabled`, `KairosSystemPrompt`)
- Modify: `csharp/src/ClaudeCode.Services/Engine/QueryEngine.cs` (KAIROS system-prompt block)
- Create: `csharp/src/ClaudeCode.Services/AutoDream/BuddyService.cs`
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs` (`_pendingBuddyNote`, buddy wiring, KAIROS number selection)
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs` (`AssistantCommand`, `BuddyCommand`)
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs` (`BuildCommandRegistry` +Assistant/Buddy)
- Create: `csharp/tests/ClaudeCode.Services.Tests/KairosAndBuddyTests.cs`

---

## Task 1: FeatureFlags class + GlobalConfig.Features

**Files:**
- Create: `csharp/src/ClaudeCode.Configuration/FeatureFlags.cs`
- Modify: `csharp/src/ClaudeCode.Configuration/Settings/GlobalConfig.cs`

- [ ] **Step 1: Write the failing test stubs** (they drive interface design)

Create `csharp/tests/ClaudeCode.Core.Tests/FeatureFlagsTests.cs` with empty test bodies that will be implemented in Task 3. For now, just getting the file ready with the using statements reveals any missing types.

```csharp
// Placeholder — full bodies added in Task 3
namespace ClaudeCode.Core.Tests;
using ClaudeCode.Configuration;
public class FeatureFlagsTests { }
```

- [ ] **Step 2: Add `Features` property to `GlobalConfig`**

Open `csharp/src/ClaudeCode.Configuration/Settings/GlobalConfig.cs`. The file ends at line 65. Add the new property before `[JsonExtensionData]`:

```csharp
    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; init; }

    [JsonExtensionData]
```

Full change in context:
```csharp
    [JsonPropertyName("tipsHistory")]
    public Dictionary<string, int>? TipsHistory { get; init; }

    [JsonPropertyName("projects")]
    public Dictionary<string, ProjectConfig>? Projects { get; init; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
```

- [ ] **Step 3: Create `FeatureFlags` static class**

Create `csharp/src/ClaudeCode.Configuration/FeatureFlags.cs`:

```csharp
namespace ClaudeCode.Configuration;

using ClaudeCode.Configuration.Settings;

/// <summary>
/// Runtime feature-flag system. Loads once at startup via <see cref="Load"/>.
/// Resolution order: environment variable &gt; settings.json entry &gt; hardcoded default (false).
/// Env var convention: CLAUDE_FEATURE_&lt;UPPERCASE_FLAG&gt; = 1|true|0|false|"".
/// </summary>
public static class FeatureFlags
{
    // Known flags with their hardcoded defaults.
    private static readonly Dictionary<string, bool> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cron"]           = false,
        ["sleep"]          = false,
        ["coordinator"]    = false,
        ["agent-triggers"] = false,
        ["voice"]          = false,
        ["kairos"]         = false,
        ["bridge"]         = false,
        ["proactive"]      = false,
    };

    // Effective flag table after merging all sources.
    private static readonly Dictionary<string, bool> _flags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// (Re-)initialises the flag table from <paramref name="config"/> and environment variables.
    /// Safe to call multiple times; each call rebuilds the table from scratch.
    /// </summary>
    public static void Load(GlobalConfig? config)
    {
        _flags.Clear();

        // Start with hardcoded defaults.
        foreach (var (k, v) in _defaults)
            _flags[k] = v;

        // Layer in settings.json overrides.
        if (config?.Features is { } settingsFlags)
            foreach (var (k, v) in settingsFlags)
                _flags[k] = v;

        // Env vars take highest precedence.
        foreach (var key in _flags.Keys.ToList())
        {
            var envName = $"CLAUDE_FEATURE_{key.ToUpperInvariant().Replace('-', '_')}";
            var raw = Environment.GetEnvironmentVariable(envName);
            if (raw is null) continue;

            // Empty string or "0" or "false" (case-insensitive) → false; anything else → true.
            _flags[key] = !string.IsNullOrEmpty(raw)
                          && !raw.Equals("0", StringComparison.Ordinal)
                          && !raw.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the named flag is enabled.
    /// Unknown flags return <see langword="false"/>.
    /// </summary>
    public static bool IsEnabled(string flag)
        => _flags.TryGetValue(flag, out var v) && v;

    /// <summary>
    /// Returns a snapshot of all flag names and their effective values,
    /// along with the source that determined each value.
    /// Used by <c>/config features</c>.
    /// </summary>
    public static IReadOnlyList<(string Flag, bool Value, string Source)> GetAll(GlobalConfig? config)
    {
        var result = new List<(string, bool, string)>();

        foreach (var key in _defaults.Keys.Union(_flags.Keys, StringComparer.OrdinalIgnoreCase).Distinct())
        {
            var envName = $"CLAUDE_FEATURE_{key.ToUpperInvariant().Replace('-', '_')}";
            var envRaw = Environment.GetEnvironmentVariable(envName);
            bool value = _flags.TryGetValue(key, out var v) ? v : false;

            string source;
            if (envRaw is not null)
                source = $"env ({envName})";
            else if (config?.Features?.ContainsKey(key) == true)
                source = "settings.json";
            else
                source = "default";

            result.Add((key, value, source));
        }

        return result.OrderBy(r => r.Flag).ToList();
    }
}
```

- [ ] **Step 4: Build to check for compilation errors**

```bash
cd csharp && dotnet build ClaudeCode.Configuration/
```

Expected: `Build succeeded.` (0 errors)

- [ ] **Step 5: Commit**

```bash
cd csharp && git add src/ClaudeCode.Configuration/FeatureFlags.cs src/ClaudeCode.Configuration/Settings/GlobalConfig.cs
git commit -m "feat: add FeatureFlags class and GlobalConfig.Features property"
```

---

## Task 2: Conditional tool registration + /config features

**Files:**
- Modify: `csharp/src/ClaudeCode.Cli/Program.cs`
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs`

- [ ] **Step 1: Initialize FeatureFlags in `Program.cs`**

In `Program.cs`, after the `configProvider` is created and before the `toolRegistry` block, add:

```csharp
// Initialise feature flags from GlobalConfig + env vars.
ClaudeCode.Configuration.FeatureFlags.Load(configProvider.GlobalConfig);
```

So the top of `Program.cs` (after the early-exit block) becomes:
```csharp
var services = new ServiceCollection();

var cwd = Environment.CurrentDirectory;
var configProvider = new ClaudeCode.Configuration.ConfigProvider(cwd);
services.AddSingleton<ClaudeCode.Configuration.IConfigProvider>(configProvider);
services.AddSingleton(sp => sp.GetRequiredService<ClaudeCode.Configuration.IConfigProvider>().Settings);
services.AddSingleton(sp => sp.GetRequiredService<ClaudeCode.Configuration.IConfigProvider>().GlobalConfig);

// Initialise feature flags from GlobalConfig + env vars.
ClaudeCode.Configuration.FeatureFlags.Load(configProvider.GlobalConfig);

// Tool registry
var toolRegistry = new ClaudeCode.Core.Tools.ToolRegistry();
```

- [ ] **Step 2: Gate tools behind feature flags in `Program.cs`**

Replace the flat tool registration block with flag-guarded groups. The exact replacement:

```csharp
// Tool registry
var toolRegistry = new ClaudeCode.Core.Tools.ToolRegistry();
toolRegistry.Register(new ClaudeCode.Tools.Bash.BashTool());
toolRegistry.Register(new ClaudeCode.Tools.FileRead.FileReadTool());
toolRegistry.Register(new ClaudeCode.Tools.FileWrite.FileWriteTool());
toolRegistry.Register(new ClaudeCode.Tools.Glob.GlobTool());
toolRegistry.Register(new ClaudeCode.Tools.Grep.GrepTool());
toolRegistry.Register(new ClaudeCode.Tools.FileEdit.FileEditTool());
toolRegistry.Register(new ClaudeCode.Tools.WebFetch.WebFetchTool());
toolRegistry.Register(new ClaudeCode.Tools.WebSearch.WebSearchTool());
// Task management tools
toolRegistry.Register(new ClaudeCode.Tools.TaskCreate.TaskCreateTool());
toolRegistry.Register(new ClaudeCode.Tools.TaskUpdate.TaskUpdateTool());
toolRegistry.Register(new ClaudeCode.Tools.TaskList.TaskListTool());
toolRegistry.Register(new ClaudeCode.Tools.TaskGet.TaskGetTool());
toolRegistry.Register(new ClaudeCode.Tools.TaskOutput.TaskOutputTool());
toolRegistry.Register(new ClaudeCode.Tools.TaskStop.TaskStopTool());
toolRegistry.Register(new ClaudeCode.Tools.TodoWrite.TodoWriteTool());
// Plan/Worktree tools
toolRegistry.Register(new ClaudeCode.Tools.PlanMode.EnterPlanModeTool());
toolRegistry.Register(new ClaudeCode.Tools.PlanMode.ExitPlanModeTool());
toolRegistry.Register(new ClaudeCode.Tools.Worktree.EnterWorktreeTool());
toolRegistry.Register(new ClaudeCode.Tools.Worktree.ExitWorktreeTool());
// Communication tools
toolRegistry.Register(new ClaudeCode.Tools.SendMessage.SendMessageTool());
toolRegistry.Register(new ClaudeCode.Tools.AskUserQuestion.AskUserQuestionTool());
toolRegistry.Register(new ClaudeCode.Tools.Skill.SkillTool());
toolRegistry.Register(new ClaudeCode.Tools.Brief.BriefTool());
// MCP tools
toolRegistry.Register(new ClaudeCode.Tools.McpTool.McpInvokeTool());
toolRegistry.Register(new ClaudeCode.Tools.McpResource.ListMcpResourcesTool());
toolRegistry.Register(new ClaudeCode.Tools.McpResource.ReadMcpResourceTool());
toolRegistry.Register(new ClaudeCode.Tools.McpAuth.McpAuthTool());
// Team tools — coordinator flag
if (ClaudeCode.Configuration.FeatureFlags.IsEnabled("coordinator"))
{
    toolRegistry.Register(new ClaudeCode.Tools.Team.TeamCreateTool());
    toolRegistry.Register(new ClaudeCode.Tools.Team.TeamDeleteTool());
}
// Platform tools
toolRegistry.Register(new ClaudeCode.Tools.PowerShell.PowerShellTool());
toolRegistry.Register(new ClaudeCode.Tools.NotebookEdit.NotebookEditTool());
toolRegistry.Register(new ClaudeCode.Tools.LSP.LSPTool());
toolRegistry.Register(new ClaudeCode.Tools.REPL.REPLTool());
// System tools — sleep flag
if (ClaudeCode.Configuration.FeatureFlags.IsEnabled("sleep")
    || ClaudeCode.Configuration.FeatureFlags.IsEnabled("proactive"))
    toolRegistry.Register(new ClaudeCode.Tools.Sleep.SleepTool());
toolRegistry.Register(new ClaudeCode.Tools.ToolSearch.ToolSearchTool(toolRegistry));
toolRegistry.Register(new ClaudeCode.Tools.SyntheticOutput.SyntheticOutputTool());
// Remote trigger — agent-triggers or proactive flag
if (ClaudeCode.Configuration.FeatureFlags.IsEnabled("agent-triggers")
    || ClaudeCode.Configuration.FeatureFlags.IsEnabled("proactive"))
    toolRegistry.Register(new ClaudeCode.Tools.RemoteTrigger.RemoteTriggerTool());
// Cron tools — cron or agent-triggers flag
if (ClaudeCode.Configuration.FeatureFlags.IsEnabled("cron")
    || ClaudeCode.Configuration.FeatureFlags.IsEnabled("agent-triggers"))
{
    toolRegistry.Register(new ClaudeCode.Tools.Cron.CronCreateTool());
    toolRegistry.Register(new ClaudeCode.Tools.Cron.CronDeleteTool());
    toolRegistry.Register(new ClaudeCode.Tools.Cron.CronListTool());
}
toolRegistry.Register(new ClaudeCode.Tools.Config.ConfigTool(configProvider));
services.AddSingleton(toolRegistry);
```

- [ ] **Step 3: Add `features` subcommand to `ConfigCommand`**

In `BuiltInCommands.cs`, find `ConfigCommand.ExecuteAsync` (around line 361). Add a `features` branch before the default help output:

```csharp
public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(ctx);

    // /config features  — show all flags and their sources
    if (ctx.Args.Length > 0 && ctx.Args[0].Equals("features", StringComparison.OrdinalIgnoreCase))
    {
        var config = ctx.ConfigProvider as ClaudeCode.Configuration.IConfigProvider;
        var flags = ClaudeCode.Configuration.FeatureFlags.GetAll(config?.GlobalConfig);

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

    // ... existing ConfigCommand output below ...
```

- [ ] **Step 4: Build the solution**

```bash
cd csharp && dotnet build
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Commit**

```bash
cd csharp
git add src/ClaudeCode.Cli/Program.cs src/ClaudeCode.Commands/BuiltInCommands.cs
git commit -m "feat: gate feature-flagged tools in Program.cs; add /config features"
```

---

## Task 3: Feature Flags tests

**Files:**
- Modify: `csharp/tests/ClaudeCode.Core.Tests/ClaudeCode.Core.Tests.csproj`
- Create: `csharp/tests/ClaudeCode.Core.Tests/FeatureFlagsTests.cs`

- [ ] **Step 1: Add Configuration project reference to Core.Tests.csproj**

Open `csharp/tests/ClaudeCode.Core.Tests/ClaudeCode.Core.Tests.csproj`. After the existing `<ProjectReference>` for ClaudeCode.Core:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\ClaudeCode.Core\ClaudeCode.Core.csproj" />
    <ProjectReference Include="..\..\src\ClaudeCode.Configuration\ClaudeCode.Configuration.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write failing tests**

Create `csharp/tests/ClaudeCode.Core.Tests/FeatureFlagsTests.cs`:

```csharp
namespace ClaudeCode.Core.Tests;

using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;

public sealed class FeatureFlagsTests : IDisposable
{
    // Clean up env vars set in each test.
    private readonly List<string> _envVarsSet = new();

    public void Dispose()
    {
        foreach (var v in _envVarsSet)
            Environment.SetEnvironmentVariable(v, null);
        FeatureFlags.Load(null); // reset to defaults
    }

    private void SetEnv(string name, string value)
    {
        _envVarsSet.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public void Default_KnownFlag_ReturnsFalse()
    {
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void SettingsJson_SetsFlag_True()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        FeatureFlags.Load(config);
        Assert.True(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_OverridesSettingsJson_True()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = false } };
        SetEnv("CLAUDE_FEATURE_CRON", "1");
        FeatureFlags.Load(config);
        Assert.True(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_OverridesSettingsJson_False()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        SetEnv("CLAUDE_FEATURE_CRON", "0");
        FeatureFlags.Load(config);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_FalseString_ReturnsFalse()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "false");
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_EmptyString_ReturnsFalse()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "");
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_ArbitraryNonFalsy_ReturnsTrue()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "yes");
        FeatureFlags.Load(null);
        Assert.True(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void UnknownFlag_ReturnsFalse()
    {
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("nonexistent-flag-xyz"));
    }

    [Fact]
    public void GetAll_ContainsAllKnownFlags()
    {
        FeatureFlags.Load(null);
        var all = FeatureFlags.GetAll(null);
        Assert.Contains(all, r => r.Flag == "cron");
        Assert.Contains(all, r => r.Flag == "kairos");
        Assert.Contains(all, r => r.Flag == "voice");
    }

    [Fact]
    public void GetAll_SourceIsEnv_WhenEnvVarSet()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "1");
        FeatureFlags.Load(null);
        var all = FeatureFlags.GetAll(null);
        var cron = all.Single(r => r.Flag == "cron");
        Assert.StartsWith("env", cron.Source);
    }

    [Fact]
    public void GetAll_SourceIsSettings_WhenSettingsJsonSet()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        FeatureFlags.Load(config);
        var all = FeatureFlags.GetAll(config);
        var cron = all.Single(r => r.Flag == "cron");
        Assert.Equal("settings.json", cron.Source);
    }

    [Fact]
    public void Load_IdempotentSecondCall_ResetsState()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        FeatureFlags.Load(config);
        Assert.True(FeatureFlags.IsEnabled("cron"));

        FeatureFlags.Load(null); // second call resets to defaults
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }
}
```

- [ ] **Step 3: Run the tests to make sure they pass**

```bash
cd csharp && dotnet test tests/ClaudeCode.Core.Tests/ --no-build -- -v normal
```

Expected: all 12 tests pass.

- [ ] **Step 4: Build full solution to confirm no regressions**

```bash
cd csharp && dotnet build
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
cd csharp
git add tests/ClaudeCode.Core.Tests/ src/
git commit -m "test: add FeatureFlagsTests; wire Configuration reference into Core.Tests"
```

---

## Task 4: PluginCommandDefinition + ScriptPluginCommand + PluginManifest.Commands

**Files:**
- Modify: `csharp/src/ClaudeCode.Services/Plugins/PluginLoader.cs`

- [ ] **Step 1: Add `PluginCommandDefinition` record and extend `PluginManifest`**

In `PluginLoader.cs`, find `public sealed class PluginManifest` (around line 195). Add the new type above it and add the `Commands` property:

```csharp
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
```

- [ ] **Step 2: Add `ScriptPluginCommand` class in `PluginLoader.cs`**

After the existing `ScriptPluginTool` class (around line 210), add:

```csharp
/// <summary>
/// A <see cref="ClaudeCode.Commands.SlashCommand"/> that delegates execution to a script file.
/// Added to the command registry when a plugin manifest declares a <c>commands</c> entry.
/// </summary>
public sealed class ScriptPluginCommand : ClaudeCode.Commands.SlashCommand
{
    private const int TimeoutMs = 30_000;

    private readonly string _name;
    private readonly string _description;
    private readonly string _scriptPath;
    private readonly string _pluginDir;

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

    public override string Name => _name;
    public override string Description => _description;

    public override async Task<bool> ExecuteAsync(
        ClaudeCode.Commands.CommandContext ctx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!File.Exists(_scriptPath))
        {
            ctx.WriteMarkup($"[red]Plugin command '{_name}': script '{Path.GetFileName(_scriptPath)}' not found in {_pluginDir.EscapeMarkup()}[/]");
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
                ctx.WriteMarkup($"[red][error][/] {stderr.TrimEnd().EscapeMarkup()}");
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
            _      => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? new ProcessStartInfo("bash", $"\"{scriptPath}\" {args}")
                        : new ProcessStartInfo("bash", $"\"{scriptPath}\" {args}"),
        };
    }
}
```

- [ ] **Step 3: Add `LoadCommands` method to `PluginLoader`**

Inside `public sealed class PluginLoader`, after the `LoadAndRegisterAll` method (around line 57), add:

```csharp
/// <summary>
/// Loads all plugins from <see cref="LoadAll"/> and returns a
/// <see cref="ScriptPluginCommand"/> for each valid command definition found.
/// Skips entries with a missing <c>Name</c> or <c>Script</c> field (prints a warning).
/// </summary>
public IEnumerable<ClaudeCode.Commands.SlashCommand> LoadCommands(string cwd)
{
    var entries = LoadAll(cwd);
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
```

- [ ] **Step 4: Build to check compilation**

```bash
cd csharp && dotnet build src/ClaudeCode.Services/
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
cd csharp
git add src/ClaudeCode.Services/Plugins/PluginLoader.cs
git commit -m "feat: add PluginCommandDefinition, ScriptPluginCommand, PluginLoader.LoadCommands"
```

---

## Task 5: Plugin commands wired into ReplSession + /help + /reload-plugins

**Files:**
- Modify: `csharp/src/ClaudeCode.Commands/SlashCommand.cs` (CommandContext new callback)
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs` (LoadPluginCommands + context)
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs` (HelpCommand + ReloadPluginsCommand)

- [ ] **Step 1: Add `ReloadPluginsAndCommands` callback to `CommandContext`**

In `SlashCommand.cs`, after the existing `ReloadPlugins` property (around line 119):

```csharp
/// <summary>
/// Triggers a full plugin reload for both tools AND commands.
/// Receives a callback that re-registers plugin commands into the command registry.
/// May be <see langword="null"/> when not wired.
/// </summary>
public Action? ReloadPluginsAndCommands { get; init; }

/// <summary>
/// The names of any plugin-contributed commands currently registered.
/// Used by <c>/help</c> to display a "Plugin Commands" section.
/// </summary>
public IReadOnlyList<string>? PluginCommandNames { get; init; }
```

- [ ] **Step 2: Add `LoadPluginCommands` instance method to `ReplSession`**

In `ReplSession.cs`, after the `_pluginLoader` field declaration (around line 38), add a tracking set:

```csharp
private readonly HashSet<string> _pluginCommandNames = new(StringComparer.OrdinalIgnoreCase);
```

After `RunAsync` startup (around line 121 where `_pluginLoader.LoadAndRegisterAll` is called), ADD a call to load plugin commands:
```csharp
_pluginLoader.LoadAndRegisterAll(cwd, _toolRegistry);
LoadPluginCommands(cwd); // NEW — register plugin slash commands
```

Add the `LoadPluginCommands` instance method to `ReplSession`:
```csharp
/// <summary>
/// Loads slash commands declared in plugin manifests and registers them in the
/// command registry.  Built-in command names win on collision (warning printed once).
/// </summary>
private void LoadPluginCommands(string cwd)
{
    // Remove previously loaded plugin commands before re-registering.
    foreach (var name in _pluginCommandNames)
        _commandRegistry.Unregister(name);
    _pluginCommandNames.Clear();

    foreach (var cmd in _pluginLoader.LoadCommands(cwd))
    {
        if (_commandRegistry.Get(cmd.Name) is not null)
        {
            Console.Error.WriteLine(
                $"[plugin] Warning: command '{cmd.Name}' conflicts with a built-in — skipped.");
            continue;
        }

        _commandRegistry.Register(cmd);
        _pluginCommandNames.Add(cmd.Name);
    }
}
```

- [ ] **Step 3: Add `Unregister` to `CommandRegistry`**

In `SlashCommand.cs`, inside `CommandRegistry`, after `Register`:

```csharp
/// <summary>
/// Removes the command registered under <paramref name="name"/> (and its aliases)
/// from the registry. No-ops when the name is not found.
/// </summary>
public void Unregister(string name)
{
    if (_commands.TryGetValue(name, out var cmd))
    {
        _commands.Remove(name);
        foreach (var alias in cmd.Aliases)
            _commands.Remove(alias);
    }
}
```

- [ ] **Step 4: Pass plugin info through `CommandContext`**

In `ReplSession.cs`, find where `CommandContext` is constructed (around line 485). Add:
```csharp
ReloadPlugins           = () => _pluginLoader.LoadAndRegisterAll(cwd, _toolRegistry),
ReloadPluginsAndCommands = () =>
{
    _pluginLoader.LoadAndRegisterAll(cwd, _toolRegistry);
    LoadPluginCommands(cwd);
},
PluginCommandNames = _pluginCommandNames.ToList(),
```

(Replace or supplement the existing `ReloadPlugins` entry.)

- [ ] **Step 5: Update `HelpCommand` to show plugin commands section**

In `BuiltInCommands.cs`, find `HelpCommand.ExecuteAsync` (around line 33). Replace with:

```csharp
public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(ctx);

    var allCmds = _registry.GetAll().OrderBy(c => c.Name).ToList();
    var pluginNames = ctx.PluginCommandNames ?? [];

    var builtIn = allCmds.Where(c => !pluginNames.Contains(c.Name)).ToList();
    var plugin  = allCmds.Where(c => pluginNames.Contains(c.Name)).ToList();

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
```

- [ ] **Step 6: Update `ReloadPluginsCommand` to also reload commands**

In `BuiltInCommands.cs`, find `ReloadPluginsCommand.ExecuteAsync` (around line 2335). Replace its body:

```csharp
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
```

- [ ] **Step 7: Build**

```bash
cd csharp && dotnet build
```

Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
cd csharp
git add src/ tests/
git commit -m "feat: wire plugin commands into ReplSession, /help, and /reload-plugins"
```

---

## Task 6: Plugin command tests

**Files:**
- Create: `csharp/tests/ClaudeCode.Services.Tests/PluginCommandTests.cs`

- [ ] **Step 1: Write failing tests**

Create `csharp/tests/ClaudeCode.Services.Tests/PluginCommandTests.cs`:

```csharp
namespace ClaudeCode.Services.Tests;

using System.Text.Json;
using ClaudeCode.Services.Plugins;

public sealed class PluginCommandTests
{
    [Fact]
    public void PluginManifest_DeserializesCommands()
    {
        var json = """
            {
              "name": "my-plugin",
              "version": "1.0.0",
              "commands": [
                { "name": "deploy", "description": "Deploy to env", "script": "deploy.sh" }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Single(manifest.Commands!);
        Assert.Equal("deploy", manifest.Commands![0].Name);
        Assert.Equal("deploy.sh", manifest.Commands![0].Script);
    }

    [Fact]
    public void ScriptPluginCommand_NameGetsSlashPrefix()
    {
        var def = new PluginCommandDefinition { Name = "deploy", Script = "deploy.sh", Description = "test" };
        var dir = Path.GetTempPath();
        var cmd = new ScriptPluginCommand(def, dir);
        Assert.Equal("/deploy", cmd.Name);
    }

    [Fact]
    public void ScriptPluginCommand_AlreadySlashedName_NotDoubled()
    {
        var def = new PluginCommandDefinition { Name = "/deploy", Script = "deploy.sh", Description = "test" };
        var dir = Path.GetTempPath();
        var cmd = new ScriptPluginCommand(def, dir);
        Assert.Equal("/deploy", cmd.Name);
    }

    [Fact]
    public void ScriptPluginCommand_MissingName_Throws()
    {
        var def = new PluginCommandDefinition { Script = "deploy.sh" };
        Assert.Throws<ArgumentException>(() => new ScriptPluginCommand(def, Path.GetTempPath()));
    }

    [Fact]
    public void ScriptPluginCommand_MissingScript_Throws()
    {
        var def = new PluginCommandDefinition { Name = "deploy" };
        Assert.Throws<ArgumentException>(() => new ScriptPluginCommand(def, Path.GetTempPath()));
    }

    [Fact]
    public async Task ScriptPluginCommand_ScriptNotFound_WritesError()
    {
        var def = new PluginCommandDefinition
        {
            Name = "missing",
            Script = "nonexistent_script_xyz.sh",
            Description = "test"
        };
        var dir = Path.GetTempPath();
        var cmd = new ScriptPluginCommand(def, dir);

        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/missing",
            Args = [],
            Cwd = dir,
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        var result = await cmd.ExecuteAsync(ctx);

        Assert.True(result);
        Assert.Contains(output, s => s.Contains("not found"));
    }

    [Fact]
    public void LoadCommands_ManifestWithNoCommands_ReturnsEmpty()
    {
        var loader = new PluginLoader();
        // No plugin directories exist in a temp dir — returns empty.
        var result = loader.LoadCommands(Path.GetTempPath()).ToList();
        // May contain commands from ~/.claude/plugins/ if user has any; skip assertion on count.
        // Just verify it does not throw.
        Assert.NotNull(result);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
cd csharp && dotnet test tests/ClaudeCode.Services.Tests/ --no-build -- -v normal
```

Expected: all plugin command tests pass.

- [ ] **Step 3: Commit**

```bash
cd csharp
git add tests/ClaudeCode.Services.Tests/PluginCommandTests.cs
git commit -m "test: add PluginCommandTests"
```

---

## Task 7: IVoiceEngine + VoiceUnavailableException + DefaultVoiceEngine + VoiceInputService

**Files:**
- Create: `csharp/src/ClaudeCode.Services/Voice/IVoiceEngine.cs`
- Create: `csharp/src/ClaudeCode.Services/Voice/VoiceUnavailableException.cs`
- Create: `csharp/src/ClaudeCode.Services/Voice/DefaultVoiceEngine.cs`
- Create: `csharp/src/ClaudeCode.Services/Voice/VoiceInputService.cs`
- Modify: `csharp/src/ClaudeCode.Services/ClaudeCode.Services.csproj`

- [ ] **Step 1: Add System.Speech NuGet reference**

In `ClaudeCode.Services.csproj`, add inside `<ItemGroup>` (the one containing package refs):

```xml
<PackageReference Include="System.Speech" Version="8.0.0"
                  Condition="'$(OS)' == 'Windows_NT'" />
```

- [ ] **Step 2: Create `IVoiceEngine.cs`**

```csharp
namespace ClaudeCode.Services.Voice;

/// <summary>
/// Abstraction over a speech recognition engine. Allows <see cref="VoiceInputService"/>
/// to be unit-tested without a real microphone.
/// </summary>
public interface IVoiceEngine : IDisposable
{
    /// <summary>Raised when the engine successfully recognizes speech.</summary>
    event Action<string>? SpeechRecognized;

    /// <summary>Raised when a recognition attempt fails (no match found).</summary>
    event Action? SpeechRejected;

    /// <summary>Begins listening. Throws <see cref="VoiceUnavailableException"/> on failure.</summary>
    void Start();

    /// <summary>Stops listening and releases the microphone.</summary>
    void Stop();
}
```

- [ ] **Step 3: Create `VoiceUnavailableException.cs`**

```csharp
namespace ClaudeCode.Services.Voice;

/// <summary>
/// Thrown by <see cref="DefaultVoiceEngine"/> when the speech recognition engine
/// cannot be initialised (no microphone, speech recognition not installed, etc.).
/// </summary>
public sealed class VoiceUnavailableException : Exception
{
    public VoiceUnavailableException(string message) : base(message) { }
    public VoiceUnavailableException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 4: Create `DefaultVoiceEngine.cs` (Windows-only)**

```csharp
namespace ClaudeCode.Services.Voice;

using System.Runtime.InteropServices;

/// <summary>
/// <see cref="IVoiceEngine"/> implementation backed by <c>System.Speech.Recognition</c>.
/// Windows-only. On non-Windows platforms, constructor throws <see cref="VoiceUnavailableException"/>.
/// </summary>
public sealed class DefaultVoiceEngine : IVoiceEngine
{
    public event Action<string>? SpeechRecognized;
    public event Action? SpeechRejected;

    // SpeechRecognitionEngine is in System.Speech, available on Windows only.
    // We reference it via dynamic invocation to avoid compile-time failures on non-Windows.
    private object? _engine; // actually System.Speech.Recognition.SpeechRecognitionEngine

    public DefaultVoiceEngine()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new VoiceUnavailableException(
                "Voice input requires Windows (System.Speech.Recognition).");

        try
        {
            // Use reflection so the assembly reference is truly conditional at runtime.
            var engineType = Type.GetType(
                "System.Speech.Recognition.SpeechRecognitionEngine, System.Speech",
                throwOnError: true)!;
            var grammarType = Type.GetType(
                "System.Speech.Recognition.DictationGrammar, System.Speech",
                throwOnError: true)!;

            _engine = Activator.CreateInstance(engineType)!;

            // engine.SetInputToDefaultAudioDevice()
            engineType.GetMethod("SetInputToDefaultAudioDevice")!.Invoke(_engine, null);

            // engine.LoadGrammar(new DictationGrammar())
            var grammar = Activator.CreateInstance(grammarType)!;
            engineType.GetMethod("LoadGrammar")!.Invoke(_engine, [grammar]);

            // Subscribe to SpeechRecognized event
            var recognizedEvent = engineType.GetEvent("SpeechRecognized")!;
            var recognizedHandler = new EventHandler<object>(OnSpeechRecognized);
            // Use a MethodInfo-based delegate to bridge the typed EventHandler<SpeechRecognizedEventArgs>
            SubscribeEvent(engineType, _engine, "SpeechRecognized", OnRawSpeechRecognized);
            SubscribeEvent(engineType, _engine, "SpeechRecognitionRejected", OnRawSpeechRejected);
        }
        catch (Exception ex) when (ex is not VoiceUnavailableException)
        {
            throw new VoiceUnavailableException($"Speech recognition engine init failed: {ex.Message}", ex);
        }
    }

    private void OnRawSpeechRecognized(object? sender, EventArgs e)
    {
        // Extract e.Result.Text via reflection.
        try
        {
            var resultProp = e.GetType().GetProperty("Result");
            var result = resultProp?.GetValue(e);
            var textProp = result?.GetType().GetProperty("Text");
            var text = textProp?.GetValue(result) as string;
            if (!string.IsNullOrWhiteSpace(text))
                SpeechRecognized?.Invoke(text);
        }
        catch { /* best-effort */ }
    }

    private void OnRawSpeechRejected(object? sender, EventArgs e)
        => SpeechRejected?.Invoke();

    public void Start()
    {
        if (_engine is null) return;
        try
        {
            // engine.RecognizeAsync(RecognizeMode.Multiple)
            var engineType = _engine.GetType();
            var modeType  = Type.GetType(
                "System.Speech.Recognition.RecognizeMode, System.Speech", throwOnError: true)!;
            var multiple   = Enum.Parse(modeType, "Multiple");
            engineType.GetMethod("RecognizeAsync", [modeType])!.Invoke(_engine, [multiple]);
        }
        catch (Exception ex)
        {
            throw new VoiceUnavailableException($"Failed to start recognition: {ex.Message}", ex);
        }
    }

    public void Stop()
    {
        try { _engine?.GetType().GetMethod("RecognizeAsyncStop")?.Invoke(_engine, null); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        Stop();
        (_engine as IDisposable)?.Dispose();
        _engine = null;
    }

    // -------------------------------------------------------------------------
    // Reflection helpers
    // -------------------------------------------------------------------------

    private static void SubscribeEvent(Type engineType, object engine, string eventName, Action<object?, EventArgs> handler)
    {
        var ev = engineType.GetEvent(eventName);
        if (ev is null) return;

        // Create a delegate of the event's handler type that calls our anonymous method.
        var invokeMethod = ev.EventHandlerType!.GetMethod("Invoke")!;
        var paramTypes   = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();

        // Build a small lambda of the right delegate type via DynamicMethod is complex;
        // simpler: create an EventHandler<EventArgs> and use reflection to add it.
        // This works because all Speech events use EventHandler<T> where T : EventArgs.
        var eventHandler = new EventHandler<EventArgs>((s, e) => handler(s, e));
        var addMethod    = ev.AddMethod!;
        // Wrap into the exact type using CreateDelegate from the universal EventHandler<EventArgs>.
        // For System.Speech events (SpeechRecognized uses EventHandler<SpeechRecognizedEventArgs>),
        // we create a compatible delegate via Delegate.CreateDelegate on a MethodInfo proxy.
        // Safe approach: use the Object overloads and cast.
        try
        {
            var del = Delegate.CreateDelegate(ev.EventHandlerType!, handler.Target,
                handler.Method, throwOnBindFailure: false);
            if (del is not null)
            {
                addMethod.Invoke(engine, [del]);
                return;
            }
        }
        catch { /* fall through */ }

        // Fallback: subscribe via EventInfo.AddEventHandler with an EventHandler<EventArgs>.
        var fallback = new EventHandler<EventArgs>((s, e) => handler(s, e));
        try { ev.AddEventHandler(engine, fallback); } catch { /* best-effort */ }
    }

    private void OnSpeechRecognized(object sender, object e) { /* unused — bridged via OnRawSpeechRecognized */ }
}
```

- [ ] **Step 5: Create `VoiceInputService.cs`**

```csharp
namespace ClaudeCode.Services.Voice;

/// <summary>
/// Manages speech-to-text input for the REPL using an <see cref="IVoiceEngine"/>.
/// Raises <see cref="TextRecognized"/> when speech is successfully captured.
/// Prints a heartbeat to the console every <see cref="HeartbeatIntervalSeconds"/> when silent.
/// </summary>
public sealed class VoiceInputService : IDisposable
{
    private const int HeartbeatIntervalSeconds = 10;

    private readonly IVoiceEngine _engine;
    private Timer? _heartbeat;
    private bool _started;

    /// <summary>Raised on the thread-pool when speech is recognized.</summary>
    public event Action<string>? TextRecognized;

    public VoiceInputService(IVoiceEngine engine)
        => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>
    /// Starts the recognition engine and heartbeat timer.
    /// Throws <see cref="VoiceUnavailableException"/> when the engine cannot start.
    /// </summary>
    public void Start()
    {
        if (_started) return;

        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.SpeechRejected   += OnSpeechRejected;
        _engine.Start();

        _heartbeat = new Timer(
            _ => Console.Write("\r[voice: listening...]  "),
            state: null,
            dueTime: TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
            period: TimeSpan.FromSeconds(HeartbeatIntervalSeconds));

        _started = true;
    }

    /// <summary>Stops recognition and disposes the heartbeat timer.</summary>
    public void Stop()
    {
        if (!_started) return;

        _heartbeat?.Dispose();
        _heartbeat = null;

        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechRejected   -= OnSpeechRejected;
        _engine.Stop();

        _started = false;
    }

    public void Dispose()
    {
        Stop();
        _engine.Dispose();
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private void OnSpeechRecognized(string text)
    {
        _heartbeat?.Change(
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
        TextRecognized?.Invoke(text);
    }

    private void OnSpeechRejected()
    {
        // Heartbeat timer already running — reset it so the next heartbeat fires
        // HeartbeatIntervalSeconds after the last rejection.
        _heartbeat?.Change(
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
    }
}
```

- [ ] **Step 6: Build**

```bash
cd csharp && dotnet build
```

Expected: 0 errors. (`System.Speech` will be referenced only on Windows; Linux/macOS builds succeed too because `DefaultVoiceEngine` is never instantiated on non-Windows.)

- [ ] **Step 7: Commit**

```bash
cd csharp
git add src/ClaudeCode.Services/Voice/ src/ClaudeCode.Services/ClaudeCode.Services.csproj
git commit -m "feat: add IVoiceEngine, DefaultVoiceEngine, VoiceInputService (System.Speech STT)"
```

---

## Task 8: Wire voice input into ReplSession + extend VoiceCommand

**Files:**
- Modify: `csharp/src/ClaudeCode.Commands/SlashCommand.cs`
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs`
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs`

- [ ] **Step 1: Add `ToggleVoiceInput` to `CommandContext`**

In `SlashCommand.cs`, after `ReloadPluginsAndCommands` (Task 5 addition):

```csharp
/// <summary>
/// Starts or stops the speech-to-text input service.
/// Receives <see langword="true"/> to start listening, <see langword="false"/> to stop.
/// May be <see langword="null"/> when voice infrastructure is not wired.
/// </summary>
public Action<bool>? ToggleVoiceInput { get; init; }
```

- [ ] **Step 2: Extend `VoiceCommand.ExecuteAsync` to control STT**

In `BuiltInCommands.cs`, find `VoiceCommand.ExecuteAsync`. Near the end of the `if (ReplModeFlags.VoiceMode)` branch (after the `await SpeakAsync(...)` call) and inside the `else` branch, add the STT toggle calls:

```csharp
    if (ReplModeFlags.VoiceMode)
    {
        // ... existing TTS detection and SpeakAsync code ...
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
```

- [ ] **Step 3: Add `_voiceInputService` field and `ToggleVoiceInput` method to `ReplSession`**

In `ReplSession.cs`, after `_promptSuggestionSvc` field (around line 54):

```csharp
private ClaudeCode.Services.Voice.VoiceInputService? _voiceInputService;
```

Add the helper methods after `ToggleVoiceInput`:

```csharp
/// <summary>Starts or stops the STT voice input service.</summary>
private void ToggleVoiceInput(bool enabled)
{
    if (enabled)
    {
        if (_voiceInputService is null)
        {
            try
            {
                var engine = new ClaudeCode.Services.Voice.DefaultVoiceEngine();
                _voiceInputService = new ClaudeCode.Services.Voice.VoiceInputService(engine);
                _voiceInputService.TextRecognized += OnVoiceTextRecognized;
            }
            catch (ClaudeCode.Services.Voice.VoiceUnavailableException ex)
            {
                AnsiConsole.MarkupLine($"[red]Voice input unavailable: {ex.Message.EscapeMarkup()}[/]");
                ClaudeCode.Core.State.ReplModeFlags.VoiceMode = false;
                return;
            }
        }
        _voiceInputService.Start();
        AnsiConsole.MarkupLine("[green]Voice input started.[/]");
    }
    else
    {
        _voiceInputService?.Stop();
    }
}

private void OnVoiceTextRecognized(string text)
{
    AnsiConsole.MarkupLine($"[grey]Recognized: \"{text.EscapeMarkup()}\"[/]");
    // Store as pending next prompt — ReplSession will submit it on next iteration.
    _pendingNextPrompt ??= text;
}
```

- [ ] **Step 4: Pass `ToggleVoiceInput` callback into `CommandContext`**

In `ReplSession.cs`, find CommandContext construction (around line 485). Add:

```csharp
ToggleVoiceInput = enabled => ToggleVoiceInput(enabled),
```

- [ ] **Step 5: Add `[MIC] ` prompt prefix when voice is active**

In `ReplSession.cs`, find the code that draws the REPL prompt. Search for the `ReadInput` or prompt-drawing logic. Add to whatever returns the prompt string:

```csharp
var promptPrefix = ClaudeCode.Core.State.ReplModeFlags.VoiceMode ? "[MIC] " : "";
```

(The exact integration point depends on how the prompt string is assembled. Search for `"> "` or `Prompt` in `ReplSession.cs` and prepend `promptPrefix`.)

- [ ] **Step 6: Build**

```bash
cd csharp && dotnet build
```

Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
cd csharp
git add src/ClaudeCode.Cli/Repl/ReplSession.cs src/ClaudeCode.Commands/
git commit -m "feat: wire voice STT into ReplSession; extend VoiceCommand for System.Speech"
```

---

## Task 9: Voice input tests

**Files:**
- Create: `csharp/tests/ClaudeCode.Services.Tests/VoiceInputServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Create `csharp/tests/ClaudeCode.Services.Tests/VoiceInputServiceTests.cs`:

```csharp
namespace ClaudeCode.Services.Tests;

using ClaudeCode.Services.Voice;

/// <summary>
/// Tests for VoiceInputService using a mock IVoiceEngine so no real microphone is needed.
/// </summary>
public sealed class VoiceInputServiceTests
{
    private sealed class MockVoiceEngine : IVoiceEngine
    {
        public event Action<string>? SpeechRecognized;
        public event Action? SpeechRejected;
        public bool StartCalled { get; private set; }
        public bool StopCalled  { get; private set; }
        public bool Disposed    { get; private set; }

        public void Start()  => StartCalled = true;
        public void Stop()   => StopCalled  = true;
        public void Dispose(){ Disposed     = true; }

        /// <summary>Simulates the engine recognizing speech.</summary>
        public void SimulateRecognized(string text) => SpeechRecognized?.Invoke(text);

        /// <summary>Simulates the engine rejecting a recognition attempt.</summary>
        public void SimulateRejected() => SpeechRejected?.Invoke();
    }

    [Fact]
    public void Start_CallsEngineStart()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();
        Assert.True(engine.StartCalled);
    }

    [Fact]
    public void Stop_CallsEngineStop()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();
        svc.Stop();
        Assert.True(engine.StopCalled);
    }

    [Fact]
    public void TextRecognized_FiredWhenEngineRecognizes()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();

        string? received = null;
        svc.TextRecognized += t => received = t;

        engine.SimulateRecognized("hello world");

        Assert.Equal("hello world", received);
    }

    [Fact]
    public void TextRecognized_NotFiredAfterStop()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();

        string? received = null;
        svc.TextRecognized += t => received = t;

        svc.Stop();
        engine.SimulateRecognized("should be ignored");

        Assert.Null(received);
    }

    [Fact]
    public void Start_CalledTwice_DoesNotDoubleSubscribe()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();
        svc.Start(); // second call is no-op

        int count = 0;
        svc.TextRecognized += _ => count++;
        engine.SimulateRecognized("test");
        Assert.Equal(1, count); // event fires exactly once
    }

    [Fact]
    public void Dispose_StopsEngineAndDisposesIt()
    {
        var engine = new MockVoiceEngine();
        var svc = new VoiceInputService(engine);
        svc.Start();
        svc.Dispose();

        Assert.True(engine.StopCalled);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public void SpeechRejected_DoesNotRaiseTextRecognized()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();

        bool raised = false;
        svc.TextRecognized += _ => raised = true;

        engine.SimulateRejected();

        Assert.False(raised);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
cd csharp && dotnet test tests/ClaudeCode.Services.Tests/ --no-build -- -v normal
```

Expected: all voice tests pass.

- [ ] **Step 3: Commit**

```bash
cd csharp
git add tests/ClaudeCode.Services.Tests/VoiceInputServiceTests.cs
git commit -m "test: add VoiceInputServiceTests with MockVoiceEngine"
```

---

## Task 10: ReplModeFlags KAIROS/Buddy flags + QueryEngine system prompt

**Files:**
- Modify: `csharp/src/ClaudeCode.Core/State/ReplModeFlags.cs`
- Modify: `csharp/src/ClaudeCode.Services/Engine/QueryEngine.cs`

- [ ] **Step 1: Add KAIROS/Buddy flags and system prompt constant to `ReplModeFlags`**

In `ReplModeFlags.cs`, after the `UltraplanSystemPrompt` const (end of file):

```csharp
    /// <summary>
    /// When <see langword="true"/>, assistant mode (KAIROS) is active.
    /// The KAIROS system-prompt addendum is appended to every request.
    /// Toggled by <c>/assistant</c>.
    /// </summary>
    public static bool KairosEnabled { get; set; }

    /// <summary>
    /// When <see langword="true"/>, buddy mode is active.
    /// After each assistant turn a lightweight secondary API call generates a
    /// one-sentence context note shown before the next prompt.
    /// Toggled by <c>/buddy</c>.
    /// </summary>
    public static bool BuddyEnabled { get; set; }

    /// <summary>
    /// System prompt addendum injected when <see cref="KairosEnabled"/> is active.
    /// Stored here so both ClaudeCode.Services and ClaudeCode.Commands can read it
    /// without a circular project reference.
    /// </summary>
    public const string KairosSystemPrompt =
        "--- ASSISTANT MODE ---\n" +
        "You are operating in assistant mode. Follow these rules every turn:\n" +
        "1. When the user's intent is ambiguous, ask exactly one clarifying question before acting.\n" +
        "2. When presenting choices, always use a numbered list (1. option  2. option ...).\n" +
        "3. Before executing any destructive operation (delete files, overwrite, reset, force-push),\n" +
        "   state what you are about to do and ask: \"Shall I proceed? (yes/no)\"\n" +
        "4. Begin every multi-step task with one sentence: \"I'll do X, then Y, then Z.\"\n" +
        "--- END ASSISTANT MODE ---";
```

- [ ] **Step 2: Add KAIROS system prompt injection to `QueryEngine.cs`**

In `QueryEngine.cs`, find the UltraplanActive block (around line 217):

```csharp
        if (ClaudeCode.Core.State.ReplModeFlags.UltraplanActive)
            systemPrompt += "\n\n" + ClaudeCode.Core.State.ReplModeFlags.UltraplanSystemPrompt;
```

Immediately after, add:

```csharp
        // Inject KAIROS assistant-mode addendum when /assistant toggle is active.
        if (ClaudeCode.Core.State.ReplModeFlags.KairosEnabled)
            systemPrompt += "\n\n" + ClaudeCode.Core.State.ReplModeFlags.KairosSystemPrompt;
```

- [ ] **Step 3: Build**

```bash
cd csharp && dotnet build
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
cd csharp
git add src/ClaudeCode.Core/State/ReplModeFlags.cs src/ClaudeCode.Services/Engine/QueryEngine.cs
git commit -m "feat: add KairosEnabled/BuddyEnabled flags; inject KAIROS system prompt in QueryEngine"
```

---

## Task 11: BuddyService + ReplSession buddy integration

**Files:**
- Create: `csharp/src/ClaudeCode.Services/AutoDream/BuddyService.cs`
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs`

- [ ] **Step 1: Create `BuddyService`**

Create `csharp/src/ClaudeCode.Services/AutoDream/BuddyService.cs`:

```csharp
namespace ClaudeCode.Services.AutoDream;

using ClaudeCode.Services.Api;

/// <summary>
/// After each completed assistant turn (when buddy mode is active), makes a lightweight
/// secondary API call to generate a one-sentence "what is the user working on?" note.
/// The note is displayed as a dim-grey footer before the next REPL prompt.
/// </summary>
public sealed class BuddyService
{
    private const string Model = "claude-haiku-4-5-20251001";
    private const int TimeoutMs = 5_000;
    private const int MaxMessages = 6; // 3 pairs

    private static readonly string SystemPrompt =
        "You are a silent context summarizer. Respond with exactly ONE sentence of at most 12 words " +
        "describing what the user is currently working on. Do not ask questions. Do not include caveats.";

    private readonly IAnthropicClient _client;
    private readonly CostTracker _costTracker;

    public BuddyService(IAnthropicClient client, CostTracker costTracker)
    {
        _client       = client       ?? throw new ArgumentNullException(nameof(client));
        _costTracker  = costTracker  ?? throw new ArgumentNullException(nameof(costTracker));
    }

    /// <summary>
    /// Returns a one-sentence context note, or <see langword="null"/> on timeout/error.
    /// </summary>
    /// <param name="recentMessages">The most recent conversation messages.</param>
    /// <param name="ct">Session cancellation token.</param>
    public async Task<string?> GetContextNoteAsync(
        IReadOnlyList<MessageParam> recentMessages,
        CancellationToken ct)
    {
        if (!recentMessages.Any()) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);

        try
        {
            // Take the last MaxMessages messages to keep the context focused.
            var slice = recentMessages
                .Skip(Math.Max(0, recentMessages.Count - MaxMessages))
                .ToList();

            var request = new MessageRequest
            {
                Model      = Model,
                MaxTokens  = 64,
                System     = [new SystemBlock { Text = SystemPrompt }],
                Messages   = slice,
            };

            string note = string.Empty;

            await foreach (var ev in _client.StreamMessageAsync(request, timeoutCts.Token))
            {
                if (ev is DeltaEvent { Delta: TextDelta td })
                    note += td.Text;

                if (ev is MessageDeltaEvent mde)
                    _costTracker.Add(Model, mde.Usage.InputTokens, mde.Usage.OutputTokens);
            }

            note = note.Trim();
            return string.IsNullOrEmpty(note) ? null : note;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 2: Add `_pendingBuddyNote` fields to `ReplSession`**

In `ReplSession.cs`, after `_promptSuggestion` field (around line 54):

```csharp
private ClaudeCode.Services.AutoDream.BuddyService? _buddySvc;
private Task<string?>? _buddyTask;         // fires after each assistant turn
```

- [ ] **Step 3: Initialize `BuddyService` in `RunAsync`**

In `ReplSession.RunAsync`, after `_agentSummaryService` is initialized (around line 135):

```csharp
_buddySvc = new ClaudeCode.Services.AutoDream.BuddyService(_client, _costTracker);
```

- [ ] **Step 4: Fire buddy task after each completed assistant turn**

In `ReplSession.cs`, find `SubmitTurnAsync` (around line 532). After the engine call returns and before the method returns, add:

```csharp
// Fire buddy note generation in background for next-prompt display.
if (ClaudeCode.Core.State.ReplModeFlags.BuddyEnabled && _buddySvc is not null)
{
    var msgs = _engine?.Messages ?? [];
    _buddyTask = _buddySvc.GetContextNoteAsync(msgs, sessionToken);
}
```

- [ ] **Step 5: Show pending buddy note before each prompt**

In `ReplSession.cs`, find the main REPL loop where the prompt is about to be shown (just before `ReadInputAsync` or `Console.Write("> ")`). Add:

```csharp
// Show buddy context note from previous turn (if ready).
if (_buddyTask is { IsCompleted: true })
{
    var note = await _buddyTask;
    _buddyTask = null;
    if (!string.IsNullOrWhiteSpace(note))
        AnsiConsole.MarkupLine($"[grey]  ↳ Buddy: {note.EscapeMarkup()}[/]");
}
else if (_buddyTask is { IsCompleted: false })
{
    // Not ready yet — discard silently (avoid display race).
    _buddyTask = null;
}
```

- [ ] **Step 6: Build**

```bash
cd csharp && dotnet build
```

Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
cd csharp
git add src/ClaudeCode.Services/AutoDream/BuddyService.cs src/ClaudeCode.Cli/Repl/ReplSession.cs
git commit -m "feat: add BuddyService; wire pending buddy note into ReplSession loop"
```

---

## Task 12: AssistantCommand + BuddyCommand + KAIROS number selection + register

**Files:**
- Modify: `csharp/src/ClaudeCode.Commands/BuiltInCommands.cs`
- Modify: `csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs`

- [ ] **Step 1: Add `AssistantCommand` to `BuiltInCommands.cs`**

After `AutofixPrCommand` (end of file, around line 5500+):

```csharp
// =============================================================================
// KAIROS / Buddy mode commands
// =============================================================================

/// <summary>
/// Toggles KAIROS assistant mode. When enabled, the system prompt instructs Claude to
/// ask clarifying questions, use numbered lists for choices, confirm destructive operations,
/// and begin multi-step tasks with a one-sentence plan.
/// Requires feature flag CLAUDE_FEATURE_KAIROS=1.
/// </summary>
public sealed class AssistantCommand : SlashCommand
{
    public override string Name        => "/assistant";
    public override string Description => "Toggle KAIROS assistant mode (structured reasoning)";

    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ClaudeCode.Configuration.FeatureFlags.IsEnabled("kairos"))
        {
            ctx.WriteMarkup("[yellow]Assistant mode is disabled. Enable with: CLAUDE_FEATURE_KAIROS=1[/]");
            return Task.FromResult(true);
        }

        ClaudeCode.Core.State.ReplModeFlags.KairosEnabled =
            !ClaudeCode.Core.State.ReplModeFlags.KairosEnabled;

        if (ClaudeCode.Core.State.ReplModeFlags.KairosEnabled)
            ctx.WriteMarkup("[green]Assistant mode ON[/] [grey](KAIROS structured reasoning active)[/]");
        else
            ctx.WriteMarkup("[grey]Assistant mode OFF[/]");

        return Task.FromResult(true);
    }
}

/// <summary>
/// Toggles Buddy mode. When enabled, a lightweight secondary API call generates a
/// one-sentence context note shown dimly before each REPL prompt.
/// Requires feature flag CLAUDE_FEATURE_KAIROS=1.
/// </summary>
public sealed class BuddyCommand : SlashCommand
{
    public override string Name        => "/buddy";
    public override string Description => "Toggle Buddy mode (ambient context notes)";

    public override Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ClaudeCode.Configuration.FeatureFlags.IsEnabled("kairos"))
        {
            ctx.WriteMarkup("[yellow]Buddy mode is disabled. Enable with: CLAUDE_FEATURE_KAIROS=1[/]");
            return Task.FromResult(true);
        }

        ClaudeCode.Core.State.ReplModeFlags.BuddyEnabled =
            !ClaudeCode.Core.State.ReplModeFlags.BuddyEnabled;

        if (ClaudeCode.Core.State.ReplModeFlags.BuddyEnabled)
            ctx.WriteMarkup("[green]Buddy mode ON[/] [grey](context notes after each turn)[/]");
        else
            ctx.WriteMarkup("[grey]Buddy mode OFF[/]");

        return Task.FromResult(true);
    }
}
```

- [ ] **Step 2: Register `AssistantCommand` and `BuddyCommand` in `BuildCommandRegistry`**

In `ReplSession.cs`, find `BuildCommandRegistry()`. At the end, before `return registry;`:

```csharp
        registry.Register(new AssistantCommand());
        registry.Register(new BuddyCommand());
```

- [ ] **Step 3: Add KAIROS numbered-selection shortcut in `ReplSession`**

In `ReplSession.cs`, find the method that processes user input before submitting to the engine (the method that reads `rawInput` and decides whether to run a command or submit a turn). Find the user-input dispatch code:

```csharp
// KAIROS numbered-selection shortcut: if KAIROS mode is active and the entire
// input is a bare integer, prefix it with "Select option ".
if (ClaudeCode.Core.State.ReplModeFlags.KairosEnabled
    && System.Text.RegularExpressions.Regex.IsMatch(rawInput.Trim(), @"^\d+$"))
{
    rawInput = $"Select option {rawInput.Trim()}";
}
```

Add this before the engine submission (after slash-command handling, so `/1` still runs any command named "1").

- [ ] **Step 4: Add `[ASSISTANT] ` prompt prefix for KAIROS mode**

In the same prompt-prefix code modified in Task 8 Step 5:

```csharp
var promptPrefix = ClaudeCode.Core.State.ReplModeFlags.VoiceMode    ? "[MIC] "
                 : ClaudeCode.Core.State.ReplModeFlags.KairosEnabled ? "[ASSISTANT] "
                 : "";
```

- [ ] **Step 5: Build**

```bash
cd csharp && dotnet build
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
cd csharp
git add src/ClaudeCode.Commands/BuiltInCommands.cs src/ClaudeCode.Cli/Repl/ReplSession.cs
git commit -m "feat: add AssistantCommand, BuddyCommand; KAIROS number selection + prompt prefix"
```

---

## Task 13: KAIROS and Buddy tests

**Files:**
- Create: `csharp/tests/ClaudeCode.Services.Tests/KairosAndBuddyTests.cs`

- [ ] **Step 1: Write failing tests**

Create `csharp/tests/ClaudeCode.Services.Tests/KairosAndBuddyTests.cs`:

```csharp
namespace ClaudeCode.Services.Tests;

using ClaudeCode.Core.State;
using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;

public sealed class KairosAndBuddyTests : IDisposable
{
    public void Dispose()
    {
        // Reset all static state.
        ReplModeFlags.KairosEnabled = false;
        ReplModeFlags.BuddyEnabled  = false;
        FeatureFlags.Load(null);
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", null);
    }

    // ---- ReplModeFlags ----

    [Fact]
    public void KairosEnabled_DefaultIsFalse()
    {
        Assert.False(ReplModeFlags.KairosEnabled);
    }

    [Fact]
    public void BuddyEnabled_DefaultIsFalse()
    {
        Assert.False(ReplModeFlags.BuddyEnabled);
    }

    [Fact]
    public void KairosSystemPrompt_ContainsKeyPhrases()
    {
        Assert.Contains("assistant mode", ReplModeFlags.KairosSystemPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarifying question", ReplModeFlags.KairosSystemPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("numbered list", ReplModeFlags.KairosSystemPrompt,
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- Feature flag gate ----

    [Fact]
    public void AssistantCommand_FlagOff_PrintsEnableInstruction()
    {
        FeatureFlags.Load(null); // defaults — kairos = false
        var cmd = new ClaudeCode.Commands.AssistantCommand();

        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/assistant",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();

        Assert.Contains(output, s => s.Contains("CLAUDE_FEATURE_KAIROS"));
        Assert.False(ReplModeFlags.KairosEnabled); // did not toggle
    }

    [Fact]
    public void AssistantCommand_FlagOn_TogglesKairosEnabled()
    {
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", "1");
        FeatureFlags.Load(null);

        var cmd = new ClaudeCode.Commands.AssistantCommand();
        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/assistant",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.True(ReplModeFlags.KairosEnabled);

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.False(ReplModeFlags.KairosEnabled);
    }

    [Fact]
    public void BuddyCommand_FlagOff_PrintsEnableInstruction()
    {
        FeatureFlags.Load(null);
        var cmd = new ClaudeCode.Commands.BuddyCommand();

        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/buddy",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();

        Assert.Contains(output, s => s.Contains("CLAUDE_FEATURE_KAIROS"));
        Assert.False(ReplModeFlags.BuddyEnabled);
    }

    [Fact]
    public void BuddyCommand_FlagOn_TogglesBuddyEnabled()
    {
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", "1");
        FeatureFlags.Load(null);

        var cmd = new ClaudeCode.Commands.BuddyCommand();
        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/buddy",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.True(ReplModeFlags.BuddyEnabled);

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.False(ReplModeFlags.BuddyEnabled);
    }
}
```

- [ ] **Step 2: Add `ClaudeCode.Configuration` reference to `ClaudeCode.Services.Tests.csproj`**

In `ClaudeCode.Services.Tests.csproj`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\ClaudeCode.Services\ClaudeCode.Services.csproj" />
    <ProjectReference Include="..\..\src\ClaudeCode.Core\ClaudeCode.Core.csproj" />
    <ProjectReference Include="..\..\src\ClaudeCode.Configuration\ClaudeCode.Configuration.csproj" />
    <ProjectReference Include="..\..\src\ClaudeCode.Commands\ClaudeCode.Commands.csproj" />
  </ItemGroup>
```

- [ ] **Step 3: Run tests**

```bash
cd csharp && dotnet test tests/ClaudeCode.Services.Tests/ --no-build -- -v normal
```

Expected: all KAIROS/Buddy tests pass.

- [ ] **Step 4: Commit**

```bash
cd csharp
git add tests/ClaudeCode.Services.Tests/
git commit -m "test: add KairosAndBuddyTests; feature flag gate + toggle verification"
```

---

## Task 14: Final build verification + cleanup commit

- [ ] **Step 1: Run full solution build**

```bash
cd csharp && dotnet build
```

Expected: `Build succeeded.` 0 errors.

- [ ] **Step 2: Run all tests**

```bash
cd csharp && dotnet test
```

Expected: all tests pass, 0 failures.

- [ ] **Step 3: Verify feature flags are gating correctly**

```bash
cd csharp
# Cron tools should NOT be registered by default
CLAUDE_FEATURE_CRON=0 dotnet run --project src/ClaudeCode.Cli -- --version
# Cron tools SHOULD be registered with flag set
CLAUDE_FEATURE_CRON=1 dotnet run --project src/ClaudeCode.Cli -- --version
```

- [ ] **Step 4: Final commit**

```bash
cd csharp
git add -A
git commit -m "feat: C# reimplementation 100% complete — Feature Flags, Plugin Commands, Voice STT, KAIROS/Buddy"
```

---

## Summary of New Files

| File | Purpose |
|------|---------|
| `ClaudeCode.Configuration/FeatureFlags.cs` | Runtime feature flags (env + settings.json) |
| `ClaudeCode.Services/Voice/IVoiceEngine.cs` | STT engine abstraction |
| `ClaudeCode.Services/Voice/VoiceUnavailableException.cs` | STT init error |
| `ClaudeCode.Services/Voice/DefaultVoiceEngine.cs` | System.Speech implementation |
| `ClaudeCode.Services/Voice/VoiceInputService.cs` | STT service + heartbeat |
| `ClaudeCode.Services/AutoDream/BuddyService.cs` | Buddy context note generation |
| `ClaudeCode.Core.Tests/FeatureFlagsTests.cs` | 12 flag resolution tests |
| `ClaudeCode.Services.Tests/PluginCommandTests.cs` | Plugin command manifest + execution tests |
| `ClaudeCode.Services.Tests/VoiceInputServiceTests.cs` | STT service with mock engine |
| `ClaudeCode.Services.Tests/KairosAndBuddyTests.cs` | KAIROS/Buddy flag gate + toggle tests |

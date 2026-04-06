using ClaudeCode.Cli;
using ClaudeCode.Services.Api;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

// Fast path for --version / -v / -V to avoid full DI container spin-up.
// `args` is the implicit top-level parameter — equivalent to Environment.GetCommandLineArgs().Skip(1).
if (args.Length == 1 && args[0] is "--version" or "-v" or "-V")
{
    Console.WriteLine($"{ClaudeCodeInfo.Version} (ClaudeCode C#)");
    return 0;
}

var services = new ServiceCollection();

// Configuration services
var cwd = Environment.CurrentDirectory;
var configProvider = new ClaudeCode.Configuration.ConfigProvider(cwd);
services.AddSingleton<ClaudeCode.Configuration.IConfigProvider>(configProvider);
services.AddSingleton(sp => sp.GetRequiredService<ClaudeCode.Configuration.IConfigProvider>().Settings);
services.AddSingleton(sp => sp.GetRequiredService<ClaudeCode.Configuration.IConfigProvider>().GlobalConfig);

// Initialise feature flags from GlobalConfig + env vars.
ClaudeCode.Configuration.FeatureFlags.Load(configProvider.GlobalConfig);

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
// System tools
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

// MCP server manager
services.AddSingleton<ClaudeCode.Mcp.McpServerManager>();

// Permission services
services.AddSingleton<ClaudeCode.Permissions.IPermissionEvaluator, ClaudeCode.Permissions.PermissionEvaluator>();
services.AddSingleton<ClaudeCode.Cli.IPermissionDialog, ClaudeCode.Cli.SpectrePermissionDialog>();

// API services
services.AddHttpClient();
services.AddSingleton<CostTracker>();
services.AddSingleton<ApiProviderConfig>(sp =>
{
    var config = sp.GetRequiredService<ClaudeCode.Configuration.IConfigProvider>();
    return ApiProviderFactory.Detect(config.GlobalConfig.PrimaryApiKey);
});
services.AddSingleton<IAnthropicClient>(sp =>
{
    var providerConfig = sp.GetRequiredService<ApiProviderConfig>();
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient();
    httpClient.Timeout = TimeSpan.FromMilliseconds(ApiConstants.DefaultTimeoutMs);
    return ApiProviderFactory.CreateClient(httpClient, providerConfig);
});

// ── Extended services ──────────────────────────────────────────────────────
// Services are registered unconditionally; each guards itself with FeatureFlags
// so they are no-ops when the corresponding flag is off.

services.AddSingleton<ClaudeCode.Services.AwaySummary.AwaySummaryService>();

services.AddSingleton<ClaudeCode.Services.Notifications.NotificationService>(
    _ => new ClaudeCode.Services.Notifications.NotificationService(null));

services.AddSingleton<ClaudeCode.Services.Sleep.PreventSleepService>();

services.AddSingleton<ClaudeCode.Services.PolicyLimits.PolicyLimitsService>();

services.AddSingleton<ClaudeCode.Services.Diagnostics.IDiagnosticProvider>(
    _ => ClaudeCode.Services.Diagnostics.NullDiagnosticProvider.Instance);
services.AddSingleton<ClaudeCode.Services.Diagnostics.DiagnosticTrackingService>();

services.AddSingleton<ClaudeCode.Services.SettingsSync.SettingsSyncService>();

services.AddSingleton<ClaudeCode.Services.Lsp.LspDiagnosticRegistry>();
services.AddSingleton<ClaudeCode.Services.Lsp.LspServerManager>();

// TeamMemorySyncService has a manual constructor — wire via factory.
services.AddSingleton<ClaudeCode.Services.TeamMemorySync.TeamMemorySyncService>(sp =>
{
    var providerConfig = sp.GetRequiredService<ApiProviderConfig>();
    var httpFactory    = sp.GetRequiredService<IHttpClientFactory>();
    var memDir = Path.Combine(
        ClaudeCode.Configuration.ConfigPaths.ClaudeHomeDir, "memory", "team");
    return new ClaudeCode.Services.TeamMemorySync.TeamMemorySyncService(
        httpFactory.CreateClient(),
        providerConfig.BaseUrl,
        providerConfig.ApiKey,
        memDir);
});

var registrar = new TypeRegistrar(services);
var app = new CommandApp<ChatCommand>(registrar);

app.Configure(config =>
{
    config.SetApplicationName("claude");
    config.SetApplicationVersion(ClaudeCodeInfo.Version);
});

return app.Run(args);

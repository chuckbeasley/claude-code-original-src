namespace ClaudeCode.Cli;

using ClaudeCode.Cli.Repl;
using ClaudeCode.Configuration;
using ClaudeCode.Core.Tools;
using ClaudeCode.Mcp;
using ClaudeCode.Permissions;
using ClaudeCode.Services.Api;
using ClaudeCode.Services.Engine;
using ClaudeCode.Tools.Agent;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

/// <summary>
/// Top-level CLI command: handles one-shot prompts and will host the interactive REPL.
/// </summary>
public sealed class ChatCommand : AsyncCommand<ChatCommand.Settings>
{
    private const int DefaultMaxTokens = 16384;

    private readonly IAnthropicClient _client;
    private readonly CostTracker _costTracker;
    private readonly IConfigProvider _configProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IPermissionDialog _permissionDialog;
    private readonly McpServerManager _mcpManager;

    public ChatCommand(
        IAnthropicClient client,
        CostTracker costTracker,
        IConfigProvider configProvider,
        ToolRegistry toolRegistry,
        IPermissionEvaluator permissionEvaluator,
        IPermissionDialog permissionDialog,
        McpServerManager mcpManager)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _permissionEvaluator = permissionEvaluator ?? throw new ArgumentNullException(nameof(permissionEvaluator));
        _permissionDialog = permissionDialog ?? throw new ArgumentNullException(nameof(permissionDialog));
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
    }

    /// <summary>
    /// CLI settings bound from command-line arguments and options.
    /// </summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The prompt text to send to Claude.</summary>
        [Description("The prompt to send to Claude")]
        [CommandArgument(0, "[prompt]")]
        public string? Prompt { get; init; }

        /// <summary>Model override; accepts full model IDs or short aliases (sonnet, opus, haiku).</summary>
        [Description("Model to use")]
        [CommandOption("-m|--model")]
        public string? Model { get; init; }

        /// <summary>When set, prints the version string and exits immediately.</summary>
        [Description("Print version")]
        [CommandOption("--version")]
        public bool Version { get; init; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (settings.Version)
        {
            AnsiConsole.WriteLine($"{ClaudeCodeInfo.Version} (ClaudeCode C#)");
            return 0;
        }

        if (settings.Prompt is not null)
        {
            return await RunOneShotAsync(settings).ConfigureAwait(false);
        }

        // Interactive REPL mode
        var repl = new ReplSession(
            _client, _costTracker, _configProvider, _toolRegistry,
            _permissionEvaluator, _permissionDialog, _mcpManager);
        return await repl.RunAsync(settings.Model).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private async Task<int> RunOneShotAsync(Settings settings)
    {
        // API key guard — checked early before any heavy work.
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ANTHROPIC_API_KEY environment variable is not set.");
            return 1;
        }

        var model = ModelResolver.Resolve(settings.Model, _configProvider.Settings.Model);
        var cwd = Environment.CurrentDirectory;

        // Register AgentTool now that runtime services and cwd are available.
        var agentDefs = AgentDefinitionLoader.LoadFromDirectory(cwd);
        _toolRegistry.Register(new AgentTool(_client, _costTracker, _toolRegistry, agentDefs));

        AnsiConsole.MarkupLine($"[grey]Model:[/] {ModelResolver.GetDisplayName(model).EscapeMarkup()}");
        AnsiConsole.WriteLine();

        var promptBuilder = new SystemPromptBuilder();
        var config = new QueryEngineConfig(
            Model: model,
            Cwd: cwd,
            CustomSystemPrompt: null,
            MaxTokens: DefaultMaxTokens,
            Tools: _toolRegistry,
            PermissionEvaluator: _permissionEvaluator,
            PermissionDialog: _permissionDialog.AskAsync,
            ThinkingBudgetTokens: (_configProvider.Settings.AlwaysThinkingEnabled == true) ? 8000 : 0);
        var engine = new QueryEngine(_client, _costTracker, promptBuilder, config);

        var renderer = new ResponseRenderer();
        try
        {
            await foreach (var evt in engine.SubmitAsync(settings.Prompt!).ConfigureAwait(false))
            {
                renderer.HandleEvent(evt);
            }
            renderer.EndTurn();
            AnsiConsole.MarkupLine($"[grey]{_costTracker.FormatUsageSummary().EscapeMarkup()}[/]");
        }
        catch (AnthropicApiException ex)
        {
            AnsiConsole.MarkupLine($"[red]API Error ({ex.StatusCode}):[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Network Error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }

        return 0;
    }
}

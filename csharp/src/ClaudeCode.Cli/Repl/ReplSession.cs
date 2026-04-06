namespace ClaudeCode.Cli.Repl;

using ClaudeCode.Cli.Bridge;
using ClaudeCode.Cli.Vim;
using ClaudeCode.Commands;
using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;
using ClaudeCode.Core.State;
using ClaudeCode.Core.Tools;
using ClaudeCode.Mcp;
using ClaudeCode.Permissions;
using ClaudeCode.Services.Api;
using ClaudeCode.Services.Engine;
using ClaudeCode.Services.Plugins;
using ClaudeCode.Services.Session;
using ClaudeCode.Tools.Agent;
using Spectre.Console;
using System.Text.Json;

/// <summary>
/// Manages the interactive REPL loop: reads user input, submits it to <see cref="QueryEngine"/>,
/// and renders streamed responses via <see cref="ResponseRenderer"/>.
/// The conversation history persists across turns for a multi-turn session.
/// </summary>
public sealed class ReplSession
{
    private const int DefaultMaxTokens = 16384;

    private readonly IAnthropicClient _client;
    private readonly CostTracker _costTracker;
    private readonly IConfigProvider _configProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IPermissionDialog _permissionDialog;
    private readonly McpServerManager _mcpManager;
    private readonly ResponseRenderer _renderer;
    private readonly CommandRegistry _commandRegistry;
    private readonly PluginLoader _pluginLoader = new();
    private QueryEngine? _engine;
    private string _lastAssistantResponse = string.Empty;
    private string _currentTheme = "default";
    private string _activeModel = string.Empty;  // set during RunAsync after model resolution
    private string _cwd = string.Empty;           // set early in RunAsync; used by ReadInput
    private BridgeServer? _bridgeServer;          // created in RunAsync after engine is initialised
    private ClaudeCode.Services.Memory.SessionMemory _sessionMemory = new();
    private ClaudeCode.Services.Memory.MemoryExtractor? _memoryExtractor;
    private ClaudeCode.Services.AutoDream.AutoDreamService? _autoDream;
    private ClaudeCode.Services.Memory.SessionMemoryService? _sessionMemoryService;
    private ClaudeCode.Services.Memory.MemoryExtractorService? _memoryExtractorService;
    private ClaudeCode.Services.AgentSummary.AgentSummaryService? _agentSummaryService;
    private ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService? _toolUsageSummary;
    private string? _pendingNextPrompt;  // set by /autofix-pr et al. via CommandContext.SetNextPrompt
    private string? _promptSuggestion;   // ghost-text hint for the next REPL input
    private ClaudeCode.Services.PromptSuggestion.PromptSuggestionService? _promptSuggestionSvc;
    private ClaudeCode.Services.Voice.VoiceInputService? _voiceInputService;
    private ClaudeCode.Services.AutoDream.BuddyService? _buddySvc;
    private Task<string?>? _buddyTask;
    private readonly HashSet<string> _pluginCommandNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new <see cref="ReplSession"/> with all required dependencies.
    /// </summary>
    public ReplSession(
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
        _renderer = new ResponseRenderer();
        _commandRegistry = BuildCommandRegistry();
    }

    /// <summary>
    /// Runs the REPL loop until the user exits or EOF is reached.
    /// </summary>
    /// <param name="modelOverride">Optional model override from the CLI --model flag.</param>
    /// <returns>An exit code: 0 for normal exit.</returns>
    public async Task<int> RunAsync(string? modelOverride = null)
    {
        var model = ModelResolver.Resolve(modelOverride, _configProvider.Settings.Model);
        _activeModel = model;
        var cwd = Environment.CurrentDirectory;
        _cwd = cwd;

        // Register AgentTool now that we have runtime services and the cwd is resolved.
        // This is done here (not in the constructor) because AgentTool needs the live
        // IAnthropicClient and CostTracker instances, and agent definitions require cwd.
        var agentDefs = AgentDefinitionLoader.LoadFromDirectory(cwd);
        _toolRegistry.Register(new AgentTool(_client, _costTracker, _toolRegistry, agentDefs));

        // Initialise CTS early so all startup tasks share a single cancellation scope.
        using var cts = new CancellationTokenSource();

        // Launch keychain, MDM, and MCP startup tasks in parallel to minimise startup latency.
        var keychainTask = LoadApiKeyFromKeychainAsync(cts.Token);
        var mdmTask      = MdmSettingsLoader.TryLoadAsync(cts.Token);
        var mcpTask      = ConnectMcpServersAsync(_mcpManager, _configProvider.Settings, _toolRegistry, cwd);

        await Task.WhenAll(keychainTask, mdmTask, mcpTask).ConfigureAwait(false);

        // Apply keychain API key when the environment variable is not already set.
        var apiKey = await keychainTask;
        if (apiKey is not null)
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);

        // Apply any MDM-managed settings as CLAUDE_-prefixed environment variables.
        var mdmSettings = await mdmTask;
        if (mdmSettings is not null)
        {
            foreach (var (key, value) in mdmSettings)
                Environment.SetEnvironmentVariable($"CLAUDE_{key}", value);
        }

        // Load and register plugins from global and project-local directories.
        _pluginLoader.LoadAndRegisterAll(cwd, _toolRegistry);
        LoadPluginCommands(cwd); // register plugin slash commands

        PrintBanner(model);

        // Show a rotating tip at session start — once every 3 sessions via file counter.
        var tip = ClaudeCode.Services.Tips.TipsService.GetNextTip();
        if (tip is not null)
            AnsiConsole.MarkupLine($"[grey]{tip}[/]");

        // Initialise session-scoped memory extractor for automatic fact extraction.
        _memoryExtractor = new ClaudeCode.Services.Memory.MemoryExtractor(_sessionMemory);

        // Initialise new services for this session.
        _sessionMemoryService = new ClaudeCode.Services.Memory.SessionMemoryService();
        _agentSummaryService  = new ClaudeCode.Services.AgentSummary.AgentSummaryService(_client);
        _toolUsageSummary     = new ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService();
        _memoryExtractorService = new ClaudeCode.Services.Memory.MemoryExtractorService(
            _client, _sessionMemoryService, new ClaudeCode.Services.Memory.MemoryStore(cwd));

        var config = new QueryEngineConfig(
            Model: model,
            Cwd: cwd,
            CustomSystemPrompt: null,
            MaxTokens: DefaultMaxTokens,
            Tools: _toolRegistry,
            PermissionEvaluator: _permissionEvaluator,
            PermissionDialog: _permissionDialog.AskAsync,
            McpManager: _mcpManager,
            QuestionDialog: async questions =>
            {
                var answers = new Dictionary<string, string>();
                await Task.Yield(); // ensure we're on a thread that allows Console I/O

                foreach (var questionEl in questions.EnumerateArray())
                {
                    var questionText = questionEl.TryGetProperty("question", out var qt)
                        ? qt.GetString() ?? "?"
                        : questionEl.ValueKind == JsonValueKind.String
                            ? questionEl.GetString() ?? "?"
                            : "?";

                    var isMultiSelect = questionEl.TryGetProperty("multiSelect", out var ms) && ms.GetBoolean();

                    if (questionEl.TryGetProperty("options", out var optionsEl) &&
                        optionsEl.ValueKind == JsonValueKind.Array &&
                        optionsEl.GetArrayLength() > 0)
                    {
                        // Build option list
                        var optionLabels = new List<string>();
                        foreach (var opt in optionsEl.EnumerateArray())
                        {
                            var label = opt.TryGetProperty("label", out var l) ? l.GetString() ?? "" : opt.GetString() ?? "";
                            optionLabels.Add(label);
                        }
                        // Always add "Other" as last option
                        optionLabels.Add("Other");

                        if (isMultiSelect)
                        {
                            var selected = AnsiConsole.Prompt(
                                new MultiSelectionPrompt<string>()
                                    .Title(questionText)
                                    .AddChoices(optionLabels));
                            answers[questionText] = JsonSerializer.Serialize(selected);
                        }
                        else
                        {
                            var selected = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title(questionText)
                                    .AddChoices(optionLabels));

                            if (selected == "Other")
                            {
                                var custom = AnsiConsole.Ask<string>("Your answer:");
                                answers[questionText] = custom;
                            }
                            else
                            {
                                answers[questionText] = selected;
                            }
                        }
                    }
                    else
                    {
                        var freeText = AnsiConsole.Ask<string>(questionText);
                        answers[questionText] = freeText;
                    }
                }

                return JsonSerializer.Serialize(answers);
            },
            ThinkingBudgetTokens: (_configProvider.Settings.AlwaysThinkingEnabled == true) ? 8000 : 0,
            HookRunner: BuildHookRunner(_configProvider.Settings),
            EffortLevel: EffortCommand.ActiveEffortLevel,
            AdvisorModel: AdvisorCommand.ActiveAdvisorModel,
            ExtraDirectories: AddDirCommand.ExtraDirectories,
            CoordinatorMode: ClaudeCode.Services.Coordinator.CoordinatorMode.IsEnabled,
            SessionMemory: _sessionMemory,
            SessionMemoryService: _sessionMemoryService,
            AgentSummaryService: _agentSummaryService,
            ToolUsageSummary: _toolUsageSummary);
        _engine = new QueryEngine(_client, _costTracker, new SystemPromptBuilder(), config);

        // Create the bridge server. It is not started automatically — the user must run
        // `/bridge start`. Queries are routed directly through the active QueryEngine.
        _bridgeServer = new BridgeServer(
            queryFunc:   (text, bct) => _engine!.SubmitAsync(text, bct),
            statusFunc:  () => (_activeModel, _engine?.SessionId ?? string.Empty));

        // Start the background AutoDream service — runs lightweight idle tasks every 5 minutes.
        _autoDream = new ClaudeCode.Services.AutoDream.AutoDreamService(cwd);
        _autoDream.Start();

        // Initialise BuddyService — generates brief context notes after each assistant turn.
        _buddySvc = new ClaudeCode.Services.AutoDream.BuddyService(_client, _costTracker);

        var inputHistory = new InputHistory();
        // Initialise prompt suggestion service when the feature is enabled.
        if (ClaudeCode.Services.PromptSuggestion.PromptSuggestionService.IsEnabled())
            _promptSuggestionSvc = new ClaudeCode.Services.PromptSuggestion.PromptSuggestionService(
                _client, _activeModel);


        // Outer Ctrl+C cancels the entire session.
        ConsoleCancelEventHandler outerCancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += outerCancelHandler;

        // Start the cron scheduler in the background
        var cronCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = Task.Run(() => ClaudeCode.Tools.Cron.CronScheduler.RunAsync(cronCts.Token), CancellationToken.None);

        try
        {
            // Fire-and-forget update check; completes in the background, never blocks the REPL.
            _ = ClaudeCode.Services.Updates.UpdateChecker.CheckAndPrintAsync(
                ClaudeCode.Cli.ClaudeCodeInfo.Version, cts.Token);

            // Tracks how many AutoDream results have already been shown so we only print new ones.
            var lastDreamTaskCount = 0;

            while (!cts.IsCancellationRequested)
            {
                // Notify AutoDream that the user is active, resetting the idle countdown.
                _autoDream?.NotifyActivity();

                // Check for scheduled cron jobs that fired while waiting
                string? scheduledInput = null;
                if (ClaudeCode.Tools.Cron.CronState.PendingPrompts.Reader.TryRead(out var pending))
                    scheduledInput = pending;

                var input = scheduledInput ?? ReadInput(inputHistory);
                if (input is null) break;  // EOF (Ctrl+D)

                var trimmed = input.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith('/'))
                {
                    var exitRequested = await HandleSlashCommandAsync(trimmed, _activeModel, cwd, cts.Token)
                        .ConfigureAwait(false);
                    if (exitRequested) break;
                    // A command (e.g. /autofix-pr) may have set a follow-up prompt.
                    if (_pendingNextPrompt is not null)
                    {
                        var next = _pendingNextPrompt;
                        _pendingNextPrompt = null;
                        await SubmitTurnAsync(next, _activeModel, cts.Token).ConfigureAwait(false);
                        if (cts.IsCancellationRequested) break;
                    }
                    continue;
                }

                // Append any pending /btw question to this user turn before submitting.
                var turnInput = trimmed;
                var pendingBtw = BtwCommand.ConsumePendingBtw();
                if (pendingBtw is not null)
                    turnInput = turnInput + "\n\nBy the way: " + pendingBtw;

                // KAIROS numbered-selection shortcut: if KAIROS mode is active and the entire
                // input is a bare integer, prefix it with "Select option ".
                if (ReplModeFlags.KairosEnabled
                    && System.Text.RegularExpressions.Regex.IsMatch(turnInput.Trim(), @"^\d+$"))
                {
                    turnInput = $"Select option {turnInput.Trim()}";
                }

                // Expand @-mentions (file, git diff, etc.) before sending to the model.
                var expandedInput = await MentionResolver.ExpandAsync(turnInput, _cwd, cts.Token).ConfigureAwait(false);

                await SubmitTurnAsync(expandedInput, _activeModel, cts.Token).ConfigureAwait(false);

                // Exit the outer loop if the session-level CTS was triggered during the turn.
                if (cts.IsCancellationRequested) break;
            }
        }
        finally
        {
            Console.CancelKeyPress -= outerCancelHandler;
            _voiceInputService?.Dispose();
            _bridgeServer?.Dispose();
            if (_autoDream != null) await _autoDream.DisposeAsync().ConfigureAwait(false);
        }

        // Auto-save session on exit so it can be resumed later.
        if (_engine is not null && _engine.Messages.Count > 0)
        {
            try
            {
                var store = new SessionStore();
                await store.SaveAsync(
                    _engine.SessionId,
                    [.. _engine.Messages],
                    model,
                    cwd,
                    _costTracker.TotalCostUsd,
                    TagCommand.ActiveTags.ToList()).ConfigureAwait(false);
            }
            catch
            {
                // Session save is best-effort; never fail the exit path.
            }
        }

        AnsiConsole.MarkupLine($"\n[grey]Session cost: {_costTracker.FormatCost()}[/]");
        return 0;
    }

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds and returns the <see cref="CommandRegistry"/> pre-populated with all built-in commands.
    /// </summary>
    private static CommandRegistry BuildCommandRegistry()
    {
        var registry = new CommandRegistry();

        // HelpCommand is registered first so its registry reference is valid
        // when the other commands are added — the registry is already passed in.
        var help = new HelpCommand(registry);
        registry.Register(help);
        registry.Register(new ClearCommand());
        registry.Register(new CostCommand());
        registry.Register(new ModelCommand());
        registry.Register(new StatusCommand());
        registry.Register(new VersionCommand());
        registry.Register(new ExitCommand());
        registry.Register(new DiffCommand());
        registry.Register(new CompactCommand());
        registry.Register(new ConfigCommand());
        registry.Register(new ResumeCommand());
        registry.Register(new CommitCommand());
        registry.Register(new MemoryCommand());
        registry.Register(new SkillsCommand());
        // Git/session/auth commands
        registry.Register(new BranchCommand());
        registry.Register(new AddDirCommand());
        registry.Register(new ContextCommand());
        registry.Register(new DoctorCommand());
        registry.Register(new EnvCommand());
        registry.Register(new ExportCommand());
        registry.Register(new LoginCommand());
        registry.Register(new LogoutCommand());
        registry.Register(new OAuthRefreshCommand());
        registry.Register(new SessionCommand());
        registry.Register(new ShareCommand());
        registry.Register(new ReviewCommand());
        registry.Register(new PrCommentsCommand());
        // Mode/config/UI commands
        registry.Register(new FastCommand());
        registry.Register(new PermissionsCommand());
        registry.Register(new PlanCommand());
        registry.Register(new TasksCommand());
        registry.Register(new ThemeCommand());
        registry.Register(new VimCommand());
        registry.Register(new HooksCommand());
        registry.Register(new McpCommand());
        registry.Register(new FilesCommand());
        registry.Register(new InitCommand());
        registry.Register(new FeedbackCommand());
        registry.Register(new CopyCommand());
        // Batch 1: git/session/config/integration commands
        registry.Register(new AdvisorCommand());
        registry.Register(new AgentsCommand());
        registry.Register(new BtwCommand());
        registry.Register(new ChromeCommand());
        registry.Register(new ColorCommand());
        registry.Register(new CommitPushPrCommand());
        registry.Register(new DesktopCommand());
        registry.Register(new EffortCommand());
        registry.Register(new ExtraUsageCommand());
        registry.Register(new HeapdumpCommand());
        registry.Register(new IdeCommand());
        registry.Register(new InitVerifiersCommand());
        registry.Register(new InsightsCommand());
        registry.Register(new InstallGithubAppCommand());
        registry.Register(new InstallSlackAppCommand());
        registry.Register(new KeybindingsCommand());
        registry.Register(new MobileCommand());
        registry.Register(new OutputStyleCommand());
        registry.Register(new PassesCommand());
        registry.Register(new PluginCommand());
        // Privacy / rate-limit / release / plugin commands
        registry.Register(new PrivacySettingsCommand());
        registry.Register(new RateLimitOptionsCommand());
        registry.Register(new ReleaseNotesCommand());
        registry.Register(new ReloadPluginsCommand());
        registry.Register(new RemoteEnvCommand());
        // Conversation management
        registry.Register(new RenameCommand());
        registry.Register(new RewindCommand());
        // Mode toggles
        registry.Register(new SandboxToggleCommand());
        registry.Register(new BriefCommand());
        // Review / stats commands
        registry.Register(new SecurityReviewCommand());
        registry.Register(new StatsCommand());
        registry.Register(new UsageCommand());
        // Informational / fun commands
        registry.Register(new StickersCommand());
        registry.Register(new TerminalSetupCommand());
        registry.Register(new UpgradeCommand());
        registry.Register(new VoiceCommand());
        registry.Register(new BridgeCommand());
        registry.Register(new TagCommand());
        registry.Register(new ThinkbackCommand());
        // 6 previously missing public commands
        registry.Register(new UltrareviewCommand());
        registry.Register(new StatuslineCommand());
        registry.Register(new ThinkbackPlayCommand());
        registry.Register(new UsageReportCommand());
        registry.Register(new ContextNonInteractiveCommand());
        registry.Register(new InstallCommand());
        registry.Register(new CoordinatorCommand());
        // New commands: ultraplan, summary, issue, onboarding, remote-setup, autofix-pr
        registry.Register(new UltraPlanCommand());
        registry.Register(new SummaryCommand());
        registry.Register(new IssueCommand());
        registry.Register(new OnboardingCommand());
        registry.Register(new RemoteSetupCommand());
        registry.Register(new AutofixPrCommand());
        registry.Register(new AssistantCommand());
        registry.Register(new BuddyCommand());

        return registry;
    }

    /// <summary>
    /// Looks up the command in the registry and executes it. Writes an error for unknown commands.
    /// </summary>
    /// <returns><see langword="true"/> when the command requested session exit; otherwise <see langword="false"/>.</returns>
    private async Task<bool> HandleSlashCommandAsync(
        string rawInput,
        string model,
        string cwd,
        CancellationToken ct)
    {
        // Tokenise: first token is the command name, rest are args.
        var tokens = rawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var commandName = tokens[0];
        var args = tokens.Length > 1 ? tokens[1..] : [];

        var command = _commandRegistry.Get(commandName);
        if (command is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Unknown command:[/] {commandName.EscapeMarkup()}. Type [blue]/help[/] for available commands.");
            return false;
        }

        var exitRequested = false;

        var ctx = new CommandContext
        {
            RawInput = rawInput,
            Args = args,
            Cwd = cwd,
            Write = text => AnsiConsole.WriteLine(text),
            WriteMarkup = markup => AnsiConsole.MarkupLine(markup),
            CostTracker = _costTracker,
            ConfigProvider = _configProvider,
            CurrentModel = model,
            ConversationMessageCount = _engine?.Messages.Count ?? 0,
            Version = ClaudeCodeInfo.Version,
            ClearScreen = AnsiConsole.Clear,
            RequestExit = () => exitRequested = true,
            CompactFunc = async ct => await _engine!.CompactAsync(ct).ConfigureAwait(false),
            RestoreSession = messages => _engine!.RestoreMessages(messages),
            LastAssistantResponse = _lastAssistantResponse,
            ConversationMessages = _engine?.Messages,
            SwitchModel = newModel => _activeModel = newModel,
            CurrentTheme = _currentTheme,
            SwitchTheme = theme => ApplyTheme(theme),
            ReloadPlugins = () => _pluginLoader.LoadAndRegisterAll(cwd, _toolRegistry),
            ReloadPluginsAndCommands = () =>
            {
                _pluginLoader.LoadAndRegisterAll(cwd, _toolRegistry);
                LoadPluginCommands(cwd);
            },
            PluginCommandNames = _pluginCommandNames.ToList(),
            SetPendingBtw = BtwCommand.SetPendingBtwValue,
            BridgeStart     = startCt => _bridgeServer!.StartAsync(startCt),
            BridgeStop      = () => _bridgeServer!.StopAsync(),
            BridgeGetStatus = () => (_bridgeServer?.IsRunning ?? false,
                                     _bridgeServer?.Port ?? 0,
                                     _bridgeServer?.Token ?? string.Empty),
            McpAllow = (server, tool) => _mcpManager.ChannelPermissions.Allow(server, tool),
            McpDeny  = (server, tool) => _mcpManager.ChannelPermissions.Deny(server, tool),
            ToolUsageSummary = _toolUsageSummary,
            AnthropicClient  = _client,
            SubmitTurn       = async (text, token) => await SubmitTurnAsync(text, _activeModel, token).ConfigureAwait(false),
            SessionId        = _engine?.SessionId,
            Memory           = _sessionMemory,
            SetNextPrompt    = prompt => _pendingNextPrompt = prompt,
            ToggleVoiceInput = enabled => ToggleVoiceInput(enabled),
        };

        await command.ExecuteAsync(ctx, ct).ConfigureAwait(false);

        return exitRequested;
    }

    /// <summary>
    /// Submits a single user turn to the engine, rendering events as they arrive.
    /// Per-turn Ctrl+C cancels only the current turn via a linked <see cref="CancellationTokenSource"/>.
    /// </summary>
    private async Task SubmitTurnAsync(string userInput, string model, CancellationToken sessionToken)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);

        ConsoleCancelEventHandler cancelTurnHandler = (_, e) =>
        {
            e.Cancel = true;
            turnCts.Cancel();
        };
        Console.CancelKeyPress += cancelTurnHandler;

        try
        {
            var turnText = new System.Text.StringBuilder();
            await foreach (var evt in _engine!.SubmitAsync(userInput, turnCts.Token).ConfigureAwait(false))
            {
                if (evt is TextDeltaEvent textEvt)
                    turnText.Append(textEvt.Text);
                _renderer.HandleEvent(evt);
            }
            _renderer.EndTurn();
            _lastAssistantResponse = turnText.ToString();
            PrintStatusLine(model);

            // Fire-and-forget memory extraction — runs every N turns in the background.
            _ = _memoryExtractorService?.MaybeExtractAsync(_engine!.Messages, CancellationToken.None);

            // Fire-and-forget prompt suggestion — generates ghost text for the next prompt.
            if (_promptSuggestionSvc is not null && _engine?.Messages is { Count: > 0 } msgs)
            {
                _ = Task.Run(async () =>
                {
                    using var suggestionCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    _promptSuggestion = await _promptSuggestionSvc
                        .GenerateAsync(msgs, suggestionCts.Token)
                        .ConfigureAwait(false);
                }, CancellationToken.None);
            }

            // Speak the response aloud when voice mode is active.
            // Fire-and-forget so the REPL prompt returns immediately while TTS plays.
            if (ReplModeFlags.VoiceMode && _lastAssistantResponse.Length > 0)
                _ = VoiceCommand.SpeakAsync(_lastAssistantResponse, sessionToken);

            // Fire buddy note generation for next-prompt display.
            if (ClaudeCode.Core.State.ReplModeFlags.BuddyEnabled && _buddySvc is not null)
            {
                var buddyMsgs = _engine?.Messages ?? Array.Empty<MessageParam>();
                _buddyTask = _buddySvc.GetContextNoteAsync(buddyMsgs, sessionToken);
            }
        }
        catch (OperationCanceledException)
        {
            _renderer.EndTurn();
            AnsiConsole.MarkupLine("\n[yellow]Interrupted.[/]");
        }
        catch (AnthropicApiException ex)
        {
            _renderer.EndTurn();
            AnsiConsole.MarkupLine($"\n[red]API Error ({ex.StatusCode}):[/] {ex.Message.EscapeMarkup()}");
        }
        catch (HttpRequestException ex)
        {
            _renderer.EndTurn();
            AnsiConsole.MarkupLine($"\n[red]Network Error:[/] {ex.Message.EscapeMarkup()}");
        }
        finally
        {
            Console.CancelKeyPress -= cancelTurnHandler;
        }
    }

    /// <summary>
    /// Connects to all configured MCP servers. Supports stdio, HTTP/SSE, and WebSocket transports.
    /// Registers the discovered tools in <paramref name="registry"/> for use during the session.
    /// </summary>
    /// <param name="manager">The <see cref="McpServerManager"/> that owns server connections.</param>
    /// <param name="settings">The merged settings for the current session.</param>
    /// <param name="registry">The tool registry in which discovered MCP tools will be registered.</param>
    /// <param name="cwd">Current working directory (used to resolve relative server paths).</param>
    private static async Task ConnectMcpServersAsync(
        McpServerManager manager,
        SettingsJson settings,
        ToolRegistry registry,
        string cwd)
    {
        var servers = settings.McpServers;
        if (servers is null || servers.Count == 0)
            return;

        foreach (var (name, entry) in servers)
        {
            if (entry.Disabled == true)
                continue;

            try
            {
                var transportType = (entry.Type ?? "stdio").ToLowerInvariant();
                ClaudeCode.Mcp.McpClient client;

                if (transportType is "sse" or "http")
                {
                    if (string.IsNullOrWhiteSpace(entry.Url))
                    {
                        AnsiConsole.MarkupLine($"[yellow][[MCP]] Server '{name.EscapeMarkup()}': 'url' is required for {transportType.EscapeMarkup()} transport.[/]");
                        continue;
                    }

                    client = await ClaudeCode.Mcp.McpClient.ConnectHttpAsync(
                        name, entry.Url, entry.Headers, entry.ApiKey)
                        .ConfigureAwait(false);
                }
                else if (transportType is "ws" or "websocket")
                {
                    if (string.IsNullOrWhiteSpace(entry.Url))
                    {
                        AnsiConsole.MarkupLine($"[yellow][[MCP]] Server '{name.EscapeMarkup()}': 'url' is required for WebSocket transport.[/]");
                        continue;
                    }

                    var wsTransport = await ClaudeCode.Mcp.Transport.WebSocketTransport.ConnectAsync(
                        entry.Url, entry.Headers).ConfigureAwait(false);
                    client = await ClaudeCode.Mcp.McpClient.ConnectWithTransportAsync(
                        name, wsTransport).ConfigureAwait(false);
                }
                else
                {
                    // stdio (default)
                    if (string.IsNullOrWhiteSpace(entry.Command))
                    {
                        AnsiConsole.MarkupLine($"[yellow][[MCP]] Server '{name.EscapeMarkup()}': 'command' is required for stdio transport.[/]");
                        continue;
                    }

                    var config = new ClaudeCode.Mcp.McpServerConfig(
                        Command: entry.Command,
                        Args: entry.Args ?? [],
                        WorkingDir: entry.WorkingDir ?? cwd,
                        Env: entry.Env);

                    client = await manager.ConnectAsync(name, config).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(entry.AuthorizationUrl))
                    manager.SetAuthorizationUrl(name, entry.AuthorizationUrl);

                // Register the full entry config so McpAuthTool can look up OAuth fields.
                manager.RegisterEntryConfig(name, entry);

                // Apply channel-level tool permissions from the server entry when specified.
                if (entry.AllowedTools is { Count: > 0 })
                    manager.ChannelPermissions.SetAllowList(name, entry.AllowedTools);
                if (entry.BlockedTools is { Count: > 0 })
                    manager.ChannelPermissions.SetBlockList(name, entry.BlockedTools);

                // Discover tools and register them as McpToolWrapper instances.
                // Pass the manager so McpToolWrapper can enforce channel permissions at call time.
                var tools = await client.ListToolsAsync().ConfigureAwait(false);
                foreach (var toolInfo in tools)
                {
                    var wrapper = new ClaudeCode.Mcp.McpToolWrapper(client, toolInfo, manager);
                    registry.Register(wrapper);
                }

                AnsiConsole.MarkupLine($"[green][[MCP]] Connected to '{name.EscapeMarkup()}' — {tools.Count} tool(s) registered.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red][[MCP]] Failed to connect to server '{name.EscapeMarkup()}': {ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    private string? ReadInput(InputHistory history)
    {
        var buffer = new System.Text.StringBuilder();
        var cursorPos = 0;
        var historyIdx = -1;
        var savedLine = string.Empty;

        // Capture vim mode once per ReadInput call; toggling /vim takes effect on next prompt.
        var vimMode = ReplModeFlags.VimMode;
        var vimState = vimMode ? VimState.NormalAt(0) : VimState.Initial;

        // Resolve the active prompt color once per ReadInput call so that a /color change
        // applied mid-session takes effect on the next prompt without rebuilding the engine.
        var promptColor = GetAnsiColorCode(ColorCommand.ActivePromptColor);

        // Show [MIC] prefix when voice mode is active, or [ASSISTANT] when KAIROS mode is active.
        var voicePrefix = ReplModeFlags.VoiceMode    ? "[MIC] "
                        : ReplModeFlags.KairosEnabled ? "[ASSISTANT] "
                        : "";

        // Show buddy context note from previous turn (if ready).
        if (_buddyTask?.IsCompleted == true)
        {
            var note = _buddyTask.GetAwaiter().GetResult(); // safe: task is already completed
            _buddyTask = null;
            if (!string.IsNullOrWhiteSpace(note))
                AnsiConsole.MarkupLine($"[grey]  ↳ Buddy: {note.EscapeMarkup()}[/]");
        }
        else if (_buddyTask != null)
        {
            // Task not ready in time — discard silently to avoid a display race.
            _buddyTask = null;
        }

        // Show any pending prompt suggestion as a dim hint above the prompt.
        var suggestion = _promptSuggestion;
        _promptSuggestion = null;
        if (!string.IsNullOrWhiteSpace(suggestion))
        {
            Console.WriteLine();
            Console.Write($"\x1b[2m[Tab] {suggestion}\x1b[0m");
        }

        // Write initial prompt with dynamic mode indicator when vim is active.
        if (vimMode)
        {
            var initialTag = vimState.Mode switch
            {
                VimMode.Normal => "[N]",
                VimMode.Visual => "[V]",
                _ => "[I]"
            };
            Console.Write($"\n{promptColor}{initialTag}{promptColor}{voicePrefix}>\x1b[0m ");
        }
        else
        {
            Console.Write($"\n{promptColor}{voicePrefix}>\x1b[0m ");
        }

        // Redraws the entire prompt line with the mode-appropriate indicator and
        // repositions the terminal cursor at cursorPos within the buffer.
        void RedrawCurrentLine()
        {
            string p;
            if (vimMode)
            {
                var modeTag = vimState.Mode switch
                {
                    VimMode.Normal => "[N]",
                    VimMode.Visual => "[V]",
                    _ => "[I]"
                };
                p = $"\r\x1b[2K{promptColor}{modeTag}{promptColor}{voicePrefix}>\x1b[0m ";
            }
            else
            {
                p = $"\r\x1b[2K{promptColor}{voicePrefix}>\x1b[0m ";
            }
            Console.Write(p);
            Console.Write(buffer.ToString());
            var behind = buffer.Length - cursorPos;
            if (behind > 0) Console.Write($"\x1b[{behind}D");
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return null; } // stdin redirected

            // ---------------------------------------------------------------
            // Vim mode: all key input processed through VimInputProcessor.
            // In Insert mode, intercept history/completion/search keys first.
            // ---------------------------------------------------------------
            if (vimMode)
            {
                // Paste detection: multiple chars arriving simultaneously indicate
                // a terminal paste event rather than individual keystrokes.
                if (Console.KeyAvailable && key.KeyChar != '\0')
                {
                    var pasteBuffer = key.KeyChar.ToString();
                    while (Console.KeyAvailable)
                    {
                        var next = Console.ReadKey(true);
                        pasteBuffer += next.KeyChar;
                    }
                    if (pasteBuffer.Contains('\n'))
                    {
                        buffer.Insert(cursorPos, pasteBuffer);
                        cursorPos += pasteBuffer.Length;
                        vimState = vimState with { CursorPos = cursorPos };
                        AnsiConsole.MarkupLine($"\n[grey](pasted {pasteBuffer.Length} chars)[/]");
                        RedrawCurrentLine();
                        continue;
                    }
                    buffer.Insert(cursorPos, pasteBuffer);
                    cursorPos += pasteBuffer.Length;
                    vimState = vimState with { CursorPos = cursorPos };
                    RedrawCurrentLine();
                    continue;
                }

                // In Insert mode only: intercept history navigation, tab completion,
                // and reverse-search before delegating to VimInputProcessor.
                if (vimState.Mode == VimMode.Insert)
                {
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        var entries = history.GetAll();
                        if (entries.Count == 0) continue;
                        if (historyIdx == -1) { savedLine = buffer.ToString(); historyIdx = entries.Count - 1; }
                        else if (historyIdx > 0) historyIdx--;
                        cursorPos = buffer.Length;
                        ReplaceCurrentLineInput(buffer, ref cursorPos, entries[historyIdx]);
                        vimState = vimState with { CursorPos = cursorPos };
                        continue;
                    }

                    if (key.Key == ConsoleKey.DownArrow)
                    {
                        if (historyIdx == -1) continue;
                        var entries = history.GetAll();
                        historyIdx++;
                        var newContent = historyIdx >= entries.Count ? (historyIdx = -1) >= 0 ? "" : savedLine : entries[historyIdx];
                        cursorPos = buffer.Length;
                        ReplaceCurrentLineInput(buffer, ref cursorPos, newContent);
                        vimState = vimState with { CursorPos = cursorPos };
                        continue;
                    }

                    if (key.Key == ConsoleKey.Tab)
                    {
                        var partialInput = buffer.ToString().TrimStart();
                        // Accept prompt suggestion when Tab is pressed on an empty buffer.
                        if (partialInput.Length == 0 && !string.IsNullOrWhiteSpace(suggestion))
                        {
                            buffer.Clear();
                            buffer.Append(suggestion);
                            cursorPos = buffer.Length;
                            suggestion = null;
                            RedrawCurrentLine();
                            continue;
                        }
                        if (partialInput.StartsWith('/') && _commandRegistry is not null)
                        {
                            var matches = _commandRegistry.GetCommandNames()
                                .Where(n => n.StartsWith(partialInput, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(n => n)
                                .ToArray();
                            if (matches.Length == 1)
                            {
                                cursorPos = buffer.Length;
                                ReplaceCurrentLineInput(buffer, ref cursorPos, matches[0]);
                                vimState = vimState with { CursorPos = cursorPos };
                            }
                            else if (matches.Length > 1)
                            {
                                Console.WriteLine();
                                Console.WriteLine(string.Join("  ", matches));
                                Console.Write($"\n{promptColor}[I]{promptColor}{voicePrefix}>\x1b[0m {buffer}");
                            }
                        }
                        else
                        {
                            // File path / @-mention completion
                            var word = GetCurrentWord(buffer.ToString(), cursorPos);
                            if (!word.StartsWith('/'))
                            {
                                var completions = GetFileCompletions(word, _cwd);
                                if (completions.Count == 1)
                                {
                                    ReplaceCurrentWord(ref buffer, ref cursorPos, word, completions[0]);
                                    vimState = vimState with { CursorPos = cursorPos };
                                    RedrawCurrentLine();
                                }
                                else if (completions.Count > 1)
                                {
                                    Console.WriteLine();
                                    AnsiConsole.MarkupLine("[grey]" + string.Join("  ", completions.Select(Markup.Escape)) + "[/]");
                                    RedrawCurrentLine();
                                }
                            }
                        }
                        continue;
                    }

                    if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        Console.Write("\n[ctrl+r] reverse-i-search: ");
                        var searchBuf = new System.Text.StringBuilder();
                        while (true)
                        {
                            var sk = Console.ReadKey(intercept: true);
                            if (sk.Key == ConsoleKey.Enter || sk.Key == ConsoleKey.Escape)
                                break;
                            if (sk.Key == ConsoleKey.Backspace && searchBuf.Length > 0)
                                searchBuf.Remove(searchBuf.Length - 1, 1);
                            else if (!char.IsControl(sk.KeyChar))
                                searchBuf.Append(sk.KeyChar);

                            var searchTerm = searchBuf.ToString();
                            var match = history.FindReverse(searchTerm);
                            Console.Write($"\r[ctrl+r] reverse-i-search '{searchTerm}': {(match ?? "(no match)")}   ");
                            if (match is not null)
                            {
                                cursorPos = buffer.Length;
                                ReplaceCurrentLineInput(buffer, ref cursorPos, match);
                                vimState = vimState with { CursorPos = cursorPos };
                            }
                        }
                        Console.WriteLine();
                        Console.Write($"\n{promptColor}[I]{promptColor}{voicePrefix}>\x1b[0m {buffer}");
                        continue;
                    }
                }

                // Route all remaining keys through the vim engine.
                var (newBuffer, newVimState, submit) = VimInputProcessor.Process(key, buffer.ToString(), vimState);
                buffer.Clear(); buffer.Append(newBuffer);
                vimState = newVimState;
                cursorPos = vimState.CursorPos;

                if (submit)
                {
                    // Multi-line continuation when line ends with backslash.
                    if (buffer.ToString().EndsWith('\\'))
                    {
                        buffer.Remove(buffer.Length - 1, 1);
                        buffer.AppendLine();
                        Console.WriteLine();
                        Console.Write("... ");
                        vimState = vimState with { CursorPos = buffer.Length, Mode = VimMode.Insert };
                        cursorPos = buffer.Length;
                        continue;
                    }
                    Console.WriteLine();
                    var result = buffer.ToString().Trim();
                    if (result.Length > 0) history.Add(result);
                    return result.Length == 0 ? null : result;
                }

                RedrawCurrentLine();
                continue;
            }

            // ---------------------------------------------------------------
            // Paste detection: multiple chars arriving simultaneously indicate
            // a terminal paste event rather than individual keystrokes.
            // ---------------------------------------------------------------
            if (Console.KeyAvailable && key.KeyChar != '\0')
            {
                // Collect all buffered chars
                var pasteBuffer = key.KeyChar.ToString();
                while (Console.KeyAvailable)
                {
                    var next = Console.ReadKey(true);
                    pasteBuffer += next.KeyChar;
                }
                if (pasteBuffer.Contains('\n'))
                {
                    // Multi-line paste: insert as-is with visual indicator
                    buffer.Insert(cursorPos, pasteBuffer);
                    cursorPos += pasteBuffer.Length;
                    AnsiConsole.MarkupLine($"\n[grey](pasted {pasteBuffer.Length} chars)[/]");
                    RedrawCurrentLine();
                    continue;
                }
                // Single-line paste: insert normally
                buffer.Insert(cursorPos, pasteBuffer);
                cursorPos += pasteBuffer.Length;
                RedrawCurrentLine();
                continue;
            }

            // ---------------------------------------------------------------
            // Standard (non-vim) key handling
            // ---------------------------------------------------------------

            if (key.Key == ConsoleKey.Enter)
            {
                // Multi-line continuation when line ends with backslash
                var currentText = buffer.ToString();
                if (currentText.EndsWith('\\'))
                {
                    buffer.Remove(buffer.Length - 1, 1);
                    buffer.AppendLine();
                    Console.WriteLine();
                    Console.Write("... ");
                    cursorPos = 0;
                    continue;
                }

                Console.WriteLine();
                var result = buffer.ToString().Trim();
                if (result.Length > 0) history.Add(result);
                return result.Length == 0 ? null : result;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (cursorPos > 0)
                {
                    buffer.Remove(buffer.Length - 1, 1);
                    cursorPos--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                var entries = history.GetAll();
                if (entries.Count == 0) continue;
                if (historyIdx == -1) { savedLine = buffer.ToString(); historyIdx = entries.Count - 1; }
                else if (historyIdx > 0) historyIdx--;
                ReplaceCurrentLineInput(buffer, ref cursorPos, entries[historyIdx]);
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (historyIdx == -1) continue;
                var entries = history.GetAll();
                historyIdx++;
                var newContent = historyIdx >= entries.Count ? (historyIdx = -1) >= 0 ? "" : savedLine : entries[historyIdx];
                ReplaceCurrentLineInput(buffer, ref cursorPos, newContent);
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var partialInput = buffer.ToString().TrimStart();
                // Accept prompt suggestion when Tab is pressed on an empty buffer.
                if (partialInput.Length == 0 && !string.IsNullOrWhiteSpace(suggestion))
                {
                    buffer.Clear();
                    buffer.Append(suggestion);
                    cursorPos = buffer.Length;
                    suggestion = null;
                    RedrawCurrentLine();
                    continue;
                }
                if (partialInput.StartsWith('/') && _commandRegistry is not null)
                {
                    var matches = _commandRegistry.GetCommandNames()
                        .Where(n => n.StartsWith(partialInput, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(n => n)
                        .ToArray();

                    if (matches.Length == 1)
                    {
                        ReplaceCurrentLineInput(buffer, ref cursorPos, matches[0]);
                    }
                    else if (matches.Length > 1)
                    {
                        Console.WriteLine();
                        Console.WriteLine(string.Join("  ", matches));
                        Console.Write($"\n{promptColor}{voicePrefix}>\x1b[0m " + buffer);
                    }
                }
                else
                {
                    // File path / @-mention completion
                    var word = GetCurrentWord(buffer.ToString(), cursorPos);
                    if (!word.StartsWith('/'))
                    {
                        var completions = GetFileCompletions(word, _cwd);
                        if (completions.Count == 1)
                        {
                            ReplaceCurrentWord(ref buffer, ref cursorPos, word, completions[0]);
                            RedrawCurrentLine();
                        }
                        else if (completions.Count > 1)
                        {
                            Console.WriteLine();
                            AnsiConsole.MarkupLine("[grey]" + string.Join("  ", completions.Select(Markup.Escape)) + "[/]");
                            RedrawCurrentLine();
                        }
                    }
                }
                continue;
            }

            if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.Write("\n[ctrl+r] reverse-i-search: ");
                var searchBuf = new System.Text.StringBuilder();
                while (true)
                {
                    var sk = Console.ReadKey(intercept: true);
                    if (sk.Key == ConsoleKey.Enter || sk.Key == ConsoleKey.Escape)
                        break;
                    if (sk.Key == ConsoleKey.Backspace && searchBuf.Length > 0)
                        searchBuf.Remove(searchBuf.Length - 1, 1);
                    else if (!char.IsControl(sk.KeyChar))
                        searchBuf.Append(sk.KeyChar);

                    var searchTerm = searchBuf.ToString();
                    var match = history.FindReverse(searchTerm);
                    Console.Write($"\r[ctrl+r] reverse-i-search '{searchTerm}': {(match ?? "(no match)")}   ");
                    if (match is not null)
                        ReplaceCurrentLineInput(buffer, ref cursorPos, match);
                }
                Console.WriteLine();
                Console.Write($"\n{promptColor}{voicePrefix}>\x1b[0m " + buffer);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                historyIdx = -1;
                buffer.Append(key.KeyChar);
                cursorPos++;
                Console.Write(key.KeyChar);
            }
        }
    }

    private static void ReplaceCurrentLineInput(
        System.Text.StringBuilder buffer, ref int cursorPos, string newContent)
    {
        Console.Write(new string('\b', cursorPos));
        Console.Write(new string(' ', cursorPos));
        Console.Write(new string('\b', cursorPos));
        var lastNl = buffer.ToString().LastIndexOf('\n');
        if (lastNl >= 0) buffer.Remove(lastNl + 1, buffer.Length - lastNl - 1);
        else buffer.Clear();
        buffer.Append(newContent);
        cursorPos = newContent.Length;
        Console.Write(newContent);
    }

    /// <summary>
    /// Returns the non-whitespace word that ends at <paramref name="pos"/> within <paramref name="buf"/>.
    /// Used for Tab-completion to identify the token being typed.
    /// </summary>
    private static string GetCurrentWord(string buf, int pos)
    {
        var start = pos;
        while (start > 0 && !char.IsWhiteSpace(buf[start - 1])) start--;
        return buf[start..pos];
    }

    /// <summary>
    /// Replaces the word that ends at <paramref name="cursorPos"/> with <paramref name="newWord"/>
    /// and advances <paramref name="cursorPos"/> to the end of the replacement.
    /// </summary>
    private static void ReplaceCurrentWord(
        ref System.Text.StringBuilder sb,
        ref int cursorPos,
        string oldWord,
        string newWord)
    {
        var start = cursorPos - oldWord.Length;
        sb.Remove(start, oldWord.Length);
        sb.Insert(start, newWord);
        cursorPos = start + newWord.Length;
    }

    /// <summary>
    /// Enumerates up to 8 file-system entries whose names begin with <paramref name="partial"/>,
    /// resolved relative to <paramref name="cwd"/>. Directories receive a trailing <c>/</c>.
    /// Returns an empty list on any I/O error.
    /// </summary>
    private static IReadOnlyList<string> GetFileCompletions(string partial, string cwd)
    {
        var dir = Path.GetDirectoryName(partial) ?? "";
        var file = Path.GetFileName(partial);
        var searchDir = string.IsNullOrEmpty(dir) ? cwd : Path.Combine(cwd, dir);
        try
        {
            return Directory.EnumerateFileSystemEntries(searchDir, file + "*")
                .Take(8)
                .Select(p => (Path.GetDirectoryName(partial) is { Length: > 0 } d ? d + "/" : "") +
                             Path.GetFileName(p) +
                             (Directory.Exists(p) ? "/" : ""))
                .ToList();
        }
        catch { return []; }
    }

    private static ClaudeCode.Services.Hooks.HookRunner? BuildHookRunner(
        ClaudeCode.Configuration.Settings.SettingsJson settings)
    {
        if (settings.DisableAllHooks == true) return null;
        if (settings.Hooks is null || settings.Hooks.Count == 0) return null;
        return new ClaudeCode.Services.Hooks.HookRunner(settings);
    }

    /// <summary>
    /// Attempts to read the Anthropic API key from the OS keychain.
    /// Returns the key when the environment variable is absent and the keychain entry exists;
    /// returns <see langword="null"/> in all other cases (env already set, keychain empty, or error).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<string?> LoadApiKeyFromKeychainAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return null;

        try
        {
            var keychain = new ClaudeCode.Services.Keychain.KeychainService();
            return await keychain.GetAsync("anthropic-api-key").ConfigureAwait(false);
        }
        catch
        {
            // Keychain access is best-effort; never block startup on a keychain failure.
            return null;
        }
    }

    /// <summary>
    /// Registers plugin-contributed slash commands in the command registry.
    /// Clears any previously registered plugin commands before re-scanning, so this method
    /// is safe to call on reload without accumulating stale entries.
    /// </summary>
    /// <param name="cwd">Current working directory used to locate project-local plugins.</param>
    private void LoadPluginCommands(string cwd)
    {
        // Remove any previously registered plugin commands.
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

    private void PrintBanner(string model)
    {
        AnsiConsole.Write(new Rule($"[blue]ClaudeCode C# v{ClaudeCodeInfo.Version}[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[grey]Model: {ModelResolver.GetDisplayName(model).EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine("[grey]Type /help for commands, /exit to quit.[/]");
    }

    private void PrintStatusLine(string model)
    {
        AnsiConsole.MarkupLine($"\n[grey]{_costTracker.FormatUsageSummary().EscapeMarkup()} | {ModelResolver.GetDisplayName(model).EscapeMarkup()}[/]");
    }

    private void ApplyTheme(string theme)
    {
        _currentTheme = theme;
        // Spectre.Console does not have a built-in global theme switcher,
        // so we signal the change by marking it. Future rendering picks up the field.
        AnsiConsole.MarkupLine($"[grey]Theme set to '{theme.EscapeMarkup()}'.[/]");
    }

    /// <summary>
    /// Starts or stops the voice input service in response to the <c>/voice</c> command.
    /// Creates the <see cref="ClaudeCode.Services.Voice.VoiceInputService"/> on first use.
    /// Reverts <see cref="ReplModeFlags.VoiceMode"/> when the engine cannot be initialised.
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to start listening; <see langword="false"/> to stop.</param>
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
                    AnsiConsole.MarkupLine(
                        $"[red]Voice input unavailable: {ex.Message.EscapeMarkup()}[/]");
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

    /// <summary>
    /// Handles a speech recognition result from <see cref="ClaudeCode.Services.Voice.VoiceInputService"/>.
    /// Prints the recognized text and queues it as the next REPL prompt turn via
    /// <see cref="_pendingNextPrompt"/>. Called on a background recognition thread — the
    /// string reference write is atomic on all supported .NET platforms.
    /// </summary>
    /// <param name="text">The recognized utterance text. Never null or empty.</param>
    private void OnVoiceTextRecognized(string text)
    {
        AnsiConsole.MarkupLine($"[grey]Recognized: \"{text.EscapeMarkup()}\"[/]");
        // Queue as the next submitted prompt turn only when no higher-priority prompt is already
        // pending (e.g. one injected by /autofix-pr). The REPL loop consumes _pendingNextPrompt
        // after the user's next slash command.
        _pendingNextPrompt ??= text;
    }

    /// <summary>
    /// Converts a color name from <see cref="ColorCommand.ActivePromptColor"/> into the
    /// corresponding ANSI SGR escape code string. Falls back to blue on unrecognised input.
    /// </summary>
    private static string GetAnsiColorCode(string color) => color.ToLowerInvariant() switch
    {
        "red"     => "\x1b[31m",
        "green"   => "\x1b[32m",
        "yellow"  => "\x1b[33m",
        "blue"    => "\x1b[34m",
        "magenta" => "\x1b[35m",
        "cyan"    => "\x1b[36m",
        "white"   => "\x1b[37m",
        _         => "\x1b[34m",  // fallback: blue
    };
}

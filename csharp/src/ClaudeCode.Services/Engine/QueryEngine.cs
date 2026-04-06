namespace ClaudeCode.Services.Engine;

using ClaudeCode.Configuration;
using ClaudeCode.Core.Messages;
using ClaudeCode.Core.State;
using ClaudeCode.Core.Tools;
using ClaudeCode.Services.Api;
using ClaudeCode.Services.Compact;
using System.Runtime.CompilerServices;
using System.Text.Json;

/// <summary>
/// Manages a multi-turn conversation with the Anthropic API.
/// Maintains conversation history, builds the system prompt, and streams
/// <see cref="QueryEvent"/> values to the caller for each assistant turn.
/// When the model responds with <c>stop_reason: "tool_use"</c>, the engine
/// executes the requested tools, appends results to history, and continues
/// the conversation automatically until the model stops requesting tools.
/// </summary>
public sealed class QueryEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Maximum number of tool-use loops per <see cref="SubmitAsync"/> call.
    /// Prevents runaway loops caused by misbehaving models or tool chains.
    /// </summary>
    private const int MaxToolLoops = 25;

    private readonly IAnthropicClient _client;
    private readonly CostTracker _costTracker;
    private readonly SystemPromptBuilder _promptBuilder;
    private readonly QueryEngineConfig _config;
    private readonly List<MessageParam> _messages = [];

    /// <summary>
    /// Counts the number of completed <see cref="SubmitAsync"/> calls.
    /// Used to detect the first user turn for coordinator-mode context wrapping.
    /// </summary>
    private int _turnCount;

    /// <summary>
    /// Circuit-breaker that prevents runaway auto-compaction loops.
    /// Tracks consecutive failures and enforces a cooldown before retrying.
    /// </summary>
    private readonly AutoCompactService _autoCompact = new();

    /// <summary>
    /// Emits a single console warning when context usage crosses 75%, guiding the user
    /// to run <c>/compact</c> before the engine triggers automatic compaction.
    /// </summary>
    private readonly CompactionWarning _compactionWarning = new();

    /// <summary>
    /// Initializes a new <see cref="QueryEngine"/>.
    /// </summary>
    /// <param name="client">The Anthropic API client. Must not be <see langword="null"/>.</param>
    /// <param name="costTracker">Cost and usage accumulator. Must not be <see langword="null"/>.</param>
    /// <param name="promptBuilder">System prompt assembler. Must not be <see langword="null"/>.</param>
    /// <param name="config">Engine configuration. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public QueryEngine(
        IAnthropicClient client,
        CostTracker costTracker,
        SystemPromptBuilder promptBuilder,
        QueryEngineConfig config)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Unique identifier for this engine instance, used as the session file name when persisting.
    /// Generated once at construction as a 12-character lowercase hex string.
    /// </summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The full conversation history accumulated across all calls to <see cref="SubmitAsync"/>.
    /// </summary>
    public IReadOnlyList<MessageParam> Messages => _messages.AsReadOnly();

    /// <summary>
    /// Replaces the conversation history with a previously saved message list,
    /// allowing an interrupted session to be resumed from a checkpoint.
    /// </summary>
    /// <param name="messages">The messages to restore. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="messages"/> is <see langword="null"/>.</exception>
    public void RestoreMessages(List<MessageParam> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.Clear();
        _messages.AddRange(messages);
    }

    /// <summary>
    /// Compacts the conversation history by summarizing older messages into a single summary
    /// exchange, then replacing the history in-place with the compacted list.
    /// Uses <see cref="CompactionService"/> with the small/fast model for cheapness.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CompactionResult"/> describing what was removed when compaction occurred;
    /// <see langword="null"/> when the conversation was too short to compact.
    /// </returns>
    public async Task<CompactionResult?> CompactAsync(CancellationToken ct = default)
    {
        // PreCompact hook — fires before context compaction starts.
        if (_config.HookRunner is not null)
        {
            await _config.HookRunner.RunAsync(new ClaudeCode.Services.Hooks.HookContext(
                Event: "PreCompact",
                ToolName: null,
                ToolInput: null,
                ToolResult: null,
                ToolIsError: false,
                SessionId: SessionId,
                Cwd: _config.Cwd), ct).ConfigureAwait(false);
        }

        var service = new CompactionService(_client);
        var (result, compacted) = await service.CompactAsync(_messages, _config.Model, ct)
            .ConfigureAwait(false);

        if (result.MessagesRemoved > 0)
        {
            _messages.Clear();
            _messages.AddRange(compacted);
            _compactionWarning.Reset();
        }

        return result.MessagesRemoved > 0 ? result : null;
    }

    /// <summary>
    /// Appends a user message to the conversation, sends the updated history to the API,
    /// and streams the assistant's response as <see cref="QueryEvent"/> values.
    /// When the model requests tool use, the engine executes each tool and loops until
    /// the model produces a final non-tool-use response.
    /// The assistant's response is automatically appended to conversation history upon completion.
    /// </summary>
    /// <param name="userMessage">The user's message text. Must not be <see langword="null"/> or whitespace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of <see cref="QueryEvent"/> values.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="userMessage"/> is null or whitespace.</exception>
    public async IAsyncEnumerable<QueryEvent> SubmitAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        // UserPromptSubmit hook — fires before the prompt is appended to history or sent to the API.
        if (_config.HookRunner is not null)
        {
            await _config.HookRunner.RunAsync(new ClaudeCode.Services.Hooks.HookContext(
                Event: "UserPromptSubmit",
                ToolName: null,
                ToolInput: userMessage,
                ToolResult: null,
                ToolIsError: false,
                SessionId: SessionId,
                Cwd: _config.Cwd), ct).ConfigureAwait(false);
        }

        // Coordinator mode: when active, wrap the very first user message with coordinator
        // context so the model knows to begin with the Research phase.
        var coordinatorActive = _config.CoordinatorMode
            || ClaudeCode.Services.Coordinator.CoordinatorMode.IsEnabled;
        var effectiveMessage = coordinatorActive && _turnCount == 0
            ? ClaudeCode.Services.Coordinator.CoordinatorMode.GetUserContext(userMessage)
            : userMessage;
        _turnCount++;

        // Append user turn to conversation history.
        _messages.Add(new MessageParam
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(effectiveMessage),
        });

        // Auto-compact if the estimated context usage exceeds 60% and the history is
        // long enough that compaction would be meaningful (more than 3 message pairs).
        if (TokenEstimator.ShouldAutoCompact(_messages, _config.Model) && _messages.Count > 6)
        {
            yield return new CompactEvent("Auto-compacting conversation...");
            await CompactAsync(ct).ConfigureAwait(false);
        }

        // Resolve system prompt: use override when provided, otherwise build dynamically.
        // ExtraDirectories is a live wrapper over AddDirCommand's list, so dirs added
        // via /add-dir mid-session are automatically included on subsequent turns.
        var systemPromptText = _config.CustomSystemPrompt
            ?? _promptBuilder.BuildText(_config.Cwd, extraDirs: _config.ExtraDirectories);

        // Append session memory facts when the session has stored any.
        if (_config.SessionMemory is not null && _config.SessionMemory.GetAll().Count > 0)
            systemPromptText += "\n\n" + _config.SessionMemory.BuildPromptSection();

        // Inject advisor annotation when an advisor model has been configured via /advisor.
        var systemPrompt = _config.AdvisorModel is { Length: > 0 } advisorModel
            ? systemPromptText + $"\n\nAdvisor model: {advisorModel} — consider using concise, focused responses as if guided by a faster model."
            : systemPromptText;

        // Inject coordinator system prompt when coordinator mode is active.
        // Appended as a distinct section so it does not interfere with the base prompt.
        if (coordinatorActive)
            systemPrompt += "\n\n" + ClaudeCode.Services.Coordinator.CoordinatorMode.GetSystemPrompt();

        // Inject UltraPlan system prompt when the /ultraplan toggle is active.
        // Stored in ClaudeCode.Core.State.ReplModeFlags to avoid a circular
        // project dependency between ClaudeCode.Services and ClaudeCode.Commands.
        if (ClaudeCode.Core.State.ReplModeFlags.UltraplanActive)
            systemPrompt += "\n\n" + ClaudeCode.Core.State.ReplModeFlags.UltraplanSystemPrompt;

        // Inject KAIROS assistant-mode addendum when /assistant toggle is active.
        if (ClaudeCode.Core.State.ReplModeFlags.KairosEnabled)
            systemPrompt += "\n\n" + ClaudeCode.Core.State.ReplModeFlags.KairosSystemPrompt;

        // Append session-memory-service facts (key/value store) after coordinator prompt.
        if (_config.SessionMemoryService is not null)
        {
            var memSection = _config.SessionMemoryService.BuildPromptSection();
            if (!string.IsNullOrEmpty(memSection))
                systemPrompt += "\n\n" + memSection;
        }

        // Build tool definitions once — avoids repeated async calls per loop iteration.
        List<ToolDefinition>? toolDefinitions = null;
        if (_config.Tools is { } registry)
            toolDefinitions = await BuildToolDefinitionsAsync(registry, ct).ConfigureAwait(false);

        for (int turn = 0; turn < MaxToolLoops; turn++)
        {
            var request = BuildRequest(systemPrompt, toolDefinitions);

            // Stream this turn and collect all events + the assistant content blocks.
            var (turnEvents, assistantContent, accumulatedInputs, stopReason) =
                await StreamTurnAsync(request, ct).ConfigureAwait(false);

            // Propagate rate-limit header info to the cost tracker.
            if (_client.LastRateLimitInfo is { } rlInfo)
                _costTracker.UpdateRateLimitInfo(rlInfo);

            // Yield all events from this turn to the caller.
            foreach (var ev in turnEvents)
                yield return ev;

            // Append the completed assistant turn to conversation history.
            if (assistantContent.Count > 0)
            {
                _messages.Add(new MessageParam
                {
                    Role = "assistant",
                    Content = JsonSerializer.SerializeToElement(assistantContent),
                });
            }

            // After each API response, check context usage and warn/compact as appropriate.
            {
                var usedTokens = TokenEstimator.EstimateMessageTokens(_messages);
                var contextLimit = TokenEstimator.GetContextWindow(_config.Model);
                _compactionWarning.MaybeWarn(usedTokens, contextLimit);

                if (_config.AutoCompact && usedTokens > 0 && contextLimit > 0
                    && _autoCompact.ShouldCompact(usedTokens, contextLimit))
                {
                    Spectre.Console.AnsiConsole.MarkupLine("[yellow]Auto-compacting context...[/]");
                    bool ok = false;
                    try { await CompactAsync(ct).ConfigureAwait(false); ok = true; }
                    catch { /* swallow compaction errors */ }
                    _autoCompact.RecordCompactionAttempt(ok);
                }
            }

            // Exit the loop when the model is done requesting tools.
            if (stopReason != "tool_use" || _config.Tools is null)
                break;

            // Build a ToolUseContext for this tool execution round.
            // AppState and FileStateCache are required by ToolUseContext; create minimal
            // instances since the engine does not have access to the full session state.
            var toolContext = new ToolUseContext
            {
                ToolRegistry = _config.Tools,
                AppState = new AppState(),
                ReadFileState = new FileStateCache(),
                Cwd = _config.Cwd,
                CancellationToken = ct,
                IsNonInteractive = _config.QuestionDialog is null,
                MainLoopModel = _config.Model,
                McpManager = _config.McpManager,
                QuestionDialog = _config.QuestionDialog,
                HookRunner = _config.HookRunner,
                AgentSummaryService = _config.AgentSummaryService,
            };

            // Execute each tool_use block and collect tool_result entries.
            var toolResultBlocks = new List<object>();

            // Pre-collect all tool-use blocks with their fully-resolved inputs so that
            // partitioning and parallel dispatch can operate on a stable snapshot.
            var parsedToolUses = new List<(string Id, string Name, JsonElement Input)>();

            foreach (var block in assistantContent)
            {
                if (!block.TryGetProperty("type", out var typeEl)
                    || typeEl.GetString() != "tool_use")
                    continue;

                var toolUseId = block.TryGetProperty("id", out var idEl)
                    ? idEl.GetString() ?? string.Empty
                    : string.Empty;

                var toolName = block.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;

                // Retrieve the accumulated full input JSON for this block.
                // The skeleton stored in assistantContent has an empty input field;
                // we tracked the actual JSON via input_json_delta events.
                JsonElement toolInput = default;
                if (accumulatedInputs.TryGetValue(toolUseId, out var inputJson)
                    && !string.IsNullOrEmpty(inputJson))
                {
                    try
                    {
                        toolInput = JsonDocument.Parse(inputJson).RootElement;
                    }
                    catch (JsonException)
                    {
                        toolInput = JsonSerializer.SerializeToElement(new { });
                    }
                }
                else
                {
                    // Fall back to the input field from the skeleton block if present.
                    if (block.TryGetProperty("input", out var inputEl))
                        toolInput = inputEl;
                    else
                        toolInput = JsonSerializer.SerializeToElement(new { });
                }

                parsedToolUses.Add((toolUseId, toolName, toolInput));
            }

            // Partition tool-use blocks into concurrent and sequential groups.
            // Concurrent-safe tools (read-only, idempotent) run in parallel; others run sequentially.
            var concurrentGroup = new List<(string Id, string Name, JsonElement Input)>();
            var sequentialQueue = new Queue<(string Id, string Name, JsonElement Input)>();

            foreach (var item in parsedToolUses)
            {
                var tool = _config.Tools.GetTool(item.Name);
                if (tool is not null && tool.IsConcurrencySafe(item.Input))
                    concurrentGroup.Add(item);
                else
                    sequentialQueue.Enqueue(item);
            }

            // Execute concurrent-safe tools in parallel via Task.WhenAll.
            if (concurrentGroup.Count > 0)
            {
                var parallelTasks = concurrentGroup.Select(async item =>
                {
                    var itemContext = toolContext with { ToolUseId = item.Id };
                    var (r, e) = await ToolExecutor.ExecuteToolAsync(
                        _config.Tools, item.Name, item.Input, itemContext,
                        _config.PermissionEvaluator, _config.PermissionDialog, _config.HookRunner, ct,
                        _config.MicroCompact, _config.ToolUsageSummary).ConfigureAwait(false);
                    return (item.Id, item.Name, Result: r, IsError: e);
                });

                var parallelResults = await Task.WhenAll(parallelTasks).ConfigureAwait(false);

                foreach (var (id, name, result, isError) in parallelResults)
                {
                    yield return new ToolResultEvent(id, name, result, isError);
                    toolResultBlocks.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = id,
                        content = result,
                        is_error = isError,
                    });
                }
            }

            // Execute sequential tools one at a time, preserving declaration order.
            while (sequentialQueue.Count > 0)
            {
                var item = sequentialQueue.Dequeue();
                var itemContext = toolContext with { ToolUseId = item.Id };
                var (result, isError) = await ToolExecutor.ExecuteToolAsync(
                    _config.Tools, item.Name, item.Input, itemContext,
                    _config.PermissionEvaluator, _config.PermissionDialog, _config.HookRunner, ct,
                    _config.MicroCompact, _config.ToolUsageSummary).ConfigureAwait(false);

                yield return new ToolResultEvent(item.Id, item.Name, result, isError);
                toolResultBlocks.Add(new
                {
                    type = "tool_result",
                    tool_use_id = item.Id,
                    content = result,
                    is_error = isError,
                });
            }

            // If no tool_use blocks were found (defensive — shouldn't happen if stop_reason is tool_use),
            // break to avoid an infinite loop.
            if (toolResultBlocks.Count == 0)
                break;

            // Append the tool results as a new user message and continue the loop.
            _messages.Add(new MessageParam
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(toolResultBlocks),
            });
        }

        // Stop hook — fires after the model delivers its final response.
        if (_config.HookRunner is not null)
        {
            await _config.HookRunner.RunAsync(new ClaudeCode.Services.Hooks.HookContext(
                Event: "Stop",
                ToolName: null,
                ToolInput: null,
                ToolResult: null,
                ToolIsError: false,
                SessionId: SessionId,
                Cwd: _config.Cwd), ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="MessageRequest"/> from current engine state.
    /// Applies effort-level overrides to <c>MaxTokens</c> and <c>ThinkingBudgetTokens</c>:
    /// <list type="bullet">
    ///   <item><description><c>low</c> — 1 024 tokens, no extended thinking.</description></item>
    ///   <item><description><c>medium</c> (default) — config values unchanged.</description></item>
    ///   <item><description><c>high</c> — extended thinking with 8 192-token budget.</description></item>
    ///   <item><description><c>max</c> — extended thinking with 32 000-token budget.</description></item>
    /// </list>
    /// </summary>
    private MessageRequest BuildRequest(string systemPrompt, List<ToolDefinition>? toolDefinitions)
    {
        var (effectiveMaxTokens, effectiveThinkingBudget) = _config.EffortLevel switch
        {
            "low"  => (1024, 0),
            "high" => (_config.MaxTokens, 8192),
            "max"  => (_config.MaxTokens, 32000),
            _      => (_config.MaxTokens, _config.ThinkingBudgetTokens), // "medium" / default
        };

        return new()
        {
            Model = _config.Model,
            MaxTokens = effectiveMaxTokens,
            Messages = [.. _messages],
            System =
            [
                new SystemBlock { Text = systemPrompt },
            ],
            Tools = toolDefinitions is { Count: > 0 } ? toolDefinitions : null,
            Thinking = effectiveThinkingBudget > 0
                ? new ThinkingConfig { Type = "enabled", BudgetTokens = effectiveThinkingBudget }
                : null,
        };
    }

    /// <summary>
    /// Streams a single assistant turn, collecting all <see cref="QueryEvent"/> values,
    /// the raw assistant content blocks, accumulated tool input JSON keyed by tool_use_id,
    /// and the stop reason.
    /// </summary>
    /// <remarks>
    /// Returning a collected list rather than yielding directly avoids the C# constraint
    /// that prevents <c>yield return</c> inside a <c>try/catch</c> block, which would be
    /// needed to handle stream errors in the same method that yields events.
    /// </remarks>
    private async Task<(
        List<QueryEvent> Events,
        List<JsonElement> AssistantContent,
        Dictionary<string, string> AccumulatedInputs,
        string? StopReason)> StreamTurnAsync(
        MessageRequest request,
        CancellationToken ct)
    {
        var events = new List<QueryEvent>();
        var blockTypes = new Dictionary<int, string>();
        var assistantContent = new List<JsonElement>();

        // Maps block index → accumulated input JSON string for tool_use blocks.
        var inputsByIndex = new Dictionary<int, System.Text.StringBuilder>();
        // Maps tool_use_id → accumulated input JSON string (resolved at block_stop or end).
        var accumulatedInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        // Maps block index → tool_use_id for tool_use blocks.
        var toolIdsByIndex = new Dictionary<int, string>();

        string? stopReason = null;

        try
        {
            await foreach (var sseEvent in _client.StreamMessageAsync(request, ct).ConfigureAwait(false))
            {
                switch (sseEvent.EventType)
                {
                    case "message_start":
                    {
                        var payload = TryDeserialize<MessageStartPayload>(sseEvent.Data);
                        if (payload?.Message.Usage is { } usage)
                            _costTracker.AddUsage(_config.Model, usage);
                        break;
                    }

                    case "content_block_start":
                    {
                        var payload = TryDeserialize<ContentBlockStartPayload>(sseEvent.Data);
                        if (payload is null)
                            break;

                        var blockTypeValue = payload.ContentBlock.TryGetProperty("type", out var typeEl)
                            ? typeEl.GetString() ?? "text"
                            : "text";
                        blockTypes[payload.Index] = blockTypeValue;

                        // Snapshot initial block skeleton for conversation history.
                        assistantContent.Add(payload.ContentBlock);

                        if (blockTypeValue == "tool_use")
                        {
                            var toolId = payload.ContentBlock.TryGetProperty("id", out var idEl)
                                ? idEl.GetString() ?? string.Empty
                                : string.Empty;
                            var toolName = payload.ContentBlock.TryGetProperty("name", out var nameEl)
                                ? nameEl.GetString() ?? string.Empty
                                : string.Empty;

                            // Initialise the input accumulator for this block.
                            inputsByIndex[payload.Index] = new System.Text.StringBuilder();
                            toolIdsByIndex[payload.Index] = toolId;

                            events.Add(new ToolUseStartEvent(toolId, toolName));
                        }

                        break;
                    }

                    case "content_block_delta":
                    {
                        var payload = TryDeserialize<ContentBlockDeltaPayload>(sseEvent.Data);
                        if (payload is null)
                            break;

                        var blockType = blockTypes.GetValueOrDefault(payload.Index, "text");
                        var delta = payload.Delta;

                        if (!delta.TryGetProperty("type", out var deltaTypeEl))
                            break;

                        var deltaType = deltaTypeEl.GetString();

                        if (deltaType == "text_delta" && delta.TryGetProperty("text", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                events.Add(new TextDeltaEvent(text));
                        }
                        else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinkingEl))
                        {
                            var thinking = thinkingEl.GetString();
                            if (!string.IsNullOrEmpty(thinking))
                                events.Add(new ThinkingDeltaEvent(thinking));
                        }
                        else if (deltaType == "input_json_delta"
                            && blockType == "tool_use"
                            && delta.TryGetProperty("partial_json", out var partialJsonEl))
                        {
                            var fragment = partialJsonEl.GetString();
                            if (!string.IsNullOrEmpty(fragment)
                                && inputsByIndex.TryGetValue(payload.Index, out var sb))
                            {
                                sb.Append(fragment);
                            }
                        }

                        break;
                    }

                    case "content_block_stop":
                    {
                        var payload = TryDeserialize<ContentBlockStopPayload>(sseEvent.Data);
                        if (payload is null)
                            break;

                        // Finalise accumulated input JSON for tool_use blocks.
                        if (inputsByIndex.TryGetValue(payload.Index, out var sb)
                            && toolIdsByIndex.TryGetValue(payload.Index, out var toolId))
                        {
                            accumulatedInputs[toolId] = sb.ToString();
                        }

                        break;
                    }

                    case "message_delta":
                    {
                        var payload = TryDeserialize<MessageDeltaPayload>(sseEvent.Data);
                        if (payload is null)
                            break;

                        if (payload.Usage is { } usage)
                            _costTracker.AddUsage(_config.Model, usage);

                        if (payload.Delta.TryGetProperty("stop_reason", out var stopEl))
                            stopReason = stopEl.GetString();

                        break;
                    }

                    case "message_stop":
                    {
                        var usageSnapshot = new UsageInfo
                        {
                            InputTokens = _costTracker.TotalInputTokens,
                            OutputTokens = _costTracker.TotalOutputTokens,
                        };
                        events.Add(new MessageCompleteEvent(usageSnapshot, stopReason));
                        break;
                    }

                    case "error":
                    {
                        var errorMessage = ParseErrorMessage(sseEvent.Data);
                        events.Add(new ErrorEvent(errorMessage));
                        // Signal error to the outer loop by clearing stop reason.
                        // The caller will see ErrorEvent in the yielded events and can act accordingly.
                        stopReason = "error";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            events.Add(new ErrorEvent($"Stream error: {ex.Message}"));
            stopReason = "error";
        }

        // Finalise any tool_use blocks whose content_block_stop was not received
        // (e.g. stream ended early). Ensures accumulatedInputs is always populated
        // for any block that began streaming.
        foreach (var (index, sb) in inputsByIndex)
        {
            if (toolIdsByIndex.TryGetValue(index, out var toolId)
                && !accumulatedInputs.ContainsKey(toolId))
            {
                accumulatedInputs[toolId] = sb.ToString();
            }
        }

        return (events, assistantContent, accumulatedInputs, stopReason);
    }

    /// <summary>
    /// Builds <see cref="ToolDefinition"/> records for all enabled tools in <paramref name="registry"/>.
    /// </summary>
    private static async Task<List<ToolDefinition>> BuildToolDefinitionsAsync(
        ToolRegistry registry,
        CancellationToken ct)
    {
        var definitions = new List<ToolDefinition>();

        foreach (var tool in registry.GetAll())
        {
            if (!tool.IsEnabled())
                continue;

            var description = await tool.GetDescriptionAsync(ct).ConfigureAwait(false);
            definitions.Add(new ToolDefinition
            {
                Name = tool.Name,
                Description = description,
                InputSchema = tool.GetInputSchema(),
            });
        }

        return definitions;
    }

    /// <summary>
    /// Deserializes <paramref name="json"/> into <typeparamref name="T"/>, returning
    /// <see langword="null"/> on empty input or any parse failure.
    /// </summary>
    private static T? TryDeserialize<T>(string json) where T : class
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the human-readable error message from an SSE "error" event data payload.
    /// Returns a generic fallback string when the payload cannot be parsed.
    /// </summary>
    private static string ParseErrorMessage(string data)
    {
        if (string.IsNullOrEmpty(data))
            return "Unknown stream error";

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("error", out var errorEl)
                && errorEl.TryGetProperty("message", out var msgEl))
            {
                return msgEl.GetString() ?? "Unknown stream error";
            }
        }
        catch (JsonException)
        {
            // Fall through to the fallback below.
        }

        return "Unknown stream error";
    }
}

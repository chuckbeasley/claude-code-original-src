namespace ClaudeCode.Services.Engine;

using System.Diagnostics;
using System.Text.Json;
using ClaudeCode.Core.Permissions;
using ClaudeCode.Core.Tools;
using ClaudeCode.Permissions;
using ClaudeCode.Services.Compact;

/// <summary>
/// Stateless helper that resolves and executes a single tool invocation on behalf of
/// <see cref="QueryEngine"/>. Decouples tool dispatch from the engine's streaming loop.
/// </summary>
internal static class ToolExecutor
{
    /// <summary>
    /// Resolves <paramref name="toolName"/> in <paramref name="registry"/>, runs the
    /// permission pipeline, and executes the tool. Returns the string result together
    /// with a flag indicating whether execution failed.
    /// </summary>
    /// <param name="registry">The registry to look up the tool from. Must not be <see langword="null"/>.</param>
    /// <param name="toolName">The canonical tool name from the model's <c>tool_use</c> block. Must not be <see langword="null"/>.</param>
    /// <param name="input">The raw JSON element from the model's <c>input</c> field.</param>
    /// <param name="context">Ambient context for the invocation. Must not be <see langword="null"/>.</param>
    /// <param name="permissionEvaluator">
    /// Optional evaluator. When non-null, permission is checked before execution.
    /// </param>
    /// <param name="permissionDialog">
    /// Optional callback invoked when the evaluator returns <see cref="PermissionAsk"/>.
    /// When <see langword="null"/> and an ask is issued, the tool is denied.
    /// </param>
    /// <param name="hookRunner">Optional hook runner. When non-null, PreToolUse and PostToolUse hooks are executed.</param>
    /// <param name="microCompact">
    /// Optional micro-compaction service. When non-null, large tool results are truncated
    /// before being returned so they do not bloat the conversation history.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="toolUsageSummary">
    /// Optional tool-usage statistics service. When non-null, records call count, error flag,
    /// and elapsed time for each tool execution.
    /// </param>
    /// <returns>
    /// A tuple of (<c>Result</c>, <c>IsError</c>) where <c>IsError</c> is <see langword="true"/>
    /// when the tool was not found, permission was denied, or the tool threw an exception.
    /// </returns>
    public static async Task<(string Result, bool IsError)> ExecuteToolAsync(
        ToolRegistry registry,
        string toolName,
        JsonElement input,
        ToolUseContext context,
        IPermissionEvaluator? permissionEvaluator,
        Func<PermissionAsk, string, string, Task<PermissionDecision>>? permissionDialog,
        ClaudeCode.Services.Hooks.HookRunner? hookRunner,
        CancellationToken ct,
        MicroCompactService? microCompact = null,
        ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService? toolUsageSummary = null)
    {
        var tool = registry.GetTool(toolName);
        if (tool is null)
            return ($"Unknown tool: {toolName}", true);

        // Permission check — only when an evaluator is wired up.
        if (permissionEvaluator is not null)
        {
            var permContext = context.AppState.ToolPermissions;
            var decision = await permissionEvaluator
                .EvaluateAsync(tool, input, permContext, ct)
                .ConfigureAwait(false);

            if (decision is PermissionDenied denied)
                return ($"Permission denied: {denied.Message}", true);

            if (decision is PermissionAsk ask)
            {
                if (permissionDialog is null)
                    return ($"Permission denied: no dialog handler configured for {toolName}", true);

                var inputStr = input.ToString();
                var userDecision = await permissionDialog(ask, toolName, inputStr).ConfigureAwait(false);

                if (userDecision is PermissionDenied userDenied)
                    return ($"Permission denied: {userDenied.Message}", true);

                // PermissionAllowed falls through to execution below.
            }
        }

        // PreToolUse hook
        if (hookRunner is not null)
        {
            await hookRunner.RunAsync(new ClaudeCode.Services.Hooks.HookContext(
                Event: "PreToolUse",
                ToolName: toolName,
                ToolInput: input.ToString(),
                ToolResult: null,
                ToolIsError: false,
                Cwd: context.Cwd), ct).ConfigureAwait(false);
        }

        string toolResult;
        bool isError;
        var sw = Stopwatch.StartNew();
        try
        {
            toolResult = await tool.ExecuteRawAsync(input, context, ct).ConfigureAwait(false);
            isError = false;
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation — do not swallow.
        }
        catch (Exception ex)
        {
            toolResult = $"Tool execution error: {ex.Message}";
            isError = true;
        }
        finally
        {
            sw.Stop();
        }

        // Record tool usage statistics when the summary service is wired.
        toolUsageSummary?.RecordToolUse(toolName, isError, sw.ElapsedMilliseconds);

        // Micro-compact large tool results before they enter the conversation history.
        // Only applied to successful results; error messages are small and need no truncation.
        if (!isError && microCompact is not null)
            toolResult = microCompact.MaybeCompact(toolResult, toolName);

        // PostToolUse hook — fires regardless of success or error.
        if (hookRunner is not null)
        {
            await hookRunner.RunAsync(new ClaudeCode.Services.Hooks.HookContext(
                Event: "PostToolUse",
                ToolName: toolName,
                ToolInput: input.ToString(),
                ToolResult: toolResult,
                ToolIsError: isError,
                Cwd: context.Cwd), ct).ConfigureAwait(false);
        }

        return (toolResult, isError);
    }
}

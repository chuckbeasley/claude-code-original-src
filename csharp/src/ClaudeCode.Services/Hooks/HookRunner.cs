namespace ClaudeCode.Services.Hooks;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeCode.Configuration.Settings;

/// <summary>
/// Context passed to each hook execution describing what triggered it.
/// </summary>
public record HookContext(
    string Event,           // "PreToolUse", "PostToolUse", "Stop"
    string? ToolName,       // set for tool hooks
    string? ToolInput,      // JSON string of tool input, set for tool hooks
    string? ToolResult,     // set for PostToolUse
    bool ToolIsError,       // set for PostToolUse
    string? SessionId = null,
    string? Cwd = null);

/// <summary>
/// Executes hooks registered for a given event in settings.
/// </summary>
public sealed class HookRunner
{
    private readonly SettingsJson _settings;
    private readonly HashSet<string> _executedOnce = new(StringComparer.Ordinal);

    public HookRunner(SettingsJson settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Runs all hooks registered for <paramref name="ctx.Event"/> whose matcher
    /// matches <paramref name="ctx.ToolName"/> (or has no matcher).
    /// </summary>
    public async Task RunAsync(HookContext ctx, CancellationToken ct = default)
    {
        if (_settings.DisableAllHooks == true) return;
        if (_settings.Hooks is null) return;
        if (!_settings.Hooks.TryGetValue(ctx.Event, out var matchers)) return;

        foreach (var matcher in matchers)
        {
            // Apply tool-name filter when a matcher string is set.
            if (!string.IsNullOrWhiteSpace(matcher.Matcher) && ctx.ToolName is not null)
            {
                if (!ctx.ToolName.Equals(matcher.Matcher, StringComparison.OrdinalIgnoreCase)
                    && !System.Text.RegularExpressions.Regex.IsMatch(ctx.ToolName, matcher.Matcher,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;
            }

            if (matcher.Commands is null) continue;

            foreach (var cmd in matcher.Commands)
            {
                try
                {
                    await ExecuteCommandAsync(cmd, ctx, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Hook failures are non-fatal; log to stderr and continue.
                    Console.Error.WriteLine($"[hooks] {ctx.Event} hook failed: {ex.Message}");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task ExecuteCommandAsync(HookCommand cmd, HookContext ctx, CancellationToken ct)
    {
        switch (cmd)
        {
            case BashHookCommand bash:
                await RunBashHookAsync(bash, ctx, ct).ConfigureAwait(false);
                break;

            case HttpHookCommand http:
                await RunHttpHookAsync(http, ctx, ct).ConfigureAwait(false);
                break;

            case PromptHookCommand:
                // Prompt hooks require an API client — not wired at this layer.
                // Skip silently; callers can extend to handle them.
                break;
        }
    }

    private async Task RunBashHookAsync(BashHookCommand bash, HookContext ctx, CancellationToken ct)
    {
        // "once" hooks run only the first time per session.
        var onceKey = $"{ctx.Event}:{bash.Command}";
        if (bash.Once == true)
        {
            if (_executedOnce.Contains(onceKey)) return;
            _executedOnce.Add(onceKey);
        }

        var shell = bash.Shell ?? (OperatingSystem.IsWindows() ? "cmd.exe" : "bash");
        var shellArg = OperatingSystem.IsWindows() ? "/c" : "-c";
        var timeout = TimeSpan.FromSeconds(bash.Timeout ?? 30);

        // Build environment variables for the hook.
        var env = BuildEnvVars(ctx);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var psi = new ProcessStartInfo(shell, $"{shellArg} \"{bash.Command.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = ctx.Cwd ?? Directory.GetCurrentDirectory(),
        };

        // Inject hook context as environment variables.
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start shell for hook: {bash.Command}");

        // Write hook context JSON to stdin.
        var contextJson = JsonSerializer.Serialize(new
        {
            hookEvent = ctx.Event,
            toolName = ctx.ToolName,
            toolInput = ctx.ToolInput,
            toolResult = ctx.ToolResult,
            toolIsError = ctx.ToolIsError,
            sessionId = ctx.SessionId,
            cwd = ctx.Cwd,
        });
        await proc.StandardInput.WriteLineAsync(contextJson).ConfigureAwait(false);
        proc.StandardInput.Close();

        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
    }

    private static async Task RunHttpHookAsync(HttpHookCommand http, HookContext ctx, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(http.Timeout ?? 10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var httpClient = new System.Net.Http.HttpClient();

        var payload = JsonSerializer.Serialize(new
        {
            hookEvent = ctx.Event,
            toolName = ctx.ToolName,
            toolInput = ctx.ToolInput,
            toolResult = ctx.ToolResult,
            toolIsError = ctx.ToolIsError,
            sessionId = ctx.SessionId,
            cwd = ctx.Cwd,
        });

        using var content = new System.Net.Http.StringContent(
            payload, Encoding.UTF8, "application/json");

        if (http.Headers is not null)
            foreach (var (k, v) in http.Headers)
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

        await httpClient.PostAsync(http.Url, content, cts.Token).ConfigureAwait(false);
    }

    private static Dictionary<string, string> BuildEnvVars(HookContext ctx) => new()
    {
        ["CLAUDE_HOOK_EVENT"]      = ctx.Event,
        ["CLAUDE_HOOK_TOOL_NAME"]  = ctx.ToolName ?? string.Empty,
        ["CLAUDE_HOOK_TOOL_INPUT"] = ctx.ToolInput ?? string.Empty,
        ["CLAUDE_HOOK_TOOL_RESULT"]= ctx.ToolResult ?? string.Empty,
        ["CLAUDE_HOOK_IS_ERROR"]   = ctx.ToolIsError ? "1" : "0",
        ["CLAUDE_HOOK_SESSION_ID"] = ctx.SessionId ?? string.Empty,
        ["CLAUDE_HOOK_CWD"]        = ctx.Cwd ?? string.Empty,
    };
}

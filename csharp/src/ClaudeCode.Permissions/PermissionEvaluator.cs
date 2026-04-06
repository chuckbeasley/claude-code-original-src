namespace ClaudeCode.Permissions;

using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeCode.Core.Permissions;
using ClaudeCode.Core.Tools;
using Microsoft.Extensions.FileSystemGlobbing;

/// <summary>
/// Evaluates whether a tool invocation should be allowed, denied, or requires user input.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>
    /// Evaluates whether a tool invocation should be allowed, denied, or requires user input.
    /// </summary>
    /// <param name="tool">The tool being invoked. Must not be <see langword="null"/>.</param>
    /// <param name="input">The raw JSON input the model supplied.</param>
    /// <param name="permContext">The merged permission context for this session. Must not be <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PermissionDecision"/> indicating the outcome.</returns>
    Task<PermissionDecision> EvaluateAsync(
        ITool tool,
        JsonElement input,
        ToolPermissionContext permContext,
        CancellationToken ct = default);

    /// <summary>
    /// Stores an approval or denial decision in the session-level cache for the given
    /// tool name and input combination so that repeat invocations skip the interactive prompt.
    /// </summary>
    /// <param name="toolName">The canonical tool name. Must not be <see langword="null"/>.</param>
    /// <param name="inputSummary">String representation of the tool input used as the cache key.</param>
    /// <param name="approved"><see langword="true"/> to cache as approved; <see langword="false"/> to cache as denied.</param>
    void CacheApproval(string toolName, string inputSummary, bool approved);

    /// <summary>
    /// Returns a previously cached decision for the tool name and input, or
    /// <see langword="null"/> when no cached entry exists.
    /// </summary>
    bool? GetCachedDecision(string toolName, string inputSummary);
}

/// <summary>
/// Three-phase permission pipeline: deny rules → allow rules → mode-based default.
/// Includes a session-level approval cache keyed by tool name and input hash so that
/// once a user approves an invocation pattern it is auto-approved for the rest of the session.
/// </summary>
public sealed class PermissionEvaluator : IPermissionEvaluator
{
    // Process-wide session cache — shared across all evaluator instances.
    // Key: "{toolName}:{inputHash:X}"  Value: approved/denied flag.
    private static readonly ConcurrentDictionary<string, bool> _sessionCache = new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // IPermissionEvaluator — cache API
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void CacheApproval(string toolName, string inputSummary, bool approved)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        _sessionCache[CacheKey(toolName, inputSummary ?? string.Empty)] = approved;
    }

    /// <inheritdoc/>
    public bool? GetCachedDecision(string toolName, string inputSummary)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var key = CacheKey(toolName, inputSummary ?? string.Empty);
        return _sessionCache.TryGetValue(key, out var val) ? val : null;
    }

    // -------------------------------------------------------------------------
    // IPermissionEvaluator — evaluation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<PermissionDecision> EvaluateAsync(
        ITool tool,
        JsonElement input,
        ToolPermissionContext permContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(permContext);

        ct.ThrowIfCancellationRequested();

        // Session cache check — bypass full rule evaluation for repeat invocations.
        var inputSummary = input.ToString();
        var cached = GetCachedDecision(tool.Name, inputSummary);
        if (cached.HasValue)
            return Task.FromResult<PermissionDecision>(
                cached.Value
                    ? new PermissionAllowed(Reason: "session cache")
                    : new PermissionDenied("Cached denial", "session cache"));

        // Phase 1: Deny rules have highest priority.
        foreach (var rule in permContext.DenyRules)
        {
            if (MatchesRule(rule, tool.Name, input))
                return Task.FromResult<PermissionDecision>(
                    new PermissionDenied(
                        $"Denied by {rule.Source} rule",
                        rule.Source.ToString()));
        }

        // Phase 2: Allow rules.
        foreach (var rule in permContext.AllowRules)
        {
            if (MatchesRule(rule, tool.Name, input))
                return Task.FromResult<PermissionDecision>(
                    new PermissionAllowed(Reason: $"Allowed by {rule.Source} rule"));
        }

        // Phase 3: Mode-based default.
        var decision = permContext.Mode switch
        {
            PermissionMode.BypassPermissions => (PermissionDecision)new PermissionAllowed(Reason: "bypass mode"),
            PermissionMode.Auto => new PermissionAllowed(Reason: "auto mode"),
            PermissionMode.DontAsk => new PermissionDenied("Operation not explicitly allowed", "dontAsk mode"),
            PermissionMode.Plan => EvaluatePlanMode(tool, input),
            PermissionMode.AcceptEdits => EvaluateAcceptEditsMode(tool, input),
            PermissionMode.Default => EvaluateDefaultMode(tool, input),
            _ => EvaluateDefaultMode(tool, input),
        };

        return Task.FromResult(decision);
    }

    // -------------------------------------------------------------------------
    // Mode-specific evaluators
    // -------------------------------------------------------------------------

    /// <summary>
    /// Plan mode: read-only tools are allowed; write tools are denied.
    /// </summary>
    private static PermissionDecision EvaluatePlanMode(ITool tool, JsonElement input)
    {
        if (tool.IsReadOnly(input))
            return new PermissionAllowed(Reason: "read-only in plan mode");
        return new PermissionDenied("Write operations are not allowed in plan mode", "plan mode");
    }

    /// <summary>
    /// AcceptEdits mode: file-editing tools and read-only tools are auto-approved;
    /// other write tools prompt the user.
    /// </summary>
    private static PermissionDecision EvaluateAcceptEditsMode(ITool tool, JsonElement input)
    {
        var name = tool.Name;
        if (name is "Edit" or "Write" or "FileRead" or "NotebookEdit" || tool.IsReadOnly(input))
            return new PermissionAllowed(Reason: "accept-edits mode");
        return new PermissionAsk($"Allow {tool.UserFacingName(input)} to execute?");
    }

    /// <summary>
    /// Default mode: read-only tools are allowed; everything else prompts.
    /// </summary>
    private static PermissionDecision EvaluateDefaultMode(ITool tool, JsonElement input)
    {
        if (tool.IsReadOnly(input))
            return new PermissionAllowed(Reason: "read-only tool");
        return new PermissionAsk($"Allow {tool.UserFacingName(input)} to execute?");
    }

    // -------------------------------------------------------------------------
    // Rule matching
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true when the given tool name and input match the permission rule.
    /// Handles both simple name rules ("Bash") and sub-pattern rules ("Bash(prefix:git*)").
    /// </summary>
    private static bool MatchesRule(PermissionRule rule, string toolName, JsonElement input)
    {
        var pattern = rule.ToolName;

        // No sub-pattern: simple name match
        var parenIdx = pattern.IndexOf('(');
        if (parenIdx < 0)
            return string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, "*", StringComparison.Ordinal);

        // Sub-pattern: "ToolName(pattern)"
        var baseName = pattern[..parenIdx].Trim();
        if (!string.Equals(baseName, toolName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(baseName, "*", StringComparison.Ordinal))
            return false;

        var closeIdx = pattern.LastIndexOf(')');
        if (closeIdx <= parenIdx) return true; // malformed — treat as match on base name only

        var subPattern = pattern[(parenIdx + 1)..closeIdx].Trim();
        if (subPattern.Length == 0) return true;

        // Extract the relevant input string for the tool.
        // Bash → "command"; FileEdit/FileRead/FileWrite/Glob/Grep → "file_path" or "path" or "pattern"
        var inputValue = ExtractInputString(toolName, input);
        if (inputValue is null) return true; // can't evaluate — default allow

        return EvaluateSubPattern(subPattern, inputValue);
    }

    private static string? ExtractInputString(string toolName, JsonElement input)
    {
        // Try common field names in priority order for each tool type.
        var fieldNames = toolName.ToLowerInvariant() switch
        {
            "bash" or "powershell"          => new[] { "command" },
            "fileedit" or "edit"            => new[] { "file_path", "path" },
            "fileread" or "read"            => new[] { "file_path", "path" },
            "filewrite" or "write"          => new[] { "file_path", "path" },
            "glob"                          => new[] { "pattern", "path" },
            "grep"                          => new[] { "path", "pattern" },
            _                               => new[] { "command", "file_path", "path", "input" },
        };

        foreach (var field in fieldNames)
        {
            if (input.TryGetProperty(field, out var val) &&
                val.ValueKind == JsonValueKind.String)
            {
                return val.GetString();
            }
        }

        return null;
    }

    private static bool EvaluateSubPattern(string subPattern, string inputValue)
    {
        // "prefix:git" → value starts with "git" (case-insensitive)
        if (subPattern.StartsWith("prefix:", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = subPattern[7..];
            return inputValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // "suffix:.ts" → value ends with ".ts"
        if (subPattern.StartsWith("suffix:", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = subPattern[7..];
            return inputValue.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        // "regex:^git\s+" → full regex match
        if (subPattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            var regexStr = subPattern[6..];
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    inputValue, regexStr,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        // Glob pattern: "**/*.ts", "/src/*", etc.
        return GlobMatch(subPattern, inputValue);
    }

    private static bool GlobMatch(string pattern, string value)
    {
        // Use Microsoft.Extensions.FileSystemGlobbing for path glob patterns.
        // Fall back to simple wildcard matching for non-path patterns.
        if (pattern.Contains('/') || pattern.Contains('*') || pattern.Contains('?'))
        {
            try
            {
                var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
                matcher.AddInclude(pattern);
                // Treat the value as a relative path from root
                var normalized = value.Replace('\\', '/').TrimStart('/');
                return matcher.Match(normalized).HasMatches;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PermissionEvaluator] Pattern '{pattern}' threw: {ex.Message}");
                // Treat malformed pattern as non-matching (deny by default)
            }
        }

        // Simple case-insensitive substring/equality
        return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the cache key from a tool name and input summary string.
    /// Uses a stable hash of the input to keep key sizes bounded.
    /// </summary>
    private static string CacheKey(string toolName, string inputSummary) =>
        $"{toolName}:{inputSummary.GetHashCode():X}";
}

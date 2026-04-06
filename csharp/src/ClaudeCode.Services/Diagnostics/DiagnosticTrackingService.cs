namespace ClaudeCode.Services.Diagnostics;

/// <summary>
/// Severity levels matching the LSP DiagnosticSeverity enum.
/// </summary>
public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

/// <summary>
/// A single diagnostic item. Mirrors the TS Diagnostic interface from the IDE MCP.
/// </summary>
public sealed record Diagnostic(
    string Uri,
    string Message,
    DiagnosticSeverity Severity,
    string? Source = null,
    string? Code    = null);

/// <summary>
/// Captures a pre-query baseline of IDE diagnostics and surfaces only the NEW
/// ones that appear after an operation. Mirrors src/services/diagnosticTracking.ts.
/// </summary>
public sealed class DiagnosticTrackingService
{
    private readonly IDiagnosticProvider _provider;
    private IReadOnlyList<Diagnostic> _baseline = [];

    public DiagnosticTrackingService(IDiagnosticProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <summary>
    /// Snapshots the current diagnostic set as the baseline. Call at the start of each query.
    /// </summary>
    public async Task CaptureBaselineAsync(CancellationToken ct = default)
    {
        try   { _baseline = await _provider.GetDiagnosticsAsync(ct); }
        catch { _baseline = []; }
    }

    /// <summary>
    /// Returns diagnostics NOT present in the baseline (new issues introduced by the last operation).
    /// Prefers _claude_fs_right: URIs when both left/right sides are present for a diff.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> GetNewDiagnosticsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Diagnostic> current;
        try   { current = await _provider.GetDiagnosticsAsync(ct); }
        catch { return []; }

        // Deduplicate using a HashSet of baseline keys.
        var baselineKeys = new HashSet<string>(_baseline.Select(DiagnosticKey),
                                               StringComparer.Ordinal);

        // Prefer _claude_fs_right: URIs (diff view right-hand side).
        var result = current
            .Where(d => !baselineKeys.Contains(DiagnosticKey(d)))
            .ToList();

        // If the same logical issue appears in both a plain URI and a _claude_fs_right: URI,
        // keep only the _claude_fs_right: version.
        var normalised = MergeRightSide(result);
        return normalised;
    }

    /// <summary>Resets the baseline to empty. Call before each new query.</summary>
    public void Reset() => _baseline = [];

    // --- helpers ---

    private static string DiagnosticKey(Diagnostic d)
        => $"{d.Uri}|{d.Severity}|{d.Message}";

    private static IReadOnlyList<Diagnostic> MergeRightSide(List<Diagnostic> items)
    {
        const string RightPrefix = "_claude_fs_right:";

        // Build lookup: normalised-uri → diagnostic for right-side items.
        var rightUris = items
            .Where(d => d.Uri.StartsWith(RightPrefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                d => d.Uri[RightPrefix.Length..],
                d => d,
                StringComparer.OrdinalIgnoreCase);

        if (rightUris.Count == 0) return items;

        // Exclude plain-URI items that have a right-side counterpart.
        return items
            .Where(d => !(!d.Uri.StartsWith(RightPrefix, StringComparison.OrdinalIgnoreCase)
                          && rightUris.ContainsKey(d.Uri)))
            .ToList();
    }
}

/// <summary>
/// Abstraction over IDE MCP getDiagnostics so the service can be tested without
/// a real IDE connection. The real implementation calls the MCP tool server.
/// </summary>
public interface IDiagnosticProvider
{
    Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Null implementation used when no IDE MCP server is connected.
/// </summary>
public sealed class NullDiagnosticProvider : IDiagnosticProvider
{
    public static readonly NullDiagnosticProvider Instance = new();
    public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Diagnostic>>([]);
}

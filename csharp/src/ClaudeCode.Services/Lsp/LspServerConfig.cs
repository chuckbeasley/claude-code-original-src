namespace ClaudeCode.Services.Lsp;

/// <summary>Configuration for a single LSP server instance.</summary>
/// <param name="Name">Human-readable name used in log messages and diagnostics.</param>
/// <param name="Command">Executable name or full path (e.g. <c>pylsp</c>, <c>omnisharp</c>).</param>
/// <param name="Args">Additional command-line arguments passed to the server process.</param>
/// <param name="Extensions">
///     File extensions this server handles, e.g. <c>[".cs", ".vb"]</c>.
///     Comparisons are case-insensitive.
/// </param>
/// <param name="WorkDir">
///     Working directory for the server process; also used as the LSP <c>rootUri</c>.
///     Defaults to <see cref="Directory.GetCurrentDirectory"/> when <see langword="null"/>.
/// </param>
public sealed record LspServerConfig(
    string Name,
    string Command,
    string[] Args,
    string[] Extensions,
    string? WorkDir = null);

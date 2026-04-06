namespace ClaudeCode.Core.Tools;

/// <summary>
/// Central registry for all <see cref="ITool"/> implementations available in the session.
/// Tools are keyed by their canonical <see cref="ITool.Name"/> and any declared
/// <see cref="ITool.Aliases"/>. All lookups are case-insensitive.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITool> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="tool"/> under its canonical name and all declared aliases.
    /// If a tool with the same name (or alias) was already registered it is replaced.
    /// </summary>
    /// <param name="tool">The tool to register. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tool"/> is <see langword="null"/>.</exception>
    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        _tools[tool.Name] = tool;

        foreach (var alias in tool.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                _aliases[alias] = tool;
        }
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered tool, checking canonical names
    /// first and then aliases. Returns <see langword="null"/> when not found.
    /// </summary>
    /// <param name="name">Canonical name or alias to look up (case-insensitive).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/>.</exception>
    public ITool? GetTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _tools.GetValueOrDefault(name) ?? _aliases.GetValueOrDefault(name);
    }

    /// <summary>
    /// Returns a snapshot of all tools registered under their canonical names.
    /// Aliases are not included in this collection.
    /// </summary>
    public IReadOnlyCollection<ITool> GetAll() => _tools.Values;
}

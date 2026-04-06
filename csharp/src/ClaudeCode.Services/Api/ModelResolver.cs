namespace ClaudeCode.Services.Api;

public static class ModelResolver
{
    // Well-known model aliases
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = "claude-opus-4-6",
        ["sonnet"] = "claude-sonnet-4-6",
        ["haiku"] = "claude-haiku-4-5-20251001",
    };

    /// <summary>
    /// Resolves the model to use, checking (in priority order):
    /// 1. Explicit override (from CLI --model flag or /model command)
    /// 2. ANTHROPIC_MODEL environment variable
    /// 3. Settings file model
    /// 4. Default model
    /// </summary>
    public static string Resolve(string? explicitModel = null, string? settingsModel = null)
    {
        var model = explicitModel
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
            ?? settingsModel
            ?? ApiConstants.DefaultModel;

        // Resolve aliases
        if (Aliases.TryGetValue(model, out var resolved))
            return resolved;

        return model;
    }

    /// <summary>
    /// Gets the small/fast model for quick operations (compaction, classification).
    /// </summary>
    public static string GetSmallFastModel()
    {
        return Environment.GetEnvironmentVariable("ANTHROPIC_SMALL_FAST_MODEL")
            ?? "claude-haiku-4-5-20251001";
    }

    /// <summary>
    /// Gets a short display name for a model ID.
    /// </summary>
    public static string GetDisplayName(string modelId)
    {
        // Extract meaningful name: "claude-sonnet-4-6" -> "Sonnet 4.6"
        var parts = modelId.Split('-');
        if (parts.Length >= 3 && parts[0] == "claude")
        {
            var family = char.ToUpper(parts[1][0]) + parts[1][1..];
            var version = string.Join(".", parts[2..].TakeWhile(p => int.TryParse(p, out _)));
            if (version.Length > 0)
                return $"{family} {version}";
            return family;
        }
        return modelId;
    }
}

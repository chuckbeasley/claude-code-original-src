namespace ClaudeCode.Services.Api;

/// <summary>Rate limit information extracted from Anthropic API response headers.</summary>
public record RateLimitInfo(
    int? RequestsLimit,
    int? RequestsRemaining,
    int? RequestsResetSeconds,
    int? TokensLimit,
    int? TokensRemaining,
    int? TokensResetSeconds,
    int? InputTokensLimit,
    int? InputTokensRemaining);

public class CostTracker
{
    private readonly Dictionary<string, ModelUsage> _modelUsage = new(StringComparer.OrdinalIgnoreCase);
    private double _totalCostUsd;
    private RateLimitInfo? _lastRateLimitInfo;
    private readonly object _lock = new();

    /// <summary>The most recently observed rate limit headers, or null if none received yet.</summary>
    public RateLimitInfo? LastRateLimitInfo
    {
        get { lock (_lock) return _lastRateLimitInfo; }
        private set { lock (_lock) _lastRateLimitInfo = value; }
    }

    /// <summary>Updates the stored rate limit information from the latest API response headers.</summary>
    public void UpdateRateLimitInfo(RateLimitInfo info) => LastRateLimitInfo = info;

    public void AddUsage(string model, ApiUsage usage)
    {
        lock (_lock)
        {
            if (!_modelUsage.TryGetValue(model, out var existing))
                existing = new ModelUsage();

            var pricing = GetPricing(model);
            var cost = CalculateCost(usage, pricing);

            _modelUsage[model] = existing with
            {
                InputTokens = existing.InputTokens + usage.InputTokens,
                OutputTokens = existing.OutputTokens + usage.OutputTokens,
                CacheReadInputTokens = existing.CacheReadInputTokens + usage.CacheReadInputTokens,
                CacheCreationInputTokens = existing.CacheCreationInputTokens + usage.CacheCreationInputTokens,
                CostUsd = existing.CostUsd + cost,
            };

            _totalCostUsd += cost;
        }
    }

    public double TotalCostUsd
    {
        get { lock (_lock) return _totalCostUsd; }
    }

    public int TotalInputTokens
    {
        get { lock (_lock) return _modelUsage.Values.Sum(u => u.InputTokens); }
    }

    public int TotalOutputTokens
    {
        get { lock (_lock) return _modelUsage.Values.Sum(u => u.OutputTokens); }
    }

    public IReadOnlyDictionary<string, ModelUsage> GetModelUsage()
    {
        lock (_lock) return new Dictionary<string, ModelUsage>(_modelUsage);
    }

    public string FormatCost() => FormatCost(TotalCostUsd);

    public static string FormatCost(double cost)
    {
        return cost switch
        {
            < 0.01 => $"${cost:F4}",
            < 1.0 => $"${cost:F3}",
            _ => $"${cost:F2}",
        };
    }

    public string FormatUsageSummary()
    {
        lock (_lock)
        {
            var input = TotalInputTokens;
            var output = TotalOutputTokens;
            return $"Cost: {FormatCost()} | Tokens: {FormatTokenCount(input)} in, {FormatTokenCount(output)} out";
        }
    }

    private static string FormatTokenCount(int tokens)
    {
        return tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
            >= 1_000 => $"{tokens / 1_000.0:F1}K",
            _ => tokens.ToString(),
        };
    }

    private static double CalculateCost(ApiUsage usage, ModelPricing pricing)
    {
        return (usage.InputTokens / 1_000_000.0) * pricing.InputPerMillion
             + (usage.OutputTokens / 1_000_000.0) * pricing.OutputPerMillion
             + (usage.CacheReadInputTokens / 1_000_000.0) * pricing.CacheReadPerMillion
             + (usage.CacheCreationInputTokens / 1_000_000.0) * pricing.CacheWritePerMillion;
    }

    private static ModelPricing GetPricing(string model)
    {
        if (ApiConstants.ModelPricing.TryGetValue(model, out var pricing))
            return pricing;

        // Fallback: try prefix matching for versioned model IDs
        foreach (var (key, value) in ApiConstants.ModelPricing)
        {
            if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        // Unknown model — use Sonnet pricing as default
        return new ModelPricing(3.0, 15.0, 0.3, 3.75);
    }
}

public record ModelUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadInputTokens { get; init; }
    public int CacheCreationInputTokens { get; init; }
    public double CostUsd { get; init; }
}

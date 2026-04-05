namespace ClaudeCode.Core.Tests;

using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;

public sealed class FeatureFlagsTests : IDisposable
{
    // Clean up env vars set in each test.
    private readonly List<string> _envVarsSet = new();

    public void Dispose()
    {
        foreach (var v in _envVarsSet)
            Environment.SetEnvironmentVariable(v, null);
        FeatureFlags.Load(null); // reset to defaults
    }

    private void SetEnv(string name, string value)
    {
        _envVarsSet.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public void Default_KnownFlag_ReturnsFalse()
    {
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void SettingsJson_SetsFlag_True()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        FeatureFlags.Load(config);
        Assert.True(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_OverridesSettingsJson_True()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = false } };
        SetEnv("CLAUDE_FEATURE_CRON", "1");
        FeatureFlags.Load(config);
        Assert.True(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_OverridesSettingsJson_False()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        SetEnv("CLAUDE_FEATURE_CRON", "0");
        FeatureFlags.Load(config);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_FalseString_ReturnsFalse()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "false");
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_EmptyString_ReturnsFalse()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "");
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void EnvVar_ArbitraryNonFalsy_ReturnsTrue()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "yes");
        FeatureFlags.Load(null);
        Assert.True(FeatureFlags.IsEnabled("cron"));
    }

    [Fact]
    public void UnknownFlag_ReturnsFalse()
    {
        FeatureFlags.Load(null);
        Assert.False(FeatureFlags.IsEnabled("nonexistent-flag-xyz"));
    }

    [Fact]
    public void GetAll_ContainsAllKnownFlags()
    {
        FeatureFlags.Load(null);
        var all = FeatureFlags.GetAll(null);
        Assert.Contains(all, r => r.Flag == "cron");
        Assert.Contains(all, r => r.Flag == "kairos");
        Assert.Contains(all, r => r.Flag == "voice");
    }

    [Fact]
    public void GetAll_SourceIsEnv_WhenEnvVarSet()
    {
        SetEnv("CLAUDE_FEATURE_CRON", "1");
        FeatureFlags.Load(null);
        var all = FeatureFlags.GetAll(null);
        var cron = all.Single(r => r.Flag == "cron");
        Assert.StartsWith("env", cron.Source);
    }

    [Fact]
    public void GetAll_SourceIsSettings_WhenSettingsJsonSet()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        FeatureFlags.Load(config);
        var all = FeatureFlags.GetAll(null);
        var cron = all.Single(r => r.Flag == "cron");
        Assert.Equal("settings.json", cron.Source);
    }

    [Fact]
    public void Load_IdempotentSecondCall_ResetsState()
    {
        var config = new GlobalConfig { Features = new Dictionary<string, bool> { ["cron"] = true } };
        FeatureFlags.Load(config);
        Assert.True(FeatureFlags.IsEnabled("cron"));

        FeatureFlags.Load(null); // second call resets to defaults
        Assert.False(FeatureFlags.IsEnabled("cron"));
    }
}

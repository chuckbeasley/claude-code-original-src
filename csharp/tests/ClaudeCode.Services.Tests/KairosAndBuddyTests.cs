namespace ClaudeCode.Services.Tests;

using ClaudeCode.Core.State;
using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;

public sealed class KairosAndBuddyTests : IDisposable
{
    public void Dispose()
    {
        // Reset all static state.
        ReplModeFlags.KairosEnabled = false;
        ReplModeFlags.BuddyEnabled  = false;
        FeatureFlags.Load(null);
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", null);
    }

    // ---- ReplModeFlags ----

    [Fact]
    public void KairosEnabled_DefaultIsFalse()
    {
        Assert.False(ReplModeFlags.KairosEnabled);
    }

    [Fact]
    public void BuddyEnabled_DefaultIsFalse()
    {
        Assert.False(ReplModeFlags.BuddyEnabled);
    }

    [Fact]
    public void KairosSystemPrompt_ContainsKeyPhrases()
    {
        Assert.Contains("assistant mode", ReplModeFlags.KairosSystemPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarifying question", ReplModeFlags.KairosSystemPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("numbered list", ReplModeFlags.KairosSystemPrompt,
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- Feature flag gate ----

    [Fact]
    public void AssistantCommand_FlagOff_PrintsEnableInstruction()
    {
        FeatureFlags.Load(null); // defaults — kairos = false
        var cmd = new ClaudeCode.Commands.AssistantCommand();

        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/assistant",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();

        Assert.Contains(output, s => s.Contains("CLAUDE_FEATURE_KAIROS"));
        Assert.False(ReplModeFlags.KairosEnabled); // did not toggle
    }

    [Fact]
    public void AssistantCommand_FlagOn_TogglesKairosEnabled()
    {
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", "1");
        FeatureFlags.Load(null);

        var cmd = new ClaudeCode.Commands.AssistantCommand();
        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/assistant",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.True(ReplModeFlags.KairosEnabled);

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.False(ReplModeFlags.KairosEnabled);
    }

    [Fact]
    public void BuddyCommand_FlagOff_PrintsEnableInstruction()
    {
        FeatureFlags.Load(null);
        var cmd = new ClaudeCode.Commands.BuddyCommand();

        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/buddy",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();

        Assert.Contains(output, s => s.Contains("CLAUDE_FEATURE_KAIROS"));
        Assert.False(ReplModeFlags.BuddyEnabled);
    }

    [Fact]
    public void BuddyCommand_FlagOn_TogglesBuddyEnabled()
    {
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", "1");
        FeatureFlags.Load(null);

        var cmd = new ClaudeCode.Commands.BuddyCommand();
        var output = new List<string>();
        var ctx = new ClaudeCode.Commands.CommandContext
        {
            RawInput = "/buddy",
            Args = [],
            Cwd = ".",
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.True(ReplModeFlags.BuddyEnabled);

        cmd.ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.False(ReplModeFlags.BuddyEnabled);
    }
}

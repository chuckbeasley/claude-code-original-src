namespace ClaudeCode.Services.Tests;

using ClaudeCode.Configuration;
using ClaudeCode.Core.State;

public sealed class KairosAndBuddyTests : IDisposable
{
    public void Dispose()
    {
        // Reset all static state.
        ReplModeFlags.KairosEnabled = false;
        ReplModeFlags.BuddyEnabled = false;
        Environment.SetEnvironmentVariable("CLAUDE_FEATURE_KAIROS", null);
        FeatureFlags.Load(null);
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
    public async Task AssistantCommand_FlagOff_PrintsEnableInstruction()
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

        await cmd.ExecuteAsync(ctx);

        Assert.Contains(output, s => s.Contains("CLAUDE_FEATURE_KAIROS"));
        Assert.False(ReplModeFlags.KairosEnabled); // did not toggle
    }

    [Fact]
    public async Task AssistantCommand_FlagOn_TogglesKairosEnabled()
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

        await cmd.ExecuteAsync(ctx);
        Assert.True(ReplModeFlags.KairosEnabled);

        await cmd.ExecuteAsync(ctx);
        Assert.False(ReplModeFlags.KairosEnabled);
    }

    [Fact]
    public async Task BuddyCommand_FlagOff_PrintsEnableInstruction()
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

        await cmd.ExecuteAsync(ctx);

        Assert.Contains(output, s => s.Contains("CLAUDE_FEATURE_KAIROS"));
        Assert.False(ReplModeFlags.BuddyEnabled);
    }

    [Fact]
    public async Task BuddyCommand_FlagOn_TogglesBuddyEnabled()
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

        await cmd.ExecuteAsync(ctx);
        Assert.True(ReplModeFlags.BuddyEnabled);

        await cmd.ExecuteAsync(ctx);
        Assert.False(ReplModeFlags.BuddyEnabled);
    }
}

namespace ClaudeCode.Services.Tests;

using System.Text.Json;
using ClaudeCode.Commands;
using ClaudeCode.Services.Plugins;

public sealed class PluginCommandTests
{
    [Fact]
    public void PluginManifest_DeserializesCommands()
    {
        var json = """
            {
              "name": "my-plugin",
              "version": "1.0.0",
              "commands": [
                { "name": "deploy", "description": "Deploy to env", "script": "deploy.sh" }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Single(manifest.Commands!);
        Assert.Equal("deploy", manifest.Commands![0].Name);
        Assert.Equal("deploy.sh", manifest.Commands![0].Script);
    }

    [Fact]
    public void ScriptPluginCommand_NameGetsSlashPrefix()
    {
        var def = new PluginCommandDefinition { Name = "deploy", Script = "deploy.sh", Description = "test" };
        var dir = Path.GetTempPath();
        var cmd = new ScriptPluginCommand(def, dir);
        Assert.Equal("/deploy", cmd.Name);
    }

    [Fact]
    public void ScriptPluginCommand_AlreadySlashedName_NotDoubled()
    {
        var def = new PluginCommandDefinition { Name = "/deploy", Script = "deploy.sh", Description = "test" };
        var dir = Path.GetTempPath();
        var cmd = new ScriptPluginCommand(def, dir);
        Assert.Equal("/deploy", cmd.Name);
    }

    [Fact]
    public void ScriptPluginCommand_MissingName_Throws()
    {
        var def = new PluginCommandDefinition { Script = "deploy.sh" };
        Assert.Throws<ArgumentException>(() => new ScriptPluginCommand(def, Path.GetTempPath()));
    }

    [Fact]
    public void ScriptPluginCommand_MissingScript_Throws()
    {
        var def = new PluginCommandDefinition { Name = "deploy" };
        Assert.Throws<ArgumentException>(() => new ScriptPluginCommand(def, Path.GetTempPath()));
    }

    [Fact]
    public async Task ScriptPluginCommand_ScriptNotFound_WritesError()
    {
        var def = new PluginCommandDefinition
        {
            Name = "missing",
            Script = "nonexistent_script_xyz.sh",
            Description = "test"
        };
        var dir = Path.GetTempPath();
        var cmd = new ScriptPluginCommand(def, dir);

        var output = new List<string>();
        var ctx = new CommandContext
        {
            RawInput = "/missing",
            Args = [],
            Cwd = dir,
            Write = output.Add,
            WriteMarkup = output.Add,
        };

        var result = await cmd.ExecuteAsync(ctx);

        Assert.True(result);
        Assert.Contains(output, s => s.Contains("not found"));
    }

    [Fact]
    public void LoadCommands_EmptyDir_ReturnsEmpty()
    {
        var loader = new PluginLoader();
        // temp dir has no .claude/plugins/ — should return empty and not throw
        var result = loader.LoadCommands(Path.GetTempPath()).ToList();
        Assert.Empty(result);
    }
}

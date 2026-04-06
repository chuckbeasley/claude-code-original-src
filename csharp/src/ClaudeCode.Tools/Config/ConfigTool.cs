namespace ClaudeCode.Tools.Config;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCode.Configuration;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="ConfigTool"/>.</summary>
public record ConfigInput
{
    /// <summary>
    /// The dot-separated setting name to read or write (e.g. <c>"model"</c>,
    /// <c>"permissions.defaultMode"</c>).
    /// </summary>
    [JsonPropertyName("setting")]
    public required string Setting { get; init; }

    /// <summary>
    /// When provided, sets the configuration value to this JSON element.
    /// When omitted (or null), the current value of <see cref="Setting"/> is returned.
    /// Both read and write operations are supported.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="ConfigTool"/>.</summary>
/// <param name="Setting">The setting that was read or written.</param>
/// <param name="CurrentValue">The current (post-operation) value, serialised as a JSON string.</param>
/// <param name="Message">Human-readable summary of the operation.</param>
public record ConfigOutput(string Setting, string? CurrentValue, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Reads and writes application settings. Settings are sourced from the merged
/// <see cref="IConfigProvider"/> and written to the project-local
/// <c>.claude/settings.json</c> file.
/// </summary>
public sealed class ConfigTool : Tool<ConfigInput, ConfigOutput>
{
    private readonly IConfigProvider _configProvider;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            setting = new { type = "string", description = "The setting name to read (e.g. 'model', 'verbose')" },
            value   = new { description = "Value to set; omit to read the current value" },
        },
        required = new[] { "setting" },
    });

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="ConfigTool"/> with the given configuration provider.
    /// </summary>
    /// <param name="configProvider">
    /// The merged configuration provider. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configProvider"/> is <see langword="null"/>.
    /// </exception>
    public ConfigTool(IConfigProvider configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "Config";

    /// <inheritdoc/>
    public override string[] Aliases => ["config", "settings"];

    /// <inheritdoc/>
    public override string? SearchHint => "read or write application configuration settings";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Reads or writes an application setting by name. " +
            "Provide `value` to write; omit it to read the current value.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `Config` to read or write a configuration setting. " +
            "Provide `setting` (e.g. `model`, `verbose`) to read its value. " +
            "Provide both `setting` and `value` to write — " +
            "the value is persisted to `.claude/settings.json` in the current working directory.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "Config";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("setting", out var setting) &&
            setting.ValueKind == JsonValueKind.String)
        {
            bool isWrite = input.Value.TryGetProperty("value", out var val) &&
                           val.ValueKind != JsonValueKind.Null &&
                           val.ValueKind != JsonValueKind.Undefined;
            return isWrite
                ? $"Writing config '{setting.GetString()}'"
                : $"Reading config '{setting.GetString()}'";
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input)
    {
        if (!input.TryGetProperty("value", out var val))
            return true;
        return val.ValueKind == JsonValueKind.Null || val.ValueKind == JsonValueKind.Undefined;
    }

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        ConfigInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Setting))
            return Task.FromResult(ValidationResult.Failure("setting must not be empty."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override ConfigInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<ConfigInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize ConfigInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(ConfigOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.CurrentValue is not null)
            return $"{result.Setting} = {result.CurrentValue}\n{result.Message}";

        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<ConfigOutput>> ExecuteAsync(
        ConfigInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Write path — persist to the project settings file.
        if (input.Value.HasValue &&
            input.Value.Value.ValueKind != JsonValueKind.Null &&
            input.Value.Value.ValueKind != JsonValueKind.Undefined)
        {
            var writeResult = WriteSettingValue(input.Setting, input.Value.Value, context.Cwd);
            return Task.FromResult(new ToolResult<ConfigOutput> { Data = writeResult });
        }

        // Read path — resolve from merged settings.
        var value = ResolveSettingValue(input.Setting);
        var output = new ConfigOutput(
            input.Setting,
            value,
            value is not null
                ? $"Setting '{input.Setting}' read successfully."
                : $"Setting '{input.Setting}' is not set or not recognised.");

        return Task.FromResult(new ToolResult<ConfigOutput> { Data = output });
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a setting name to its string representation from the current
    /// merged <see cref="IConfigProvider.Settings"/>.
    /// Supports top-level property names (case-insensitive) and falls back to
    /// the <see cref="Configuration.Settings.SettingsJson.ExtensionData"/> catch-all.
    /// </summary>
    private string? ResolveSettingValue(string settingName)
    {
        var settings = _configProvider.Settings;
        var settingsElement = JsonSerializer.SerializeToElement(settings);

        // Support dot-path navigation (e.g., "permissions.defaultMode")
        var parts = settingName.Split('.');
        var current = settingsElement;

        foreach (var part in parts)
        {
            JsonElement next;
            if (current.TryGetProperty(part, out next))
            {
                current = next;
                continue;
            }

            // Case-insensitive fallback
            bool found = false;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    current = property.Value;
                    found = true;
                    break;
                }
            }
            if (!found) return null;
        }

        return current.ValueKind == JsonValueKind.Undefined ? null : current.ToString();
    }

    /// <summary>
    /// Writes a setting to the project-local settings file <c>.claude/settings.json</c>.
    /// Creates the file (and directory) if it does not yet exist.
    /// </summary>
    private ConfigOutput WriteSettingValue(string settingName, JsonElement value, string cwd)
    {
        var settingsPath = Path.Combine(cwd, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        // Load existing file or start with an empty object.
        Dictionary<string, JsonElement> existing;
        if (File.Exists(settingsPath))
        {
            try
            {
                var text = File.ReadAllText(settingsPath);
                existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)
                    ?? new Dictionary<string, JsonElement>();
            }
            catch
            {
                existing = new Dictionary<string, JsonElement>();
            }
        }
        else
        {
            existing = new Dictionary<string, JsonElement>();
        }

        // Dot-path support: split on '.' and merge into nested JSON.
        var parts = settingName.Split('.');
        if (parts.Length == 1)
        {
            // Simple key — write directly.
            existing[settingName] = value;
        }
        else
        {
            // Navigate/build nested objects using JsonNode for mutation.
            var root = System.Text.Json.Nodes.JsonObject.Create(
                existing.Count > 0
                    ? JsonSerializer.SerializeToElement(existing)
                    : JsonSerializer.SerializeToElement(new Dictionary<string, object?>()))!;

            var node = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (node[part] is System.Text.Json.Nodes.JsonObject child)
                {
                    node = child;
                }
                else
                {
                    var newObj = new System.Text.Json.Nodes.JsonObject();
                    node[part] = newObj;
                    node = newObj;
                }
            }

            var leaf = parts[^1];
            node[leaf] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());

            // Merge back into existing dict.
            existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(root.ToJsonString())
                ?? existing;
        }

        // Write back with indented formatting.
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(existing, opts));

        // Reload the config provider so the change takes effect immediately.
        _configProvider.Reload();

        var displayValue = value.ValueKind == JsonValueKind.String
            ? $"\"{value.GetString()}\""
            : value.GetRawText();

        return new ConfigOutput(
            settingName,
            displayValue,
            $"Setting '{settingName}' updated to {displayValue} in {settingsPath}.");
    }
}

namespace ClaudeCode.Tools.Cron;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Shared cron state
// ---------------------------------------------------------------------------

/// <summary>
/// A registered cron job stored in <see cref="CronState"/>.
/// </summary>
/// <param name="Id">Unique, auto-assigned job identifier (e.g. <c>"cron-1"</c>).</param>
/// <param name="CronExpr">Standard cron expression defining the schedule.</param>
/// <param name="Prompt">The prompt text that runs on each trigger.</param>
/// <param name="Recurring">
/// When <see langword="true"/> the job fires on every matching schedule tick;
/// when <see langword="false"/> it fires once and is then considered complete.
/// </param>
/// <param name="Durable">
/// When <see langword="true"/> the job survives session restarts.
/// Jobs marked as durable are persisted to ~/.claude/cron-jobs.json and reloaded on startup.
/// </param>
/// <param name="CreatedAt">UTC timestamp when the job was registered.</param>
public record CronJob(
    string Id,
    string CronExpr,
    string Prompt,
    bool Recurring,
    bool Durable,
    DateTimeOffset CreatedAt);

/// <summary>
/// In-process, static storage for cron jobs shared across
/// <see cref="CronCreateTool"/>, <see cref="CronDeleteTool"/>, and <see cref="CronListTool"/>.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> and
/// <see cref="Interlocked"/> for ID generation.
/// </summary>
public static class CronState
{
    private static int _nextId;

    /// <summary>All currently registered cron jobs keyed by their ID.</summary>
    public static ConcurrentDictionary<string, CronJob> Jobs { get; } = new();

    /// <summary>
    /// Channel that the scheduler writes due cron prompts to.
    /// The REPL loop reads from this before prompting for user input.
    /// </summary>
    public static Channel<string> PendingPrompts { get; } =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Tracks the last UTC minute each job fired, to prevent double-firing.</summary>
    public static ConcurrentDictionary<string, DateTimeOffset> LastFired { get; } = new();

    /// <summary>Returns the next unique job identifier in the form <c>cron-N</c>.</summary>
    public static string NextId() => $"cron-{Interlocked.Increment(ref _nextId)}";

    /// <summary>
    /// Advances the ID counter past all IDs currently in <see cref="Jobs"/>, preventing
    /// collisions when durable jobs are reloaded at startup and a new job is created
    /// before the async load completes.
    /// </summary>
    internal static void AdvanceIdPastLoadedJobs()
    {
        if (Jobs.Count == 0) return;

        // Extract numeric part from IDs like "cron-7"
        var maxId = Jobs.Keys
            .Select(k => { var parts = k.Split('-'); return parts.Length == 2 && int.TryParse(parts[1], out var n) ? n : 0; })
            .DefaultIfEmpty(0)
            .Max();

        // Advance _nextId if needed (thread-safe compare-exchange loop)
        int current;
        do { current = _nextId; }
        while (current <= maxId && Interlocked.CompareExchange(ref _nextId, maxId + 1, current) != current);
    }
}

/// <summary>
/// Serialization DTO for a durable cron job persisted to <c>~/.claude/cron-jobs.json</c>.
/// </summary>
internal sealed record CronJobDurableEntry(
    [property: JsonPropertyName("cronExpression")] string CronExpression,
    [property: JsonPropertyName("prompt")]         string Prompt,
    [property: JsonPropertyName("durable")]        bool   Durable,
    [property: JsonPropertyName("recurring")]      bool   Recurring);

// ===========================================================================
// CronCreateTool
// ===========================================================================

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="CronCreateTool"/>.</summary>
public record CronCreateInput
{
    /// <summary>Standard 5-field cron expression (e.g. <c>"0 9 * * 1"</c> for 09:00 every Monday).</summary>
    [JsonPropertyName("cron")]
    public required string Cron { get; init; }

    /// <summary>The prompt text that will be run on each schedule trigger.</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>
    /// When <see langword="true"/> (default) the job runs on every matching tick.
    /// When <see langword="false"/> the job fires once only.
    /// </summary>
    [JsonPropertyName("recurring")]
    public bool? Recurring { get; init; }

    /// <summary>
    /// When <see langword="true"/> the job should survive session restarts.
    /// Jobs marked as durable are persisted to ~/.claude/cron-jobs.json and reloaded on startup.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    [JsonPropertyName("durable")]
    public bool? Durable { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="CronCreateTool"/>.</summary>
/// <param name="JobId">The auto-assigned identifier for the new cron job.</param>
/// <param name="Message">Human-readable confirmation of the job creation.</param>
public record CronCreateOutput(string JobId, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Registers a new cron job in <see cref="CronState"/>. Returns the auto-assigned job ID.
/// </summary>
public sealed class CronCreateTool : Tool<CronCreateInput, CronCreateOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            cron      = new { type = "string", description = "Standard 5-field cron expression (e.g. '0 9 * * 1')" },
            prompt    = new { type = "string", description = "Prompt text executed on each trigger" },
            recurring = new { type = "boolean", description = "Whether the job recurs (default true)" },
            durable   = new { type = "boolean", description = "Whether the job survives session restarts; persisted to ~/.claude/cron-jobs.json (default false)" },
        },
        required = new[] { "cron", "prompt" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "CronCreate";

    /// <inheritdoc/>
    public override string[] Aliases => ["cron_create", "create_cron"];

    /// <inheritdoc/>
    public override string? SearchHint => "schedule recurring or one-shot cron jobs";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Creates a scheduled cron job that runs a prompt on a recurring or one-shot schedule. " +
            "Returns the assigned job ID.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `CronCreate` to schedule a prompt. " +
            "Provide a standard 5-field `cron` expression and a `prompt` string. " +
            "Set `recurring` to false to run only once. " +
            "Use `CronDelete` to remove a job and `CronList` to list all jobs.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "CronCreate";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("cron", out var cron) &&
            cron.ValueKind == JsonValueKind.String)
        {
            return $"Scheduling cron job '{cron.GetString()}'";
        }
        return "Creating cron job";
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        CronCreateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Cron))
            return Task.FromResult(ValidationResult.Failure("cron must not be empty."));

        if (string.IsNullOrWhiteSpace(input.Prompt))
            return Task.FromResult(ValidationResult.Failure("prompt must not be empty."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override CronCreateInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<CronCreateInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize CronCreateInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(CronCreateOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Job ID: {result.JobId}\n{result.Message}";
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<CronCreateOutput>> ExecuteAsync(
        CronCreateInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var id = CronState.NextId();
        var job = new CronJob(
            Id: id,
            CronExpr: input.Cron.Trim(),
            Prompt: input.Prompt,
            Recurring: input.Recurring ?? true,
            Durable: input.Durable ?? false,
            CreatedAt: DateTimeOffset.UtcNow);

        CronState.Jobs[id] = job;

        // Persist durable jobs so they survive session restarts.
        if (job.Durable)
            await SaveDurableJobsAsync().ConfigureAwait(false);

        var message =
            $"Cron job '{id}' created. " +
            $"Schedule: '{job.CronExpr}'. " +
            $"Recurring: {job.Recurring}. " +
            $"Durable: {job.Durable}.";

        return new ToolResult<CronCreateOutput>
        {
            Data = new CronCreateOutput(id, message),
        };
    }

    // -----------------------------------------------------------------------
    // Durable persistence
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads durable jobs persisted from a previous session on first use of this tool type.
    /// Runs as a fire-and-forget background task from the static constructor.
    /// </summary>
#pragma warning disable CS4014 // fire-and-forget is intentional in the static constructor
    static CronCreateTool()
    {
        _ = LoadDurableJobsAsync();
    }
#pragma warning restore CS4014

    private static async Task LoadDurableJobsAsync()
    {
        var filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "cron-jobs.json");

        if (!File.Exists(filePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<CronJobDurableEntry>>(json);
            if (entries is null) return;

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.CronExpression) ||
                    string.IsNullOrWhiteSpace(entry.Prompt))
                    continue;

                var id = CronState.NextId();
                var job = new CronJob(
                    Id: id,
                    CronExpr: entry.CronExpression,
                    Prompt: entry.Prompt,
                    Recurring: entry.Recurring,
                    Durable: true,
                    CreatedAt: DateTimeOffset.UtcNow);

                CronState.Jobs.TryAdd(id, job);
            }

            // Advance the ID counter past all loaded IDs to prevent collisions
            CronState.AdvanceIdPastLoadedJobs();
        }
        catch { /* best-effort; ignore corrupt or missing file */ }
    }

    private static async Task SaveDurableJobsAsync()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "cron-jobs.json");

        var durableJobs = CronState.Jobs.Values
            .Where(j => j.Durable)
            .Select(j => new CronJobDurableEntry(j.CronExpr, j.Prompt, j.Durable, j.Recurring))
            .ToList();

        try
        {
            var json = JsonSerializer.Serialize(durableJobs,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
        catch { /* best-effort; non-fatal persistence failure */ }
    }
}

// ===========================================================================
// CronDeleteTool
// ===========================================================================

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="CronDeleteTool"/>.</summary>
public record CronDeleteInput
{
    /// <summary>The ID of the cron job to remove (e.g. <c>"cron-1"</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="CronDeleteTool"/>.</summary>
/// <param name="JobId">The ID of the job that was removed.</param>
/// <param name="Message">Human-readable confirmation or error message.</param>
public record CronDeleteOutput(string JobId, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Removes a cron job from <see cref="CronState"/> by its ID. Returns a confirmation message.
/// </summary>
public sealed class CronDeleteTool : Tool<CronDeleteInput, CronDeleteOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string", description = "ID of the cron job to delete (e.g. 'cron-1')" },
        },
        required = new[] { "id" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "CronDelete";

    /// <inheritdoc/>
    public override string[] Aliases => ["cron_delete", "delete_cron"];

    /// <inheritdoc/>
    public override string? SearchHint => "delete a scheduled cron job by ID";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Removes a cron job from the schedule by its job ID.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `CronDelete` to remove a scheduled job. " +
            "Provide the `id` returned when the job was created (e.g. `cron-1`). " +
            "Use `CronList` to see all current job IDs.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "CronDelete";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return $"Deleting cron job '{id.GetString()}'";
        }
        return "Deleting cron job";
    }

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ValidationResult> ValidateInputAsync(
        CronDeleteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Id))
            return Task.FromResult(ValidationResult.Failure("id must not be empty."));

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override CronDeleteInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<CronDeleteInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize CronDeleteInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(CronDeleteOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Message;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<CronDeleteOutput>> ExecuteAsync(
        CronDeleteInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (CronState.Jobs.TryRemove(input.Id, out _))
        {
            return Task.FromResult(new ToolResult<CronDeleteOutput>
            {
                Data = new CronDeleteOutput(input.Id, $"Cron job '{input.Id}' deleted successfully."),
            });
        }

        return Task.FromResult(new ToolResult<CronDeleteOutput>
        {
            Data = new CronDeleteOutput(input.Id, $"Cron job '{input.Id}' not found."),
        });
    }
}

// ===========================================================================
// CronListTool
// ===========================================================================

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Empty input record for the <see cref="CronListTool"/> (no parameters required).</summary>
public record CronListInput;

/// <summary>Strongly-typed output for the <see cref="CronListTool"/>.</summary>
/// <param name="Jobs">Snapshot of all currently registered cron jobs.</param>
public record CronListOutput(IReadOnlyList<CronJob> Jobs);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Lists all cron jobs currently registered in <see cref="CronState"/>.
/// Read-only; no parameters required.
/// </summary>
public sealed class CronListTool : Tool<CronListInput, CronListOutput>
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "CronList";

    /// <inheritdoc/>
    public override string[] Aliases => ["cron_list", "list_crons"];

    /// <inheritdoc/>
    public override string? SearchHint => "list all scheduled cron jobs";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult("Lists all currently registered cron jobs and their schedules.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `CronList` to see all scheduled jobs. " +
            "No parameters are required. " +
            "Use `CronCreate` to add a job or `CronDelete` to remove one.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "CronList";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
        => "Listing cron jobs";

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override bool IsReadOnly(JsonElement input) => true;

    /// <inheritdoc/>
    public override bool IsConcurrencySafe(JsonElement input) => true;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override CronListInput DeserializeInput(JsonElement json)
    {
        // No properties expected; instantiate empty record regardless of content.
        return new CronListInput();
    }

    /// <inheritdoc/>
    public override string MapResultToString(CronListOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Jobs.Count == 0)
            return "No cron jobs registered.";

        var sb = new StringBuilder();
        sb.AppendLine($"{result.Jobs.Count} cron job(s) registered:");
        sb.AppendLine();

        foreach (var job in result.Jobs)
        {
            sb.Append("- ").Append(job.Id)
              .Append(" | ").Append(job.CronExpr)
              .Append(" | recurring=").Append(job.Recurring)
              .Append(" | durable=").Append(job.Durable)
              .Append(" | created=").AppendLine(job.CreatedAt.ToString("u"));
            sb.Append("  Prompt: ").AppendLine(job.Prompt);
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override Task<ToolResult<CronListOutput>> ExecuteAsync(
        CronListInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Take a consistent snapshot of the dictionary values.
        var snapshot = CronState.Jobs.Values.ToList();

        return Task.FromResult(new ToolResult<CronListOutput>
        {
            Data = new CronListOutput(snapshot),
        });
    }
}

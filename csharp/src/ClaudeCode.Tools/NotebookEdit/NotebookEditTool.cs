namespace ClaudeCode.Tools.NotebookEdit;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeCode.Core.Tools;

// ---------------------------------------------------------------------------
// Input / Output records
// ---------------------------------------------------------------------------

/// <summary>Strongly-typed input for the <see cref="NotebookEditTool"/>.</summary>
public record NotebookEditInput
{
    /// <summary>Absolute or working-directory-relative path to the .ipynb file.</summary>
    [JsonPropertyName("notebook_path")]
    public required string NotebookPath { get; init; }

    /// <summary>The new source content for the target cell.</summary>
    [JsonPropertyName("new_source")]
    public required string NewSource { get; init; }

    /// <summary>
    /// Optional cell identifier. Accepts a Jupyter cell ID string or the shorthand
    /// <c>cell-N</c> (zero-based index). When omitted the first cell is targeted for
    /// replace/delete, or a new cell is appended for insert mode.
    /// </summary>
    [JsonPropertyName("cell_id")]
    public string? CellId { get; init; }

    /// <summary>
    /// Cell type for newly inserted cells. Must be <c>"code"</c> or <c>"markdown"</c>.
    /// Required when <see cref="EditMode"/> is <c>"insert"</c>.
    /// </summary>
    [JsonPropertyName("cell_type")]
    public string? CellType { get; init; }

    /// <summary>
    /// Edit mode: <c>"replace"</c> (default), <c>"insert"</c>, or <c>"delete"</c>.
    /// </summary>
    [JsonPropertyName("edit_mode")]
    public string? EditMode { get; init; }
}

/// <summary>Strongly-typed output for the <see cref="NotebookEditTool"/>.</summary>
/// <param name="Success">Whether the edit completed without error.</param>
/// <param name="Message">Human-readable summary of the operation performed.</param>
public record NotebookEditOutput(bool Success, string Message);

// ---------------------------------------------------------------------------
// Tool implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Edits a cell inside a Jupyter notebook (<c>.ipynb</c>) file.
/// Supports replacing, inserting, and deleting cells identified by cell ID or
/// zero-based index (<c>cell-N</c>).
/// </summary>
public sealed class NotebookEditTool : Tool<NotebookEditInput, NotebookEditOutput>
{
    private const string ModeReplace = "replace";
    private const string ModeInsert = "insert";
    private const string ModeDelete = "delete";
    private const string CellTypeCode = "code";
    private const string CellTypeMarkdown = "markdown";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            notebook_path = new { type = "string", description = "Path to the .ipynb notebook file" },
            new_source = new { type = "string", description = "New source content for the cell" },
            cell_id = new { type = "string", description = "Cell ID or 'cell-N' (zero-based index)" },
            cell_type = new { type = "string", @enum = new[] { "code", "markdown" }, description = "Cell type (required for insert mode)" },
            edit_mode = new { type = "string", @enum = new[] { "replace", "insert", "delete" }, description = "Edit operation; defaults to 'replace'" },
        },
        required = new[] { "notebook_path", "new_source" },
    });

    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override string Name => "NotebookEdit";

    /// <inheritdoc/>
    public override string[] Aliases => ["notebook_edit", "edit_notebook"];

    /// <inheritdoc/>
    public override string? SearchHint => "edit Jupyter notebook cells";

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override JsonElement GetInputSchema() => Schema;

    /// <inheritdoc/>
    public override Task<string> GetDescriptionAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Edits a cell inside a Jupyter notebook (.ipynb) file. " +
            "Supports replace, insert, and delete operations identified by cell ID or index.");

    /// <inheritdoc/>
    public override Task<string> GetPromptAsync(CancellationToken ct = default)
        => Task.FromResult(
            "Use `NotebookEdit` to modify Jupyter notebook cells. " +
            "Provide `notebook_path` (must end in .ipynb) and `new_source`. " +
            "Target a cell with `cell_id` (a Jupyter cell ID or `cell-N` for zero-based index). " +
            "Set `edit_mode` to `replace` (default), `insert`, or `delete`. " +
            "When inserting, `cell_type` (`code` or `markdown`) is required.");

    /// <inheritdoc/>
    public override string UserFacingName(JsonElement? input = null) => "NotebookEdit";

    /// <inheritdoc/>
    public override string? GetActivityDescription(JsonElement? input = null)
    {
        if (input is null) return null;
        if (input.Value.TryGetProperty("notebook_path", out var path) &&
            path.ValueKind == JsonValueKind.String)
        {
            var mode = "Editing";
            if (input.Value.TryGetProperty("edit_mode", out var editMode) &&
                editMode.ValueKind == JsonValueKind.String)
            {
                mode = editMode.GetString() switch
                {
                    ModeInsert => "Inserting into",
                    ModeDelete => "Deleting from",
                    _ => "Editing",
                };
            }
            return $"{mode} {System.IO.Path.GetFileName(path.GetString())}";
        }
        return null;
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
        NotebookEditInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.NotebookPath))
            return Task.FromResult(ValidationResult.Failure("notebook_path must not be empty."));

        if (!input.NotebookPath.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ValidationResult.Failure("notebook_path must be a .ipynb file."));

        var mode = (input.EditMode ?? ModeReplace).ToLowerInvariant();
        if (mode is not ModeReplace and not ModeInsert and not ModeDelete)
            return Task.FromResult(ValidationResult.Failure(
                $"edit_mode must be one of: '{ModeReplace}', '{ModeInsert}', '{ModeDelete}'."));

        if (mode == ModeInsert)
        {
            if (string.IsNullOrWhiteSpace(input.CellType))
                return Task.FromResult(ValidationResult.Failure(
                    "cell_type is required when edit_mode is 'insert'."));

            var cellType = input.CellType!.ToLowerInvariant();
            if (cellType is not CellTypeCode and not CellTypeMarkdown)
                return Task.FromResult(ValidationResult.Failure(
                    $"cell_type must be '{CellTypeCode}' or '{CellTypeMarkdown}'."));
        }

        return Task.FromResult(ValidationResult.Success);
    }

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override NotebookEditInput DeserializeInput(JsonElement json)
    {
        var result = JsonSerializer.Deserialize<NotebookEditInput>(json.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialize NotebookEditInput: result was null.");
        return result;
    }

    /// <inheritdoc/>
    public override string MapResultToString(NotebookEditOutput result, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success ? result.Message : $"Error: {result.Message}";
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<ToolResult<NotebookEditOutput>> ExecuteAsync(
        NotebookEditInput input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var notebookPath = System.IO.Path.IsPathRooted(input.NotebookPath)
            ? input.NotebookPath
            : System.IO.Path.Combine(context.Cwd, input.NotebookPath);

        if (!System.IO.File.Exists(notebookPath))
        {
            return Result(false, $"Notebook file not found: {notebookPath}");
        }

        string rawJson;
        try
        {
            rawJson = await System.IO.File.ReadAllTextAsync(notebookPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Result(false, $"Failed to read notebook: {ex.Message}");
        }

        JsonNode? notebook;
        try
        {
            notebook = JsonNode.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            return Result(false, $"Failed to parse notebook JSON: {ex.Message}");
        }

        if (notebook is null)
            return Result(false, "Notebook JSON parsed as null.");

        var cells = notebook["cells"] as JsonArray;
        if (cells is null)
            return Result(false, "Notebook does not contain a 'cells' array.");

        var mode = (input.EditMode ?? ModeReplace).ToLowerInvariant();
        var output = mode switch
        {
            ModeReplace => ApplyReplace(cells, input),
            ModeInsert  => ApplyInsert(cells, input),
            ModeDelete  => ApplyDelete(cells, input),
            _           => Result(false, $"Unknown edit_mode '{mode}'."),
        };

        if (!output.Data.Success)
            return output;

        try
        {
            var serialised = notebook.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(notebookPath, serialised, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Result(false, $"Failed to write notebook: {ex.Message}");
        }

        return output;
    }

    // -----------------------------------------------------------------------
    // Private edit helpers
    // -----------------------------------------------------------------------

    private static ToolResult<NotebookEditOutput> ApplyReplace(JsonArray cells, NotebookEditInput input)
    {
        int index = ResolveCellIndex(cells, input.CellId);
        if (index < 0)
            return Result(false, BuildNotFoundMessage(input.CellId, cells.Count));

        var cell = cells[index] as JsonObject;
        if (cell is null)
            return Result(false, $"Cell at index {index} is not a JSON object.");

        cell["source"] = JsonValue.Create(input.NewSource);
        return Result(true, $"Replaced source of cell at index {index}.");
    }

    private static ToolResult<NotebookEditOutput> ApplyInsert(JsonArray cells, NotebookEditInput input)
    {
        var cellType = input.CellType!.ToLowerInvariant();
        var newCell = BuildCell(cellType, input.NewSource);

        int insertAt;
        if (string.IsNullOrWhiteSpace(input.CellId))
        {
            // Append at end when no cell_id given.
            insertAt = cells.Count;
        }
        else
        {
            int refIndex = ResolveCellIndex(cells, input.CellId);
            if (refIndex < 0)
                return Result(false, BuildNotFoundMessage(input.CellId, cells.Count));
            // Insert after the referenced cell.
            insertAt = refIndex + 1;
        }

        cells.Insert(insertAt, newCell);
        return Result(true, $"Inserted {cellType} cell at index {insertAt}.");
    }

    private static ToolResult<NotebookEditOutput> ApplyDelete(JsonArray cells, NotebookEditInput input)
    {
        int index = ResolveCellIndex(cells, input.CellId);
        if (index < 0)
            return Result(false, BuildNotFoundMessage(input.CellId, cells.Count));

        cells.RemoveAt(index);
        return Result(true, $"Deleted cell at index {index}.");
    }

    /// <summary>
    /// Resolves a cell identifier to a zero-based index within <paramref name="cells"/>.
    /// Accepts <c>cell-N</c> (zero-based numeric index) or a Jupyter cell ID string.
    /// Returns -1 when the identifier does not match any cell.
    /// </summary>
    private static int ResolveCellIndex(JsonArray cells, string? cellId)
    {
        if (string.IsNullOrWhiteSpace(cellId))
            return cells.Count > 0 ? 0 : -1;

        // Shorthand: "cell-N" → zero-based index
        if (cellId.StartsWith("cell-", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = cellId["cell-".Length..];
            if (int.TryParse(suffix, out int idx) && idx >= 0 && idx < cells.Count)
                return idx;
            return -1;
        }

        // Jupyter cell ID lookup
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] is JsonObject obj &&
                obj["id"] is JsonValue idVal &&
                string.Equals(idVal.GetValue<string>(), cellId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static JsonObject BuildCell(string cellType, string source)
    {
        var cell = new JsonObject
        {
            ["cell_type"] = JsonValue.Create(cellType),
            ["source"] = JsonValue.Create(source),
            ["metadata"] = new JsonObject(),
        };

        if (cellType == CellTypeCode)
        {
            cell["outputs"] = new JsonArray();
            cell["execution_count"] = JsonValue.Create<int?>(null);
        }

        return cell;
    }

    private static string BuildNotFoundMessage(string? cellId, int count)
        => string.IsNullOrWhiteSpace(cellId)
            ? "Notebook has no cells."
            : $"Cell '{cellId}' not found. Notebook has {count} cell(s).";

    private static ToolResult<NotebookEditOutput> Result(bool success, string message)
        => new() { Data = new NotebookEditOutput(success, message) };
}

namespace ClaudeCode.Core.Tools;

using System.Text.Json;
using ClaudeCode.Core.Messages;
using ClaudeCode.Core.Permissions;

/// <summary>
/// Generic abstract base class for all Claude Code tools.
/// Provides strongly-typed input/output handling and default implementations for
/// the optional <see cref="ITool"/> members.
/// </summary>
/// <typeparam name="TInput">
/// The strongly-typed input model. Must be a reference type so that JSON
/// deserialisation can produce a null-safe instance.
/// </typeparam>
/// <typeparam name="TOutput">The strongly-typed result produced by this tool.</typeparam>
public abstract class Tool<TInput, TOutput> : ITool where TInput : class
{
    // -----------------------------------------------------------------------
    // ITool identity
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public virtual string[] Aliases => [];

    /// <inheritdoc/>
    public virtual string? SearchHint => null;

    /// <inheritdoc/>
    public virtual int MaxResultSizeChars => 100_000;

    // -----------------------------------------------------------------------
    // Schema & prompting
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public abstract JsonElement GetInputSchema();

    /// <inheritdoc/>
    public abstract Task<string> GetDescriptionAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<string> GetPromptAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public virtual string UserFacingName(JsonElement? input = null) => Name;

    /// <inheritdoc/>
    public virtual string? GetActivityDescription(JsonElement? input = null) => null;

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Executes the tool with the deserialised <paramref name="input"/> and ambient
    /// <paramref name="context"/>. Implementations must honour
    /// <see cref="ToolUseContext.CancellationToken"/>.
    /// </summary>
    /// <param name="input">Deserialised, validated input for this invocation.</param>
    /// <param name="context">Ambient context providing session state and services.</param>
    /// <param name="ct">Cancellation token; typically forwarded from <paramref name="context"/>.</param>
    /// <returns>A <see cref="ToolResult{TOutput}"/> containing the typed output.</returns>
    public abstract Task<ToolResult<TOutput>> ExecuteAsync(
        TInput input,
        ToolUseContext context,
        CancellationToken ct = default);

    // -----------------------------------------------------------------------
    // Permission & validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluates whether this invocation is allowed under the current session's
    /// permission policy. The default implementation always returns
    /// <see cref="PermissionAllowed"/> (unrestricted). Override to enforce
    /// tool-specific rules.
    /// </summary>
    /// <param name="input">The deserialised input for this invocation.</param>
    /// <param name="context">Ambient context containing the permission mode and rules.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual Task<PermissionDecision> CheckPermissionsAsync(
        TInput input,
        ToolUseContext context,
        CancellationToken ct = default)
        => Task.FromResult<PermissionDecision>(new PermissionAllowed());

    /// <summary>
    /// Validates the deserialised <paramref name="input"/> before execution.
    /// The default implementation returns <see cref="ValidationResult.Success"/>.
    /// Override to enforce preconditions (e.g. required fields, path constraints).
    /// </summary>
    /// <param name="input">The deserialised input for this invocation.</param>
    /// <param name="context">Ambient context.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual Task<ValidationResult> ValidateInputAsync(
        TInput input,
        ToolUseContext context,
        CancellationToken ct = default)
        => Task.FromResult(ValidationResult.Success);

    // -----------------------------------------------------------------------
    // Behaviour flags
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public virtual bool IsEnabled() => true;

    /// <inheritdoc/>
    public virtual bool IsReadOnly(JsonElement input) => false;

    /// <inheritdoc/>
    public virtual bool IsConcurrencySafe(JsonElement input) => false;

    // -----------------------------------------------------------------------
    // Serialisation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Deserialises a raw <see cref="JsonElement"/> (from the model's tool-use block)
    /// into a strongly-typed <typeparamref name="TInput"/> instance.
    /// </summary>
    /// <param name="json">The JSON element from the <c>input</c> field of a tool-use block.</param>
    /// <returns>A non-null <typeparamref name="TInput"/> instance.</returns>
    public abstract TInput DeserializeInput(JsonElement json);

    /// <summary>
    /// Converts the typed <paramref name="result"/> into the string payload that is
    /// placed into the <c>content</c> field of a <c>tool_result</c> API block.
    /// </summary>
    /// <param name="result">The output produced by <see cref="ExecuteAsync"/>.</param>
    /// <param name="toolUseId">
    /// The <c>id</c> from the originating <c>tool_use</c> block, used to correlate
    /// the result with the request.
    /// </param>
    /// <returns>A string that will be sent back to the model as the tool result content.</returns>
    public abstract string MapResultToString(TOutput result, string toolUseId);

    /// <inheritdoc/>
    /// <remarks>
    /// Validates the deserialised input before execution. When validation fails the
    /// error message is returned directly rather than throwing, so the orchestration
    /// loop can forward it to the model as a <c>tool_result</c> without aborting the session.
    /// </remarks>
    public async Task<string> ExecuteRawAsync(
        JsonElement input,
        ToolUseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var typedInput = DeserializeInput(input);

        var validation = await ValidateInputAsync(typedInput, context, ct).ConfigureAwait(false);
        if (!validation.IsValid)
            return $"Error: {validation.ErrorMessage}";

        var result = await ExecuteAsync(typedInput, context, ct).ConfigureAwait(false);
        return MapResultToString(result.Data, string.Empty);
    }
}

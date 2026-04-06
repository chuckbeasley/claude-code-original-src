namespace ClaudeCode.Core.Tools;

/// <summary>
/// The outcome of input validation performed before a tool executes.
/// Use <see cref="Success"/> for the happy path; call <see cref="Failure"/> to
/// produce a descriptive error without throwing.
/// </summary>
public record ValidationResult
{
    /// <summary><see langword="true"/> when validation passed; <see langword="false"/> when it failed.</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Human-readable description of the validation failure.
    /// <see langword="null"/> when <see cref="IsValid"/> is <see langword="true"/>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional numeric code categorising the failure (e.g. an exit-code convention).
    /// Zero means unclassified; meaningful values are tool-specific.
    /// </summary>
    public int ErrorCode { get; init; }

    /// <summary>Singleton representing a successful validation.</summary>
    public static ValidationResult Success { get; } = new() { IsValid = true };

    /// <summary>
    /// Produces a failed <see cref="ValidationResult"/> with the supplied message and optional error code.
    /// </summary>
    /// <param name="message">Human-readable description of what failed.</param>
    /// <param name="errorCode">Optional numeric code; defaults to <c>0</c> (unclassified).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is <see langword="null"/>.</exception>
    public static ValidationResult Failure(string message, int errorCode = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new() { IsValid = false, ErrorMessage = message, ErrorCode = errorCode };
    }
}

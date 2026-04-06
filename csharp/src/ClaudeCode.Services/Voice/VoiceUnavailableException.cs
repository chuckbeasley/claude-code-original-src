namespace ClaudeCode.Services.Voice;

/// <summary>
/// Thrown when the speech recognition engine cannot be initialised.
/// </summary>
public sealed class VoiceUnavailableException : Exception
{
    public VoiceUnavailableException(string message) : base(message) { }
    public VoiceUnavailableException(string message, Exception inner) : base(message, inner) { }
}

namespace ClaudeCode.Services.Voice;

/// <summary>
/// Abstraction over a speech recognition engine.
/// </summary>
public interface IVoiceEngine : IDisposable
{
    /// <summary>Raised when speech is successfully recognized.</summary>
    event Action<string>? SpeechRecognized;

    /// <summary>Raised when a recognition attempt fails (no match).</summary>
    event Action? SpeechRejected;

    /// <summary>Starts listening. Throws <see cref="VoiceUnavailableException"/> on failure.</summary>
    void Start();

    /// <summary>Stops listening and releases the microphone.</summary>
    void Stop();
}

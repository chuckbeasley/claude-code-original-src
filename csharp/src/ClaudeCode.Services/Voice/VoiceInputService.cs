namespace ClaudeCode.Services.Voice;

/// <summary>
/// Manages STT input for the REPL.
/// Raises <see cref="TextRecognized"/> when speech is captured.
/// Prints a heartbeat to the console every 10 seconds while listening.
/// </summary>
public sealed class VoiceInputService : IDisposable
{
    private const int HeartbeatIntervalSeconds = 10;

    private readonly IVoiceEngine _engine;
    private Timer? _heartbeat;
    private bool _started;

    /// <summary>Raised when speech is recognized.</summary>
    public event Action<string>? TextRecognized;

    public VoiceInputService(IVoiceEngine engine)
        => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>Starts recognition and the heartbeat timer.</summary>
    public void Start()
    {
        if (_started) return;

        // Subscribe before calling Start() so no recognition events are missed in the
        // brief window between engine start and subscription. If Start() throws
        // VoiceUnavailableException, the caller must Dispose() this service;
        // Dispose() -> _engine.Dispose() tears down the engine and prevents callbacks.
        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.SpeechRejected   += OnSpeechRejected;

        try
        {
            _engine.Start();
        }
        catch
        {
            // Roll back subscriptions so a failed Start leaves no dangling handlers.
            _engine.SpeechRecognized -= OnSpeechRecognized;
            _engine.SpeechRejected   -= OnSpeechRejected;
            throw;
        }

        _heartbeat = new Timer(
            _ => Console.Write("\r[voice: listening...]  "),
            state: null,
            dueTime: TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
            period: TimeSpan.FromSeconds(HeartbeatIntervalSeconds));

        _started = true;
    }

    /// <summary>Stops recognition and the heartbeat timer.</summary>
    public void Stop()
    {
        if (!_started) return;

        _heartbeat?.Dispose();
        _heartbeat = null;

        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechRejected   -= OnSpeechRejected;
        _engine.Stop();

        _started = false;
    }

    public void Dispose()
    {
        Stop();
        _engine.Dispose();
    }

    private void OnSpeechRecognized(string text)
    {
        ResetHeartbeat();
        TextRecognized?.Invoke(text);
    }

    private void OnSpeechRejected()
        => ResetHeartbeat();

    private void ResetHeartbeat()
        => _heartbeat?.Change(
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
            TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
}

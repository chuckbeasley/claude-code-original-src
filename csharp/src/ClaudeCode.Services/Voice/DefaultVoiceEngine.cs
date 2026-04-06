namespace ClaudeCode.Services.Voice;

using System.Runtime.InteropServices;

/// <summary>
/// <see cref="IVoiceEngine"/> backed by <c>System.Speech.Recognition</c> (Windows-only).
/// Uses reflection to avoid compile-time failures on non-Windows platforms.
/// </summary>
public sealed class DefaultVoiceEngine : IVoiceEngine
{
    public event Action<string>? SpeechRecognized;
    public event Action? SpeechRejected;

    private object? _engine; // System.Speech.Recognition.SpeechRecognitionEngine

    public DefaultVoiceEngine()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new VoiceUnavailableException(
                "Voice input requires Windows (System.Speech.Recognition).");

        try
        {
            var engineType  = Type.GetType("System.Speech.Recognition.SpeechRecognitionEngine, System.Speech",
                throwOnError: true)!;
            var grammarType = Type.GetType("System.Speech.Recognition.DictationGrammar, System.Speech",
                throwOnError: true)!;

            _engine = Activator.CreateInstance(engineType)!;
            engineType.GetMethod("SetInputToDefaultAudioDevice")!.Invoke(_engine, null);
            var grammar = Activator.CreateInstance(grammarType)!;
            engineType.GetMethod("LoadGrammar")!.Invoke(_engine, [grammar]);

            SubscribeEvent(engineType, _engine, "SpeechRecognized",            OnRawSpeechRecognized);
            SubscribeEvent(engineType, _engine, "SpeechRecognitionRejected",   OnRawSpeechRejected);
        }
        catch (Exception ex) when (ex is not VoiceUnavailableException)
        {
            throw new VoiceUnavailableException($"Speech recognition init failed: {ex.Message}", ex);
        }
    }

    public void Start()
    {
        if (_engine is null) return;
        try
        {
            var engineType = _engine.GetType();
            var modeType   = Type.GetType("System.Speech.Recognition.RecognizeMode, System.Speech",
                throwOnError: true)!;
            var multiple   = Enum.Parse(modeType, "Multiple");
            engineType.GetMethod("RecognizeAsync", [modeType])!.Invoke(_engine, [multiple]);
        }
        catch (Exception ex)
        {
            throw new VoiceUnavailableException($"Failed to start recognition: {ex.Message}", ex);
        }
    }

    public void Stop()
    {
        try { _engine?.GetType().GetMethod("RecognizeAsyncStop")?.Invoke(_engine, null); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        Stop();
        (_engine as IDisposable)?.Dispose();
        _engine = null;
    }

    private void OnRawSpeechRecognized(object? sender, EventArgs e)
    {
        try
        {
            var result = e.GetType().GetProperty("Result")?.GetValue(e);
            var text   = result?.GetType().GetProperty("Text")?.GetValue(result) as string;
            if (!string.IsNullOrWhiteSpace(text))
                SpeechRecognized?.Invoke(text);
        }
        catch { /* best-effort */ }
    }

    private void OnRawSpeechRejected(object? sender, EventArgs e)
        => SpeechRejected?.Invoke();

    private static void SubscribeEvent(Type engineType, object engine, string eventName,
        Action<object?, EventArgs> handler)
    {
        var ev = engineType.GetEvent(eventName);
        if (ev is null) return;

        // Create a delegate of the exact event handler type that calls our Action.
        // Uses CLR delegate contravariance: method(object?, EventArgs) satisfies
        // EventHandler<DerivedEventArgs> because the method accepts the base type.
        try
        {
            var del = Delegate.CreateDelegate(
                ev.EventHandlerType!,
                handler.Target,
                handler.Method,
                throwOnBindFailure: false);

            if (del is not null)
                ev.AddEventHandler(engine, del);
            else
                // Very last resort: try adding as-is in case CLR allows the covariant bind
                ev.AddEventHandler(engine, new EventHandler<EventArgs>((s, e) => handler(s, e)));
        }
        catch { /* best-effort — event subscription is optional */ }
    }
}

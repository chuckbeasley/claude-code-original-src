namespace ClaudeCode.Services.Tests;

using ClaudeCode.Services.Voice;

public sealed class VoiceInputServiceTests
{
    private sealed class MockVoiceEngine : IVoiceEngine
    {
        public event Action<string>? SpeechRecognized;
        public event Action? SpeechRejected;
        public bool StartCalled { get; private set; }
        public bool StopCalled  { get; private set; }
        public bool Disposed    { get; private set; }

        public void Start()  => StartCalled = true;
        public void Stop()   => StopCalled  = true;
        public void Dispose(){ Disposed     = true; }

        public void SimulateRecognized(string text) => SpeechRecognized?.Invoke(text);
        public void SimulateRejected()              => SpeechRejected?.Invoke();
    }

    [Fact]
    public void Start_CallsEngineStart()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();
        Assert.True(engine.StartCalled);
    }

    [Fact]
    public void Stop_CallsEngineStop()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();
        svc.Stop();
        Assert.True(engine.StopCalled);
    }

    [Fact]
    public void TextRecognized_FiredWhenEngineRecognizes()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();

        string? received = null;
        svc.TextRecognized += t => received = t;

        engine.SimulateRecognized("hello world");

        Assert.Equal("hello world", received);
    }

    [Fact]
    public void TextRecognized_NotFiredAfterStop()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();

        string? received = null;
        svc.TextRecognized += t => received = t;

        svc.Stop();
        engine.SimulateRecognized("should be ignored");

        Assert.Null(received);
    }

    [Fact]
    public void Start_CalledTwice_DoesNotDoubleSubscribe()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();
        svc.Start(); // second call is no-op

        int count = 0;
        svc.TextRecognized += _ => count++;
        engine.SimulateRecognized("test");
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_StopsEngineAndDisposesIt()
    {
        var engine = new MockVoiceEngine();
        var svc = new VoiceInputService(engine);
        svc.Start();
        svc.Dispose();

        Assert.True(engine.StopCalled);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public void SpeechRejected_DoesNotRaiseTextRecognized()
    {
        var engine = new MockVoiceEngine();
        using var svc = new VoiceInputService(engine);
        svc.Start();

        bool raised = false;
        svc.TextRecognized += _ => raised = true;

        engine.SimulateRejected();

        Assert.False(raised);
    }
}

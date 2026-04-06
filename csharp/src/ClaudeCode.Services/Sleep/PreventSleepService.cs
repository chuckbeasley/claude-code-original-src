using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeCode.Services.Sleep;

/// <summary>
/// Prevents the operating system from sleeping while a long-running task is active.
/// </summary>
/// <remarks>
/// Platform behaviour:
/// <list type="bullet">
///   <item><description>
///     <b>Windows</b>: calls <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)</c>
///     via P/Invoke to keep the system awake, and clears the requirement on release.
///   </description></item>
///   <item><description>
///     <b>macOS</b>: spawns <c>caffeinate -i -t 300</c> and restarts it every 4 minutes so the
///     OS assertion is continuously renewed until <see cref="Release"/> is called.
///   </description></item>
///   <item><description>
///     <b>Linux</b>: no-op on both <see cref="Acquire"/> and <see cref="Release"/>.
///   </description></item>
/// </list>
/// All public methods swallow exceptions and never throw.
/// </remarks>
public sealed class PreventSleepService : IDisposable
{
    // ─── Windows P/Invoke ─────────────────────────────────────────────────────

    private const uint ES_CONTINUOUS      = 0x80000000u;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001u;

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    // ─── macOS constants ──────────────────────────────────────────────────────

    /// <summary>
    /// Interval at which the <c>caffeinate</c> process is restarted (4 minutes),
    /// keeping it alive well within its 300-second (5-minute) timeout.
    /// </summary>
    private const int CaffeinateRestartIntervalMs = 240_000;

    // ─── State ────────────────────────────────────────────────────────────────

    private Process? _caffeinate;
    private Timer?   _timer;
    private bool     _acquired;
    private bool     _disposed;

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Signals the OS that the system must not sleep. Idempotent: calling when already
    /// acquired is a no-op. All exceptions are swallowed.
    /// </summary>
    public void Acquire()
    {
        if (_disposed || _acquired) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                AcquireWindows();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                AcquireMacOS();
            // Linux: no-op
        }
        catch { /* swallow — sleep prevention is best-effort */ }

        _acquired = true;
    }

    /// <summary>
    /// Withdraws the sleep-prevention request made by <see cref="Acquire"/>.
    /// Idempotent: calling when not acquired is a no-op. All exceptions are swallowed.
    /// </summary>
    public void Release()
    {
        if (_disposed || !_acquired) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ReleaseWindows();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                ReleaseMacOS();
            // Linux: no-op
        }
        catch { /* swallow — sleep prevention is best-effort */ }

        _acquired = false;
    }

    /// <summary>
    /// Releases the sleep-prevention lock (if held) and frees associated resources.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        // Release() must be called before _disposed is set to true so that its
        // own _disposed guard does not short-circuit the cleanup.
        Release();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ─── Windows implementation ───────────────────────────────────────────────

    /// <summary>
    /// Sets the thread execution state to prevent the system from sleeping.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void AcquireWindows()
        => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

    /// <summary>
    /// Clears the <c>ES_SYSTEM_REQUIRED</c> flag, allowing the system to sleep normally.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ReleaseWindows()
        => SetThreadExecutionState(ES_CONTINUOUS);

    // ─── macOS implementation ─────────────────────────────────────────────────

    /// <summary>
    /// Spawns the first <c>caffeinate</c> process and starts the restart timer.
    /// </summary>
    [SupportedOSPlatform("osx")]
    private void AcquireMacOS()
    {
        _caffeinate = SpawnCaffeinate();
        _timer = new Timer(
            RestartCaffeinate,
            state: null,
            dueTime: CaffeinateRestartIntervalMs,
            period: CaffeinateRestartIntervalMs);
    }

    /// <summary>
    /// Disposes the restart timer and kills the running <c>caffeinate</c> process.
    /// </summary>
    [SupportedOSPlatform("osx")]
    private void ReleaseMacOS()
    {
        _timer?.Dispose();
        _timer = null;
        KillCaffeinate();
    }

    /// <summary>
    /// Timer callback: kills the current <c>caffeinate</c> instance and spawns a new one
    /// so the OS assertion is renewed before the 300-second timeout expires.
    /// </summary>
    [SupportedOSPlatform("osx")]
    private void RestartCaffeinate(object? state)
    {
        try
        {
            KillCaffeinate();
            _caffeinate = SpawnCaffeinate();
        }
        catch { /* swallow — timer callback must never propagate */ }
    }

    /// <summary>
    /// Starts a new <c>caffeinate -i -t 300</c> process and returns it.
    /// </summary>
    [SupportedOSPlatform("osx")]
    private static Process? SpawnCaffeinate()
    {
        var psi = new ProcessStartInfo("caffeinate")
        {
            UseShellExecute = false,
            ArgumentList    = { "-i", "-t", "300" }
        };
        return Process.Start(psi);
    }

    /// <summary>
    /// Kills and disposes <see cref="_caffeinate"/> if it is set, then nulls the field.
    /// Each cleanup step is individually guarded so a failure in Kill does not
    /// prevent Dispose from running.
    /// </summary>
    [SupportedOSPlatform("osx")]
    private void KillCaffeinate()
    {
        try { _caffeinate?.Kill(); }   catch { }
        try { _caffeinate?.Dispose(); } catch { }
        _caffeinate = null;
    }
}

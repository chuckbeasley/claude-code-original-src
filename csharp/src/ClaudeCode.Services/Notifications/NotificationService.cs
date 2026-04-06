namespace ClaudeCode.Services.Notifications;

/// <summary>
/// Identifies the notification delivery channel used by <see cref="NotificationService"/>.
/// </summary>
public enum NotificationChannel
{
    /// <summary>Detect the best channel from the <c>TERM_PROGRAM</c> environment variable.</summary>
    Auto,

    /// <summary>iTerm2 proprietary OSC escape sequence (<c>\u001b]9;{title}\u0007</c>).</summary>
    ITerm2,

    /// <summary>Kitty terminal proprietary OSC escape sequence (<c>\u001b]99;i=1:d=1;{title}\u0007</c>).</summary>
    Kitty,

    /// <summary>Ghostty terminal — falls back to an audible bell.</summary>
    Ghostty,

    /// <summary>Audible bell character (<c>BEL</c> / <c>\u0007</c>) written to stdout.</summary>
    Bell,

    /// <summary>No notification is sent.</summary>
    Disabled,
}

/// <summary>
/// Sends desktop/terminal notifications when Claude finishes a long-running task.
/// All delivery operations are best-effort: exceptions are swallowed internally so this
/// service never disrupts the calling workflow.
/// </summary>
public sealed class NotificationService
{
    private readonly NotificationChannel _channel;

    /// <summary>
    /// Initialises the service and resolves the active <see cref="NotificationChannel"/>.
    /// </summary>
    /// <param name="configuredChannel">
    /// The channel name from configuration (e.g. <c>"iTerm2"</c>, <c>"Bell"</c>, <c>"Disabled"</c>).
    /// Pass <see langword="null"/> or an empty/whitespace string to default to
    /// <see cref="NotificationChannel.Auto"/>.
    /// </param>
    public NotificationService(string? configuredChannel = null)
    {
        if (string.IsNullOrWhiteSpace(configuredChannel))
        {
            _channel = NotificationChannel.Auto;
            return;
        }

        _channel = Enum.TryParse<NotificationChannel>(configuredChannel, ignoreCase: true, out var parsed)
            ? parsed
            : NotificationChannel.Auto;
    }

    /// <summary>
    /// Sends a notification using the configured channel.
    /// </summary>
    /// <remarks>
    /// This method never throws (except for a null <paramref name="title"/>, which is a
    /// programming error). All I/O failures are swallowed silently.
    /// </remarks>
    /// <param name="title">The notification title or primary message text.</param>
    /// <param name="body">
    /// Optional secondary message body. Most terminal channels ignore this value.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="title"/> is <see langword="null"/>.</exception>
    public void Notify(string title, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        var channel = _channel == NotificationChannel.Auto
            ? ResolveAutoChannel()
            : _channel;

        switch (channel)
        {
            case NotificationChannel.Disabled:
                return;

            case NotificationChannel.ITerm2:
                SendITerm2(title);
                break;

            case NotificationChannel.Kitty:
                SendKitty(title);
                break;

            case NotificationChannel.Ghostty:
            case NotificationChannel.Bell:
                SendBell();
                break;

            default:
                // Fallback for any future enum values not yet handled.
                SendBell();
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inspects environment variables at notification time to pick the best channel.
    /// Never returns <see cref="NotificationChannel.Auto"/> or <see cref="NotificationChannel.Disabled"/>.
    /// </summary>
    private static NotificationChannel ResolveAutoChannel()
    {
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        if (!string.IsNullOrEmpty(termProgram))
        {
            if (string.Equals(termProgram, "iTerm.app", StringComparison.OrdinalIgnoreCase))
                return NotificationChannel.ITerm2;

            if (string.Equals(termProgram, "kitty", StringComparison.OrdinalIgnoreCase))
                return NotificationChannel.Kitty;

            if (string.Equals(termProgram, "ghostty", StringComparison.OrdinalIgnoreCase))
                return NotificationChannel.Ghostty;
        }

        var itermSession = Environment.GetEnvironmentVariable("ITERM_SESSION_ID");
        if (!string.IsNullOrEmpty(itermSession))
            return NotificationChannel.ITerm2;

        return NotificationChannel.Bell;
    }

    private static void SendITerm2(string title)
    {
        try
        {
            Console.Error.Write($"\u001b]9;{title}\u0007");
        }
        catch
        {
            // Best-effort — swallow all I/O exceptions.
        }
    }

    private static void SendKitty(string title)
    {
        try
        {
            Console.Error.Write($"\u001b]99;i=1:d=1;{title}\u0007");
        }
        catch
        {
            // Best-effort — swallow all I/O exceptions.
        }
    }

    private static void SendBell()
    {
        try
        {
            Console.Write('\u0007');
        }
        catch
        {
            // Best-effort — swallow all I/O exceptions.
        }
    }
}

namespace ClaudeCode.Tools.Cron;

/// <summary>
/// Background scheduler that evaluates registered cron jobs every 30 seconds
/// and posts due prompts to <see cref="CronState.PendingPrompts"/>.
/// </summary>
public static class CronScheduler
{
    /// <summary>
    /// Starts the background scheduler loop. Returns when the token is cancelled.
    /// Call this with Task.Run() from the REPL session.
    /// </summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                FireDueJobs();
            }
            catch (OperationCanceledException) { break; }
            catch { /* swallow spurious exceptions to keep scheduler alive */ }
        }
    }

    private static void FireDueJobs()
    {
        var now = DateTimeOffset.UtcNow;
        // Truncate to minute resolution for cron matching
        var nowMinute = new DateTimeOffset(now.Year, now.Month, now.Day,
            now.Hour, now.Minute, 0, TimeSpan.Zero);

        foreach (var (id, job) in CronState.Jobs)
        {
            try
            {
                if (!IsDue(job.CronExpr, nowMinute))
                    continue;

                // Prevent double-firing within the same minute
                if (CronState.LastFired.TryGetValue(id, out var lastFired) &&
                    lastFired >= nowMinute)
                    continue;

                CronState.LastFired[id] = nowMinute;
                CronState.PendingPrompts.Writer.TryWrite(job.Prompt);

                // Auto-remove one-shot jobs after firing
                if (!job.Recurring)
                    CronState.Jobs.TryRemove(id, out _);
            }
            catch { /* skip broken job */ }
        }
    }

    /// <summary>
    /// Evaluates a 5-field cron expression against a UTC datetime (minute resolution).
    /// Fields: minute hour day-of-month month day-of-week.
    /// Supports: * (any), */n (every n), n (exact), n-m (range), n,m (list).
    /// </summary>
    public static bool IsDue(string cronExpr, DateTimeOffset when)
    {
        var fields = cronExpr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;

        return FieldMatches(fields[0], when.Minute, 0, 59)
            && FieldMatches(fields[1], when.Hour, 0, 23)
            && FieldMatches(fields[2], when.Day, 1, 31)
            && FieldMatches(fields[3], when.Month, 1, 12)
            && FieldMatches(fields[4], (int)when.DayOfWeek, 0, 7); // 0 and 7 = Sunday
    }

    private static bool FieldMatches(string field, int value, int min, int max)
    {
        if (field == "*") return true;

        // Comma-separated list: "1,3,5"
        if (field.Contains(','))
            return field.Split(',').Any(f => FieldMatches(f.Trim(), value, min, max));

        // Step: "*/5" or "1-5/2"
        if (field.Contains('/'))
        {
            var parts = field.Split('/', 2);
            var step = int.TryParse(parts[1], out var s) ? s : 1;
            var start = parts[0] == "*" ? min
                : parts[0].Contains('-') ? ParseRange(parts[0]).start : int.Parse(parts[0]);
            return value >= start && (value - start) % step == 0;
        }

        // Range: "1-5"
        if (field.Contains('-'))
        {
            var (start, end) = ParseRange(field);
            return value >= start && value <= end;
        }

        // Exact: "5"
        return int.TryParse(field, out var exact) && (exact == value || (exact == 7 && value == 0));
    }

    private static (int start, int end) ParseRange(string range)
    {
        var parts = range.Split('-', 2);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}

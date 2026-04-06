namespace ClaudeCode.Services.Api;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

/// <summary>
/// Decorates <see cref="AnthropicClient"/> with automatic retry logic using exponential
/// backoff with jitter. Only retries if no events have been yielded from the stream yet,
/// since partial streams cannot be safely replayed.
/// </summary>
/// <remarks>
/// The retry loop runs as a background writer into a <see cref="Channel{T}"/>.
/// The public <see cref="StreamMessageAsync"/> method reads from the channel,
/// allowing <c>yield return</c> to live outside any try/catch block — which
/// is required by the C# compiler (CS1626).
/// </remarks>
public sealed class RetryingAnthropicClient : IAnthropicClient
{
    private readonly AnthropicClient _inner;
    private readonly ILogger<RetryingAnthropicClient>? _logger;
    private readonly int _maxRetries;

    /// <summary>
    /// Initializes a new instance of <see cref="RetryingAnthropicClient"/>.
    /// </summary>
    /// <param name="inner">The underlying <see cref="AnthropicClient"/> to wrap.</param>
    /// <param name="logger">Optional logger for retry diagnostics.</param>
    /// <param name="maxRetries">Maximum number of retry attempts; defaults to <see cref="ApiConstants.DefaultMaxRetries"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="inner"/> is null.</exception>
    public RetryingAnthropicClient(
        AnthropicClient inner,
        ILogger<RetryingAnthropicClient>? logger = null,
        int maxRetries = ApiConstants.DefaultMaxRetries)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _logger = logger;
        _maxRetries = maxRetries;
    }

    /// <inheritdoc/>
    /// Delegates to the inner <see cref="AnthropicClient.LastRateLimitInfo"/> so callers
    /// always see the rate-limit headers from the most recent attempt, even after retries.
    public RateLimitInfo? LastRateLimitInfo => _inner.LastRateLimitInfo;

    /// <inheritdoc/>
    /// <remarks>
    /// Streaming is driven through a bounded channel. The retry loop writes events
    /// into the channel as a background task; this method reads from the channel and
    /// yields each event. If the background task faults before any event is written,
    /// the exception propagates to the caller on the first <c>MoveNextAsync()</c>.
    /// If it faults after events have been yielded, the exception propagates on the
    /// subsequent <c>MoveNextAsync()</c> — at which point retrying is unsafe, so the
    /// exception is re-thrown as-is.
    /// </remarks>
    public async IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Unbounded channel: the producer (retry loop) writes events; the consumer
        // (this iterator) reads and yields them. SingleReader/SingleWriter optimises
        // the common case of one caller.
        var channel = Channel.CreateUnbounded<SseEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // Launch the retry loop as a background task. It completes the channel writer
        // when it either finishes successfully or gives up retrying (by throwing).
        var producer = RunWithRetryAsync(request, channel.Writer, ct);

        // Yield each event as it arrives. The channel reader will complete once the
        // writer is closed (either by success or by the producer faulting).
        await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }

        // Await the producer to observe any exception that occurred after streaming
        // started (mid-stream errors) or after all retries were exhausted.
        await producer.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the streaming request with retry logic, writing events into
    /// <paramref name="writer"/> and completing it when done (or on terminal failure).
    /// </summary>
    private async Task RunWithRetryAsync(
        MessageRequest request,
        ChannelWriter<SseEvent> writer,
        CancellationToken ct)
    {
        int attempt = 0;
        int consecutive529 = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;

                bool wroteAny = false;
                Exception? caughtException = null;

                try
                {
                    await foreach (var evt in _inner.StreamMessageAsync(request, ct).ConfigureAwait(false))
                    {
                        wroteAny = true;
                        // TryWrite always succeeds on an unbounded channel unless the reader is gone
                        await writer.WriteAsync(evt, ct).ConfigureAwait(false);
                    }

                    // Stream completed successfully
                    writer.Complete();
                    return;
                }
                catch (AnthropicApiException ex) when (!wroteAny)
                {
                    caughtException = ex;
                }
                catch (HttpRequestException ex) when (!wroteAny)
                {
                    caughtException = ex;
                }
                catch (Exception ex)
                {
                    // Mid-stream failure or non-retryable exception — surface to caller immediately
                    writer.Complete(ex);
                    return;
                }

                // At this point no events were written and we caught a potentially-retryable exception
                if (caughtException is AnthropicApiException apiEx)
                {
                    if (apiEx.IsOverloaded)
                    {
                        consecutive529++;
                        if (consecutive529 > ApiConstants.Max529Retries)
                        {
                            _logger?.LogError(
                                "Max 529 retries ({Max}) exceeded",
                                ApiConstants.Max529Retries);
                            writer.Complete(apiEx);
                            return;
                        }
                    }
                    else
                    {
                        consecutive529 = 0;
                    }

                    if (!apiEx.IsRetryable || attempt > _maxRetries)
                    {
                        writer.Complete(apiEx);
                        return;
                    }

                    var delay = CalculateDelay(attempt, apiEx.RetryAfter);
                    _logger?.LogWarning(
                        "Retrying after {StatusCode} (attempt {Attempt}/{Max}, delay {DelayMs}ms)",
                        apiEx.StatusCode, attempt, _maxRetries, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                else if (caughtException is HttpRequestException httpEx)
                {
                    // Network-level error before any events — safe to retry
                    consecutive529 = 0;

                    if (attempt > _maxRetries)
                    {
                        writer.Complete(httpEx);
                        return;
                    }

                    var delay = CalculateDelay(attempt, null);
                    _logger?.LogWarning(
                        "Retrying after network error (attempt {Attempt}/{Max}, delay {DelayMs}ms)",
                        attempt, _maxRetries, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Covers OperationCanceledException from ct or Task.Delay
            writer.Complete(ex);
        }
    }

    /// <summary>
    /// Calculates the retry delay using exponential backoff with jitter, or returns
    /// the server-provided <paramref name="retryAfter"/> value if present.
    /// </summary>
    private static TimeSpan CalculateDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter.HasValue)
            return retryAfter.Value;

        // Exponential backoff: baseDelay * 2^(attempt-1), capped, with up to 25% jitter
        var baseDelay = ApiConstants.BaseDelayMs * Math.Pow(2, attempt - 1);
        var capped = Math.Min(baseDelay, ApiConstants.MaxBackoffMs);
        var jitter = Random.Shared.NextDouble() * 0.25 * capped;
        return TimeSpan.FromMilliseconds(capped + jitter);
    }
}

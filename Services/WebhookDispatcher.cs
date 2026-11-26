using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace abaci_bot.Services;

/// <summary>
/// Exception thrown when the bounded webhook dispatcher channel is saturated so ingestion can fail fast with HTTP 503.
/// </summary>
public sealed class WebhookDispatcherOverloadedException : Exception
{
    public WebhookDispatcherOverloadedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Asynchronous dispatcher decoupling HTTP webhook acknowledgment from governance pipeline execution.
/// </summary>
public interface IWebhookDispatcher
{
    bool TryEnqueue(
        string deliveryId,
        string eventSummary,
        Func<CancellationToken, ValueTask> work,
        Action? onPermanentFailure = null);
}

/// <summary>
/// Bounded background channel dispatcher that executes queued webhook deliveries concurrently, retries transient
/// failures with backoff, releases delivery deduplication claims when permanent failure occurs, and records
/// errors into <see cref="RuntimeHealthState"/>.
/// </summary>
public sealed class WebhookDispatcher : IHostedService, IDisposable, IWebhookDispatcher
{
    private readonly record struct WebhookWorkItem(
        string DeliveryId,
        string EventSummary,
        Func<CancellationToken, ValueTask> Work,
        Action? OnPermanentFailure,
        int Attempt = 1);

    private readonly Channel<WebhookWorkItem> _channel;
    private readonly CancellationTokenSource _drainCts = new();
    private readonly CancellationTokenSource _delayedRetryCts = new();
    private readonly ConcurrentDictionary<long, Task> _pendingDelayedRetries = new();
    private readonly ILogger<WebhookDispatcher>? _logger;
    private readonly RuntimeHealthState? _runtimeHealth;
    private readonly int _maxConcurrency;
    private readonly int _maxAttempts;
    private readonly Func<int, TimeSpan> _retryDelaySelector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _inlineRetryBlockThreshold;
    private readonly TimeSpan _maxRateLimitDelay;
    private Task? _workersTask;
    private long _delayedRetrySequence;
    private int _disposed;

    public WebhookDispatcher(
        ILogger<WebhookDispatcher>? logger = null,
        RuntimeHealthState? runtimeHealth = null,
        int capacity = 256,
        int maxConcurrency = 4,
        int maxAttempts = 3,
        Func<int, TimeSpan>? retryDelaySelector = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? inlineRetryBlockThreshold = null,
        TimeSpan? maxRateLimitDelay = null)
    {
        _logger = logger;
        _runtimeHealth = runtimeHealth;
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelaySelector = retryDelaySelector ?? DefaultRetryDelay;
        _delayAsync = delayAsync ?? Task.Delay;
        _inlineRetryBlockThreshold = inlineRetryBlockThreshold ?? TimeSpan.FromSeconds(15);
        _maxRateLimitDelay = maxRateLimitDelay ?? TimeSpan.FromMinutes(65);
        _channel = Channel.CreateBounded<WebhookWorkItem>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    internal static ValueTask<bool> HandleWebhookExceptionAsync(Exception ex, HttpContext httpContext)
    {
        if (ex is WebhookDispatcherOverloadedException && !httpContext.Response.HasStarted)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            httpContext.Response.Headers.RetryAfter = "5";
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }

    public bool TryEnqueue(
        string deliveryId,
        string eventSummary,
        Func<CancellationToken, ValueTask> work,
        Action? onPermanentFailure = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new WebhookWorkItem(
            string.IsNullOrWhiteSpace(deliveryId) ? "<unknown>" : deliveryId,
            string.IsNullOrWhiteSpace(eventSummary) ? "webhook" : eventSummary,
            work,
            onPermanentFailure);
        return _channel.Writer.TryWrite(item);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var workers = new Task[_maxConcurrency];
        for (int i = 0; i < _maxConcurrency; i++)
        {
            workers[i] = Task.Run(() => RunWorkerAsync(_drainCts.Token), CancellationToken.None);
        }

        _workersTask = Task.WhenAll(workers);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _delayedRetryCts.Cancel();

        try
        {
            if (_workersTask != null)
            {
                using var cancelDrain = cancellationToken.Register(() => _drainCts.Cancel());
                await _workersTask.ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseRemainingQueuedClaims();
            var pendingDelayed = _pendingDelayedRetries.Values.ToArray();
            if (pendingDelayed.Length > 0)
            {
                await Task.WhenAll(pendingDelayed).ConfigureAwait(false);
            }
        }
    }

    private void ReleaseRemainingQueuedClaims()
    {
        while (_channel.Reader.TryRead(out var dropped))
        {
            _logger?.LogWarning(
                "Webhook delivery '{DeliveryId}' ({EventSummary}) was still queued when host shutdown forced cancellation; releasing delivery claim.",
                dropped.DeliveryId, dropped.EventSummary);
            InvokePermanentFailureCallback(dropped);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _delayedRetryCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _drainCts.Cancel(); } catch (ObjectDisposedException) { }
        _delayedRetryCts.Dispose();
        _drainCts.Dispose();
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ExecuteWorkItemWithRetryAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown
        }
    }

    private async Task ExecuteWorkItemWithRetryAsync(WebhookWorkItem item, CancellationToken stoppingToken)
    {
        for (int attempt = item.Attempt; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                stoppingToken.ThrowIfCancellationRequested();
                await item.Work(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "Webhook delivery '{DeliveryId}' ({EventSummary}) interrupted by host shutdown; releasing delivery claim.",
                    item.DeliveryId, item.EventSummary);
                InvokePermanentFailureCallback(item);
                return;
            }
            catch (Exception ex) when (attempt >= _maxAttempts || TransientErrorPolicy.IsNonRetriable(ex))
            {
                _logger?.LogError(
                    ex,
                    "Webhook delivery '{DeliveryId}' ({EventSummary}) failed permanently on attempt {Attempt}/{MaxAttempts}; releasing delivery claim.",
                    item.DeliveryId, item.EventSummary, attempt, _maxAttempts);
                _runtimeHealth?.RecordError(
                    nameof(WebhookDispatcher),
                    ex,
                    $"Webhook delivery '{item.DeliveryId}' ({item.EventSummary}) failed");
                InvokePermanentFailureCallback(item);
                return;
            }
            catch (Exception ex)
            {
                var delay = ComputeRetryDelay(attempt, ex, out var rateLimitType);
                if (delay > _inlineRetryBlockThreshold)
                {
                    _logger?.LogWarning(
                        ex,
                        "Webhook delivery '{DeliveryId}' ({EventSummary}) hit {FailureKind} on attempt {Attempt}/{MaxAttempts}; scheduling non-blocking delayed re-enqueue in {DelayMs}ms.",
                        item.DeliveryId, item.EventSummary, rateLimitType ?? "long retry delay", attempt, _maxAttempts, (int)delay.TotalMilliseconds);
                    ScheduleDelayedRetry(item with { Attempt = attempt + 1 }, delay);
                    return;
                }

                _logger?.LogWarning(
                    ex,
                    "Webhook delivery '{DeliveryId}' ({EventSummary}) failed transiently on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.",
                    item.DeliveryId, item.EventSummary, attempt, _maxAttempts, (int)delay.TotalMilliseconds);
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await _delayAsync(delay, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    InvokePermanentFailureCallback(item);
                    return;
                }
            }
        }
    }

    private TimeSpan ComputeRetryDelay(int attempt, Exception ex, out string? rateLimitType)
    {
        var baseDelay = _retryDelaySelector(attempt);
        if (TransientErrorPolicy.TryGetRateLimitInfo(ex, out var retryAfter, out var detectedType))
        {
            rateLimitType = detectedType;
            var effectiveRateLimit = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                ? retryAfter.Value
                : TimeSpan.FromSeconds(60);
            if (effectiveRateLimit > _maxRateLimitDelay)
            {
                effectiveRateLimit = _maxRateLimitDelay;
            }

            return effectiveRateLimit > baseDelay ? effectiveRateLimit : baseDelay;
        }

        rateLimitType = null;
        return baseDelay;
    }

    private void ScheduleDelayedRetry(WebhookWorkItem nextItem, TimeSpan delay)
    {
        var id = Interlocked.Increment(ref _delayedRetrySequence);
        var task = Task.Run(async () =>
        {
            try
            {
                await _delayAsync(delay, _delayedRetryCts.Token).ConfigureAwait(false);
                if (!_channel.Writer.TryWrite(nextItem))
                {
                    _logger?.LogWarning(
                        "Webhook dispatcher closed before delayed retry for delivery '{DeliveryId}' ({EventSummary}) could re-enqueue; releasing delivery claim.",
                        nextItem.DeliveryId, nextItem.EventSummary);
                    InvokePermanentFailureCallback(nextItem);
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning(
                    "Delayed retry for webhook delivery '{DeliveryId}' ({EventSummary}) cancelled by host shutdown; releasing delivery claim.",
                    nextItem.DeliveryId, nextItem.EventSummary);
                InvokePermanentFailureCallback(nextItem);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Delayed retry scheduling failed for webhook delivery '{DeliveryId}' ({EventSummary}); releasing delivery claim.",
                    nextItem.DeliveryId, nextItem.EventSummary);
                _runtimeHealth?.RecordError(
                    nameof(WebhookDispatcher),
                    ex,
                    $"Delayed retry failed for '{nextItem.DeliveryId}'");
                InvokePermanentFailureCallback(nextItem);
            }
            finally
            {
                _pendingDelayedRetries.TryRemove(id, out _);
            }
        }, CancellationToken.None);

        _pendingDelayedRetries[id] = task;
        if (task.IsCompleted)
        {
            _pendingDelayedRetries.TryRemove(id, out _);
        }
    }

    private void InvokePermanentFailureCallback(WebhookWorkItem item)
    {
        try
        {
            item.OnPermanentFailure?.Invoke();
        }
        catch (Exception callbackEx)
        {
            _logger?.LogWarning(
                callbackEx,
                "Failed to release delivery claim for webhook '{DeliveryId}' after failure.",
                item.DeliveryId);
        }
    }

    private static TimeSpan DefaultRetryDelay(int attempt) => attempt switch
    {
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(6),
        _ => TimeSpan.FromSeconds(15)
    };
}

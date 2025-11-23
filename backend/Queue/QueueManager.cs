using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

/// <summary>
/// QueueManager is a singleton service that processes the download queue.
/// IMPORTANT: As a singleton, this service must NOT inject DavDatabaseContext (which is scoped).
/// Instead, create new context instances per operation using 'await using var dbContext = new DavDatabaseContext()'
/// This pattern prevents disposed context exceptions and ensures proper database connection management.
/// </summary>
public class QueueManager : IDisposable
{
    private InProgressQueueItem? _inProgressQueueItem;

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly HealthCheckService _healthCheckService;

    // HIGH-3 FIX: Queue restart tracking
    private int _queueRestartCount = 0;
    private DateTime? _lastQueueFailure = null;
    private readonly TimeSpan _maxRetryWindow = TimeSpan.FromHours(1);
    private const int MaxRestartsPerHour = 10;

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        HealthCheckService healthCheckService
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _websocketManager = websocketManager;
        _healthCheckService = healthCheckService;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());

        // HIGH-3 FIX: Start queue processing with auto-restart capability
        _ = Task.Run(() => QueueProcessingWithRetry(_cancellationTokenSource.Token));
    }

    /// <summary>
    /// Queue processing wrapper with automatic restart and exponential backoff.
    /// HIGH-3 FIX: Ensures queue doesn't stay down after crashes.
    /// </summary>
    private async Task QueueProcessingWithRetry(CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        const int maxRetryDelay = 300; // 5 minutes

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Reset restart count if we've been running successfully for an hour
                if (_lastQueueFailure.HasValue &&
                    DateTime.UtcNow - _lastQueueFailure.Value > _maxRetryWindow)
                {
                    Log.Information(
                        "Queue has been stable for {Hours} hour(s), resetting restart counter from {Count} to 0",
                        _maxRetryWindow.TotalHours, _queueRestartCount);
                    _queueRestartCount = 0;
                    _lastQueueFailure = null;
                }

                // Run queue processing
                await ProcessQueueAsync(ct);

                // If we get here, ProcessQueueAsync exited normally (should not happen)
                Log.Warning("ProcessQueueAsync exited normally - this should not happen");
                retryDelay = TimeSpan.FromSeconds(5); // Reset delay
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                Log.Information("Queue processing cancelled - shutting down");
                break;
            }
            catch (Exception ex)
            {
                _queueRestartCount++;
                _lastQueueFailure = DateTime.UtcNow;

                // Check if we've exceeded restart limit
                if (_queueRestartCount > MaxRestartsPerHour)
                {
                    Log.Fatal(ex,
                        "Queue processing has failed {Count} times in the last hour. " +
                        "Giving up to prevent infinite restart loop. Manual intervention required.",
                        _queueRestartCount);

                    // Send alert via websocket
                    _websocketManager.SendMessage(
                        WebsocketTopic.QueueError,
                        $"Queue processing has failed {_queueRestartCount} times - stopped");

                    // Exit retry loop
                    break;
                }

                Log.Error(ex,
                    "Queue processing failed (attempt {Attempt}/{MaxAttempts}) - restarting in {Delay}s",
                    _queueRestartCount, MaxRestartsPerHour, retryDelay.TotalSeconds);

                // Send websocket notification
                _websocketManager.SendMessage(
                    WebsocketTopic.QueueError,
                    $"Queue processing error - restarting in {retryDelay.TotalSeconds}s");

                // Wait before retry
                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Exponential backoff
                retryDelay = TimeSpan.FromSeconds(
                    Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay));
            }
        }

        Log.Information("Queue processing retry loop exited");
    }

    /// <summary>
    /// Get queue health status for monitoring.
    /// HIGH-3 FIX: Health endpoint for queue status.
    /// </summary>
    public (bool IsRunning, int RestartCount, DateTime? LastFailure) GetQueueHealth()
    {
        // Queue is considered running if we haven't exceeded restart limit
        var isRunning = _queueRestartCount <= MaxRestartsPerHour;
        return (isRunning, _queueRestartCount, _lastQueueFailure);
    }

    public (QueueItem? queueItem, int? progress) GetInProgressQueueItem()
    {
        return (_inProgressQueueItem?.QueueItem, _inProgressQueueItem?.ProgressPercentage);
    }

    public async Task RemoveQueueItemsAsync
    (
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        await LockAsync(async () =>
        {
            var inProgressId = _inProgressQueueItem?.QueueItem?.Id;
            if (inProgressId is not null && queueItemIds.Contains(inProgressId.Value))
            {
                await _inProgressQueueItem!.CancellationTokenSource.CancelAsync();
                await _inProgressQueueItem.ProcessingTask;
                _inProgressQueueItem = null;
            }

            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct);
            await dbClient.Ctx.SaveChangesAsync(ct);
        });
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // get the next queue-item from the database
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var topItem = await LockAsync(() => dbClient.GetTopQueueItem(ct));
                if (topItem.queueItem is null || topItem.queueNzbContents is null)
                {
                    // if we're done with the queue, wait
                    // five seconds before checking again.
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                // LOW-2 FIX: Use 'using' statement to ensure CancellationTokenSource is disposed
                using (var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    await LockAsync(() =>
                    {
                        _inProgressQueueItem = BeginProcessingQueueItem(
                            dbClient, topItem.queueItem, topItem.queueNzbContents, queueItemCancellationTokenSource
                        );
                    });
                    await (_inProgressQueueItem?.ProcessingTask ?? Task.CompletedTask);
                } // CTS disposed here automatically
            }
            catch (Exception e)
            {
                Log.Error(e, "An unexpected error occurred while processing the queue");
            }
            finally
            {
                await LockAsync(() => { _inProgressQueueItem = null; });
            }
        }
    }

    private InProgressQueueItem BeginProcessingQueueItem
    (
        DavDatabaseClient dbClient,
        QueueItem queueItem,
        QueueNzbContents queueNzbContents,
        CancellationTokenSource cts
    )
    {
        var progressHook = new Progress<int>();
        var task = new QueueItemProcessor(
            queueItem, queueNzbContents, dbClient, _usenetClient, 
            _configManager, _websocketManager, _healthCheckService,
            progressHook, cts.Token
        ).ProcessAsync();
        var inProgressQueueItem = new InProgressQueueItem()
        {
            QueueItem = queueItem,
            ProcessingTask = task,
            ProgressPercentage = 0,
            CancellationTokenSource = cts
        };
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
        progressHook.ProgressChanged += (_, progress) =>
        {
            inProgressQueueItem.ProgressPercentage = progress;
            var message = $"{queueItem.Id}|{progress}";
            if (progress is 100 or 200) _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message);
            else debounce(() => _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message));
        };
        return inProgressQueueItem;
    }

    private async Task LockAsync(Func<Task> actionAsync)
    {
        await _semaphore.WaitAsync();
        try
        {
            await actionAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<T> LockAsync<T>(Func<Task<T>> actionAsync)
    {
        await _semaphore.WaitAsync();
        try
        {
            return await actionAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task LockAsync(Action action)
    {
        await _semaphore.WaitAsync();
        try
        {
            action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _semaphore?.Dispose();
    }

    private class InProgressQueueItem
    {
        public QueueItem QueueItem { get; init; }
        public int ProgressPercentage { get; set; }
        public Task ProcessingTask { get; init; }
        public CancellationTokenSource CancellationTokenSource { get; init; }
    }
}
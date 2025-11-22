# Bug Fix Implementation Plan - Production Issues

**Created:** 2025-11-22
**Target:** Production Launch
**Total Issues:** 12 (3 Critical, 4 High, 3 Medium, 2 Low)

---

## 📋 Executive Summary

This plan organizes the 12 identified issues into a 3-week sprint with clear priorities, dependencies, and testing requirements.

**Week 1:** Critical fixes (4 items) - 3-4 days
**Week 2:** High priority fixes (4 items) - 4-5 days
**Week 3:** Medium/Low priority fixes + comprehensive testing (5 items) - 5 days

**Total Estimated Time:** 12-14 days of focused development

---

## 🎯 Phase 1: Critical Fixes (Week 1)

### CRITICAL-1: Fix Thread Pool Configuration
**Priority:** P0 - Must fix before production
**Complexity:** Low
**Time Estimate:** 1 hour
**Risk:** High impact if wrong

#### Current Code
```csharp
// backend/Program.cs:31-33
ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
ThreadPool.SetMaxThreads(maxWorker, Math.Max(maxIo, 2000));
ThreadPool.SetMinThreads(100, 200);
```

#### Implementation Plan
1. Add environment variable support for thread pool configuration
2. Calculate conservative defaults based on CPU count
3. Add logging for thread pool settings
4. Add validation and warnings

#### Proposed Fix
```csharp
// backend/Program.cs:28-45
static void ConfigureThreadPool()
{
    var cpuCount = Environment.ProcessorCount;

    // Allow override via environment variables (for tuning)
    var minWorkerThreads = EnvironmentUtil.GetIntVariable("MIN_WORKER_THREADS") ?? (cpuCount * 2);
    var minIoThreads = EnvironmentUtil.GetIntVariable("MIN_IO_THREADS") ?? (cpuCount * 4);
    var maxIoThreads = EnvironmentUtil.GetIntVariable("MAX_IO_THREADS");

    // Clamp to reasonable values
    minWorkerThreads = Math.Clamp(minWorkerThreads, cpuCount, cpuCount * 4);
    minIoThreads = Math.Clamp(minIoThreads, cpuCount * 2, cpuCount * 8);

    ThreadPool.SetMinThreads(minWorkerThreads, minIoThreads);

    // Only override max IO threads if explicitly configured
    // Let the runtime manage max worker threads
    if (maxIoThreads.HasValue)
    {
        ThreadPool.GetMaxThreads(out var maxWorker, out var _);
        var clampedMaxIo = Math.Clamp(maxIoThreads.Value, minIoThreads, 2000);
        ThreadPool.SetMaxThreads(maxWorker, clampedMaxIo);
    }

    // Log configuration for diagnostics
    ThreadPool.GetMinThreads(out var actualMinWorker, out var actualMinIo);
    ThreadPool.GetMaxThreads(out var actualMaxWorker, out var actualMaxIo);
    Log.Information(
        "Thread pool configured: CPU={CpuCount}, MinThreads={MinWorker}/{MinIo}, MaxThreads={MaxWorker}/{MaxIo}",
        cpuCount, actualMinWorker, actualMinIo, actualMaxWorker, actualMaxIo);
}

static async Task Main(string[] args)
{
    ConfigureThreadPool();
    // ... rest of code
}
```

#### Testing Requirements
- Test in container with 1 CPU (should use ~2/4 min threads)
- Test in container with 4 CPUs (should use ~8/16 min threads)
- Test in container with 16 CPUs (should use ~32/64 min threads)
- Load test: Verify no thread starvation under heavy load
- Monitor thread pool metrics during stress test

#### Success Criteria
- [ ] Thread pool scales with CPU count
- [ ] No thread pool starvation under load
- [ ] Settings logged on startup
- [ ] Environment variables work for override

---

### CRITICAL-2: Verify Database Context Lifetime
**Priority:** P0 - Must verify before production
**Complexity:** Medium
**Time Estimate:** 2-3 hours
**Risk:** Medium - Already mostly correct

#### Implementation Plan
1. Audit all Singleton services for DbContext injection
2. Verify QueueManager pattern is correct
3. Add comments documenting the pattern
4. Add startup validation

#### Steps

**Step 1: Search for violations**
```bash
# Find all singleton services
grep -r "AddSingleton" backend/ --include="*.cs"

# Check each singleton for DbContext usage
# Look for constructor injection of DavDatabaseContext
```

**Step 2: Verify QueueManager pattern**
The current pattern in QueueManager.cs:77-87 is CORRECT:
```csharp
await using var dbContext = new DavDatabaseContext();
var dbClient = new DavDatabaseClient(dbContext);
```

This creates a new context per operation, which is the right pattern for singletons.

**Step 3: Add startup validation**
```csharp
// backend/Program.cs - Add after builder.Build()
public static void ValidateDependencyInjection(IServiceProvider services)
{
    // Verify no singleton services inject scoped dependencies
    var singletonServices = new[]
    {
        typeof(UsenetStreamingClient),
        typeof(QueueManager),
        typeof(ArrMonitoringService),
        typeof(HealthCheckService),
        typeof(ServerHealthTracker),
        typeof(ConfigManager),
        typeof(WebsocketManager)
    };

    foreach (var serviceType in singletonServices)
    {
        var service = services.GetRequiredService(serviceType);
        // Check that service doesn't have DbContext fields via reflection
        var fields = serviceType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        var dbContextField = fields.FirstOrDefault(f =>
            f.FieldType == typeof(DavDatabaseContext) ||
            f.FieldType == typeof(DavDatabaseClient));

        if (dbContextField != null)
        {
            Log.Fatal(
                "Singleton service {ServiceType} has injected DbContext field {FieldName}. " +
                "Singletons must create DbContext instances per operation, not store them.",
                serviceType.Name, dbContextField.Name);
            throw new InvalidOperationException(
                $"Singleton {serviceType.Name} incorrectly injects DbContext");
        }
    }

    Log.Information("Dependency injection validation passed");
}

// In Main()
var app = builder.Build();
ValidateDependencyInjection(app.Services);
```

**Step 4: Document pattern**
Add comments to QueueManager and other singletons:
```csharp
/// <summary>
/// QueueManager is a singleton service that processes the download queue.
/// NOTE: As a singleton, it must NOT inject DavDatabaseContext (which is scoped).
/// Instead, create new context instances per operation using 'await using var dbContext = new DavDatabaseContext()'
/// </summary>
public class QueueManager : IDisposable
```

#### Testing Requirements
- Run startup validation - should pass
- Verify no exceptions during application start
- Run for 24 hours to ensure no "DbContext disposed" errors

#### Success Criteria
- [ ] Startup validation passes
- [ ] All singletons documented with DbContext pattern
- [ ] No DbContext lifetime violations found
- [ ] 24-hour stability test passes

---

### CRITICAL-3: Fix ConnectionPool Dispose Deadlock
**Priority:** P0 - Prevents clean shutdown
**Complexity:** Low
**Time Estimate:** 1 hour
**Risk:** Medium - Affects shutdown only

#### Current Code
```csharp
// backend/Clients/Usenet/Connections/ConnectionPool.cs:315-327
public void Dispose()
{
    try
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    catch { }
}
```

#### Implementation Plan
1. Fix Dispose() to avoid sync context deadlocks
2. Remove catch-all exception swallowing
3. Add logging for disposal errors
4. Add timeout protection

#### Proposed Fix
```csharp
// backend/Clients/Usenet/Connections/ConnectionPool.cs:315-335
public void Dispose()
{
    // Use Task.Run to execute async disposal on thread pool thread
    // This avoids sync context deadlocks and properly waits for completion
    try
    {
        // Use timeout to prevent hanging indefinitely
        var disposeTask = Task.Run(async () => await DisposeAsync());

        if (!disposeTask.Wait(TimeSpan.FromSeconds(30)))
        {
            // Log timeout but don't throw - disposal should be best-effort
            System.Diagnostics.Debug.WriteLine(
                "ConnectionPool disposal timed out after 30 seconds");
        }
    }
    catch (Exception ex)
    {
        // Log but don't throw - disposal should not throw
        // However, be more specific than catch-all
        System.Diagnostics.Debug.WriteLine(
            $"ConnectionPool disposal failed: {ex.GetType().Name}: {ex.Message}");
    }
}
```

#### Alternative (Better): Make callers use IAsyncDisposable
```csharp
// backend/Clients/Usenet/MultiServerNntpClient.cs:566-577
public void Dispose()
{
    // Synchronous disposal - prefer DisposeAsync()
    foreach (var server in _servers)
    {
        server.ConnectionPool.OnConnectionPoolChanged -= OnServerConnectionPoolChanged;

        // Option 1: Call sync method that doesn't block
        if (server.Client is IDisposable disposable)
            disposable.Dispose();
    }
    _servers.Clear();
    _updateLock.Dispose();
    GC.SuppressFinalize(this);
}

public async ValueTask DisposeAsync()
{
    foreach (var server in _servers)
    {
        server.ConnectionPool.OnConnectionPoolChanged -= OnServerConnectionPoolChanged;

        // Use async disposal for connection pools
        if (server.ConnectionPool is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (server.Client is IDisposable disposable)
            disposable.Dispose();
    }
    _servers.Clear();
    _updateLock.Dispose();
    GC.SuppressFinalize(this);
}
```

#### Testing Requirements
- Test graceful shutdown with SIGTERM
- Test shutdown under load (active connections)
- Test shutdown with stuck connections
- Verify no hangs within 30 seconds
- Monitor for connection leaks after shutdown

#### Success Criteria
- [ ] Application shuts down within 30 seconds on SIGTERM
- [ ] No deadlocks during disposal
- [ ] Disposal errors logged (not silently swallowed)
- [ ] All connections closed on shutdown

---

### CRITICAL-4: Add Full Exception Logging
**Priority:** P0 - Critical for production debugging
**Complexity:** Low
**Time Estimate:** 30 minutes
**Risk:** Low

#### Current Code
```csharp
// backend/Middlewares/ExceptionMiddleware.cs:50-60
catch (Exception e) when (IsDavItemRequest(context))
{
    if (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = 500;
    }
    var filePath = GetRequestFilePath(context);
    Log.Error($"File `{filePath}` could not be read due to unhandled {e.GetType()}: {e.Message}");
}
```

#### Implementation Plan
1. Log full exception with stack trace
2. Add exception type categorization
3. Re-throw critical exceptions
4. Add sanitization for production

#### Proposed Fix
```csharp
// backend/Middlewares/ExceptionMiddleware.cs:50-75
catch (Exception e) when (IsDavItemRequest(context))
{
    if (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = 500;
    }

    var filePath = GetRequestFilePath(context);

    // Log full exception with stack trace for debugging
    Log.Error(e,
        "Unhandled exception serving WebDAV file {FilePath}. Type: {ExceptionType}, Request: {Method} {Path}",
        filePath, e.GetType().Name, context.Request.Method, context.Request.Path);

    // Re-throw critical exceptions that should crash the app
    if (e is OutOfMemoryException || e is StackOverflowException)
    {
        Log.Fatal(e, "Critical exception occurred - application will terminate");
        throw;
    }

    // For database errors, log additional context
    if (e is DbUpdateException || e.GetType().Namespace?.StartsWith("Microsoft.EntityFrameworkCore") == true)
    {
        Log.Error("Database error details - this may indicate database corruption or connection issues");
    }
}
```

#### Additional Improvements
```csharp
// Add exception metrics/counters for monitoring
private static readonly Dictionary<string, int> _exceptionCounts = new();
private static readonly object _metricsLock = new();

private void RecordException(Exception e)
{
    var exceptionType = e.GetType().Name;
    lock (_metricsLock)
    {
        _exceptionCounts.TryGetValue(exceptionType, out var count);
        _exceptionCounts[exceptionType] = count + 1;
    }
}

// Expose metrics endpoint
public static Dictionary<string, int> GetExceptionMetrics()
{
    lock (_metricsLock)
    {
        return new Dictionary<string, int>(_exceptionCounts);
    }
}
```

#### Testing Requirements
- Trigger various exception types
- Verify full stack traces in logs
- Verify OOM/StackOverflow re-throw
- Verify database errors logged with context

#### Success Criteria
- [ ] Full exception stack traces logged
- [ ] Exception types visible in logs
- [ ] Critical exceptions terminate app
- [ ] Database errors have additional context

---

## 🔶 Phase 2: High Priority Fixes (Week 2)

### HIGH-1: Add Database Connection Error Handling
**Priority:** P1
**Complexity:** Low
**Time Estimate:** 1 hour
**Risk:** Low

#### Current Code
```csharp
// backend/Database/DavDatabaseClient.cs:61-70
var connection = Ctx.Database.GetDbConnection();
if (connection.State != System.Data.ConnectionState.Open)
    await connection.OpenAsync(ct);
// ... execute command without try-catch
```

#### Implementation Plan
1. Add try-catch around connection operations
2. Add retry logic for transient errors
3. Log connection state transitions
4. Add connection timeout configuration

#### Proposed Fix
```csharp
// backend/Database/DavDatabaseClient.cs:39-85
public async Task<long> GetRecursiveSize(Guid dirId, CancellationToken ct = default)
{
    if (dirId == DavItem.Root.Id)
    {
        return await Ctx.Items.SumAsync(x => x.FileSize, ct) ?? 0;
    }

    const string sql = @"
        WITH RECURSIVE RecursiveChildren AS (
            SELECT Id, FileSize
            FROM DavItems
            WHERE ParentId = @parentId

            UNION ALL

            SELECT d.Id, d.FileSize
            FROM DavItems d
            INNER JOIN RecursiveChildren rc ON d.ParentId = rc.Id
        )
        SELECT IFNULL(SUM(FileSize), 0)
        FROM RecursiveChildren;
    ";

    const int maxRetries = 3;
    var retryDelays = new[] {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    };

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try
        {
            var connection = Ctx.Database.GetDbConnection();

            // Ensure connection is open
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 30; // 30 second timeout

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@parentId";
            parameter.Value = dirId;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result);
        }
        catch (Exception ex) when (
            ex is System.Data.Common.DbException ||
            ex is InvalidOperationException)
        {
            // Log the error
            Serilog.Log.Warning(ex,
                "Database query failed for GetRecursiveSize (attempt {Attempt}/{MaxRetries}): {ErrorMessage}",
                attempt + 1, maxRetries + 1, ex.Message);

            // If this was the last attempt, throw
            if (attempt >= maxRetries)
            {
                Serilog.Log.Error(ex,
                    "Failed to calculate recursive size for directory {DirId} after {Retries} retries",
                    dirId, maxRetries + 1);
                throw;
            }

            // Wait before retry (with cancellation support)
            await Task.Delay(retryDelays[attempt], ct);
        }
        catch (OperationCanceledException)
        {
            // Don't retry on cancellation
            throw;
        }
    }

    // Should never reach here
    throw new InvalidOperationException("Retry loop completed unexpectedly");
}
```

#### Testing Requirements
- Test with database locked (simulate with busy handler)
- Test with connection closed mid-query
- Test with very long-running query
- Verify retry logic works
- Verify cancellation stops retries

#### Success Criteria
- [ ] Transient errors trigger retry
- [ ] Permanent errors logged and thrown
- [ ] Connection timeout prevents hangs
- [ ] Cancellation stops retries immediately

---

### HIGH-2: Audit and Fix Cancellation Token Propagation
**Priority:** P1
**Complexity:** High
**Time Estimate:** 4-6 hours
**Risk:** Medium - Many files to audit

#### Implementation Plan
1. Create audit script to find missing CancellationToken parameters
2. Prioritize most critical paths (streaming, queue processing)
3. Add cancellation token to method signatures
4. Propagate tokens through call chains
5. Add cancellation checks in long-running loops

#### Audit Script
```bash
#!/bin/bash
# Find async methods without CancellationToken parameter

echo "Async methods missing CancellationToken:"
grep -rn "async Task" backend/ --include="*.cs" | \
  grep -v "CancellationToken" | \
  grep -v "// NO CT:" | \
  grep -v "Test" | \
  head -30
```

#### Priority Areas to Fix

**1. MultiServerNntpClient.cs:218-223**
```csharp
// BEFORE
public async Task WaitForReady(CancellationToken cancellationToken)
{
    var tasks = _servers.Select(s => s.Client.WaitForReady(cancellationToken));
    await Task.WhenAll(tasks);
}

// AFTER - Add timeout
public async Task WaitForReady(CancellationToken cancellationToken)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(TimeSpan.FromSeconds(30)); // Timeout if any server hangs

    var tasks = _servers.Select(s => s.Client.WaitForReady(cts.Token));

    try
    {
        await Task.WhenAll(tasks);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // User cancelled - rethrow
        throw;
    }
    catch (OperationCanceledException)
    {
        // Timeout - log which servers didn't respond
        var completedServers = tasks.Where(t => t.IsCompleted).Count();
        Serilog.Log.Error(
            "WaitForReady timed out after 30 seconds. {Completed}/{Total} servers ready",
            completedServers, _servers.Count);
        throw new TimeoutException("Server initialization timed out after 30 seconds");
    }
}
```

**2. CombinedStream.cs - Add cancellation checks**
```csharp
// backend/Streams/CombinedStream.cs:28-57
public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
{
    if (count == 0) return 0;

    while (true) // Changed from !cancellationToken.IsCancellationRequested
    {
        cancellationToken.ThrowIfCancellationRequested(); // Explicit check

        // If we haven't read the first stream, read it.
        if (_currentStream == null)
        {
            if (!_streams.MoveNext()) return 0;
            _currentStream = await _streams.Current;
        }

        // read from our current stream
        var readCount = await _currentStream.ReadAsync(
            buffer.AsMemory(offset, count),
            cancellationToken
        );
        _position += readCount;
        if (readCount > 0) return readCount;

        // If we couldn't read anything from our current stream,
        // it's time to advance to the next stream.
        await _currentStream.DisposeAsync();
        if (!_streams.MoveNext()) return 0;
        _currentStream = await _streams.Current;
    }
}
```

**3. QueueItemProcessor - ensure cancellation propagates**
```bash
# Find QueueItemProcessor and verify all database calls pass ct
# Find all async calls and ensure ct is passed
```

#### Testing Requirements
- Test request cancellation during streaming
- Test SIGTERM during queue processing
- Test client disconnect during long operation
- Verify resources cleaned up on cancellation

#### Success Criteria
- [ ] All critical paths propagate cancellation
- [ ] Cancellation tests pass
- [ ] No zombie tasks after cancellation
- [ ] Resources cleaned up properly

---

### HIGH-3: Add Queue Processing Auto-Restart
**Priority:** P1 - Critical for reliability
**Complexity:** Medium
**Time Estimate:** 2-3 hours
**Risk:** Low

#### Current Code
```csharp
// backend/Queue/QueueManager.cs:37-47
_ = Task.Run(async () =>
{
    try
    {
        await ProcessQueueAsync(_cancellationTokenSource.Token);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Log.Fatal(ex, "QueueManager failed unexpectedly - queue processing has stopped");
    }
});
```

#### Implementation Plan
1. Add retry loop with exponential backoff
2. Add maximum retry limit with manual reset
3. Add health check endpoint for queue status
4. Add metrics for restart count
5. Fire websocket event on queue failure

#### Proposed Fix
```csharp
// backend/Queue/QueueManager.cs:30-80
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

    // Start queue processing with auto-restart
    _ = Task.Run(() => QueueProcessingWithRetry(_cancellationTokenSource.Token));
}

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
                    "Queue has been stable for {Hours} hour(s), resetting restart counter",
                    _maxRetryWindow.TotalHours);
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

// Add health check method
public (bool IsRunning, int RestartCount, DateTime? LastFailure) GetQueueHealth()
{
    // Queue is considered running if we haven't exceeded restart limit
    var isRunning = _queueRestartCount <= MaxRestartsPerHour;
    return (isRunning, _queueRestartCount, _lastQueueFailure);
}
```

#### Add Health Check Endpoint
```csharp
// backend/Api/Controllers/QueueController.cs or similar
[HttpGet("queue/health")]
public IActionResult GetQueueHealth()
{
    var queueManager = HttpContext.RequestServices.GetRequiredService<QueueManager>();
    var (isRunning, restartCount, lastFailure) = queueManager.GetQueueHealth();

    return Ok(new
    {
        status = isRunning ? "healthy" : "unhealthy",
        restartCount,
        lastFailure,
        message = isRunning
            ? "Queue processing is running normally"
            : "Queue processing has failed too many times and requires manual intervention"
    });
}
```

#### Testing Requirements
- Test queue crash and auto-restart
- Test multiple failures trigger backoff
- Test restart count resets after stability
- Test max restart limit stops infinite loop
- Test health endpoint returns correct status

#### Success Criteria
- [ ] Queue automatically restarts on failure
- [ ] Exponential backoff prevents rapid failures
- [ ] Restart limit prevents infinite loops
- [ ] Health endpoint shows queue status
- [ ] Websocket events fired on failure

---

### HIGH-4: Fix UsenetStreamingClient Disposal
**Priority:** P1
**Complexity:** Low
**Time Estimate:** 1 hour
**Risk:** Low

#### Current Code
```csharp
// backend/Clients/Usenet/UsenetStreamingClient.cs:117-142
public void Dispose()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
        return;

    // Unsubscribe from all events...

    // Dispose the segment cache
    lock (_segmentCacheLock)
    {
        _healthySegmentCache?.Dispose();
    }
    // Missing: disposal of _client and _multiServerClient
}
```

#### Implementation Plan
1. Add disposal of all IDisposable fields
2. Implement IAsyncDisposable for proper async cleanup
3. Add null checks
4. Add logging for disposal errors

#### Proposed Fix
```csharp
// backend/Clients/Usenet/UsenetStreamingClient.cs:117-165
public void Dispose()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
        return;

    try
    {
        // Unsubscribe from all events to prevent memory leaks
        if (_multiServerClient != null && _poolChangedHandler != null)
            _multiServerClient.OnAggregateConnectionPoolChanged -= _poolChangedHandler;

        if (_healthTracker != null)
        {
            if (_serverUnavailableHandler != null)
                _healthTracker.OnServerUnavailable -= _serverUnavailableHandler;
            if (_healthResetHandler != null)
                _healthTracker.OnAllServersHealthReset -= _healthResetHandler;
        }

        if (_configManager != null && _configChangedHandler != null)
            _configManager.OnConfigChanged -= _configChangedHandler;

        // Dispose the segment cache
        lock (_segmentCacheLock)
        {
            _healthySegmentCache?.Dispose();
            _healthySegmentCache = null;
        }

        // Dispose clients (CachingNntpClient wraps MultiServerNntpClient)
        // Disposing _client will dispose the cache and the underlying client
        if (_client is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }

        // MultiServerNntpClient should be disposed by the wrapper,
        // but dispose it directly if it wasn't
        if (_multiServerClient != null && !ReferenceEquals(_client, _multiServerClient))
        {
            _multiServerClient.Dispose();
        }
    }
    catch (Exception ex)
    {
        // Log but don't throw - disposal should be best-effort
        Serilog.Log.Error(ex, "Error during UsenetStreamingClient disposal");
    }
}

public async ValueTask DisposeAsync()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
        return;

    try
    {
        // Unsubscribe from events (same as Dispose)
        if (_multiServerClient != null && _poolChangedHandler != null)
            _multiServerClient.OnAggregateConnectionPoolChanged -= _poolChangedHandler;

        if (_healthTracker != null)
        {
            if (_serverUnavailableHandler != null)
                _healthTracker.OnServerUnavailable -= _serverUnavailableHandler;
            if (_healthResetHandler != null)
                _healthTracker.OnAllServersHealthReset -= _healthResetHandler;
        }

        if (_configManager != null && _configChangedHandler != null)
            _configManager.OnConfigChanged -= _configChangedHandler;

        // Dispose the segment cache
        lock (_segmentCacheLock)
        {
            _healthySegmentCache?.Dispose();
            _healthySegmentCache = null;
        }

        // Async disposal of clients
        if (_client is IAsyncDisposable asyncDisposableClient)
        {
            await asyncDisposableClient.DisposeAsync();
        }
        else if (_client is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }

        if (_multiServerClient != null && !ReferenceEquals(_client, _multiServerClient))
        {
            if (_multiServerClient is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                _multiServerClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
    catch (Exception ex)
    {
        Serilog.Log.Error(ex, "Error during UsenetStreamingClient async disposal");
    }
}
```

#### Update Program.cs Registration
```csharp
// Since UsenetStreamingClient is now IAsyncDisposable,
// ensure it's properly disposed on shutdown
app.Lifetime.ApplicationStopping.Register(() =>
{
    var streamingClient = app.Services.GetService<UsenetStreamingClient>();
    if (streamingClient != null)
    {
        try
        {
            streamingClient.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing UsenetStreamingClient on shutdown");
        }
    }
});
```

#### Testing Requirements
- Test graceful shutdown disposes all resources
- Test disposal under load
- Test disposal with active connections
- Monitor for resource leaks after disposal

#### Success Criteria
- [ ] All IDisposable fields disposed
- [ ] No resource leaks on shutdown
- [ ] Disposal errors logged
- [ ] Both Dispose and DisposeAsync work correctly

---

## 🟡 Phase 3: Medium/Low Priority + Testing (Week 3)

### MEDIUM-1: Fix CombinedStream Disposal
**Priority:** P2
**Complexity:** Medium
**Time Estimate:** 2 hours
**Risk:** Low - Edge case

#### Proposed Fix
```csharp
// backend/Streams/CombinedStream.cs:119-142
public override async ValueTask DisposeAsync()
{
    if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1)
        return;

    try
    {
        // Dispose current stream
        if (_currentStream != null)
        {
            await _currentStream.DisposeAsync();
            _currentStream = null;
        }

        // Dispose any remaining streams in the enumerator
        // This handles pre-fetched Task<Stream> items
        var remainingStreamTasks = new List<Task<Stream>>();
        while (_streams.MoveNext())
        {
            remainingStreamTasks.Add(_streams.Current);
        }

        // Dispose the enumerator first
        _streams.Dispose();

        // Now await and dispose the remaining streams
        foreach (var streamTask in remainingStreamTasks)
        {
            try
            {
                // Give each stream a short timeout to avoid hanging
                var stream = await streamTask.WaitAsync(TimeSpan.FromSeconds(5));
                await stream.DisposeAsync();
            }
            catch (TimeoutException)
            {
                // Stream task didn't complete - log but continue
                System.Diagnostics.Debug.WriteLine("Stream task timed out during disposal");
            }
            catch (Exception ex)
            {
                // Best effort cleanup - log but don't fail
                System.Diagnostics.Debug.WriteLine($"Error disposing stream: {ex.Message}");
            }
        }

        GC.SuppressFinalize(this);
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"CombinedStream disposal error: {ex.Message}");
    }
}
```

---

### MEDIUM-2: Make Cache Sizes Configurable
**Priority:** P2
**Complexity:** Low
**Time Estimate:** 1 hour
**Risk:** Low

#### Implementation
```csharp
// backend/Config/ConfigManager.cs - Add methods
public int GetSegmentCacheSize()
{
    var value = StringUtil.EmptyToNull(GetConfigValue("cache.segment-cache-size"))
        ?? EnvironmentUtil.GetVariable("SEGMENT_CACHE_SIZE")
        ?? "50000";

    if (!int.TryParse(value, out var result))
        result = 50000;

    return Math.Clamp(result, 1000, 200000); // 1K to 200K entries
}

public int GetArticleCacheSize()
{
    var value = StringUtil.EmptyToNull(GetConfigValue("cache.article-cache-size"))
        ?? EnvironmentUtil.GetVariable("ARTICLE_CACHE_SIZE")
        ?? "8192";

    if (!int.TryParse(value, out var result))
        result = 8192;

    return Math.Clamp(result, 100, 50000); // 100 to 50K entries
}
```

```csharp
// backend/Clients/Usenet/UsenetStreamingClient.cs:46
_healthySegmentCache = new MemoryCache(new MemoryCacheOptions()
{
    SizeLimit = _configManager.GetSegmentCacheSize()
});

// Line 110
var cache = new MemoryCache(new MemoryCacheOptions()
{
    SizeLimit = _configManager.GetArticleCacheSize()
});
```

---

### LOW-1: Add ConfigManager IDisposable
**Priority:** P3
**Complexity:** Low
**Time Estimate:** 30 minutes
**Risk:** Very low

#### Implementation
```csharp
// backend/Config/ConfigManager.cs
public class ConfigManager : IDisposable
{
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private bool _disposed = false;

    // ... existing code ...

    public void Dispose()
    {
        if (_disposed) return;

        _configLock?.Dispose();
        _disposed = true;
    }
}
```

```csharp
// backend/Program.cs - Update registration
builder.Services
    .AddSingleton<ConfigManager>(sp =>
    {
        var config = new ConfigManager();
        config.LoadConfig().Wait();
        return config;
    });

// Add disposal on shutdown
app.Lifetime.ApplicationStopping.Register(() =>
{
    var config = app.Services.GetService<ConfigManager>();
    config?.Dispose();
});
```

---

### LOW-2: Fix CancellationTokenSource Using Statement
**Priority:** P3
**Complexity:** Low
**Time Estimate:** 15 minutes
**Risk:** Very low

#### Implementation
```csharp
// backend/Queue/QueueManager.cs:82-116
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
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            // process the queue-item with proper disposal
            using (var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await LockAsync(() =>
                {
                    _inProgressQueueItem = BeginProcessingQueueItem(
                        dbClient, topItem.queueItem, topItem.queueNzbContents, queueItemCancellationTokenSource
                    );
                });
                await (_inProgressQueueItem?.ProcessingTask ?? Task.CompletedTask);
            } // CTS disposed here
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
```

---

## 🧪 Comprehensive Testing Plan

### Phase 3.1: Load Testing (2 days)
**Tools:** k6, Apache Bench, custom scripts

**Test 1: Concurrent Streams**
- Simulate 50 concurrent WebDAV streaming clients
- Monitor thread pool, memory, connections
- Duration: 4 hours
- Success: No thread starvation, stable memory

**Test 2: Queue Processing Under Load**
- Add 100 queue items
- Trigger random failures
- Verify auto-restart works
- Success: Queue recovers, no hangs

**Test 3: Database Connection Pool**
- Simulate 1000 rapid requests
- Monitor database connections
- Check for leaks
- Success: Connections properly recycled

### Phase 3.2: Memory Leak Testing (2 days)
**Tools:** dotMemory, dotnet-counters, custom metrics

**Test 1: 24-Hour Stability**
- Run application for 24 hours
- Monitor memory growth
- Track GC behavior
- Success: Memory stable, no leaks

**Test 2: Connection Lifecycle**
- Open/close 10,000 connections
- Monitor for leaks
- Check event unsubscription
- Success: All resources cleaned up

**Test 3: Stream Disposal**
- Stream 1000 files
- Cancel halfway through
- Verify cleanup
- Success: No stream handles leaked

### Phase 3.3: Graceful Shutdown Testing (1 day)

**Test 1: Clean Shutdown**
- Start application
- Begin streaming
- Send SIGTERM
- Success: Shutdown within 30s, no errors

**Test 2: Shutdown Under Load**
- 50 active streams
- Queue processing
- Send SIGTERM
- Success: Clean shutdown, resources freed

**Test 3: Repeated Restart**
- Start, wait 10s, stop
- Repeat 100 times
- Check for resource accumulation
- Success: No leaks across restarts

---

## 📊 Progress Tracking

### Week 1 Checklist
- [ ] CRITICAL-1: Thread pool configuration
  - [ ] Implementation complete
  - [ ] Tests pass (1, 4, 16 CPU scenarios)
  - [ ] Documentation updated
- [ ] CRITICAL-2: Database context audit
  - [ ] Audit complete
  - [ ] Validation added
  - [ ] 24-hour test passes
- [ ] CRITICAL-3: ConnectionPool dispose
  - [ ] Implementation complete
  - [ ] Shutdown tests pass
  - [ ] No deadlocks observed
- [ ] CRITICAL-4: Exception logging
  - [ ] Full logging implemented
  - [ ] Critical exceptions re-throw
  - [ ] Tests verified

### Week 2 Checklist
- [ ] HIGH-1: Database error handling
  - [ ] Retry logic implemented
  - [ ] Tests pass
- [ ] HIGH-2: Cancellation token audit
  - [ ] Audit complete (30+ locations)
  - [ ] Critical paths fixed
  - [ ] Cancellation tests pass
- [ ] HIGH-3: Queue auto-restart
  - [ ] Implementation complete
  - [ ] Health endpoint added
  - [ ] Restart tests pass
- [ ] HIGH-4: UsenetStreamingClient disposal
  - [ ] Implementation complete
  - [ ] No leaks detected

### Week 3 Checklist
- [ ] MEDIUM-1: CombinedStream disposal
- [ ] MEDIUM-2: Configurable cache sizes
- [ ] LOW-1: ConfigManager disposal
- [ ] LOW-2: CancellationTokenSource using
- [ ] Load testing complete
- [ ] Memory leak testing complete
- [ ] Shutdown testing complete

---

## 🚀 Deployment Plan

### Pre-Deployment
1. All Week 1-3 checklist items complete
2. All tests passing
3. Code reviewed
4. Documentation updated
5. Monitoring configured

### Deployment Strategy
1. Deploy to staging environment
2. Run 24-hour soak test
3. Monitor metrics closely
4. Deploy to production with gradual rollout
5. Monitor for first 48 hours

### Rollback Plan
- Keep previous version ready
- Monitor error rates
- Auto-rollback if error rate > 5%
- Manual rollback procedure documented

---

## 📈 Success Metrics

### Stability
- Uptime > 99.9%
- No queue processing failures
- Zero deadlocks on shutdown
- Exception rate < 0.1% of requests

### Performance
- Thread pool utilization < 80%
- Response time P95 < 500ms
- Streaming latency < 2s to first byte

### Memory
- Memory growth < 10MB per day
- GC pressure stable
- No memory leaks detected

### Resource Management
- Database connections < 50 active
- WebDAV connections properly closed
- No file handle leaks

---

*End of Implementation Plan*

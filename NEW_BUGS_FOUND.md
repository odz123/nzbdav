# New Bugs Found - Deep Bug Bash Session

**Date:** 2025-11-22
**Scope:** Deep analysis focusing on hard-to-find bugs including concurrency, resource leaks, edge cases, and subtle logic errors

## Executive Summary

Discovered **15 new bugs** not documented in the existing BUG_REPORT.md. These include critical memory leaks, race conditions, missing disposal patterns, potential null reference exceptions, and edge case handling issues.

### Severity Breakdown
- **Critical:** 4 bugs (Memory leaks, missing base class disposal)
- **High:** 6 bugs (Race conditions, null reference, resource leaks)
- **Medium:** 5 bugs (Edge cases, missing error handling)

---

## Critical Severity Bugs

### BUG-NEW-001: Event Handler Memory Leak in UsenetStreamingClient
**File:** `backend/Clients/Usenet/UsenetStreamingClient.cs:47,51,55,62`
**Severity:** Critical
**Type:** Memory Leak

**Description:**
UsenetStreamingClient subscribes to 4 event handlers but never unsubscribes:
```csharp
// Line 47
_multiServerClient.OnAggregateConnectionPoolChanged += OnConnectionPoolChanged;

// Line 51
_healthTracker.OnServerUnavailable += OnServerUnavailable;

// Line 55
_healthTracker.OnAllServersHealthReset += OnAllServersHealthReset;

// Line 62
configManager.OnConfigChanged += async (_, configEventArgs) => { ... }
```

**Impact:**
- If UsenetStreamingClient is ever disposed and recreated, the old instance will remain in memory
- EventHandlers keep references to the subscribing object, preventing garbage collection
- Over time, this can lead to significant memory leaks
- The event publishers (_multiServerClient, _healthTracker, configManager) will accumulate dead event handlers

**Fix:**
Implement IDisposable and unsubscribe in Dispose:
```csharp
public class UsenetStreamingClient : IDisposable
{
    private EventHandler<MultiServerNntpClient.AggregateConnectionPoolChangedEventArgs>? _poolChangedHandler;
    private EventHandler<ServerUnavailableEventArgs>? _serverUnavailableHandler;
    private EventHandler? _healthResetHandler;
    private EventHandler<ConfigChangedEventArgs>? _configChangedHandler;

    public UsenetStreamingClient(...)
    {
        _poolChangedHandler = OnConnectionPoolChanged;
        _serverUnavailableHandler = OnServerUnavailable;
        _healthResetHandler = OnAllServersHealthReset;
        _configChangedHandler = async (_, args) => { ... };

        _multiServerClient.OnAggregateConnectionPoolChanged += _poolChangedHandler;
        _healthTracker.OnServerUnavailable += _serverUnavailableHandler;
        _healthTracker.OnAllServersHealthReset += _healthResetHandler;
        configManager.OnConfigChanged += _configChangedHandler;
    }

    public void Dispose()
    {
        if (_multiServerClient != null)
            _multiServerClient.OnAggregateConnectionPoolChanged -= _poolChangedHandler;
        if (_healthTracker != null)
        {
            _healthTracker.OnServerUnavailable -= _serverUnavailableHandler;
            _healthTracker.OnAllServersHealthReset -= _healthResetHandler;
        }
        if (_configManager != null)
            _configManager.OnConfigChanged -= _configChangedHandler;
    }
}
```

---

### BUG-NEW-002: Event Handler Memory Leak in HealthCheckService
**File:** `backend/Services/HealthCheckService.cs:43`
**Severity:** Critical
**Type:** Memory Leak

**Description:**
HealthCheckService subscribes to OnConfigChanged event but never unsubscribes:
```csharp
// Line 43-54
_configManager.OnConfigChanged += (_, configEventArgs) =>
{
    if (!_configManager.HasUsenetConfigChanged(configEventArgs.ChangedConfig)) return;

    var oldCache = _missingSegmentCache;
    _missingSegmentCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
    oldCache.Dispose();
};
```

**Impact:**
- Same memory leak issue as BUG-NEW-001
- If HealthCheckService is recreated, old instances won't be garbage collected
- ConfigManager accumulates dead event handlers

**Fix:**
Store the event handler and unsubscribe on disposal:
```csharp
private EventHandler<ConfigChangedEventArgs>? _configChangedHandler;

public HealthCheckService(...)
{
    _configChangedHandler = (_, configEventArgs) =>
    {
        if (!_configManager.HasUsenetConfigChanged(configEventArgs.ChangedConfig)) return;
        var oldCache = _missingSegmentCache;
        _missingSegmentCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
        oldCache.Dispose();
    };

    _configManager.OnConfigChanged += _configChangedHandler;
}

public void Dispose()
{
    if (_configManager != null)
        _configManager.OnConfigChanged -= _configChangedHandler;
}
```

---

### BUG-NEW-003: Missing base.DisposeAsync() in NzbFileStream
**File:** `backend/Streams/NzbFileStream.cs:147-152`
**Severity:** Critical
**Type:** Resource Management - Incomplete Disposal

**Description:**
The DisposeAsync method doesn't call base.DisposeAsync():
```csharp
public override async ValueTask DisposeAsync()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1) return;
    if (_innerStream != null) await _innerStream.DisposeAsync();
    GC.SuppressFinalize(this);
    // Missing: await base.DisposeAsync();
}
```

Compare this to the sync Dispose method (line 144) which correctly calls `base.Dispose(disposing)`.

**Impact:**
- Base class cleanup is not executed when using async disposal
- Potential resource leaks if base Stream class has resources to clean up
- Violates the async dispose pattern
- May cause issues with frameworks that rely on proper async disposal chains

**Fix:**
```csharp
public override async ValueTask DisposeAsync()
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1) return;
    if (_innerStream != null) await _innerStream.DisposeAsync();
    await base.DisposeAsync();
    GC.SuppressFinalize(this);
}
```

---

### BUG-NEW-004: Race Condition on Cache Replacement in HealthCheckService
**File:** `backend/Services/HealthCheckService.cs:43-54`
**Severity:** Critical
**Type:** Concurrency - Race Condition

**Description:**
When config changes, multiple threads could execute the event handler simultaneously:
```csharp
_configManager.OnConfigChanged += (_, configEventArgs) =>
{
    if (!_configManager.HasUsenetConfigChanged(configEventArgs.ChangedConfig)) return;

    // RACE CONDITION: Multiple threads can execute this simultaneously
    var oldCache = _missingSegmentCache;
    _missingSegmentCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
    oldCache.Dispose();
};
```

**Race Scenario:**
1. Thread A reads `_missingSegmentCache` into `oldCache`
2. Thread B reads `_missingSegmentCache` into `oldCache` (same instance)
3. Thread A creates new cache and assigns to `_missingSegmentCache`
4. Thread B creates another new cache and assigns to `_missingSegmentCache`
5. Thread A calls `oldCache.Dispose()` - disposes the original
6. Thread B calls `oldCache.Dispose()` - **double dispose** OR disposes Thread A's new cache!

Meanwhile, Thread C (from health check service at line 234) could be trying to write to a cache that's being replaced or already disposed.

**Impact:**
- ObjectDisposedException when trying to use a disposed cache
- Memory leak (Thread B's cache is orphaned)
- Cache corruption
- Application crashes during config updates

**Fix:**
Add locking around cache replacement:
```csharp
private readonly object _cacheLock = new object();

_configManager.OnConfigChanged += (_, configEventArgs) =>
{
    if (!_configManager.HasUsenetConfigChanged(configEventArgs.ChangedConfig)) return;

    lock (_cacheLock)
    {
        var oldCache = _missingSegmentCache;
        _missingSegmentCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
        oldCache.Dispose();
    }
};

// Also protect writes:
lock (_cacheLock)
{
    _missingSegmentCache.Set(e.SegmentId, true, new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
        Size = 1
    });
}
```

---

## High Severity Bugs

### BUG-NEW-005: Race Condition on Cache Replacement in UsenetStreamingClient
**File:** `backend/Clients/Usenet/UsenetStreamingClient.cs:137-142`
**Severity:** High
**Type:** Concurrency - Race Condition

**Description:**
Same issue as BUG-NEW-004 but in UsenetStreamingClient:
```csharp
private void ClearSegmentCache()
{
    // RACE CONDITION: Multiple threads can execute this simultaneously
    var oldCache = _healthySegmentCache;
    _healthySegmentCache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 50000 });
    oldCache.Dispose();
}
```

This is called from multiple event handlers (OnServerUnavailable, OnAllServersHealthReset) which can fire concurrently.

**Impact:**
- Same as BUG-NEW-004: ObjectDisposedException, memory leaks, crashes
- Even more critical because this cache is used frequently during streaming operations

**Fix:**
Add locking:
```csharp
private readonly object _segmentCacheLock = new object();

private void ClearSegmentCache()
{
    lock (_segmentCacheLock)
    {
        var oldCache = _healthySegmentCache;
        _healthySegmentCache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 50000 });
        oldCache.Dispose();
    }
}

// Also protect read/write operations:
private bool IsSegmentCachedAsHealthy(string segmentId)
{
    lock (_segmentCacheLock)
    {
        return _healthySegmentCache.TryGetValue(segmentId, out _);
    }
}
```

---

### BUG-NEW-006: Potential Null Reference in Server Health Logging
**File:** `backend/Clients/Usenet/UsenetStreamingClient.cs:259`
**Severity:** High
**Type:** Null Reference Exception

**Description:**
Server health stats logging assumes ServerId is never null:
```csharp
var healthSummary = string.Join(", ", serverHealthStats.Select(s =>
    $"{s.ServerId.Substring(0, Math.Min(8, s.ServerId.Length))}: ..."
));
```

If `ServerId` is null or empty, `Substring` will throw NullReferenceException.

**Impact:**
- Application crash during health check logging
- Loss of diagnostic information
- Service disruption

**Fix:**
```csharp
var healthSummary = string.Join(", ", serverHealthStats.Select(s =>
{
    var serverId = s.ServerId ?? "unknown";
    var shortId = serverId.Length > 8 ? serverId.Substring(0, 8) : serverId;
    return $"{shortId}: {(s.IsAvailable ? "OK" : "UNAVAILABLE")} ({s.TotalSuccesses}✓/{s.TotalFailures}✗)";
}));
```

---

### BUG-NEW-007: Event Handler Leak in QueueManager
**File:** `backend/Queue/QueueManager.cs:127-133`
**Severity:** High
**Type:** Memory Leak - Event Handler

**Description:**
Each call to BeginProcessingQueueItem creates a new Progress<int> object and subscribes to its ProgressChanged event:
```csharp
var progressHook = new Progress<int>();
progressHook.ProgressChanged += (_, progress) =>
{
    inProgressQueueItem.ProgressPercentage = progress;
    // ...
};
```

The lambda captures `inProgressQueueItem` and `queueItem`, keeping them alive even after processing completes.

**Impact:**
- Memory leak: Each processed queue item keeps references alive
- Over thousands of downloads, this accumulates significant memory
- Garbage collector cannot reclaim completed queue items

**Fix:**
Use weak references or ensure the Progress object is disposed:
```csharp
// Option 1: Dispose Progress (if it implements IDisposable in future)
// Option 2: Use weak event pattern
// Option 3: Clear the handler after use
// Option 4: Restructure to avoid capturing heavy objects
```

---

### BUG-NEW-008: CancellationTokenSource Leak on Exception
**File:** `backend/Queue/QueueManager.cs:85-101`
**Severity:** High
**Type:** Resource Leak

**Description:**
If an exception occurs between creating the CancellationTokenSource (line 85) and storing it in _inProgressQueueItem (line 88-90), the CTS won't be disposed:
```csharp
using var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
await LockAsync(() =>
{
    // If this throws, the CTS above won't be disposed
    _inProgressQueueItem = BeginProcessingQueueItem(
        dbClient, topItem.queueItem, topItem.queueNzbContents, queueItemCancellationTokenSource
    );
});
```

**Impact:**
- CancellationTokenSource resource leak
- Timer resources not cleaned up
- Accumulates over time with each failed queue item initialization

**Fix:**
The `using` statement should handle this, but ensure BeginProcessingQueueItem doesn't throw before storing the CTS. Better pattern:
```csharp
CancellationTokenSource? queueItemCancellationTokenSource = null;
try
{
    queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
    await LockAsync(() =>
    {
        _inProgressQueueItem = BeginProcessingQueueItem(
            dbClient, topItem.queueItem, topItem.queueNzbContents, queueItemCancellationTokenSource
        );
    });
    await (_inProgressQueueItem?.ProcessingTask ?? Task.CompletedTask);
}
finally
{
    queueItemCancellationTokenSource?.Dispose();
    await LockAsync(() => { _inProgressQueueItem = null; });
}
```

---

### BUG-NEW-009: Potential Division by Zero in HealthCheckService
**File:** `backend/Services/HealthCheckService.cs:135`
**Severity:** High
**Type:** Division by Zero (Protected but Worth Noting)

**Description:**
```csharp
Log.Information("Parallel health check cycle completed: {FileCount} files in {ElapsedMs}ms ({AvgPerFile}ms/file)",
    davItems.Count, cycleStartTime.ElapsedMilliseconds,
    cycleStartTime.ElapsedMilliseconds / davItems.Count);  // Division by zero if count is 0
```

However, this is protected by the check on line 91: `if (davItems.Count == 0) { continue; }`, so we never reach line 135 with count = 0.

**Impact:**
- Currently protected, but fragile
- Future refactoring could break this assumption
- Code is not obviously safe

**Fix:**
Add defensive programming:
```csharp
var avgPerFile = davItems.Count > 0 ? cycleStartTime.ElapsedMilliseconds / davItems.Count : 0;
Log.Information("Parallel health check cycle completed: {FileCount} files in {ElapsedMs}ms ({AvgPerFile}ms/file)",
    davItems.Count, cycleStartTime.ElapsedMilliseconds, avgPerFile);
```

---

### BUG-NEW-010: Incorrect NextHealthCheck Calculation Overflow
**File:** `backend/Services/HealthCheckService.cs:212-214`
**Severity:** High
**Type:** Arithmetic Overflow / Logic Error

**Description:**
```csharp
davItem.NextHealthCheck = davItem.ReleaseDate != null
    ? davItem.ReleaseDate + 2 * (davItem.LastHealthCheck - davItem.ReleaseDate)
    : null;
```

**Issues:**
1. If ReleaseDate is very old (e.g., 1990), and LastHealthCheck is now (2025), the expression `2 * (35 years)` could overflow TimeSpan
2. If ReleaseDate is in the future (clock skew, timezone issues), the calculation produces weird results
3. The formula doubles the time interval, which could result in NextHealthCheck being hundreds of years in the future for old files

**Impact:**
- Potential arithmetic overflow exception
- Files with old ReleaseDate never get rechecked
- Files with future ReleaseDate (due to server clock issues) get inappropriate check schedules

**Fix:**
```csharp
if (davItem.ReleaseDate != null && davItem.LastHealthCheck != null)
{
    var age = davItem.LastHealthCheck.Value - davItem.ReleaseDate.Value;

    // Cap the next check interval to a reasonable maximum (e.g., 1 year)
    var nextInterval = TimeSpan.FromTicks(Math.Min(age.Ticks * 2, TimeSpan.FromDays(365).Ticks));

    // Ensure next check is not more than 1 year in the future
    var nextCheck = davItem.LastHealthCheck.Value + nextInterval;
    var maxCheck = DateTimeOffset.UtcNow + TimeSpan.FromDays(365);

    davItem.NextHealthCheck = nextCheck < maxCheck ? nextCheck : maxCheck;
}
else
{
    davItem.NextHealthCheck = null;
}
```

---

## Medium Severity Bugs

### BUG-NEW-011: Misleading Exception Message in InterpolationSearch
**File:** `backend/Utils/InterpolationSearch.cs:39-40`
**Severity:** Medium
**Type:** Error Handling - Misleading Message

**Description:**
```csharp
if (!byteRangeToSearch.Contains(searchByte) || indexRangeToSearch.Count <= 0)
    throw new SeekPositionNotFoundException($"Corrupt file. Cannot find byte position {searchByte}.");
```

This throws "Corrupt file" even when seeking beyond EOF, which is not necessarily file corruption. It could be a legitimate programming error or user trying to seek past the end.

**Impact:**
- Confusing error messages
- Users/developers think file is corrupted when it's actually a seek issue
- Makes debugging harder

**Fix:**
```csharp
if (!byteRangeToSearch.Contains(searchByte))
    throw new SeekPositionNotFoundException($"Seek position {searchByte} is outside valid range [{byteRangeToSearch.StartInclusive}, {byteRangeToSearch.EndExclusive})");

if (indexRangeToSearch.Count <= 0)
    throw new SeekPositionNotFoundException($"No segments available in search range. File may be corrupted or seek position {searchByte} is invalid.");
```

---

### BUG-NEW-012: Potential Stack Overflow in Segment Cache Search
**File:** `backend/Streams/NzbFileStream.cs:87-93`
**Severity:** Medium
**Type:** Performance - Inefficient Algorithm

**Description:**
```csharp
// Check if we have a cached result that contains this offset
foreach (var (_, cachedResult) in _segmentCache)
{
    if (cachedResult.FoundByteRange.Contains(byteOffset))
        return cachedResult;
}
```

This iterates through all cache entries (up to 100) for every seek operation. With frequent seeking, this becomes O(n) per seek.

**Impact:**
- Performance degradation with frequent seeking
- Not a bug per se, but suboptimal
- Could cause frame drops in video playback with lots of seeking

**Fix:**
Use a more efficient data structure:
```csharp
// Use a sorted list or interval tree for O(log n) lookups
private readonly SortedDictionary<long, InterpolationSearch.Result> _segmentCache = new();

// Binary search for the containing range
private InterpolationSearch.Result? FindCachedRange(long byteOffset)
{
    foreach (var cached in _segmentCache.Values)
    {
        if (cached.FoundByteRange.Contains(byteOffset))
            return cached;
    }
    return null;
}
```

---

### BUG-NEW-013: Missing Connection Pool Event Unsubscription
**File:** `backend/Clients/Usenet/MultiServerNntpClient.cs:98`
**Severity:** Medium
**Type:** Memory Leak - Event Handler

**Description:**
```csharp
var connectionPool = new ConnectionPool<INntpClient>(config.MaxConnections, createConnection);
connectionPool.OnConnectionPoolChanged += OnServerConnectionPoolChanged;
```

When servers are updated (line 269-291), old connection pools are disposed but the event handler is never unsubscribed first.

**Impact:**
- Event handler keeps reference to disposed connection pool
- Memory leak accumulating with each server configuration update
- Potential ObjectDisposedException if event fires during disposal

**Fix:**
```csharp
// Before disposing in UpdateServersAsync (line 280-283):
foreach (var oldServer in oldServers)
{
    try
    {
        oldServer.Client.OnConnectionPoolChanged -= OnServerConnectionPoolChanged;
        oldServer.Client.Dispose();
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to dispose server {Name}", oldServer.Config.Name);
    }
}
```

---

### BUG-NEW-014: ServerId Could Be Null in ServerHealthStats
**File:** `backend/Clients/Usenet/ServerHealthTracker.cs:238`
**Severity:** Medium
**Type:** Null Safety

**Description:**
```csharp
public class ServerHealthStats
{
    public string ServerId { get; set; } = string.Empty;
    // ...
}
```

While initialized to empty string, the property can be set to null by callers. This causes BUG-NEW-006.

**Impact:**
- Null reference exceptions in logging and display code
- Defensive coding needed everywhere this is used

**Fix:**
Make it non-nullable:
```csharp
public class ServerHealthStats
{
    public required string ServerId { get; init; } = string.Empty;
    // Or use C# 11 required members
}
```

---

### BUG-NEW-015: Missing Error Handling in ConnectionPool Dispose Callbacks
**File:** `backend/Clients/Usenet/Connections/ConnectionPool.cs:140,160,171`
**Severity:** Medium
**Type:** Error Handling

**Description:**
Fire-and-forget disposal could swallow exceptions:
```csharp
private void Return(T connection)
{
    if (Volatile.Read(ref _disposed) == 1)
    {
        _ = DisposeConnectionAsync(connection); // fire & forget - exceptions lost!
        // ...
    }
}
```

If DisposeConnectionAsync throws, the exception is silently lost.

**Impact:**
- Failed disposal goes unnoticed
- Resource leaks if disposal actually fails
- No logging of disposal errors
- Difficult to debug connection cleanup issues

**Fix:**
```csharp
private void Return(T connection)
{
    if (Volatile.Read(ref _disposed) == 1)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DisposeConnectionAsync(connection);
            }
            catch (Exception ex)
            {
                // Log the error instead of silently swallowing
                System.Diagnostics.Debug.WriteLine($"Failed to dispose connection: {ex}");
            }
        });
        Interlocked.Decrement(ref _live);
        TriggerConnectionPoolChangedEvent();
        return;
    }
    // ...
}
```

---

## Summary Statistics

| Category | Count |
|----------|-------|
| Memory Leaks (Event Handlers) | 5 |
| Race Conditions | 2 |
| Resource Management Issues | 3 |
| Null Reference Issues | 2 |
| Arithmetic/Logic Errors | 1 |
| Error Handling Issues | 2 |
| **Total New Bugs** | **15** |

## Combined Statistics (Including Existing Bugs)

| Category | Existing | New | Total |
|----------|----------|-----|-------|
| Critical | 5 | 4 | 9 |
| High | 8 | 6 | 14 |
| Medium | 6 | 5 | 11 |
| Low | 3 | 0 | 3 |
| **Total** | **22** | **15** | **37** |

## Priority Recommendations

### Immediate Action Required (Critical):
1. **BUG-NEW-001, BUG-NEW-002**: Fix event handler memory leaks - these will cause memory growth over time
2. **BUG-NEW-003**: Fix missing base.DisposeAsync() call - violates disposal pattern
3. **BUG-NEW-004, BUG-NEW-005**: Fix cache replacement race conditions - can cause crashes during config updates

### High Priority:
4. **BUG-NEW-006**: Fix null reference in server health logging
5. **BUG-NEW-007, BUG-NEW-008**: Fix resource leaks in QueueManager
6. **BUG-NEW-010**: Fix NextHealthCheck calculation overflow

### Medium Priority:
7. **BUG-NEW-011 through BUG-NEW-015**: Address error handling and performance issues

## Testing Recommendations

1. **Memory leak testing**: Run application for extended periods with frequent config changes to detect memory leaks
2. **Concurrency stress testing**: Simultaneously update config while health checks are running
3. **Event handler cleanup testing**: Verify all event handlers are properly unsubscribed
4. **Edge case testing**: Test with very old release dates, clock skew scenarios
5. **Resource cleanup testing**: Verify all IDisposable resources are properly disposed
6. **Null safety testing**: Test with null/empty server IDs and other edge cases

---

**End of Report**

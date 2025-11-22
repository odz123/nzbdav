# Performance Issues Report

## Executive Summary

This report documents **15 critical performance issues** discovered during a systematic code review of the NzbDav backend. These issues primarily involve blocking async operations, inefficient locking patterns, excessive allocations, and suboptimal caching strategies that could significantly impact application performance, especially under high load.

---

## Critical Issues (Fix Immediately)

### 1. **Blocking Async Code with `.Wait()` - DEADLOCK RISK**
**Location:** `backend/Config/ConfigManager.cs:57`
**Severity:** 🔴 Critical
**Impact:** High risk of deadlocks, thread pool starvation

```csharp
public void UpdateValues(List<ConfigItem> configItems)
{
    _configLock.Wait();  // ❌ BLOCKING ASYNC SEMAPHORE
    try
    {
        foreach (var configItem in configItems)
        {
            _config[configItem.ConfigName] = configItem.ConfigValue;
        }
        // ... event invocation
    }
    finally
    {
        _configLock.Release();
    }
}
```

**Problem:**
- Blocking wait on `SemaphoreSlim` in synchronous method
- Can cause deadlocks if called from async context
- Thread pool threads are blocked unnecessarily

**Fix:** Convert method to async:
```csharp
public async Task UpdateValuesAsync(List<ConfigItem> configItems)
{
    await _configLock.WaitAsync();
    // ... rest of method
}
```

---

### 2. **Multiple Blocking `.GetAwaiter().GetResult()` Calls**
**Locations:**
- `backend/Streams/BufferToEndStream.cs:152` ⚠️
- `backend/Streams/AesDecoderStream.cs:94` ⚠️
- `backend/Streams/NzbFileStream.cs:31` ⚠️
- `backend/Streams/MultipartFileStream.cs:34` ⚠️
- `backend/Streams/DavMultipartFileStream.cs:25` ⚠️
- `backend/Streams/LimitedLengthStream.cs:13` ⚠️
- `backend/Streams/CombinedStream.cs:25` ⚠️

**Severity:** 🔴 Critical
**Impact:** Thread pool starvation, potential deadlocks

**Example from NzbFileStream.cs:**
```csharp
public override int Read(byte[] buffer, int offset, int count)
{
    return ReadAsync(buffer, offset, count).GetAwaiter().GetResult(); // ❌
}
```

**Problem:**
- Synchronous Read() methods block on async operations
- WebDAV operations may call these synchronous methods
- Each blocked call holds a thread pool thread
- Under high concurrency, can exhaust thread pool

**Context:** While some frameworks (like WebDAV libraries) may require synchronous methods, these implementations create performance bottlenecks. The codebase has 7+ instances of this anti-pattern.

---

### 3. **Disposal Blocking Async Code**
**Location:** `backend/Clients/Usenet/Connections/ConnectionPool.cs:317`
**Severity:** 🔴 Critical
**Impact:** Blocks finalizer thread, disposal delays

```csharp
public void Dispose()
{
    try
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult(); // ❌
    }
    catch
    {
        // Suppress exceptions during disposal
    }
}
```

**Problem:**
- Blocks synchronous Dispose() on async DisposeAsync()
- Can block finalizer thread if object is finalized
- May cause deadlocks in some contexts

**Fix:** Document that callers should prefer DisposeAsync(), or use a different disposal pattern

---

## High Severity Issues

### 4. **Lock Held While Copying Collections**
**Location:** `backend/Websocket/WebsocketManager.cs:33,58`
**Severity:** 🟠 High
**Impact:** Lock contention, reduced throughput

```csharp
// Line 33
lock (_lastMessage) lastMessage = _lastMessage.ToList(); // ❌

// Line 58
lock (_authenticatedSockets) authenticatedSockets = _authenticatedSockets.ToList(); // ❌
```

**Problem:**
- `.ToList()` allocates and copies entire collection inside lock
- Lock is held during memory allocation and enumeration
- Blocks other threads unnecessarily
- On large collections, this can be slow

**Fix:** Use concurrent collections or copy outside lock:
```csharp
// Option 1: Use ConcurrentDictionary/ConcurrentBag
// Option 2: Lock only the snapshot operation
lock (_lastMessage)
{
    lastMessage = new List<KeyValuePair<WebsocketTopic, string>>(_lastMessage);
}
```

---

### 5. **Excessive Task.Run() Usage**
**Locations:**
- `backend/Clients/Usenet/Connections/ConnectionPool.cs:141,172,194` (disposal cleanup)
- `backend/Streams/BufferToEndStream.cs:58` (pumping stream)
- `backend/Services/HealthCheckService.cs:136` (inside LINQ)
- `backend/Tasks/BaseTask.cs:21`

**Severity:** 🟠 High
**Impact:** Unnecessary thread pool overhead

**Example from HealthCheckService.cs:**
```csharp
var tasks = davItems.Select(davItem =>
{
    return Task.Run(async () =>  // ❌ Unnecessary Task.Run
    {
        // Each health check spawns a new Task.Run
        await PerformHealthCheck(item, itemDbClient, connectionsPerFile, cts.Token);
    }, cts.Token);
});
await Task.WhenAll(tasks);
```

**Problem:**
- `Task.Run()` queues work to thread pool
- Since `PerformHealthCheck` is already async, wrapping it in `Task.Run` is redundant
- Creates extra Task overhead and thread context switches
- The LINQ `.Select()` already creates tasks lazily

**Fix:** Remove Task.Run wrapper:
```csharp
var tasks = davItems.Select(async davItem =>
{
    await using var itemDbContext = new DavDatabaseContext();
    var itemDbClient = new DavDatabaseClient(itemDbContext);
    var item = await itemDbClient.Ctx.Items.FindAsync(new object[] { davItem.Id }, cts.Token);
    if (item != null)
        await PerformHealthCheck(item, itemDbClient, connectionsPerFile, cts.Token);
});
```

---

### 6. **Inefficient Cache Lookup with Linear Search**
**Location:** `backend/Streams/NzbFileStream.cs:91-105`
**Severity:** 🟠 High
**Impact:** O(n) lookup on every seek operation

```csharp
// BUG FIX NEW-012: Optimize cache lookup using sorted dictionary
InterpolationSearch.Result? candidateResult = null;
long candidateKey = -1;

// Find the largest key that is <= byteOffset
foreach (var (key, value) in _segmentCache)  // ❌ Linear search O(n)
{
    if (key > byteOffset)
        break; // SortedDictionary is ordered, so we can stop here

    if (value.FoundByteRange.Contains(byteOffset))
        return value; // Found exact match

    if (key > candidateKey)
    {
        candidateKey = key;
        candidateResult = value;
    }
}
```

**Problem:**
- Despite using `SortedDictionary` for efficient lookups, code uses linear search
- Iterates through every entry until finding one > byteOffset
- O(n) complexity instead of O(log n)
- Called frequently during streaming/seeking operations

**Fix:** Use `SortedDictionary` methods efficiently:
```csharp
// Use binary search capabilities of SortedDictionary
var candidates = _segmentCache
    .Where(kvp => kvp.Key <= byteOffset && kvp.Value.FoundByteRange.Contains(byteOffset))
    .FirstOrDefault();

if (candidates.Value.FoundByteRange.StartInclusive != 0)
    return candidates.Value;

// Or use reverse iteration with LINQ:
var candidate = _segmentCache.Reverse()
    .FirstOrDefault(kvp => kvp.Key <= byteOffset);
```

---

### 7. **Missing ConfigureAwait(false) Calls**
**Severity:** 🟡 Medium
**Impact:** Unnecessary context captures in library code

**Statistics:**
- Only **32 occurrences** of `ConfigureAwait` across 6 files
- Hundreds of `await` calls without it

**Affected files:**
- Most stream classes
- Most database operations
- Most client operations

**Problem:**
- Without `ConfigureAwait(false)`, async continuations capture SynchronizationContext
- In ASP.NET Core (after 3.0), this is less critical but still has overhead
- Library code should use `ConfigureAwait(false)` as best practice

**Fix:** Add `.ConfigureAwait(false)` to all library async calls:
```csharp
await connection.OpenAsync(ct).ConfigureAwait(false);
```

---

## Medium Severity Issues

### 8. **Multiple Lock Objects for Same Data**
**Location:** `backend/Config/ConfigManager.cs:13-14`
**Severity:** 🟡 Medium
**Impact:** Confusing locking strategy, potential for misuse

```csharp
private readonly SemaphoreSlim _configLock = new(1, 1);
private readonly object _syncLock = new(); // For synchronous access
```

**Problem:**
- Two different locks protecting same `_config` dictionary
- `_configLock` used in LoadConfig (async)
- `_syncLock` used in GetConfigValue (sync)
- Creates opportunity for race conditions if used incorrectly

**Better approach:** Use single locking strategy (preferably async-friendly)

---

### 9. **Potential Memory Leak - Event Handler References**
**Status:** ✅ Already Fixed (NEW-004, NEW-005)
**Locations:** Multiple services have proper cleanup now

The code properly disposes event handlers in:
- `HealthCheckService.Dispose()`
- `UsenetStreamingClient.Dispose()`
- Properly unsubscribes from events

**Good pattern observed:**
```csharp
// Store handler for cleanup
private EventHandler<ConfigEventArgs>? _configChangedHandler;

// Subscribe
_configManager.OnConfigChanged += _configChangedHandler;

// Unsubscribe in Dispose
_configManager.OnConfigChanged -= _configChangedHandler;
```

---

### 10. **Redundant Database Queries**
**Location:** `backend/Database/DavDatabaseClient.cs:89-92`
**Severity:** 🟡 Medium
**Impact:** Inefficient query pattern

```csharp
var queueItem = await Ctx.QueueItems
    .OrderByDescending(q => q.Priority)
    .ThenBy(q => q.CreatedAt)
    .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
    .Skip(0)    // ❌ Unnecessary Skip(0)
    .Take(1)
    .FirstOrDefaultAsync(ct);
```

**Problem:**
- `.Skip(0)` is redundant (does nothing)
- `.Take(1).FirstOrDefaultAsync()` is redundant - `FirstOrDefaultAsync()` already limits to 1

**Fix:**
```csharp
var queueItem = await Ctx.QueueItems
    .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
    .OrderByDescending(q => q.Priority)
    .ThenBy(q => q.CreatedAt)
    .FirstOrDefaultAsync(ct);
```

---

### 11. **Fire-and-Forget Task Exceptions**
**Locations:**
- `backend/Services/ArrMonitoringService.cs:24`
- `backend/Services/HealthCheckService.cs:68`
- `backend/Queue/QueueManager.cs:36`

**Severity:** 🟡 Medium
**Impact:** Silent failures

```csharp
_ = StartMonitoringService(); // ❌ Exceptions are swallowed
```

**Problem:**
- Fire-and-forget tasks (discarded with `_`)
- If task throws unhandled exception, it's silently lost
- No error logging or handling

**Fix:** Log unobserved exceptions:
```csharp
_ = Task.Run(async () =>
{
    try
    {
        await StartMonitoringService();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Monitoring service failed");
    }
});
```

---

## Low Severity Issues / Observations

### 12. **Large Memory Caches Without Pressure Monitoring**
**Locations:**
- `UsenetStreamingClient`: 50,000 entry cache
- `HealthCheckService`: 10,000 entry cache
- `CachingNntpClient`: 8,192 entry cache

**Observation:**
- Multiple `MemoryCache` instances with high size limits
- No memory pressure monitoring
- Could consume significant memory under load

**Recommendation:** Consider:
- Adding memory pressure callbacks
- Implementing cache eviction policies
- Monitoring cache hit rates

---

### 13. **Synchronous Lock in Async Context**
**Location:** Throughout codebase - many `lock` statements

**Observation:**
- C# `lock` statement is synchronous only
- Used in async methods means threads are held during lock
- Consider `SemaphoreSlim.WaitAsync()` for async-compatible locking

**Examples:**
- `backend/Services/HealthCheckService.cs:56,84`
- `backend/Clients/Usenet/UsenetStreamingClient.cs:138,184`

---

### 14. **Missing Indexes on Database Queries**
**Status:** ✅ Partially Addressed
**Location:** `backend/Database/DavDatabaseContext.cs:356`

Good index found:
```csharp
e.HasIndex(i => new { i.Result, i.RepairStatus, i.CreatedAt })
```

**Recommendation:** Review all query patterns and ensure indexes exist for:
- All WHERE clause columns
- All ORDER BY columns
- All foreign keys

---

### 15. **BufferToEndStream Memory Usage**
**Location:** `backend/Streams/BufferToEndStream.cs`
**Severity:** 🟢 Low (by design)
**Observation:** Informational

```csharp
// Drains the source to EOF, then disposes it.
// Even if callers never read (or dispose early) the copy continues in the background
```

**Problem:**
- Stream continues buffering even if not read
- Could consume memory buffering entire file
- No backpressure mechanism if reader is slow

**Note:** This appears intentional per the design (comment at line 11-15), but worth monitoring for large files.

---

## Performance Metrics Impact Estimates

| Issue | Thread Pool Impact | Memory Impact | Latency Impact | Scalability Impact |
|-------|-------------------|---------------|----------------|-------------------|
| #1-3 (Blocking async) | 🔴 High | 🟡 Medium | 🔴 High | 🔴 Critical |
| #4 (Lock contention) | 🟠 Medium | 🟢 Low | 🟠 Medium | 🟠 High |
| #5 (Excess Task.Run) | 🟠 Medium | 🟡 Medium | 🟡 Medium | 🟠 Medium |
| #6 (Linear search) | 🟢 Low | 🟢 Low | 🟠 High | 🟠 Medium |
| #7 (ConfigureAwait) | 🟡 Medium | 🟡 Medium | 🟡 Medium | 🟢 Low |
| #8-15 (Others) | 🟢 Low | 🟡 Medium | 🟢 Low | 🟡 Medium |

---

## Recommended Fix Priority

1. **Immediate (This Week):**
   - Fix #1: Make ConfigManager.UpdateValues async
   - Fix #2: Review all `.GetAwaiter().GetResult()` calls - determine if sync methods are required by WebDAV library
   - Fix #3: Document DisposeAsync() preference

2. **High Priority (Next Sprint):**
   - Fix #4: Refactor WebsocketManager locking
   - Fix #5: Remove unnecessary Task.Run() wrappers
   - Fix #6: Optimize NzbFileStream cache lookup
   - Fix #11: Add fire-and-forget exception handling

3. **Medium Priority (Next Month):**
   - Fix #7: Add ConfigureAwait(false) throughout
   - Fix #8: Consolidate locking in ConfigManager
   - Fix #10: Clean up redundant LINQ operations
   - Monitor #12: Cache memory usage

4. **Low Priority (Technical Debt):**
   - #13: Consider async-compatible locks
   - #14: Review database indexes
   - #15: Monitor BufferToEndStream memory usage

---

## Testing Recommendations

1. **Load Testing:**
   - Test with 50+ concurrent WebDAV streams
   - Monitor thread pool thread count
   - Watch for thread pool starvation warnings

2. **Stress Testing:**
   - Long-running streams (hours)
   - Monitor memory growth
   - Check for memory leaks

3. **Performance Benchmarks:**
   - Measure seek operation latency (issue #6)
   - Measure config read/write contention (issue #4)
   - Measure parallel health check throughput (issue #5)

---

## Conclusion

The codebase shows signs of previous bug fixes (NEW-003, NEW-004, NEW-005, NEW-008, NEW-009, NEW-012, NEW-015) which is good. However, the blocking async patterns (#1-3) are **critical performance and correctness issues** that should be addressed immediately to prevent deadlocks and improve scalability.

The good news: Most issues have straightforward fixes and the overall architecture is sound. Addressing the top 6 issues would significantly improve performance under concurrent load.

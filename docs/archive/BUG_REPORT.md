# Bug Hunt Report - NzbDav

**Date:** 2025-11-19
**Auditor:** Claude (Automated Bug Hunt)
**Scope:** Full codebase security and stability review

## Executive Summary

This report documents **22 bugs** identified during a comprehensive code review of the NzbDav application. The bugs range from critical security vulnerabilities to race conditions and resource management issues.

### Severity Breakdown
- **Critical:** 5 bugs (Security vulnerabilities, DoS potential)
- **High:** 8 bugs (Race conditions, data corruption potential)
- **Medium:** 6 bugs (Resource leaks, incorrect behavior)
- **Low:** 3 bugs (Missing best practices)

---

## Critical Severity Bugs

### BUG-001: Invalid HTTP Range Request Handling
**File:** `backend/WebDav/Base/GetAndHeadHandlerPatch.cs:112-131`
**Severity:** Critical
**Type:** Logic Error / Potential DoS

**Description:**
The code doesn't validate HTTP range requests properly. If a client sends:
- `range.Start > length - 1` (start beyond file size)
- `range.End < range.Start` (invalid range)

The calculated length becomes negative, which could cause undefined behavior or crashes.

**Code:**
```csharp
var start = range.Start ?? 0;
var end = Math.Min(range.End ?? long.MaxValue, length - 1);
length = end - start + 1;  // Can be negative!
```

**Impact:**
- Potential integer underflow
- Undefined behavior in stream reading
- Possible application crash

**Fix:**
Validate ranges before use:
```csharp
if (start > length - 1 || start < 0)
{
    response.SetStatus((DavStatusCode)416);
    return true;
}
if (end < start)
{
    response.SetStatus((DavStatusCode)416);
    return true;
}
```

---

### BUG-002: Timing Attack on Authentication
**File:** `backend/Api/Controllers/Authenticate/AuthenticateController.cs:18-22`
**Severity:** Critical
**Type:** Security - Information Disclosure

**Description:**
The authentication check uses short-circuit evaluation:
```csharp
Authenticated = account != null
    && PasswordUtil.Verify(account.PasswordHash, request.Password, account.RandomSalt)
```

If `account` is null (invalid username), password verification is skipped, creating a timing difference between:
1. Invalid username (fast - ~0.1ms)
2. Valid username with wrong password (slow - ~100ms due to bcrypt/pbkdf2)

**Impact:**
Attackers can enumerate valid usernames through timing analysis.

**Fix:**
Always perform password verification:
```csharp
var passwordHash = account?.PasswordHash ?? "dummy_hash_for_timing";
var salt = account?.RandomSalt ?? "dummy_salt";
var isValid = PasswordUtil.Verify(passwordHash, request.Password, salt);
Authenticated = account != null && isValid;
```

---

### BUG-003: DNS Rebinding / TOCTOU in SSRF Protection
**File:** `backend/Api/SabControllers/AddUrl/AddUrlRequest.cs:89-160`
**Severity:** Critical
**Type:** Security - SSRF Bypass

**Description:**
The SSRF protection checks IP addresses before making the HTTP request:
```csharp
// Line 102: Check IP
var hostAddresses = Dns.GetHostAddresses(uri.Host);
// ... validation ...

// Line 50: Make request (LATER)
var response = await HttpClient.GetAsync(url);
```

Between DNS lookup and HTTP request, an attacker can change DNS records (DNS rebinding attack):
1. First lookup: Returns public IP → passes validation
2. DNS changes to private IP
3. HTTP request: Goes to private IP → SSRF successful

**Impact:**
- Access to internal services (AWS metadata, internal APIs)
- Bypass of network security controls
- Potential credential theft

**Fix:**
Use the resolved IP address directly or implement DNS pinning:
```csharp
var safeIp = hostAddresses.First(IsIpAddressSafe);
var builder = new UriBuilder(uri) { Host = safeIp.ToString() };
var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
request.Headers.Host = uri.Host; // Preserve original host header
var response = await HttpClient.SendAsync(request);
```

---

### BUG-004: Denial of Service via Unbounded HTTP Response
**File:** `backend/Api/SabControllers/AddUrl/AddUrlRequest.cs:68`
**Severity:** Critical
**Type:** Security - DoS / Memory Exhaustion

**Description:**
The code reads the entire HTTP response into memory without size limits:
```csharp
var fileContents = await response.Content.ReadAsStringAsync();
```

An attacker can provide a URL returning gigabytes of data, causing:
- Memory exhaustion
- Application crash
- Service unavailability

**Impact:**
- Easy DoS attack vector
- OOM crashes
- Service disruption

**Fix:**
Implement response size limit:
```csharp
const long MaxNzbFileSize = 10 * 1024 * 1024; // 10MB
if (response.Content.Headers.ContentLength > MaxNzbFileSize)
    throw new Exception($"NZB file too large");

using var stream = await response.Content.ReadAsStreamAsync();
using var limitedStream = new LimitedStream(stream, MaxNzbFileSize);
var fileContents = await new StreamReader(limitedStream).ReadToEndAsync();
```

---

### BUG-005: Missing HTTP Request Timeout
**File:** `backend/Api/SabControllers/AddUrl/AddUrlRequest.cs:50`
**Severity:** Critical
**Type:** Security - DoS / Resource Exhaustion

**Description:**
The HTTP request has no timeout:
```csharp
var response = await HttpClient.GetAsync(url);
```

An attacker can provide a URL that never responds, causing threads to hang indefinitely and exhausting server resources.

**Impact:**
- Thread pool exhaustion
- Resource starvation
- Service unavailability

**Fix:**
Add timeout to HttpClient:
```csharp
private static readonly HttpClient HttpClient = new()
{
    Timeout = TimeSpan.FromSeconds(30),
    DefaultRequestHeaders = { { "User-Agent", UserAgentHeader } }
};
```

---

## High Severity Bugs

### BUG-006: Race Condition in ServerHealthTracker
**File:** `backend/Clients/Usenet/ServerHealthTracker.cs:17-66`
**Severity:** High
**Type:** Concurrency - Race Condition

**Description:**
While `ConcurrentDictionary` provides thread-safe dictionary access, the `ServerHealth` objects stored in it are modified without locking:

```csharp
// IsServerAvailable - lines 28-29
health.IsCircuitOpen = false;  // Race condition!
health.ConsecutiveFailures = 0;

// RecordSuccess - lines 44-47
health.ConsecutiveFailures = 0;  // Race condition!
health.TotalSuccesses++;  // Race condition!

// RecordFailure - lines 56-64
health.ConsecutiveFailures++;  // Race condition!
health.TotalFailures++;  // Race condition!
```

**Impact:**
- Incorrect failure counting
- Circuit breaker may fail to open/close correctly
- Servers may be incorrectly marked as healthy/unhealthy

**Fix:**
Add locking to ServerHealth or use atomic operations:
```csharp
private class ServerHealth
{
    private readonly object _lock = new();

    public void IncrementFailures()
    {
        lock (_lock)
        {
            ConsecutiveFailures++;
            TotalFailures++;
            LastFailureTime = DateTime.UtcNow;
        }
    }
}
```

---

### BUG-007: Race Condition in MultiServerNntpClient.UpdateServersAsync
**File:** `backend/Clients/Usenet/MultiServerNntpClient.cs:269-291`
**Severity:** High
**Type:** Concurrency - Race Condition

**Description:**
The `UpdateServersAsync` method uses `_updateLock`, but `ExecuteWithFailover` (line 161) doesn't acquire this lock when iterating `_servers`:

```csharp
// UpdateServersAsync
await _updateLock.WaitAsync();
_servers.Clear();  // Modifies list

// ExecuteWithFailover (no lock!)
var availableServers = _servers.Where(...).ToList();  // Reads list
```

If `UpdateServersAsync` is called while `ExecuteWithFailover` is running, the list could be modified during enumeration.

**Impact:**
- `InvalidOperationException: Collection was modified`
- Potential crash during server operations
- Service disruption

**Fix:**
Use `ReaderWriterLockSlim` or snapshot the list:
```csharp
private async Task<T> ExecuteWithFailover<T>(...)
{
    List<ServerInstance> serversSnapshot;
    await _updateLock.WaitAsync();
    try
    {
        serversSnapshot = _servers.ToList();
    }
    finally
    {
        _updateLock.Release();
    }

    var availableServers = serversSnapshot.Where(...).ToList();
    // ... rest of method
}
```

---

### BUG-008: Race Condition in ConfigManager.LoadConfig
**File:** `backend/Config/ConfigManager.cs:15-27`
**Severity:** High
**Type:** Concurrency - Race Condition

**Description:**
`LoadConfig` reads from database without a lock, then acquires lock to update `_config`:

```csharp
// Line 18: No lock while reading database
var configItems = await dbContext.ConfigItems.ToListAsync();

// Line 19: Lock acquired after reading
lock (_config)
{
    _config.Clear();
    // ...
}
```

If two threads call `LoadConfig` concurrently or if `GetConfigValue` is called during load, race conditions occur.

**Impact:**
- Stale configuration data
- Inconsistent state
- Potential `KeyNotFoundException`

**Fix:**
Acquire lock before database read or use async locking:
```csharp
private readonly SemaphoreSlim _configLock = new(1, 1);

public async Task LoadConfig()
{
    await _configLock.WaitAsync();
    try
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync();
        _config.Clear();
        foreach (var configItem in configItems)
        {
            _config[configItem.ConfigName] = configItem.ConfigValue;
        }
    }
    finally
    {
        _configLock.Release();
    }
}
```

---

### BUG-009: Missing Error Handling for bool.Parse
**File:** `backend/Config/ConfigManager.cs` (Multiple locations)
**Severity:** High
**Type:** Error Handling - Crash Potential

**Description:**
Multiple methods use `bool.Parse()` without try-catch:
- Line 128: `IsEnsureImportableVideoEnabled`
- Line 135: `ShowHiddenWebdavFiles`
- Line 154: `IsEnforceReadonlyWebdavEnabled`
- Line 161: `IsEnsureArticleExistenceEnabled`
- Line 168: `IsPreviewPar2FilesEnabled`
- Line 175: `IsIgnoreSabHistoryLimitEnabled`
- Line 189: `IsRepairJobEnabled`

If config values are corrupted (e.g., "yes", "1", "true1"), `FormatException` crashes the application.

**Impact:**
- Application crash on corrupted config
- Service unavailability
- No graceful degradation

**Fix:**
Use `bool.TryParse`:
```csharp
public bool IsEnsureImportableVideoEnabled()
{
    var defaultValue = true;
    var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
    return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
}
```

---

### BUG-010: Missing Error Handling for int.Parse
**File:** `backend/Config/ConfigManager.cs:274`
**Severity:** High
**Type:** Error Handling - Crash Potential

**Description:**
```csharp
Port = int.Parse(GetConfigValue("usenet.port") ?? "119")
```

If `GetConfigValue("usenet.port")` returns non-numeric string, `FormatException` crashes the application.

**Impact:**
- Application crash on invalid config
- Service unavailability

**Fix:**
```csharp
Port = int.TryParse(GetConfigValue("usenet.port"), out var port) ? port : 119
```

---

### BUG-011: Race Condition in NzbFileStream Dispose
**File:** `backend/Streams/NzbFileStream.cs:131-144`
**Severity:** High
**Type:** Concurrency - Double Dispose

**Description:**
Both `Dispose(bool disposing)` and `DisposeAsync()` check `_disposed` without synchronization:

```csharp
protected override void Dispose(bool disposing)
{
    if (_disposed) return;  // Race condition!
    _innerStream?.Dispose();
    _disposed = true;
}

public override async ValueTask DisposeAsync()
{
    if (_disposed) return;  // Race condition!
    if (_innerStream != null) await _innerStream.DisposeAsync();
    _disposed = true;
}
```

**Impact:**
- Double disposal of `_innerStream`
- `ObjectDisposedException`
- Undefined behavior

**Fix:**
Use `Interlocked.CompareExchange`:
```csharp
private int _disposed = 0;

protected override void Dispose(bool disposing)
{
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1) return;
    _innerStream?.Dispose();
}
```

---

### BUG-012: Race Condition in CombinedStream Dispose
**File:** `backend/Streams/CombinedStream.cs:107-123`
**Severity:** High
**Type:** Concurrency - Double Dispose

**Description:**
Same issue as BUG-011 - both disposal methods check `_isDisposed` without synchronization.

**Impact:**
- Double disposal of streams
- `ObjectDisposedException`

**Fix:**
Same as BUG-011 - use atomic operations.

---

### BUG-013: Unhandled Exception in UpdateServersAsync
**File:** `backend/Clients/Usenet/MultiServerNntpClient.cs:280-283`
**Severity:** High
**Type:** Error Handling - State Corruption

**Description:**
If `Dispose()` throws an exception while disposing old connection pools, it's unhandled:

```csharp
foreach (var oldServer in oldServers)
{
    oldServer.Client.Dispose();  // Could throw!
}
```

This could leave the system in an inconsistent state with new servers configured but old ones not disposed.

**Impact:**
- Resource leaks
- Inconsistent state
- Connection pool exhaustion

**Fix:**
```csharp
foreach (var oldServer in oldServers)
{
    try
    {
        oldServer.Client.Dispose();
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to dispose server {Name}", oldServer.Config.Name);
    }
}
```

---

## Medium Severity Bugs

### BUG-014: Missing base.Dispose in NzbFileStream
**File:** `backend/Streams/NzbFileStream.cs:131-136`
**Severity:** Medium
**Type:** Resource Management

**Description:**
The `Dispose(bool disposing)` method doesn't call `base.Dispose(disposing)`, violating the dispose pattern.

**Impact:**
- Potential resource leaks
- Base class cleanup not executed

**Fix:**
```csharp
protected override void Dispose(bool disposing)
{
    if (_disposed) return;
    if (disposing)
    {
        _innerStream?.Dispose();
    }
    _disposed = true;
    base.Dispose(disposing);  // Add this!
}
```

---

### BUG-015: Missing base.Dispose in CombinedStream
**File:** `backend/Streams/CombinedStream.cs:107-114`
**Severity:** Medium
**Type:** Resource Management

**Description:**
Same issue as BUG-014.

**Fix:**
Same as BUG-014.

---

### BUG-016: Position Tracking Bug in CombinedStream.DiscardBytesAsync
**File:** `backend/Streams/CombinedStream.cs:59-85`
**Severity:** Medium
**Type:** Logic Error

**Description:**
Line 79 increments position by the requested count, not the actual bytes discarded:

```csharp
while (remaining > 0)
{
    var toRead = (int)Math.Min(remaining, throwaway.Length);
    var read = await ReadAsync(throwaway, 0, toRead);
    remaining -= read;
    if (read == 0) break;  // EOF reached!
}

_position += count;  // Wrong! Should be (count - remaining)
```

If EOF is reached before discarding all bytes, `_position` becomes incorrect.

**Impact:**
- Incorrect stream position tracking
- Potential read from wrong offset
- Data corruption in streaming

**Fix:**
```csharp
var actuallyDiscarded = count - remaining;
_position += actuallyDiscarded;
```

---

### BUG-017: No Bounds Checking in NzbFileStream.Seek
**File:** `backend/Streams/NzbFileStream.cs:41-51`
**Severity:** Medium
**Type:** Error Handling

**Description:**
The `Seek` method doesn't validate that the resulting position is within bounds (0 to Length):

```csharp
public override long Seek(long offset, SeekOrigin origin)
{
    var absoluteOffset = origin == SeekOrigin.Begin ? offset
        : origin == SeekOrigin.Current ? _position + offset
        : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
    // No bounds check!
    _position = absoluteOffset;
    // ...
}
```

**Impact:**
- Seeking to negative position
- Seeking beyond file length
- Undefined behavior on subsequent reads

**Fix:**
```csharp
if (absoluteOffset < 0 || absoluteOffset > Length)
    throw new ArgumentOutOfRangeException(nameof(offset), "Seek position out of bounds");
```

---

### BUG-018: SeekOrigin.End Not Supported
**File:** `backend/Streams/NzbFileStream.cs:41-51`
**Severity:** Medium
**Type:** Feature Gap / Potential Bug

**Description:**
`SeekOrigin.End` is not supported, but this is commonly used (e.g., seeking to last 100 bytes).

**Impact:**
- Limited seek functionality
- Incompatibility with some media players
- Potential crashes if callers expect full Stream support

**Fix:**
```csharp
var absoluteOffset = origin == SeekOrigin.Begin ? offset
    : origin == SeekOrigin.Current ? _position + offset
    : origin == SeekOrigin.End ? Length + offset
    : throw new ArgumentException("Invalid SeekOrigin", nameof(origin));
```

---

### BUG-019: Potential Database Context Lifetime Issue
**File:** `backend/Queue/QueueManager.cs:73-102`
**Severity:** Medium
**Type:** Resource Management

**Description:**
The database context is created with `await using` on line 73 but the `dbClient` is passed to `BeginProcessingQueueItem` which starts an async task. If the task spawns background work that continues after the await on line 92, the context could be disposed while still in use.

**Impact:**
- Potential `ObjectDisposedException`
- Database operation failures
- Data corruption

**Recommendation:**
Review `QueueItemProcessor.ProcessAsync()` to ensure it doesn't spawn background tasks. If it does, the database context lifetime needs to be reconsidered.

---

## Low Severity Bugs

### BUG-020: Inconsistent Exception Logging
**File:** `backend/Queue/QueueManager.cs:96`
**Severity:** Low
**Type:** Logging / Observability

**Description:**
Error logging uses string interpolation with just the message:
```csharp
Log.Error($"An unexpected error occured while processing the queue: {e.Message}");
```

This loses the stack trace and inner exceptions.

**Impact:**
- Difficult debugging
- Lost diagnostic information

**Fix:**
```csharp
Log.Error(e, "An unexpected error occurred while processing the queue");
```

---

### BUG-021: Typo in Error Message
**File:** `backend/Queue/QueueManager.cs:96`
**Severity:** Low
**Type:** Quality

**Description:**
"occured" should be "occurred"

**Fix:**
```csharp
Log.Error(e, "An unexpected error occurred while processing the queue");
```

---

### BUG-022: Static HttpClient Without Timeout
**File:** `backend/Api/SabControllers/AddUrl/AddUrlRequest.cs:15-18`
**Severity:** Low
**Type:** Configuration

**Description:**
The static `HttpClient` doesn't configure timeout (covered by BUG-005) or other important settings like max response content buffer size.

**Impact:**
- Resource exhaustion (covered by other bugs)
- Suboptimal performance

**Fix:**
```csharp
private static readonly HttpClient HttpClient = new()
{
    Timeout = TimeSpan.FromSeconds(30),
    MaxResponseContentBufferSize = 10 * 1024 * 1024, // 10MB
    DefaultRequestHeaders = { { "User-Agent", UserAgentHeader } }
};
```

---

## Summary Statistics

| Category | Count |
|----------|-------|
| Security Vulnerabilities | 5 |
| Race Conditions | 5 |
| Error Handling Issues | 3 |
| Resource Management | 4 |
| Logic Errors | 3 |
| Code Quality | 2 |
| **Total** | **22** |

## Priority Recommendations

1. **Immediate Action Required:**
   - BUG-001: Fix HTTP range validation (prevents crashes)
   - BUG-002: Fix timing attack (security)
   - BUG-003: Fix DNS rebinding (security)
   - BUG-004: Add response size limit (prevents DoS)
   - BUG-005: Add HTTP timeout (prevents DoS)

2. **High Priority:**
   - BUG-006 through BUG-013: Fix all race conditions and error handling

3. **Medium Priority:**
   - BUG-014 through BUG-019: Fix resource management and logic errors

4. **Low Priority:**
   - BUG-020 through BUG-022: Quality improvements

## Testing Recommendations

1. Add unit tests for HTTP range request handling with edge cases
2. Add concurrency tests for ServerHealthTracker
3. Add integration tests for SSRF protection
4. Add stress tests for HTTP client with malicious URLs
5. Add fuzz testing for configuration parsing

---

**End of Report**

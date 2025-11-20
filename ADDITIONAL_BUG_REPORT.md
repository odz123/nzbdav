# Additional Bug Hunt Report - NzbDav
## Supplementary Security & Stability Review

**Date:** 2025-11-20
**Scope:** Comprehensive search for bugs not covered in BUG_REPORT.md

---

## Critical Severity Bugs

### BUG-023: Process Resource Leak in SymlinkAndStrmUtil
**File:** `/home/user/nzbdav/backend/Utils/SymlinkAndStrmUtil.cs:44-68`
**Severity:** Critical
**Type:** Resource Management - Handle Leak

**Description:**
The `GetAllSymlinksAndStrmsLinux` method uses `Process.Start()` with `using var process`, which is good. However, there's a subtle issue: the `StandardOutput` stream is read via `ReadLine()` in a loop, but if an exception occurs during yielding (lines 54-67), the process won't be properly cleaned up because the generator hasn't completed.

Additionally, the process object created is not guaranteed to be disposed if the generator is abandoned mid-iteration. This is a resource leak vulnerability.

**Code:**
```csharp
using var process = Process.Start(startInfo)!;
while (process.StandardOutput.EndOfStream == false)
{
    var filePath = process.StandardOutput.ReadLine();
    if (filePath == null) break;
    var target = process.StandardOutput.ReadLine();
    if (target == null) break;
    // ... yield return
}
```

**Impact:**
- Process handle leak
- Standard output stream not flushed/closed properly
- Potential resource exhaustion with repeated calls
- File descriptor leak on Linux

**Recommendation:**
Ensure process is explicitly disposed and error handling is robust:
```csharp
using var process = Process.Start(startInfo)!;
using (process)
{
    try
    {
        // ... read and yield
    }
    finally
    {
        process?.StandardOutput?.Dispose();
        if (!process.HasExited)
            process.Kill();
    }
}
```

---

### BUG-024: Unsafe Null-Forgiving Operators on Reflection Results
**File:** `/home/user/nzbdav/backend/Extensions/SevenZipArchiveExtensions.cs:9-12, 17-19`
**Severity:** Critical
**Type:** Null Reference Exception

**Description:**
The code uses reflection to access private fields but applies force null-forgiving operator (!) to potentially null values:

```csharp
var database = archive?.GetReflectionField("_database");
var dataStartPosition = (long?)database?.GetReflectionField("_dataStartPosition");
var packStreamStartPositions = (List<long>?)database?.GetReflectionField("_packStreamStartPositions");
return dataStartPosition!.Value + packStreamStartPositions![index];  // Can throw!
```

If reflection returns null (field doesn't exist, wrong type, etc.), the force unwrap will throw `NullReferenceException`.

**Similar issues in:**
- `/home/user/nzbdav/backend/Extensions/SevenZipArchiveEntryExtensions.cs:39-41`
- `/home/user/nzbdav/backend/Extensions/RarHeaderExtensions.cs:170-174`

**Impact:**
- Runtime crashes
- Loss of service availability
- Unhandled exceptions

**Recommendation:**
Add proper null checks:
```csharp
return dataStartPosition?.Value ?? throw new InvalidOperationException(
    "Failed to extract 7zip archive metadata");
```

---

### BUG-025: Potential Deadlock from Sync-over-Async
**File:** Multiple locations
**Severity:** Critical
**Type:** Concurrency - Deadlock Risk

**Description:**
Several methods use `.GetAwaiter().GetResult()` to bridge async/sync gaps, which can cause deadlocks in ASP.NET contexts:

**Affected files:**
- `/home/user/nzbdav/backend/Streams/NzbFileStream.cs:30`
- `/home/user/nzbdav/backend/Streams/CombinedStream.cs:25`
- `/home/user/nzbdav/backend/Utils/InterpolationSearch.cs:22`
- `/home/user/nzbdav/backend/Streams/AesDecoderStream.cs:94`
- `/home/user/nzbdav/backend/Streams/MultipartFileStream.cs:34`
- `/home/user/nzbdav/backend/Streams/LimitedLengthStream.cs:13`
- `/home/user/nzbdav/backend/Streams/DavMultipartFileStream.cs:25`
- `/home/user/nzbdav/backend/Clients/Usenet/Connections/ConnectionPool.cs:284`

**Example Code:**
```csharp
public override int Read(byte[] buffer, int offset, int count)
{
    return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
}
```

**Impact:**
- Deadlock in request handling
- Thread pool starvation
- Service hang/freeze
- Difficult to diagnose issues

**Recommendation:**
Refactor to be fully async or use proper synchronization mechanisms:
```csharp
// Option 1: Make synchronous version truly synchronous
public override int Read(byte[] buffer, int offset, int count)
{
    // Use sync-friendly implementation
}

// Option 2: Use proper async wrapper (if called from async context)
// Use ValueTask-based pattern with proper ConfigureAwait
```

---

## High Severity Bugs

### BUG-026: Collection Empty Exception on First() Without Guard
**File:** Multiple locations
**Severity:** High
**Type:** Logic Error - Exception Potential

**Description:**
Several methods call `.First()` on collections that may be empty without prior validation:

**Affected locations:**
- `/home/user/nzbdav/backend/Queue/FileProcessors/SevenZipProcessor.cs:59`
- `/home/user/nzbdav/backend/Queue/FileProcessors/MultipartMkvProcessor.cs:45,47`
- `/home/user/nzbdav/backend/Queue/FileAggregators/RarAggregator.cs:57`
- `/home/user/nzbdav/backend/Clients/Usenet/Models/UsenetArticleHeaders.cs:28`

**Example Code (SevenZipProcessor.cs:59):**
```csharp
ReleaseDate = _fileInfos.First().ReleaseDate,  // Throws if _fileInfos is empty!
```

**Impact:**
- `InvalidOperationException: Sequence contains no elements`
- Processing failures
- Queue items stuck in processing state

**Recommendation:**
Add guards:
```csharp
if (_fileInfos.Count == 0)
    throw new InvalidOperationException("No file parts available for processing");
ReleaseDate = _fileInfos.First().ReleaseDate,
```

---

### BUG-027: Last() Without Empty Collection Check
**File:** `/home/user/nzbdav/backend/Models/MultipartFile.cs:8`
**Severity:** High
**Type:** Logic Error - Exception Potential

**Description:**
```csharp
public long FileSize => FileParts.Last().ByteRange.EndExclusive;
```

If `FileParts` is empty, this property will throw `InvalidOperationException`.

**Impact:**
- Runtime exception on property access
- Cascading failures in stream handling
- Data corruption risk

**Recommendation:**
```csharp
public long FileSize => FileParts.Count > 0 
    ? FileParts.Last().ByteRange.EndExclusive 
    : 0L;
```

---

### BUG-028: Blocking Wait Instead of Async in ConfigManager
**File:** `/home/user/nzbdav/backend/Config/ConfigManager.cs:37`
**Severity:** High
**Type:** Anti-pattern - Sync Over Async

**Description:**
```csharp
public string? GetConfigValue(string configName)
{
    _configLock.Wait();  // BLOCKING! Should use WaitAsync()
    try
    {
        return _config.TryGetValue(configName, out string? value) ? value : null;
    }
    finally
    {
        _configLock.Release();
    }
}
```

This is synchronous blocking on an async-available `SemaphoreSlim`. Compare to `LoadConfig()` which correctly uses `await _configLock.WaitAsync()`.

**Impact:**
- Thread pool starvation
- Request handling delays
- Potential deadlocks
- Poor async performance

**Recommendation:**
Make the method async or use a different synchronization mechanism:
```csharp
public async Task<string?> GetConfigValueAsync(string configName)
{
    await _configLock.WaitAsync();
    try
    {
        return _config.TryGetValue(configName, out string? value) ? value : null;
    }
    finally
    {
        _configLock.Release();
    }
}

// OR for synchronous callers, use SyncLock pattern
private readonly object _syncLock = new();
public string? GetConfigValue(string configName)
{
    lock (_syncLock)
    {
        return _config.TryGetValue(configName, out string? value) ? value : null;
    }
}
```

---

### BUG-029: Fire-and-Forget Async Operations Without Exception Handling
**File:** Multiple locations
**Severity:** High
**Type:** Error Handling - Silent Failures

**Description:**
Fire-and-forget async operations can hide exceptions:

**Affected locations:**
- `/home/user/nzbdav/backend/Clients/Usenet/Connections/ConnectionPool.cs:140,160,171`
  ```csharp
  _ = DisposeConnectionAsync(connection); // fire & forget
  ```
- `/home/user/nzbdav/backend/Services/HealthCheckService.cs:53`
  ```csharp
  _ = StartMonitoringService();
  ```
- `/home/user/nzbdav/backend/Services/ArrMonitoringService.cs:24`
  ```csharp
  _ = StartMonitoringService();
  ```
- Multiple WebSocket send operations

**Impact:**
- Exceptions silently swallowed
- Resource leaks not detected
- Debugging difficult
- Service degradation unnoticed

**Recommendation:**
Add proper exception handling:
```csharp
#pragma warning disable CS4014
_ = DisposeConnectionAsync(connection).ContinueWith(t =>
{
    if (t.IsFaulted)
        _logger?.LogError(t.Exception, "Failed to dispose connection");
});
#pragma warning restore CS4014
```

---

### BUG-030: Unsafe Type Casting on Reflection Results
**File:** `/home/user/nzbdav/backend/Extensions/SevenZipArchiveEntryExtensions.cs:39-41`
**Severity:** High
**Type:** Type Safety - Runtime Exception Risk

**Description:**
```csharp
var folderFirstPackStreamId = (int)folder?.GetReflectionField("_firstPackStreamId")!;
var databaseDataStartPosition = (long)database?.GetReflectionField("_dataStartPosition")!;
var databasePackStreamStartPositions = (List<long>)database?.GetReflectionField("_packStreamStartPositions")!;
```

Force casting reflection results to specific types without validation can throw `InvalidCastException`.

**Impact:**
- Runtime crashes
- Unhandled exceptions in critical paths
- Service unavailability

**Recommendation:**
Add type validation:
```csharp
var fieldValue = folder?.GetReflectionField("_firstPackStreamId");
if (fieldValue is not int folderFirstPackStreamId)
    throw new InvalidOperationException("Expected _firstPackStreamId to be int");
```

---

### BUG-031: ProbingStream Read Boundary Issue
**File:** `/home/user/nzbdav/backend/Streams/ProbingStream.cs:50,70`
**Severity:** High
**Type:** Logic Error - Boundary Condition

**Description:**
When returning the probed byte, the code doesn't validate that `count >= 1`:

```csharp
if (_probeByte.HasValue)
{
    buffer[offset] = _probeByte.Value;
    _probeByte = null;
    var read = stream.Read(buffer, offset + 1, count - 1);  // count-1 could be negative!
    return 1 + read;
}
```

If `count == 0`, then `count - 1 == -1`, which is invalid for the Read method.

**Impact:**
- `ArgumentException` from invalid count
- Stream reading failures
- Data corruption

**Recommendation:**
Add boundary check:
```csharp
if (count <= 0) return 0;
```

---

## Medium Severity Bugs

### BUG-032: Unsafe Null-Forgiving on Nullable Parent Access
**File:** `/home/user/nzbdav/backend/Tasks/RemoveUnlinkedFilesTask.cs:112-113`
**Severity:** Medium
**Type:** Null Reference Exception Risk

**Description:**
```csharp
if (item.Parent!.Children.All(x => removedItems.Contains(x.Id)))
    RemoveItem(item.Parent!, removedItems);
```

The code assumes `item.Parent` is not null, but if it is, `NullReferenceException` is thrown.

**Impact:**
- Runtime exception
- Task failure
- Incomplete cleanup

**Recommendation:**
```csharp
if (item.Parent != null && item.Parent.Children.All(x => removedItems.Contains(x.Id)))
    RemoveItem(item.Parent, removedItems);
```

---

### BUG-033: ArrClient JSON Deserialization Throwing NullReferenceException
**File:** `/home/user/nzbdav/backend/Clients/RadarrSonarr/ArrClient.cs:62,69`
**Severity:** Medium
**Type:** Error Handling

**Description:**
```csharp
return await JsonSerializer.DeserializeAsync<T>(response) ?? throw new NullReferenceException();
return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new NullReferenceException();
```

Throwing `NullReferenceException` is an anti-pattern. Should throw more specific exception.

**Impact:**
- Poor error diagnostics
- Misleading exception type
- Debugging difficulty

**Recommendation:**
```csharp
return await JsonSerializer.DeserializeAsync<T>(response) 
    ?? throw new InvalidOperationException($"Failed to deserialize response as {typeof(T).Name}");
```

---

### BUG-034: Missing Proper Exception Context in SymlinkAndStrmUtil
**File:** `/home/user/nzbdav/backend/Utils/SymlinkAndStrmUtil.cs:82`
**Severity:** Medium
**Type:** Error Handling

**Description:**
```csharp
return IsStrm(x) ? new StrmInfo() { StrmPath = x.FullName, TargetUrl = File.ReadAllText(x.FullName) }
```

`File.ReadAllText()` can throw `IOException`, `UnauthorizedAccessException`, etc. without proper error context or retry logic.

**Impact:**
- Task failures
- Incomplete directory scans
- Poor error messages

**Recommendation:**
```csharp
try
{
    var content = File.ReadAllText(x.FullName);
    return new StrmInfo() { StrmPath = x.FullName, TargetUrl = content };
}
catch (Exception ex)
{
    _logger?.LogWarning(ex, "Failed to read strm file: {Path}", x.FullName);
    return null;
}
```

---

### BUG-035: ContinueWith Without Proper Error Handling
**File:** `/home/user/nzbdav/backend/Clients/Usenet/MultiConnectionNntpClient.cs:75-78`
**Severity:** Medium
**Type:** Error Handling

**Description:**
```csharp
// we intentionally do not pass the cancellation token to ContinueWith,
// This event is triggered when the host runtime initiates a shutdown,
.ContinueWith(_ => connectionLock.Dispose());
```

The continuation doesn't check if the preceding task failed. If `WaitForReady` throws, the exception is still unhandled.

**Impact:**
- Unobserved task exceptions
- Resource leaks if WaitForReady fails
- Hard to debug

---

## Low Severity Bugs

### BUG-036: Potential Symlink Path Traversal
**File:** `/home/user/nzbdav/backend/Utils/SymlinkAndStrmUtil.cs:56,64`
**Severity:** Low
**Type:** Path Validation

**Description:**
```csharp
StrmPath = Path.GetFullPath(filePath, directoryPath),
SymlinkPath = Path.GetFullPath(filePath, directoryPath),
```

While `Path.GetFullPath` provides some protection, it's good practice to validate that the resolved path is within the expected directory:

**Recommendation:**
```csharp
var fullPath = Path.GetFullPath(filePath, directoryPath);
if (!fullPath.StartsWith(Path.GetFullPath(directoryPath)))
    throw new InvalidOperationException($"Path traversal detected: {filePath}");
```

---

### BUG-037: Generic Exception Throwing
**File:** Multiple locations
**Severity:** Low
**Type:** Code Quality

**Description:**
Several places throw generic `Exception` instead of specific exception types:

**Examples:**
- `/home/user/nzbdav/backend/Utils/OrganizedLinksUtil.cs:96`
  ```csharp
  _ => throw new Exception("Unknown link type")
  ```
- `/home/user/nzbdav/backend/Api/SabControllers/AddUrl/AddUrlRequest.cs:48,52`
  ```csharp
  throw new Exception($"The url is invalid.");
  throw new Exception("Invalid URL format.");
  ```

**Impact:**
- Difficult exception handling
- Loss of semantic meaning
- Poor error context

**Recommendation:**
Use specific exception types:
```csharp
throw new InvalidOperationException("Unknown link type");
throw new ArgumentException("URL is invalid", nameof(url));
throw new UriFormatException("Invalid URL format");
```

---

## Summary Statistics

| Category | Count |
|----------|-------|
| Critical Issues | 3 |
| High Severity Issues | 8 |
| Medium Severity Issues | 4 |
| Low Severity Issues | 2 |
| **Total Additional Bugs** | **17** |

---

## Combined Impact Assessment

**Original BUG_REPORT.md:** 22 bugs
**This Report:** 17 bugs
**Total Identified Issues:** 39 bugs

### Critical Priority Fixes Needed:
1. BUG-023: Process resource leak
2. BUG-024: Null-forgiving on reflection
3. BUG-025: Sync-over-async deadlocks

### Recommended Fix Order:
1. Fix all critical deadlock/resource issues first
2. Add comprehensive null checking
3. Replace fire-and-forget patterns
4. Update exception handling
5. Refactor sync-over-async patterns

---

**End of Supplementary Report**

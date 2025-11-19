# Fix: Empty Hostname Error After Adding Second Usenet Server

## Problem
After adding a second Usenet server, the application crashed with the error:
```
[04:28:47 ERR] File `/content/Movies/Playdate.2025.2160p.MULTI.WEB-DL.SDR.H265-AOC/gLQHlEfce1umw6Dkc6xVk248ugrcbsWp.mkv` could not be read due to unhandled System.ArgumentException: Empty string not allowed (Parameter 'hostname')
```

## Root Cause
When a second Usenet server was added through the configuration interface with an empty or missing `Host` field, the application:
1. Saved the invalid configuration to the database
2. Attempted to create NNTP connections using the empty hostname
3. The underlying NNTP library threw `ArgumentException` for empty hostname parameter

## Solution
Implemented three layers of validation to prevent this issue:

### 1. Config Loading Validation (`ConfigManager.cs`)
**File**: `/backend/Config/ConfigManager.cs`

Added validation in `GetUsenetServers()` method to filter out servers with invalid configuration:
```csharp
// Filter out servers with invalid configuration (empty host, etc.)
return servers
    .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Host))
    .ToList();
```

**Effect**: Prevents servers with empty hostnames from being loaded into the application, even if they exist in the database.

### 2. Server Instance Creation Validation (`MultiServerNntpClient.cs`)
**File**: `/backend/Clients/Usenet/MultiServerNntpClient.cs`

Added validation in `CreateServerInstance()` method:
```csharp
// Validate required fields
if (string.IsNullOrWhiteSpace(config.Host))
    throw new InvalidOperationException(
        $"Server '{config.Name}' (ID: {config.Id}) has an empty hostname. Please configure a valid hostname.");
```

**Effect**: Provides a clear, descriptive error message if a server with an empty hostname somehow makes it past the first validation layer.

### 3. API Save Validation (`UpdateConfigController.cs`)
**File**: `/backend/Api/Controllers/UpdateConfig/UpdateConfigController.cs`

Added validation before saving configuration to database:
```csharp
// Validate usenet.servers configuration if present
var usenetServersConfig = request.ConfigItems.FirstOrDefault(x => x.ConfigName == "usenet.servers");
if (usenetServersConfig != null && !string.IsNullOrWhiteSpace(usenetServersConfig.ConfigValue))
{
    var servers = System.Text.Json.JsonSerializer.Deserialize<List<...>>(usenetServersConfig.ConfigValue);
    if (servers != null)
    {
        var invalidServers = servers
            .Where(s => s.Enabled && string.IsNullOrWhiteSpace(s.Host))
            .ToList();
        
        if (invalidServers.Any())
        {
            throw new BadHttpRequestException(
                $"The following enabled servers have invalid configuration (missing hostname): {serverNames}. " +
                "Please provide a valid hostname for all enabled servers.");
        }
    }
}
```

**Effect**: Prevents invalid server configurations from being saved to the database in the first place.

## Testing Recommendations

### Test Case 1: Adding Server with Empty Host
1. Navigate to Usenet server configuration
2. Try to add/save a server with an empty hostname field
3. **Expected**: API should return a 400 Bad Request with descriptive error message
4. **Expected**: Configuration should NOT be saved

### Test Case 2: Loading Existing Invalid Configuration
1. If you have existing invalid server configurations in the database
2. Restart the application
3. **Expected**: Invalid servers should be silently filtered out
4. **Expected**: Application should continue to work with valid servers only

### Test Case 3: Normal Operation
1. Add a second server with all required fields filled in (hostname, port, credentials)
2. **Expected**: Server should be added successfully
3. **Expected**: Multi-server failover should work correctly

## Files Modified
1. `/backend/Config/ConfigManager.cs` - Line 247: Added hostname validation filter
2. `/backend/Clients/Usenet/MultiServerNntpClient.cs` - Line 56-59: Added validation check
3. `/backend/Api/Controllers/UpdateConfig/UpdateConfigController.cs` - Line 15-41: Added pre-save validation

## Migration Path
If you have existing invalid configurations in your database:
- They will automatically be filtered out on next application restart
- You don't need to manually clean up the database
- However, you may want to review and update/remove invalid entries through the UI

## Related Documentation
- See `MULTI_SERVER_SETUP.md` for proper multi-server configuration
- All enabled servers MUST have a valid hostname configured

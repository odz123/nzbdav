using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// HealthCheckService is a singleton service that monitors file health and performs repairs.
/// IMPORTANT: As a singleton, this service must NOT inject DavDatabaseContext (which is scoped).
/// Instead, create new context instances per operation using 'await using var dbContext = new DavDatabaseContext()'
/// This pattern prevents disposed context exceptions and ensures proper database connection management.
/// </summary>
public class HealthCheckService : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly CancellationToken _cancellationToken = SigtermUtil.GetCancellationToken();

    // Cache for missing segment IDs with 24 hour TTL to allow recovery from temporary issues
    private IMemoryCache _missingSegmentCache;
    private readonly object _missingSegmentCacheLock = new object();

    // Store event handler for proper cleanup
    private EventHandler<ConfigManager.ConfigEventArgs>? _configChangedHandler;
    private int _disposed = 0;

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;

        // Initialize missing segment cache with 10,000 entry limit
        _missingSegmentCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });

        // Store event handler for proper cleanup
        _configChangedHandler = (_, configEventArgs) =>
        {
            // when any usenet server configuration changes, clear the missing segments cache
            // this includes adding/removing/modifying servers in multi-server setup
            if (!_configManager.HasUsenetConfigChanged(configEventArgs.ChangedConfig)) return;

            // BUG FIX NEW-004: Add locking to prevent race conditions on cache replacement
            lock (_missingSegmentCacheLock)
            {
                // Clear cache by disposing old cache and creating a new one
                // Compact() only removes expired entries, not all entries
                var oldCache = _missingSegmentCache;
                _missingSegmentCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
                oldCache.Dispose();
            }
        };

        _configManager.OnConfigChanged += _configChangedHandler;

        // PERF FIX #11: Add error handling to fire-and-forget task
        _ = Task.Run(async () =>
        {
            try
            {
                await StartMonitoringService();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Fatal(ex, "HealthCheckService failed unexpectedly - service has stopped");
            }
        });
    }

    /// <summary>
    /// Dispose of resources and unsubscribe from events
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
            return;

        // Unsubscribe from config changed event to prevent memory leak
        if (_configManager != null && _configChangedHandler != null)
            _configManager.OnConfigChanged -= _configChangedHandler;

        // Dispose the missing segment cache
        lock (_missingSegmentCacheLock)
        {
            _missingSegmentCache?.Dispose();
        }
    }

    private async Task StartMonitoringService()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken);
                    continue;
                }

                // set reserved-connections context
                // Use total connections across all servers for multi-server compatibility
                var maxRepairConnections = _configManager.GetMaxRepairConnections();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
                var reservedConnections = _configManager.GetTotalMaxConnections() - maxRepairConnections;
                using var _ = cts.Token.SetScopedContext(new ReservedConnectionsContext(reservedConnections));

                // get multiple davItems to health-check in parallel
                var cycleStartTime = System.Diagnostics.Stopwatch.StartNew();
                var parallelCount = _configManager.GetParallelHealthCheckCount();
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var currentDateTime = DateTimeOffset.UtcNow;
                var davItems = await GetHealthCheckQueueItems(dbClient)
                    .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
                    .Take(parallelCount)
                    .ToListAsync(cts.Token);

                // if there are no items to health-check, don't do anything
                if (davItems.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    continue;
                }

                // perform health checks in parallel
                var connectionsPerFile = Math.Max(1, maxRepairConnections / davItems.Count);
                Log.Information("Starting parallel health check: {FileCount} files, {ConnectionsPerFile} connections each",
                    davItems.Count, connectionsPerFile);

                // PERF FIX #5: Remove unnecessary Task.Run - async lambda already creates a task
                var tasks = davItems.Select(async davItem =>
                {
                    var fileStartTime = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        // Each file gets its own database context to avoid concurrency issues
                        await using var itemDbContext = new DavDatabaseContext();
                        var itemDbClient = new DavDatabaseClient(itemDbContext);
                        // Re-fetch the item to avoid tracking conflicts
                        var item = await itemDbClient.Ctx.Items.FindAsync(new object[] { davItem.Id }, cts.Token);
                        if (item != null)
                        {
                            await PerformHealthCheck(item, itemDbClient, connectionsPerFile, cts.Token);
                            fileStartTime.Stop();
                            Log.Information("File health check completed: {FileName} ({FileType}) in {ElapsedMs}ms",
                                item.Name, item.Type, fileStartTime.ElapsedMilliseconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        fileStartTime.Stop();
                        Log.Warning("File health check failed: {FileName} in {ElapsedMs}ms - {Error}",
                            davItem.Name, fileStartTime.ElapsedMilliseconds, ex.Message);
                        throw;
                    }
                });

                await Task.WhenAll(tasks);
                cycleStartTime.Stop();
                // BUG FIX NEW-009: Add defensive check for division by zero
                var avgPerFile = davItems.Count > 0 ? cycleStartTime.ElapsedMilliseconds / davItems.Count : 0;
                Log.Information("Parallel health check cycle completed: {FileCount} files in {ElapsedMs}ms ({AvgPerFile}ms/file)",
                    davItems.Count, cycleStartTime.ElapsedMilliseconds, avgPerFile);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error performing background health checks: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken);
            }
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        var actionNeeded = HealthCheckResult.RepairAction.ActionNeeded;
        var healthCheckResults = dbClient.Ctx.HealthCheckResults;

        // Filter out items that have ActionNeeded repair status
        // EF Core translates .Any() to an efficient NOT EXISTS subquery
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.NzbFile
                        || x.Type == DavItem.ItemType.RarFile
                        || x.Type == DavItem.ItemType.MultipartFile)
            .Where(x => !healthCheckResults.Any(h => h.DavItemId == x.Id && h.RepairStatus == actionNeeded));
    }

    private async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct
    )
    {
        try
        {
            // update the release date, if null
            var segments = await GetAllSegments(davItem, dbClient, ct);
            if (davItem.ReleaseDate == null) await UpdateReleaseDate(davItem, segments, ct);


            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check with sampling
            var samplingRate = GetSamplingRateForFile(davItem);
            var minSegments = _configManager.GetMinHealthCheckSegments();
            var fileAge = davItem.ReleaseDate != null
                ? (DateTimeOffset.UtcNow - davItem.ReleaseDate.Value).Days
                : (int?)null;

            var serverConfigs = _usenetClient.GetServerConfigs();
            var serverCount = serverConfigs.Count;
            var serverNames = string.Join(", ", serverConfigs.Select(s => s.Name));

            Log.Information("Health check starting: {FileName} - {SegmentCount} segments, {SamplingRate:P0} sampling rate, {FileAgeDays} days old, {ServerCount} servers: [{ServerNames}]",
                davItem.Name, segments.Count, samplingRate, fileAge, serverCount, serverNames);

            var progress = progressHook.ToPercentage(segments.Count);
            await _usenetClient.CheckAllSegmentsAsync(segments, concurrency, samplingRate, minSegments, progress, ct);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // BUG FIX NEW-010: Fix NextHealthCheck calculation to prevent overflow and handle edge cases
            davItem.LastHealthCheck = DateTimeOffset.UtcNow;

            if (davItem.ReleaseDate != null && davItem.LastHealthCheck != null)
            {
                var age = davItem.LastHealthCheck.Value - davItem.ReleaseDate.Value;

                // Cap the next check interval to a reasonable maximum (1 year)
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
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Healthy,
                RepairStatus = HealthCheckResult.RepairAction.None,
                Message = "File is healthy."
            }));
            await dbClient.Ctx.SaveChangesAsync(ct);
        }
        catch (UsenetArticleNotFoundException e)
        {
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
            {
                // PERF FIX #17: Remove redundant lock - MemoryCache.Set is already thread-safe
                // Cache missing segment for 24 hours to prevent repeated failed downloads
                _missingSegmentCache.Set(e.SegmentId, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
                    Size = 1
                });
            }

            // when usenet article is missing, perform repairs
            await Repair(davItem, dbClient, ct);
        }
    }

    private async Task UpdateReleaseDate(DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = StringUtil.EmptyToNull(segments.FirstOrDefault());
        if (firstSegmentId == null) return;
        var articleHeaders = await _usenetClient.GetArticleHeadersAsync(firstSegmentId, ct);
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.Type == DavItem.ItemType.NzbFile)
        {
            var nzbFile = await dbClient.GetNzbFileAsync(davItem.Id, ct);
            return nzbFile?.SegmentIds?.ToList() ?? [];
        }

        if (davItem.Type == DavItem.ItemType.RarFile)
        {
            var rarFile = await dbClient.Ctx.RarFiles
                .Where(x => x.Id == davItem.Id)
                .FirstOrDefaultAsync(ct);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.Type == DavItem.ItemType.MultipartFile)
        {
            var multipartFile = await dbClient.Ctx.MultipartFiles
                .Where(x => x.Id == davItem.Id)
                .FirstOrDefaultAsync(ct);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        try
        {
            // if the file extension has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blacklistedExtensions = _configManager.GetBlacklistedExtensions();
            if (blacklistedExtensions.Contains(Path.GetExtension(davItem.Name).ToLower()))
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "File extension is marked in settings as ignored (unwanted) file type.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Could not find corresponding symlink or strm-file within Library Dir.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                var rootFolders = await arrClient.GetRootFolders();
                if (!rootFolders.Any(x => symlinkOrStrmPath.StartsWith(x.Path!))) continue;

                // if we found a corresponding arr instance,
                // then remove and search.
                if (await arrClient.RemoveAndSearch(symlinkOrStrmPath))
                {
                    dbClient.Ctx.Items.Remove(davItem);
                    dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                    {
                        Id = Guid.NewGuid(),
                        DavItemId = davItem.Id,
                        Path = davItem.Path,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Result = HealthCheckResult.HealthResult.Unhealthy,
                        RepairStatus = HealthCheckResult.RepairAction.Repaired,
                        Message = string.Join(" ", [
                            "File had missing articles.",
                            $"Corresponding {linkType} found within Library Dir.",
                            "Triggered new Arr search."
                        ])
                    }));
                    await dbClient.Ctx.SaveChangesAsync(ct);
                    return;
                }

                // if we could not find corresponding media-item to remove-and-search
                // within the found arr instance, then break out of this loop so that
                // we can fall back to the behavior below of deleting both the link-file
                // and the dav-item.
                break;
            }

            // if we could not find a corresponding arr instance
            // then we can delete both the item and the link-file.
            PathUtil.SafeDeleteFile(symlinkOrStrmPath);
            dbClient.Ctx.Items.Remove(davItem);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.Deleted,
                Message = string.Join(" ", [
                    "File had missing articles.",
                    $"Corresponding {linkType} found within Library Dir.",
                    "Could not find corresponding Radarr/Sonarr media-item to trigger a new search.",
                    $"Deleted the webdav-file and {linkType}."
                ])
            }));
            await dbClient.Ctx.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = null;
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
            }));
            await dbClient.Ctx.SaveChangesAsync(ct);
        }
    }

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    public void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        // PERF FIX #17: Remove redundant lock - MemoryCache.TryGetValue is already thread-safe
        // Lock is only needed for cache replacement (see constructor and Dispose)
        foreach (var segmentId in segmentIds)
        {
            if (_missingSegmentCache.TryGetValue(segmentId, out _))
            {
                throw new UsenetArticleNotFoundException(segmentId);
            }
        }
    }

    private double GetSamplingRateForFile(DavItem davItem)
    {
        var baseSamplingRate = _configManager.GetHealthCheckSamplingRate();

        // If adaptive sampling is disabled, use base rate
        if (!_configManager.IsAdaptiveSamplingEnabled())
            return baseSamplingRate;

        // Calculate file age
        var age = DateTimeOffset.UtcNow - (davItem.ReleaseDate ?? DateTimeOffset.UtcNow);

        // Adjust sampling rate based on age (newer files = higher sampling rate)
        return age.TotalDays switch
        {
            < 30 => Math.Min(1.0, baseSamplingRate * 2.0),   // New files: double the rate (max 100%)
            < 180 => baseSamplingRate,                        // Medium age: use configured rate
            < 365 => Math.Max(0.05, baseSamplingRate * 0.67), // Older files: reduce to 67%
            _ => Math.Max(0.05, baseSamplingRate * 0.33)      // Very old: reduce to 33% (min 5%)
        };
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx)
{
    public DavDatabaseContext Ctx => ctx;

    // OPTIMIZATION: Cache directory sizes to avoid expensive recursive CTE queries
    // Cache entries expire after 5 minutes and are limited to 10,000 entries
    private static readonly MemoryCache DirectorySizeCache = new(new MemoryCacheOptions
    {
        SizeLimit = 10000,
        ExpirationScanFrequency = TimeSpan.FromMinutes(1)
    });

    private static readonly MemoryCacheEntryOptions CacheOptions = new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        Size = 1
    };

    // file
    public async Task<DavItem?> GetFileById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return null;
        return await ctx.Items.AsNoTracking().Where(i => i.Id == guid).FirstOrDefaultAsync();
    }

    public Task<List<DavItem>> GetFilesByIdPrefix(string prefix)
    {
        return ctx.Items
            .AsNoTracking()
            .Where(i => i.IdPrefix == prefix)
            .Where(i => i.Type == DavItem.ItemType.NzbFile
                        || i.Type == DavItem.ItemType.RarFile
                        || i.Type == DavItem.ItemType.MultipartFile)
            .ToListAsync();
    }

    // directory
    public Task<List<DavItem>> GetDirectoryChildrenAsync(Guid dirId, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking().Where(x => x.ParentId == dirId).ToListAsync(ct);
    }

    public Task<DavItem?> GetDirectoryChildAsync(Guid dirId, string childName, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking().FirstOrDefaultAsync(x => x.ParentId == dirId && x.Name == childName, ct);
    }

    public async Task<long> GetRecursiveSize(Guid dirId, CancellationToken ct = default)
    {
        // OPTIMIZATION: Check cache first to avoid expensive recursive query
        var cacheKey = $"DirSize_{dirId}";
        if (DirectorySizeCache.TryGetValue<long>(cacheKey, out var cachedSize))
        {
            return cachedSize;
        }

        if (dirId == DavItem.Root.Id)
        {
            var rootSize = await Ctx.Items.AsNoTracking().SumAsync(x => x.FileSize, ct) ?? 0;
            // Cache root directory size
            DirectorySizeCache.Set(cacheKey, rootSize, CacheOptions);
            return rootSize;
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

        // HIGH-1 FIX: Add retry logic for transient database errors
        const int maxRetries = 3;
        var retryDelays = new[]
        {
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
                var size = Convert.ToInt64(result);

                // OPTIMIZATION: Cache the result for future queries
                DirectorySizeCache.Set(cacheKey, size, CacheOptions);

                return size;
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

    // nzbfile
    public async Task<DavNzbFile?> GetNzbFileAsync(Guid id, CancellationToken ct = default)
    {
        return await ctx.NzbFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    // queue
    public async Task<(QueueItem? queueItem, QueueNzbContents? queueNzbContents)> GetTopQueueItem
    (
        CancellationToken ct = default
    )
    {
        var nowTime = DateTime.UtcNow;
        // PERF FIX #10: Remove redundant Skip(0) and Take(1) - FirstOrDefaultAsync already limits to 1
        // Also moved Where before OrderBy for better query performance
        var queueItem = await Ctx.QueueItems
            .AsNoTracking()
            .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var queueNzbContents = queueItem != null
            ? await Ctx.QueueNzbContents.AsNoTracking().FirstOrDefaultAsync(q => q.Id == queueItem.Id, ct)
            : null;
        return (queueItem, queueNzbContents);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        var queueItems = category != null
            ? Ctx.QueueItems.AsNoTracking().Where(q => q.Category == category)
            : Ctx.QueueItems.AsNoTracking();
        return queueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Skip(start)
            .Take(limit)
            .ToArrayAsync(cancellationToken: ct);
    }

    public Task<int> GetQueueItemsCount(string? category, CancellationToken ct = default)
    {
        var queueItems = category != null
            ? Ctx.QueueItems.AsNoTracking().Where(q => q.Category == category)
            : Ctx.QueueItems.AsNoTracking();
        return queueItems.CountAsync(cancellationToken: ct);
    }

    public async Task RemoveQueueItemsAsync(List<Guid> ids, CancellationToken ct = default)
    {
        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct);
    }

    // history
    public async Task<HistoryItem?> GetHistoryItemAsync(string id)
    {
        return await Ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.Parse(id));
    }

    public async Task RemoveHistoryItemsAsync(List<Guid> ids, bool deleteFiles, CancellationToken ct = default)
    {
        if (deleteFiles)
        {
            // OPTIMIZATION: Pre-fetch download directory IDs to avoid nested subquery
            // Old query had O(n²) complexity with nested WHERE/Contains
            var downloadDirIds = await Ctx.HistoryItems
                .AsNoTracking()
                .Where(h => ids.Contains(h.Id) && h.DownloadDirId != null)
                .Select(h => h.DownloadDirId!.Value)
                .ToListAsync(ct);

            // Use simple IN clause instead of nested subquery - much faster
            if (downloadDirIds.Count > 0)
            {
                await Ctx.Items
                    .Where(d => downloadDirIds.Contains(d.Id))
                    .ExecuteDeleteAsync(ct);
            }
        }

        await Ctx.HistoryItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct);
    }

    private class FileSizeResult
    {
        public long TotalSize { get; init; }
    }

    // health check
    public async Task<List<HealthCheckStat>> GetHealthCheckStatsAsync
    (
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default
    )
    {
        return await Ctx.HealthCheckStats
            .AsNoTracking()
            .Where(h => h.DateStartInclusive >= from && h.DateStartInclusive <= to)
            .GroupBy(h => new { h.Result, h.RepairStatus })
            .Select(g => new HealthCheckStat
            {
                Result = g.Key.Result,
                RepairStatus = g.Key.RepairStatus,
                Count = g.Select(r => r.Count).Sum(),
            })
            .ToListAsync(ct);
    }

    // completed-symlinks
    public async Task<List<DavItem>> GetCompletedSymlinkCategoryChildren(string category,
        CancellationToken ct = default)
    {
        var query = from historyItem in Ctx.HistoryItems.AsNoTracking()
            where historyItem.Category == category
                  && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                  && historyItem.DownloadDirId != null
            join davItem in Ctx.Items.AsNoTracking() on historyItem.DownloadDirId equals davItem.Id
            where davItem.Type == DavItem.ItemType.Directory
            select davItem;
        return await query.Distinct().ToListAsync(ct);
    }
}
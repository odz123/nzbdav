using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;
using Usenet.Nntp.Responses;
using Usenet.Nzb;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : IDisposable
{
    private readonly INntpClient _client;
    private readonly WebsocketManager _websocketManager;
    private readonly ServerHealthTracker _healthTracker;
    private readonly ConfigManager _configManager;
    private readonly ILogger<MultiServerNntpClient>? _logger;
    private MultiServerNntpClient? _multiServerClient;
    private IMemoryCache _healthySegmentCache;
    private readonly object _segmentCacheLock = new object();

    // Store event handlers for proper cleanup
    private EventHandler<MultiServerNntpClient.AggregateConnectionPoolChangedEventArgs>? _poolChangedHandler;
    private EventHandler<ServerUnavailableEventArgs>? _serverUnavailableHandler;
    private EventHandler? _healthResetHandler;
    private EventHandler<ConfigChangedEventArgs>? _configChangedHandler;
    private int _disposed = 0;

    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ServerHealthTracker healthTracker,
        ILogger<MultiServerNntpClient>? logger = null)
    {
        // initialize private members
        _websocketManager = websocketManager;
        _healthTracker = healthTracker;
        _configManager = configManager;
        _logger = logger;

        // initialize healthy segment cache (24 hour TTL, max 50,000 entries)
        _healthySegmentCache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 50000 });

        // get server configurations
        var serverConfigs = configManager.GetUsenetServers();

        // initialize the multi-server client
        _multiServerClient = new MultiServerNntpClient(serverConfigs, _healthTracker, _logger);

        // Create and store event handlers for proper cleanup
        _poolChangedHandler = OnConnectionPoolChanged;
        _serverUnavailableHandler = OnServerUnavailable;
        _healthResetHandler = OnAllServersHealthReset;
        _configChangedHandler = async (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configManager.HasUsenetConfigChanged(configEventArgs.ChangedConfig))
                return;

            try
            {
                // update server configurations
                // Note: UpdateServersAsync will call ResetAllServerHealth which fires
                // OnAllServersHealthReset event, which will clear the segment cache
                var newServerConfigs = configManager.GetUsenetServers();

                // Validate that we have at least one server before updating
                if (newServerConfigs.Count == 0)
                {
                    Serilog.Log.Error("Server configuration update returned zero servers - keeping existing servers to prevent breaking streams");
                    return;
                }

                if (_multiServerClient != null)
                {
                    await _multiServerClient.UpdateServersAsync(newServerConfigs);
                    // No need to call SetupConnectionPoolMonitoring - the event is already subscribed
                    // and InitializeServers in MultiServerNntpClient will fire the initial event
                }
            }
            catch (Exception ex)
            {
                // BUG FIX: If server config update fails, log but don't break existing connections
                // This prevents streams from breaking when config has temporary issues
                Serilog.Log.Error(ex,
                    "Failed to update server configurations - keeping existing servers to prevent breaking streams. " +
                    "Please check your Usenet server configuration.");
            }
        };

        // Subscribe to aggregate connection pool events from multi-server client
        _multiServerClient.OnAggregateConnectionPoolChanged += _poolChangedHandler;

        // Subscribe to server unavailable events to invalidate segment cache
        // BUG FIX #2: Clear segment cache when circuit breaker opens
        _healthTracker.OnServerUnavailable += _serverUnavailableHandler;

        // Subscribe to health reset events to invalidate segment cache
        // BUG FIX #3: Clear segment cache when all health is reset
        _healthTracker.OnAllServersHealthReset += _healthResetHandler;

        // when config changes, update the servers
        configManager.OnConfigChanged += _configChangedHandler;

        // wrap with caching
        var cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 8192 });
        _client = new CachingNntpClient(_multiServerClient, cache);
    }

    /// <summary>
    /// Dispose of resources and unsubscribe from events
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
            return;

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
        }
    }

    /// <summary>
    /// Handle connection pool changes and send websocket updates
    /// </summary>
    private void OnConnectionPoolChanged(object? sender, MultiServerNntpClient.AggregateConnectionPoolChangedEventArgs args)
    {
        var message = $"{args.Live}|{args.Max}|{args.Idle}";
        _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);
    }

    /// <summary>
    /// Handle server becoming unavailable and clear segment cache
    /// BUG FIX #2: When a circuit breaker opens, invalidate the segment cache
    /// because cached segments might only exist on the now-unavailable server
    /// </summary>
    private void OnServerUnavailable(object? sender, ServerUnavailableEventArgs args)
    {
        Serilog.Log.Warning(
            "Server {ServerId} circuit breaker opened - clearing segment cache to ensure accurate health checks",
            args.ServerId);

        ClearSegmentCache();
    }

    /// <summary>
    /// Handle all servers health being reset and clear segment cache
    /// BUG FIX #3: When health tracking is reset, invalidate the segment cache
    /// to ensure fresh health checks after server reconfiguration
    /// </summary>
    private void OnAllServersHealthReset(object? sender, EventArgs args)
    {
        Serilog.Log.Information("All server health reset - clearing segment cache");
        ClearSegmentCache();
    }

    /// <summary>
    /// Clear the segment cache by disposing and recreating it
    /// BUG FIX NEW-005: Add locking to prevent race conditions on cache replacement
    /// </summary>
    private void ClearSegmentCache()
    {
        lock (_segmentCacheLock)
        {
            // Dispose old cache and create new one to ensure ALL entries are cleared
            var oldCache = _healthySegmentCache;
            _healthySegmentCache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 50000 });
            oldCache.Dispose();
        }
    }

    public Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        return CheckAllSegmentsAsync(segmentIds, concurrency, 1.0, 10, progress, cancellationToken);
    }

    public async Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        double samplingRate,
        int minSegments,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        using var childCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var _ = childCt.Token.SetScopedContext(cancellationToken.GetContext<object>());
        var token = childCt.Token;

        var segmentList = segmentIds.ToList();
        var totalSegments = segmentList.Count;

        // Apply sampling if rate < 1.0
        var segmentsToCheck = samplingRate >= 1.0
            ? segmentList
            : GetStrategicSample(segmentList, samplingRate, minSegments);

        // Filter out segments that are cached as healthy (if cache is enabled)
        var isCacheEnabled = _configManager.IsHealthySegmentCacheEnabled();
        var segmentsToActuallyCheck = isCacheEnabled
            ? segmentsToCheck.Where(s => !IsSegmentCachedAsHealthy(s)).ToList()
            : segmentsToCheck;

        var sampledSegments = segmentsToCheck.Count;
        var cacheHits = sampledSegments - segmentsToActuallyCheck.Count;

        // If all segments are cached as healthy, we're done
        if (segmentsToActuallyCheck.Count == 0)
        {
            startTime.Stop();
            Serilog.Log.Information(
                "Health check completed (all cached): {TotalSegments} segments, {SampledSegments} sampled, {CacheHits} cache hits, {ElapsedMs}ms",
                totalSegments, sampledSegments, cacheHits, startTime.ElapsedMilliseconds);
            return;
        }

        var tasks = segmentsToActuallyCheck
            .Select(async x => await CheckSegmentWithRetryAsync(x, token))
            .WithConcurrencyAsync(concurrency);

        var processed = 0;
        var consecutiveFailures = 0;
        var totalChecked = segmentsToActuallyCheck.Count;

        var newCacheEntries = 0;
        await foreach (var task in tasks)
        {
            progress?.Report(++processed);

            if (!task.IsSuccess)
            {
                consecutiveFailures++;

                // Early termination: if we find 3+ consecutive missing segments, file is unhealthy
                if (consecutiveFailures >= 3)
                {
                    startTime.Stop();
                    Serilog.Log.Warning(
                        "Health check failed (early termination): {TotalSegments} segments, {SampledSegments} sampled ({SamplingRate:P0}), {CacheHits} cache hits, {CheckedSegments} checked, {ElapsedMs}ms - 3+ consecutive failures detected",
                        totalSegments, sampledSegments, samplingRate, cacheHits, processed, startTime.ElapsedMilliseconds);
                    await childCt.CancelAsync();
                    throw new UsenetArticleNotFoundException(task.SegmentId);
                }
            }
            else
            {
                consecutiveFailures = 0;

                // Cache successful segment checks (if cache is enabled)
                if (isCacheEnabled)
                {
                    CacheHealthySegment(task.SegmentId);
                    newCacheEntries++;
                }
            }

            // If any single segment fails (and we haven't hit consecutive threshold), still fail
            if (!task.IsSuccess && consecutiveFailures < 3)
            {
                startTime.Stop();
                Serilog.Log.Warning(
                    "Health check failed: {TotalSegments} segments, {SampledSegments} sampled ({SamplingRate:P0}), {CacheHits} cache hits, {CheckedSegments}/{TotalToCheck} checked, {ElapsedMs}ms - segment not found",
                    totalSegments, sampledSegments, samplingRate, cacheHits, processed, totalChecked, startTime.ElapsedMilliseconds);
                await childCt.CancelAsync();
                throw new UsenetArticleNotFoundException(task.SegmentId);
            }
        }

        startTime.Stop();
        var samplingPercentage = totalSegments > 0 ? (double)sampledSegments / totalSegments : 1.0;
        var cacheEfficiency = sampledSegments > 0 ? (double)cacheHits / sampledSegments : 0.0;

        // Log server health stats to help debug multi-server issues
        var serverHealthStats = GetServerHealthStats();
        if (serverHealthStats.Count > 1)
        {
            // BUG FIX NEW-006: Add null safety for ServerId
            var healthSummary = string.Join(", ", serverHealthStats.Select(s =>
            {
                var serverId = s.ServerId ?? "unknown";
                var shortId = serverId.Length > 8 ? serverId.Substring(0, 8) : serverId;
                return $"{shortId}: {(s.IsAvailable ? "OK" : "UNAVAILABLE")} ({s.TotalSuccesses}✓/{s.TotalFailures}✗)";
            }));
            Serilog.Log.Information(
                "Health check completed: {TotalSegments} segments, {SampledSegments} sampled ({SamplingPercentage:P0}), {CacheHits} cache hits ({CacheEfficiency:P0}), {CheckedSegments} network checks, {NewCacheEntries} new cache entries, {ElapsedMs}ms | Server health: [{HealthSummary}]",
                totalSegments, sampledSegments, samplingPercentage, cacheHits, cacheEfficiency, segmentsToActuallyCheck.Count, newCacheEntries, startTime.ElapsedMilliseconds, healthSummary);
        }
        else
        {
            Serilog.Log.Information(
                "Health check completed: {TotalSegments} segments, {SampledSegments} sampled ({SamplingPercentage:P0}), {CacheHits} cache hits ({CacheEfficiency:P0}), {CheckedSegments} network checks, {NewCacheEntries} new cache entries, {ElapsedMs}ms",
                totalSegments, sampledSegments, samplingPercentage, cacheHits, cacheEfficiency, segmentsToActuallyCheck.Count, newCacheEntries, startTime.ElapsedMilliseconds);
        }
    }

    private static List<string> GetStrategicSample(List<string> segments, double samplingRate, int minSegments)
    {
        var segmentCount = segments.Count;
        var sampleSize = Math.Max(minSegments, (int)(segmentCount * samplingRate));

        // If sample size >= total segments, return all segments
        if (sampleSize >= segmentCount)
            return segments;

        var sample = new HashSet<string>();

        // Always check first 3 and last 3 segments (most likely to be missing)
        var edgeCount = Math.Min(3, segmentCount / 2);
        foreach (var segment in segments.Take(edgeCount))
            sample.Add(segment);
        foreach (var segment in segments.TakeLast(edgeCount))
            sample.Add(segment);

        // Fill remaining quota with random samples from middle
        var remaining = sampleSize - sample.Count;
        if (remaining > 0)
        {
            var startIdx = edgeCount;
            var endIdx = segmentCount - edgeCount;
            var middleSegments = segments.Skip(startIdx).Take(endIdx - startIdx).ToList();

            if (middleSegments.Count > 0)
            {
                var random = new Random();
                var randomSample = middleSegments
                    .OrderBy(_ => random.Next())
                    .Take(remaining);

                foreach (var segment in randomSample)
                    sample.Add(segment);
            }
        }

        // Return in original order to maintain sequential checking benefits
        return segments.Where(s => sample.Contains(s)).ToList();
    }

    private async Task<(string SegmentId, bool IsSuccess)> CheckSegmentWithRetryAsync(
        string segmentId,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var retryDelays = new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) };

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var result = await _client.StatAsync(segmentId, cancellationToken);

                // Only these response codes indicate the article is actually missing
                if (result.ResponseType == NntpStatResponseType.NoArticleWithThatNumber ||
                    result.ResponseType == NntpStatResponseType.NoArticleWithThatMessageId)
                {
                    return (segmentId, false);
                }

                // Article exists
                if (result.ResponseType == NntpStatResponseType.ArticleExists)
                {
                    return (segmentId, true);
                }

                // Protocol/state errors (412, 420) - retry if we have attempts left
                if (result.ResponseType == NntpStatResponseType.NoGroupSelected ||
                    result.ResponseType == NntpStatResponseType.CurrentArticleInvalid)
                {
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelays[attempt], cancellationToken);
                        continue;
                    }
                    // Treat as success after retries to avoid false positives
                    return (segmentId, true);
                }

                // Unknown response - log and retry
                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelays[attempt], cancellationToken);
                    continue;
                }

                // After all retries, assume success to avoid false positives
                return (segmentId, true);
            }
            catch (UsenetArticleNotFoundException)
            {
                // Article definitively not found - don't retry, fail immediately
                return (segmentId, false);
            }
            catch (Exception) when (attempt < maxRetries)
            {
                // Retry on transient network errors
                await Task.Delay(retryDelays[attempt], cancellationToken);
            }
            catch (Exception)
            {
                // After all retries exhausted with exceptions (connection/auth errors),
                // assume success to avoid false positives and misleading login errors.
                // The multi-server failover in ExecuteWithFailover will handle real issues.
                return (segmentId, true);
            }
        }

        // After all retries exhausted with exceptions, assume success to avoid false positives
        return (segmentId, true);
    }

    public async Task<NzbFileStream> GetFileStream(NzbFile nzbFile, int concurrentConnections, CancellationToken ct)
    {
        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await _client.GetFileSizeAsync(nzbFile, cancellationToken: ct);
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections);
    }

    public NzbFileStream GetFileStream(NzbFile nzbFile, long fileSize, int concurrentConnections)
    {
        return new NzbFileStream(nzbFile.GetSegmentIds(), fileSize, _client, concurrentConnections);
    }

    public NzbFileStream GetFileStream(string[] segmentIds, long fileSize, int concurrentConnections)
    {
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
    {
        return _client.GetSegmentStreamAsync(segmentId, includeHeaders, ct);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return _client.GetFileSizeAsync(file, cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return _client.GetArticleHeadersAsync(segmentId, cancellationToken);
    }

    /// <summary>
    /// Get health statistics for all configured servers
    /// </summary>
    public List<ServerHealthStats> GetServerHealthStats()
    {
        return _multiServerClient?.GetServerHealthStats() ?? new List<ServerHealthStats>();
    }

    /// <summary>
    /// Get current server configurations
    /// </summary>
    public IReadOnlyList<UsenetServerConfig> GetServerConfigs()
    {
        return _multiServerClient?.GetServerConfigs() ?? Array.Empty<UsenetServerConfig>();
    }

    // PERF FIX #17: Remove redundant locks - MemoryCache is already thread-safe for TryGetValue/Set
    // Locks are only needed for cache replacement operations (see ClearSegmentCache)
    private bool IsSegmentCachedAsHealthy(string segmentId)
    {
        return _healthySegmentCache.TryGetValue(segmentId, out _);
    }

    private void CacheHealthySegment(string segmentId)
    {
        var cacheTtl = _configManager.GetHealthySegmentCacheTtl();
        _healthySegmentCache.Set(segmentId, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = cacheTtl,
            Size = 1 // Each entry counts as 1 toward the size limit
        });
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        string host,
        int port,
        bool useSsl,
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        var connection = new ThreadSafeNntpClient();
        if (!await connection.ConnectAsync(host, port, useSsl, cancellationToken))
            throw new CouldNotConnectToUsenetException("Could not connect to usenet host. Check connection settings.");
        if (!await connection.AuthenticateAsync(user, pass, cancellationToken))
            throw new CouldNotLoginToUsenetException("Could not login to usenet host. Check username and password.");
        return connection;
    }
}
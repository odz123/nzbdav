using Microsoft.Extensions.Logging;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using Usenet.Exceptions;
using Usenet.Nntp.Responses;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// NNTP client that supports multiple servers with automatic failover
/// </summary>
public class MultiServerNntpClient : INntpClient
{
    private readonly List<ServerInstance> _servers = new();
    private readonly ServerHealthTracker _healthTracker;
    private readonly ILogger<MultiServerNntpClient>? _logger;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly Dictionary<string, ServerConnectionStats> _serverStats = new();
    private readonly object _statsLock = new();

    /// <summary>
    /// Event fired when the aggregate connection pool stats change across all servers
    /// </summary>
    public event EventHandler<AggregateConnectionPoolChangedEventArgs>? OnAggregateConnectionPoolChanged;

    public MultiServerNntpClient(
        IEnumerable<UsenetServerConfig> serverConfigs,
        ServerHealthTracker healthTracker,
        ILogger<MultiServerNntpClient>? logger = null)
    {
        _healthTracker = healthTracker;
        _logger = logger;
        InitializeServers(serverConfigs);
    }

    private void InitializeServers(IEnumerable<UsenetServerConfig> serverConfigs)
    {
        var configs = serverConfigs
            .Where(s => s.Enabled)
            .OrderBy(s => s.Priority)
            .ThenBy(s => s.Name)
            .ToList();

        if (configs.Count == 0)
        {
            _logger?.LogWarning("No enabled Usenet servers are configured. Usenet functionality will be unavailable until servers are configured.");
            return;
        }

        foreach (var config in configs)
        {
            var serverInstance = CreateServerInstance(config);
            _servers.Add(serverInstance);

            // Initialize stats for this server
            lock (_statsLock)
            {
                _serverStats[config.Id] = new ServerConnectionStats
                {
                    Live = 0,
                    Idle = 0,
                    Max = config.MaxConnections
                };
            }

            _logger?.LogInformation(
                "Initialized server: {Name} ({Host}:{Port}) with {Connections} connections at priority {Priority}",
                config.Name, config.Host, config.Port, config.MaxConnections, config.Priority);
        }

        // Fire initial aggregate event with all zeros except max
        lock (_statsLock)
        {
            var totalMax = _serverStats.Values.Sum(s => s.Max);
            OnAggregateConnectionPoolChanged?.Invoke(this, new AggregateConnectionPoolChangedEventArgs(
                0, 0, totalMax));
        }
    }

    private ServerInstance CreateServerInstance(UsenetServerConfig config)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new InvalidOperationException(
                $"Server '{config.Name}' (ID: {config.Id}) has an empty hostname. Please configure a valid hostname.");

        var createConnection = (CancellationToken ct) =>
            UsenetStreamingClient.CreateNewConnection(
                config.Host, config.Port, config.UseSsl,
                config.Username, config.Password, ct);

        var connectionPool = new ConnectionPool<INntpClient>(config.MaxConnections, createConnection);

        // Subscribe to connection pool events to aggregate stats across all servers
        connectionPool.OnConnectionPoolChanged += OnServerConnectionPoolChanged;

        var multiConnectionClient = new MultiConnectionNntpClient(connectionPool);

        return new ServerInstance
        {
            Config = config,
            Client = multiConnectionClient,
            ConnectionPool = connectionPool
        };
    }

    /// <summary>
    /// Handle connection pool changes from individual servers and fire aggregate event
    /// </summary>
    private void OnServerConnectionPoolChanged(object? sender, ConnectionPool<INntpClient>.ConnectionPoolChangedEventArgs args)
    {
        // Find which server this event came from
        ServerInstance? sourceServer = null;
        lock (_servers)
        {
            sourceServer = _servers.FirstOrDefault(s => ReferenceEquals(s.ConnectionPool, sender));
        }

        if (sourceServer == null)
            return; // Event from a disposed server

        // Update the stats for this specific server
        lock (_statsLock)
        {
            _serverStats[sourceServer.Config.Id] = new ServerConnectionStats
            {
                Live = args.Live,
                Idle = args.Idle,
                Max = args.Max
            };

            // Calculate aggregate stats across all servers
            var totalLive = _serverStats.Values.Sum(s => s.Live);
            var totalIdle = _serverStats.Values.Sum(s => s.Idle);
            var totalMax = _serverStats.Values.Sum(s => s.Max);

            // Fire aggregate event
            OnAggregateConnectionPoolChanged?.Invoke(this, new AggregateConnectionPoolChangedEventArgs(
                totalLive, totalIdle, totalMax));
        }
    }

    private class ServerConnectionStats
    {
        public int Live { get; init; }
        public int Idle { get; init; }
        public int Max { get; init; }
    }

    public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Connection is handled per-server in MultiServerNntpClient");
    }

    public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Authentication is handled per-server in MultiServerNntpClient");
    }

    public Task<NntpStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return ExecuteWithFailover(
            server => server.Client.StatAsync(segmentId, cancellationToken),
            segmentId,
            cancellationToken,
            isArticleNotFoundRetryable: true);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken cancellationToken)
    {
        return ExecuteWithFailover(
            server => server.Client.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            segmentId,
            cancellationToken,
            isArticleNotFoundRetryable: true);
    }

    public Task<YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return ExecuteWithFailover(
            server => server.Client.GetSegmentYencHeaderAsync(segmentId, cancellationToken),
            segmentId,
            cancellationToken,
            isArticleNotFoundRetryable: true);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return ExecuteWithFailover(
            server => server.Client.GetFileSizeAsync(file, cancellationToken),
            $"file:{file.FileName}",
            cancellationToken,
            isArticleNotFoundRetryable: true);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return ExecuteWithFailover(
            server => server.Client.GetArticleHeadersAsync(segmentId, cancellationToken),
            segmentId,
            cancellationToken,
            isArticleNotFoundRetryable: true);
    }

    public Task<NntpDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        if (_servers.Count == 0)
            throw new InvalidOperationException("No Usenet servers are configured");

        // Use primary server for date queries
        return _servers[0].Client.DateAsync(cancellationToken);
    }

    public async Task WaitForReady(CancellationToken cancellationToken)
    {
        // HIGH-2 FIX: Add timeout to prevent indefinite hang if a server doesn't respond
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // Timeout if any server hangs

        var tasks = _servers.Select(s => s.Client.WaitForReady(cts.Token));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancelled - rethrow original exception
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout - log which servers didn't respond
            var completedServers = tasks.Count(t => t.IsCompletedSuccessfully);
            Serilog.Log.Error(
                "WaitForReady timed out after 30 seconds. {Completed}/{Total} servers ready",
                completedServers, _servers.Count);
            throw new TimeoutException(
                $"Server initialization timed out after 30 seconds. Only {completedServers}/{_servers.Count} servers ready.");
        }
    }

    /// <summary>
    /// Execute an operation with automatic failover to backup servers
    /// </summary>
    private async Task<T> ExecuteWithFailover<T>(
        Func<ServerInstance, Task<T>> operation,
        string resourceId,
        CancellationToken cancellationToken,
        bool isArticleNotFoundRetryable)
    {
        var exceptions = new List<Exception>();

        // Take snapshot of servers to avoid race condition with UpdateServersAsync
        List<ServerInstance> serversSnapshot;
        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            serversSnapshot = _servers.ToList();
        }
        finally
        {
            _updateLock.Release();
        }

        var availableServers = serversSnapshot.Where(s => _healthTracker.IsServerAvailable(s.Config.Id)).ToList();
        var unavailableServers = serversSnapshot.Where(s => !_healthTracker.IsServerAvailable(s.Config.Id)).ToList();

        if (availableServers.Count == 0)
        {
            _logger?.LogWarning("All servers are unavailable due to circuit breaker. Attempting all servers anyway.");
            availableServers = serversSnapshot.ToList();
            unavailableServers.Clear();
        }

        _logger?.LogDebug("Multi-server operation: {AvailableCount} available, {UnavailableCount} unavailable servers",
            availableServers.Count, unavailableServers.Count);

        // Try available servers first
        foreach (var server in availableServers)
        {
            try
            {
                _logger?.LogDebug("Attempting operation on server: {ServerName} (Priority: {Priority})",
                    server.Config.Name, server.Config.Priority);
                var result = await operation(server);
                _healthTracker.RecordSuccess(server.Config.Id);

                if (exceptions.Count > 0)
                {
                    _logger?.LogInformation(
                        "Successfully retrieved {ResourceId} from fallback server {ServerName} (Priority: {Priority}) after {FailureCount} failures",
                        resourceId, server.Config.Name, server.Config.Priority, exceptions.Count);
                }
                else
                {
                    _logger?.LogDebug("Successfully retrieved {ResourceId} from primary server {ServerName} (Priority: {Priority})",
                        resourceId, server.Config.Name, server.Config.Priority);
                }

                return result;
            }
            catch (UsenetArticleNotFoundException ex)
            {
                _logger?.LogWarning(
                    "Article {ResourceId} not found on server {ServerName}",
                    resourceId, server.Config.Name);

                // BUG FIX #4: Record success - server responded correctly, article just doesn't exist
                _healthTracker.RecordSuccess(server.Config.Id);

                exceptions.Add(ex);

                // If article not found is not retryable for this operation, throw immediately
                if (!isArticleNotFoundRetryable)
                {
                    _logger?.LogDebug("Article not found is not retryable for this operation");
                    throw;
                }

                // Try next server
                continue;
            }
            catch (CouldNotConnectToUsenetException ex)
            {
                _logger?.LogWarning(
                    "Could not connect to server {ServerName}: {Message}",
                    server.Config.Name, ex.Message);

                _healthTracker.RecordFailure(server.Config.Id, ex);
                exceptions.Add(ex);
                // Try next server
                continue;
            }
            catch (CouldNotLoginToUsenetException ex)
            {
                _logger?.LogWarning(
                    "Could not authenticate to server {ServerName}: {Message}",
                    server.Config.Name, ex.Message);

                _healthTracker.RecordFailure(server.Config.Id, ex);
                exceptions.Add(ex);
                // Try next server
                continue;
            }
            catch (NntpException ex)
            {
                _logger?.LogWarning(
                    "NNTP error on server {ServerName}: {Message}",
                    server.Config.Name, ex.Message);

                _healthTracker.RecordFailure(server.Config.Id, ex);
                exceptions.Add(ex);
                // Try next server
                continue;
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled by the client (not a server failure)
                // Don't record as server failure, don't try other servers, just rethrow
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Unexpected error on server {ServerName}",
                    server.Config.Name);

                // BUG FIX #5: Record failure for all exceptions to trigger circuit breaker
                _healthTracker.RecordFailure(server.Config.Id, ex);

                exceptions.Add(ex);
                // Try next server
                continue;
            }
        }

        // If all available servers failed with article not found, try unavailable servers as last resort
        // This handles the case where an article exists only on a server with an open circuit breaker
        if (isArticleNotFoundRetryable &&
            unavailableServers.Count > 0 &&
            exceptions.All(e => e is UsenetArticleNotFoundException))
        {
            _logger?.LogInformation(
                "All {Count} available servers reported article not found for {ResourceId}. " +
                "Attempting {UnavailableCount} unavailable servers as last resort.",
                availableServers.Count, resourceId, unavailableServers.Count);

            foreach (var server in unavailableServers)
            {
                try
                {
                    _logger?.LogDebug("Attempting operation on unavailable server: {ServerName}", server.Config.Name);
                    var result = await operation(server);
                    _healthTracker.RecordSuccess(server.Config.Id);

                    _logger?.LogInformation(
                        "Successfully retrieved {ResourceId} from previously unavailable server {ServerName} after {FailureCount} failures",
                        resourceId, server.Config.Name, exceptions.Count);

                    return result;
                }
                catch (UsenetArticleNotFoundException ex)
                {
                    _logger?.LogWarning(
                        "Article {ResourceId} not found on unavailable server {ServerName}",
                        resourceId, server.Config.Name);

                    // BUG FIX #6: Record success - server responded correctly, article just doesn't exist
                    _healthTracker.RecordSuccess(server.Config.Id);

                    exceptions.Add(ex);
                    // Try next server
                    continue;
                }
                catch (CouldNotConnectToUsenetException ex)
                {
                    _logger?.LogWarning(
                        "Could not connect to unavailable server {ServerName}: {Message}",
                        server.Config.Name, ex.Message);

                    _healthTracker.RecordFailure(server.Config.Id, ex);
                    exceptions.Add(ex);
                    // Try next server
                    continue;
                }
                catch (CouldNotLoginToUsenetException ex)
                {
                    _logger?.LogWarning(
                        "Could not authenticate to unavailable server {ServerName}: {Message}",
                        server.Config.Name, ex.Message);

                    _healthTracker.RecordFailure(server.Config.Id, ex);
                    exceptions.Add(ex);
                    // Try next server
                    continue;
                }
                catch (NntpException ex)
                {
                    _logger?.LogWarning(
                        "NNTP error on unavailable server {ServerName}: {Message}",
                        server.Config.Name, ex.Message);

                    _healthTracker.RecordFailure(server.Config.Id, ex);
                    exceptions.Add(ex);
                    // Try next server
                    continue;
                }
                catch (OperationCanceledException)
                {
                    // Operation was canceled by the client (not a server failure)
                    // Don't record as server failure, don't try other servers, just rethrow
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(
                        ex,
                        "Unexpected error on unavailable server {ServerName}",
                        server.Config.Name);

                    // BUG FIX #6: Record failure for all exceptions to trigger circuit breaker
                    _healthTracker.RecordFailure(server.Config.Id, ex);

                    exceptions.Add(ex);
                    // Try next server
                    continue;
                }
            }
        }

        // All servers failed
        if (exceptions.Count > 0)
        {
            // Prioritize exceptions to show the most relevant error to the user:
            // 1. Article not found - means we successfully connected but article doesn't exist
            // 2. Other operation errors (NonRetryableDownloadException) - means we connected but operation failed
            // 3. Connection/authentication errors (RetryableDownloadException) - couldn't even try the operation
            //
            // This prevents misleading "Could not login" errors when the real issue is a missing article.
            var articleNotFoundException = exceptions.OfType<UsenetArticleNotFoundException>().FirstOrDefault();
            var nonRetryableException = exceptions.OfType<NonRetryableDownloadException>().FirstOrDefault();
            var exceptionToThrow = articleNotFoundException ?? nonRetryableException ?? exceptions[0];

            // Log summary of all failures for diagnostics
            var exceptionSummary = string.Join(", ",
                exceptions.GroupBy(e => e.GetType().Name)
                          .Select(g => $"{g.Count()}x {g.Key}"));

            var totalServersAttempted = availableServers.Count +
                (exceptions.All(e => e is UsenetArticleNotFoundException) ? unavailableServers.Count : 0);

            _logger?.LogError(
                "All {ServerCount} servers failed for resource {ResourceId}. Failures: [{ExceptionSummary}]. Throwing: {Error}",
                totalServersAttempted, resourceId, exceptionSummary, exceptionToThrow.Message);

            throw exceptionToThrow;
        }

        throw new InvalidOperationException("No servers available to execute operation");
    }

    /// <summary>
    /// Update the server configurations and connection pools
    /// </summary>
    public async Task UpdateServersAsync(IEnumerable<UsenetServerConfig> newServerConfigs)
    {
        await _updateLock.WaitAsync();
        try
        {
            // BUG FIX: Validate new configuration before clearing old servers
            // Filter to get the actual servers that will be initialized
            var configsToInitialize = newServerConfigs
                .Where(s => s.Enabled)
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.Name)
                .ToList();

            // If no valid servers in new config, keep existing servers and log error
            if (configsToInitialize.Count == 0)
            {
                _logger?.LogError(
                    "UpdateServersAsync called with zero enabled servers - " +
                    "keeping existing {Count} servers to prevent breaking streams",
                    _servers.Count);
                return;
            }

            var oldServers = _servers.ToList();
            _servers.Clear();

            // Clear old stats
            lock (_statsLock)
            {
                _serverStats.Clear();
            }

            // Reset health tracker for all servers to clear circuit breaker states
            // This ensures newly configured servers start with a clean slate
            _healthTracker.ResetAllServerHealth();

            InitializeServers(newServerConfigs);

            // Dispose old connection pools
            foreach (var oldServer in oldServers)
            {
                try
                {
                    // Unsubscribe from events before disposing
                    oldServer.ConnectionPool.OnConnectionPoolChanged -= OnServerConnectionPoolChanged;
                    oldServer.Client.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to dispose server {Name}", oldServer.Config.Name);
                }
            }

            _logger?.LogInformation("Updated server configurations. Now have {Count} servers", _servers.Count);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>
    /// Get current server configurations
    /// </summary>
    public IReadOnlyList<UsenetServerConfig> GetServerConfigs()
    {
        return _servers.Select(s => s.Config).ToList();
    }

    /// <summary>
    /// Get health statistics for all servers
    /// </summary>
    public List<ServerHealthStats> GetServerHealthStats()
    {
        return _servers.Select(s => _healthTracker.GetHealthStats(s.Config.Id)).ToList();
    }

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            // Unsubscribe from connection pool events before disposing
            server.ConnectionPool.OnConnectionPoolChanged -= OnServerConnectionPoolChanged;
            server.Client.Dispose();
        }
        _servers.Clear();
        _updateLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private class ServerInstance
    {
        public required UsenetServerConfig Config { get; init; }
        public required MultiConnectionNntpClient Client { get; init; }
        public required ConnectionPool<INntpClient> ConnectionPool { get; init; }
    }

    /// <summary>
    /// Event args for aggregate connection pool statistics across all servers
    /// </summary>
    public class AggregateConnectionPoolChangedEventArgs : EventArgs
    {
        public int Live { get; }
        public int Idle { get; }
        public int Max { get; }
        public int Active => Live - Idle;

        public AggregateConnectionPoolChangedEventArgs(int live, int idle, int max)
        {
            Live = live;
            Idle = idle;
            Max = max;
        }
    }
}

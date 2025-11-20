using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Tracks health status of Usenet servers using circuit breaker pattern
/// </summary>
public class ServerHealthTracker
{
    private readonly ConcurrentDictionary<string, ServerHealth> _serverHealth = new();
    private readonly TimeSpan _circuitBreakerTimeout = TimeSpan.FromMinutes(2);
    private readonly int _failureThreshold = 5;

    /// <summary>
    /// Check if a server is available for use
    /// </summary>
    public bool IsServerAvailable(string serverId)
    {
        if (!_serverHealth.TryGetValue(serverId, out var health))
            return true; // Unknown servers are assumed healthy

        return health.IsAvailable(_circuitBreakerTimeout);
    }

    /// <summary>
    /// Record a successful operation on a server
    /// </summary>
    public void RecordSuccess(string serverId)
    {
        var health = _serverHealth.GetOrAdd(serverId, _ => new ServerHealth());
        health.RecordSuccessInternal();
    }

    /// <summary>
    /// Record a failed operation on a server
    /// </summary>
    public void RecordFailure(string serverId, Exception exception)
    {
        var health = _serverHealth.GetOrAdd(serverId, _ => new ServerHealth());
        health.RecordFailureInternal(exception, _failureThreshold);
    }

    /// <summary>
    /// Get health statistics for a server
    /// </summary>
    public ServerHealthStats GetHealthStats(string serverId)
    {
        if (!_serverHealth.TryGetValue(serverId, out var health))
        {
            return new ServerHealthStats
            {
                ServerId = serverId,
                IsAvailable = true,
                ConsecutiveFailures = 0,
                TotalSuccesses = 0,
                TotalFailures = 0
            };
        }

        return health.GetStats(serverId);
    }

    /// <summary>
    /// Get health statistics for all tracked servers
    /// </summary>
    public List<ServerHealthStats> GetAllHealthStats()
    {
        return _serverHealth.Select(kvp => GetHealthStats(kvp.Key)).ToList();
    }

    /// <summary>
    /// Reset health tracking for a server
    /// </summary>
    public void ResetServerHealth(string serverId)
    {
        _serverHealth.TryRemove(serverId, out _);
    }

    /// <summary>
    /// Reset health tracking for all servers
    /// </summary>
    public void ResetAllServerHealth()
    {
        _serverHealth.Clear();
    }

    private class ServerHealth
    {
        private readonly object _lock = new();
        private int _consecutiveFailures;
        private int _totalSuccesses;
        private int _totalFailures;
        private DateTime? _lastSuccessTime;
        private DateTime? _lastFailureTime;
        private Exception? _lastException;
        private bool _isCircuitOpen;

        public bool IsAvailable(TimeSpan circuitBreakerTimeout)
        {
            lock (_lock)
            {
                // If circuit is open, check if timeout has elapsed
                if (_isCircuitOpen)
                {
                    if (DateTime.UtcNow - _lastFailureTime > circuitBreakerTimeout)
                    {
                        // Reset to half-open state
                        _isCircuitOpen = false;
                        _consecutiveFailures = 0;
                        return true;
                    }
                    return false; // Circuit still open
                }

                return true;
            }
        }

        public void RecordSuccessInternal()
        {
            lock (_lock)
            {
                _consecutiveFailures = 0;
                _totalSuccesses++;
                _lastSuccessTime = DateTime.UtcNow;
                _isCircuitOpen = false;
            }
        }

        public void RecordFailureInternal(Exception exception, int failureThreshold)
        {
            lock (_lock)
            {
                _consecutiveFailures++;
                _totalFailures++;
                _lastFailureTime = DateTime.UtcNow;
                _lastException = exception;

                // Open circuit if threshold exceeded
                if (_consecutiveFailures >= failureThreshold)
                {
                    _isCircuitOpen = true;
                }
            }
        }

        public ServerHealthStats GetStats(string serverId)
        {
            lock (_lock)
            {
                return new ServerHealthStats
                {
                    ServerId = serverId,
                    IsAvailable = !_isCircuitOpen,
                    ConsecutiveFailures = _consecutiveFailures,
                    TotalSuccesses = _totalSuccesses,
                    TotalFailures = _totalFailures,
                    LastSuccessTime = _lastSuccessTime,
                    LastFailureTime = _lastFailureTime,
                    LastException = _lastException?.Message
                };
            }
        }
    }
}

/// <summary>
/// Health statistics for a server
/// </summary>
public class ServerHealthStats
{
    public string ServerId { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int TotalSuccesses { get; set; }
    public int TotalFailures { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public DateTime? LastFailureTime { get; set; }
    public string? LastException { get; set; }
}

using System.Collections.Concurrent;
using NzbWebDAV.Exceptions;

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
    /// Event fired when a server's circuit breaker opens (server becomes unavailable)
    /// </summary>
    public event EventHandler<ServerUnavailableEventArgs>? OnServerUnavailable;

    /// <summary>
    /// Event fired when all server health tracking is reset
    /// </summary>
    public event EventHandler? OnAllServersHealthReset;

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
        var circuitOpened = health.RecordFailureInternal(exception, _failureThreshold);

        // Fire event if circuit breaker just opened
        if (circuitOpened)
        {
            OnServerUnavailable?.Invoke(this, new ServerUnavailableEventArgs { ServerId = serverId });
        }
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

        return health.GetStats(serverId, _circuitBreakerTimeout);
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

        // BUG FIX #3: Fire event to notify listeners (e.g., clear segment cache)
        OnAllServersHealthReset?.Invoke(this, EventArgs.Empty);
    }

    private class ServerHealth
    {
        private readonly object _lock = new();
        private int _consecutiveFailures;
        private int _totalSuccesses;
        private int _totalFailures;
        private int _totalArticlesNotFound;
        private DateTime? _lastSuccessTime;
        private DateTime? _lastFailureTime;
        private Exception? _lastException;
        private CircuitState _circuitState = CircuitState.Closed;

        public bool IsAvailable(TimeSpan circuitBreakerTimeout)
        {
            lock (_lock)
            {
                // If circuit is open, check if timeout has elapsed to transition to half-open
                if (_circuitState == CircuitState.Open)
                {
                    // BUG FIX #2: Add null safety check for _lastFailureTime
                    if (_lastFailureTime.HasValue &&
                        DateTime.UtcNow - _lastFailureTime.Value > circuitBreakerTimeout)
                    {
                        // BUG FIX #1: Transition to half-open state (don't immediately close)
                        // This allows the next request to test the server
                        _circuitState = CircuitState.HalfOpen;
                        return true; // Allow one request to test the server
                    }
                    return false; // Circuit still open
                }

                // Circuit is closed or half-open, allow requests
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

                // BUG FIX #1: Close circuit from any state on success
                // This handles the half-open -> closed transition
                _circuitState = CircuitState.Closed;
            }
        }

        public bool RecordFailureInternal(Exception exception, int failureThreshold)
        {
            lock (_lock)
            {
                var wasOpen = _circuitState == CircuitState.Open;

                _consecutiveFailures++;
                _totalFailures++;
                _lastFailureTime = DateTime.UtcNow;
                _lastException = exception;

                // Track article not found errors specifically
                if (exception is UsenetArticleNotFoundException)
                {
                    _totalArticlesNotFound++;
                }

                // BUG FIX #1: If we're in half-open and get a failure, immediately reopen
                if (_circuitState == CircuitState.HalfOpen)
                {
                    _circuitState = CircuitState.Open;
                }
                // Open circuit if threshold exceeded
                else if (_consecutiveFailures >= failureThreshold)
                {
                    _circuitState = CircuitState.Open;
                }

                // Return true if circuit just transitioned to Open
                return !wasOpen && _circuitState == CircuitState.Open;
            }
        }

        public ServerHealthStats GetStats(string serverId, TimeSpan circuitBreakerTimeout)
        {
            lock (_lock)
            {
                // BUG FIX #3: Use same logic as IsAvailable for consistency
                bool isAvailable;
                if (_circuitState == CircuitState.Open)
                {
                    isAvailable = _lastFailureTime.HasValue &&
                                  DateTime.UtcNow - _lastFailureTime.Value > circuitBreakerTimeout;
                }
                else
                {
                    isAvailable = true; // Closed or HalfOpen
                }

                return new ServerHealthStats
                {
                    ServerId = serverId,
                    IsAvailable = isAvailable,
                    ConsecutiveFailures = _consecutiveFailures,
                    TotalSuccesses = _totalSuccesses,
                    TotalFailures = _totalFailures,
                    TotalArticlesNotFound = _totalArticlesNotFound,
                    LastSuccessTime = _lastSuccessTime,
                    LastFailureTime = _lastFailureTime,
                    LastException = _lastException?.Message
                };
            }
        }
    }

    /// <summary>
    /// Circuit breaker state
    /// </summary>
    private enum CircuitState
    {
        Closed,    // Normal operation
        Open,      // Circuit is open, rejecting requests
        HalfOpen   // Testing if service has recovered
    }
}

/// <summary>
/// Health statistics for a server
/// </summary>
public class ServerHealthStats
{
    // BUG FIX NEW-014: Make ServerId required and non-nullable
    public required string ServerId { get; init; } = string.Empty;
    public bool IsAvailable { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int TotalSuccesses { get; set; }
    public int TotalFailures { get; set; }
    public int TotalArticlesNotFound { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public DateTime? LastFailureTime { get; set; }
    public string? LastException { get; set; }
}

/// <summary>
/// Event args for when a server becomes unavailable
/// </summary>
public class ServerUnavailableEventArgs : EventArgs
{
    public required string ServerId { get; init; }
}

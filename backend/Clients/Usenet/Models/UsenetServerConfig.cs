namespace NzbWebDAV.Clients.Usenet.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Configuration for a single Usenet server
/// </summary>
public class UsenetServerConfig
{
    /// <summary>
    /// Unique identifier for this server configuration
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Display name for this server
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Hostname or IP address
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Port number (typically 119 for unencrypted, 563 for SSL)
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 119;

    /// <summary>
    /// Whether to use SSL/TLS encryption
    /// </summary>
    [JsonPropertyName("useSsl")]
    public bool UseSsl { get; set; } = false;

    /// <summary>
    /// Username for authentication
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password for authentication
    /// </summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of concurrent connections to this server
    /// </summary>
    [JsonPropertyName("maxConnections")]
    public int MaxConnections { get; set; } = 50;

    /// <summary>
    /// Priority level (lower number = higher priority, 0 = highest)
    /// Servers are tried in priority order during failover
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Whether this server is enabled
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Server retention in days (0 = unknown/unlimited)
    /// Used for optimization - can skip servers with insufficient retention
    /// </summary>
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 0;

    /// <summary>
    /// Optional: Comma-separated list of server groups/tags
    /// Can be used to categorize servers (e.g., "primary", "fill", "backup")
    /// </summary>
    [JsonPropertyName("groups")]
    public string Groups { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"{Name} ({Host}:{Port}) - Priority {Priority}, {MaxConnections} connections";
    }
}

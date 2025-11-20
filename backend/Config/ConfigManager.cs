using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    private readonly Dictionary<string, string> _config = new();
    private readonly SemaphoreSlim _configLock = new(1, 1);
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

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

    public string? GetConfigValue(string configName)
    {
        _configLock.Wait();
        try
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue, options);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        _configLock.Wait();
        try
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }

            OnConfigChanged?.Invoke(this, new ConfigEventArgs
            {
                ChangedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue),
                NewConfig = _config.ToDictionary(x => x.Key, x => x.Value)
            });
        }
        finally
        {
            _configLock.Release();
        }
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("MOUNT_DIR"))
               ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public string GetApiCategories()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.categories"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("CATEGORIES"))
               ?? "audio,software,tv,movies";
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public int GetMaxConnections()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("usenet.connections")) ?? "50";
        return int.TryParse(value, out var result) ? result : 50;
    }

    public int GetConnectionsPerStream()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("usenet.connections-per-stream"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("CONNECTIONS_PER_STREAM"))
            ?? "5";
        return int.TryParse(value, out var result) ? result : 5;
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("WEBDAV_USER"));
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxQueueConnections()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.max-queue-connections"))
            ?? GetMaxConnections().ToString();
        return int.TryParse(value, out var result) ? result : GetMaxConnections();
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public bool IsEnsureArticleExistenceEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-article-existence"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public int GetMaxRepairConnections()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("repair.connections"))
            ?? GetMaxConnections().ToString();
        return int.TryParse(value, out var result) ? result : GetMaxConnections();
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
        return isRepairJobEnabled
               && GetMaxRepairConnections() > 0
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public double GetHealthCheckSamplingRate()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("repair.sampling-rate")) ?? "0.15";
        if (!double.TryParse(value, out var result))
            return 0.15;
        return Math.Clamp(result, 0.05, 1.0);
    }

    public int GetMinHealthCheckSegments()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("repair.min-segments")) ?? "10";
        if (!int.TryParse(value, out var result))
            result = 10;
        return Math.Clamp(result, 1, 100); // 1 to 100 segments
    }

    public bool IsAdaptiveSamplingEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.adaptive-sampling"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public bool IsHealthySegmentCacheEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.cache-enabled"));
        return configValue != null && bool.TryParse(configValue, out var result) ? result : defaultValue;
    }

    public TimeSpan GetHealthySegmentCacheTtl()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("repair.cache-ttl-hours")) ?? "24";
        if (!int.TryParse(value, out var hours))
            hours = 24;
        return TimeSpan.FromHours(Math.Clamp(hours, 1, 168)); // 1 hour to 7 days
    }

    public int GetParallelHealthCheckCount()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("repair.parallel-files")) ?? "3";
        if (!int.TryParse(value, out var count))
            count = 3;
        return Math.Clamp(count, 1, 10); // 1 to 10 files in parallel
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlacklistedExtensions()
    {
        var defaultValue = ".nfo, .par2, .sfv";
        return (GetConfigValue("api.download-extension-blacklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    /// <summary>
    /// Get Usenet server configurations with backward compatibility
    /// </summary>
    public List<UsenetServerConfig> GetUsenetServers()
    {
        // First check if we have new multi-server configuration
        var serversJson = GetConfigValue("usenet.servers");
        if (!string.IsNullOrWhiteSpace(serversJson))
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var servers = JsonSerializer.Deserialize<List<UsenetServerConfig>>(serversJson, options);
                if (servers != null && servers.Count > 0)
                {
                    // Filter out servers with invalid configuration (empty host, etc.)
                    return servers
                        .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Host))
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // Fall through to legacy configuration
            }
        }

        // Fall back to legacy single-server configuration
        var host = GetConfigValue("usenet.host");
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                "No Usenet server configuration found. Please configure either 'usenet.servers' or legacy 'usenet.host' settings.");
        }

        var port = int.TryParse(GetConfigValue("usenet.port"), out var parsedPort) ? parsedPort : 119;
        var useSsl = bool.TryParse(GetConfigValue("usenet.use-ssl"), out var parsedUseSsl) && parsedUseSsl;

        var legacyServer = new UsenetServerConfig
        {
            Id = "legacy-server",
            Name = "Primary Server",
            Host = host,
            Port = port,
            UseSsl = useSsl,
            Username = GetConfigValue("usenet.user") ?? string.Empty,
            Password = GetConfigValue("usenet.pass") ?? string.Empty,
            MaxConnections = GetMaxConnections(),
            Priority = 0,
            Enabled = true
        };

        return new List<UsenetServerConfig> { legacyServer };
    }

    /// <summary>
    /// Check if any Usenet server configuration changed
    /// </summary>
    public bool HasUsenetConfigChanged(Dictionary<string, string> changedConfig)
    {
        return changedConfig.ContainsKey("usenet.servers") ||
               changedConfig.ContainsKey("usenet.host") ||
               changedConfig.ContainsKey("usenet.port") ||
               changedConfig.ContainsKey("usenet.use-ssl") ||
               changedConfig.ContainsKey("usenet.user") ||
               changedConfig.ContainsKey("usenet.pass") ||
               changedConfig.ContainsKey("usenet.connections");
    }

    public class ConfigEventArgs : EventArgs
    {
        public Dictionary<string, string> ChangedConfig { get; set; } = new();
        public Dictionary<string, string> NewConfig { get; set; } = new();
    }
}
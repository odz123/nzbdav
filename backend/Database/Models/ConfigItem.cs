using System.Collections.Immutable;

namespace NzbWebDAV.Database.Models;

public class ConfigItem
{
    public static readonly ImmutableHashSet<string> Keys = ImmutableHashSet.Create([
        // General settings
        "general.base-url",

        // API settings
        "api.key",
        "api.categories",
        "api.manual-category",
        "api.max-queue-connections",
        "api.ensure-importable-video",
        "api.ensure-article-existence",
        "api.ignore-history-limit",
        "api.download-extension-blacklist",
        "api.duplicate-nzb-behavior",
        "api.import-strategy",
        "api.completed-downloads-dir",

        // Usenet settings
        "usenet.servers",
        "usenet.host",
        "usenet.port",
        "usenet.use-ssl",
        "usenet.connections",
        "usenet.connections-per-stream",
        "usenet.user",
        "usenet.pass",

        // WebDAV settings
        "webdav.user",
        "webdav.pass",
        "webdav.show-hidden-files",
        "webdav.enforce-readonly",
        "webdav.preview-par2-files",

        // Rclone settings
        "rclone.mount-dir",

        // Media settings
        "media.library-dir",

        // Arr settings
        "arr.instances",

        // Repair settings
        "repair.connections",
        "repair.enable",
        "repair.sampling-rate",
        "repair.min-segments",
        "repair.adaptive-sampling",
        "repair.cache-enabled",
        "repair.cache-ttl-hours",
        "repair.parallel-files",
    ]);

    public string ConfigName { get; set; } = null!;
    public string ConfigValue { get; set; } = null!;
}
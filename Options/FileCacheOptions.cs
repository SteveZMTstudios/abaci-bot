namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the in-memory Git blob and file content cache.
/// </summary>
public class FileCacheOptions
{
    [ConfigField(
        Description = "Maximum total bytes retained in the in-memory Git blob/file content cache before LRU eviction (50 MiB).",
        Unit = "bytes",
        Range = ">= 0",
        EmptyBehavior = "0 evicts every entry immediately, effectively disabling the cache.")]
    public long MaxSizeBytes { get; set; } = 50 * 1024 * 1024;
}


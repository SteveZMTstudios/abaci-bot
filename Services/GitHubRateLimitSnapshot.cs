namespace abaci_bot.Services;

/// <summary>
/// Point-in-time snapshot of GitHub Core and Search API rate limits and reset timestamps.
/// </summary>
public record GitHubRateLimitSnapshot(
    int? CoreRemaining,
    DateTimeOffset? CoreReset,
    int? SearchRemaining,
    DateTimeOffset? SearchReset);


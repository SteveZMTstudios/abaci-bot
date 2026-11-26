namespace abaci_bot.Options;

/// <summary>
/// Action executed when an ephemeral bot reply exceeds its retention delay.
/// </summary>
public enum EphemeralReplyAction
{
    Delete,
    Minimize
}

/// <summary>
/// Configuration options for unified persistent bot state (ignored packages, failure cooldowns,
/// user language preferences, and repository localization policies).
/// </summary>
public class BotStateOptions
{
    [ConfigField(
        Description = "Absolute local directory path for persisting bot-state.json and default lookout-first-seen.json (relative paths fail startup validation).",
        Format = "absolute local directory path",
        EmptyBehavior = "resolves LocalApplicationData -> XDG_DATA_HOME -> $HOME/.local/share/abaci-bot -> temp directory (with warning).")]
    public string StateDirectory { get; set; } = string.Empty;

    [ConfigField(
        Description = "Retention period for user language preferences before inactive entries are pruned.",
        Unit = "days",
        Range = ">= 1")]
    public int LocaleRetentionDays { get; set; } = 180;

    [ConfigField(
        Description = "Global fallback locale used when neither repository policy nor user preference is set.",
        AllowedValues = "\"en\", \"zh\", and \"en-*\" / \"zh-*\" locale tags (normalized on validation)",
        EmptyBehavior = "empty or unrecognized values fail startup validation.")]
    public string DefaultLocale { get; set; } = "en";

    [ConfigField(
        Description = "Per-repository forced locales disabling user language overrides for matched repositories.",
        EmptyBehavior = "no repositories force a global language; users may select their preferred language.",
        Example = "{\"openRuyi-Project/openRuyi\": \"zh\"}",
        MapKeyFormat = "\"owner/repo\"",
        MapValueFormat = "\"en\", \"zh\", or \"en-*\" / \"zh-*\" locale tag")]
    public Dictionary<string, string> RepositoryForcedLocales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [ConfigField(
        Description = "Per-repository default locales applied when a user has no personal preference.",
        EmptyBehavior = "repositories fall back to DefaultLocale when a user has no personal preference.",
        Example = "{\"openRuyi-Project/openRuyi\": \"zh\"}",
        MapKeyFormat = "\"owner/repo\"",
        MapValueFormat = "\"en\", \"zh\", or \"en-*\" / \"zh-*\" locale tag")]
    public Dictionary<string, string> RepositoryDefaultLocales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [ConfigField(
        Description = "Delay before ephemeral bot usage and rejection replies are cleaned up.",
        Unit = "minutes",
        Range = "0..1440",
        EmptyBehavior = "0 disables automatic cleanup of ephemeral replies.")]
    public int EphemeralReplyDeleteMinutes { get; set; } = 5;

    [ConfigField(
        Description = "Cleanup action executed when an ephemeral bot reply exceeds EphemeralReplyDeleteMinutes.",
        AllowedValues = "\"Delete\" (deletes via REST API) or \"Minimize\" (folds as OUTDATED via GraphQL API)")]
    public EphemeralReplyAction EphemeralReplyAction { get; set; } = EphemeralReplyAction.Delete;
}

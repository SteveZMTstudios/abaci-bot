namespace abaci_bot.Options;

/// <summary>
/// Configuration options for bot account detection and whitelisting.
/// </summary>
public class BotDetectionOptions
{
    [ConfigField(
        Description = "Explicit GitHub logins or \"[bot]\" slugs treated as bots when guarding human triage transitions.",
        Format = "GitHub logins or \"[bot]\" slugs",
        EmptyBehavior = "only GitHub's own bot markers (\"type=Bot\" and the \"[bot]\" suffix) are recognized; listed accounts no longer suppress triage-label removal.",
        Example = "[\"github-actions\", \"abaci-bot[bot]\"]")]
    public List<string> Whitelist { get; set; } = new()
    {
        "github-actions",
        "github-actions[bot]",
        "abaci-bot",
        "abaci-bot[bot]",
        "dependabot",
        "dependabot[bot]"
    };
}

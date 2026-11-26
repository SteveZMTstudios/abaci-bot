using System.Globalization;

namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated stale issues and pull requests lifecycle worker.
/// </summary>
public class StaleLifecycleOptions
{
    [ConfigField(
        Description = "Master toggle for the background stale management worker.",
        TrueBehavior = "sweeps every CheckIntervalHours.",
        FalseBehavior = "the worker logs that it is disabled and performs no scans.")]
    public bool Enabled { get; set; }

    [ConfigField(
        Description = "Safety dry-run switch preventing live GitHub writes.",
        TrueBehavior = "candidates are only logged without mutating GitHub.",
        FalseBehavior = "stale labels, comments, and closures are applied to GitHub.")]
    public bool DryRun { get; set; } = true;

    [ConfigField(
        Description = "Repositories monitored for stale lifecycle sweeps.",
        Format = "\"owner/repo\" entries",
        EmptyBehavior = "no repository is scanned, so the worker is effectively idle.",
        Example = "[\"openRuyi-Project/openRuyi\"]")]
    public List<string> MonitoredRepositories { get; set; } = new();

    [ConfigField(
        Description = "Repositories explicitly excluded from stale management (takes precedence over MonitoredRepositories).",
        Format = "\"owner/repo\" entries",
        EmptyBehavior = "nothing is excluded; entries here win over MonitoredRepositories.",
        Example = "[\"openRuyi-Project/archive-repo\"]")]
    public List<string> ExcludedRepositories { get; set; } = new();

    [ConfigField(
        Description = "Inactive days before an issue or PR is marked with Status: Stale (only used when Enabled=true and DryRun=false).",
        Unit = "days",
        Range = "> 0",
        EmptyBehavior = "values <= 0 are rejected by startup validation.")]
    public int StaleDays { get; set; } = 30;

    [ConfigField(
        Description = "Grace period in days after Status: Stale before automatic closure (anchored by the stale comment).",
        Unit = "days",
        Range = "> 0",
        EmptyBehavior = "values <= 0 are rejected by startup validation.")]
    public int CloseDays { get; set; } = 7;

    [ConfigField(
        Description = "Labels that exempt an issue or PR from stale marking and closure.",
        Format = "GitHub label names",
        EmptyBehavior = "no label protects an item from stale processing.",
        Example = "[\"Status: stale-exempt\", \"Workflow: Blocked\"]")]
    public List<string> ExemptLabels { get; set; } = new()
    {
        Services.GitHubLabels.StatusStaleExempt,
        Services.GitHubLabels.WorkflowBlocked,
        "Security",
        "Epic"
    };

    public const string DefaultStaleCommentTemplate =
        "This issue or pull request has been automatically marked as stale because it has not had recent activity " +
        "within the last {StaleDays} days. It will be closed in {CloseDays} days if no further activity occurs. " +
        "If this is still relevant, please comment or push a commit to keep it active. Thank you for your contributions!";

    public const string DefaultCloseCommentTemplate =
        "This issue or pull request was closed because it has been stalled for over {CloseDays} days with no activity after being marked as stale. " +
        "Feel free to reopen it or file a new one if you would like to continue the work.";

    [ConfigField(
        Description = "Comment template posted when an issue or PR is marked with Status: Stale.",
        Format = "Markdown text supporting {StaleDays} and {CloseDays} placeholders",
        EmptyBehavior = "the built-in default stale notification message is posted.")]
    public string StaleCommentTemplate { get; set; } = DefaultStaleCommentTemplate;

    [ConfigField(
        Description = "Comment template posted when a stale issue or PR is automatically closed.",
        Format = "Markdown text supporting the {CloseDays} placeholder",
        EmptyBehavior = "the built-in default close notification message is posted.")]
    public string CloseCommentTemplate { get; set; } = DefaultCloseCommentTemplate;

    /// <summary>
    /// Formats the stale notification comment by replacing {StaleDays} and {CloseDays} placeholders.
    /// </summary>
    public string FormatStaleComment()
    {
        var template = !string.IsNullOrWhiteSpace(StaleCommentTemplate)
            ? StaleCommentTemplate
            : DefaultStaleCommentTemplate;

        return template
            .Replace("{StaleDays}", StaleDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{CloseDays}", CloseDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Formats the close notification comment by replacing {CloseDays} placeholder.
    /// </summary>
    public string FormatCloseComment()
    {
        var template = !string.IsNullOrWhiteSpace(CloseCommentTemplate)
            ? CloseCommentTemplate
            : DefaultCloseCommentTemplate;

        return template.Replace("{CloseDays}", CloseDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [ConfigField(
        Description = "Execution interval controlling how often an enabled worker starts a new sweep.",
        Unit = "hours",
        Range = "> 0",
        EmptyBehavior = "values <= 0 are rejected by startup validation.")]
    public int CheckIntervalHours { get; set; } = 24;

    [ConfigField(
        Description = "Delay inserted between individual GitHub API candidate operations to prevent burst throttling.",
        Unit = "milliseconds",
        Range = ">= 0",
        EmptyBehavior = "0 means no delay between GitHub API calls.")]
    public int RequestThrottleMilliseconds { get; set; } = 500;

    [ConfigField(
        Description = "Minimum remaining GitHub API quota required before starting a stale sweep.",
        Unit = "API requests remaining",
        Range = ">= 0",
        EmptyBehavior = "0 disables the quota gate so sweeps start regardless of remaining quota.")]
    public int MinRateLimitRemaining { get; set; } = 100;
}

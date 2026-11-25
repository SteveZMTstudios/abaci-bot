namespace abaci_bot.Options;

/// <summary>
/// Configuration options for conversation locking, mention command moderation, and inactive PR sweep.
/// </summary>
public class ConversationGuardOptions
{
    /// <summary>
    /// Whether conversation guard mention command moderation (<c>@&lt;bot&gt; lock</c>, <c>unlock</c>, <c>hide</c>, <c>archive</c>) is enabled.
    /// Default: false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, moderation actions are logged without applying lock, minimize, or archive on GitHub.
    /// Default: true.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Repository whitelist for conversation guard moderation formatted as 'owner/repo'.
    /// If empty, conversation moderation is disabled globally.
    /// Default: empty list.
    /// </summary>
    public List<string> RepositoryAllowList { get; set; } = new();

    /// <summary>
    /// Independent safety switch for pull request level archiving (<c>@&lt;bot&gt; archive</c>).
    /// GitHub's <c>archivePullRequest</c> GraphQL mutation requires a <c>triage</c>+ user PAT (<c>PackageUpdate:BotPat</c>) and
    /// always returns FORBIDDEN for GitHub App installation tokens, so enabling this in App-only mode fails fast on startup.
    /// Archived pull requests are strictly read-only and reject all new comments; unarchiving must be performed out-of-band via the GitHub Web UI or <c>gh api graphql</c>.
    /// Default: false.
    /// </summary>
    public bool ArchiveEnabled { get; set; }

    /// <summary>
    /// Inactivity threshold in days based on closed_at after which closed PRs are automatically locked to prevent post-closure harassment.
    /// Bound to 7..365.
    /// Default: 60.
    /// </summary>
    public int InactiveCloseDays { get; set; } = 60;

    /// <summary>
    /// Execution interval in hours for the background closed PR lock sweep service.
    /// Bound to 1..72.
    /// Default: 12.
    /// </summary>
    public int SweepIntervalHours { get; set; } = 12;

    /// <summary>
    /// Time window in minutes for the PR author to reply with <c>@&lt;bot&gt; lock confirm</c> (or <c>@&lt;bot&gt; archive confirm</c>) before the confirmation request expires.
    /// Bound to 1..60.
    /// Default: 15.
    /// </summary>
    public int AuthorLockConfirmTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum number of inactive closed pull requests locked per repository in a single sweep cycle.
    /// Remaining eligible pull requests are deferred to subsequent sweeps.
    /// Bound to 1..500. Default: 50.
    /// </summary>
    public int MaxLocksPerSweep { get; set; } = 50;
}

namespace abaci_bot.Options;

/// <summary>
/// Configuration options for conversation locking, mention command moderation, and inactive PR sweep.
/// </summary>
public class ConversationGuardOptions
{
    [ConfigField(
        Description = "Enables or disables conversation guard mention commands and closed PR locking.",
        TrueBehavior = "mention commands are processed and closed PRs exceeding InactiveCloseDays are locked.",
        FalseBehavior = "conversation guard module and background lock sweep are disabled.")]
    public bool Enabled { get; set; }

    [ConfigField(
        Description = "Safety dry-run switch for conversation moderation actions.",
        TrueBehavior = "moderation actions are logged without applying locks, minimization, or archiving on GitHub.",
        FalseBehavior = "moderation actions are executed live on the remote repository.")]
    public bool DryRun { get; set; } = true;

    [ConfigField(
        Description = "Repository whitelist eligible for conversation guard moderation and inactive lock sweeps.",
        Format = "array of \"owner/repo\" strings",
        EmptyBehavior = "conversation moderation is disabled globally across all repositories.",
        Example = "[\"openRuyi-Project/openRuyi\"]")]
    public List<string> RepositoryAllowList { get; set; } = new();

    [ConfigField(
        Description = "Independent safety switch enabling pull request archiving via GraphQL (@<bot> archive; strictly read-only until unarchived out-of-band).",
        TrueBehavior = "pull request archiving via GraphQL is enabled (requires PackageUpdate:BotPat with triage+ user PAT; App-only mode fails fast on startup).",
        FalseBehavior = "archive commands are rejected (recommended for GitHub App-only deployments).")]
    public bool ArchiveEnabled { get; set; }

    [ConfigField(
        Description = "Threshold based on closed_at after which inactive closed PRs are automatically locked.",
        Unit = "days",
        Range = "7..365")]
    public int InactiveCloseDays { get; set; } = 60;

    [ConfigField(
        Description = "Execution interval for the background closed PR lock sweep service.",
        Unit = "hours",
        Range = "1..72")]
    public int SweepIntervalHours { get; set; } = 12;

    [ConfigField(
        Description = "Time window for the PR author to reply with @<bot> lock confirm (or archive confirm) before expiry.",
        Unit = "minutes",
        Range = "1..60")]
    public int AuthorLockConfirmTimeoutMinutes { get; set; } = 15;

    [ConfigField(
        Description = "Maximum inactive closed PRs locked per repository per sweep (500 is the search transport cap; remaining PRs defer to the next sweep).",
        Unit = "pull requests per repository per sweep",
        Range = "1..500")]
    public int MaxLocksPerSweep { get; set; } = 50;
}

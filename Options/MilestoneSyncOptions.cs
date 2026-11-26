namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated milestone assignment module and background backfill sweep service.
/// </summary>
public class MilestoneSyncOptions
{
    [ConfigField(
        Description = "Enables or disables automated monthly milestone synchronization on merged pull requests.",
        TrueBehavior = "merged PRs without a milestone are assigned to the current month's YYYY.MM milestone.",
        FalseBehavior = "milestone synchronization module and background backfill sweep are disabled.")]
    public bool Enabled { get; set; }

    [ConfigField(
        Description = "Safety dry-run switch for milestone synchronization.",
        TrueBehavior = "simulated operations are logged without writing to GitHub.",
        FalseBehavior = "milestone assignments and milestone creation requests are written to the remote repository.")]
    public bool DryRun { get; set; } = true;

    [ConfigField(
        Description = "Repository whitelist eligible for automated milestone assignment and backfill.",
        Format = "array of \"owner/repo\" strings",
        EmptyBehavior = "milestone synchronization is disabled globally across all repositories.",
        Example = "[\"openRuyi-Project/openRuyi\"]")]
    public List<string> RepositoryAllowList { get; set; } = new();

    [ConfigField(
        Description = "Permits automatic creation of missing monthly milestones (YYYY.MM).",
        TrueBehavior = "the bot creates missing monthly milestones (YYYY.MM) on the remote repository.",
        FalseBehavior = "if the target monthly milestone does not exist, assignment is skipped.")]
    public bool CreationEnabled { get; set; }

    [ConfigField(
        Description = "Target due day of the month configured when creating a new monthly milestone.",
        Unit = "day of month",
        Range = "1..28")]
    public int DueDayOfMonth { get; set; } = 25;

    [ConfigField(
        Description = "Historical time window for backfilling merged pull requests lacking a milestone.",
        Unit = "days",
        Range = "1..365")]
    public int BackfillDays { get; set; } = 30;

    [ConfigField(
        Description = "Execution interval for the background milestone backfill sweep service.",
        Unit = "hours",
        Range = "1..168")]
    public int SweepIntervalHours { get; set; } = 24;

    [ConfigField(
        Description = "IANA or system timezone identifier used when determining the YYYY.MM release milestone month (handles UTC offsets and DST).",
        Format = "IANA or system timezone ID (e.g. \"Asia/Shanghai\", \"UTC\", \"America/New_York\")",
        EmptyBehavior = "empty or unrecognized timezone IDs fail startup validation fast.")]
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
}

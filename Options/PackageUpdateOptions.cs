namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated upstream package update background service.
/// </summary>
public class PackageUpdateOptions
{
    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public string TargetRepository { get; set; } = "openRuyi-Project/openRuyi";

    public string BotLogin { get; set; } = "aqua-rium";

    /// <summary>
    /// Optional Personal Access Token for the bot. If empty, falls back to GitHub App authentication.
    /// Note: setting <c>BotPat</c> switches ALL GitHub API calls globally to that PAT user identity, so it must hold
    /// <c>Contents: Read and write</c>, <c>Pull requests: Read and write</c>, <c>Issues: Read and write</c>,
    /// <c>Actions: Read-only</c>, <c>Organization Members: Read-only</c> (classic <c>read:org</c>, required by <c>WorkflowStateModule</c>),
    /// and <c>push+</c> repository access (required whenever <c>PackageUpdate</c> runs live with <c>DryRun=false</c> or <c>ConversationGuard:Enabled=true</c>).
    /// </summary>
    public string BotPat { get; set; } = string.Empty;

    public string BranchPrefix { get; set; } = "autoupdate/";

    public int CheckIntervalHours { get; set; } = 12;

    public LookoutUpdateOptions Lookout { get; set; } = new();

    public RustCratePollerOptions RustCratePoller { get; set; } = new();

    public ObsOptions Obs { get; set; } = new();

    public bool AllowCrossMajorVersion { get; set; }

    public bool SkipPackagesWithPatches { get; set; } = true;

    public int MaxCandidatesPerSweep { get; set; } = 5;

    public int MaxConcurrentPullRequests { get; set; } = 5;

    /// <summary>
    /// Number of days an open PR whose CI failed can remain inactive before being automatically closed to free capacity,
    /// and maximum age of merged PRs before unverified branches are reclaimed. Bound to 1..363 so the derived OBS
    /// merged-PR lookback window (<c>Math.Clamp(FailedPrTtlDays + 2, 7, 365)</c>) always stays within 7..365 days.
    /// </summary>
    public int FailedPrTtlDays { get; set; } = 7;

    public int CooldownHours { get; set; } = 72;

    public int MaxConsecutiveFailures { get; set; } = 3;

    public bool IssueOnGivingUp { get; set; } = true;

    /// <summary>
    /// Shared recovery horizon in days controlling CI give-up cooldown duration, consecutive CI failure count
    /// decay window (<c>LastFailureAt</c>), and fallback OBS block cooldown when issue creation fails. Default: 30.
    /// </summary>
    public int GiveUpIssueCooldownDays { get; set; } = 30;

    public string GiveUpIssueLabel { get; set; } = Services.GitHubLabels.StatusStaleExempt;

    public int MinRateLimitRemaining { get; set; } = 500;

    public int RequestThrottleMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Maximum compressed repository tarball size in bytes accepted by <c>GitHubSpecCatalog</c> (via HTTP <c>Content-Length</c> pre-check
    /// and bounded streaming read) before aborting download and falling back to the Git Tree API; individual and cumulative decompressed
    /// <c>.spec</c> entries are additionally capped at 4 MiB per spec and 256 MiB total. Set to 0 to disable the compressed archive size cap. Default: 104857600 (100 MiB).
    /// </summary>
    public long MaxArchiveBytes { get; set; } = 100L * 1024 * 1024;
}

public class LookoutUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://lookout.openruyi.cn";

    public int StaleThresholdHours { get; set; } = 48;

    /// <summary>
    /// Optional path to the JSON file used to persist first-observed timestamps for Lookout candidates across restarts.
    /// When empty, defaults to <c>lookout-first-seen.json</c> under <c>IBotStateStore.StateDirectory</c>
    /// (<c>BotState:StateDirectory</c>, or the local application data directory when unset).
    /// </summary>
    public string FirstSeenStatePath { get; set; } = string.Empty;
}

public class RustCratePollerOptions
{
    public bool Enabled { get; set; } = true;

    public string IndexBaseUrl { get; set; } = "https://index.crates.io";

    public int MaxCratesPerSweep { get; set; } = 60;

    public int RequestDelayMilliseconds { get; set; } = 300;
}

public class ObsOptions
{
    public string BaseUrl { get; set; } = "https://build.openruyi.cn";

    public string Project { get; set; } = "openruyi";

    public string TargetRepository { get; set; } = "riscv64";

    public string TargetArch { get; set; } = "riscv64";

    public bool RequireObsSuccessAfterMerge { get; set; }
}

namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated upstream package update background service.
/// </summary>
public class PackageUpdateOptions
{
    [ConfigField(
        Description = "Master toggle for the automated upstream package update background worker.",
        TrueBehavior = "runs package discovery and PR update sweeps every CheckIntervalHours.",
        FalseBehavior = "no package update sweeps are performed.")]
    public bool Enabled { get; set; }

    [ConfigField(
        Description = "Safety dry-run switch for package updates.",
        TrueBehavior = "discovers candidates and prepares update plans without pushing branches or opening PRs.",
        FalseBehavior = "creates branches, commits SPEC rewrites, and opens/updates PRs on GitHub.")]
    public bool DryRun { get; set; } = true;

    [ConfigField(
        Description = "Target repository managed by automated package update sweeps.",
        Format = "\"owner/repo\"",
        EmptyBehavior = "sweeps are skipped.")]
    public string TargetRepository { get; set; } = "openRuyi-Project/openRuyi";

    [ConfigField(
        Description = "Fallback GitHub login used only in BotPat mode when live IBotIdentityProvider lookup fails.",
        Format = "GitHub login username",
        EmptyBehavior = "allowed at startup; ownership and bot-triage checks fail closed if live identity resolution also fails.")]
    public string BotLogin { get; set; } = "aqua-rium";

    [ConfigField(
        Description = "Optional Personal Access Token switching all GitHub API calls globally to that PAT user identity.",
        Format = "GitHub PAT token string (Contents/Pull requests/Issues RW, Actions R, Org Members R, push+ when live)",
        EmptyBehavior = "uses the GitHub App installation token instead (ConversationGuard:ArchiveEnabled=true is disallowed).",
        RequiredWhen = "ConversationGuard:ArchiveEnabled=true",
        Secret = true)]
    public string BotPat { get; set; } = string.Empty;

    [ConfigField(
        Description = "Branch name prefix applied to automated package update branches.",
        Format = "branch name prefix",
        EmptyBehavior = "branches are created without a prefix (not recommended).")]
    public string BranchPrefix { get; set; } = "autoupdate/";

    [ConfigField(
        Description = "Interval between automated package update sweeps.",
        Unit = "hours",
        Range = ">= 1")]
    public int CheckIntervalHours { get; set; } = 12;

    [ConfigField(Description = "Configuration for the Lookout upstream candidate source.")]
    public LookoutUpdateOptions Lookout { get; set; } = new();

    [ConfigField(Description = "Configuration for the crates.io sparse-index candidate source.")]
    public RustCratePollerOptions RustCratePoller { get; set; } = new();

    [ConfigField(Description = "Configuration for Open Build Service (OBS) post-merge build verification.")]
    public ObsOptions Obs { get; set; } = new();

    [ConfigField(
        Description = "Controls whether automated updates may cross a major version boundary.",
        TrueBehavior = "major version bumps are permitted when passing other admission checks.",
        FalseBehavior = "updates crossing a major version boundary (or with unparsable major components) are skipped fail-closed.")]
    public bool AllowCrossMajorVersion { get; set; }

    [ConfigField(
        Description = "Controls handling of packages carrying downstream Patch directives.",
        TrueBehavior = "packages carrying downstream patch files are skipped.",
        FalseBehavior = "packages with patches may still be considered for automated updates.")]
    public bool SkipPackagesWithPatches { get; set; } = true;

    [ConfigField(
        Description = "Maximum candidate updates evaluated per sweep cycle.",
        Unit = "candidates",
        Range = ">= 1")]
    public int MaxCandidatesPerSweep { get; set; } = 5;

    [ConfigField(
        Description = "Maximum open automated update pull requests allowed concurrently.",
        Unit = "pull requests",
        Range = ">= 1")]
    public int MaxConcurrentPullRequests { get; set; } = 5;

    [ConfigField(
        Description = "Days an inactive failed PR stays open before auto-closure and lookback age for merged-branch reclamation (values outside 1..363 fail startup validation).",
        Unit = "days",
        Range = "1..363")]
    public int FailedPrTtlDays { get; set; } = 7;

    [ConfigField(
        Description = "Minimum stabilization age an upstream release must reach before PR submission (security advisories bypass this delay).",
        Unit = "hours",
        Range = ">= 0",
        EmptyBehavior = "0 disables the stabilization window so candidates are submitted immediately.")]
    public int CooldownHours { get; set; } = 72;

    [ConfigField(
        Description = "Consecutive failure threshold before giving up on a package and entering cooldown.",
        Unit = "failures",
        Range = ">= 1")]
    public int MaxConsecutiveFailures { get; set; } = 3;

    [ConfigField(
        Description = "Whether to file a tracking issue when a package reaches MaxConsecutiveFailures.",
        TrueBehavior = "a tracking issue is filed when the failure limit is reached.",
        FalseBehavior = "cooldown is entered silently without filing a tracking issue.")]
    public bool IssueOnGivingUp { get; set; } = true;

    [ConfigField(
        Description = "Shared recovery horizon controlling CI give-up cooldown, failure decay, and fallback OBS cooldown (0 or <= 0 fails validation).",
        Unit = "days",
        Range = ">= 1")]
    public int GiveUpIssueCooldownDays { get; set; } = 30;

    [ConfigField(
        Description = "Label applied to tracking issues filed after repeated update failures.",
        Format = "label name",
        EmptyBehavior = "falls back to Status: stale-exempt.")]
    public string GiveUpIssueLabel { get; set; } = Services.GitHubLabels.StatusStaleExempt;

    [ConfigField(
        Description = "Minimum remaining core GitHub API quota required to continue candidate sweeps.",
        Unit = "API requests remaining",
        Range = ">= 0",
        EmptyBehavior = "0 disables the API quota floor check.")]
    public int MinRateLimitRemaining { get; set; } = 500;

    [ConfigField(
        Description = "Throttle delay inserted after publishing each update pull request to avoid secondary rate limits.",
        Unit = "milliseconds",
        Range = ">= 0",
        EmptyBehavior = "0 disables inter-PR throttling.")]
    public int RequestThrottleMilliseconds { get; set; } = 2000;

    [ConfigField(
        Description = "Max compressed tarball bytes accepted before falling back to Git Tree API (decompressed .spec entries stay capped at 4 MiB / 256 MiB).",
        Unit = "bytes",
        Range = ">= 0",
        EmptyBehavior = "0 disables the compressed archive cap and streams any compressed archive size.")]
    public long MaxArchiveBytes { get; set; } = 100L * 1024 * 1024;

    [ConfigField(
        Description = "Module-specific documentation links appended to automated package update PR comments (stepped-aside notices and failed-PR TTL closures).",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no documentation links are appended to package update PR comments.",
        Example = "[{ \"Title\": \"Auto-Update Guide\", \"TitleZh\": \"自动更新指南\", \"Url\": \"https://openruyi.org/docs/autoupdate\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}

public class LookoutUpdateOptions
{
    [ConfigField(
        Description = "Enables or disables the Lookout candidate source.",
        TrueBehavior = "queries Lookout for upstream package releases.",
        FalseBehavior = "the Lookout candidate source is skipped.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Base URL of the Lookout release index service.",
        Format = "absolute URL",
        EmptyBehavior = "Lookout queries cannot run.")]
    public string BaseUrl { get; set; } = "https://lookout.openruyi.cn";

    [ConfigField(
        Description = "Maximum age before Lookout candidate observations are treated as stale.",
        Unit = "hours",
        Range = "> 0")]
    public int StaleThresholdHours { get; set; } = 48;

    [ConfigField(
        Description = "Optional local file path for persisting Lookout first-observed timestamps across restarts.",
        Format = "local file path",
        EmptyBehavior = "defaults to lookout-first-seen.json under BotState:StateDirectory (/state or local app data directory).")]
    public string FirstSeenStatePath { get; set; } = string.Empty;
}

public class RustCratePollerOptions
{
    [ConfigField(
        Description = "Enables or disables the crates.io candidate poller.",
        TrueBehavior = "polls crates.io sparse index for Rust crate updates.",
        FalseBehavior = "the crates.io candidate source is skipped.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Base URL of the crates.io sparse index.",
        Format = "absolute URL",
        EmptyBehavior = "crates.io queries cannot run.")]
    public string IndexBaseUrl { get; set; } = "https://index.crates.io";

    [ConfigField(
        Description = "Maximum crates queried per sweep slice.",
        Unit = "crates",
        Range = ">= 1")]
    public int MaxCratesPerSweep { get; set; } = 60;

    [ConfigField(
        Description = "Delay inserted between crates.io sparse index HTTP requests.",
        Unit = "milliseconds",
        Range = ">= 0",
        EmptyBehavior = "0 disables request throttling.")]
    public int RequestDelayMilliseconds { get; set; } = 300;
}

public class ObsOptions
{
    [ConfigField(
        Description = "Base URL of the Open Build Service (OBS) instance for post-merge verification.",
        Format = "absolute URL",
        EmptyBehavior = "OBS verification is disabled.")]
    public string BaseUrl { get; set; } = "https://build.openruyi.cn";

    [ConfigField(
        Description = "OBS project name containing the monitored packages.",
        Format = "OBS project name",
        EmptyBehavior = "invalid when OBS verification is enabled.")]
    public string Project { get; set; } = "openruyi";

    [ConfigField(
        Description = "OBS repository name (not an architecture).",
        Format = "OBS repository name",
        EmptyBehavior = "defaults to riscv64 when left unchanged.")]
    public string TargetRepository { get; set; } = "riscv64";

    [ConfigField(
        Description = "Target OBS build architecture filter.",
        Format = "architecture identifier (e.g. riscv64, x86_64)",
        EmptyBehavior = "falls back to standard package architecture filtering.")]
    public string TargetArch { get; set; } = "riscv64";

    [ConfigField(
        Description = "Controls whether OBS post-merge failures apply Auto-Update: Blocked.",
        TrueBehavior = "a Failed, Unresolvable, or Broken post-merge OBS build applies Auto-Update: Blocked and opens an issue.",
        FalseBehavior = "OBS results are logged/reported without applying Auto-Update: Blocked.")]
    public bool RequireObsSuccessAfterMerge { get; set; }
}

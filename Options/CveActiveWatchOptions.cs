namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the active CVE vulnerability watch background service.
/// </summary>
public class CveActiveWatchOptions
{
    [ConfigField(
        Description = "Master toggle for the active CVE vulnerability watch background worker.",
        TrueBehavior = "scans catalog packages every CheckIntervalHours.",
        FalseBehavior = "no proactive CVE scans are executed.")]
    public bool Enabled { get; set; }

    [ConfigField(
        Description = "Safety dry-run switch preventing issue writes during proactive CVE scans.",
        TrueBehavior = "findings are logged without creating or closing tracking issues.",
        FalseBehavior = "security tracking issues are created, updated, and closed on GitHub.")]
    public bool DryRun { get; set; } = true;

    [ConfigField(
        Description = "Target repository scanned for CVE exposure across RPM specs.",
        Format = "\"owner/repo\"",
        EmptyBehavior = "scans and issue filing are skipped.")]
    public string TargetRepository { get; set; } = "openRuyi-Project/openRuyi";

    [ConfigField(
        Description = "Interval between proactive CVE watch sweeps.",
        Unit = "hours",
        Range = ">= 1")]
    public int CheckIntervalHours { get; set; } = 24;

    [ConfigField(
        Description = "Number of package queries batched into each OSV API request.",
        Unit = "packages per batch",
        Range = "1..1000")]
    public int OsvBatchSize { get; set; } = 1000;

    [ConfigField(
        Description = "Packages evaluated per sweep using a persisted repository-scoped 2PC watermark in bot-state.json when DryRun=false.",
        Unit = "packages per sweep",
        Range = ">= 0",
        EmptyBehavior = "0 scans every package in the catalog during each sweep.")]
    public int PackagesPerSweep { get; set; }

    [ConfigField(
        Description = "Controls best-effort OSV querying of native RPM specs lacking an authoritative upstream ecosystem.",
        TrueBehavior = "queries non-macro native RPM packages best-effort and adds a best-effort warning banner to generated issues.",
        FalseBehavior = "skips native RPM specs lacking crates.io or PyPI ecosystems to avoid cross-distribution false positives.")]
    public bool IncludeUnverifiedNativePackages { get; set; }

    [ConfigField(
        Description = "Daily cap on fallback queries dispatched to NIST NVD when OSV lacks CVSS scores.",
        Unit = "queries per day",
        Range = ">= 0",
        EmptyBehavior = "0 disables NVD fallback queries.")]
    public int MaxNvdQueriesPerDay { get; set; } = 200;

    [ConfigField(
        Description = "Delay inserted between NVD requests to avoid HTTP 429 rate limits.",
        Unit = "milliseconds",
        Range = ">= 0",
        EmptyBehavior = "0 removes inter-request delay (only safe with an NVD API key).")]
    public int NvdRequestDelayMilliseconds { get; set; } = 6500;

    [ConfigField(
        Description = "Maximum concurrent HTTP requests dispatched when resolving OSV vulnerability details.",
        Unit = "concurrent requests",
        Range = ">= 1")]
    public int MaxConcurrentOsvRequests { get; set; } = 8;

    [ConfigField(
        Description = "Label applied to proactive CVE tracking issues.",
        Format = "label name",
        EmptyBehavior = "falls back to Vulnerability: Detected.")]
    public string IssueLabel { get; set; } = "Vulnerability: Detected";

    [ConfigField(
        Description = "Duration before cached proactive CVE query results expire.",
        Unit = "hours",
        Range = ">= 1")]
    public int CacheExpirationHours { get; set; } = 168; // 7 days

    [ConfigField(
        Description = "Module-specific documentation links appended to proactive CVE notices, auto-resolution comments, and tracking issue bodies (before the hidden package marker).",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no documentation links are appended to proactive CVE comments.",
        Example = "[{ \"Title\": \"Security Policy\", \"TitleZh\": \"安全策略\", \"Url\": \"https://openruyi.org/docs/security\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}

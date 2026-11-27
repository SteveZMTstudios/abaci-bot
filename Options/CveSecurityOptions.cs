namespace abaci_bot.Options;

/// <summary>
/// Configuration options for CVE vulnerability severity and lifecycle tracking.
/// </summary>
public class CveSecurityOptions
{
    [ConfigField(
        Description = "Enables or disables the CVE security vulnerability tracking module.",
        TrueBehavior = "PR changes mentioning CVEs are queried and labeled by severity.",
        FalseBehavior = "the module is skipped with no CVE lookup, severity, or lifecycle label.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Base URL for the OSV.dev vulnerability lookup API.",
        Format = "absolute URL",
        EmptyBehavior = "OSV lookup is skipped while NVD fallback is still attempted.")]
    public string OsvApiBaseUrl { get; set; } = "https://api.osv.dev/v1/vulns/";

    [ConfigField(
        Description = "Base URL for the NIST NVD CVE 2.0 API.",
        Format = "absolute URL",
        EmptyBehavior = "NVD fallback lookup is skipped and only OSV is used.")]
    public string NvdApiBaseUrl { get; set; } = "https://services.nvd.nist.gov/rest/json/cves/2.0";

    [ConfigField(
        Description = "Optional NIST NVD API key used to increase query rate limits.",
        Format = "NVD API key string",
        EmptyBehavior = "null or empty uses unauthenticated NVD rate limits.",
        Secret = true)]
    public string? NvdApiKey { get; set; }

    [ConfigField(
        Description = "In-memory cache duration for confirmed CVE lookup results.",
        Unit = "hours",
        Range = ">= 0",
        EmptyBehavior = "0 disables caching of resolved CVE records (higher API usage).")]
    public double CacheExpirationHours { get; set; } = 12;

    [ConfigField(
        Description = "In-memory cache duration for unknown/unindexed CVE records before re-querying.",
        Unit = "hours",
        Range = ">= 0",
        EmptyBehavior = "0 prevents caching of unknown/unindexed CVEs.")]
    public double UnknownCacheExpirationHours { get; set; } = 1;

    [ConfigField(
        Description = "HTTP request timeout applied to each OSV/NVD request.",
        Unit = "seconds",
        Range = "> 0")]
    public double RequestTimeoutSeconds { get; set; } = 15;

    [ConfigField(
        Description = "Module-specific documentation links appended to the consolidated PR review footer when VulnerabilityModule posts a CVE security breakdown notice.",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no CVE-specific documentation links are appended.",
        Example = "[{ \"Title\": \"CVE Advisory Process\", \"TitleZh\": \"CVE 漏洞处理流程\", \"Url\": \"https://openruyi.org/docs/cve\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}

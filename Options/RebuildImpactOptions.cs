using System.Diagnostics.CodeAnalysis;

namespace abaci_bot.Options;

/// <summary>
/// A titled external reference link rendered in consolidated PR governance review headers
/// and deduplicated against rebuild-gate guidance.
/// </summary>
public sealed record VerificationReferenceLink
{
    [ConfigField(
        Description = "Display title used for English and default locales (single line).",
        Format = "1..120 character single-line title",
        Example = "\"Contribution Guide\"",
        Required = true)]
    public string Title { get; set; } = string.Empty;

    [ConfigField(
        Description = "Optional Chinese display title used in Chinese locales.",
        Format = "optional 1..120 character single-line title",
        EmptyBehavior = "falls back to Title when null or blank.",
        Example = "\"贡献指南\"")]
    public string? TitleZh { get; set; }

    [ConfigField(
        Description = "Absolute HTTP or HTTPS destination URL.",
        Format = "absolute http/https URI without whitespace or control characters",
        Example = "\"https://openruyi.org/docs/contributing\"",
        Required = true)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Validates and normalizes <paramref name="rawUrl"/> as an absolute HTTP/HTTPS URI with no whitespace or control characters.
    /// </summary>
    internal static bool TryNormalizeHttpUrl(string? rawUrl, [NotNullWhen(true)] out Uri? normalizedUri)
    {
        normalizedUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalizedUri = candidate;
        return true;
    }
}

/// <summary>
/// Configuration options for RPM dependency DAG building and rebuild impact gating.
/// </summary>
public class RebuildImpactOptions
{
    [ConfigField(
        Description = "Enables or disables downstream RPM rebuild impact analysis and merge gating.",
        TrueBehavior = "evaluates affected downstream packages and applies Rebuild:* / Workflow: Blocked gating.",
        FalseBehavior = "the module is skipped and background repodata refresh stops unless an ABI baseline URL is configured.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Remote URL to repomd.xml or a primary.xml(.gz/.zst) file.",
        Format = "URL to repomd.xml or primary.xml(.gz/.zst)",
        EmptyBehavior = "null falls back to the repository derived from AbiCompliance:StableRpmBaseUrl; if neither is set, no dependency graph is loaded and no Rebuild label is applied.")]
    public string? PrimaryXmlUrl { get; set; }

    [ConfigField(
        Description = "Path to a local primary metadata FILE (.xml or .xml.gz), not a directory.",
        Format = "file path",
        EmptyBehavior = "null means no local snapshot is read.")]
    public string? LocalCachePath { get; set; }

    [ConfigField(
        Description = "Polling interval for refreshing repository repodata (values below 0.1 are clamped to 0.1).",
        Unit = "hours",
        Range = "> 0")]
    public double RefreshIntervalHours { get; set; } = 6;

    [ConfigField(
        Description = "Dependency graph traversal mode.",
        AllowedValues = "\"Transitive\" (full reverse closure) or \"Direct\" (immediate dependents)")]
    public string CalculationMode { get; set; } = "Transitive";

    [ConfigField(
        Description = "Maximum recursive traversal depth for transitive closure calculation.",
        Unit = "dependency hops",
        Range = ">= 1 or null",
        EmptyBehavior = "null enables unlimited traversal depth.")]
    public int? MaxDepth { get; set; }

    [ConfigField(
        Description = "Affected package threshold triggering Workflow: Blocked on TargetBranchToBlock (0 blocks every change).",
        Unit = "affected packages",
        Range = ">= 0")]
    public int BlockThreshold { get; set; } = 1001;

    [ConfigField(
        Description = "Target release branch stream subject to rebuild impact merge blocking.",
        AllowedValues = "\"Rolling\" or \"LTS\"",
        EmptyBehavior = "falls back to \"Rolling\" when omitted.")]
    public string TargetBranchToBlock { get; set; } = "Rolling";

    [ConfigField(
        Description = "Configured governance reference links rendered once in the consolidated PR review comment header (and deduplicated against rebuild-gate guidance).",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no external reference links are rendered in consolidated review comments.",
        Example = "[{ \"Title\": \"Contribution Guide\", \"TitleZh\": \"贡献指南\", \"Url\": \"https://openruyi.org/docs/contributing\" }]")]
    public List<VerificationReferenceLink> VerificationGuideLinks { get; set; } = new();
}

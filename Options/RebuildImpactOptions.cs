namespace abaci_bot.Options;

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
        Description = "Module-specific documentation links appended dynamically to the consolidated PR review footer when RebuildImpact triggers a blocked rebuild gate (deduplicated against inline body URLs and BotState:GeneralDocumentationLinks).",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no rebuild-specific documentation links are appended.",
        Example = "[{ \"Title\": \"Rebuild Verification Guide\", \"TitleZh\": \"重构验证指南\", \"Url\": \"https://openruyi.org/docs/rebuild\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}

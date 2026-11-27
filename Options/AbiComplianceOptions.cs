namespace abaci_bot.Options;

/// <summary>
/// Configuration options for binary ABI compliance checking.
/// Supports native Libabigail abipkgdiff with pure managed C# fallback.
/// </summary>
public class AbiComplianceOptions
{
    [ConfigField(
        Description = "Enables or disables binary ABI compliance checking.",
        TrueBehavior = "workflow_run events trigger RPM ABI analysis and apply Impact labels.",
        FalseBehavior = "the module is skipped with no workflow_run analysis or Impact label.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Executable name or absolute path for the Libabigail abipkgdiff binary.",
        Format = "executable name or absolute path",
        EmptyBehavior = "the startup probe fails and the deployment gate decides whether startup continues.")]
    public string AbipkgdiffExecutablePath { get; set; } = "abipkgdiff";

    [ConfigField(
        Description = "Enables automatic fallback to the managed C# ELF/RPM symbol diff engine when native abipkgdiff is unavailable.",
        TrueBehavior = "managed fallback may be used when native tools are unavailable and AllowManagedFallback is true.",
        FalseBehavior = "the managed engine is never used, even when AllowManagedFallback is true.")]
    public bool EnableFallback { get; set; } = true;

    [ConfigField(
        Description = "Explicit deployment consent to use managed symbol-level ABI comparison when native abipkgdiff is unavailable.",
        TrueBehavior = "the managed symbol-level engine is permitted.",
        FalseBehavior = "startup aborts when native tools are unavailable.")]
    public bool AllowManagedFallback { get; set; }

    [ConfigField(
        Description = "Strict native-tool enforcement switch.",
        TrueBehavior = "startup aborts when native tools are unavailable, ignoring AllowManagedFallback and environment/file overrides.",
        FalseBehavior = "AllowManagedFallback and overrides govern fallback permission.")]
    public bool StrictEngine { get; set; }

    [ConfigField(
        Description = "Workflow name or path substrings authorized to trigger ABI analysis on workflow_run events.",
        Format = "workflow name or path substrings",
        EmptyBehavior = "built-in defaults build, package, and rpm are used.",
        Example = "[\"build\", \"package\", \"rpm\"]")]
    public List<string> WorkflowNames { get; set; } = new() { "build", "package", "rpm" };

    [ConfigField(
        Description = "Absolute base URL of the stable RPM repository root for baseline package lookups.",
        Format = "absolute URL",
        EmptyBehavior = "null disables baseline lookups so every check ends as Status: ABI Unverified.")]
    public string? StableRpmBaseUrl { get; set; }

    [ConfigField(
        Description = "Working directory used for temporary RPM extraction during ABI comparison.",
        Format = "directory path",
        EmptyBehavior = "null uses the system temporary directory.")]
    public string? TempExtractionDirectory { get; set; }

    [ConfigField(
        Description = "Process timeout applied to a single abipkgdiff invocation.",
        Unit = "seconds",
        Range = "> 0")]
    public int TimeoutSeconds { get; set; } = 300;

    [ConfigField(
        Description = "Maximum uncompressed payload processed by the managed engine (exceeding it fails the check instead of exhausting memory).",
        Unit = "bytes",
        Range = "> 0")]
    public long MaxTotalPayloadBytes { get; set; } = 1024L * 1024 * 1024;

    [ConfigField(
        Description = "Module-specific documentation links appended to the consolidated PR review footer when AbiCompliance posts an ABI breaking-change or unverified notice.",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no ABI-specific documentation links are appended.",
        Example = "[{ \"Title\": \"ABI Compatibility Guide\", \"TitleZh\": \"ABI 兼容性指南\", \"Url\": \"https://openruyi.org/docs/abi\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}

namespace abaci_bot.Options;

/// <summary>
/// Configuration options for SPDX license compliance governance.
/// </summary>
public class SpdxComplianceOptions
{
    [ConfigField(
        Description = "Enables or disables the SPDX license compliance scanning module.",
        TrueBehavior = "spec file licenses are validated against the SPDX catalog on PR updates.",
        FalseBehavior = "the module is skipped entirely with no spec license validation or label.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Remote URL of the official SPDX licenses JSON index.",
        Format = "absolute URL",
        EmptyBehavior = "the embedded license list is used and no remote refresh is attempted.")]
    public string UpdateRemoteUrl { get; set; } = "https://spdx.org/licenses/licenses.json";

    [ConfigField(
        Description = "Remote URL of the official SPDX exceptions JSON index.",
        Format = "absolute URL",
        EmptyBehavior = "the embedded exception list is used and no remote refresh is attempted.")]
    public string UpdateExceptionsRemoteUrl { get; set; } = "https://spdx.org/licenses/exceptions.json";

    [ConfigField(
        Description = "Periodic background update interval for SPDX data.",
        Unit = "hours",
        Range = ">= 0",
        EmptyBehavior = "0 disables periodic refresh; the last loaded list remains in memory.")]
    public double AutoUpdateIntervalHours { get; set; } = 24;

    [ConfigField(
        Description = "Whether known RPM legacy license aliases (GPLv2+, ASL 2.0, etc.) are accepted.",
        TrueBehavior = "known legacy aliases are accepted as compliant.",
        FalseBehavior = "legacy aliases are reported as Compliance: License Issue.")]
    public bool AllowLegacyAliases { get; set; } = true;

    [ConfigField(
        Description = "Maximum concurrency used for parallel spec evaluation.",
        Unit = "concurrent workers",
        Range = ">= 1",
        EmptyBehavior = "higher values use more CPU; values < 1 restrict parallelism to 1.")]
    public int MaxDegreeOfParallelism { get; set; } = 8;

    [ConfigField(
        Description = "Module-specific documentation links appended to the consolidated PR review footer when SpdxCompliance triggers a license compliance notice.",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no SPDX-specific documentation links are appended.",
        Example = "[{ \"Title\": \"SPDX License Policy\", \"TitleZh\": \"SPDX 许可证规范\", \"Url\": \"https://openruyi.org/docs/spdx\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}

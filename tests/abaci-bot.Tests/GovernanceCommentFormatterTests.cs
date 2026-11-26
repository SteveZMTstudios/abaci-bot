using abaci_bot.Options;
using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class GovernanceCommentFormatterTests
{
    private readonly GovernanceCommentFormatter _formatter = new();

    private static void AssertNoEmojis(string text)
    {
        // Common emojis used in previous iterations
        Assert.DoesNotContain("⚠️", text);
        Assert.DoesNotContain("🛡️", text);
        Assert.DoesNotContain("⛔", text);
        Assert.DoesNotContain("🚀", text);
    }

    private static void AssertNoInternalTaskCodes(string text)
    {
        Assert.DoesNotContain("Part 2.2", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Part2.2", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("W10", text);
        Assert.DoesNotContain("R12", text);
    }

    [Fact]
    public void FormatConsolidatedHeader_EnglishDefault_ReturnsExpectedHeaderWithoutEmojis()
    {
        var header = _formatter.FormatConsolidatedHeader();
        var lines = header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Equal(
            "Thank you for your contribution! We have identified a few points that require your attention:",
            lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("## abaci-bot Review Notes", lines[2]);
        Assert.DoesNotContain("may require you or somebody's attention", header, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", header, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", header, StringComparison.OrdinalIgnoreCase);
        AssertNoEmojis(header);
    }

    [Fact]
    public void FormatConsolidatedHeader_Chinese_ReturnsExpectedHeaderWithoutEmojis()
    {
        var header = _formatter.FormatConsolidatedHeader("zh-CN");
        var lines = header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Equal("感谢您的贡献！我们发现以下几个需要您关注的事项：", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("## abaci-bot 审查说明", lines[2]);
        Assert.DoesNotContain("可能需要您或维护者关注", header, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", header, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", header, StringComparison.OrdinalIgnoreCase);
        AssertNoEmojis(header);
    }

    [Fact]
    public void FormatConsolidatedHeader_UsesDynamicBotDisplayName_AndConfiguredVerificationGuideLinks()
    {
        var formatterWithLinks = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
            {
                VerificationGuideLinks = new List<VerificationReferenceLink>
                {
                    new()
                    {
                        Title = "Governance Docs",
                        TitleZh = "治理文档",
                        Url = "https://docs.openruyi.org/governance"
                    },
                    new()
                    {
                        Title = "Duplicate Ignored",
                        Url = "https://docs.openruyi.org/governance"
                    }
                }
            }));

        var enHeader = formatterWithLinks.FormatConsolidatedHeader("en", "abaci-bot-test[bot]");
        var enLines = enHeader.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Equal("Thank you for your contribution! We have identified a few points that require your attention:", enLines[0]);
        Assert.Contains("## abaci-bot-test Review Notes", enHeader, StringComparison.Ordinal);
        Assert.Contains("[Governance Docs](https://docs.openruyi.org/governance)", enHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate Ignored", enHeader, StringComparison.Ordinal);

        var zhHeader = formatterWithLinks.FormatConsolidatedHeader("zh-CN", "openruyi-bot");
        Assert.StartsWith("感谢您的贡献！我们发现以下几个需要您关注的事项：", zhHeader, StringComparison.Ordinal);
        Assert.Contains("## openruyi-bot 审查说明", zhHeader, StringComparison.Ordinal);
        Assert.Contains("[治理文档](https://docs.openruyi.org/governance)", zhHeader, StringComparison.Ordinal);

        var fallbackHeader = formatterWithLinks.FormatConsolidatedHeader("en", "   ");
        Assert.Contains("## abaci-bot Review Notes", fallbackHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsolidatedMessage_MultiSectionAndAbiOnly_IncludesSingleOpenerAndConfiguredLinkOnce()
    {
        const string enOpener = "Thank you for your contribution! We have identified a few points that require your attention:";
        const string zhOpener = "感谢您的贡献！我们发现以下几个需要您关注的事项：";
        const string configuredUrl = "https://docs.openruyi.org/ci-guide";
        var configuredLinks = new List<VerificationReferenceLink>
        {
            new() { Title = "CI Reference", TitleZh = "CI 文档", Url = configuredUrl }
        };

        var configuredFormatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
            {
                VerificationGuideLinks = configuredLinks
            }));

        var spdxEn = configuredFormatter.FormatSpdxLicenseIssue(
            new[] { ("SPECS/pkg/pkg.spec", "Custom", "Unknown identifier") });
        var cveEn = configuredFormatter.FormatCveBreakdown(
            new List<(string CveId, double? Cvss, string? Severity, string Source)>
            {
                ("CVE-2025-0001", 7.5, "High", "OSV")
            });
        var rebuildEn = configuredFormatter.FormatRebuildGateBlocked(
            new[] { "pkg" }, 1200, 1001, "Rolling", "en", configuredLinks);
        var abiFailedEn = configuredFormatter.FormatAbiCheckFailed("Missing baseline");
        var abiBreakingEn = configuredFormatter.FormatAbiBreakingChange("1 removed symbol");

        var multiEn = string.Join(
            "\n\n",
            configuredFormatter.FormatConsolidatedHeader("en", "abaci-bot"),
            spdxEn,
            cveEn,
            rebuildEn,
            abiBreakingEn);

        Assert.StartsWith(enOpener, multiEn, StringComparison.Ordinal);
        Assert.Equal(1, multiEn.Split(enOpener, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, multiEn.Split("Thank you for your contribution!", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, multiEn.Split(configuredUrl, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("See also", multiEn, StringComparison.Ordinal);
        AssertNoEmojis(multiEn);

        var abiOnlyEn = string.Join(
            "\n\n",
            configuredFormatter.FormatConsolidatedHeader("en", "abaci-bot"),
            abiFailedEn);
        Assert.StartsWith(enOpener, abiOnlyEn, StringComparison.Ordinal);
        Assert.Equal(1, abiOnlyEn.Split(enOpener, StringSplitOptions.None).Length - 1);
        Assert.Contains($"[CI Reference]({configuredUrl})", abiOnlyEn, StringComparison.Ordinal);
        AssertNoEmojis(abiOnlyEn);

        var spdxZh = configuredFormatter.FormatSpdxLicenseIssue(
            new[] { ("SPECS/pkg/pkg.spec", "Custom", "未知许可证") }, "zh-CN");
        var cveZh = configuredFormatter.FormatCveBreakdown(
            new List<(string CveId, double? Cvss, string? Severity, string Source)>
            {
                ("CVE-2025-0001", 7.5, "High", "OSV")
            },
            "zh-CN");
        var rebuildZh = configuredFormatter.FormatRebuildGateBlocked(
            new[] { "pkg" }, 1200, 1001, "Rolling", "zh-CN", configuredLinks);
        var abiFailedZh = configuredFormatter.FormatAbiCheckFailed("缺少基线包", "zh-CN");

        var multiZh = string.Join(
            "\n\n",
            configuredFormatter.FormatConsolidatedHeader("zh-CN", "abaci-bot"),
            spdxZh,
            cveZh,
            rebuildZh,
            abiFailedZh);

        Assert.StartsWith(zhOpener, multiZh, StringComparison.Ordinal);
        Assert.Equal(1, multiZh.Split(zhOpener, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, multiZh.Split("感谢您的贡献！", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, multiZh.Split(configuredUrl, StringSplitOptions.None).Length - 1);
        AssertNoEmojis(multiZh);
    }

    [Fact]
    public void FormatSpdxLicenseIssue_EnglishDefault_UsesGfmTip()
    {
        var issues = new List<(string File, string Expression, string Details)>
        {
            ("SPECS/Catch2/Catch2.spec", "Totally-Fake-License", "Non-standard or unknown identifier(s): `Totally-Fake-License`")
        };

        var output = _formatter.FormatSpdxLicenseIssue(issues);

        Assert.StartsWith("> [!TIP]", output);
        Assert.Contains("SPDX License Tag Update Suggested", output);
        Assert.Contains("SPECS/Catch2/Catch2.spec", output);
        Assert.Contains("Totally-Fake-License", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatSpdxLicenseIssue_Chinese_UsesGfmTip()
    {
        var issues = new List<(string File, string Expression, string Details)>
        {
            ("SPECS/test/test.spec", "FakeLicense", "非标准或未知标识符")
        };

        var output = _formatter.FormatSpdxLicenseIssue(issues, "zh-CN");

        Assert.StartsWith("> [!TIP]", output);
        Assert.Contains("请将 SPDX 许可证标签补充为标准标识符", output);
        Assert.Contains("SPECS/test/test.spec", output);
        AssertNoEmojis(output);
    }

    [Fact]
    public void FormatCveBreakdown_EnglishDefault_UsesGfmNote()
    {
        var cves = new List<(string CveId, double? Cvss, string? Severity, string Source)>
        {
            ("CVE-2024-3094", 10.0, "Critical", "OSV"),
            ("CVE-2023-9999", null, "Unknown", "NVD")
        };

        var output = _formatter.FormatCveBreakdown(cves);

        Assert.StartsWith("> [!NOTE]", output);
        Assert.Contains("Security Vulnerability Assessment", output);
        Assert.Contains("CVE-2024-3094", output);
        Assert.Contains("10.0", output);
        Assert.Contains("No public CVSS score found", output);
        Assert.Contains("expected if embargoed or unreleased", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatRebuildGateBlocked_EnglishDefault_UsesGfmImportantAndDecoupledObsGuidance()
    {
        var packages = new[] { "glibc", "openssl" };
        var output = _formatter.FormatRebuildGateBlocked(packages, 1500, 1001, "Rolling");

        Assert.StartsWith("> [!IMPORTANT]", output);
        Assert.Contains("Rebuild Impact Hold", output);
        Assert.Contains("1500", output);
        Assert.Contains("1001", output);
        Assert.Contains("Rolling", output);
        Assert.Contains("Maintainers will coordinate isolated downstream rebuild verification", output);
        Assert.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, output);
        Assert.DoesNotContain("OBS", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Staging", output);
        Assert.DoesNotContain("See also", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatRebuildGateBlocked_WithReferenceLinks_RendersBilingualSeeAlsoAndEscapesAndDedups()
    {
        var packages = new[] { "glibc" };
        var links = new List<VerificationReferenceLink>
        {
            null!,
            new() { Title = "Ignored Bad Url", Url = "ftp://invalid.example.com" },
            new() { Title = "   ", Url = "https://ignored-empty-title.example.org" },
            new()
            {
                Title = "OBS [Reverse-Dep]\\Guide\nWith Newline",
                TitleZh = "OBS 反向依赖\\[指南]",
                Url = "https://Docs.Example.org/obs-guide"
            },
            new()
            {
                Title = "Duplicate Ignored",
                Url = "https://docs.example.org/obs-guide"
            },
            new()
            {
                Title = "Fallback Distro Policy",
                TitleZh = "  ",
                Url = "https://example.org/policy"
            }
        };

        var enOutput = _formatter.FormatRebuildGateBlocked(packages, 1200, 1001, "Rolling", "en", links);
        Assert.Contains("> **See also**:", enOutput);
        Assert.Contains("> - [OBS \\[Reverse-Dep\\]\\\\Guide With Newline](https://docs.example.org/obs-guide)", enOutput);
        Assert.Contains("> - [Fallback Distro Policy](https://example.org/policy)", enOutput);
        Assert.DoesNotContain("Duplicate Ignored", enOutput);
        Assert.DoesNotContain("Ignored Bad Url", enOutput);
        Assert.DoesNotContain("ignored-empty-title", enOutput);

        var zhWithoutLinks = _formatter.FormatRebuildGateBlocked(packages, 1200, 1001, "Rolling", "zh-CN", Array.Empty<VerificationReferenceLink>());
        Assert.Contains("Rebuild 影响范围暂缓合并", zhWithoutLinks);
        Assert.Contains("外部贡献者无需进行额外环境配置", zhWithoutLinks);
        Assert.DoesNotContain("OBS", zhWithoutLinks, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, zhWithoutLinks);
        Assert.DoesNotContain("Staging", zhWithoutLinks);
        Assert.DoesNotContain("另请参阅", zhWithoutLinks);

        var zhWithLinks = _formatter.FormatRebuildGateBlocked(packages, 1200, 1001, "Rolling", "zh-CN", links);
        Assert.Contains("> **另请参阅**：", zhWithLinks);
        Assert.Contains("> - [OBS 反向依赖\\\\\\[指南\\]](https://docs.example.org/obs-guide)", zhWithLinks);
        Assert.Contains("> - [Fallback Distro Policy](https://example.org/policy)", zhWithLinks);
        AssertNoEmojis(zhWithLinks);
        AssertNoInternalTaskCodes(zhWithLinks);
    }

    [Fact]
    public void FormatAbiBreakingChange_EnglishDefault_UsesGfmCautionAndTruncatesLongOutput()
    {
        var longSnippet = new string('A', 2000);
        var output = _formatter.FormatAbiBreakingChange(longSnippet);

        Assert.StartsWith("> [!CAUTION]", output);
        Assert.Contains("ABI Breaking Change Detected", output);
        Assert.Contains("(output truncated)", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatAbiCheckFailed_EnglishDefault_UsesGfmNote()
    {
        var output = _formatter.FormatAbiCheckFailed("Baseline RPM package not available for package 'glibc'");

        Assert.StartsWith("> [!NOTE]", output);
        Assert.Contains("ABI Comparison Not Completed", output);
        Assert.Contains("Baseline RPM package not available", output);
        Assert.Contains("Status: ABI Unverified", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatAbiCheckFailed_Chinese_UsesGfmNote()
    {
        var output = _formatter.FormatAbiCheckFailed("未找到依赖 baseline", "zh-CN");

        Assert.StartsWith("> [!NOTE]", output);
        Assert.Contains("ABI 自动比对未能完成", output);
        Assert.Contains("未找到依赖 baseline", output);
        Assert.Contains("Status: ABI Unverified", output);
        AssertNoEmojis(output);
    }

    [Fact]
    public void FormatAbiCheckFailed_MultilineReason_FormatsInCodeBlock()
    {
        var multiline = "rpm2cpio: command not found\ncpio: premature end of archive\nabipkgdiff: Error while extracting package";
        var output = _formatter.FormatAbiCheckFailed(multiline);

        Assert.StartsWith("> [!NOTE]", output);
        Assert.Contains("> ```", output);
        Assert.Contains("> rpm2cpio: command not found", output);
        Assert.Contains("> abipkgdiff: Error while extracting package", output);
        AssertNoEmojis(output);
    }

    [Fact]
    public void FormatAbiCheckFailed_LongReason_TruncatesTo1000Chars()
    {
        var longReason = new string('A', 2000);
        var output = _formatter.FormatAbiCheckFailed(longReason);

        Assert.Contains("(output truncated)", output);
        AssertNoEmojis(output);
    }

    [Fact]
    public void FormatAbiBreakingChange_ChineseWithLongSnippet_TruncatesWithChineseSuffix()
    {
        var longSnippet = new string('B', 2000);
        var output = _formatter.FormatAbiBreakingChange(longSnippet, "zh-CN");

        Assert.StartsWith("> [!CAUTION]", output);
        Assert.Contains("检测到破坏性 ABI 变更", output);
        Assert.Contains("输出过长已截断", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatAbiCheckFailed_ChineseWithLongMultilineReason_FormatsCodeBlock()
    {
        var multilineLong = "Error line 1\nError line 2\n" + new string('X', 1200);
        var outputZh = _formatter.FormatAbiCheckFailed(multilineLong, "zh-CN");
        Assert.Contains("```", outputZh);
        Assert.Contains("输出过长已截断", outputZh);
        AssertNoEmojis(outputZh);
    }
}

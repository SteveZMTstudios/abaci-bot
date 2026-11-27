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
    public void FormatConsolidatedHeader_UsesDynamicBotDisplayName_AndOmitsReferenceLinks()
    {
        var formatterWithLinks = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                GeneralDocumentationLinks = new List<DocumentationLink>
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
        Assert.DoesNotContain("https://docs.openruyi.org/governance", enHeader, StringComparison.Ordinal);

        var zhHeader = formatterWithLinks.FormatConsolidatedHeader("zh-CN", "openruyi-bot");
        Assert.StartsWith("感谢您的贡献！我们发现以下几个需要您关注的事项：", zhHeader, StringComparison.Ordinal);
        Assert.Contains("## openruyi-bot 审查说明", zhHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("https://docs.openruyi.org/governance", zhHeader, StringComparison.Ordinal);

        var fallbackHeader = formatterWithLinks.FormatConsolidatedHeader("en", "   ");
        Assert.Contains("## abaci-bot Review Notes", fallbackHeader, StringComparison.Ordinal);

        var enFooter = formatterWithLinks.FormatConsolidatedFooter("en");
        Assert.StartsWith("For more details, see:", enFooter, StringComparison.Ordinal);
        Assert.Contains("[Governance Docs](https://docs.openruyi.org/governance)", enFooter, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate Ignored", enFooter, StringComparison.Ordinal);

        var zhFooter = formatterWithLinks.FormatConsolidatedFooter("zh-CN");
        Assert.StartsWith("更多参考文档：", zhFooter, StringComparison.Ordinal);
        Assert.Contains("[治理文档](https://docs.openruyi.org/governance)", zhFooter, StringComparison.Ordinal);

        Assert.Equal(string.Empty, _formatter.FormatConsolidatedFooter("en"));
        Assert.Equal(string.Empty, _formatter.FormatConsolidatedFooter("zh-CN"));
    }

    [Fact]
    public void ConsolidatedMessage_MultiSectionAndAbiOnly_PlacesReferenceFooterAfterFindings()
    {
        const string enOpener = "Thank you for your contribution! We have identified a few points that require your attention:";
        const string zhOpener = "感谢您的贡献！我们发现以下几个需要您关注的事项：";
        const string configuredUrl = "https://docs.openruyi.org/ci-guide";
        var configuredLinks = new List<DocumentationLink>
        {
            new() { Title = "CI Reference", TitleZh = "CI 文档", Url = configuredUrl }
        };

        var configuredFormatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                GeneralDocumentationLinks = configuredLinks
            }));

        var spdxEn = configuredFormatter.FormatSpdxLicenseIssue(
            new[] { ("SPECS/pkg/pkg.spec", "Custom", "Unknown identifier") });
        var cveEn = configuredFormatter.FormatCveBreakdown(
            new List<(string CveId, double? Cvss, string? Severity, string Source)>
            {
                ("CVE-2025-0001", 7.5, "High", "OSV")
            });
        var rebuildEn = configuredFormatter.FormatRebuildGateBlocked(
            new[] { "pkg" }, 1200, 1001, "Rolling", "en", referenceLinks: null);
        var abiFailedEn = configuredFormatter.FormatAbiCheckFailed("Missing baseline");
        var abiBreakingEn = configuredFormatter.FormatAbiBreakingChange("1 removed symbol");

        var multiEn = string.Join(
            "\n\n",
            configuredFormatter.FormatConsolidatedHeader("en", "abaci-bot"),
            spdxEn,
            cveEn,
            rebuildEn,
            abiBreakingEn,
            configuredFormatter.FormatConsolidatedFooter("en", moduleDocumentationLinks: configuredLinks, includeGeneralDocs: true));

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
        Assert.DoesNotContain(configuredUrl, abiOnlyEn, StringComparison.Ordinal);
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
            new[] { "pkg" }, 1200, 1001, "Rolling", "zh-CN", referenceLinks: null);
        var abiFailedZh = configuredFormatter.FormatAbiCheckFailed("缺少基线包", "zh-CN");

        var multiZh = string.Join(
            "\n\n",
            configuredFormatter.FormatConsolidatedHeader("zh-CN", "abaci-bot"),
            spdxZh,
            cveZh,
            rebuildZh,
            abiFailedZh,
            configuredFormatter.FormatConsolidatedFooter("zh-CN", moduleDocumentationLinks: configuredLinks, includeGeneralDocs: true));

        Assert.StartsWith(zhOpener, multiZh, StringComparison.Ordinal);
        Assert.Equal(1, multiZh.Split(zhOpener, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, multiZh.Split("感谢您的贡献！", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, multiZh.Split(configuredUrl, StringSplitOptions.None).Length - 1);
        AssertNoEmojis(multiZh);
    }

    [Fact]
    public void FormatWipTip_DraftAndReady_RenderPureMarkdownTipsWithoutHtmlButtons()
    {
        const string prUrl = "https://github.com/openRuyi-Project/openRuyi/pull/42";

        var draftEn = _formatter.FormatWipTip(WipTipKind.Draft, prUrl, "en");
        Assert.StartsWith("Thank you for your contribution!", draftEn, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot Review Notes", draftEn, StringComparison.Ordinal);
        Assert.Contains("> [!TIP]", draftEn, StringComparison.Ordinal);
        Assert.Contains("**Convert to draft**", draftEn, StringComparison.Ordinal);
        Assert.Contains($"[Open the pull request]({prUrl})", draftEn, StringComparison.Ordinal);
        Assert.EndsWith(GovernanceCommentFormatter.WipDraftTipMarker, draftEn, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", draftEn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-url", draftEn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", draftEn, StringComparison.OrdinalIgnoreCase);
        AssertNoEmojis(draftEn);

        var readyEn = _formatter.FormatWipTip(WipTipKind.Ready, prUrl, "en");
        Assert.StartsWith("Thank you for your contribution!", readyEn, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot Review Notes", readyEn, StringComparison.Ordinal);
        Assert.Contains("> [!TIP]", readyEn, StringComparison.Ordinal);
        Assert.Contains("**Ready for review**", readyEn, StringComparison.Ordinal);
        Assert.Contains($"[Open the pull request]({prUrl})", readyEn, StringComparison.Ordinal);
        Assert.EndsWith(GovernanceCommentFormatter.WipReadyTipMarker, readyEn, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", readyEn, StringComparison.OrdinalIgnoreCase);
        AssertNoEmojis(readyEn);

        var draftZh = _formatter.FormatWipTip(WipTipKind.Draft, prUrl, "zh-CN");
        Assert.StartsWith("感谢您的贡献！我们发现以下几个需要您关注的事项：", draftZh, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot 审查说明", draftZh, StringComparison.Ordinal);
        Assert.Contains("> [!TIP]", draftZh, StringComparison.Ordinal);
        Assert.Contains("Convert to draft", draftZh, StringComparison.Ordinal);
        Assert.Contains($"[打开拉取请求]({prUrl})", draftZh, StringComparison.Ordinal);
        Assert.EndsWith(GovernanceCommentFormatter.WipDraftTipMarker, draftZh, StringComparison.Ordinal);
        AssertNoEmojis(draftZh);

        var readyZh = _formatter.FormatWipTip(WipTipKind.Ready, prUrl, "zh-CN");
        Assert.StartsWith("感谢您的贡献！我们发现以下几个需要您关注的事项：", readyZh, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot 审查说明", readyZh, StringComparison.Ordinal);
        Assert.Contains("> [!TIP]", readyZh, StringComparison.Ordinal);
        Assert.Contains("Ready for review", readyZh, StringComparison.Ordinal);
        Assert.Contains($"[打开拉取请求]({prUrl})", readyZh, StringComparison.Ordinal);
        Assert.EndsWith(GovernanceCommentFormatter.WipReadyTipMarker, readyZh, StringComparison.Ordinal);
        AssertNoEmojis(readyZh);
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
        var links = new List<DocumentationLink>
        {
            null!,
            new() { Title = "Ignored Empty Url", Url = "   " },
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
                Url = "https://Docs.Example.org/obs-guide"
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
        Assert.Contains("> - [OBS \\[Reverse-Dep\\]\\\\Guide With Newline](https://Docs.Example.org/obs-guide)", enOutput);
        Assert.Contains("> - [Fallback Distro Policy](https://example.org/policy)", enOutput);
        Assert.DoesNotContain("Duplicate Ignored", enOutput);
        Assert.DoesNotContain("Ignored Empty Url", enOutput);
        Assert.DoesNotContain("ignored-empty-title", enOutput);

        var zhWithoutLinks = _formatter.FormatRebuildGateBlocked(packages, 1200, 1001, "Rolling", "zh-CN", Array.Empty<DocumentationLink>());
        Assert.Contains("Rebuild 影响范围暂缓合并", zhWithoutLinks);
        Assert.Contains("外部贡献者无需进行额外环境配置", zhWithoutLinks);
        Assert.DoesNotContain("OBS", zhWithoutLinks, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, zhWithoutLinks);
        Assert.DoesNotContain("Staging", zhWithoutLinks);
        Assert.DoesNotContain("另请参阅", zhWithoutLinks);

        var zhWithLinks = _formatter.FormatRebuildGateBlocked(packages, 1200, 1001, "Rolling", "zh-CN", links);
        Assert.Contains("> **另请参阅**：", zhWithLinks);
        Assert.Contains("> - [OBS 反向依赖\\\\\\[指南\\]](https://Docs.Example.org/obs-guide)", zhWithLinks);
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

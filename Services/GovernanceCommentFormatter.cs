using System.Globalization;
using System.Text;
using abaci_bot.Options;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Default implementation of <see cref="IGovernanceCommentFormatter"/>.
/// Formats pull request comments using GitHub Flavored Markdown (GFM) alerts
/// without emojis or internal task codes, defaulting to English with internationalization support.
/// </summary>
public class GovernanceCommentFormatter : IGovernanceCommentFormatter
{
    public const string RebuildGateCommentMarker = "<!-- abaci-bot:rebuild-gate -->";
    public const string WipDraftTipMarker = "<!-- abaci-bot:wip-tip:draft -->";
    public const string WipReadyTipMarker = "<!-- abaci-bot:wip-tip:ready -->";

    internal const string WipDraftTipLeadEn =
        "> This pull request title contains `WIP`. If it is not ready for review, use **Convert to draft** under the pull request description.";
    internal const string WipDraftTipLeadZh =
        "> 此拉取请求标题包含 `WIP`。如果尚未准备好审查，请在 PR 描述下方点击 **Convert to draft**。";
    internal const string WipReadyTipLeadEn =
        "> `WIP` was removed from the title. If the changes are ready, use **Ready for review** under the pull request description.";
    internal const string WipReadyTipLeadZh =
        "> `WIP` 已从标题移除。如果更改已准备好审查，请在 PR 描述下方点击 **Ready for review**。";

    private readonly IReadOnlyList<DocumentationLink>? _generalDocumentationLinks;

    public GovernanceCommentFormatter(IOptions<BotStateOptions>? botStateOptions = null)
    {
        _generalDocumentationLinks = botStateOptions?.Value.GeneralDocumentationLinks;
    }

    public string PreferredLocale { get; set; } = "en";

    private bool IsChinese(string? locale) =>
        string.Equals(locale ?? PreferredLocale, "zh", StringComparison.OrdinalIgnoreCase) ||
        (locale ?? PreferredLocale).StartsWith("zh-", StringComparison.OrdinalIgnoreCase);

    public string FormatConsolidatedHeader(string? locale = null) =>
        FormatConsolidatedHeader(locale, botDisplayName: null);

    public string FormatConsolidatedHeader(string? locale, string? botDisplayName)
    {
        var isZh = IsChinese(locale);
        var displayName = NormalizeBotDisplayName(botDisplayName);
        var sb = new StringBuilder();

        if (isZh)
        {
            sb.AppendLine("感谢您的贡献！我们发现以下几个需要您关注的事项：");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"## {displayName} 审查说明");
        }
        else
        {
            sb.AppendLine("Thank you for your contribution! We have identified a few points that require your attention:");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"## {displayName} Review Notes");
        }

        return sb.ToString().TrimEnd();
    }

    public string FormatConsolidatedFooter(
        string? locale = null,
        IReadOnlyList<DocumentationLink>? moduleDocumentationLinks = null,
        bool includeGeneralDocs = true,
        IReadOnlySet<string>? excludedUrls = null)
    {
        var isZh = IsChinese(locale);
        var combinedLinks = new List<DocumentationLink?>();
        if (moduleDocumentationLinks != null && moduleDocumentationLinks.Count > 0)
        {
            combinedLinks.AddRange(moduleDocumentationLinks);
        }

        if (includeGeneralDocs && _generalDocumentationLinks != null && _generalDocumentationLinks.Count > 0)
        {
            combinedLinks.AddRange(_generalDocumentationLinks);
        }

        return DocumentationLinkFormatter.FormatSeeAlsoFooter(combinedLinks, isZh, excludedUrls);
    }

    public string FormatWipTip(
        WipTipKind kind,
        string prUrl,
        string? locale = null,
        IReadOnlyList<DocumentationLink>? documentationLinks = null)
    {
        var isZh = IsChinese(locale);
        var sb = new StringBuilder();
        var marker = kind == WipTipKind.Draft ? WipDraftTipMarker : WipReadyTipMarker;

        sb.AppendLine("> [!TIP]");
        if (kind == WipTipKind.Draft)
        {
            if (isZh)
            {
                sb.AppendLine(WipDraftTipLeadZh);
                sb.AppendLine(">");
                sb.AppendLine(CultureInfo.InvariantCulture, $"> [打开拉取请求]({prUrl})");
            }
            else
            {
                sb.AppendLine(WipDraftTipLeadEn);
                sb.AppendLine(">");
                sb.AppendLine(CultureInfo.InvariantCulture, $"> [Open the pull request]({prUrl})");
            }
        }
        else
        {
            if (isZh)
            {
                sb.AppendLine(WipReadyTipLeadZh);
                sb.AppendLine(">");
                sb.AppendLine(CultureInfo.InvariantCulture, $"> [打开拉取请求]({prUrl})");
            }
            else
            {
                sb.AppendLine(WipReadyTipLeadEn);
                sb.AppendLine(">");
                sb.AppendLine(CultureInfo.InvariantCulture, $"> [Open the pull request]({prUrl})");
            }
        }

        var seeAlsoBlockquote = DocumentationLinkFormatter.FormatSeeAlsoBlockquoteFooter(documentationLinks, isZh);
        if (!string.IsNullOrEmpty(seeAlsoBlockquote))
        {
            sb.AppendLine(">");
            sb.AppendLine(seeAlsoBlockquote);
        }

        sb.Append(marker);
        return sb.ToString();
    }

    private static string NormalizeBotDisplayName(string? botDisplayName)
    {
        if (string.IsNullOrWhiteSpace(botDisplayName))
        {
            return "abaci-bot";
        }

        var trimmed = botDisplayName.Trim();
        const string botSuffix = "[bot]";
        if (trimmed.EndsWith(botSuffix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^botSuffix.Length].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "abaci-bot" : trimmed;
    }

    public string FormatSpdxLicenseIssue(IReadOnlyList<(string File, string Expression, string Details)> issues, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var sb = new StringBuilder();
        sb.AppendLine("> [!TIP]");

        if (isZh)
        {
            sb.AppendLine("> **请将 SPDX 许可证标签补充为标准标识符**");
            sb.AppendLine("> 为帮助软件包元数据顺利通过合规检查，请将以下 Spec 文件中的 `License:` 更新为标准 SPDX 标识符：");
            foreach (var (file, expr, details) in issues)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{file}`: 当前声明 `{expr}`（{details}）。");
            }
            sb.AppendLine(">");
            sb.Append("> 您可以在 [SPDX License List](https://spdx.org/licenses/) 查询标准标识符并在方便时推送更新。");
        }
        else
        {
            sb.AppendLine("> **SPDX License Tag Update Suggested**");
            sb.AppendLine("> To help keep package metadata compliant, please update the `License:` field(s) below to use standard SPDX identifiers:");
            foreach (var (file, expr, details) in issues)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{file}`: declared `{expr}` ({details}).");
            }
            sb.AppendLine(">");
            sb.Append("> You can look up standard identifiers in the [SPDX License List](https://spdx.org/licenses/) and push an update whenever you're ready.");
        }

        return sb.ToString();
    }

    public string FormatCveBreakdown(IReadOnlyList<(string CveId, double? Cvss, string? Severity, string Source)> cves, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var sb = new StringBuilder();
        sb.AppendLine("> [!NOTE]");

        if (isZh)
        {
            sb.AppendLine("> **安全漏洞评估清单**");
            sb.AppendLine("> 我们检测到本 PR 涉及以下 CVE 漏洞记录，供您与维护者审查参考：");
            foreach (var (cveId, cvss, severity, source) in cves)
            {
                if (cvss.HasValue)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: CVSS **{cvss.Value:F1}** ({severity}, 来源: {source})");
                }
                else if (string.Equals(source, CveReport.SourcePartiallyFailed, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: 一个漏洞数据源不可用，结果可能不完整（请安全团队手动核验）");
                }
                else if (string.Equals(source, CveReport.SourceQueryFailed, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: 漏洞数据源查询失败（请安全团队手动核验）");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: 未查询到公网 CVSS 评分（请安全团队手动核验）");
                }
            }
        }
        else
        {
            sb.AppendLine("> **Security Vulnerability Assessment**");
            sb.AppendLine("> We spotted the following CVE references associated with this pull request:");
            foreach (var (cveId, cvss, severity, source) in cves)
            {
                if (cvss.HasValue)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: CVSS **{cvss.Value:F1}** ({severity}, source: {source})");
                }
                else if (string.Equals(source, CveReport.SourcePartiallyFailed, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: One vulnerability source was unavailable; details may be incomplete (manual security review required)");
                }
                else if (string.Equals(source, CveReport.SourceQueryFailed, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: Vulnerability sources could not be queried (manual security review required)");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: No public CVSS score found (manual security review required; expected if embargoed or unreleased)");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public string FormatRebuildGateBlocked(
        IReadOnlyList<string> packages,
        int affectedCount,
        int threshold,
        string targetBranch,
        string? locale = null,
        IReadOnlyList<DocumentationLink>? referenceLinks = null)
    {
        var isZh = IsChinese(locale);
        var pkgList = string.Join("`, `", packages);
        var headerUrls = DocumentationLinkFormatter.ResolveLinks(_generalDocumentationLinks, isZh)
            .Select(l => l.Url)
            .ToHashSet(StringComparer.Ordinal);
        var resolvedLinks = DocumentationLinkFormatter.ResolveLinks(referenceLinks, isZh)
            .Where(l => !headerUrls.Contains(l.Url))
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine("> [!IMPORTANT]");

        if (isZh)
        {
            sb.AppendLine("> **Rebuild 影响范围暂缓合并**");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> 本次变更涉及基础软件包（`{pkgList}`），在下游反向依赖图中会影响 **{affectedCount}** 个软件包重构（暂缓阈值：{threshold}）。");
            sb.AppendLine(">");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> 为保障 `{targetBranch}` 分支稳定性，我们先将该 PR 的合并暂时置于等待状态（已标记 `Workflow: Blocked`），待维护者确认下游兼容后推进。");
            sb.AppendLine("> **后续步骤**：维护者会协调完成受影响下游软件包的隔离构建验证，确认通过后解除暂缓状态，外部贡献者无需进行额外环境配置。");
        }
        else
        {
            sb.AppendLine("> **Rebuild Impact Hold**");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> Because changes to base packages (`{pkgList}`) trigger a cascading rebuild of **{affectedCount}** downstream packages in the reverse dependency graph (hold threshold: {threshold}),");
            sb.AppendLine(">");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> we place merges to `{targetBranch}` on hold (`Workflow: Blocked`) while maintainers verify downstream compatibility.");
            sb.AppendLine("> **Next Steps**: Maintainers will coordinate isolated downstream rebuild verification and lift the hold once checks pass—no extra setup is required from external contributors.");
        }

        if (resolvedLinks.Count > 0)
        {
            sb.AppendLine(">");
            sb.AppendLine(isZh ? "> **另请参阅**：" : "> **See also**:");
            foreach (var (title, url) in resolvedLinks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - [{title}]({url})");
            }
        }

        sb.AppendLine(RebuildGateCommentMarker);
        return sb.ToString().TrimEnd();
    }

    public string FormatAbiBreakingChange(string diffSnippet, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var snippet = diffSnippet;
        if (snippet.Length > 1500)
        {
            snippet = isZh
                ? snippet[..1500] + "\n... (输出过长已截断)"
                : snippet[..1500] + "\n... (output truncated)";
        }

        var sb = new StringBuilder();
        sb.AppendLine("> [!CAUTION]");

        if (isZh)
        {
            sb.AppendLine("> **检测到破坏性 ABI 变更**");
            sb.AppendLine("> 二进制比对发现共享库存在破坏性 ABI 修改（例如导出的公共符号缺失或 SONAME 变更）：");
            sb.AppendLine(">");
            sb.AppendLine("> ```");
            foreach (var line in snippet.Split('\n'))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> {line.TrimEnd('\r')}");
            }
            sb.AppendLine("> ```");
            sb.AppendLine(">");
            sb.Append("> 依赖该动态库的下游软件包可能在运行时发生符号解析失败。请与维护者确认该破坏性变更是否符合预期，以便协调后续下游重构。");
        }
        else
        {
            sb.AppendLine("> **ABI Breaking Change Detected**");
            sb.AppendLine("> Binary comparison detected incompatible ABI modifications in shared libraries (for example, missing exported symbols or SONAME changes):");
            sb.AppendLine(">");
            sb.AppendLine("> ```");
            foreach (var line in snippet.Split('\n'))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> {line.TrimEnd('\r')}");
            }
            sb.AppendLine("> ```");
            sb.AppendLine(">");
            sb.Append("> Downstream packages depending on this library may encounter symbol resolution failures at runtime. Please confirm with maintainers whether this breaking ABI change is intended so downstream rebuilds can be coordinated.");
        }

        return sb.ToString();
    }

    public string FormatAbiCheckFailed(string reason, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var sanitizedReason = reason.Trim();
        if (sanitizedReason.Length > 1000)
        {
            sanitizedReason = isZh
                ? sanitizedReason[..1000] + "\n... (输出过长已截断)"
                : sanitizedReason[..1000] + "\n... (output truncated)";
        }

        var sb = new StringBuilder();
        sb.AppendLine("> [!NOTE]");

        if (isZh)
        {
            sb.AppendLine("> **ABI 自动比对未能完成**");
            sb.AppendLine("> 本 PR 的二进制 ABI 自动比对未能完整执行（这并不意味着您的变更存在问题）：");
            if (sanitizedReason.Contains('\n'))
            {
                sb.AppendLine("> - 原因：");
                sb.AppendLine(">");
                sb.AppendLine("> ```");
                foreach (var line in sanitizedReason.Split('\n'))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> {line.TrimEnd('\r')}");
                }
                sb.AppendLine("> ```");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - 原因：{sanitizedReason}");
            }
            sb.AppendLine(">");
            sb.Append("> 本 PR 已标记 `Status: ABI Unverified`，维护者可按需检查构建产物或基线软件包。");
        }
        else
        {
            sb.AppendLine("> **ABI Comparison Not Completed**");
            sb.AppendLine("> We couldn't complete the automated binary ABI comparison for this pull request (this is not necessarily an issue with your changes):");
            if (sanitizedReason.Contains('\n'))
            {
                sb.AppendLine("> - Reason:");
                sb.AppendLine(">");
                sb.AppendLine("> ```");
                foreach (var line in sanitizedReason.Split('\n'))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> {line.TrimEnd('\r')}");
                }
                sb.AppendLine("> ```");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - Reason: {sanitizedReason}");
            }
            sb.AppendLine(">");
            sb.Append("> This pull request has been marked `Status: ABI Unverified`. Maintainers can inspect the build artifacts or baseline packages if needed.");
        }

        return sb.ToString();
    }
}

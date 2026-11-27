using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Resolves, deduplicates, escapes, and formats <see cref="DocumentationLink"/> collections
/// for consolidated PR review footers and standalone bot comments.
/// </summary>
public static partial class DocumentationLinkFormatter
{
    [GeneratedRegex(@"https?://[^\s<>""\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRegex();

    /// <summary>
    /// Filters non-empty documentation links, selects the locale-appropriate title,
    /// escapes markdown link titles, excludes optional <paramref name="excludedUrls"/>,
    /// and deduplicates by trimmed URL string while passing destinations through verbatim.
    /// </summary>
    public static List<(string Title, string Url)> ResolveLinks(
        IEnumerable<DocumentationLink?>? links,
        bool isChinese,
        IReadOnlySet<string>? excludedUrls = null)
    {
        var result = new List<(string Title, string Url)>();
        if (links == null)
        {
            return result;
        }

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Url))
            {
                continue;
            }

            var url = link.Url.Trim();
            if (excludedUrls != null && excludedUrls.Contains(url))
            {
                continue;
            }

            var rawTitle = isChinese && !string.IsNullOrWhiteSpace(link.TitleZh) ? link.TitleZh : link.Title;
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                continue;
            }

            if (!seenUrls.Add(url))
            {
                continue;
            }

            result.Add((EscapeMarkdownLinkTitle(rawTitle), url));
        }

        return result;
    }

    /// <summary>
    /// Extracts verbatim HTTP/HTTPS URL strings from rendered markdown sections so footers avoid repeating
    /// documentation links that already appear inline inside finding bodies.
    /// </summary>
    public static HashSet<string> ExtractNormalizedHttpUrls(IEnumerable<string?>? bodies)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        if (bodies == null)
        {
            return urls;
        }

        foreach (var body in bodies)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            foreach (Match match in HttpUrlRegex().Matches(body))
            {
                var candidate = TrimUrlDelimiters(match.Value);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    urls.Add(candidate);
                }
            }
        }

        return urls;
    }

    private static string TrimUrlDelimiters(string rawUrl)
    {
        var candidate = rawUrl.TrimEnd('.', ',', ';', ':', '!', '?');
        var openParens = candidate.Count(static c => c == '(');
        var closeParens = candidate.Count(static c => c == ')');
        while (closeParens > openParens && candidate.EndsWith(')'))
        {
            candidate = candidate[..^1];
            closeParens--;
        }

        return candidate.TrimEnd('.', ',', ';', ':', '!', '?');
    }

    /// <summary>
    /// Formats a top-level "See also" / "更多参考文档" footer section, or <see cref="string.Empty"/> when no valid links remain.
    /// </summary>
    public static string FormatSeeAlsoFooter(
        IEnumerable<DocumentationLink?>? links,
        bool isChinese,
        IReadOnlySet<string>? excludedUrls = null)
    {
        return FormatListSection(
            links,
            isChinese,
            excludedUrls,
            heading: isChinese ? "更多参考文档：" : "For more details, see:",
            linePrefix: "- ");
    }

    /// <summary>
    /// Formats a GFM blockquote-indented "See also" / "另请参阅" section for callout blocks (e.g., <c>&gt; [!TIP]</c>),
    /// or <see cref="string.Empty"/> when no valid links remain.
    /// </summary>
    public static string FormatSeeAlsoBlockquoteFooter(
        IEnumerable<DocumentationLink?>? links,
        bool isChinese)
    {
        return FormatListSection(
            links,
            isChinese,
            excludedUrls: null,
            heading: isChinese ? "> **另请参阅**：" : "> **See also**:",
            linePrefix: "> - ");
    }

    private static string FormatListSection(
        IEnumerable<DocumentationLink?>? links,
        bool isChinese,
        IReadOnlySet<string>? excludedUrls,
        string heading,
        string linePrefix)
    {
        var resolvedLinks = ResolveLinks(links, isChinese, excludedUrls);
        if (resolvedLinks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(heading);
        foreach (var (title, url) in resolvedLinks)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{linePrefix}[{title}]({url})");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Appends a "See also" footer to <paramref name="body"/> when valid <paramref name="links"/> are provided.
    /// </summary>
    public static string AppendFooter(
        string body,
        IEnumerable<DocumentationLink?>? links,
        bool isChinese)
    {
        var footer = FormatSeeAlsoFooter(links, isChinese);
        if (string.IsNullOrEmpty(footer))
        {
            return body;
        }

        return string.IsNullOrWhiteSpace(body)
            ? footer
            : $"{body.TrimEnd()}\n\n{footer}";
    }

    /// <summary>
    /// Inserts a "See also" footer immediately before the trailing hidden HTML <paramref name="marker"/>,
    /// or appends at the end when the marker is not present.
    /// </summary>
    public static string AppendFooterBeforeMarker(
        string body,
        string marker,
        IEnumerable<DocumentationLink?>? links,
        bool isChinese)
    {
        var footer = FormatSeeAlsoFooter(links, isChinese);
        if (string.IsNullOrEmpty(footer))
        {
            return body;
        }

        if (!string.IsNullOrEmpty(marker))
        {
            var markerIndex = body.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                var before = body[..markerIndex].TrimEnd();
                var fromMarker = body[markerIndex..];
                return string.IsNullOrEmpty(before)
                    ? $"{footer}\n\n{fromMarker}"
                    : $"{before}\n\n{footer}\n\n{fromMarker}";
            }
        }

        return AppendFooter(body, links, isChinese);
    }

    private static string EscapeMarkdownLinkTitle(string rawTitle)
    {
        return rawTitle
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }
}

using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// A titled external documentation link rendered in bot comments and review footers.
/// </summary>
public sealed record DocumentationLink
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
        Description = "Destination URL passed through verbatim into rendered markdown links.",
        Format = "non-empty URL string",
        Example = "\"https://openruyi.org/docs/contributing\"",
        Required = true)]
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Shared startup validator for <see cref="DocumentationLink"/> collections across module options.
/// </summary>
public static class DocumentationLinkValidator
{
    public const int MaxDocumentationLinks = 5;
    public const int MaxLinkTitleLength = 120;

    /// <summary>
    /// Validates count bounds, non-null items, single-line title length, non-empty URL strings, and exact URL uniqueness.
    /// </summary>
    public static void Validate(
        IReadOnlyList<DocumentationLink>? links,
        string propertyName,
        List<string> failures)
    {
        if (links == null)
        {
            return;
        }

        if (links.Count > MaxDocumentationLinks)
        {
            failures.Add($"{propertyName} supports at most {MaxDocumentationLinks} entries (got {links.Count}).");
            return;
        }

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link == null)
            {
                failures.Add($"{propertyName}[{i}] cannot be null.");
                continue;
            }

            ValidateLinkTitle(link.Title, $"{propertyName}[{i}].{nameof(link.Title)}", required: true, failures);
            ValidateLinkTitle(link.TitleZh, $"{propertyName}[{i}].{nameof(link.TitleZh)}", required: false, failures);

            if (string.IsNullOrWhiteSpace(link.Url))
            {
                failures.Add($"{propertyName}[{i}].{nameof(link.Url)} is required and cannot be empty.");
            }
            else
            {
                var trimmedUrl = link.Url.Trim();
                if (!seenUrls.Add(trimmedUrl))
                {
                    failures.Add($"{propertyName}[{i}].{nameof(link.Url)} duplicates an existing URL ('{trimmedUrl}').");
                }
            }
        }
    }

    private static void ValidateLinkTitle(string? title, string path, bool required, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            if (required)
            {
                failures.Add($"{path} is required and cannot be empty.");
            }

            return;
        }

        if (title.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
        {
            failures.Add($"{path} cannot contain newlines or control characters.");
            return;
        }

        if (title.Trim().Length > MaxLinkTitleLength)
        {
            failures.Add($"{path} must be at most {MaxLinkTitleLength} characters.");
        }
    }
}

/// <summary>
/// Generic <see cref="IValidateOptions{TOptions}"/> adapter validating a module's <c>DocumentationLinks</c> property at startup.
/// </summary>
public sealed class DocumentationLinksOptionsValidator<TOptions>(
    Func<TOptions, IReadOnlyList<DocumentationLink>?> selector,
    string propertyName = "DocumentationLinks") : IValidateOptions<TOptions>
    where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var failures = new List<string>();
        DocumentationLinkValidator.Validate(selector(options), propertyName, failures);
        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

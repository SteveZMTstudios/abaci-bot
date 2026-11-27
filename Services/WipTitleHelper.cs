using System.Text.RegularExpressions;

namespace abaci_bot.Services;

/// <summary>
/// Shared token-aware detector for WIP pull request titles, keeping workflow state labeling
/// and draft reminder guidance consistent across <c>WIP:</c>, <c>[WIP]</c>, and <c>(WIP)</c>.
/// </summary>
internal static partial class WipTitleHelper
{
    [GeneratedRegex(@"(?:^\s*WIP(?![A-Za-z0-9])|[\[\(]\s*WIP\s*[\]\)])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WipTokenRegex();

    /// <summary>
    /// Returns <c>true</c> when <paramref name="title"/> contains a standalone <c>WIP</c> token.
    /// </summary>
    public static bool IsWip(string? title)
    {
        return !string.IsNullOrWhiteSpace(title) && WipTokenRegex().IsMatch(title);
    }
}

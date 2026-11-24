using System.Globalization;
using System.Text.RegularExpressions;
using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module unifying package update admission decisions, version comparison,
/// branch deduction for Rust crates, and spec invariants.
/// Pulls complexity down from individual services and prevents temporal decomposition.
/// </summary>
public static class UpdateAdmission
{
    private static readonly Regex FullVersionRegex = new(
        @"^\s*%global\s+full_version\s+(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex VersionRegex = new(
        @"^Version:\s*(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Extracts the authoritative non-macro current version from RPM SPEC content,
    /// preferring <c>%global full_version</c> and falling back to <c>Version:</c>.
    /// Rejects unresolved RPM macro placeholders containing <c>%</c>.
    /// </summary>
    public static bool TryExtractCurrentVersion(string? specContent, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return false;
        }

        var fullMatch = FullVersionRegex.Match(specContent);
        if (fullMatch.Success)
        {
            var candidate = fullMatch.Groups["ver"].Value.Trim();
            if (!string.IsNullOrEmpty(candidate) && !candidate.Contains('%'))
            {
                version = candidate;
                return true;
            }
        }

        var verMatch = VersionRegex.Match(specContent);
        if (verMatch.Success)
        {
            var candidate = verMatch.Groups["ver"].Value.Trim();
            if (!string.IsNullOrEmpty(candidate) && !candidate.Contains('%'))
            {
                version = candidate;
                return true;
            }
        }

        return false;
    }

    private static readonly Regex SafePackageNameRegex = new(
        @"^[a-zA-Z0-9_\-+.]+$",
        RegexOptions.Compiled);

    private static readonly Regex SafeVersionRegex = new(
        @"^(?:\d+:)?[a-zA-Z0-9_.+~^]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates whether a package name conforms to safe RPM package naming rules.
    /// Rejects path traversal (.. or /), control characters, spaces, and unexpected symbols.
    /// </summary>
    public static bool IsValidPackageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        if (trimmed.Contains("..") || trimmed.Contains('/') || trimmed.Contains('\\'))
            return false;

        return SafePackageNameRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// Validates whether a version string conforms to safe RPM version rules.
    /// Rejects newlines, carriage returns, spaces, slashes, hyphens, and unexpected symbols.
    /// </summary>
    public static bool IsValidTargetVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var trimmed = version.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('-'))
            return false;

        return SafeVersionRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// Deduce branch name for a Rust crate package given the directory name and crate name.
    /// Handles underscore to hyphen normalization (e.g. rand_core -> rust-rand-core-0.10).
    /// Returns the deduced branch (e.g. "0.10") or empty string if no specific branch constraint.
    /// </summary>
    public static string DeduceBranch(string? dirName, string crateName)
    {
        if (string.IsNullOrWhiteSpace(dirName) || string.IsNullOrWhiteSpace(crateName))
            return string.Empty;

        var normalizedCrate = crateName.Trim().Replace('_', '-');
        var prefix = $"rust-{normalizedCrate}-";

        if (dirName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return dirName.Substring(prefix.Length);
        }

        return string.Empty;
    }

    /// <summary>
    /// Checks whether the target version is compatible with the branch name deduced from the directory.
    /// E.g. branch "0.6" requires target "0.6.x"; branch "1" requires target "1.x".
    /// </summary>
    public static bool IsVersionCompatibleWithBranch(string targetVersion, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return true;

        if (string.Equals(targetVersion, branch, StringComparison.OrdinalIgnoreCase))
            return true;

        var branchPrefix = branch.EndsWith('.') ? branch : branch + ".";
        return targetVersion.StartsWith(branchPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips build metadata and vendor suffixes (+suffix) from a version string.
    /// E.g. "0.4.88+curl-8.19.0" -> "0.4.88".
    /// </summary>
    public static string StripVersionSuffix(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var plusIdx = version.IndexOf('+');
        return plusIdx >= 0 ? version.Substring(0, plusIdx).Trim() : version.Trim();
    }

    /// <summary>
    /// Evaluates if the current version in a SPEC already satisfies, matches, or exceeds the target version,
    /// ignoring '+suffix' vendor metadata.
    /// </summary>
    public static bool IsVersionAtOrPastTarget(string specContent, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(specContent) || string.IsNullOrWhiteSpace(targetVersion))
            return false;

        var cleanTarget = StripVersionSuffix(targetVersion);

        // Check Version: tag
        var verMatch = VersionRegex.Match(specContent);
        if (verMatch.Success)
        {
            var cleanVer = StripVersionSuffix(verMatch.Groups["ver"].Value);
            if (CompareRpmVersions(cleanVer, cleanTarget) >= 0)
                return true;
        }

        // Check %global full_version tag
        var fullVerMatch = FullVersionRegex.Match(specContent);
        if (fullVerMatch.Success)
        {
            var cleanFullVer = StripVersionSuffix(fullVerMatch.Groups["ver"].Value);
            if (CompareRpmVersions(cleanFullVer, cleanTarget) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates if the current version in a SPEC already satisfies or matches the target version,
    /// ignoring '+suffix' vendor metadata. Kept for backwards compatibility.
    /// </summary>
    public static bool IsVersionAtTarget(string specContent, string targetVersion)
        => IsVersionAtOrPastTarget(specContent, targetVersion);

    /// <summary>
    /// Compares two RPM version strings according to standard rpmvercmp rules.
    /// Handles digits, alpha segments, '~' (tilde sorts before anything), and '^' (caret sorts after base version).
    /// Returns 1 if v1 > v2, -1 if v1 &lt; v2, and 0 if equal.
    /// </summary>
    public static int CompareRpmVersions(string? v1, string? v2)
    {
        var (epoch1, rawVer1) = SplitEpoch(StripVersionSuffix(v1 ?? string.Empty));
        var (epoch2, rawVer2) = SplitEpoch(StripVersionSuffix(v2 ?? string.Empty));

        var epochCmp = epoch1.CompareTo(epoch2);
        if (epochCmp != 0)
            return epochCmp > 0 ? 1 : -1;

        var str1 = rawVer1;
        var str2 = rawVer2;

        if (string.Equals(str1, str2, StringComparison.Ordinal))
            return 0;

        int i1 = 0, i2 = 0;
        while (i1 < str1.Length || i2 < str2.Length)
        {
            // Skip non-alphanumeric, non-tilde, non-caret separators (e.g. '.', '_', '-')
            while (i1 < str1.Length && !char.IsLetterOrDigit(str1[i1]) && str1[i1] != '~' && str1[i1] != '^')
                i1++;
            while (i2 < str2.Length && !char.IsLetterOrDigit(str2[i2]) && str2[i2] != '~' && str2[i2] != '^')
                i2++;

            // Handle ~ (tilde)
            if ((i1 < str1.Length && str1[i1] == '~') || (i2 < str2.Length && str2[i2] == '~'))
            {
                if (i1 >= str1.Length || str1[i1] != '~') return 1; // v2 has ~, v1 does not -> v1 is newer
                if (i2 >= str2.Length || str2[i2] != '~') return -1; // v1 has ~, v2 does not -> v2 is newer
                i1++; i2++;
                continue;
            }

            // Handle ^ (caret)
            if ((i1 < str1.Length && str1[i1] == '^') || (i2 < str2.Length && str2[i2] == '^'))
            {
                if (i1 >= str1.Length) return -1; // v1 ended, v2 has ^ -> v2 is newer
                if (i2 >= str2.Length) return 1;  // v2 ended, v1 has ^ -> v1 is newer
                if (str1[i1] == '^' && str2[i2] != '^') return -1;
                if (str1[i1] != '^' && str2[i2] == '^') return 1;
                i1++; i2++;
                continue;
            }

            if (i1 >= str1.Length) return i2 < str2.Length ? -1 : 0;
            if (i2 >= str2.Length) return 1;

            bool isDigit1 = char.IsDigit(str1[i1]);
            bool isDigit2 = char.IsDigit(str2[i2]);

            // Number segment vs letter segment
            if (isDigit1 && !isDigit2) return 1;
            if (!isDigit1 && isDigit2) return -1;

            if (isDigit1)
            {
                int start1 = i1;
                while (i1 < str1.Length && char.IsDigit(str1[i1])) i1++;
                int start2 = i2;
                while (i2 < str2.Length && char.IsDigit(str2[i2])) i2++;

                var seg1 = str1.Substring(start1, i1 - start1).TrimStart('0');
                var seg2 = str2.Substring(start2, i2 - start2).TrimStart('0');

                if (seg1.Length > seg2.Length) return 1;
                if (seg1.Length < seg2.Length) return -1;
                int cmp = string.CompareOrdinal(seg1, seg2);
                if (cmp != 0) return cmp > 0 ? 1 : -1;
            }
            else
            {
                int start1 = i1;
                while (i1 < str1.Length && char.IsLetter(str1[i1])) i1++;
                int start2 = i2;
                while (i2 < str2.Length && char.IsLetter(str2[i2])) i2++;

                var seg1 = str1.Substring(start1, i1 - start1);
                var seg2 = str2.Substring(start2, i2 - start2);

                int cmp = string.CompareOrdinal(seg1, seg2);
                if (cmp != 0) return cmp > 0 ? 1 : -1;
            }
        }

        return 0;
    }

    private static (long Epoch, string Version) SplitEpoch(string input)
    {
        var colonIdx = input.IndexOf(':');
        if (colonIdx > 0 && long.TryParse(input[..colonIdx], out var epoch) && epoch >= 0)
        {
            return (epoch, input[(colonIdx + 1)..]);
        }

        return (0, input);
    }

    private enum MajorTokenKind
    {
        Numeric,
        DelimitedAlpha
    }

    private readonly record struct MajorToken(MajorTokenKind Kind, long NumericValue, string? AlphaValue);

    /// <summary>
    /// Checks whether an upgrade crosses a major version boundary.
    /// Compares RPM epochs first, then structured major tokens (leading numeric runs with
    /// CalVer <c>YYYYMMDD</c>/<c>YYYYMM</c> normalization such as <c>2026c</c> or <c>3_508</c>,
    /// or delimited alpha tokens like <c>B.02.20</c>), failing closed (<c>true</c>) whenever
    /// token kinds differ or either version is opaque and the stripped versions differ.
    /// </summary>
    public static bool IsCrossMajorVersion(string currentVersion, string targetVersion)
    {
        var cleanCurrent = StripVersionSuffix(currentVersion);
        var cleanTarget = StripVersionSuffix(targetVersion);

        if (string.IsNullOrWhiteSpace(cleanCurrent) || string.IsNullOrWhiteSpace(cleanTarget))
        {
            return !string.Equals(cleanCurrent, cleanTarget, StringComparison.OrdinalIgnoreCase);
        }

        var (currentEpoch, currentBody) = SplitEpoch(cleanCurrent);
        var (targetEpoch, targetBody) = SplitEpoch(cleanTarget);
        if (currentEpoch != targetEpoch)
            return true;

        if (TryExtractMajorToken(currentBody, out var currentMajor) &&
            TryExtractMajorToken(targetBody, out var targetMajor))
        {
            if (currentMajor.Kind != targetMajor.Kind)
                return true;

            return currentMajor.Kind == MajorTokenKind.Numeric
                ? currentMajor.NumericValue != targetMajor.NumericValue
                : !string.Equals(currentMajor.AlphaValue, targetMajor.AlphaValue, StringComparison.OrdinalIgnoreCase);
        }

        return !string.Equals(cleanCurrent, cleanTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractMajorToken(string version, out MajorToken token)
    {
        token = default;
        var span = version.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return false;
        }

        // Strip a single leading 'v'/'V' only when immediately followed by an ASCII digit (e.g. v1.2.3 -> 1.2.3)
        if (span.Length >= 2 && (span[0] == 'v' || span[0] == 'V') && char.IsAsciiDigit(span[1]))
        {
            span = span[1..];
        }

        if (char.IsAsciiDigit(span[0]))
        {
            int digitLen = 0;
            while (digitLen < span.Length && char.IsAsciiDigit(span[digitLen]))
            {
                digitLen++;
            }

            if (!long.TryParse(span[..digitLen], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            {
                return false;
            }

            // CalVer support: 8-digit date (YYYYMMDD, e.g. 20260526 -> 2026) and 6-digit date (YYYYMM, e.g. 202602 -> 2026)
            if (major >= 19900000 && major <= 21001231)
            {
                major /= 10000;
            }
            else if (major >= 199000 && major <= 210012)
            {
                major /= 100;
            }

            token = new MajorToken(MajorTokenKind.Numeric, major, null);
            return true;
        }

        if (char.IsAsciiLetter(span[0]))
        {
            int letterLen = 0;
            while (letterLen < span.Length && char.IsAsciiLetter(span[letterLen]))
            {
                letterLen++;
            }

            if (letterLen < span.Length && (span[letterLen] == '.' || span[letterLen] == '_'))
            {
                token = new MajorToken(MajorTokenKind.DelimitedAlpha, 0, span[..letterLen].ToString());
                return true;
            }

            return false;
        }

        return false;
    }
}


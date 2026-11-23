using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace abaci_bot.Services;

/// <summary>
/// Default implementation of <see cref="IRemoteAssetBackfiller"/>.
/// Hides the complexity of GitHub artifact APIs, zip decompression, diff parsing, and spec mutations.
/// </summary>
public class RemoteAssetBackfiller : IRemoteAssetBackfiller
{
    private static readonly Regex SafeSpecPathRegex = new(
        @"^SPECS/[^/\\\s]+/[^/\\\s]+\.spec$",
        RegexOptions.Compiled);

    private readonly IGitHubService _gitHubService;
    private readonly ILogger<RemoteAssetBackfiller>? _logger;

    public RemoteAssetBackfiller(
        IGitHubService gitHubService,
        ILogger<RemoteAssetBackfiller>? logger = null)
    {
        _gitHubService = gitHubService ?? throw new ArgumentNullException(nameof(gitHubService));
        _logger = logger;
    }

    internal static bool IsSafeSpecPath(string? specPath)
    {
        if (string.IsNullOrWhiteSpace(specPath))
            return false;

        var normalized = specPath.Trim().Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal))
            return false;

        return SafeSpecPathRegex.IsMatch(normalized);
    }

    internal static string ReplaceFirstExactLine(string content, string oldLine, string newLine)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(oldLine))
            return content;

        var trimmedOld = oldLine.Trim();
        var segments = Regex.Split(content, @"(\r\n|\r|\n)");
        for (int i = 0; i < segments.Length; i += 2)
        {
            if (string.Equals(segments[i].Trim(), trimmedOld, StringComparison.Ordinal))
            {
                var leadingLen = segments[i].Length - segments[i].TrimStart().Length;
                var trailingLen = segments[i].Length - segments[i].TrimEnd().Length;
                var prefix = leadingLen > 0 ? segments[i][..leadingLen] : string.Empty;
                var suffix = trailingLen > 0 ? segments[i][^trailingLen..] : string.Empty;
                segments[i] = string.Concat(prefix, newLine.Trim(), suffix);
                return string.Concat(segments);
            }
        }

        return content;
    }

    public async Task<bool> TryBackfillAsync(
        string owner,
        string repo,
        string branchName,
        int prNumber,
        long runId,
        CancellationToken ct = default)
    {
        try
        {
            var artifacts = await _gitHubService.ListWorkflowArtifactsAsync(owner, repo, runId, ct);
            var diffArtifact = artifacts.FirstOrDefault(a => a.Name != null && a.Name.Contains("remoteasset", StringComparison.OrdinalIgnoreCase));
            if (diffArtifact == null)
                return false;

            _logger?.LogInformation("Found remoteasset artifact '{Name}' (ID: {Id}) for PR #{PrNumber}; downloading diff to backfill",
                diffArtifact.Name, diffArtifact.Id, prNumber);

            await using var stream = await _gitHubService.DownloadArtifactAsync(owner, repo, diffArtifact.Id, ct);
            var diffContent = await ArtifactDiffExtractor.ExtractDiffContentAsync(stream, ct);
            if (string.IsNullOrWhiteSpace(diffContent))
                return false;

            bool backfilledAny = false;

            // Extract file path from diff header and validate strict SPECS/<pkg>/<file>.spec path without traversal
            var fileMatches = Regex.Matches(diffContent, @"^\+\+\+\s+(?:b/)?(?<path>\S+)", RegexOptions.Multiline);
            foreach (Match fileMatch in fileMatches)
            {
                var specPath = fileMatch.Groups["path"].Value.Trim();
                if (!IsSafeSpecPath(specPath))
                {
                    _logger?.LogWarning("Rejected unsafe RemoteAsset spec path '{Path}' in diff artifact for PR #{PrNumber}", specPath, prNumber);
                    continue;
                }

                var changes = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(diffContent, specPath);
                if (changes.Count == 0)
                    continue;

                string currentSpec;
                try
                {
                    currentSpec = await _gitHubService.GetFileContentAsync(owner, repo, specPath, branchName, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to load current spec {Path} from branch {Branch}", specPath, branchName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(currentSpec))
                    continue;

                var updatedSpec = currentSpec;
                foreach (var (oldLine, newLine) in changes)
                {
                    updatedSpec = ReplaceFirstExactLine(updatedSpec, oldLine, newLine);
                }

                if (!string.Equals(updatedSpec, currentSpec, StringComparison.Ordinal))
                {
                    _logger?.LogInformation("Backfilling updated #!RemoteAsset checksums to branch {Branch} for PR #{PrNumber}", branchName, prNumber);
                    await _gitHubService.UpdateFileContentAsync(
                        owner, repo, specPath, "chore: update #!RemoteAsset sha256 checksums", updatedSpec, branchName, ct);
                    backfilledAny = true;
                }
            }

            return backfilledAny;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to backfill remoteasset diff artifact for PR #{PrNumber}", prNumber);
            return false;
        }
    }
}

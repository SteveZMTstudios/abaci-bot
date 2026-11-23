using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Service performing Post-Merge OBS build verification for merged abaci-bot update PRs.
/// Validates that OBS _service has synchronized the merge commit SHA before asserting _result.
/// </summary>
public class ObsPostMergeVerifier
{
    private readonly IGitHubService _gitHubService;
    private readonly IObsBuildService _obsBuildService;
    private readonly PackageUpdateOptions _options;
    private readonly ILogger<ObsPostMergeVerifier>? _logger;
    private readonly IPagedIssueSearchGateway? _pagedIssueSearch;
    private readonly IObsBuildFreshnessGateway? _freshnessGateway;
    private readonly ISpecCatalog? _specCatalog;

    private static readonly Regex PackageTitleRegex = new(
        @"^SPECS:\s*bump\s+(?<pkg>\S+)(?:\s+\S+\s*->\s*(?<ver>\S+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedIssueTitleRegex = new(
        @"^\[Auto-Update:\s*Blocked\]\s*Post-merge OBS build failure\s+on\s+package\s+(?<pkg>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedIssueMarkerRegex = new(
        @"<!--\s*abaci-obs-blocked:\s*(?<pkg>\S+)\s*-->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedIssueVersionRegex = new(
        @"^-\s*\*\*Package Version\*\*:\s*`(?<ver>[^`\r\n]+)`",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedIssuePrNumberRegex = new(
        @"after\s+merging\s+PR\s+#(?<pr>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string BuildObsBlockedIssueTitle(string packageName) =>
        $"[{GitHubLabels.AutoUpdateBlocked}] Post-merge OBS build failure on package {packageName}";

    public ObsPostMergeVerifier(
        IGitHubService gitHubService,
        IObsBuildService obsBuildService,
        IOptions<PackageUpdateOptions> options,
        ILogger<ObsPostMergeVerifier>? logger = null,
        IPagedIssueSearchGateway? pagedIssueSearch = null,
        IObsBuildFreshnessGateway? freshnessGateway = null,
        ISpecCatalog? specCatalog = null)
    {
        _gitHubService = gitHubService;
        _obsBuildService = obsBuildService;
        _options = options.Value;
        _logger = logger;
        _pagedIssueSearch = pagedIssueSearch ?? gitHubService as IPagedIssueSearchGateway;
        _freshnessGateway = freshnessGateway ?? obsBuildService as IObsBuildFreshnessGateway;
        _specCatalog = specCatalog;
    }

    private static readonly string[] BlockedLabels = { GitHubLabels.AutoUpdateBlocked };

    public async Task VerifyMergedPullRequestsAsync(string targetRepo, CancellationToken ct = default)
    {
        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
            return;

        var owner = parts[0].Trim();
        var repo = parts[1].Trim();

        var checkSince = DateTimeOffset.UtcNow.AddDays(-7);

        IReadOnlyList<Octokit.PullRequest> mergedPrs;
        try
        {
            mergedPrs = await _gitHubService.SearchMergedPullRequestsAsync(owner, repo, string.Empty, checkSince, ct)
                        ?? Array.Empty<Octokit.PullRequest>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to search merged pull requests for {TargetRepo}", targetRepo);
            mergedPrs = Array.Empty<Octokit.PullRequest>();
        }

        Dictionary<string, Issue>? openBlockedByPackage = null;
        var loadedOpenBlockedIssues = false;
        var isBlockedIndexTruncated = false;
        var handledPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (mergedPrs.Count > 0)
        {
            _logger?.LogInformation("Evaluating {Count} merged bot PRs for OBS build synchronization", mergedPrs.Count);
        }

        foreach (var pr in mergedPrs)
        {
            if (ct.IsCancellationRequested)
                break;

            var match = PackageTitleRegex.Match(pr.Title ?? string.Empty);
            if (!match.Success)
                continue;

            var packageName = match.Groups["pkg"].Value.Trim();
            var targetVersion = match.Groups["ver"].Success ? match.Groups["ver"].Value.Trim() : null;
            var mergeSha = pr.MergeCommitSha;
            if (string.IsNullOrWhiteSpace(mergeSha))
                continue;

            var serviceRevision = await _obsBuildService.GetPackageServiceRevisionAsync(
                _options.Obs.Project, packageName, ct);

            if (string.IsNullOrEmpty(serviceRevision))
            {
                _logger?.LogDebug("OBS _service not found or no revision for {Package}", packageName);
                continue;
            }

            if (!string.Equals(serviceRevision, mergeSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("OBS _service revision ({ObsRev}) does not match PR merge SHA ({MergeSha}) for {Package}; pending sync",
                    serviceRevision, mergeSha, packageName);
                continue;
            }

            // Revision matches: OBS has synchronized this merged commit! Now check _result
            var buildResult = await _obsBuildService.GetPackageBuildResultAsync(
                _options.Obs.Project, packageName, _options.Obs.TargetRepository, _options.Obs.TargetArch, ct);

            _logger?.LogInformation("OBS build result for merged PR #{PrNumber} ({Package}): {Status} (dirty={Dirty})",
                pr.Number, packageName, buildResult.Code, buildResult.IsDirty);

            if (buildResult.Code == ObsStatusCode.Succeeded)
            {
                var isFreshSuccess = await VerifyObsSuccessIsFreshAsync(buildResult, pr.MergedAt, ct);
                if (!isFreshSuccess)
                {
                    _logger?.LogInformation(
                        "OBS _result reported Succeeded for merged PR #{PrNumber} ({Package}), but build is dirty or not yet confirmed after merge; waiting for fresh job",
                        pr.Number, packageName);
                    handledPackages.Add(packageName);
                    continue;
                }

                handledPackages.Add(packageName);
                if (!_options.DryRun)
                {
                    if (pr.Labels != null && pr.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            await _gitHubService.RemoveLabelAsync(owner, repo, pr.Number, GitHubLabels.AutoUpdateBlocked, ct);
                            _logger?.LogInformation("Removed {BlockedLabel} from merged PR #{PrNumber} after successful OBS build",
                                GitHubLabels.AutoUpdateBlocked, pr.Number);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to remove blocked label from PR #{Number}", pr.Number);
                        }
                    }

                    await TryReclaimMergedBranchAsync(owner, repo, pr, "after successful OBS build verification", ct);

                    (loadedOpenBlockedIssues, openBlockedByPackage, isBlockedIndexTruncated) = await EnsureOpenBlockedIndexLoadedAsync(
                        owner, repo, loadedOpenBlockedIssues, openBlockedByPackage, isBlockedIndexTruncated, ct);
                    await CloseObsBlockedTrackingIssueIfOpenAsync(
                        owner, repo, packageName,
                        $"Post-merge OBS build succeeded on PR #{pr.Number} (commit `{mergeSha}`). Closing tracking issue and resuming automated updates for `{packageName}`.",
                        openBlockedByPackage, ct);
                }
            }
            else
            {
                handledPackages.Add(packageName);
                if (_options.Obs.RequireObsSuccessAfterMerge &&
                    (buildResult.Code == ObsStatusCode.Failed || buildResult.Code == ObsStatusCode.Unresolvable))
                {
                    _logger?.LogWarning("Merged PR #{PrNumber} failed post-merge OBS gate requirement ({Status})", pr.Number, buildResult.Code);
                    if (!_options.DryRun)
                    {
                        try
                        {
                            await _gitHubService.AddLabelsAsync(owner, repo, pr.Number, BlockedLabels, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to apply blocked label to merged PR #{Number}", pr.Number);
                        }

                        (loadedOpenBlockedIssues, openBlockedByPackage, isBlockedIndexTruncated) = await EnsureOpenBlockedIndexLoadedAsync(
                            owner, repo, loadedOpenBlockedIssues, openBlockedByPackage, isBlockedIndexTruncated, ct);
                        await EnsureObsBlockedTrackingIssueAsync(
                            owner, repo, packageName, targetVersion, pr.Number, mergeSha, buildResult,
                            openBlockedByPackage, isBlockedIndexTruncated, ct);
                    }
                }

                // If PR has been merged past TTL, reclaim branch even if OBS build failed/remained unresolved
                if (!_options.DryRun && pr.MergedAt.HasValue &&
                    pr.MergedAt.Value < DateTimeOffset.UtcNow.AddDays(-_options.FailedPrTtlDays))
                {
                    await TryReclaimMergedBranchAsync(owner, repo, pr, "after reaching merged TTL", ct);
                }
            }

            var obsEndpoint = FormatObsPublicEndpoint(_options.Obs.BaseUrl);
            var comment = $"""
                ### OBS Build Verification
                - **Project**: `{buildResult.Project}`
                - **Package**: `{buildResult.Package}`
                - **Repository / Arch**: `{buildResult.Repository}` / `{buildResult.Arch}`
                - **Status**: `{buildResult.Code}` (`{buildResult.RawCode}`)
                - **Synchronized Commit**: `{mergeSha}`

                > Verified via `{obsEndpoint}` read-model.
                """;

            Octokit.IssueComment? existingComment = null;
            try
            {
                var existingComments = await _gitHubService.GetIssueCommentsAsync(owner, repo, pr.Number, ct);
                existingComment = existingComments.FirstOrDefault(c => c.Body != null && c.Body.Contains("### OBS Build Verification"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Failed to check existing comments on PR #{PrNumber}", pr.Number);
            }

            if (existingComment != null)
            {
                // If comment already reflects current status and commit, skip
                if (existingComment.Body != null &&
                    existingComment.Body.Contains($"- **Status**: `{buildResult.Code}`") &&
                    existingComment.Body.Contains($"- **Synchronized Commit**: `{mergeSha}`"))
                {
                    _logger?.LogDebug("OBS build verification comment is already up-to-date on PR #{PrNumber}; skipping", pr.Number);
                    continue;
                }

                if (!_options.DryRun)
                {
                    _logger?.LogInformation("Updating OBS build verification comment on PR #{PrNumber} to status {Status}", pr.Number, buildResult.Code);
                    try
                    {
                        await _gitHubService.UpdateCommentAsync(owner, repo, existingComment.Id, comment, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogDebug(ex, "Failed to update OBS build verification comment on PR #{PrNumber}", pr.Number);
                    }
                }
                continue;
            }

            if (!_options.DryRun)
            {
                try
                {
                    await _gitHubService.CreateCommentAsync(owner, repo, pr.Number, comment, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to post OBS build verification comment on PR #{PrNumber}", pr.Number);
                }
            }
        }

        if (_options.Obs.RequireObsSuccessAfterMerge && !ct.IsCancellationRequested)
        {
            await ReconcileOpenObsBlockedIssuesAsync(
                owner,
                repo,
                handledPackages,
                loadedOpenBlockedIssues,
                openBlockedByPackage,
                isBlockedIndexTruncated,
                ct);
        }
    }

    private async Task ReconcileOpenObsBlockedIssuesAsync(
        string owner,
        string repo,
        HashSet<string> handledPackages,
        bool loadedOpenBlockedIssues,
        Dictionary<string, Issue>? openBlockedByPackage,
        bool isBlockedIndexTruncated,
        CancellationToken ct)
    {
        if (!_options.DryRun)
        {
            (loadedOpenBlockedIssues, openBlockedByPackage, _) = await EnsureOpenBlockedIndexLoadedAsync(
                owner, repo, loadedOpenBlockedIssues, openBlockedByPackage, isBlockedIndexTruncated, ct);
        }

        if (openBlockedByPackage == null || openBlockedByPackage.Count == 0)
        {
            return;
        }

        foreach (var (packageName, openIssue) in openBlockedByPackage.ToList())
        {
            if (ct.IsCancellationRequested)
                break;

            if (handledPackages.Contains(packageName))
                continue;

            // Resolution path 1: main branch was upgraded beyond the failing package version
            if (_specCatalog != null && TryExtractBlockedVersion(openIssue.Body, out var blockedVersion))
            {
                try
                {
                    var spec = await _specCatalog.GetSpecAsync(packageName, ct);
                    var currentMainVersion = spec != null ? ExtractCurrentSpecVersion(spec.Content) : null;
                    if (!string.IsNullOrWhiteSpace(currentMainVersion) &&
                        UpdateAdmission.CompareRpmVersions(currentMainVersion, blockedVersion) > 0)
                    {
                        var upgradeComment = $"Base branch `main` has already been updated for `{packageName}` " +
                                             $"(`{currentMainVersion}` > `{blockedVersion}`). Closing OBS blocked tracking issue and resuming automated updates.";
                        if (await CloseObsBlockedTrackingIssueIfOpenAsync(owner, repo, packageName, upgradeComment, openBlockedByPackage, ct) &&
                            !_options.DryRun)
                        {
                            await TryReclaimBranchFromBlockedIssueAsync(owner, repo, openIssue, "after main branch upgrade past failed OBS build", ct);
                        }
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to inspect main branch version for OBS blocked package {Package}", packageName);
                }
            }

            // Resolution path 2: OBS subsequently produced a verified fresh Succeeded build even beyond the 7-day PR window
            ObsPackageBuildResult buildResult;
            try
            {
                buildResult = await _obsBuildService.GetPackageBuildResultAsync(
                    _options.Obs.Project, packageName, _options.Obs.TargetRepository, _options.Obs.TargetArch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Failed to re-query OBS status for blocked package {Package}", packageName);
                continue;
            }

            DateTimeOffset? blockedSince = openIssue.CreatedAt != default ? openIssue.CreatedAt : null;
            if (await VerifyObsSuccessIsFreshAsync(buildResult, blockedSince, ct))
            {
                var obsComment = $"OBS build verification succeeded on subsequent check (`{buildResult.Project}` {buildResult.Repository}/{buildResult.Arch}). " +
                                 $"Closing tracking issue and resuming automated updates for `{packageName}`.";
                if (await CloseObsBlockedTrackingIssueIfOpenAsync(owner, repo, packageName, obsComment, openBlockedByPackage, ct) &&
                    !_options.DryRun)
                {
                    await TryReclaimBranchFromBlockedIssueAsync(owner, repo, openIssue, "after delayed OBS build recovery", ct);
                }
            }
        }
    }

    private async Task<bool> VerifyObsSuccessIsFreshAsync(
        ObsPackageBuildResult buildResult,
        DateTimeOffset? referenceTime,
        CancellationToken ct)
    {
        if (buildResult.Code != ObsStatusCode.Succeeded || buildResult.IsDirty)
        {
            return false;
        }

        if (_freshnessGateway == null)
        {
            return true;
        }

        ObsPackageBuildFreshness? freshness;
        try
        {
            freshness = await _freshnessGateway.GetPackageBuildFreshnessAsync(
                buildResult.Project,
                buildResult.Package,
                buildResult.Repository,
                buildResult.Arch,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to verify OBS build freshness for {Package}", buildResult.Package);
            return false;
        }

        if (freshness == null || !freshness.IsServiceReady)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(freshness.SourceMd5) ||
            string.IsNullOrWhiteSpace(freshness.LatestJobSrcMd5) ||
            !string.Equals(freshness.SourceMd5, freshness.LatestJobSrcMd5, StringComparison.OrdinalIgnoreCase) ||
            freshness.LatestJobCode != ObsStatusCode.Succeeded)
        {
            return false;
        }

        if (referenceTime.HasValue)
        {
            if (!freshness.LatestJobStartTime.HasValue ||
                freshness.LatestJobStartTime.Value < referenceTime.Value.AddMinutes(-2))
            {
                return false;
            }
        }

        return true;
    }

    private async Task TryReclaimMergedBranchAsync(string owner, string repo, PullRequest pr, string reason, CancellationToken ct)
    {
        var branchName = pr.Head?.Ref;
        var headRepo = pr.Head?.Repository?.FullName;
        var isSameRepo = headRepo != null && string.Equals(headRepo, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase);
        var prefix = !string.IsNullOrEmpty(_options.BranchPrefix) ? _options.BranchPrefix : "autoupdate/";

        if (isSameRepo && !string.IsNullOrWhiteSpace(branchName) && branchName.StartsWith(prefix, StringComparison.Ordinal))
        {
            // 1. Check whether a newer open PR is already reusing this branch
            var openPr = await _gitHubService.FindOpenPullRequestByHeadAsync(owner, repo, branchName, ct);
            if (openPr != null)
            {
                _logger?.LogDebug("Skipping branch {Branch} deletion: active open PR #{OpenPr} is currently reusing it", branchName, openPr.Number);
                return;
            }

            // 2. Check whether the branch reference has moved forward past the merged PR's head SHA
            var currentSha = await _gitHubService.GetRefShaAsync(owner, repo, branchName, ct);
            if (!string.IsNullOrEmpty(pr.Head?.Sha) && !string.Equals(currentSha, pr.Head.Sha, StringComparison.Ordinal))
            {
                _logger?.LogDebug("Skipping branch {Branch} deletion: branch head has moved (current: {CurrentSha}, merged PR: {PrSha})",
                    branchName, currentSha, pr.Head.Sha);
                return;
            }

            try
            {
                await _gitHubService.DeleteRefAsync(owner, repo, branchName, ct);
                _logger?.LogInformation("Reclaimed merged branch {Branch} for PR #{PrNumber} {Reason}",
                    branchName, pr.Number, reason);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to reclaim merged branch {Branch} for PR #{PrNumber}", branchName, pr.Number);
            }
        }
    }

    private static string FormatObsPublicEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "OBS /public";
        }

        var trimmed = baseUrl.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? $"{uri.Authority}/public"
            : $"{trimmed.TrimEnd('/')}/public";
    }

    private async Task<(bool Loaded, Dictionary<string, Issue>? Index, bool IsTruncated)> EnsureOpenBlockedIndexLoadedAsync(
        string owner,
        string repo,
        bool alreadyLoaded,
        Dictionary<string, Issue>? existing,
        bool alreadyTruncated,
        CancellationToken ct)
    {
        if (alreadyLoaded)
        {
            return (true, existing, alreadyTruncated);
        }

        try
        {
            IReadOnlyList<Issue> issues;
            bool isTruncated;
            if (_pagedIssueSearch != null)
            {
                var page = await _pagedIssueSearch.SearchOpenIssuesByLabelAsync(owner, repo, GitHubLabels.AutoUpdateBlocked, ct);
                issues = page.Items;
                isTruncated = page.IsTruncated;
            }
            else
            {
                issues = await _gitHubService.SearchStaleCandidatesAsync(
                    owner,
                    repo,
                    DateTimeOffset.UtcNow.AddDays(1),
                    isPr: false,
                    labelFilter: $"label:\"{GitHubLabels.AutoUpdateBlocked}\" is:issue is:open",
                    ct: ct);
                isTruncated = issues != null && issues.Count >= 500;
            }

            if (isTruncated)
            {
                _logger?.LogWarning(
                    "Open '{BlockedLabel}' issue index in {Owner}/{Repo} is truncated ({Retrieved} items retrieved); existing indexed issues will be updated/closed, while creating new unindexed blocked tracking issues is deferred fail-closed",
                    GitHubLabels.AutoUpdateBlocked, owner, repo, issues?.Count ?? 0);
            }

            var index = new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    if (issue != null && TryExtractObsBlockedPackage(issue, out var pkg))
                    {
                        index.TryAdd(pkg, issue);
                    }
                }
            }

            return (true, index, isTruncated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to load open '{BlockedLabel}' issues in {Owner}/{Repo}",
                GitHubLabels.AutoUpdateBlocked, owner, repo);
            return (true, null, false);
        }
    }

    private async Task EnsureObsBlockedTrackingIssueAsync(
        string owner,
        string repo,
        string packageName,
        string? targetVersion,
        int prNumber,
        string mergeSha,
        ObsPackageBuildResult buildResult,
        Dictionary<string, Issue>? openBlockedByPackage,
        bool isIndexTruncated,
        CancellationToken ct)
    {
        if (openBlockedByPackage == null)
        {
            return;
        }

        if (openBlockedByPackage.ContainsKey(packageName))
        {
            return;
        }

        if (isIndexTruncated)
        {
            _logger?.LogWarning(
                "Deferring creation of new OBS blocked tracking issue for {Package} because the open '{BlockedLabel}' issue index was truncated",
                packageName, GitHubLabels.AutoUpdateBlocked);
            return;
        }

        var issueTitle = BuildObsBlockedIssueTitle(packageName);
        var exemptLabel = !string.IsNullOrWhiteSpace(_options.GiveUpIssueLabel)
            ? _options.GiveUpIssueLabel
            : GitHubLabels.StatusStaleExempt;
        var versionLine = !string.IsNullOrWhiteSpace(targetVersion)
            ? $"\n- **Package Version**: `{targetVersion}`"
            : string.Empty;
        var issueBody = $"""
            ## Post-Merge OBS Build Gate Blocked
            Package `{packageName}` failed post-merge Open Build Service verification after merging PR #{prNumber}.

            - **Project / Target**: `{buildResult.Project}` (`{buildResult.Repository}` / `{buildResult.Arch}`)
            - **Status**: `{buildResult.Code}` (`{buildResult.RawCode}`)
            - **Synchronized Commit**: `{mergeSha}`{versionLine}
            - **Resolution**: Automated updates for `{packageName}` are paused until OBS reports a fresh `Succeeded` build, `main` upgrades past this version, or a maintainer closes this tracking issue.

            <!-- abaci-obs-blocked: {packageName} -->
            """;

        try
        {
            var created = await _gitHubService.CreateIssueAsync(
                owner,
                repo,
                issueTitle,
                issueBody,
                new[] { GitHubLabels.AutoUpdateBlocked, exemptLabel },
                ct);
            if (created != null)
            {
                openBlockedByPackage.TryAdd(packageName, created);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to create OBS blocked tracking issue for package {Package}", packageName);
        }
    }

    private async Task<bool> CloseObsBlockedTrackingIssueIfOpenAsync(
        string owner,
        string repo,
        string packageName,
        string closingComment,
        Dictionary<string, Issue>? openBlockedByPackage,
        CancellationToken ct)
    {
        if (openBlockedByPackage == null || !openBlockedByPackage.TryGetValue(packageName, out var openIssue))
        {
            return false;
        }

        try
        {
            await _gitHubService.CreateCommentAsync(owner, repo, openIssue.Number, closingComment, ct);
            await _gitHubService.CloseIssueAsync(owner, repo, openIssue.Number, ct);
            openBlockedByPackage.Remove(packageName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to close recovered OBS tracking issue #{Number} for {Package}", openIssue.Number, packageName);
            return false;
        }
    }

    private async Task TryReclaimBranchFromBlockedIssueAsync(
        string owner,
        string repo,
        Issue blockedIssue,
        string reason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blockedIssue.Body))
        {
            return;
        }

        var match = BlockedIssuePrNumberRegex.Match(blockedIssue.Body);
        if (!match.Success ||
            !int.TryParse(
                match.Groups["pr"].Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var prNumber) ||
            prNumber <= 0)
        {
            return;
        }

        try
        {
            var mergedPr = await _gitHubService.GetPullRequestAsync(owner, repo, prNumber, ct);
            if (mergedPr != null)
            {
                await TryReclaimMergedBranchAsync(owner, repo, mergedPr, reason, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to look up PR #{PrNumber} from OBS blocked issue #{IssueNumber} for branch reclamation",
                prNumber, blockedIssue.Number);
        }
    }

    private static bool TryExtractObsBlockedPackage(Issue issue, out string packageName)
    {
        if (!string.IsNullOrWhiteSpace(issue.Body))
        {
            var markerMatch = BlockedIssueMarkerRegex.Match(issue.Body);
            if (markerMatch.Success)
            {
                packageName = markerMatch.Groups["pkg"].Value.Trim();
                if (!string.IsNullOrEmpty(packageName))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(issue.Title))
        {
            var titleMatch = BlockedIssueTitleRegex.Match(issue.Title);
            if (titleMatch.Success)
            {
                packageName = titleMatch.Groups["pkg"].Value.Trim();
                if (!string.IsNullOrEmpty(packageName))
                {
                    return true;
                }
            }
        }

        packageName = string.Empty;
        return false;
    }

    private static bool TryExtractBlockedVersion(string? body, out string version)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            var match = BlockedIssueVersionRegex.Match(body);
            if (match.Success)
            {
                version = match.Groups["ver"].Value.Trim();
                if (!string.IsNullOrEmpty(version))
                {
                    return true;
                }
            }
        }

        version = string.Empty;
        return false;
    }

    private static string? ExtractCurrentSpecVersion(string? specContent) =>
        UpdateAdmission.TryExtractCurrentVersion(specContent, out var version)
            ? version
            : null;
}


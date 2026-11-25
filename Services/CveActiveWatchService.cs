using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Background orchestration worker for active CVE monitoring and vulnerability issue tracking.
/// Dispatches batch OSV scanning across package catalog, aggregates outstanding CVEs per package,
/// and closes issues automatically when subsequent scans confirm the vulnerabilities are resolved.
/// </summary>
public class CveActiveWatchService : BackgroundService
{
    internal const string HealthSourceName = "CveActiveWatch";
    internal const int MaxConsecutiveRewindsPerPackage = 3;
    private const string CursorStateKey = "cve-active-watch";
    private const string PoisonCursorStateKey = "cve-active-watch-poison";

    private readonly IGitHubService _gitHubService;
    private readonly IPagedIssueSearchGateway? _pagedIssueSearch;
    private readonly IPackageVulnerabilityScanner _scanner;
    private readonly CveActiveWatchOptions _options;
    private readonly ILogger<CveActiveWatchService>? _logger;
    private readonly ISpecCatalog? _specCatalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<(string PackageName, string Version)>>>? _catalogProvider;
    private readonly IBotStateStore? _stateStore;
    private readonly RuntimeHealthState? _healthState;
    private readonly string _cursorStateKey;
    private readonly string _poisonStateKey;
    private string? _lastWatermark;
    private string? _lastPoisonPackage;
    private int _lastPoisonRewindCount;
    private bool _poisonStateLoaded;
    private bool _hasPersistedPoisonState;

    public static string GetCursorStateKey(string? targetRepository) =>
        string.IsNullOrWhiteSpace(targetRepository)
            ? CursorStateKey
            : $"{CursorStateKey}:{targetRepository.Trim().ToLowerInvariant()}";

    public static string GetPoisonStateKey(string? targetRepository) =>
        string.IsNullOrWhiteSpace(targetRepository)
            ? PoisonCursorStateKey
            : $"{PoisonCursorStateKey}:{targetRepository.Trim().ToLowerInvariant()}";

    private static readonly Regex PackageIssueTitleRegex = new(
        @"^\[Security\]\s*Outstanding CVEs detected in\s+(?<pkg>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PackageIssueBodyMarkerRegex = new(
        @"<!--\s*abaci-cve-package:\s*(?<pkg>\S+)\s*-->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal const string UnverifiedNoticeMarker = "<!-- abaci-cve-unverified-notice -->";

    private static readonly HashSet<string> ManagedSeverityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        GitHubLabels.Severity1,
        GitHubLabels.Severity2,
        GitHubLabels.Severity3,
        GitHubLabels.Severity4
    };

    private static readonly Regex CrateNameRegex = new(
        @"^%global\s+crate_name\s+(?<name>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PypiNameRegex = new(
        @"^%global\s+(?:pypi_name|srcname)\s+(?<name>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public CveActiveWatchService(
        IGitHubService gitHubService,
        IPackageVulnerabilityScanner scanner,
        IOptions<CveActiveWatchOptions> options,
        ILogger<CveActiveWatchService>? logger = null,
        ISpecCatalog? specCatalog = null,
        Func<CancellationToken, Task<IReadOnlyList<(string PackageName, string Version)>>>? catalogProvider = null,
        IPagedIssueSearchGateway? pagedIssueSearch = null,
        IBotStateStore? stateStore = null,
        RuntimeHealthState? healthState = null)
    {
        _gitHubService = gitHubService;
        _scanner = scanner;
        _options = options.Value;
        _logger = logger;
        _specCatalog = specCatalog;
        _catalogProvider = catalogProvider;
        _pagedIssueSearch = pagedIssueSearch ?? gitHubService as IPagedIssueSearchGateway;
        _stateStore = stateStore;
        _healthState = healthState;
        _cursorStateKey = GetCursorStateKey(options.Value.TargetRepository);
        _poisonStateKey = GetPoisonStateKey(options.Value.TargetRepository);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("CveActiveWatchService background worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.TargetRepository))
                {
                    await RunScanSweepAsync(stoppingToken);
                }
                else
                {
                    _healthState?.RecordSourceOutcome(HealthSourceName, "disabled", 0);
                    _logger?.LogDebug("CveActiveWatchService is disabled or TargetRepository is unconfigured; skipping sweep");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _healthState?.RecordSourceOutcome(HealthSourceName, "failed", 0, ex.Message);
                _logger?.LogError(ex, "Unexpected error occurred during CveActiveWatchService sweep");
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours));
            await Task.Delay(interval, stoppingToken);
        }

        _logger?.LogInformation("CveActiveWatchService background worker stopping");
    }

    public async Task RunScanSweepAsync(CancellationToken ct = default)
    {
        var targetRepo = _options.TargetRepository.Trim();
        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
        {
            _logger?.LogError("Invalid TargetRepository format '{Repo}'; expected 'owner/repo'", targetRepo);
            _healthState?.RecordSourceOutcome(HealthSourceName, "failed", 0, $"Invalid TargetRepository format '{targetRepo}'");
            return;
        }

        var owner = parts[0];
        var repo = parts[1];

        _logger?.LogInformation("Starting CveActiveWatchService scan sweep for {TargetRepo} (DryRun={DryRun})", targetRepo, _options.DryRun);

        // Fail-closed rate limit snapshot check before starting expensive OSV/GitHub scans
        var rateLimit = await _gitHubService.GetRateLimitSnapshotAsync(ct);
        if (rateLimit == null || !rateLimit.CoreRemaining.HasValue || rateLimit.CoreRemaining.Value < 500)
        {
            _logger?.LogWarning("Rate limit remaining ({Remaining}) is unknown or low; deferring CVE scan sweep",
                rateLimit?.CoreRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
            _healthState?.RecordSourceOutcome(HealthSourceName, "degraded", 0, "GitHub Core API rate limit is unknown or low.");
            return;
        }
        if (!rateLimit.SearchRemaining.HasValue || rateLimit.SearchRemaining.Value < 5)
        {
            _logger?.LogWarning("GitHub Search API rate limit remaining ({Remaining}) is unknown or low; deferring CVE scan sweep",
                rateLimit.SearchRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
            _healthState?.RecordSourceOutcome(HealthSourceName, "degraded", 0, "GitHub Search API rate limit is unknown or low.");
            return;
        }

        IReadOnlyList<PackageScanTarget> targetsToScan = Array.Empty<PackageScanTarget>();
        IReadOnlyList<string> selectedSliceNames = Array.Empty<string>();
        var failedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentWatermark = _lastWatermark ?? _stateStore?.GetCursorWatermark(_cursorStateKey);
        var slicedUpstream = false;

        // Slice package names BEFORE calling GetSpecAsync so fallback mode never fetches O(N) specs.
        if (_catalogProvider == null && _specCatalog != null && _options.PackagesPerSweep > 0)
        {
            var allNames = await _specCatalog.GetAllPackageNamesAsync(ct);
            if (allNames.Count > 0)
            {
                (targetsToScan, selectedSliceNames) = await FetchSlicedSpecCatalogAsync(
                    allNames, _options.PackagesPerSweep, currentWatermark, failedPackages, ct);
                slicedUpstream = true;
            }
        }

        if (!slicedUpstream)
        {
            var packages = await LoadPackageCatalogAsync(ct);
            if (packages.Count == 0)
            {
                _healthState?.RecordSourceOutcome(HealthSourceName, "healthy", 0);
                _logger?.LogInformation("No packages found in catalog to scan for vulnerabilities");
                return;
            }

            if (_options.PackagesPerSweep > 0)
            {
                var sortedPackages = packages
                    .Where(p => !string.IsNullOrWhiteSpace(p.PackageName))
                    .OrderBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var sortedNames = sortedPackages.Select(p => p.PackageName).ToList();
                if (sortedNames.Count > 0)
                {
                    var sliceCount = Math.Min(_options.PackagesPerSweep, sortedNames.Count);
                    var startIndex = FindWatermarkStartIndex(sortedNames, currentWatermark);
                    var chunk = new List<PackageScanTarget>(sliceCount);
                    var sliceNames = new List<string>(sliceCount);
                    for (int i = 0; i < sliceCount; i++)
                    {
                        var pkg = sortedPackages[(startIndex + i) % sortedPackages.Count];
                        chunk.Add(pkg);
                        sliceNames.Add(pkg.PackageName);
                    }
                    targetsToScan = chunk;
                    selectedSliceNames = sliceNames;
                }
            }
            else
            {
                targetsToScan = packages;
            }
        }

        if (targetsToScan.Count == 0)
        {
            if (!ct.IsCancellationRequested)
            {
                var outcome = CommitSliceWatermark(selectedSliceNames, failedPackages);
                RecordSweepHealthOutcome(0, failedPackages, outcome);
            }
            _logger?.LogInformation("No packages found in catalog to scan for vulnerabilities");
            return;
        }

        _logger?.LogInformation("Dispatching OSV scan for {Count} catalog packages", targetsToScan.Count);

        PackageVulnerabilityScanResult scanResult;
        try
        {
            scanResult = _scanner is ITargetedPackageVulnerabilityScanner targetedScanner
                ? await targetedScanner.ScanTargetsAsync(targetsToScan, ct)
                : await _scanner.ScanPackagesAsync(targetsToScan.Select(t => (t.PackageName, t.Version)).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Vulnerability scan failed; deferring CVE cursor advancement so the current slice is retried");
            _healthState?.RecordSourceOutcome(HealthSourceName, "failed", 0, ex.Message);
            return;
        }

        if (scanResult.FailedPackages != null)
        {
            foreach (var failedPkg in scanResult.FailedPackages)
            {
                if (!string.IsNullOrWhiteSpace(failedPkg))
                {
                    failedPackages.Add(failedPkg);
                }
            }
        }

        var findings = scanResult.Findings;
        var vulnerablePackages = BuildVulnerablePackagesMap(findings);
        var hasUnverifiedPackages = scanResult.UnverifiedNativePackages != null && scanResult.UnverifiedNativePackages.Count > 0;

        _logger?.LogInformation("Vulnerability scan identified {Count} packages with outstanding CVEs", findings.Count);

        if (findings.Count == 0 && scanResult.SuccessfullyScannedPackages.Count == 0 && !hasUnverifiedPackages)
        {
            if (!ct.IsCancellationRequested)
            {
                var outcome = CommitSliceWatermark(selectedSliceNames, failedPackages);
                RecordSweepHealthOutcome(0, failedPackages, outcome);
            }
            return;
        }

        var issueLabel = !string.IsNullOrWhiteSpace(_options.IssueLabel)
            ? _options.IssueLabel
            : GitHubLabels.VulnerabilityDetected;

        IReadOnlyList<Issue> openVulnerabilityIssues;
        var isIndexTruncated = false;
        var totalOpenIssues = 0;
        try
        {
            if (_pagedIssueSearch != null)
            {
                var pageResult = await _pagedIssueSearch.SearchOpenIssuesByLabelAsync(owner, repo, issueLabel, ct);
                openVulnerabilityIssues = pageResult.Items;
                isIndexTruncated = pageResult.IsTruncated;
                totalOpenIssues = pageResult.TotalCount;
            }
            else
            {
                openVulnerabilityIssues = await _gitHubService.SearchStaleCandidatesAsync(
                    owner,
                    repo,
                    DateTimeOffset.UtcNow,
                    isPr: false,
                    labelFilter: $"label:\"{issueLabel}\" is:issue is:open",
                    ct: ct);
                totalOpenIssues = openVulnerabilityIssues.Count;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(
                ex,
                "Failed to query existing open vulnerability issues for {Owner}/{Repo}; deferring issue creation and closure to avoid duplicates",
                owner,
                repo);
            _healthState?.RecordSourceOutcome(HealthSourceName, "failed", 0, ex.Message);
            return;
        }

        if (isIndexTruncated)
        {
            _logger?.LogWarning(
                "Open vulnerability issue index truncated in {Owner}/{Repo} ({Retrieved} of {Total} open issues); updating indexed issues and closing resolved indexed issues, while deferring new issue creation to prevent duplicates.",
                owner, repo, openVulnerabilityIssues.Count, totalOpenIssues);
        }

        var openIssuesByPackage = new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
        foreach (var openIssue in openVulnerabilityIssues)
        {
            if (openIssue != null && TryExtractTrackedPackageName(openIssue, out var trackedPkg))
            {
                openIssuesByPackage.TryAdd(trackedPkg, openIssue);
            }
        }

        // 1. Process new or existing findings (Rollup Issue per package) without per-finding Search API calls
        foreach (var finding in vulnerablePackages.Values)
        {
            if (ct.IsCancellationRequested)
                break;

            var ok = await ProcessPackageFindingAsync(owner, repo, finding, issueLabel, openIssuesByPackage, isIndexTruncated, ct);
            if (!ok)
            {
                failedPackages.Add(finding.PackageName);
            }
        }

        // 2. Pure scan-based auto-close: reuse the pre-fetched index so resolved checks emit zero extra Search API calls
        await CheckAndCloseResolvedIssuesAsync(owner, repo, issueLabel, openVulnerabilityIssues, scanResult.SuccessfullyScannedPackages, vulnerablePackages, failedPackages, ct);

        // 3. Notify maintainers once on pre-existing open issues for native RPMs skipped by the authoritative ecosystem gate
        if (!_options.IncludeUnverifiedNativePackages && hasUnverifiedPackages)
        {
            await NotifyUnverifiedLegacyIssuesAsync(owner, repo, openIssuesByPackage, scanResult.UnverifiedNativePackages!, ct);
        }

        if (!ct.IsCancellationRequested)
        {
            var outcome = CommitSliceWatermark(selectedSliceNames, failedPackages);
            RecordSweepHealthOutcome(findings.Count, failedPackages, outcome);
        }
    }

    private static Dictionary<string, PackageVulnerabilityFinding> BuildVulnerablePackagesMap(
        IReadOnlyList<PackageVulnerabilityFinding> findings)
    {
        var map = new Dictionary<string, PackageVulnerabilityFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings)
        {
            if (finding == null || string.IsNullOrWhiteSpace(finding.PackageName))
            {
                continue;
            }

            if (!map.TryGetValue(finding.PackageName, out var existing))
            {
                map[finding.PackageName] = finding;
                continue;
            }

            var mergedCveIds = existing.CveIds
                .Concat(finding.CveIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var mergedReports = existing.DetailedReports
                .Concat(finding.DetailedReports)
                .GroupBy(r => r.CveId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var mergedTotal = Math.Max(
                Math.Max(existing.TotalVulnerabilityCount, finding.TotalVulnerabilityCount),
                mergedCveIds.Count);
            var selectedVersion = UpdateAdmission.CompareRpmVersions(finding.Version, existing.Version) > 0
                ? finding.Version
                : existing.Version;

            map[finding.PackageName] = existing with
            {
                Version = selectedVersion,
                CveIds = mergedCveIds,
                DetailedReports = mergedReports,
                TotalVulnerabilityCount = mergedTotal,
                IsUnverifiedEcosystem = existing.IsUnverifiedEcosystem && finding.IsUnverifiedEcosystem,
                IsHeuristicUpstream = existing.IsHeuristicUpstream && finding.IsHeuristicUpstream
            };
        }

        return map;
    }

    private async Task NotifyUnverifiedLegacyIssuesAsync(
        string owner,
        string repo,
        IReadOnlyDictionary<string, Issue> openIssuesByPackage,
        IReadOnlySet<string> unverifiedPackages,
        CancellationToken ct)
    {
        foreach (var packageName in unverifiedPackages)
        {
            if (ct.IsCancellationRequested)
                break;

            if (!openIssuesByPackage.TryGetValue(packageName, out var openIssue))
                continue;

            if (_options.DryRun)
            {
                _logger?.LogInformation(
                    "[DryRun] Would post one-time unverified native ecosystem notice on legacy CVE issue #{Number} for {Package}",
                    openIssue.Number, packageName);
                continue;
            }

            try
            {
                var comments = await _gitHubService.GetIssueCommentsAsync(owner, repo, openIssue.Number, ct);
                if (comments != null && comments.Any(c =>
                        c.Body != null && c.Body.Contains(UnverifiedNoticeMarker, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var notice = $"""
                    > **Unverified Native RPM Ecosystem Notice**
                    >
                    > Package `{packageName}` does not declare an authoritative upstream OSV ecosystem (`crates.io` or `PyPI`). By default (`CveActiveWatch:IncludeUnverifiedNativePackages = false`), automated scanning now skips unverified native RPM packages to prevent cross-distribution false positives, so this tracking issue will no longer be automatically updated or auto-closed.
                    >
                    > Please verify whether any listed CVEs apply to openRuyi's packaging and manually close this issue when resolved, or explicitly set `CveActiveWatch:IncludeUnverifiedNativePackages = true` if you want best-effort package-name scanning to continue maintaining it.

                    {UnverifiedNoticeMarker}
                    """;

                await _gitHubService.CreateCommentAsync(owner, repo, openIssue.Number, notice, ct);
                _logger?.LogInformation(
                    "Posted one-time unverified ecosystem migration notice on legacy CVE issue #{Number} for {Package}",
                    openIssue.Number, packageName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to post unverified ecosystem notice on issue #{Number} for {Package}", openIssue.Number, packageName);
            }
        }
    }

    private async Task<bool> ProcessPackageFindingAsync(
        string owner,
        string repo,
        PackageVulnerabilityFinding finding,
        string issueLabel,
        IDictionary<string, Issue> openIssuesByPackage,
        bool isIndexTruncated,
        CancellationToken ct)
    {
        var issueTitle = $"[Security] Outstanding CVEs detected in {finding.PackageName}";
        var body = GenerateIssueBody(finding);
        var maxSeverity = CalculateMaxSeverity(finding.DetailedReports);

        var labels = new List<string>
        {
            issueLabel,
            GitHubLabels.StatusStaleExempt
        };
        if (!string.IsNullOrEmpty(maxSeverity))
        {
            labels.Add(maxSeverity);
        }

        try
        {
            openIssuesByPackage.TryGetValue(finding.PackageName, out var existingIssue);

            if (existingIssue != null)
            {
                var updateTitle = !string.IsNullOrWhiteSpace(existingIssue.Title) ? existingIssue.Title : issueTitle;
                var bodyChanged = !string.Equals(
                    NormalizeLineEndings(existingIssue.Body),
                    NormalizeLineEndings(body),
                    StringComparison.Ordinal);
                var titleChanged = !string.Equals(
                    existingIssue.Title ?? string.Empty,
                    updateTitle,
                    StringComparison.Ordinal);

                var currentLabels = existingIssue.Labels ?? Array.Empty<Label>();
                var obsoleteSeverityLabels = currentLabels
                    .Where(l => l.Name != null && ManagedSeverityLabels.Contains(l.Name))
                    .Select(l => l.Name!)
                    .Where(oldLabel => !string.Equals(oldLabel, maxSeverity, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var existingLabelSet = new HashSet<string>(
                    currentLabels.Where(l => !string.IsNullOrWhiteSpace(l.Name)).Select(l => l.Name!),
                    StringComparer.OrdinalIgnoreCase);
                var missingLabels = labels.Where(l => !existingLabelSet.Contains(l)).ToArray();

                if (!bodyChanged && !titleChanged && obsoleteSeverityLabels.Count == 0 && missingLabels.Length == 0)
                {
                    _logger?.LogDebug("Existing vulnerability issue #{Number} for {Package} is already up to date; skipping writes",
                        existingIssue.Number, finding.PackageName);
                    return true;
                }

                _logger?.LogInformation("Updating existing vulnerability issue #{Number} for {Package}", existingIssue.Number, finding.PackageName);
                if (!_options.DryRun)
                {
                    if (bodyChanged || titleChanged)
                    {
                        await _gitHubService.UpdateIssueAsync(owner, repo, existingIssue.Number, updateTitle, body, ct);
                    }

                    // Reconcile severity labels: remove obsolete Severity:* labels
                    foreach (var oldLabel in obsoleteSeverityLabels)
                    {
                        try
                        {
                            await _gitHubService.RemoveLabelAsync(owner, repo, existingIssue.Number, oldLabel, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to remove obsolete severity label {Label} from issue #{Number}", oldLabel, existingIssue.Number);
                        }
                    }

                    if (missingLabels.Length > 0)
                    {
                        await _gitHubService.AddLabelsAsync(owner, repo, existingIssue.Number, missingLabels, ct);
                    }
                }
            }
            else if (isIndexTruncated)
            {
                _logger?.LogWarning(
                    "Deferring new vulnerability issue creation for {Package} because the open issue index was truncated.",
                    finding.PackageName);
            }
            else
            {
                _logger?.LogInformation("Creating new vulnerability issue for {Package} ({Count} CVEs, {Severity})",
                    finding.PackageName, finding.CveIds.Count, maxSeverity ?? "Unrated");

                if (!_options.DryRun)
                {
                    var created = await _gitHubService.CreateIssueAsync(owner, repo, issueTitle, body, labels.ToArray(), ct);
                    if (created != null)
                    {
                        openIssuesByPackage[finding.PackageName] = created;
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to create or update vulnerability issue for {Package}", finding.PackageName);
            return false;
        }
    }

    private async Task CheckAndCloseResolvedIssuesAsync(
        string owner,
        string repo,
        string issueLabel,
        IReadOnlyList<Issue> openVulnerabilityIssues,
        IReadOnlySet<string> successfullyScannedPackages,
        Dictionary<string, PackageVulnerabilityFinding> currentVulnerabilities,
        ISet<string> failedPackages,
        CancellationToken ct)
    {
        foreach (var issue in openVulnerabilityIssues)
        {
            if (ct.IsCancellationRequested)
                break;

            if (issue == null || !TryExtractTrackedPackageName(issue, out var packageName))
                continue;

            // Only auto-close if this package was verified in a successful authoritative scan batch without outstanding CVEs
            if (!successfullyScannedPackages.Contains(packageName))
                continue;

            if (!currentVulnerabilities.ContainsKey(packageName))
            {
                // Vulnerabilities for this package were cleared!
                _logger?.LogInformation("All CVEs resolved for {Package}; closing issue #{Number}", packageName, issue.Number);

                if (!_options.DryRun)
                {
                    try
                    {
                        // Apply Resolved label and close before commenting so transient Close errors
                        // never post duplicate closing comments and retry cleanly on the next sweep.
                        await _gitHubService.AddLabelsAsync(owner, repo, issue.Number, new[] { GitHubLabels.VulnerabilityResolved }, ct);
                        await _gitHubService.CloseIssueAsync(owner, repo, issue.Number, ct);

                        var resolutionComment = "All previously detected CVE vulnerabilities have been resolved in the current package version. Closing issue.";
                        await _gitHubService.CreateCommentAsync(owner, repo, issue.Number, resolutionComment, ct);

                        try
                        {
                            await _gitHubService.RemoveLabelAsync(owner, repo, issue.Number, issueLabel, ct);
                        }
                        catch (Exception removeEx) when (removeEx is not OperationCanceledException)
                        {
                            _logger?.LogWarning(removeEx,
                                "Closed resolved vulnerability issue #{Number} for {Package} but failed to remove label '{Label}'",
                                issue.Number, packageName, issueLabel);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failedPackages.Add(packageName);
                        _logger?.LogWarning(ex,
                            "Failed to verify and close resolved vulnerability issue #{Number} for {Package}",
                            issue.Number, packageName);
                    }
                }
            }
        }
    }

    internal static bool TryExtractTrackedPackageName(Issue issue, out string packageName)
    {
        if (!string.IsNullOrWhiteSpace(issue.Body))
        {
            var markerMatches = PackageIssueBodyMarkerRegex.Matches(issue.Body);
            if (markerMatches.Count > 0)
            {
                packageName = markerMatches[^1].Groups["pkg"].Value.Trim();
                if (!string.IsNullOrEmpty(packageName))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(issue.Title))
        {
            var titleMatch = PackageIssueTitleRegex.Match(issue.Title);
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

    public static string? CalculateMaxSeverity(IEnumerable<CveReport> reports)
    {
        var severities = reports
            .Select(r => r.SeverityLabel)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (severities.Contains(GitHubLabels.Severity1)) return GitHubLabels.Severity1;
        if (severities.Contains(GitHubLabels.Severity2)) return GitHubLabels.Severity2;
        if (severities.Contains(GitHubLabels.Severity3)) return GitHubLabels.Severity3;
        if (severities.Contains(GitHubLabels.Severity4)) return GitHubLabels.Severity4;
        return null;
    }

    public static string? FormatVulnerabilityUrl(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var trimmed = id.Trim();
        var escaped = Uri.EscapeDataString(trimmed);
        if (trimmed.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            return $"https://nvd.nist.gov/vuln/detail/{escaped}";

        if (trimmed.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase))
            return $"https://github.com/advisories/{escaped}";

        return $"https://osv.dev/vulnerability/{escaped}";
    }

    public static string GenerateIssueBody(PackageVulnerabilityFinding finding)
    {
        const int maxDisplayCount = 20;
        var totalCount = Math.Max(finding.TotalVulnerabilityCount, finding.DetailedReports.Count);
        var displayReports = finding.DetailedReports
            .OrderByDescending(r => r.CvssScore ?? 0)
            .Take(maxDisplayCount)
            .ToList();

        var rows = string.Join("\n", displayReports.Select(r =>
        {
            var linkUrl = FormatVulnerabilityUrl(r.CveId);
            var safeCveId = EscapeMarkdownLinkText(r.CveId);
            var idCell = linkUrl != null ? $"[{safeCveId}]({linkUrl})" : $"`{EscapeMarkdown(r.CveId)}`";
            return $"| {idCell} | `{r.SeverityLabel ?? "Unknown"}` | {r.CvssScore?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A"} | {EscapeMarkdown(r.Summary ?? "Vulnerability details unavailable")} |";
        }));

        var truncationNotice = totalCount > displayReports.Count
            ? $"\n\n> **Notice**: Truncated. Displaying {displayReports.Count} sampled vulnerabilities (sorted by CVSS within the {displayReports.Count}-entry detail cap) out of {totalCount} total reported advisories."
            : string.Empty;

        var bestEffortBanner = finding.IsUnverifiedEcosystem
            ? "\n\n> **Best-Effort / Unverified Ecosystem**: This native RPM package has no authoritative openRuyi OSV ecosystem feed; results are matched by package name across upstream distributions and may contain false positives."
            : string.Empty;

        var heuristicBanner = !finding.IsUnverifiedEcosystem && finding.IsHeuristicUpstream
            ? "\n\n> **Heuristic Upstream Name Match**: This package lacks an explicit `%global pypi_name`, `srcname`, or `crate_name` macro and was matched using a package-name prefix heuristic. Because OSV empty responses cannot reliably prove resolution for heuristic names, this issue will **not** auto-close and requires manual maintainer triage."
            : string.Empty;

        var markerPackage = EscapeHtmlCommentMarker(finding.PackageName);
        var safePackage = EscapeMarkdown(finding.PackageName);
        var safeVersion = EscapeMarkdown(finding.Version);
        var marker = $"\n\n<!-- abaci-cve-package: {markerPackage} -->";
        var footerNote = (finding.IsUnverifiedEcosystem || finding.IsHeuristicUpstream)
            ? "> Automatically tracked by abaci-bot. Because upstream resolution cannot be proven automatically for unverified or heuristic-matched packages, this issue requires manual triage and will **not** close automatically."
            : "> Automatically tracked by abaci-bot. When this package is updated and CVEs are cleared, this issue will close automatically.";
        var body = $"""
            ## Vulnerability Assessment for `{safePackage}` ({safeVersion}){bestEffortBanner}{heuristicBanner}

            The following unpatched CVE vulnerabilities were actively detected in `{safePackage}`:

            | CVE ID | Severity | CVSS Score | Summary |
            |---|---|---|---|
            {rows}{truncationNotice}

            {footerNote}
            """;

        // Hard cap at 60,000 characters to prevent GitHub 422 limit (65536 max) while keeping identity marker intact
        const string overflowNotice = "\n\n> [Content truncated due to size limits]";
        var maxContentLength = 60000 - marker.Length - overflowNotice.Length;
        if (body.Length + marker.Length > 60000)
        {
            body = string.Concat(body.AsSpan(0, Math.Max(0, maxContentLength)), overflowNotice);
        }

        return body + marker;
    }

    private static string EscapeMarkdownLinkText(string text)
    {
        return EscapeMarkdown(text)
            .Replace("[", "\\[")
            .Replace("]", "\\]");
    }

    private static string EscapeHtmlCommentMarker(string text)
    {
        return text
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("<!--", "&lt;!--")
            .Replace("-->", "--&gt;")
            .Trim();
    }

    private static string EscapeMarkdown(string text)
        => EscapeHtmlCommentMarker(text).Replace("|", "\\|");

    private static string NormalizeLineEndings(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    internal static PackageScanTarget? TryExtractScanTargetFromSpec(string packageName, string? specContent)
    {
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(specContent))
        {
            return null;
        }

        if (!UpdateAdmission.TryExtractCurrentVersion(specContent, out var version))
        {
            return null;
        }

        string? ecosystem = null;
        string? upstreamName = null;

        var crateMatch = CrateNameRegex.Match(specContent);
        if (crateMatch.Success)
        {
            ecosystem = "crates.io";
            var candidateName = crateMatch.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidateName) && !candidateName.Contains('%'))
            {
                upstreamName = candidateName;
            }
        }
        else if (packageName.StartsWith("python-", StringComparison.OrdinalIgnoreCase) ||
                 packageName.StartsWith("python3-", StringComparison.OrdinalIgnoreCase))
        {
            ecosystem = "PyPI";
            var pypiMatch = PypiNameRegex.Match(specContent);
            if (pypiMatch.Success)
            {
                var candidateName = pypiMatch.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(candidateName) && !candidateName.Contains('%'))
                {
                    upstreamName = candidateName;
                }
            }
        }

        return new PackageScanTarget(packageName, version, ecosystem, upstreamName);
    }

    internal static int FindWatermarkStartIndex(IReadOnlyList<string> sortedNames, string? lastWatermark)
    {
        if (sortedNames.Count == 0 || string.IsNullOrWhiteSpace(lastWatermark))
        {
            return 0;
        }

        for (int i = 0; i < sortedNames.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Compare(sortedNames[i], lastWatermark) > 0)
            {
                return i;
            }
        }

        return 0;
    }

    private readonly record struct WatermarkCommitOutcome(
        string? BlockingPackage,
        int BlockingAttempts,
        IReadOnlyList<string> SkippedPoisonPackages)
    {
        public static readonly WatermarkCommitOutcome None = new(null, 0, Array.Empty<string>());
    }

    private void EnsurePoisonStateLoaded()
    {
        if (_poisonStateLoaded)
        {
            return;
        }

        _poisonStateLoaded = true;
        var raw = _stateStore?.GetCursorWatermark(_poisonStateKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var separator = raw.LastIndexOf('|');
        if (separator <= 0 || separator >= raw.Length - 1)
        {
            return;
        }

        var pkg = raw[..separator].Trim();
        if (!string.IsNullOrWhiteSpace(pkg) &&
            int.TryParse(
                raw[(separator + 1)..],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var count) &&
            count >= 1)
        {
            _lastPoisonPackage = pkg;
            _lastPoisonRewindCount = count;
            _hasPersistedPoisonState = true;
        }
    }

    private void TryPersistPoisonState(string? packageName, int attempts)
    {
        if (_options.DryRun || _stateStore == null)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                if (!_hasPersistedPoisonState)
                {
                    return;
                }

                _stateStore.SetCursorWatermark(_poisonStateKey, null);
                _hasPersistedPoisonState = false;
                return;
            }

            _stateStore.SetCursorWatermark(
                _poisonStateKey,
                $"{packageName}|{attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            _hasPersistedPoisonState = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist CVE poison-pill state for package '{Package}'", packageName ?? "<clear>");
        }
    }

    private void RecordSweepHealthOutcome(
        int findingsCount,
        IReadOnlySet<string> failedPackages,
        WatermarkCommitOutcome commitOutcome)
    {
        if (_healthState == null)
        {
            return;
        }

        if (commitOutcome.SkippedPoisonPackages is { Count: > 0 } skipped)
        {
            var preview = string.Join(", ", skipped.Take(5));
            var message = $"Skipped poison-pill package(s) after {MaxConsecutiveRewindsPerPackage} consecutive failures: {preview}";
            if (!string.IsNullOrEmpty(commitOutcome.BlockingPackage))
            {
                message += $"; deferred cursor before failed package '{commitOutcome.BlockingPackage}' (attempt {commitOutcome.BlockingAttempts}/{MaxConsecutiveRewindsPerPackage}).";
            }

            _healthState.RecordSourceOutcome(
                HealthSourceName,
                "degraded",
                findingsCount,
                message);
            return;
        }

        if (!string.IsNullOrEmpty(commitOutcome.BlockingPackage))
        {
            _healthState.RecordSourceOutcome(
                HealthSourceName,
                "degraded",
                findingsCount,
                $"Deferred cursor before failed package '{commitOutcome.BlockingPackage}' (attempt {commitOutcome.BlockingAttempts}/{MaxConsecutiveRewindsPerPackage}).");
            return;
        }

        if (failedPackages.Count > 0)
        {
            var preview = string.Join(", ", failedPackages.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(5));
            _healthState.RecordSourceOutcome(
                HealthSourceName,
                "degraded",
                findingsCount,
                $"Completed sweep with {failedPackages.Count} failed package(s): {preview}");
            return;
        }

        _healthState.RecordSourceOutcome(HealthSourceName, "healthy", findingsCount);
    }

    private WatermarkCommitOutcome CommitSliceWatermark(IReadOnlyList<string> sliceNames, IReadOnlySet<string> failedPackages)
    {
        if (_options.PackagesPerSweep <= 0 || sliceNames.Count == 0)
        {
            return WatermarkCommitOutcome.None;
        }

        EnsurePoisonStateLoaded();

        var blockingIdx = -1;
        string? blockingPackage = null;
        var blockingAttempts = 0;
        var skippedPoisons = new List<string>();

        for (int i = 0; i < sliceNames.Count; i++)
        {
            var pkg = sliceNames[i];
            if (!failedPackages.Contains(pkg))
            {
                if (string.Equals(_lastPoisonPackage, pkg, StringComparison.OrdinalIgnoreCase))
                {
                    _lastPoisonPackage = null;
                    _lastPoisonRewindCount = 0;
                }
                continue;
            }

            var attempts = string.Equals(_lastPoisonPackage, pkg, StringComparison.OrdinalIgnoreCase)
                ? _lastPoisonRewindCount + 1
                : 1;

            if (attempts < MaxConsecutiveRewindsPerPackage)
            {
                _lastPoisonPackage = pkg;
                _lastPoisonRewindCount = attempts;
                blockingIdx = i;
                blockingPackage = pkg;
                blockingAttempts = attempts;
                break;
            }

            _lastPoisonPackage = null;
            _lastPoisonRewindCount = 0;
            skippedPoisons.Add(pkg);
            _logger?.LogError(
                "Skipping poison-pill CVE package {Package} after {Attempts} consecutive failures to prevent cursor starvation",
                pkg, attempts);
        }

        TryPersistPoisonState(blockingPackage, blockingAttempts);

        if (blockingIdx == 0)
        {
            _logger?.LogWarning(
                "Keeping CVE scan watermark at {Watermark} because first package {Package} in slice failed (attempt {Attempts}/{MaxAttempts})",
                _lastWatermark ?? "<start>", sliceNames[0], blockingAttempts, MaxConsecutiveRewindsPerPackage);
            return new WatermarkCommitOutcome(blockingPackage, blockingAttempts, skippedPoisons);
        }

        string newWatermark;
        if (blockingIdx > 0)
        {
            newWatermark = sliceNames[blockingIdx - 1];
            _logger?.LogWarning(
                "Rewinding CVE scan watermark to {Watermark} before failed package {Package} (attempt {Attempts}/{MaxAttempts})",
                newWatermark, sliceNames[blockingIdx], blockingAttempts, MaxConsecutiveRewindsPerPackage);
        }
        else
        {
            newWatermark = sliceNames[^1];
        }

        _lastWatermark = newWatermark;
        if (!_options.DryRun && _stateStore != null)
        {
            try
            {
                _stateStore.SetCursorWatermark(_cursorStateKey, newWatermark);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist CVE scan cursor watermark '{Watermark}'", newWatermark);
            }
        }

        return new WatermarkCommitOutcome(blockingPackage, blockingAttempts, skippedPoisons);
    }

    private async Task<(IReadOnlyList<PackageScanTarget> Targets, IReadOnlyList<string> SliceNames)> FetchSlicedSpecCatalogAsync(
        IReadOnlyList<string> allNames,
        int packagesPerSweep,
        string? currentWatermark,
        ISet<string> failedPackages,
        CancellationToken ct)
    {
        var sortedNames = allNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sortedNames.Count == 0)
        {
            return (Array.Empty<PackageScanTarget>(), Array.Empty<string>());
        }

        var sliceCount = Math.Min(packagesPerSweep, sortedNames.Count);
        var startIndex = FindWatermarkStartIndex(sortedNames, currentWatermark);
        var selectedNames = new List<string>(sliceCount);
        for (int i = 0; i < sliceCount; i++)
        {
            selectedNames.Add(sortedNames[(startIndex + i) % sortedNames.Count]);
        }

        var result = new List<PackageScanTarget>(sliceCount);
        foreach (var name in selectedNames)
        {
            if (ct.IsCancellationRequested)
                break;

            SpecContext? spec = null;
            try
            {
                spec = await _specCatalog!.GetSpecAsync(name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to fetch spec for {Package}; marking slice item failed for retry", name);
                failedPackages.Add(name);
                continue;
            }

            if (spec == null)
            {
                failedPackages.Add(name);
                continue;
            }

            var target = TryExtractScanTargetFromSpec(name, spec.Content);
            if (target != null)
            {
                result.Add(target);
            }
        }

        return (result, selectedNames);
    }

    private async Task<IReadOnlyList<PackageScanTarget>> LoadPackageCatalogAsync(CancellationToken ct)
    {
        if (_catalogProvider != null)
        {
            var supplied = await _catalogProvider(ct);
            return supplied.Select(p => new PackageScanTarget(p.PackageName, p.Version)).ToList();
        }

        if (_specCatalog != null)
        {
            var names = await _specCatalog.GetAllPackageNamesAsync(ct);
            var result = new List<PackageScanTarget>();
            foreach (var name in names)
            {
                if (ct.IsCancellationRequested)
                    break;

                var spec = await _specCatalog.GetSpecAsync(name, ct);
                var target = TryExtractScanTargetFromSpec(name, spec?.Content);
                if (target != null)
                {
                    result.Add(target);
                }
            }
            if (result.Count > 0)
                return result;
        }

        var list = new List<PackageScanTarget>();
        const string specsDir = "SPECS";
        if (!Directory.Exists(specsDir))
            return list;

        foreach (var dir in Directory.GetDirectories(specsDir))
        {
            if (ct.IsCancellationRequested)
                break;

            var dirName = Path.GetFileName(dir);
            var specFiles = Directory.GetFiles(dir, "*.spec");
            if (specFiles.Length == 0)
                continue;

            try
            {
                var content = await File.ReadAllTextAsync(specFiles[0], ct);
                var target = TryExtractScanTargetFromSpec(dirName, content);
                if (target != null)
                {
                    list.Add(target);
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        return list;
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background orchestration worker for automated upstream package updates.
/// Dispatches candidate discovery across dual sources, controls rate limits and concurrency quotas,
/// executes remote SPEC updates, performs failure backoff with 30-day cooldowns, and post-merge OBS checks.
/// </summary>
public class PackageUpdateService : BackgroundService
{
    private readonly IGitHubService _gitHubService;
    private readonly IEnumerable<IUpdateCandidateSource> _candidateSources;
    private readonly IEnumerable<ISpecVersionRewriter> _specRewriters;
    private readonly IPackageUpdatePublisher _publisher;
    private readonly ObsPostMergeVerifier _obsPostMergeVerifier;
    private readonly ISpecCatalog _specCatalog;
    private readonly IRemoteAssetBackfiller _remoteAssetBackfiller;
    private readonly IBotStateStore? _stateStore;
    private readonly RuntimeHealthState? _healthState;
    private readonly IOpenPullRequestInventoryGateway? _inventoryGateway;
    private readonly IBotIdentityProvider? _botIdentity;
    private readonly PackageUpdateOptions _options;
    private readonly ILogger<PackageUpdateService>? _logger;
    private readonly string _schedulerOffsetStateKey;
    private int _schedulerOriginOffset;

    internal const string SchedulerOffsetStateKey = "candidate-scheduler-origin";

    internal static string GetSchedulerOffsetStateKey(string? targetRepository) =>
        string.IsNullOrWhiteSpace(targetRepository)
            ? SchedulerOffsetStateKey
            : $"{SchedulerOffsetStateKey}:{targetRepository.Trim().ToLowerInvariant()}";

    // In-memory tracking of consecutive failures and cooldown timestamps
    private readonly ConcurrentDictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    // Unit-test fallback only when constructed with stateStore == null; production DI always persists via IBotStateStore.
    private readonly ConcurrentDictionary<int, bool> _recordedFailedPrs = new();
    private readonly ConcurrentDictionary<string, int> _trackedBlockedIssues = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex BlockedIssueTitleRegex = new(
        @"^\[Auto-Update:\s*Blocked\]\s*(?:Repeated CI failures|Post-merge OBS build failure)\s+on\s+package\s+(?<pkg>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PackageUpdateService(
        IGitHubService gitHubService,
        IEnumerable<IUpdateCandidateSource> candidateSources,
        IEnumerable<ISpecVersionRewriter> specRewriters,
        IPackageUpdatePublisher publisher,
        ObsPostMergeVerifier obsPostMergeVerifier,
        ISpecCatalog specCatalog,
        IOptions<PackageUpdateOptions> options,
        IRemoteAssetBackfiller remoteAssetBackfiller,
        ILogger<PackageUpdateService>? logger = null,
        IBotStateStore? stateStore = null,
        RuntimeHealthState? healthState = null,
        IOpenPullRequestInventoryGateway? inventoryGateway = null,
        IBotIdentityProvider? botIdentity = null)
    {
        _gitHubService = gitHubService;
        _candidateSources = candidateSources;
        _specRewriters = specRewriters;
        _publisher = publisher;
        _obsPostMergeVerifier = obsPostMergeVerifier;
        _specCatalog = specCatalog ?? throw new ArgumentNullException(nameof(specCatalog));
        _options = options.Value;
        _remoteAssetBackfiller = remoteAssetBackfiller ?? throw new ArgumentNullException(nameof(remoteAssetBackfiller));
        _logger = logger;
        _stateStore = stateStore;
        _healthState = healthState;
        _inventoryGateway = inventoryGateway ?? gitHubService as IOpenPullRequestInventoryGateway;
        _botIdentity = botIdentity ?? gitHubService as IBotIdentityProvider;
        _schedulerOffsetStateKey = GetSchedulerOffsetStateKey(_options.TargetRepository);
        _schedulerOriginOffset = LoadSchedulerOffset(stateStore, _schedulerOffsetStateKey);
    }

    private static int LoadSchedulerOffset(IBotStateStore? stateStore, string cursorKey)
    {
        var raw = stateStore?.GetCursorWatermark(cursorKey);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("PackageUpdateService background worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.TargetRepository))
                {
                    await RunSweepAsync(stoppingToken);
                }
                else
                {
                    _logger?.LogDebug("PackageUpdateService is disabled or TargetRepository is unconfigured; skipping sweep");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error occurred during PackageUpdateService sweep");
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours));
            await Task.Delay(interval, stoppingToken);
        }

        _logger?.LogInformation("PackageUpdateService background worker stopping");
    }

    public async Task RunSweepAsync(CancellationToken ct = default)
    {
        var targetRepo = _options.TargetRepository.Trim();
        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
        {
            _logger?.LogError("Invalid TargetRepository format '{Repo}'; expected 'owner/repo'", targetRepo);
            return;
        }

        var owner = parts[0];
        var repo = parts[1];

        _logger?.LogInformation("Starting PackageUpdateService sweep for {TargetRepo} (DryRun={DryRun})", targetRepo, _options.DryRun);

        // 1. Quota & Rate Limit Gate
        var rateLimit = await _gitHubService.GetRateLimitSnapshotAsync(ct);
        if (rateLimit.CoreRemaining == null || rateLimit.CoreRemaining < _options.MinRateLimitRemaining)
        {
            _logger?.LogWarning("Rate limit remaining ({Remaining}) is unknown or below threshold ({Min}); deferring sweep",
                rateLimit.CoreRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown", _options.MinRateLimitRemaining);
            return;
        }

        // 2. Deadlock Resolution: check open bot PRs for failed CI TTL, count active PRs, and load blocked gates
        var blockedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedLoaded = await LoadBlockedPackagesAsync(owner, repo, blockedPackages, ct);
        var activePrCount = await CleanUpStaleFailedPrsAsync(owner, repo, blockedPackages, ct);

        try
        {
            if (!_options.DryRun && !await EnsureRequiredLabelsAsync(owner, repo, targetRepo, ct))
            {
                _logger?.LogWarning(
                    "Failed to ensure required Auto-Update labels in {TargetRepo}; failing closed and deferring candidate publishing until next sweep",
                    targetRepo);
                return;
            }

            if (!blockedLoaded || activePrCount is null)
            {
                _logger?.LogWarning(
                    "Failed to load Auto-Update: Blocked gates or active PR inventory for {TargetRepo}; deferring candidate collection and publishing until next sweep",
                    targetRepo);
                return;
            }

            // 3. Collect candidates from all registered sources
            var candidates = await CollectCandidatesAsync(ct);
            if (candidates.Count == 0)
            {
                _logger?.LogWarning("PackageUpdate sweep discovered 0 candidates across all enabled update sources for {TargetRepo}", targetRepo);
            }

            // 4. Process candidates respecting gates and sweep limits
            await ProcessCandidatesAsync(owner, repo, targetRepo, activePrCount.Value, candidates, blockedPackages, ct);
        }
        finally
        {
            // 5. Post-sweep tasks (OBS post-merge check) always run unless sweep is cancelled
            if (!ct.IsCancellationRequested)
            {
                await RunPostSweepTasksAsync(targetRepo, ct);
            }
        }
    }

    private async Task<bool> LoadBlockedPackagesAsync(
        string owner,
        string repo,
        HashSet<string> blockedPackages,
        CancellationToken ct)
    {
        try
        {
            var blockedIssues = await _gitHubService.SearchStaleCandidatesAsync(
                owner,
                repo,
                DateTimeOffset.UtcNow.AddDays(1),
                isPr: false,
                labelFilter: $"label:\"{GitHubLabels.AutoUpdateBlocked}\"",
                ct: ct);

            if (blockedIssues == null)
            {
                _logger?.LogWarning("Blocked issue search returned null for {Owner}/{Repo}; failing closed", owner, repo);
                return false;
            }

            foreach (var issue in blockedIssues)
            {
                if (issue == null)
                {
                    continue;
                }

                if (TryExtractBlockedPackage(issue, out var pkg))
                {
                    blockedPackages.Add(pkg);
                    _trackedBlockedIssues.TryAdd(pkg, issue.Number);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to load blocked package tracking issues for {Owner}/{Repo}", owner, repo);
            return false;
        }
    }

    private async Task<bool> EnsureRequiredLabelsAsync(string owner, string repo, string targetRepo, CancellationToken ct)
    {
        var requiredLabels = new (string Name, string Color, string Description)[]
        {
            (GitHubLabels.AutoUpdate, "0366d6", "Pull requests automatically created for package updates"),
            (GitHubLabels.AutoUpdateRust, "dea584", "Pull requests automatically created for Rust crate updates"),
            (GitHubLabels.AutoUpdateBlocked, "d73a4a", "Package updates blocked due to repeated failures or missing dependencies"),
            (GitHubLabels.StatusStaleExempt, "ffffff", "Exempt from automatic stale closure")
        };

        try
        {
            if (_inventoryGateway != null)
            {
                return await _inventoryGateway.EnsureRequiredLabelsVerifiedAsync(owner, repo, requiredLabels, ct);
            }

            await _gitHubService.EnsureLabelsExistAsync(owner, repo, requiredLabels, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to verify/ensure required Auto-Update labels in {TargetRepo}", targetRepo);
            return false;
        }
    }

    private async Task<List<UpdateCandidate>> CollectCandidatesAsync(CancellationToken ct)
    {
        var allCandidates = new List<UpdateCandidate>();
        foreach (var source in _candidateSources)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var candidates = await source.GetCandidatesAsync(ct);
                allCandidates.AddRange(candidates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Failed to retrieve candidates from source {Source}", source.SourceName);
            }
        }

        _logger?.LogInformation("Discovered {Count} total candidates across all sources", allCandidates.Count);
        return allCandidates;
    }

    private async Task ProcessCandidatesAsync(
        string owner,
        string repo,
        string targetRepo,
        int activePrCount,
        IReadOnlyList<UpdateCandidate> discoveredCandidates,
        IReadOnlySet<string> blockedPackages,
        CancellationToken ct)
    {
        const int SecurityAdvisoryConcurrencyAllowance = 5;

        var scheduleResult = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates: discoveredCandidates,
            maxPublishes: _options.MaxCandidatesPerSweep,
            startOriginOffset: _schedulerOriginOffset,
            canAttempt: (candidate, publishedSoFar) =>
            {
                int maxAllowed = candidate.IsSecurityAdvisory
                    ? _options.MaxConcurrentPullRequests + SecurityAdvisoryConcurrencyAllowance
                    : _options.MaxConcurrentPullRequests;
                if (activePrCount + publishedSoFar >= maxAllowed)
                {
                    _logger?.LogInformation(
                        "Reached concurrency limit ({MaxAllowed}) with active={Active}, published={Published} for tier IsSecurity={IsSecurity}; stopping tier",
                        maxAllowed, activePrCount, publishedSoFar, candidate.IsSecurityAdvisory);
                    return false;
                }
                return true;
            },
            tryPublishAsync: (candidate, token) =>
                ProcessSingleCandidateAsync(owner, repo, targetRepo, candidate, blockedPackages, token),
            onPublishedAsync: _options.RequestThrottleMilliseconds > 0
                ? (_, token) => Task.Delay(_options.RequestThrottleMilliseconds, token)
                : null,
            onCandidateError: (candidate, ex) =>
                _logger?.LogError(ex, "Failed to process update candidate {Package} ({Origin}); continuing sweep",
                    candidate.PackageName, candidate.Origin),
            ct: ct);

        if (scheduleResult.TotalPublished >= _options.MaxCandidatesPerSweep &&
            discoveredCandidates.Count > scheduleResult.TotalPublished)
        {
            _logger?.LogInformation(
                "Reached MaxCandidatesPerSweep limit ({Max}); remaining candidates deferred to next sweep",
                _options.MaxCandidatesPerSweep);
        }

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Skipping persisted scheduler offset and candidate sweep cursor commits");
        }
        else
        {
            _schedulerOriginOffset = scheduleResult.NextOriginOffset;
            try
            {
                _stateStore?.SetCursorWatermark(
                    _schedulerOffsetStateKey,
                    scheduleResult.NextOriginOffset.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist candidate scheduler origin offset watermark");
            }

            // Sources that scan a bounded slice (Rust crates) must not advance past candidates
            // that the scheduler could not attempt because the sweep quota was exhausted.
            foreach (var source in _candidateSources)
            {
                if (source is ICandidateSweepCursor cursor)
                {
                    try
                    {
                        cursor.CompleteSweep(scheduleResult.ProcessedPackageNames, scheduleResult.FailedPackageNames);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex, "Failed to commit sweep cursor for source {Source}", source.SourceName);
                    }
                }
            }
        }

        var reportedSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (origin, stats) in scheduleResult.ByOrigin.OrderBy(kv => (int)kv.Key))
        {
            _logger?.LogInformation(
                "PackageUpdate source summary [{Origin}]: discovered={Discovered}, attempted={Attempted}, published={Published}, skipped={Skipped}, failed={Failed}, oldestPublishedAt={OldestPublishedAt}",
                origin,
                stats.Discovered,
                stats.Attempted,
                stats.Published,
                stats.Skipped,
                stats.Failed,
                stats.OldestPublishedAt?.ToString("O") ?? "n/a");

            var sourceName = origin switch
            {
                CandidateOrigin.Lookout => "Lookout",
                CandidateOrigin.RustCrate => "RustCrates",
                _ => origin.ToString()
            };
            reportedSourceNames.Add(sourceName);
            _healthState?.RecordSourceSweepStats(sourceName, stats.Published, stats.Skipped, stats.OldestPublishedAt);
        }

        if (_healthState != null)
        {
            var currentHealth = _healthState.GetSourceHealth();
            foreach (var source in _candidateSources)
            {
                if (reportedSourceNames.Contains(source.SourceName))
                    continue;

                if (currentHealth.TryGetValue(source.SourceName, out var snap) &&
                    string.Equals(snap.Status, "disabled", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _healthState.RecordSourceSweepStats(source.SourceName, 0, 0, null);
            }
        }
    }

    private async Task<bool> ProcessSingleCandidateAsync(
        string owner,
        string repo,
        string targetRepo,
        UpdateCandidate candidate,
        IReadOnlySet<string> blockedPackages,
        CancellationToken ct)
    {
        // 0. Whitelist and sanity check candidate package name and target version
        if (!UpdateAdmission.IsValidPackageName(candidate.PackageName) || !UpdateAdmission.IsValidTargetVersion(candidate.TargetVersion))
        {
            _logger?.LogWarning("Invalid candidate package name '{Package}' or target version '{Version}'; skipping",
                candidate.PackageName, candidate.TargetVersion);
            return false;
        }

        // A. Upstream version stabilization window (CooldownHours)
        if (_options.CooldownHours > 0 && candidate.PublishedAt.HasValue && !candidate.IsSecurityAdvisory)
        {
            var age = DateTimeOffset.UtcNow - candidate.PublishedAt.Value;
            if (age < TimeSpan.FromHours(_options.CooldownHours))
            {
                _logger?.LogDebug("Package {Package} version {Version} published {Age:F1}h ago, within {Cooldown}h stabilization window; skipping",
                    candidate.PackageName, candidate.TargetVersion, age.TotalHours, _options.CooldownHours);
                return false;
            }
        }

        // B. Read existing SPEC from base branch (main) using required ISpecCatalog
        SpecContext? specContext = null;
        try
        {
            specContext = await _specCatalog.GetSpecAsync(candidate.PackageName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Could not load SPEC for candidate {Package} via SpecCatalog", candidate.PackageName);
        }

        if (specContext == null || string.IsNullOrWhiteSpace(specContext.Content))
            return false;

        var specContent = specContext.Content;

        // Check if main branch was already manually updated to or past target version (closed-loop reset)
        if (UpdateAdmission.IsVersionAtOrPastTarget(specContent, candidate.TargetVersion))
        {
            _logger?.LogInformation("Package {Package} on main is already at or past target version {Version}; resetting cooldown",
                candidate.PackageName, candidate.TargetVersion);
            ResetCooldown(candidate.PackageName);
            return false;
        }

        // F4: prefer authoritative current version parsed from main branch SPEC over potentially stale source metadata
        var effectiveCandidate = UpdateAdmission.TryExtractCurrentVersion(specContent, out var specCurrentVersion)
            ? candidate with { CurrentVersion = specCurrentVersion }
            : candidate;

        // Pre-gate: skip packages blocked by an open Auto-Update: Blocked tracking issue or PR
        if (blockedPackages.Contains(effectiveCandidate.PackageName))
        {
            _logger?.LogInformation("Skipping {Package} because an open issue or PR carries '{BlockedLabel}'",
                effectiveCandidate.PackageName, GitHubLabels.AutoUpdateBlocked);
            return false;
        }

        // C. Check repeated CI failure cooldown and closed-loop issue reset
        if (IsInCooldown(effectiveCandidate.PackageName))
        {
            bool issueWasClosed = false;
            if (_trackedBlockedIssues.TryGetValue(effectiveCandidate.PackageName, out var issueNum))
            {
                try
                {
                    var issue = await _gitHubService.GetIssueAsync(owner, repo, issueNum, ct);
                    if (issue != null && issue.State.Value == Octokit.ItemState.Closed)
                    {
                        _logger?.LogInformation("Tracking issue #{IssueNum} for package {Package} was closed; resetting cooldown", issueNum, effectiveCandidate.PackageName);
                        ResetCooldown(effectiveCandidate.PackageName);
                        _trackedBlockedIssues.TryRemove(effectiveCandidate.PackageName, out _);
                        issueWasClosed = true;
                    }
                    else
                    {
                        _logger?.LogDebug("Package {Package} remains blocked by open issue #{IssueNum}; skipping", effectiveCandidate.PackageName, issueNum);
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to query tracking issue #{IssueNum} for {Package}; maintaining cooldown", issueNum, effectiveCandidate.PackageName);
                    return false;
                }
            }

            if (!issueWasClosed && IsInCooldown(effectiveCandidate.PackageName))
            {
                _logger?.LogDebug("Package {Package} is in failure cooldown; skipping", effectiveCandidate.PackageName);
                return false;
            }
        }

        // D. Check runtime @bot ignore and #!NoAutoUpdate directive
        var runtimeIgnore = _stateStore?.GetIgnoredPackage(owner, repo, effectiveCandidate.PackageName);
        if (runtimeIgnore != null)
        {
            _logger?.LogInformation("Skipping {Package} because it is ignored at runtime by @{IgnoredBy}: {Reason}",
                effectiveCandidate.PackageName, runtimeIgnore.IgnoredBy, runtimeIgnore.Reason ?? "no reason specified");
            return false;
        }

        if (NoAutoUpdateMarker.HasNoAutoUpdate(specContent, out var optOutReason))
        {
            _logger?.LogInformation("Skipping {Package} due to in-spec opt-out: {Reason}", effectiveCandidate.PackageName, optOutReason);
            return false;
        }

        // Check SkipPackagesWithPatches option
        if (_options.SkipPackagesWithPatches && Regex.IsMatch(specContent, @"^Patch\d*:", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            _logger?.LogInformation("Skipping {Package} because it contains downstream Patch directives and SkipPackagesWithPatches=true", effectiveCandidate.PackageName);
            return false;
        }

        // Check AllowCrossMajorVersion option using authoritative SPEC version
        if (!_options.AllowCrossMajorVersion && UpdateAdmission.IsCrossMajorVersion(effectiveCandidate.CurrentVersion, effectiveCandidate.TargetVersion))
        {
            _logger?.LogInformation("Skipping {Package} because {Current} -> {Target} crosses major version boundary and AllowCrossMajorVersion=false",
                effectiveCandidate.PackageName, effectiveCandidate.CurrentVersion, effectiveCandidate.TargetVersion);
            return false;
        }

        // E. Select compatible rewriter
        var rewriter = _specRewriters.FirstOrDefault(r => r.CanHandle(specContext));
        if (rewriter == null)
        {
            _logger?.LogDebug("No compatible rewriter found for {Package}", effectiveCandidate.PackageName);
            return false;
        }

        var rewriteResult = rewriter.Rewrite(specContext, effectiveCandidate.TargetVersion);
        if (!rewriteResult.Success || !rewriteResult.HasChanged)
        {
            _logger?.LogDebug("Rewrite failed or produced no change for {Package}: {Error}", effectiveCandidate.PackageName, rewriteResult.ErrorMessage);
            return false;
        }

        // F. Publish PR with authoritative spec path and current version
        var publishResult = await _publisher.PublishUpdateAsync(effectiveCandidate, rewriteResult, targetRepo, "main", specContext.RelativePath, ct);
        if (publishResult.Success && !publishResult.Skipped)
        {
            _logger?.LogInformation("Successfully published update for {Package} -> {Version} (PR #{PrNumber})",
                candidate.PackageName, candidate.TargetVersion, publishResult.PullRequestNumber);
            return true;
        }

        if (publishResult.Skipped)
        {
            _logger?.LogDebug("Publish skipped for {Package}: {Reason}", candidate.PackageName, publishResult.SkipReason);
            return false;
        }

        var failureReason = string.IsNullOrWhiteSpace(publishResult.SkipReason)
            ? "unknown publish failure"
            : publishResult.SkipReason;
        _logger?.LogError("Publish failed for {Package} -> {Version}: {Reason}",
            effectiveCandidate.PackageName, effectiveCandidate.TargetVersion, failureReason);
        throw new InvalidOperationException(
            $"Publish failed for {effectiveCandidate.PackageName} -> {effectiveCandidate.TargetVersion}: {failureReason}");
    }

    private async Task<int?> CleanUpStaleFailedPrsAsync(
        string owner,
        string repo,
        HashSet<string> blockedPackages,
        CancellationToken ct)
    {
        // Counts active bot PRs (with bounded failed-PR slot cap) and cleans up inactive failed PRs past TTL
        int healthyActiveCount = 0;
        int failedActiveCount = 0;
        try
        {
            var branchPrefix = !string.IsNullOrEmpty(_options.BranchPrefix) ? _options.BranchPrefix : "autoupdate/";

            // 1. Search open auto-update PRs by baseline label
            var labelPrs = await _gitHubService.SearchStaleCandidatesAsync(
                owner, repo, DateTimeOffset.UtcNow, isPr: true, labelFilter: $"label:\"{GitHubLabels.AutoUpdate}\" is:pr is:open", ct: ct);

            if (labelPrs == null)
            {
                _logger?.LogWarning("Open bot PR search returned null for {Owner}/{Repo}; failing closed", owner, repo);
                return null;
            }

            // 2. Supplement inventory with REST branch-prefix enumeration so unlabeled bot PRs cannot leak slots
            IReadOnlyList<Octokit.PullRequest> prefixPrs = Array.Empty<Octokit.PullRequest>();
            if (_inventoryGateway != null)
            {
                try
                {
                    prefixPrs = await _inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync(owner, repo, branchPrefix, ct)
                                ?? Array.Empty<Octokit.PullRequest>();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Branch-prefix open PR inventory query failed for {Owner}/{Repo}; failing closed", owner, repo);
                    return null;
                }
            }

            var labelPrMap = new Dictionary<int, Octokit.Issue>();
            var prefixPrMap = new Dictionary<int, Octokit.PullRequest>();
            var orderedPrNumbers = new List<int>();
            var seenPrNumbers = new HashSet<int>();

            foreach (var issue in labelPrs)
            {
                if (issue != null && seenPrNumbers.Add(issue.Number))
                {
                    labelPrMap[issue.Number] = issue;
                    orderedPrNumbers.Add(issue.Number);
                }
            }

            foreach (var pr in prefixPrs)
            {
                if (pr != null)
                {
                    prefixPrMap[pr.Number] = pr;
                    if (seenPrNumbers.Add(pr.Number))
                    {
                        orderedPrNumbers.Add(pr.Number);
                    }
                }
            }

            IReadOnlySet<string> ownBotLogins = orderedPrNumbers.Count > 0
                ? await ResolveOwnBotLoginsAsync(ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prNumber in orderedPrNumbers)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Check PR branch workflow runs
                var fullPr = await _gitHubService.GetPullRequestAsync(owner, repo, prNumber, ct);
                prefixPrMap.TryGetValue(prNumber, out var fallbackPr);
                labelPrMap.TryGetValue(prNumber, out var labelIssue);

                var branchName = fullPr?.Head?.Ref ?? fallbackPr?.Head?.Ref;
                var prAuthor = fullPr?.User?.Login ?? fallbackPr?.User?.Login ?? labelIssue?.User?.Login;

                // Do not self-heal labels, count slots, record failure cooldowns, or close human PRs that merely share the branch prefix.
                if (labelIssue == null && !BotCommitOwnership.IsOwnBotLogin(prAuthor, ownBotLogins))
                {
                    _logger?.LogDebug(
                        "Ignoring non-bot open PR #{PrNumber} on branch {Branch} (author={Author})",
                        prNumber, branchName, prAuthor ?? "unknown");
                    continue;
                }

                var headSha = fullPr?.Head?.Sha ?? fallbackPr?.Head?.Sha;
                var prUpdatedAt = fullPr?.UpdatedAt ?? fallbackPr?.UpdatedAt ?? labelIssue?.UpdatedAt;
                var knownLabels = (fullPr?.Labels is { Count: > 0 } ? fullPr.Labels : null)
                                  ?? (fallbackPr?.Labels is { Count: > 0 } ? fallbackPr.Labels : null)
                                  ?? labelIssue?.Labels;

                var runs = !string.IsNullOrWhiteSpace(branchName)
                    ? await _gitHubService.ListWorkflowRunsAsync(owner, repo, branchName, ct)
                    : Array.Empty<Octokit.WorkflowRun>();

                // Precise gate matching: find remoteasset run for backfill
                var remoteAssetRun = WorkflowGateLocator.FindRemoteAssetRun(runs, headSha);
                bool isRemoteAssetFailed = remoteAssetRun != null && remoteAssetRun.Conclusion == Octokit.WorkflowRunConclusion.Failure;

                // Overall CI failure: check if any run on head commit failed
                var runsOnHead = runs.Where(r => headSha == null || string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)).ToList();
                bool isFailed = runsOnHead.Any(r => r.Conclusion == Octokit.WorkflowRunConclusion.Failure);

                // Backfill updated #!RemoteAsset checksums when remoteasset workflow fails on a pure-bot branch
                if (isRemoteAssetFailed && !string.IsNullOrWhiteSpace(branchName) && remoteAssetRun != null && !_options.DryRun)
                {
                    if (await CanBackfillBotBranchAsync(owner, repo, branchName, ownBotLogins, ct))
                    {
                        await _remoteAssetBackfiller.TryBackfillAsync(owner, repo, branchName, prNumber, remoteAssetRun.Id, ct);
                    }
                }

                // Wire failure detection into cooldown tracking
                var branchPackageName = branchName != null && branchName.StartsWith(branchPrefix, StringComparison.OrdinalIgnoreCase)
                    ? branchName.Substring(branchPrefix.Length)
                    : null;

                // Item B self-heal: restore missing Auto-Update / Status: stale-exempt labels on bot PRs found by prefix fallback or partial labeling
                if (!_options.DryRun && (labelIssue == null && fallbackPr != null || (knownLabels != null && knownLabels.Count > 0)))
                {
                    await SelfHealMissingBotPrLabelsAsync(owner, repo, prNumber, branchPackageName, knownLabels, labelIssue == null, ct);
                }

                var hasBlockedLabel = (labelIssue?.Labels != null && labelIssue.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase))) ||
                                      (fullPr?.Labels != null && fullPr.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase))) ||
                                      (fallbackPr?.Labels != null && fallbackPr.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase)));
                if (hasBlockedLabel && !string.IsNullOrEmpty(branchPackageName))
                {
                    blockedPackages.Add(branchPackageName);
                }

                if (isFailed && !string.IsNullOrEmpty(branchPackageName))
                {
                    await CheckAndRecordFailureAsync(owner, repo, branchPackageName, prNumber, ct);
                }

                var isInactiveFailed = isFailed && prUpdatedAt.HasValue &&
                                       prUpdatedAt.Value < DateTimeOffset.UtcNow.AddDays(-_options.FailedPrTtlDays);

                if (isInactiveFailed)
                {
                    var headRepoFullName = fullPr?.Head?.Repository?.FullName ?? fallbackPr?.Head?.Repository?.FullName;
                    var isSameRepo = headRepoFullName != null && string.Equals(headRepoFullName, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase);
                    var hasExpectedPrefix = !string.IsNullOrWhiteSpace(branchName) && branchName.StartsWith(branchPrefix, StringComparison.Ordinal);

                    if (!_options.DryRun)
                    {
                        var canAutoClose = await CanAutoCloseFailedPrAsync(owner, repo, branchName, prAuthor, isSameRepo, ownBotLogins, ct);
                        if (!canAutoClose)
                        {
                            _logger?.LogWarning(
                                "Skipping TTL auto-close for failed PR #{Number} (Branch: {Branch}) because ownership verification failed or human commits were detected",
                                prNumber, branchName);
                            failedActiveCount++;
                            continue;
                        }

                        _logger?.LogInformation("Closing inactive failed bot PR #{Number} (Branch: {Branch}) to free concurrency slot", prNumber, branchName);
                        var closeMsg = $"Automated pull request has been failing CI and inactive for over {_options.FailedPrTtlDays} days. Closing to reclaim concurrency slot.";
                        await _gitHubService.CreateCommentAsync(owner, repo, prNumber, closeMsg, ct);
                        await _gitHubService.CloseIssueAsync(owner, repo, prNumber, ct);

                        if (isSameRepo && hasExpectedPrefix && !string.IsNullOrWhiteSpace(branchName))
                        {
                            try
                            {
                                await _gitHubService.DeleteRefAsync(owner, repo, branchName, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Failed to delete remote branch {Branch}", branchName);
                            }
                        }
                    }
                    else
                    {
                        _logger?.LogInformation("Closing inactive failed bot PR #{Number} (Branch: {Branch}) to free concurrency slot", prNumber, branchName);
                    }
                }
                else if (isFailed)
                {
                    failedActiveCount++;
                }
                else
                {
                    healthyActiveCount++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to enumerate active bot PRs for concurrency management");
            return null;
        }

        var maxConcurrent = Math.Max(1, _options.MaxConcurrentPullRequests);
        var totalOpen = healthyActiveCount + failedActiveCount;
        var hardCap = maxConcurrent * 2;
        if (totalOpen >= hardCap)
        {
            return totalOpen;
        }

        var cappedFailedSlots = Math.Min(failedActiveCount, Math.Max(0, maxConcurrent - 1));
        return healthyActiveCount + cappedFailedSlots;
    }

    private async Task<IReadOnlySet<string>> ResolveOwnBotLoginsAsync(CancellationToken ct)
    {
        var ownLogins = await BotCommitOwnership.ResolveOwnBotLoginsAsync(
            _botIdentity,
            _options.BotLogin,
            requireLiveWhenProviderPresent: true,
            ct);
        if (_botIdentity != null && ownLogins.Count == 0)
        {
            _logger?.LogWarning("Could not resolve live bot identity; failing closed on branch/PR ownership checks");
        }

        return ownLogins;
    }

    private async Task<bool> CanBackfillBotBranchAsync(
        string owner,
        string repo,
        string branchName,
        IReadOnlySet<string> ownBotLogins,
        CancellationToken ct)
    {
        try
        {
            var latestCommit = await _gitHubService.GetBranchLatestCommitAsync(owner, repo, branchName, ct);
            var isBotCommit = BotCommitOwnership.IsOwnBotCommit(latestCommit, ownBotLogins);
            if (!isBotCommit)
            {
                _logger?.LogWarning(
                    "Skipping #!RemoteAsset backfill on branch {Branch} because latest commit is missing or authored by a human ({Author})",
                    branchName, latestCommit?.AuthorLogin ?? "unknown");
            }

            return isBotCommit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to verify branch commit ownership before #!RemoteAsset backfill on {Branch}; skipping fail-closed", branchName);
            return false;
        }
    }

    private async Task<bool> CanAutoCloseFailedPrAsync(
        string owner,
        string repo,
        string? branchName,
        string? prAuthor,
        bool isSameRepo,
        IReadOnlySet<string> ownBotLogins,
        CancellationToken ct)
    {
        if (isSameRepo && !string.IsNullOrWhiteSpace(branchName))
        {
            try
            {
                var latestCommit = await _gitHubService.GetBranchLatestCommitAsync(owner, repo, branchName, ct);
                return BotCommitOwnership.IsOwnBotCommit(latestCommit, ownBotLogins);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Failed to verify branch commit ownership for {Branch}; skipping TTL close fail-closed", branchName);
                return false;
            }
        }

        return BotCommitOwnership.IsOwnBotLogin(prAuthor, ownBotLogins);
    }

    private async Task SelfHealMissingBotPrLabelsAsync(
        string owner,
        string repo,
        int prNumber,
        string? branchPackageName,
        IReadOnlyList<Octokit.Label>? currentLabels,
        bool discoveredViaPrefixFallback,
        CancellationToken ct)
    {
        var existing = new HashSet<string>(
            (currentLabels ?? Array.Empty<Octokit.Label>()).Where(l => !string.IsNullOrWhiteSpace(l.Name)).Select(l => l.Name),
            StringComparer.OrdinalIgnoreCase);

        var expected = new List<string> { GitHubLabels.AutoUpdate, GitHubLabels.StatusStaleExempt };
        if (!string.IsNullOrWhiteSpace(branchPackageName) &&
            branchPackageName.StartsWith("rust-", StringComparison.OrdinalIgnoreCase))
        {
            expected.Add(GitHubLabels.AutoUpdateRust);
        }

        var missing = discoveredViaPrefixFallback && existing.Count == 0
            ? expected.ToArray()
            : expected.Where(l => !existing.Contains(l)).ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        try
        {
            await _gitHubService.AddLabelsAsync(owner, repo, prNumber, missing, ct);
            _logger?.LogInformation("Self-healed missing Auto-Update labels ({Labels}) on open PR #{PrNumber}",
                string.Join(", ", missing), prNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to self-heal missing Auto-Update labels on PR #{PrNumber}", prNumber);
        }
    }

    private async Task RunPostSweepTasksAsync(string targetRepo, CancellationToken ct)
    {
        try
        {
            await _obsPostMergeVerifier.VerifyMergedPullRequestsAsync(targetRepo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to run OBS post-merge verification");
        }
    }

    internal static bool TryExtractBlockedPackage(Octokit.Issue issue, out string packageName)
    {
        if (ObsPostMergeVerifier.TryExtractObsBlockedPackage(issue, out packageName))
        {
            return true;
        }

        var match = BlockedIssueTitleRegex.Match(issue.Title ?? string.Empty);
        if (match.Success)
        {
            var pkg = match.Groups["pkg"].Value.Trim();
            if (!string.IsNullOrEmpty(pkg))
            {
                packageName = pkg;
                return true;
            }
        }

        packageName = string.Empty;
        return false;
    }

    public bool IsInCooldown(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return false;

        return IsInCiCooldown(packageName) || IsInObsBlockCooldown(packageName);
    }

    private bool IsInCiCooldown(string packageName)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cooldowns.TryGetValue(packageName, out var cooldownUntil))
        {
            if (now < cooldownUntil)
                return true;

            _cooldowns.TryRemove(packageName, out _);
        }

        var (owner, repo) = ParseTargetRepository();
        var persisted = owner != null && repo != null
            ? _stateStore?.GetPackageFailure(owner, repo, packageName)
            : null;
        if (persisted?.CooldownUntil.HasValue == true)
        {
            if (now < persisted.CooldownUntil.Value)
                return true;

            if (!_options.DryRun)
            {
                TryPersistClearPackageFailure(owner!, repo!, packageName);
            }
        }

        return false;
    }

    private bool IsInObsBlockCooldown(string packageName)
    {
        var (owner, repo) = ParseTargetRepository();
        var persisted = owner != null && repo != null
            ? _stateStore?.GetPackageFailure(owner, repo, packageName)
            : null;
        if (persisted?.ObsBlockedUntil.HasValue == true)
        {
            if (DateTimeOffset.UtcNow < persisted.ObsBlockedUntil.Value)
                return true;

            if (!_options.DryRun)
            {
                TryPersistClearObsBlockCooldown(owner!, repo!, packageName);
            }
        }

        return false;
    }

    public void ResetCooldown(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return;

        _cooldowns.TryRemove(packageName, out _);
        _failureCounts.TryRemove(packageName, out _);
        var (owner, repo) = ParseTargetRepository();
        if (owner != null && repo != null && !_options.DryRun)
        {
            TryPersistClearPackageFailure(owner, repo, packageName);
            TryPersistClearObsBlockCooldown(owner, repo, packageName);
        }
        _logger?.LogInformation("Reset cooldown and failure count for package {Package}", packageName);
    }

    public bool RecordFailure(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) || IsInCiCooldown(packageName))
            return false;

        var (owner, repo) = ParseTargetRepository();
        var cooldownDays = _options.GiveUpIssueCooldownDays > 0 ? _options.GiveUpIssueCooldownDays : 30;
        var decayWindow = TimeSpan.FromDays(cooldownDays);
        var persisted = owner != null && repo != null
            ? _stateStore?.GetPackageFailure(owner, repo, packageName)
            : null;

        int initialCount = 0;
        if (persisted != null)
        {
            if (persisted.LastFailureAt.HasValue && DateTimeOffset.UtcNow - persisted.LastFailureAt.Value > decayWindow)
            {
                _failureCounts.TryRemove(packageName, out _);
                TryPersistClearPackageFailure(owner!, repo!, packageName);
                _logger?.LogInformation("Decayed stale CI failure count for package {Package} (last failure was >{Days} days ago)",
                    packageName, cooldownDays);
            }
            else
            {
                initialCount = persisted.ConsecutiveFailures;
            }
        }

        var count = _failureCounts.AddOrUpdate(
            packageName,
            _ => initialCount + 1,
            (_, current) => Math.Max(current, initialCount) + 1);
        _logger?.LogWarning("Recorded failure for package {Package} (Consecutive failures: {Count})", packageName, count);

        if (count >= _options.MaxConsecutiveFailures)
        {
            var cooldownUntil = DateTimeOffset.UtcNow.AddHours(cooldownDays * 24);
            _failureCounts.TryRemove(packageName, out _);
            var persistedOk = owner != null && repo != null &&
                              TryPersistSetPackageFailure(owner, repo, packageName, count, cooldownUntil);
            if (!persistedOk)
            {
                // Retain in-memory cooldown only as fallback when persistence is absent or failed.
                _cooldowns[packageName] = cooldownUntil;
            }
            _logger?.LogWarning("Package {Package} reached {Max} consecutive CI failures; entering {Days}-day cooldown",
                packageName, _options.MaxConsecutiveFailures, cooldownDays);
            return true;
        }

        if (owner != null && repo != null)
        {
            TryPersistSetPackageFailure(owner, repo, packageName, count, null);
        }

        return false;
    }

    private void TryPersistClearPackageFailure(string owner, string repo, string packageName)
    {
        try
        {
            _stateStore?.ClearPackageFailure(owner, repo, packageName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear persisted failure state for package {Package} in {Owner}/{Repo}", packageName, owner, repo);
        }
    }

    private void TryPersistClearObsBlockCooldown(string owner, string repo, string packageName)
    {
        try
        {
            _stateStore?.ClearObsBlockCooldown(owner, repo, packageName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear persisted OBS block cooldown for package {Package} in {Owner}/{Repo}", packageName, owner, repo);
        }
    }

    private bool TryPersistSetPackageFailure(string owner, string repo, string packageName, int count, DateTimeOffset? cooldownUntil)
    {
        if (_stateStore == null)
        {
            return false;
        }

        try
        {
            _stateStore.SetPackageFailure(owner, repo, packageName, count, cooldownUntil);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist failure state for package {Package} in {Owner}/{Repo}", packageName, owner, repo);
            return false;
        }
    }

    private (string? Owner, string? Repo) ParseTargetRepository()
    {
        if (string.IsNullOrWhiteSpace(_options.TargetRepository))
            return (null, null);

        var parts = _options.TargetRepository.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }

    private async Task CheckAndRecordFailureAsync(string owner, string repo, string packageName, int prNumber, CancellationToken ct)
    {
        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would record CI failure for package {Package} (PR #{PrNumber})", packageName, prNumber);
            return;
        }

        // Primary path persists deduplication in bot-state.json across restarts; in-memory set is only for unit tests with stateStore == null.
        bool isNewFailure;
        if (_stateStore != null)
        {
            try
            {
                isNewFailure = _stateStore.TryMarkFailedPrRecorded(owner, repo, prNumber);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist recorded failed PR #{PrNumber} for {Package} in {Owner}/{Repo}", prNumber, packageName, owner, repo);
                isNewFailure = _recordedFailedPrs.TryAdd(prNumber, true);
            }
        }
        else
        {
            isNewFailure = _recordedFailedPrs.TryAdd(prNumber, true);
        }
        if (!isNewFailure)
            return;

        var enteredCooldown = RecordFailure(packageName);
        if (enteredCooldown && !_options.DryRun && _options.IssueOnGivingUp)
        {
            var cooldownDays = _options.GiveUpIssueCooldownDays > 0 ? _options.GiveUpIssueCooldownDays : 30;
            var issueTitle = $"[{GitHubLabels.AutoUpdateBlocked}] Repeated CI failures on package {packageName}";
            var issueBody = $"""
                ## Repeated CI Failures Alert
                Automated updates for package `{packageName}` have encountered repeated CI failures ({_options.MaxConsecutiveFailures} distinct failed PRs).

                - **Latest Failed PR**: #{prNumber}
                - **Action Taken**: Entering {cooldownDays}-day cooldown before further automated attempts.
                - **Guidance**: Please inspect dependencies or build requirements manually.
                """;

            try
            {
                var issueLabel = !string.IsNullOrWhiteSpace(_options.GiveUpIssueLabel)
                    ? _options.GiveUpIssueLabel
                    : GitHubLabels.StatusStaleExempt;

                var createdIssue = await _gitHubService.CreateIssueAsync(owner, repo, issueTitle, issueBody, new[] { GitHubLabels.AutoUpdateBlocked, issueLabel }, ct);
                if (createdIssue != null)
                {
                    _trackedBlockedIssues[packageName] = createdIssue.Number;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create blocked issue for {Package}", packageName);
            }
        }
    }
}


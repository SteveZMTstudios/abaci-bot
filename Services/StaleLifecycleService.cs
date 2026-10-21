using abaci_bot.Options;
using abaci_bot.Pipelines;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background periodic service for stale issue and PR lifecycle management.
/// Uses GitHub search queries to locate inactive items, enforces exemption whitelists,
/// throttles requests to respect GitHub Secondary Rate Limits, and automatically
/// applies Status: Stale or closes expired items.
/// </summary>
public class StaleLifecycleService : BackgroundService
{
    private readonly IGitHubService _gitHub;
    private readonly IOptions<StaleLifecycleOptions> _options;
    private readonly IConfiguration _config;
    private readonly PullRequestLockManager _lockManager;
    private readonly ILogger<StaleLifecycleService> _logger;
    private const int MinSearchWatermarkPerSweep = 4;

    public StaleLifecycleService(
        IGitHubService gitHub,
        IOptions<StaleLifecycleOptions> options,
        IConfiguration config,
        PullRequestLockManager lockManager,
        ILogger<StaleLifecycleService> logger)
    {
        _gitHub = gitHub;
        _options = options;
        _config = config;
        _lockManager = lockManager;
        _logger = logger;
    }

    // Internal delay for application startup, overridable in tests
    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    // Internal delay between rate-limit retries, overridable in tests
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("StaleLifecycleService is disabled via configuration.");
            return;
        }

        _logger.LogInformation("StaleLifecycleService started. Periodic interval: {Hours} hours.", options.CheckIntervalHours);

        // Initial delay to let application finish startup
        try
        {
            if (StartupDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartupDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.CheckIntervalHours)));

        do
        {
            try
            {
                await RunLifecycleSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred during stale lifecycle sweep.");
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunLifecycleSweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var repositories = GetTargetRepositories();

        if (repositories.Count == 0)
        {
            _logger.LogWarning("No monitored repositories configured for StaleLifecycleService. Skipping sweep to prevent unintended mutations.");
            return;
        }

        foreach (var (owner, repo) in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessRepositoryAsync(owner, repo, options, cancellationToken);
        }
    }

    private async Task ProcessRepositoryAsync(string owner, string repo, StaleLifecycleOptions options, CancellationToken cancellationToken)
    {
        // 1. Inspect rate limit quota before beginning queries. If below watermark, retry with backoff.
        const int maxRateLimitRetries = 3;
        var retryCount = 0;

        while (true)
        {
            var remainingCore = await _gitHub.GetRemainingRateLimitAsync(cancellationToken);
            var remainingSearch = await _gitHub.GetRemainingSearchRateLimitAsync(cancellationToken);

            if (remainingCore is null)
            {
                _logger.LogError("Authentication or network error while querying GitHub rate limits. Aborting stale sweep for {Owner}/{Repo}.", owner, repo);
                return;
            }

            // Watermark requirement: Minimum 4 search calls per repository sweep
            // (2 for stale candidate discovery: issues + PRs; 2 for close candidate discovery: issues + PRs).
            // Note on fail-open: If remainingSearch is null (rate limit probe failed due to transient network glitch),
            // we fail-open and proceed with the sweep so a temporary probe failure does not halt governance,
            // relying on downstream per-call rate limit exception handling.
            var isCoreHealthy = remainingCore.Value >= options.MinRateLimitRemaining;
            var isSearchHealthy = remainingSearch == null || remainingSearch.Value >= MinSearchWatermarkPerSweep;

            if (isCoreHealthy && isSearchHealthy)
                break;

            retryCount++;
            if (retryCount > maxRateLimitRetries)
            {
                _logger.LogError("GitHub API quota (Core: {Core}, Search: {Search}) is persistently below watermark after {Retries} retries. Skipping sweep for {Owner}/{Repo}.",
                    remainingCore.Value, remainingSearch, maxRateLimitRetries, owner, repo);
                return;
            }

            _logger.LogWarning("GitHub API quota (Core: {Core}, Search: {Search}) is below watermark. Pausing before retry {Attempt}/{Max}.",
                remainingCore.Value, remainingSearch, retryCount, maxRateLimitRetries);

            try
            {
                if (RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var staleThreshold = now.AddDays(-options.StaleDays);
        var closeThreshold = now.AddDays(-options.CloseDays);

        // 2. Fetch candidates to mark as Stale:
        // Open, not draft, without Status: Stale, not updated within StaleDays
        IReadOnlyList<Octokit.Issue> staleIssues;
        IReadOnlyList<Octokit.Issue> stalePrs;
        try
        {
            staleIssues = await _gitHub.SearchStaleCandidatesAsync(
                owner, repo, staleThreshold, isPr: false, labelFilter: $"-label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);
            stalePrs = await _gitHub.SearchStaleCandidatesAsync(
                owner, repo, staleThreshold, isPr: true, labelFilter: $"-label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);
        }
        catch (Octokit.RateLimitExceededException ex)
        {
            DateTimeOffset? resetTime = null;
            try
            {
                resetTime = ex.Reset;
            }
            catch (NullReferenceException)
            {
                // Octokit's Reset property can throw NullReferenceException if HttpResponse is missing (e.g. in mocks)
            }

            _logger.LogWarning(ex, "Search API quota exceeded during candidate search for {Owner}/{Repo}. Reset: {Reset}. Skipping repository sweep.",
                owner, repo, resetTime);
            return;
        }

        var newStaleCandidates = staleIssues.Concat(stalePrs)
            .GroupBy(i => i.Number)
            .Select(g => g.First())
            .ToList();

        foreach (var item in newStaleCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExempt(item, options))
                continue;

            try
            {
                using (await _lockManager.AcquireLockAsync(owner, repo, item.Number, cancellationToken))
                {
                    // Lock-time TOCTOU verification: verify latest authoritative state on GitHub
                    var latest = await TryVerifyEligibilityAsync(owner, repo, item.Number, requireStaleLabel: false, staleThreshold, options, cancellationToken);
                    if (latest == null)
                        continue;

                    if (options.DryRun)
                    {
                        _logger.LogInformation("[DryRun] Would mark {Owner}/{Repo}#{Number} as Status: Stale", owner, repo, item.Number);
                        continue;
                    }

                    _logger.LogInformation("Marking {Owner}/{Repo}#{Number} as Status: Stale", owner, repo, item.Number);

                    // Post comment FIRST: anchors updated_at for CloseDays calculation.
                    // If posting the comment fails, do NOT apply the label (preventing grace period collapse in next cycle).
                    var staleComment = options.FormatStaleComment();
                    await _gitHub.CreateCommentAsync(owner, repo, item.Number, staleComment, cancellationToken);

                    await _gitHub.AddLabelsAsync(owner, repo, item.Number, new[] { GitHubLabels.StatusStale }, cancellationToken);
                }
            }
            catch (Octokit.RateLimitExceededException ex)
            {
                DateTimeOffset? resetTime = null;
                try { resetTime = ex.Reset; } catch (NullReferenceException) { }
                _logger.LogWarning(ex, "GitHub Core API rate limit exceeded while processing {Owner}/{Repo}#{Number}. Reset: {Reset}. Aborting sweep for this repository.",
                    owner, repo, item.Number, resetTime);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to mark {Owner}/{Repo}#{Number} as stale. Skipping item.", owner, repo, item.Number);
            }

            if (options.RequestThrottleMilliseconds > 0)
            {
                await Task.Delay(options.RequestThrottleMilliseconds, cancellationToken);
            }
        }

        // 3. Fetch candidates to auto-close:
        // Open, not draft, with Status: Stale, and not updated within CloseDays (i.e. grace period expired)
        IReadOnlyList<Octokit.Issue> closeIssues;
        IReadOnlyList<Octokit.Issue> closePrs;
        try
        {
            closeIssues = await _gitHub.SearchStaleCandidatesAsync(
                owner, repo, closeThreshold, isPr: false, labelFilter: $"label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);
            closePrs = await _gitHub.SearchStaleCandidatesAsync(
                owner, repo, closeThreshold, isPr: true, labelFilter: $"label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);
        }
        catch (Octokit.RateLimitExceededException ex)
        {
            DateTimeOffset? resetTime = null;
            try
            {
                resetTime = ex.Reset;
            }
            catch (NullReferenceException)
            {
                // Octokit's Reset property can throw NullReferenceException if HttpResponse is missing (e.g. in mocks)
            }

            _logger.LogWarning(ex, "Search API quota exceeded during close candidate search for {Owner}/{Repo}. Reset: {Reset}. Skipping repository sweep.",
                owner, repo, resetTime);
            return;
        }

        var closeCandidates = closeIssues.Concat(closePrs)
            .GroupBy(i => i.Number)
            .Select(g => g.First())
            .ToList();

        foreach (var item in closeCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExempt(item, options))
                continue;

            try
            {
                using (await _lockManager.AcquireLockAsync(owner, repo, item.Number, cancellationToken))
                {
                    // Lock-time TOCTOU verification: verify latest authoritative state on GitHub
                    var latest = await TryVerifyEligibilityAsync(owner, repo, item.Number, requireStaleLabel: true, closeThreshold, options, cancellationToken);
                    if (latest == null)
                        continue;

                    if (options.DryRun)
                    {
                        _logger.LogInformation("[DryRun] Would auto-close expired stale object {Owner}/{Repo}#{Number}", owner, repo, item.Number);
                        continue;
                    }

                    _logger.LogInformation("Auto-closing expired stale object {Owner}/{Repo}#{Number}", owner, repo, item.Number);

                    // Auto-close object first: prevents infinite comment-posting loops if close succeeds and comment fails
                    await _gitHub.CloseIssueAsync(owner, repo, item.Number, cancellationToken);

                    var closeComment = options.FormatCloseComment();
                    await _gitHub.CreateCommentAsync(owner, repo, item.Number, closeComment, cancellationToken);
                }
            }
            catch (Octokit.RateLimitExceededException ex)
            {
                DateTimeOffset? resetTime = null;
                try { resetTime = ex.Reset; } catch (NullReferenceException) { }
                _logger.LogWarning(ex, "GitHub Core API rate limit exceeded while auto-closing {Owner}/{Repo}#{Number}. Reset: {Reset}. Aborting sweep for this repository.",
                    owner, repo, item.Number, resetTime);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to auto-close stale object {Owner}/{Repo}#{Number}. Skipping item.", owner, repo, item.Number);
            }

            if (options.RequestThrottleMilliseconds > 0)
            {
                await Task.Delay(options.RequestThrottleMilliseconds, cancellationToken);
            }
        }
    }

    private async Task<Octokit.Issue?> TryVerifyEligibilityAsync(
        string owner,
        string repo,
        int number,
        bool requireStaleLabel,
        DateTimeOffset threshold,
        StaleLifecycleOptions options,
        CancellationToken cancellationToken)
    {
        var latest = await _gitHub.GetIssueAsync(owner, repo, number, cancellationToken);
        if (latest == null || latest.State.Value != Octokit.ItemState.Open)
            return null;

        var hasStale = latest.Labels.Any(l => string.Equals(l.Name, GitHubLabels.StatusStale, StringComparison.OrdinalIgnoreCase));
        if (hasStale != requireStaleLabel)
            return null;

        if (IsExempt(latest, options))
            return null;

        if (latest.UpdatedAt > threshold)
            return null;

        return latest;
    }

    private static bool IsExempt(Octokit.Issue item, StaleLifecycleOptions options)
    {
        // Protection: Check exempt labels
        if (item.Labels.Any(l => options.ExemptLabels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
            return true;

        // Protection: Draft PRs and WIP titles should not be marked or closed as stale
        if (item.Title.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.PullRequest?.Draft == true)
            return true;

        return false;
    }

    private List<(string Owner, string Repo)> GetTargetRepositories()
    {
        var options = _options.Value;
        var list = new List<(string Owner, string Repo)>();

        if (options.MonitoredRepositories.Count > 0)
        {
            foreach (var r in options.MonitoredRepositories)
            {
                var parts = r.Split('/');
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    list.Add((parts[0].Trim(), parts[1].Trim()));
                }
                else
                {
                    _logger.LogWarning("Invalid repository entry '{Entry}' in MonitoredRepositories. Expected format 'owner/repo'.", r);
                }
            }
        }
        else
        {
            // Do NOT silently fallback to primary repository; explicit configuration is required.
            _logger.LogWarning("No MonitoredRepositories configured for StaleLifecycleService. Skipping sweep to prevent unintended mutations.");
        }

        // Apply exclusion list
        if (options.ExcludedRepositories.Count > 0)
        {
            list.RemoveAll(r => options.ExcludedRepositories.Any(e =>
                string.Equals(e, $"{r.Owner}/{r.Repo}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e, r.Repo, StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }
}

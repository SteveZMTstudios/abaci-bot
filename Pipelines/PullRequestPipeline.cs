using System.Runtime.ExceptionServices;
using abaci_bot.Contexts;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Octokit;

namespace abaci_bot.Pipelines;

/// <summary>
/// Orchestrates the execution of registered <see cref="IPullRequestModule"/> instances.
/// Orders modules by priority, applies a fail-fast error policy, and performs best-effort
/// write-back with automated partial-write recovery to reconcile state drift on GitHub.
/// </summary>
public class PullRequestPipeline
{
    private readonly IReadOnlyList<IPullRequestModule> _modules;
    private readonly LabelMutexEngine _mutexEngine;
    private readonly IGitHubService _gitHubService;
    private readonly IGovernanceCommentFormatter _formatter;
    private readonly ILocaleResolver? _localeResolver;
    private readonly IBotIdentityProvider? _botIdentity;
    private readonly ILogger<PullRequestPipeline>? _logger;
    private readonly RuntimeHealthState? _runtimeHealth;
    private readonly TimeSpan _recoveryTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _commentRetryDelayAsync;
    private const int MaxUnflushedCommentEntries = 1024;
    private static readonly TimeSpan UnflushedCommentTtl = TimeSpan.FromHours(1);
    private readonly object _unflushedCommentsLock = new();
    private readonly Dictionary<string, (string? HeadSha, IReadOnlyList<string> Comments, DateTimeOffset ExpiresAtUtc)> _unflushedCommentsByPr =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default timeout budget allocated for automated partial write recovery reconciliation.
    /// An independent cancellation source is used so that caller cancellation does not prevent
    /// restoring consistent label state on the remote repository.
    /// </summary>
    public static readonly TimeSpan DefaultRecoveryTimeout = TimeSpan.FromSeconds(15);

    public PullRequestPipeline(
        IEnumerable<IPullRequestModule> modules,
        LabelMutexEngine mutexEngine,
        IGitHubService gitHubService,
        ILogger<PullRequestPipeline>? logger = null,
        TimeSpan? recoveryTimeout = null,
        IGovernanceCommentFormatter? formatter = null,
        ILocaleResolver? localeResolver = null,
        Func<TimeSpan, CancellationToken, Task>? commentRetryDelayAsync = null,
        RuntimeHealthState? runtimeHealth = null,
        IBotIdentityProvider? botIdentity = null)
    {
        _modules = modules.OrderBy(m => m.Priority).ToList();
        _mutexEngine = mutexEngine;
        _gitHubService = gitHubService;
        _logger = logger;
        _runtimeHealth = runtimeHealth;
        _recoveryTimeout = recoveryTimeout ?? DefaultRecoveryTimeout;
        _formatter = formatter ?? new GovernanceCommentFormatter();
        _localeResolver = localeResolver;
        _botIdentity = botIdentity ?? gitHubService as IBotIdentityProvider;
        _commentRetryDelayAsync = commentRetryDelayAsync ?? Task.Delay;
    }

    /// <summary>
    /// Registered modules in priority-sorted execution order.
    /// </summary>
    public IReadOnlyList<IPullRequestModule> Modules => _modules;

    /// <summary>
    /// Evaluates whether at least one registered module can process the given context.
    /// </summary>
    public bool HasEligibleModule(PullRequestContext context) =>
        _modules.Any(m => m.ShouldProcess(context));

    /// <summary>
    /// Executes the pipeline sequentially across all active modules and synchronizes label changes.
    /// In case of partial write failures, attempts automated recovery by fetching fresh remote state and retrying.
    /// </summary>
    public async Task ExecuteAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Refresh PR state and labels after lock acquisition to ensure workflow decisions
        // and diff calculations evaluate against fresh remote state rather than stale payloads.
        // Perform at most 2 quick inline attempts on transient errors; if still failing, propagate
        // so WebhookDispatcher retries with backoff and releases the delivery claim if exhausted.
        const int maxRefreshAttempts = 2;
        RefreshResult refreshResult = default;
        for (var attempt = 0; attempt < maxRefreshAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            refreshResult = await context.RefreshAsync(cancellationToken);
            if (refreshResult.IsRefreshed || refreshResult.Outcome != RefreshOutcome.TransientFailure)
            {
                break;
            }

            if (attempt < maxRefreshAttempts - 1)
            {
                var delay = TimeSpan.FromMilliseconds(50 * (attempt + 1) + Random.Shared.Next(10, 30));
                _logger?.LogWarning(refreshResult.Error,
                    "Transient error refreshing remote PR {Owner}/{Repo}#{PrNumber} on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms...",
                    context.Owner, context.Repo, context.PrNumber, attempt + 1, maxRefreshAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (!refreshResult.IsRefreshed)
        {
            switch (refreshResult.Outcome)
            {
                case RefreshOutcome.Gone:
                    _logger?.LogWarning(refreshResult.Error,
                        "Post-lock refresh found pull request {Owner}/{Repo}#{PrNumber} missing (404 Gone); aborting label mutation without retry.",
                        context.Owner, context.Repo, context.PrNumber);
                    return;

                case RefreshOutcome.NonRetriableFailure:
                case RefreshOutcome.InvalidState:
                    _logger?.LogError(refreshResult.Error,
                        "Post-lock refresh failed for {Owner}/{Repo}#{PrNumber} with non-retriable outcome {Outcome}; aborting label mutation without retry.",
                        context.Owner, context.Repo, context.PrNumber, refreshResult.Outcome);
                    _runtimeHealth?.RecordError(
                        nameof(PullRequestPipeline),
                        refreshResult.Error,
                        $"Post-lock refresh failed ({refreshResult.Outcome}) for {context.Owner}/{context.Repo}#{context.PrNumber}");
                    throw new NonRetriablePipelineException(
                        $"Post-lock refresh failed ({refreshResult.Outcome}) for {context.Owner}/{context.Repo}#{context.PrNumber}.",
                        refreshResult.Error);

                default:
                    _logger?.LogError(refreshResult.Error,
                        "Post-lock refresh failed transiently for {Owner}/{Repo}#{PrNumber} after {Attempts} inline attempts; surfacing failure for dispatcher retry.",
                        context.Owner, context.Repo, context.PrNumber, maxRefreshAttempts);
                    _runtimeHealth?.RecordError(
                        nameof(PullRequestPipeline),
                        refreshResult.Error,
                        $"Post-lock transient refresh failure for {context.Owner}/{context.Repo}#{context.PrNumber}");
                    if (refreshResult.Error != null)
                    {
                        ExceptionDispatchInfo.Capture(refreshResult.Error).Throw();
                    }

                    throw new InvalidOperationException(
                        $"Post-lock refresh failed transiently for {context.Owner}/{context.Repo}#{context.PrNumber}.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_localeResolver != null)
        {
            context.PreferredLocale ??= _localeResolver.ResolveForReply(
                context.Owner,
                context.Repo,
                context.AuthorLogin ?? context.SenderLogin);
        }

        // Evaluate module eligibility against refreshed authoritative remote state
        if (!HasEligibleModule(context))
        {
            _logger?.LogDebug("No eligible governance modules to process {Owner}/{Repo}#{PrNumber} after post-lock refresh.",
                context.Owner, context.Repo, context.PrNumber);
            return;
        }

        foreach (var module in _modules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!module.ShouldProcess(context))
                continue;

            _logger?.LogDebug("Executing governance module {ModuleName} (Priority: {Priority}) on {Owner}/{Repo}#{PrNumber}",
                module.ModuleName, module.Priority, context.Owner, context.Repo, context.PrNumber);

            try
            {
                // Fail-fast policy: unhandled exceptions abort the pipeline immediately,
                // preventing partial or corrupted label state from being pushed to GitHub.
                await module.ProcessAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Governance module {ModuleName} threw an unhandled exception while processing {Owner}/{Repo}#{PrNumber}. Aborting pipeline fail-fast.",
                    module.ModuleName, context.Owner, context.Repo, context.PrNumber);
                throw;
            }
        }

        var diff = _mutexEngine.ComputeDiff(context.ExistingLabels, context.LabelsToAdd, context.LabelsToRemove);
        var tracker = new WriteTracker();

        try
        {
            await ApplyDiffAsync(context, diff, tracker, cancellationToken);
            await FlushPendingCommentsAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not CommentPostingTransientException)
        {
            // If cancellation is requested before any write attempt was sent to GitHub,
            // fail-fast without attempting recovery because remote state has not been touched.
            if ((ex is OperationCanceledException || cancellationToken.IsCancellationRequested) && tracker.AttemptedWrites == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            _logger?.LogWarning(ex,
                "Partial label write failure or cancellation on pull request {Owner}/{Repo}#{PrNumber} after {Attempted} attempted ({Completed} completed) writes. Commencing automated recovery.",
                context.Owner, context.Repo, context.PrNumber, tracker.AttemptedWrites, tracker.CompletedWrites);

            // Recovery plan for partial writes or API failures:
            // Fetch fresh labels from GitHub, recalculate diff, and retry the remaining operations.
            // If cancellation arrived after a write succeeded, we use a bounded recovery token to reconcile
            // the partial update on GitHub so competing/mutex labels are not left in an inconsistent state.
            var recoverySucceeded = false;
            WriteTracker? recoveryTracker = null;
            Exception? recoveryFailure = null;

            try
            {
                using var recoveryCts = new CancellationTokenSource(_recoveryTimeout);
                var recoveryToken = recoveryCts.Token;

                var freshPr = await _gitHubService.GetPullRequestAsync(context.Owner, context.Repo, context.PrNumber, recoveryToken);
                if (freshPr?.Labels == null)
                {
                    throw new InvalidOperationException(
                        $"Automated recovery failed: unable to fetch authoritative remote pull request state or labels for {context.Owner}/{context.Repo}#{context.PrNumber}.");
                }

                var freshLabels = freshPr.Labels.Select(l => l.Name);
                var recoveryDiff = _mutexEngine.ComputeDiff(freshLabels, context.LabelsToAdd, context.LabelsToRemove);

                recoveryTracker = new WriteTracker();
                await ApplyDiffAsync(context, recoveryDiff, recoveryTracker, recoveryToken);
                recoverySucceeded = true;
            }
            catch (Exception recEx)
            {
                recoveryFailure = recEx;
            }

            if (recoverySucceeded)
            {
                if (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogWarning(
                        "Pipeline cancelled after {AttemptedWrites} attempted writes (recovery applied {RecoveryWrites} writes) for pull request {Owner}/{Repo}#{PrNumber}; automated recovery successfully reconciled state. Re-throwing cancellation.",
                        tracker.AttemptedWrites, recoveryTracker?.CompletedWrites ?? 0, context.Owner, context.Repo, context.PrNumber);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                _logger?.LogInformation(
                    "Automated recovery succeeded for pull request {Owner}/{Repo}#{PrNumber} after initial write failure (initial writes: {InitialWrites}, recovery writes: {RecoveryWrites}).",
                    context.Owner, context.Repo, context.PrNumber, tracker.CompletedWrites, recoveryTracker?.CompletedWrites ?? 0);

                await FlushPendingCommentsAsync(context, cancellationToken);
            }
            else
            {
                _logger?.LogError(recoveryFailure,
                    "Failed to recover partial label updates for pull request {Owner}/{Repo}#{PrNumber} after initial failure.",
                    context.Owner, context.Repo, context.PrNumber);

                throw new AggregateException(
                    (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
                        ? $"Pipeline cancelled after {tracker.AttemptedWrites} attempted writes and recovery attempt failed."
                        : "Failed to synchronize pull request labels after recovery attempt.",
                    ex, recoveryFailure!);
            }
        }

        await ApplyMilestoneAsync(context, cancellationToken);
    }

    private async Task ApplyMilestoneAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        if (!context.TargetMilestone.HasValue)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _gitHubService.SetIssueMilestoneAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                context.TargetMilestone.Value,
                cancellationToken);

            _logger?.LogInformation(
                "Successfully assigned milestone {Milestone} to pull request {Owner}/{Repo}#{PrNumber}",
                context.TargetMilestone.Value, context.Owner, context.Repo, context.PrNumber);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Milestone assignment failure does not roll back or invalidate label synchronization (isolated channel 3)
            _logger?.LogWarning(
                ex,
                "Failed to assign milestone {Milestone} to pull request {Owner}/{Repo}#{PrNumber}. Label synchronization remains unaffected.",
                context.TargetMilestone.Value, context.Owner, context.Repo, context.PrNumber);
        }
    }

    private sealed class WriteTracker
    {
        public int AttemptedWrites;
        public int CompletedWrites;
    }

    private async Task ApplyDiffAsync(PullRequestContext context, LabelDiffResult diff, WriteTracker tracker, CancellationToken cancellationToken)
    {
        if (diff.LabelsToAdd.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.AttemptedWrites++;
            await _gitHubService.AddLabelsAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                diff.LabelsToAdd.ToArray(),
                cancellationToken);
            tracker.CompletedWrites++;
        }

        foreach (var label in diff.LabelsToRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.AttemptedWrites++;
            await _gitHubService.RemoveLabelAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                label,
                cancellationToken);
            tracker.CompletedWrites++;
        }
    }

    private sealed class CommentPostingTransientException : Exception
    {
        public CommentPostingTransientException(Exception innerException)
            : base(innerException.Message, innerException)
        {
        }
    }

    private IReadOnlyList<string> MergeWithCarriedOverComments(
        string prKey,
        string? headSha,
        IReadOnlyList<string> pendingComments,
        bool rebuildGateConfirmedOnRemote = false)
    {
        IReadOnlyList<string>? carried = null;
        var now = DateTimeOffset.UtcNow;
        lock (_unflushedCommentsLock)
        {
            if (_unflushedCommentsByPr.TryGetValue(prKey, out var entry))
            {
                if (entry.ExpiresAtUtc > now &&
                    string.Equals(entry.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
                {
                    carried = entry.Comments;
                }
                else
                {
                    _unflushedCommentsByPr.Remove(prKey);
                }
            }
        }

        if (carried == null || carried.Count == 0)
        {
            return pendingComments;
        }

        var merged = new List<string>(carried.Count + pendingComments.Count);
        var hasCurrentRebuildGate = pendingComments.Any(c =>
            c.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, StringComparison.Ordinal));

        foreach (var prior in carried)
        {
            if ((hasCurrentRebuildGate || rebuildGateConfirmedOnRemote) &&
                prior.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, StringComparison.Ordinal))
            {
                continue;
            }

            if (!pendingComments.Contains(prior, StringComparer.Ordinal) &&
                !merged.Contains(prior, StringComparer.Ordinal))
            {
                merged.Add(prior);
            }
        }

        foreach (var current in pendingComments)
        {
            if (!merged.Contains(current, StringComparer.Ordinal))
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private void ClearCarriedOverComments(string prKey)
    {
        lock (_unflushedCommentsLock)
        {
            _unflushedCommentsByPr.Remove(prKey);
        }
    }

    private void SaveCarriedOverComments(string prKey, string? headSha, IReadOnlyList<string> comments)
    {
        if (comments.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_unflushedCommentsLock)
        {
            if (_unflushedCommentsByPr.Count >= MaxUnflushedCommentEntries && !_unflushedCommentsByPr.ContainsKey(prKey))
            {
                foreach (var expiredKey in _unflushedCommentsByPr
                             .Where(kv => kv.Value.ExpiresAtUtc <= now)
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    _unflushedCommentsByPr.Remove(expiredKey);
                }

                if (_unflushedCommentsByPr.Count >= MaxUnflushedCommentEntries)
                {
                    var oldestKey = _unflushedCommentsByPr.OrderBy(kv => kv.Value.ExpiresAtUtc).First().Key;
                    _unflushedCommentsByPr.Remove(oldestKey);
                }
            }

            _unflushedCommentsByPr[prKey] = (headSha, comments.ToArray(), now + UnflushedCommentTtl);
        }
    }

    private async Task<string?> TryResolveBotDisplayNameAsync(CancellationToken cancellationToken)
    {
        if (_botIdentity == null)
        {
            return null;
        }

        try
        {
            return await _botIdentity.GetBotLoginAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Could not resolve live bot login for comment header; falling back to default display name.");
            return null;
        }
    }

    private async Task FlushPendingCommentsAsync(
        PullRequestContext context,
        CancellationToken cancellationToken)
    {
        var prKey = $"{context.Owner}/{context.Repo}#{context.PrNumber}";
        var rebuildGateConfirmedOnRemote = context.Items.TryGetValue(
            abaci_bot.Modules.RebuildImpactModule.RebuildGateCommentConfirmedOnRemoteItemKey,
            out var confirmedObj) && confirmedObj is true;
        var effectiveComments = MergeWithCarriedOverComments(
            prKey,
            context.HeadSha,
            context.PendingComments,
            rebuildGateConfirmedOnRemote);
        if (effectiveComments.Count == 0)
        {
            if (rebuildGateConfirmedOnRemote)
            {
                ClearCarriedOverComments(prKey);
            }
            return;
        }

        var hasRebuildGateMarker = effectiveComments.Any(c =>
            c.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, StringComparison.Ordinal));

        var botDisplayName = await TryResolveBotDisplayNameAsync(cancellationToken);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(_formatter.FormatConsolidatedHeader(context.PreferredLocale, botDisplayName));
        sb.AppendLine();
        foreach (var comment in effectiveComments)
        {
            sb.AppendLine(comment);
            sb.AppendLine();
        }

        var consolidatedComment = sb.ToString().TrimEnd();

        // Rebuild-gate comments rely on WebhookDispatcher retries + live marker self-healing (single pipeline attempt
        // prevents 3x3 amplification); non-gate comments allow one fast inline retry before surfacing to WebhookDispatcher.
        var maxAttempts = hasRebuildGateMarker ? 1 : 2;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _gitHubService.CreateCommentAsync(
                    context.Owner,
                    context.Repo,
                    context.PrNumber,
                    consolidatedComment,
                    cancellationToken);

                context.PendingComments.Clear();
                ClearCarriedOverComments(prKey);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (TransientErrorPolicy.IsNonRetriable(ex))
            {
                ClearCarriedOverComments(prKey);
                _logger?.LogError(
                    ex,
                    "Non-retriable error posting consolidated comment on PR {Owner}/{Repo}#{PrNumber}; retaining applied gate labels fail-closed.",
                    context.Owner, context.Repo, context.PrNumber);
                _runtimeHealth?.RecordError(
                    nameof(PullRequestPipeline),
                    ex,
                    $"Non-retriable comment failure on {context.Owner}/{context.Repo}#{context.PrNumber}");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger?.LogWarning(
                    ex,
                    "Transient error posting consolidated comment on PR {Owner}/{Repo}#{PrNumber} (attempt {Attempt}/{MaxAttempts}); retrying.",
                    context.Owner, context.Repo, context.PrNumber, attempt, maxAttempts);
                await _commentRetryDelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (Exception ex)
            {
                SaveCarriedOverComments(prKey, context.HeadSha, effectiveComments);
                _runtimeHealth?.RecordError(
                    nameof(PullRequestPipeline),
                    ex,
                    $"Transient comment failure on {context.Owner}/{context.Repo}#{context.PrNumber}");

                _logger?.LogWarning(
                    ex,
                    "Transient error posting consolidated comment on PR {Owner}/{Repo}#{PrNumber} (hasRebuildGate={HasRebuildGate}); retaining applied labels fail-closed and surfacing for dispatcher retry.",
                    context.Owner, context.Repo, context.PrNumber, hasRebuildGateMarker);
                throw new CommentPostingTransientException(ex);
            }
        }
    }
}

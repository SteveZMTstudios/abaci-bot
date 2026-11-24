namespace abaci_bot.Services;

/// <summary>
/// Per-source execution counters for a single <see cref="CandidateScheduler"/> sweep.
/// </summary>
public sealed record CandidateOriginStats(
    CandidateOrigin Origin,
    int Discovered,
    int Attempted,
    int Published,
    int Skipped,
    int Failed,
    DateTimeOffset? OldestPublishedAt);

/// <summary>
/// Aggregate scheduling result returned by <see cref="CandidateScheduler.ScheduleAndExecuteAsync"/>.
/// </summary>
public sealed record CandidateScheduleResult(
    int TotalPublished,
    int NextOriginOffset,
    IReadOnlyDictionary<CandidateOrigin, CandidateOriginStats> ByOrigin,
    IReadOnlySet<string> ProcessedPackageNames,
    IReadOnlySet<string>? FailedPackageNames = null);

/// <summary>
/// Deep module encapsulating cross-source fair candidate scheduling, per-candidate error isolation,
/// and per-origin throughput/backlog metrics without coupling callers to queue or turn mechanics.
/// </summary>
public static class CandidateScheduler
{
    /// <summary>
    /// Schedules and executes update candidates across two tiers (security advisories first, then routine updates),
    /// rotating fairly across <see cref="CandidateOrigin"/> buckets on published outcomes.
    /// <paramref name="canAttempt"/> acts as a tier-level capacity gate: returning <c>false</c> halts the current
    /// tier immediately without dequeueing or counting unattempted candidates in <c>ProcessedPackageNames</c>.
    /// </summary>
    public static async Task<CandidateScheduleResult> ScheduleAndExecuteAsync(
        IReadOnlyList<UpdateCandidate> candidates,
        int maxPublishes,
        int startOriginOffset,
        Func<UpdateCandidate, int, bool> canAttempt,
        Func<UpdateCandidate, CancellationToken, Task<bool>> tryPublishAsync,
        Func<UpdateCandidate, CancellationToken, Task>? onPublishedAsync = null,
        Action<UpdateCandidate, Exception>? onCandidateError = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(canAttempt);
        ArgumentNullException.ThrowIfNull(tryPublishAsync);

        var statsByOrigin = new Dictionary<CandidateOrigin, MutableOriginStats>();
        foreach (var candidate in candidates)
        {
            if (!statsByOrigin.TryGetValue(candidate.Origin, out var entry))
            {
                entry = new MutableOriginStats(candidate.Origin);
                statsByOrigin[candidate.Origin] = entry;
            }

            entry.Discovered++;
            if (candidate.PublishedAt.HasValue &&
                (!entry.OldestPublishedAt.HasValue || candidate.PublishedAt.Value < entry.OldestPublishedAt.Value))
            {
                entry.OldestPublishedAt = candidate.PublishedAt.Value;
            }
        }

        int totalPublished = 0;
        var nextOffset = unchecked(startOriginOffset + 1) & int.MaxValue;
        var processedPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedPackageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0 || maxPublishes <= 0)
        {
            return new CandidateScheduleResult(totalPublished, nextOffset, FreezeStats(statsByOrigin), processedPackageNames, failedPackageNames);
        }

        var hasSecurity = candidates.Any(c => c.IsSecurityAdvisory);
        var hasRoutine = candidates.Any(c => !c.IsSecurityAdvisory);
        var securityQuota = ResolveSecurityQuota(maxPublishes, hasSecurity, hasRoutine, startOriginOffset);

        // Tier 1: Security advisories first (cross-origin round-robin).
        // Reserve at least one slot for routine updates when both tiers have work:
        // security advisories may not consume the entire sweep budget.
        totalPublished = await ExecuteTierAsync(
            candidates.Where(c => c.IsSecurityAdvisory).ToList(),
            totalPublished,
            securityQuota,
            startOriginOffset,
            statsByOrigin,
            processedPackageNames,
            failedPackageNames,
            canAttempt,
            tryPublishAsync,
            onPublishedAsync,
            onCandidateError,
            ct);

        // Tier 2: Routine updates (cross-origin round-robin advancing on published outcome).
        if (totalPublished < maxPublishes && !ct.IsCancellationRequested)
        {
            totalPublished = await ExecuteTierAsync(
                candidates.Where(c => !c.IsSecurityAdvisory).ToList(),
                totalPublished,
                maxPublishes,
                startOriginOffset,
                statsByOrigin,
                processedPackageNames,
                failedPackageNames,
                canAttempt,
                tryPublishAsync,
                onPublishedAsync,
                onCandidateError,
                ct);
        }

        return new CandidateScheduleResult(totalPublished, nextOffset, FreezeStats(statsByOrigin), processedPackageNames, failedPackageNames);
    }

    private static int ResolveSecurityQuota(int maxPublishes, bool hasSecurity, bool hasRoutine, int startOriginOffset)
    {
        if (!hasSecurity || !hasRoutine)
        {
            return maxPublishes;
        }

        if (maxPublishes >= 2)
        {
            return maxPublishes - 1;
        }

        // With a one-slot sweep budget, alternate security and routine across sweeps
        // so neither tier can starve the other.
        return (startOriginOffset & int.MaxValue) % 2 == 0 ? 1 : 0;
    }

    private static async Task<int> ExecuteTierAsync(
        List<UpdateCandidate> tierCandidates,
        int totalPublished,
        int maxPublishes,
        int startOriginOffset,
        Dictionary<CandidateOrigin, MutableOriginStats> statsByOrigin,
        HashSet<string> processedPackageNames,
        HashSet<string> failedPackageNames,
        Func<UpdateCandidate, int, bool> canAttempt,
        Func<UpdateCandidate, CancellationToken, Task<bool>> tryPublishAsync,
        Func<UpdateCandidate, CancellationToken, Task>? onPublishedAsync,
        Action<UpdateCandidate, Exception>? onCandidateError,
        CancellationToken ct)
    {
        if (tierCandidates.Count == 0 || totalPublished >= maxPublishes)
        {
            return totalPublished;
        }

        var orderedBuckets = tierCandidates
            .GroupBy(c => c.Origin)
            .OrderBy(g => (int)g.Key)
            .Select(g => (
                Origin: g.Key,
                Queue: new Queue<UpdateCandidate>(
                    g.OrderBy(c => c.PublishedAt ?? DateTimeOffset.MaxValue)
                     .ThenBy(c => c.PackageName, StringComparer.OrdinalIgnoreCase))))
            .ToList();

        var offset = orderedBuckets.Count > 0 ? (startOriginOffset & int.MaxValue) % orderedBuckets.Count : 0;
        var activeBuckets = new List<(CandidateOrigin Origin, Queue<UpdateCandidate> Queue)>(orderedBuckets.Count);
        for (int i = 0; i < orderedBuckets.Count; i++)
        {
            activeBuckets.Add(orderedBuckets[(offset + i) % orderedBuckets.Count]);
        }

        int bucketIndex = 0;
        while (totalPublished < maxPublishes && activeBuckets.Count > 0 && !ct.IsCancellationRequested)
        {
            var (_, queue) = activeBuckets[bucketIndex];
            var publishedInTurn = false;
            var concurrencyBlocked = false;

            while (queue.Count > 0 && totalPublished < maxPublishes && !ct.IsCancellationRequested)
            {
                var candidate = queue.Peek();
                if (!canAttempt(candidate, totalPublished))
                {
                    concurrencyBlocked = true;
                    break;
                }

                queue.Dequeue();
                var originStats = statsByOrigin[candidate.Origin];
                originStats.Attempted++;

                bool published;
                try
                {
                    published = await tryPublishAsync(candidate, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    originStats.Failed++;
                    failedPackageNames.Add(candidate.PackageName);
                    onCandidateError?.Invoke(candidate, ex);
                    continue;
                }

                processedPackageNames.Add(candidate.PackageName);

                if (published)
                {
                    totalPublished++;
                    originStats.Published++;
                    publishedInTurn = true;

                    if (onPublishedAsync != null)
                    {
                        await onPublishedAsync(candidate, ct);
                    }

                    break;
                }

                originStats.Skipped++;
            }

            if (concurrencyBlocked)
            {
                break;
            }

            if (queue.Count == 0)
            {
                activeBuckets.RemoveAt(bucketIndex);
                if (activeBuckets.Count > 0)
                {
                    bucketIndex %= activeBuckets.Count;
                }
            }
            else if (publishedInTurn)
            {
                bucketIndex = (bucketIndex + 1) % activeBuckets.Count;
            }
        }

        return totalPublished;
    }

    private static IReadOnlyDictionary<CandidateOrigin, CandidateOriginStats> FreezeStats(
        Dictionary<CandidateOrigin, MutableOriginStats> mutable)
    {
        var result = new Dictionary<CandidateOrigin, CandidateOriginStats>(mutable.Count);
        foreach (var (origin, entry) in mutable)
        {
            result[origin] = new CandidateOriginStats(
                origin,
                entry.Discovered,
                entry.Attempted,
                entry.Published,
                entry.Skipped,
                entry.Failed,
                entry.OldestPublishedAt);
        }

        return result;
    }

    private sealed class MutableOriginStats
    {
        public CandidateOrigin Origin { get; }
        public int Discovered { get; set; }
        public int Attempted { get; set; }
        public int Published { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public DateTimeOffset? OldestPublishedAt { get; set; }

        public MutableOriginStats(CandidateOrigin origin) => Origin = origin;
    }
}

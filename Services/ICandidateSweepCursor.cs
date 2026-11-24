namespace abaci_bot.Services;

/// <summary>
/// Optional capability for candidate sources whose discovery cursor must account for
/// scheduler outcomes. Sources that scan a bounded slice (for example the Rust crates
/// poller) implement this so candidates skipped only because the sweep quota was
/// exhausted are retried on the next sweep instead of being silently advanced past.
/// </summary>
public interface ICandidateSweepCursor
{
    /// <summary>
    /// Called after a sweep with every candidate package name that the scheduler either
    /// published or skipped (<paramref name="processedPackageNames"/>), plus the subset of attempted
    /// candidates whose execution threw an error (<paramref name="failedPackageNames"/>).
    /// Implementations rewind their durable cursor before the earliest candidate absent from
    /// <paramref name="processedPackageNames"/>; only candidates in <paramref name="failedPackageNames"/>
    /// consume the bounded poison-pill rewind limit, whereas quota/concurrency deferrals rewind without penalty.
    /// </summary>
    void CompleteSweep(
        IReadOnlySet<string> processedPackageNames,
        IReadOnlySet<string>? failedPackageNames = null);
}

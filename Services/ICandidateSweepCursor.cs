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
    /// published, skipped, or failed. Implementations rewind their durable cursor before
    /// the earliest candidate not present in <paramref name="processedPackageNames"/>.
    /// </summary>
    void CompleteSweep(IReadOnlySet<string> processedPackageNames);
}

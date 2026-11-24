namespace abaci_bot.Services;

/// <summary>
/// Status representation of an OBS package build result.
/// </summary>
public enum ObsStatusCode
{
    Succeeded,
    Failed,
    Building,
    Scheduled,
    Unresolvable,
    Broken,
    Blocked,
    Disabled,
    Excluded,
    Unknown
}

/// <summary>
/// Detailed result of an OBS query for a specific package build.
/// </summary>
public record ObsPackageBuildResult(
    string Project,
    string Package,
    string Repository,
    string Arch,
    ObsStatusCode Code,
    string RawCode,
    string? Details = null,
    bool IsDirty = false);

/// <summary>
/// Authoritative OBS source-to-job freshness evidence read from <c>/public/source</c> and <c>_jobhistory</c>.
/// </summary>
public sealed record ObsPackageBuildFreshness(
    bool IsServiceReady,
    string? SourceMd5,
    string? LatestJobSrcMd5,
    DateTimeOffset? LatestJobStartTime,
    ObsStatusCode LatestJobCode);

/// <summary>
/// Narrow freshness verification gateway implemented by <see cref="ObsBuildService"/> to decouple
/// multi-endpoint cryptographic freshness checks from the primary <see cref="IObsBuildService"/> status contract.
/// </summary>
public interface IObsBuildFreshnessGateway
{
    Task<ObsPackageBuildFreshness?> GetPackageBuildFreshnessAsync(
        string project,
        string package,
        string repository,
        string arch,
        CancellationToken ct = default);
}

/// <summary>
/// Deep module abstraction over Open Build Service (OBS) REST API.
/// Exclusively targets /public endpoints for anonymous query without credential leakage.
/// </summary>
public interface IObsBuildService
{
    /// <summary>
    /// Reads the commit SHA configured in the package's OBS _service definition.
    /// Returns null if _service does not exist or has no revision parameter.
    /// </summary>
    Task<string?> GetPackageServiceRevisionAsync(string project, string package, CancellationToken ct = default);

    /// <summary>
    /// Queries the build status of a package in the specified project, repository, and arch.
    /// </summary>
    Task<ObsPackageBuildResult> GetPackageBuildResultAsync(
        string project, string package, string repository, string arch, CancellationToken ct = default);
}


namespace abaci_bot.Services;

/// <summary>
/// Normalized repository collaborator permission levels returned by GitHub's collaborator permissions endpoint.
/// </summary>
public enum RepositoryAccessLevel
{
    None = 0,
    Read = 1,
    Triage = 2,
    Write = 3,
    Maintain = 4,
    Admin = 5
}

/// <summary>
/// Narrow port for querying authoritative repository-level collaborator permissions
/// and probing GitHub App installation access on startup.
/// </summary>
public interface IRepositoryPermissionService
{
    /// <summary>
    /// Queries the authoritative repository-level access level for <paramref name="username"/> in <paramref name="owner"/>/<paramref name="repo"/>.
    /// Returns <see cref="RepositoryAccessLevel.None"/> when the user has no collaborator access or is not found.
    /// Throws if the bot installation lacks permission to query repository collaborators.
    /// </summary>
    Task<RepositoryAccessLevel> GetUserPermissionAsync(
        string owner,
        string repo,
        string username,
        CancellationToken ct = default);

    /// <summary>
    /// Probes repository collaborator endpoint access during startup to verify elevated GitHub App installation permissions
    /// (or <c>push</c>+ access when <c>PackageUpdate:BotPat</c> is configured).
    /// </summary>
    Task VerifyCollaboratorPermissionProbeAsync(
        string owner,
        string repo,
        CancellationToken ct = default);

    /// <summary>
    /// Probes whether the active credential holds sufficient permissions to execute <c>archivePullRequest</c>
    /// (requires a <c>triage</c>+ user PAT via <c>PackageUpdate:BotPat</c>—or <c>push</c>+ when combined with collaborator probing;
    /// fails fast when permissions metadata is unavailable or in GitHub App-only mode) on <paramref name="owner"/>/<paramref name="repo"/>.
    /// </summary>
    Task VerifyArchivePermissionProbeAsync(
        string owner,
        string repo,
        CancellationToken ct = default);
}

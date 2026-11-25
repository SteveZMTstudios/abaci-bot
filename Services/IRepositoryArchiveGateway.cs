namespace abaci_bot.Services;

/// <summary>
/// Narrow gateway for downloading repository archive snapshots in a single GitHub API request.
/// Keeps <see cref="IGitHubService"/> free of archive transport concerns.
/// </summary>
public interface IRepositoryArchiveGateway
{
    /// <summary>
    /// Opens a streaming gzipped tarball snapshot for <paramref name="owner"/>/<paramref name="repo"/> at <paramref name="reference"/>.
    /// Enforces <paramref name="maxArchiveBytes"/> during download when positive, rejecting oversized responses before full buffering.
    /// Returns <see langword="null"/> if the archive cannot be retrieved or violates size limits prior to body consumption.
    /// </summary>
    Task<Stream?> OpenRepositoryTarballStreamAsync(
        string owner,
        string repo,
        string reference,
        long maxArchiveBytes = 0,
        CancellationToken ct = default);
}

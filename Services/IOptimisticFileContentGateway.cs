namespace abaci_bot.Services;

/// <summary>
/// Authoritative file snapshot carrying both UTF-8 content and its Git blob SHA for optimistic CAS writes.
/// </summary>
public sealed record RepositoryFileContent(string Content, string BlobSha);

/// <summary>
/// Narrow port for optimistic compare-and-swap (CAS) repository file reads and updates without widening
/// <see cref="IGitHubService"/>.
/// </summary>
public interface IOptimisticFileContentGateway
{
    Task<RepositoryFileContent?> GetFileContentWithShaAsync(
        string owner,
        string repo,
        string path,
        string reference,
        CancellationToken ct = default);

    Task<string> UpdateFileContentIfUnchangedAsync(
        string owner,
        string repo,
        string path,
        string message,
        string content,
        string branch,
        string expectedBlobSha,
        CancellationToken ct = default);
}

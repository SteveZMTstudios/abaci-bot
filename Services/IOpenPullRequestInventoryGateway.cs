using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Narrow gateway supplementing pull request label searches with REST branch-prefix inventory
/// and verified label assurance without expanding <see cref="IGitHubService"/>.
/// </summary>
public interface IOpenPullRequestInventoryGateway
{
    Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsByHeadPrefixAsync(
        string owner,
        string repo,
        string branchPrefix,
        CancellationToken ct = default);

    Task<bool> EnsureRequiredLabelsVerifiedAsync(
        string owner,
        string repo,
        IEnumerable<(string Name, string Color, string Description)> labels,
        CancellationToken ct = default);
}

using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.PullRequestReviewComment;

namespace abaci_bot.Modules;

/// <summary>
/// Shiny New Pull Request governance module (Priority: 15).
/// Tags newly opened PRs with the "Status: New" label to highlight them for reviewer triage.
/// Automatically schedules removal of the "Status: New" label as soon as the first valid human review
/// or comment (from someone other than the author and bots) is submitted.
/// </summary>
public class ShinyNewPrModule : IPullRequestModule
{
    private readonly BotDetectionOptions _botOptions;
    private readonly PackageUpdateOptions _packageUpdateOptions;
    private readonly IBotIdentityProvider? _botIdentity;

    public string ModuleName => "ShinyNewPrModule";

    public int Priority => 15;

    public ShinyNewPrModule(
        IOptions<BotDetectionOptions>? botOptions = null,
        IBotIdentityProvider? botIdentity = null,
        IOptions<PackageUpdateOptions>? packageUpdateOptions = null)
    {
        _botOptions = botOptions?.Value ?? new BotDetectionOptions();
        _botIdentity = botIdentity;
        _packageUpdateOptions = packageUpdateOptions?.Value ?? new PackageUpdateOptions();
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType switch
        {
            PullRequestEventType.PullRequest =>
                !context.IsTerminal && context.PullRequestAction == PullRequestAction.Opened,

            PullRequestEventType.Review =>
                context.PullRequestReviewAction == PullRequestReviewAction.Submitted,

            PullRequestEventType.IssueComment =>
                context.IssueCommentAction == IssueCommentAction.Created,

            PullRequestEventType.ReviewComment =>
                context.PullRequestReviewCommentAction == PullRequestReviewCommentAction.Created,

            _ => false
        };
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        // 1. Tag newly opened non-terminal PRs with the "Status: New" indicator
        if (context.EventType == PullRequestEventType.PullRequest)
        {
            if (!context.IsTerminal && context.PullRequestAction == PullRequestAction.Opened)
            {
                context.LabelsToAdd.Add(GitHubLabels.StatusNew);
            }

            return;
        }

        // 2. On review or comment: if the PR currently carries "Status: New", remove it upon first human non-author engagement
        if (context.ExistingLabels.Contains(GitHubLabels.StatusNew))
        {
            if (!context.IsSenderAuthorOrBot(_botOptions.Whitelist) &&
                !await BotCommitOwnership.IsOwnBotLoginAsync(_botIdentity, context.SenderLogin, _packageUpdateOptions.BotLogin, cancellationToken))
            {
                context.LabelsToRemove.Add(GitHubLabels.StatusNew);
            }
        }
    }
}

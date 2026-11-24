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
/// Instant Un-stale webhook module (Priority: 12).
/// Automatically schedules removal of "Status: Stale" when human activity
/// (new commit, reopen, human comment, or review) occurs on a pull request.
/// </summary>
public class StaleUnstaleModule : IPullRequestModule
{
    private readonly BotDetectionOptions _botOptions;
    private readonly PackageUpdateOptions _packageUpdateOptions;
    private readonly IBotIdentityProvider? _botIdentity;

    public string ModuleName => "StaleUnstaleModule";

    public int Priority => 12;

    public StaleUnstaleModule(
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
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened,

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
        if (!context.ExistingLabels.Contains(GitHubLabels.StatusStale))
            return;

        // Check if sender is a bot or our own configured PAT identity; automated actions should not un-stale objects.
        if (BotDetectionHelper.IsBot(context.SenderLogin, context.SenderType, _botOptions.Whitelist) ||
            await BotCommitOwnership.IsOwnBotLoginAsync(_botIdentity, context.SenderLogin, _packageUpdateOptions.BotLogin, cancellationToken))
            return;

        // Human author or reviewer activity restores the PR from stale
        context.LabelsToRemove.Add(GitHubLabels.StatusStale);
    }
}

using abaci_bot.Contexts;

namespace abaci_bot.Services;

/// <summary>
/// Service for conversation locking, unlock, spam comment hiding, and PR archiving commands.
/// </summary>
public interface IConversationGuardService
{
    /// <summary>
    /// Processes a conversation guard command (<c>@&lt;bot&gt; lock</c>, <c>@&lt;bot&gt; unlock</c>, <c>@&lt;bot&gt; hide @user</c>, <c>@&lt;bot&gt; archive</c>) from an issue comment.
    /// </summary>
    Task HandleCommandAsync(
        PullRequestContext context,
        string commentBody,
        string senderLogin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans for closed pull requests older than inactiveDays and locks them with TooHeated lock reason.
    /// </summary>
    Task LockInactivePullRequestsAsync(
        string owner,
        string repo,
        int inactiveDays,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

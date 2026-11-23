namespace abaci_bot.Services;

/// <summary>
/// Author and committer metadata for the latest commit on a branch,
/// used to distinguish automated bot commits from human or external-bot edits.
/// </summary>
public sealed record BranchCommitInfo(
    string Sha,
    string Message,
    string AuthorLogin,
    string AuthorEmail,
    string CommitterLogin,
    string CommitterEmail);

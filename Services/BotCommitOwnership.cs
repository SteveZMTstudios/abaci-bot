namespace abaci_bot.Services;

/// <summary>
/// Centralizes bot GitHub identity matching and commit ownership verification so branch-prefix
/// PR discovery, stale TTL cleanup, and update publishing share identical anti-spoofing rules.
/// </summary>
internal static class BotCommitOwnership
{
    public static HashSet<string> BuildOwnBotLoginSet(string? liveBotLogin, string? configuredBotLogin)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentityVariants(set, liveBotLogin);
        AddIdentityVariants(set, configuredBotLogin);
        return set;
    }

    public static bool IsOwnBotLogin(string? login, IReadOnlySet<string> ownLogins)
    {
        if (string.IsNullOrWhiteSpace(login) || ownLogins.Count == 0)
        {
            return false;
        }

        return ownLogins.Contains(login.Trim());
    }

    public static bool IsOwnBotCommit(BranchCommitInfo? commit, IReadOnlySet<string> ownLogins)
    {
        if (commit == null || !IsOwnBotLogin(commit.AuthorLogin, ownLogins))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(commit.CommitterLogin))
        {
            var committer = commit.CommitterLogin.Trim();
            var isAuthorizedCommitter = ownLogins.Contains(committer) ||
                                        string.Equals(committer, "web-flow", StringComparison.OrdinalIgnoreCase);
            if (!isAuthorizedCommitter)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIdentityVariants(HashSet<string> set, string? rawLogin)
    {
        if (string.IsNullOrWhiteSpace(rawLogin))
        {
            return;
        }

        var trimmed = rawLogin.Trim();
        set.Add(trimmed);

        const string botSuffix = "[bot]";
        if (trimmed.EndsWith(botSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var bare = trimmed[..^botSuffix.Length];
            if (!string.IsNullOrWhiteSpace(bare))
            {
                set.Add(bare);
            }
        }
        else
        {
            set.Add($"{trimmed}{botSuffix}");
        }
    }
}

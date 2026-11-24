namespace abaci_bot.Services;

/// <summary>
/// Centralizes bot GitHub identity matching and commit ownership verification so branch-prefix
/// PR discovery, stale TTL cleanup, and update publishing share identical anti-spoofing rules.
/// </summary>
internal static class BotCommitOwnership
{
    public static HashSet<string> BuildOwnBotLoginSet(
        string? liveBotLogin,
        string? configuredBotLogin,
        bool requireLive = false)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(liveBotLogin))
        {
            AddIdentityVariants(set, liveBotLogin);
            return set;
        }

        if (requireLive)
        {
            return set;
        }

        AddIdentityVariants(set, configuredBotLogin);
        return set;
    }

    public static async Task<HashSet<string>> ResolveOwnBotLoginsAsync(
        IBotIdentityProvider? botIdentity,
        string? configuredBotLogin,
        bool requireLiveWhenProviderPresent,
        CancellationToken ct = default)
    {
        string? liveBotLogin = null;
        if (botIdentity != null)
        {
            try
            {
                liveBotLogin = await botIdentity.GetBotLoginAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                liveBotLogin = null;
            }
        }

        return BuildOwnBotLoginSet(
            liveBotLogin,
            configuredBotLogin,
            requireLive: requireLiveWhenProviderPresent && botIdentity != null);
    }

    public static bool IsOwnBotLogin(string? login, IReadOnlySet<string> ownLogins)
    {
        if (string.IsNullOrWhiteSpace(login) || ownLogins.Count == 0)
        {
            return false;
        }

        return ownLogins.Contains(login.Trim());
    }

    public static async Task<bool> IsOwnBotLoginAsync(
        IBotIdentityProvider? botIdentity,
        string? senderLogin,
        string? configuredBotLogin = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(senderLogin))
        {
            return false;
        }

        var ownLogins = await ResolveOwnBotLoginsAsync(
            botIdentity,
            configuredBotLogin,
            requireLiveWhenProviderPresent: false,
            ct);
        return IsOwnBotLogin(senderLogin, ownLogins);
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

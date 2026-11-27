using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Pull request reminder module (Priority: 22) that maintains a single idempotent
/// <c>&gt; [!TIP]</c> comment encouraging contributors to keep WIP titles and draft state aligned.
/// </summary>
public class WipDraftModule : IPullRequestModule
{
    private readonly WipDraftOptions _options;
    private readonly IGovernanceCommentFormatter _formatter;
    private readonly IBotIdentityProvider? _botIdentity;
    private readonly string? _configuredBotLogin;
    private readonly RuntimeHealthState? _runtimeHealth;
    private readonly ILogger<WipDraftModule>? _logger;

    private sealed record ExistingTipComment(long Id, WipTipKind Kind, string Body);

    public string ModuleName => "WipDraftModule";

    public int Priority => 22;

    public WipDraftModule(
        IOptions<WipDraftOptions>? options = null,
        IGovernanceCommentFormatter? formatter = null,
        IBotIdentityProvider? botIdentity = null,
        IOptions<PackageUpdateOptions>? packageUpdateOptions = null,
        ILogger<WipDraftModule>? logger = null,
        RuntimeHealthState? runtimeHealth = null)
    {
        _options = options?.Value ?? new WipDraftOptions();
        _formatter = formatter ?? new GovernanceCommentFormatter();
        _botIdentity = botIdentity;
        var packageUpdate = packageUpdateOptions?.Value;
        _configuredBotLogin = !string.IsNullOrWhiteSpace(packageUpdate?.BotPat)
            ? packageUpdate.BotLogin
            : null;
        _logger = logger;
        _runtimeHealth = runtimeHealth;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        return _options.Enabled &&
               context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.Edited ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.Closed);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        // Avoid scanning comments on ordinary synchronize pushes for clean non-WIP, non-draft open PRs.
        if (context.PullRequestAction == PullRequestAction.Synchronize &&
            !context.IsTerminal &&
            !WipTitleHelper.IsWip(context.Title) &&
            !context.IsDraft)
        {
            return;
        }

        var inspection = await FindExistingTipCommentsAsync(context, cancellationToken);

        // Fail closed when bot identity cannot be resolved so another bot's comment is never mutated.
        if (inspection == null)
        {
            return;
        }

        var (existingTips, botDisplayName) = inspection.Value;

        var desiredKind = ResolveDesiredTipKind(context, existingTips);
        if (desiredKind == null)
        {
            QueueDeletions(context, existingTips);
            return;
        }

        var prUrl = $"https://github.com/{context.Owner}/{context.Repo}/pull/{context.PrNumber}";
        var targetBody = _formatter.FormatWipTip(
            desiredKind.Value,
            prUrl,
            context.PreferredLocale,
            _options.DocumentationLinks,
            botDisplayName);
        if (string.IsNullOrWhiteSpace(targetBody))
        {
            return;
        }

        var targetMarker = desiredKind.Value == WipTipKind.Draft
            ? GovernanceCommentFormatter.WipDraftTipMarker
            : GovernanceCommentFormatter.WipReadyTipMarker;

        if (existingTips.Count == 0)
        {
            if (_options.DryRun)
            {
                _logger?.LogInformation(
                    "[DryRun] WipDraftModule would create {Kind} TIP comment on {Owner}/{Repo}#{PrNumber}.",
                    desiredKind.Value, context.Owner, context.Repo, context.PrNumber);
                return;
            }

            context.PendingNotices.Add(new NoticeIntent(targetMarker, targetBody));
            return;
        }

        var primary = existingTips[0];
        var duplicates = existingTips.Skip(1).ToList();
        var needsUpdate = primary.Kind != desiredKind.Value ||
                          !string.Equals(primary.Body.Trim(), targetBody.Trim(), StringComparison.Ordinal);

        if (_options.DryRun)
        {
            if (needsUpdate || duplicates.Count > 0)
            {
                _logger?.LogInformation(
                    "[DryRun] WipDraftModule would reconcile {Kind} TIP comment (update={NeedsUpdate}, deleteDuplicates={DupCount}) on {Owner}/{Repo}#{PrNumber}.",
                    desiredKind.Value, needsUpdate, duplicates.Count, context.Owner, context.Repo, context.PrNumber);
            }

            return;
        }

        if (needsUpdate)
        {
            context.PendingNotices.Add(new NoticeIntent(targetMarker, targetBody, ExistingCommentId: primary.Id));
        }

        QueueDeletions(context, duplicates);
    }

    /// <summary>
    /// Validates that <paramref name="body"/> strictly matches the standalone bot TIP comment shape
    /// rather than containing an injected marker inside an arbitrary governance or review comment.
    /// </summary>
    internal static bool TryParseTipKind(string? body, out WipTipKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        string marker;
        if (normalized.EndsWith($"\n{GovernanceCommentFormatter.WipDraftTipMarker}", StringComparison.Ordinal))
        {
            kind = WipTipKind.Draft;
            marker = GovernanceCommentFormatter.WipDraftTipMarker;
        }
        else if (normalized.EndsWith($"\n{GovernanceCommentFormatter.WipReadyTipMarker}", StringComparison.Ordinal))
        {
            kind = WipTipKind.Ready;
            marker = GovernanceCommentFormatter.WipReadyTipMarker;
        }
        else
        {
            return false;
        }

        var bodyBeforeMarker = normalized[..^(marker.Length + 1)];
        var lines = bodyBeforeMarker.Split('\n');
        var tipStartIndex = Array.FindIndex(lines, static line => string.Equals(line, "> [!TIP]", StringComparison.Ordinal));
        if (tipStartIndex < 0 || tipStartIndex + 1 >= lines.Length)
        {
            return false;
        }

        // Reject multi-callout review comments or injected callouts anywhere else in the body.
        for (var i = 0; i < lines.Length; i++)
        {
            if (i != tipStartIndex && lines[i].TrimStart().StartsWith("> [!", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (tipStartIndex > 0)
        {
            // Unified frame must start with the standard consolidated review opener + heading and nothing else before > [!TIP].
            if (tipStartIndex < 3 ||
                (!string.Equals(lines[0], GovernanceCommentFormatter.ConsolidatedOpenerEn, StringComparison.Ordinal) &&
                 !string.Equals(lines[0], GovernanceCommentFormatter.ConsolidatedOpenerZh, StringComparison.Ordinal)) ||
                lines[1].Length != 0 ||
                !lines[2].StartsWith("## ", StringComparison.Ordinal) ||
                (!lines[2].EndsWith(" Review Notes", StringComparison.Ordinal) &&
                 !lines[2].EndsWith(" 审查说明", StringComparison.Ordinal)))
            {
                return false;
            }

            for (var i = 3; i < tipStartIndex; i++)
            {
                if (lines[i].Length != 0)
                {
                    return false;
                }
            }
        }

        var expectedLeadMatch = kind == WipTipKind.Draft
            ? string.Equals(lines[tipStartIndex + 1], GovernanceCommentFormatter.WipDraftTipLeadEn, StringComparison.Ordinal) ||
              string.Equals(lines[tipStartIndex + 1], GovernanceCommentFormatter.WipDraftTipLeadZh, StringComparison.Ordinal)
            : string.Equals(lines[tipStartIndex + 1], GovernanceCommentFormatter.WipReadyTipLeadEn, StringComparison.Ordinal) ||
              string.Equals(lines[tipStartIndex + 1], GovernanceCommentFormatter.WipReadyTipLeadZh, StringComparison.Ordinal);
        if (!expectedLeadMatch)
        {
            return false;
        }

        var index = tipStartIndex + 1;
        while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
        {
            index++;
        }

        // After the > [!TIP] blockquote, permit only blank lines and the unified "See also" footer list.
        var sawFooterHeader = false;
        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            if (!sawFooterHeader &&
                (string.Equals(line, "For more details, see:", StringComparison.Ordinal) ||
                 string.Equals(line, "更多参考文档：", StringComparison.Ordinal)))
            {
                sawFooterHeader = true;
                continue;
            }

            if (sawFooterHeader && line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static WipTipKind? ResolveDesiredTipKind(
        PullRequestContext context,
        IReadOnlyList<ExistingTipComment> existingTips)
    {
        if (context.IsTerminal)
        {
            return null;
        }

        var hasWip = WipTitleHelper.IsWip(context.Title);
        var isDraft = context.IsDraft;

        if (hasWip)
        {
            return isDraft ? null : WipTipKind.Draft;
        }

        if (!isDraft)
        {
            return null;
        }

        // Respect manual drafts unless this edit event just removed a WIP token from the PR title.
        var justRemovedWip = context.PullRequestAction == PullRequestAction.Edited &&
                             WipTitleHelper.IsWip(context.PreviousTitle);
        return existingTips.Count > 0 || justRemovedWip ? WipTipKind.Ready : null;
    }

    private void QueueDeletions(PullRequestContext context, IEnumerable<ExistingTipComment> tipsToDelete)
    {
        foreach (var tip in tipsToDelete)
        {
            var marker = tip.Kind == WipTipKind.Draft
                ? GovernanceCommentFormatter.WipDraftTipMarker
                : GovernanceCommentFormatter.WipReadyTipMarker;

            if (_options.DryRun)
            {
                _logger?.LogInformation(
                    "[DryRun] WipDraftModule would delete {Kind} TIP comment {CommentId} on {Owner}/{Repo}#{PrNumber}.",
                    tip.Kind, tip.Id, context.Owner, context.Repo, context.PrNumber);
                continue;
            }

            context.PendingNotices.Add(new NoticeIntent(marker, Body: null, ExistingCommentId: tip.Id, Delete: true));
        }
    }

    private async Task<(IReadOnlyList<ExistingTipComment> Tips, string? BotDisplayName)?> FindExistingTipCommentsAsync(
        PullRequestContext context,
        CancellationToken cancellationToken)
    {
        var ownBotLogins = await BotCommitOwnership.ResolveOwnBotLoginsAsync(
            _botIdentity,
            _configuredBotLogin,
            requireLiveWhenProviderPresent: false,
            cancellationToken);

        if (ownBotLogins.Count == 0)
        {
            var identityError = new InvalidOperationException(
                $"Unable to resolve own bot identity for WIP TIP reconciliation on {context.Owner}/{context.Repo}#{context.PrNumber}.");
            _runtimeHealth?.RecordError(
                nameof(WipDraftModule),
                identityError,
                $"Own bot identity unresolved during WIP TIP reconciliation on {context.Owner}/{context.Repo}#{context.PrNumber}");
            _logger?.LogWarning(
                "Unable to resolve own bot identity while reconciling WIP TIP comments on {Owner}/{Repo}#{PrNumber}; skipping fail-closed.",
                context.Owner, context.Repo, context.PrNumber);
            return null;
        }

        IReadOnlyList<IssueComment> comments;
        try
        {
            comments = await context.IssueComments.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeHealth?.RecordError(
                nameof(WipDraftModule),
                ex,
                $"Failed to inspect WIP TIP comments on {context.Owner}/{context.Repo}#{context.PrNumber}");
            _logger?.LogWarning(
                ex,
                "Could not inspect existing WIP TIP comments on PR {Owner}/{Repo}#{PrNumber}; skipping reconciliation for this delivery.",
                context.Owner, context.Repo, context.PrNumber);
            return null;
        }

        var matches = new List<ExistingTipComment>();
        foreach (var comment in comments)
        {
            if (comment is null ||
                !BotCommitOwnership.IsOwnBotLogin(comment.User?.Login, ownBotLogins) ||
                !TryParseTipKind(comment.Body, out var kind))
            {
                continue;
            }

            matches.Add(new ExistingTipComment(comment.Id, kind, comment.Body));
        }

        matches.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return (matches, ownBotLogins.FirstOrDefault());
    }
}

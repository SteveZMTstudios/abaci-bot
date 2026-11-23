using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Deep module publisher implementing idempotent long-lived branches (autoupdate/{pkg}).
/// Enforces committer whitelisting (protecting human commits), branch version checks (zero-push),
/// and PR template compliance with upstream openRuyi guidelines.
/// </summary>
public class GitHubPackageUpdatePublisher : IPackageUpdatePublisher
{
    private readonly IGitHubService _gitHubService;
    private readonly PackageUpdateOptions _options;
    private readonly IBotIdentityProvider? _botIdentity;
    private readonly ILocaleResolver? _localeResolver;
    private readonly ILogger<GitHubPackageUpdatePublisher>? _logger;

    public GitHubPackageUpdatePublisher(
        IGitHubService gitHubService,
        IOptions<PackageUpdateOptions> options,
        ILogger<GitHubPackageUpdatePublisher>? logger = null,
        IBotIdentityProvider? botIdentity = null,
        ILocaleResolver? localeResolver = null)
    {
        _gitHubService = gitHubService;
        _options = options.Value;
        _logger = logger;
        _botIdentity = botIdentity ?? gitHubService as IBotIdentityProvider;
        _localeResolver = localeResolver;
    }

    public async Task<PublishResult> PublishUpdateAsync(
        UpdateCandidate candidate,
        SpecRewriteResult rewriteResult,
        string targetRepo,
        string baseBranch = "main",
        string? specPath = null,
        CancellationToken ct = default)
    {
        if (candidate == null || rewriteResult == null || !rewriteResult.Success || !rewriteResult.HasChanged)
        {
            return new PublishResult(false, true, "Rewrite result is invalid or has no changes");
        }

        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
        {
            return new PublishResult(false, false, $"Invalid target repository '{targetRepo}'");
        }

        var owner = parts[0].Trim();
        var repo = parts[1].Trim();

        if (string.IsNullOrWhiteSpace(specPath))
        {
            throw new ArgumentException("Authoritative SPEC file relative path must be provided by the spec catalog.", nameof(specPath));
        }

        var branchPrefix = string.IsNullOrWhiteSpace(_options.BranchPrefix) ? "autoupdate/" : _options.BranchPrefix;
        var branchName = $"{branchPrefix}{candidate.PackageName}";
        var finalSpecPath = specPath;

        string? botLogin = null;
        if (_botIdentity != null)
        {
            try
            {
                botLogin = await _botIdentity.GetBotLoginAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Could not resolve live bot login");
            }
        }

        var existingBranchSha = await _gitHubService.GetRefShaAsync(owner, repo, branchName, ct);
        var existingPr = await _gitHubService.FindOpenPullRequestByHeadAsync(owner, repo, branchName, ct);

        if (!string.IsNullOrEmpty(existingBranchSha))
        {
            // 1. Author-first own-bot commit check (protect web-flow edits, external bots, local amends, and empty identities)
            BranchCommitInfo? latestCommit;
            try
            {
                latestCommit = await _gitHubService.GetBranchLatestCommitAsync(owner, repo, branchName, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to read latest commit on branch {Branch}; failing closed", branchName);
                return new PublishResult(false, false, $"Failed to verify committer identity on branch '{branchName}' (fail-closed)");
            }

            if (latestCommit == null)
            {
                _logger?.LogWarning("Could not retrieve latest commit info for branch {Branch}; failing closed", branchName);
                return new PublishResult(false, false, $"Branch commit details unavailable for '{branchName}' (fail-closed)");
            }

            if (!IsOwnBotCommit(latestCommit, botLogin))
            {
                var authorDisplay = !string.IsNullOrWhiteSpace(latestCommit.AuthorLogin)
                    ? latestCommit.AuthorLogin.Trim()
                    : (!string.IsNullOrWhiteSpace(latestCommit.CommitterLogin) ? latestCommit.CommitterLogin.Trim() : "unknown");
                var actorMention = !string.IsNullOrWhiteSpace(latestCommit.AuthorLogin)
                    ? $"@{authorDisplay}"
                    : $"`{authorDisplay}`";

                _logger?.LogWarning(
                    "Non-automated commits detected on branch {Branch} (author={Author}, committer={Committer}); stepping aside",
                    branchName, latestCommit.AuthorLogin, latestCommit.CommitterLogin);

                if (existingPr != null && !_options.DryRun)
                {
                    bool alreadyCommented = false;
                    try
                    {
                        var existingComments = await _gitHubService.GetIssueCommentsAsync(owner, repo, existingPr.Number, ct);
                        alreadyCommented = existingComments != null && existingComments.Any(c =>
                            c.Body != null &&
                            c.Body.Contains("stepped aside", StringComparison.OrdinalIgnoreCase) &&
                            c.Body.Contains(branchName, StringComparison.OrdinalIgnoreCase) &&
                            c.Body.Contains($"`{candidate.TargetVersion}`", StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogDebug(ex, "Failed to inspect existing stepped-aside comments on PR #{PrNumber}", existingPr.Number);
                    }

                    if (!alreadyCommented)
                    {
                        var warningComment = $"A newer upstream version `{candidate.TargetVersion}` is available for `{candidate.PackageName}`, " +
                                             $"but branch `{branchName}` contains human commits by {actorMention}. " +
                                             "Automated update has stepped aside to prevent overwriting manual work.";
                        await _gitHubService.CreateCommentAsync(owner, repo, existingPr.Number, warningComment, ct);
                    }
                }

                return new PublishResult(true, true, $"Human commits by {authorDisplay} detected on branch");
            }

            // 2. Branch SPEC version reading (strong idempotency: skip if already at target version, ignoring +suffix)
            try
            {
                var branchSpecContent = await _gitHubService.GetFileContentAsync(owner, repo, finalSpecPath, branchName, ct);
                if (UpdateAdmission.IsVersionAtTarget(branchSpecContent, candidate.TargetVersion))
                {
                    _logger?.LogInformation("Branch {Branch} is already at target version {Version}; skipping push",
                        branchName, candidate.TargetVersion);
                    return new PublishResult(true, true, $"Branch already at target version {candidate.TargetVersion}",
                        existingPr?.Number, existingPr?.HtmlUrl);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not read existing SPEC from branch {Branch}; failing closed", branchName);
                return new PublishResult(false, false, $"Failed to read spec on branch '{branchName}' (fail-closed)");
            }
        }
        else
        {
            // Create the branch starting from baseBranch
            var baseSha = await _gitHubService.GetRefShaAsync(owner, repo, baseBranch, ct);
            if (string.IsNullOrEmpty(baseSha))
            {
                return new PublishResult(false, false, $"Base branch '{baseBranch}' ref not found");
            }

            if (!_options.DryRun)
            {
                await _gitHubService.CreateOrUpdateRefAsync(owner, repo, branchName, baseSha, force: false, ct);
            }
        }

        var commitMessage = $"SPECS: bump {candidate.PackageName} {candidate.CurrentVersion} -> {candidate.TargetVersion}";
        var prTitle = commitMessage;
        var locale = _localeResolver?.ResolveForBotPullRequest(owner, repo);
        var prBody = GeneratePullRequestBody(candidate, locale, botLogin);

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would write commit '{Message}' to branch '{Branch}' and upsert PR", commitMessage, branchName);
            return new PublishResult(true, false, "[DryRun] Simulated publish", existingPr?.Number, existingPr?.HtmlUrl);
        }

        // Commit updated SPEC to branch
        await _gitHubService.UpdateFileContentAsync(
            owner, repo, finalSpecPath, commitMessage, rewriteResult.UpdatedContent, branchName, ct);

        var requiredLabels = GetRequiredLabels(candidate);
        PullRequest pr;
        if (existingPr != null)
        {
            _logger?.LogInformation("Updating existing PR #{PrNumber} for branch {Branch}", existingPr.Number, branchName);
            pr = await _gitHubService.UpdatePullRequestAsync(owner, repo, existingPr.Number, prTitle, prBody, ct);

            var labelsToApply = existingPr.Labels != null && existingPr.Labels.Count > 0
                ? requiredLabels
                    .Where(req => !existingPr.Labels.Any(l => string.Equals(l.Name, req, StringComparison.OrdinalIgnoreCase)))
                    .ToArray()
                : requiredLabels;

            if (labelsToApply.Length > 0 && !await TryEnsurePullRequestLabelsAsync(owner, repo, existingPr.Number, labelsToApply, ct))
            {
                return new PublishResult(false, false, $"Failed to apply required labels to existing PR #{existingPr.Number}", pr.Number, pr.HtmlUrl);
            }
        }
        else
        {
            _logger?.LogInformation("Creating new PR for branch {Branch}", branchName);
            pr = await _gitHubService.CreatePullRequestAsync(owner, repo, prTitle, branchName, baseBranch, prBody, ct);

            if (!await TryEnsurePullRequestLabelsAsync(owner, repo, pr.Number, requiredLabels, ct))
            {
                return new PublishResult(false, false, $"Failed to apply required labels to new PR #{pr.Number}", pr.Number, pr.HtmlUrl);
            }
        }

        return new PublishResult(true, false, null, pr.Number, pr.HtmlUrl);
    }

    internal static string[] GetRequiredLabels(UpdateCandidate candidate)
    {
        var labels = new List<string>(3)
        {
            GitHubLabels.AutoUpdate,
            GitHubLabels.StatusStaleExempt
        };
        if (candidate.Origin == CandidateOrigin.RustCrate)
        {
            labels.Add(GitHubLabels.AutoUpdateRust);
        }

        return labels.ToArray();
    }

    private async Task<bool> TryEnsurePullRequestLabelsAsync(
        string owner,
        string repo,
        int prNumber,
        string[] labels,
        CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _gitHubService.AddLabelsAsync(owner, repo, prNumber, labels, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == maxAttempts)
                {
                    _logger?.LogError(ex, "Failed to apply required labels ({Labels}) to PR #{PrNumber} after {Attempts} attempts",
                        string.Join(", ", labels), prNumber, maxAttempts);
                }
                else
                {
                    _logger?.LogWarning(ex, "Attempt {Attempt} to apply labels to PR #{PrNumber} failed; retrying", attempt, prNumber);
                }
            }
        }

        return false;
    }

    internal bool IsOwnBotCommit(BranchCommitInfo commit, string? liveBotLogin = null)
    {
        var ownLogins = BotCommitOwnership.BuildOwnBotLoginSet(liveBotLogin, _options.BotLogin);
        return BotCommitOwnership.IsOwnBotCommit(commit, ownLogins);
    }

    public static string GeneratePullRequestBody(UpdateCandidate candidate, string? locale = null, string? botLogin = null)
    {
        var isZh = string.Equals(locale, "zh", StringComparison.OrdinalIgnoreCase) ||
                   (locale != null && locale.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));
        var mentionHint = !string.IsNullOrWhiteSpace(botLogin)
            ? (isZh
                ? $"评论 `@{botLogin} ignore {candidate.PackageName}` 或在 SPEC 文件头部加入 `#!NoAutoUpdate: <reason>`"
                : $"comment `@{botLogin} ignore {candidate.PackageName}` or add `#!NoAutoUpdate: <reason>` to the SPEC file header")
            : (isZh
                ? "在 SPEC 文件头部加入 `#!NoAutoUpdate: <reason>`"
                : "add `#!NoAutoUpdate: <reason>` to the SPEC file header");

        if (isZh)
        {
            return $"""
                ## 摘要
                自动将 `{candidate.PackageName}` 从 `{candidate.CurrentVersion}` 更新至 `{candidate.TargetVersion}`。

                - **来源**: {candidate.Origin}
                - **依据**: {candidate.EvidenceUrl}

                ## AI 辅助贡献声明
                - [ ] 我已阅读 [AI-Assisted Contribution Policy]，本 Pull Request 包含非平凡的 AI 辅助内容。
                <!-- Assisted-by: None (automated rule-based bot) -->

                ## 检查清单
                - [x] 已在 spec 文件中更新包版本
                - [ ] 已构建并测试

                > 提交与描述由机器人自动维护。
                > 如需停止本包的自动更新，请{mentionHint}。
                """;
        }

        return $"""
            ## Summary
            Automatic version bump for `{candidate.PackageName}` from `{candidate.CurrentVersion}` to `{candidate.TargetVersion}`.

            - **Source**: {candidate.Origin}
            - **Evidence**: {candidate.EvidenceUrl}

            ## AI-Assisted Contribution
            - [ ] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.
            <!-- Assisted-by: None (automated rule-based bot) -->

            ## Checklist
            - [x] Package version updated in spec file
            - [ ] Built and tested

            > Commits and description are automatically maintained by the bot.
            > To pause automated updates for this package, {mentionHint}.
            """;
    }
}


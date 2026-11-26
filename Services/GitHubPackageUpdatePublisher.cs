using System.Net;
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
    private static readonly Regex PatchDirectiveRegex = new(
        @"^Patch\d*:",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly IGitHubService _gitHubService;
    private readonly IOptimisticFileContentGateway _optimisticFileGateway;
    private readonly PackageUpdateOptions _options;
    private readonly IBotIdentityProvider? _botIdentity;
    private readonly ILocaleResolver? _localeResolver;
    private readonly IReadOnlyList<ISpecVersionRewriter> _specRewriters;
    private readonly ILogger<GitHubPackageUpdatePublisher>? _logger;

    public GitHubPackageUpdatePublisher(
        IGitHubService gitHubService,
        IOptions<PackageUpdateOptions> options,
        ILogger<GitHubPackageUpdatePublisher>? logger = null,
        IBotIdentityProvider? botIdentity = null,
        ILocaleResolver? localeResolver = null,
        IEnumerable<ISpecVersionRewriter>? specRewriters = null,
        IOptimisticFileContentGateway? optimisticFileGateway = null)
    {
        _gitHubService = gitHubService ?? throw new ArgumentNullException(nameof(gitHubService));
        _optimisticFileGateway = optimisticFileGateway
            ?? gitHubService as IOptimisticFileContentGateway
            ?? throw new ArgumentException(
                "IGitHubService must implement IOptimisticFileContentGateway or an explicit optimisticFileGateway must be provided.",
                nameof(optimisticFileGateway));
        _options = options.Value;
        _logger = logger;
        _botIdentity = botIdentity ?? gitHubService as IBotIdentityProvider;
        _localeResolver = localeResolver;
        _specRewriters = specRewriters != null
            ? specRewriters.ToList()
            : new List<ISpecVersionRewriter> { new PlainSpecVersionRewriter(), new RustCrateSpecRewriter() };
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

        var effectiveContent = rewriteResult.UpdatedContent;
        var effectiveCandidate = candidate;
        var needsBranchCommit = true;
        string? targetBranchBlobSha = null;

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

            if (_botIdentity != null && string.IsNullOrWhiteSpace(botLogin))
            {
                _logger?.LogWarning("Could not resolve live bot identity for branch {Branch}; failing closed", branchName);
                return new PublishResult(false, false, $"Live bot identity unavailable for '{branchName}' (fail-closed)");
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
            string? branchSpecContent;
            var branchAlreadyAtTarget = false;
            try
            {
                var branchFile = await _optimisticFileGateway.GetFileContentWithShaAsync(owner, repo, finalSpecPath, branchName, ct);
                if (branchFile == null || string.IsNullOrWhiteSpace(branchFile.BlobSha))
                {
                    _logger?.LogWarning("Could not retrieve blob SHA for existing SPEC {SpecPath} on branch {Branch}; failing closed",
                        finalSpecPath, branchName);
                    return new PublishResult(false, false, $"Failed to resolve blob SHA on branch '{branchName}' (fail-closed)");
                }

                branchSpecContent = branchFile.Content;
                targetBranchBlobSha = branchFile.BlobSha;
                if (UpdateAdmission.IsVersionAtTarget(branchSpecContent, candidate.TargetVersion))
                {
                    if (existingPr != null)
                    {
                        _logger?.LogInformation("Branch {Branch} and PR #{PrNumber} are already at target version {Version}; skipping push",
                            branchName, existingPr.Number, candidate.TargetVersion);
                        return new PublishResult(true, true, $"Branch already at target version {candidate.TargetVersion}",
                            existingPr.Number, existingPr.HtmlUrl);
                    }

                    _logger?.LogInformation("Branch {Branch} is already at target version {Version} without an open PR; verifying against live base before creating PR",
                        branchName, candidate.TargetVersion);
                    branchAlreadyAtTarget = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not read existing SPEC from branch {Branch}; failing closed", branchName);
                return new PublishResult(false, false, $"Failed to read spec on branch '{branchName}' (fail-closed)");
            }

            // 3. Re-verify and re-rewrite against live baseBranch SPEC so #!NoAutoUpdate, admission blocks,
            // and non-version edits merged into main after the PR opened are preserved (including orphan branches).
            var liveBaseSha = await _gitHubService.GetRefShaAsync(owner, repo, baseBranch, ct);
            var baseRefForCheck = !string.IsNullOrEmpty(liveBaseSha) ? liveBaseSha : baseBranch;
            var baseCheck = await VerifyLiveBaseSpecAsync(
                owner, repo, finalSpecPath, baseRefForCheck, candidate, rewriteResult, ct);
            if (baseCheck.AbortResult != null)
            {
                return baseCheck.AbortResult;
            }
            effectiveContent = baseCheck.EffectiveContent;
            effectiveCandidate = baseCheck.EffectiveCandidate;

            if (branchAlreadyAtTarget &&
                (string.Equals(NormalizeLineEndings(branchSpecContent), NormalizeLineEndings(effectiveContent), StringComparison.Ordinal) ||
                 AreEqualIgnoringRemoteAssetLines(branchSpecContent, effectiveContent)))
            {
                needsBranchCommit = false;
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

            // Re-verify and re-rewrite against live base SPEC at baseSha so stale catalog caches never overwrite main commits or bypass #!NoAutoUpdate.
            var baseCheck = await VerifyLiveBaseSpecAsync(
                owner, repo, finalSpecPath, baseSha, candidate, rewriteResult, ct);
            if (baseCheck.AbortResult != null)
            {
                return baseCheck.AbortResult;
            }
            effectiveContent = baseCheck.EffectiveContent;
            effectiveCandidate = baseCheck.EffectiveCandidate;
            targetBranchBlobSha = baseCheck.BaseBlobSha;

            if (!_options.DryRun)
            {
                await _gitHubService.CreateOrUpdateRefAsync(owner, repo, branchName, baseSha, force: false, ct);
            }
        }

        var commitMessage = $"SPECS: bump {effectiveCandidate.PackageName} {effectiveCandidate.CurrentVersion} -> {effectiveCandidate.TargetVersion}";
        var prTitle = commitMessage;
        var locale = _localeResolver?.ResolveForBotPullRequest(owner, repo);
        var prBody = GeneratePullRequestBody(effectiveCandidate, locale, botLogin);

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would write commit '{Message}' to branch '{Branch}' and upsert PR", commitMessage, branchName);
            return new PublishResult(true, false, "[DryRun] Simulated publish", existingPr?.Number, existingPr?.HtmlUrl);
        }

        if (needsBranchCommit)
        {
            if (!string.IsNullOrEmpty(existingBranchSha))
            {
                var currentBranchSha = await _gitHubService.GetRefShaAsync(owner, repo, branchName, ct);
                if (!string.IsNullOrEmpty(currentBranchSha) &&
                    !string.Equals(currentBranchSha, existingBranchSha, StringComparison.Ordinal))
                {
                    _logger?.LogWarning(
                        "Branch {Branch} moved concurrently from {ExpectedSha} to {CurrentSha} before write; failing closed",
                        branchName, existingBranchSha, currentBranchSha);
                    return new PublishResult(false, false, $"Branch '{branchName}' changed concurrently before commit (fail-closed)");
                }
            }

            // Commit updated SPEC to branch using mandatory optimistic CAS
            if (string.IsNullOrWhiteSpace(targetBranchBlobSha))
            {
                _logger?.LogWarning("Expected blob SHA missing before committing SPEC {SpecPath} on branch {Branch}; failing closed",
                    finalSpecPath, branchName);
                return new PublishResult(false, false, $"Missing expected blob SHA for branch '{branchName}' (fail-closed)");
            }

            try
            {
                await _optimisticFileGateway.UpdateFileContentIfUnchangedAsync(
                    owner, repo, finalSpecPath, commitMessage, effectiveContent, branchName, targetBranchBlobSha, ct);
            }
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            {
                _logger?.LogWarning(
                    ex,
                    "Optimistic concurrency conflict updating {SpecPath} on branch {Branch} (expectedBlobSha={ExpectedSha}); failing closed",
                    finalSpecPath, branchName, targetBranchBlobSha);
                return new PublishResult(
                    false,
                    false,
                    $"SPEC file '{finalSpecPath}' changed concurrently on branch '{branchName}' (optimistic concurrency conflict)");
            }
        }

        var requiredLabels = GetRequiredLabels(effectiveCandidate);
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

    private async Task<(PublishResult? AbortResult, string EffectiveContent, UpdateCandidate EffectiveCandidate, string? BaseBlobSha)> VerifyLiveBaseSpecAsync(
        string owner,
        string repo,
        string finalSpecPath,
        string baseRefOrSha,
        UpdateCandidate candidate,
        SpecRewriteResult rewriteResult,
        CancellationToken ct)
    {
        string? liveBaseSpec;
        string? liveBaseBlobSha = null;
        try
        {
            var liveFile = await _optimisticFileGateway.GetFileContentWithShaAsync(owner, repo, finalSpecPath, baseRefOrSha, ct);
            liveBaseSpec = liveFile?.Content;
            liveBaseBlobSha = liveFile?.BlobSha;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not read live base SPEC {SpecPath} at {BaseRef}; failing closed", finalSpecPath, baseRefOrSha);
            return (new PublishResult(false, false, $"Failed to read live base spec at '{baseRefOrSha}' (fail-closed)"),
                rewriteResult.UpdatedContent, candidate, null);
        }

        if (string.IsNullOrWhiteSpace(liveBaseSpec))
        {
            _logger?.LogWarning("Live base SPEC {SpecPath} at {BaseRef} is empty or missing; failing closed", finalSpecPath, baseRefOrSha);
            return (new PublishResult(false, false, $"Live base SPEC '{finalSpecPath}' at '{baseRefOrSha}' is empty or missing (fail-closed)"),
                rewriteResult.UpdatedContent, candidate, null);
        }

        if (NoAutoUpdateMarker.HasNoAutoUpdate(liveBaseSpec, out var liveNoAutoReason))
        {
            _logger?.LogInformation(
                "Live base SPEC {SpecPath} at {BaseRef} contains #!NoAutoUpdate ({Reason}); skipping update for {Package}",
                finalSpecPath, baseRefOrSha, liveNoAutoReason ?? "no reason", candidate.PackageName);
            return (new PublishResult(true, true, "Live base SPEC has #!NoAutoUpdate"),
                rewriteResult.UpdatedContent, candidate, null);
        }

        if (UpdateAdmission.IsVersionAtOrPastTarget(liveBaseSpec, candidate.TargetVersion))
        {
            _logger?.LogInformation(
                "Live base SPEC {SpecPath} at {BaseRef} is already at or past target version {Version}; skipping update for {Package}",
                finalSpecPath, baseRefOrSha, candidate.TargetVersion, candidate.PackageName);
            return (new PublishResult(true, true, $"Live base SPEC already at or past target version {candidate.TargetVersion}"),
                rewriteResult.UpdatedContent, candidate, null);
        }

        if (_options.SkipPackagesWithPatches && PatchDirectiveRegex.IsMatch(liveBaseSpec))
        {
            _logger?.LogInformation(
                "Live base SPEC {SpecPath} at {BaseRef} contains downstream Patch directives and SkipPackagesWithPatches=true; skipping {Package}",
                finalSpecPath, baseRefOrSha, candidate.PackageName);
            return (new PublishResult(true, true, "Live base SPEC contains Patch directives and SkipPackagesWithPatches=true"),
                rewriteResult.UpdatedContent, candidate, null);
        }

        var effectiveCandidate = UpdateAdmission.TryExtractCurrentVersion(liveBaseSpec, out var liveVersion)
            ? candidate with { CurrentVersion = liveVersion }
            : candidate;

        if (!_options.AllowCrossMajorVersion &&
            UpdateAdmission.IsCrossMajorVersion(effectiveCandidate.CurrentVersion, effectiveCandidate.TargetVersion))
        {
            _logger?.LogInformation(
                "Live base SPEC {SpecPath} at {BaseRef} would cross major version boundary ({Current} -> {Target}); skipping {Package}",
                finalSpecPath, baseRefOrSha, effectiveCandidate.CurrentVersion, effectiveCandidate.TargetVersion, effectiveCandidate.PackageName);
            return (new PublishResult(true, true, "Live base SPEC crosses major version boundary and AllowCrossMajorVersion=false"),
                rewriteResult.UpdatedContent, effectiveCandidate, null);
        }

        if (_specRewriters.Count == 0)
        {
            _logger?.LogWarning("No spec rewriters configured to re-rewrite live base SPEC {SpecPath}; failing closed", finalSpecPath);
            return (new PublishResult(false, false, "No spec rewriters configured to re-rewrite live base SPEC (fail-closed)"),
                rewriteResult.UpdatedContent, effectiveCandidate, null);
        }

        var normalizedDir = Path.GetDirectoryName(finalSpecPath.Replace('\\', '/'))?.Replace('\\', '/');
        var dirName = !string.IsNullOrEmpty(normalizedDir)
            ? Path.GetFileName(normalizedDir)
            : effectiveCandidate.PackageName;
        var liveContext = new SpecContext(effectiveCandidate.PackageName, dirName, liveBaseSpec, finalSpecPath);
        var rewriter = _specRewriters.FirstOrDefault(r => r.CanHandle(liveContext));
        if (rewriter == null)
        {
            return (new PublishResult(true, true, "Live base SPEC has no compatible automated rewriter"),
                rewriteResult.UpdatedContent, effectiveCandidate, null);
        }

        var liveRewrite = rewriter.Rewrite(liveContext, effectiveCandidate.TargetVersion);
        if (!liveRewrite.Success || !liveRewrite.HasChanged)
        {
            return (new PublishResult(true, true,
                    $"Live base SPEC rewrite produced no change or failed: {liveRewrite.ErrorMessage ?? "no-op"}"),
                rewriteResult.UpdatedContent, effectiveCandidate, null);
        }

        return (null, liveRewrite.UpdatedContent, effectiveCandidate, liveBaseBlobSha);
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
        var ownLogins = BotCommitOwnership.BuildOwnBotLoginSet(
            liveBotLogin,
            _options.BotLogin,
            requireLive: _botIdentity != null);
        return BotCommitOwnership.IsOwnBotCommit(commit, ownLogins);
    }

    private static string? NormalizeLineEndings(string? value)
        => value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool AreEqualIgnoringRemoteAssetLines(string? left, string? right)
    {
        var normLeft = NormalizeLineEndings(left);
        var normRight = NormalizeLineEndings(right);
        if (normLeft == null || normRight == null)
        {
            return false;
        }

        var leftLines = normLeft.Split('\n');
        var rightLines = normRight.Split('\n');
        if (leftLines.Length != rightLines.Length)
        {
            return false;
        }

        for (int i = 0; i < leftLines.Length; i++)
        {
            if (string.Equals(leftLines[i], rightLines[i], StringComparison.Ordinal))
            {
                continue;
            }

            var isLeftRemoteAsset = leftLines[i].TrimStart().StartsWith("#!RemoteAsset", StringComparison.OrdinalIgnoreCase);
            var isRightRemoteAsset = rightLines[i].TrimStart().StartsWith("#!RemoteAsset", StringComparison.OrdinalIgnoreCase);
            if (!isLeftRemoteAsset || !isRightRemoteAsset)
            {
                return false;
            }
        }

        return true;
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


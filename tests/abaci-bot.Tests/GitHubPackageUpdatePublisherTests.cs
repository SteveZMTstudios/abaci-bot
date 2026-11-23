using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class GitHubPackageUpdatePublisherTests
{
    [Fact]
    public async Task PublishUpdateAsync_BranchAlreadyAtTargetVersion_SkipsWithoutPushing()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Latest commit by bot
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        // File on branch is ALREADY at target version 8.22.0
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Contains("already at target version", result.SkipReason);

        // Verify zero writes / zero updates were executed
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_HumanCommitsOnBranch_StepsAsideAndComments()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Latest commit by human maintainer 'misaka00251'
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "manual fix", "misaka00251", "maintainer@openruyi.org", "web-flow", "noreply@github.com"));

        var fakePr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(fakePr, 42);

        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(fakePr);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.23.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.23.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Contains("Human commits by misaka00251 detected", result.SkipReason);

        // Verify warning comment was posted on PR
        await gitHubMock.Received(1).CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 42, Arg.Is<string>(c => c.Contains("contains human commits by @misaka00251")), Arg.Any<CancellationToken>());

        // Verify no overwrite happened
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_WebFlowAndBotCommits_AreAllowedToUpdate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Automated Contents API commit authored by bot and signed by GitHub's web-flow committer
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "SPECS: bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");

        var fakePr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(fakePr, 42);

        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(fakePr);

        gitHubMock.UpdatePullRequestAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(fakePr);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.False(result.Skipped);

        // Assert file update was dispatched
        await gitHubMock.Received(1).UpdateFileContentAsync(
            "openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", Arg.Any<string>(), rewriteResult.UpdatedContent, "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_ThrowsArgumentException_WhenSpecPathIsNullOrWhiteSpace()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", ""));
    }

    [Fact]
    public void GeneratePullRequestBody_CompliesWithUpstreamPullRequestTemplate()
    {
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://lookout.openruyi.cn/package/curl");
        var body = GitHubPackageUpdatePublisher.GeneratePullRequestBody(candidate);

        Assert.Contains("## Summary", body);
        Assert.Contains("Automatic version bump for `curl` from `8.21.0` to `8.22.0`", body);
        Assert.Contains("## AI-Assisted Contribution", body);
        Assert.Contains("- [ ] I have read the [AI-Assisted Contribution Policy]", body);
        Assert.Contains("## Checklist", body);
        Assert.Contains("- [x] Package version updated in spec file", body);
        Assert.Contains("To pause automated updates for this package, add `#!NoAutoUpdate: <reason>` to the SPEC file header.", body);
        Assert.DoesNotContain("如需停止", body);
    }

    [Fact]
    public async Task PublishUpdateAsync_ReturnsSkipped_WhenCandidateOrRewriteResultInvalid()
    {
        var publisher = new GitHubPackageUpdatePublisher(Substitute.For<IGitHubService>(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");

        // 1. Rewrite failed
        var failedRewrite = new SpecRewriteResult(false, "", "error");
        var res1 = await publisher.PublishUpdateAsync(candidate, failedRewrite, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(res1.Success);
        Assert.True(res1.Skipped);

        // 2. Rewrite has no changes
        var unchangedRewrite = new SpecRewriteResult(true, "", null, HasChanged: false);
        var res2 = await publisher.PublishUpdateAsync(candidate, unchangedRewrite, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(res2.Success);
        Assert.True(res2.Skipped);

        // 3. Invalid target repo
        var validRewrite = new SpecRewriteResult(true, "new content", null, HasChanged: true);
        var res3 = await publisher.PublishUpdateAsync(candidate, validRewrite, "invalid_repo_without_slash", "main", "SPECS/curl.spec");
        Assert.False(res3.Success);
        Assert.False(res3.Skipped);
    }

    [Fact]
    public async Task PublishUpdateAsync_FailsClosed_WhenLatestCommitFailsOrNull()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        // Throws exception reading latest commit
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns<Task<BranchCommitInfo?>>(_ => throw new InvalidOperationException("API error"));

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(res.Success);
        Assert.Contains("fail-closed", res.SkipReason);

        // Null latest commit
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((BranchCommitInfo?)null);
        var resNull = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(resNull.Success);
        Assert.Contains("fail-closed", resNull.SkipReason);
    }

    [Fact]
    public async Task PublishUpdateAsync_CreatesBranchAndNewPullRequest_WhenBranchDoesNotExist()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((string?)null); // Branch does not exist
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 99);
        typeof(PullRequest).GetProperty(nameof(PullRequest.HtmlUrl))?.SetValue(createdPr, "https://github.com/owner/repo/pull/99");
        gitHubMock.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false }));
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.False(res.Skipped);
        Assert.Equal(99, res.PullRequestNumber);

        // Verify branch was created from base SHA
        await gitHubMock.Received(1).CreateOrUpdateRefAsync("owner", "repo", "autoupdate/curl", "baseSha456", force: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_RespectsDryRun_WithoutWritingToGitHub()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = true });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.False(res.Skipped);
        Assert.Contains("DryRun", res.SkipReason);

        await gitHubMock.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_StepsAside_WhenHumanCommitsDetectedOnBranch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 55);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(existingPr);

        // Human committer
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commitSha", "manual tweak", "dev-human", "human@example.com", "dev-human", "human@example.com"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.True(res.Skipped);
        Assert.Contains("Human commits", res.SkipReason);

        // Warning comment posted
        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 55, Arg.Is<string>(c => c.Contains("stepped aside")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_Skips_WhenBranchAlreadyAtTargetVersion()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 56);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(existingPr);

        // Bot committer
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commitSha", "bump version", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        // Spec on branch already at target version 8.22.0
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\nRelease: 1\n");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.True(res.Skipped);
        Assert.Contains("already at target version", res.SkipReason);

        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_FailsClosed_WhenBranchSpecReadThrows()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commitSha", "bump version", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("Failed to read spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.False(res.Success);
        Assert.Contains("fail-closed", res.SkipReason);
    }

    [Fact]
    public async Task PublishUpdateAsync_AddsRustLabel_WhenOriginIsRustCrate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 77);
        gitHubMock.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/rust-anstream", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("rust-anstream", "0.6.0", "0.6.1", CandidateOrigin.RustCrate, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/rust-anstream/rust-anstream.spec");

        Assert.True(res.Success);
        Assert.Equal(77, res.PullRequestNumber);

        await gitHubMock.Received(1).AddLabelsAsync(
            "owner", "repo", 77, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.AutoUpdateRust)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "", null, false)] // empty identity fails closed
    [InlineData("alice", "web-flow", null, false)] // human GitHub Web UI edit signed by web-flow steps aside
    [InlineData("web-flow", "web-flow", null, false)] // bare web-flow without bot author fails closed
    [InlineData("dependabot[bot]", "web-flow", null, false)] // arbitrary external [bot] rejected
    [InlineData("github-actions[bot]", "github-actions[bot]", null, false)] // external action bot rejected
    [InlineData("my-custom-bot[bot]", "alice", null, false)] // local human amend preserving bot author rejected
    [InlineData("my-custom-bot", "web-flow", null, true)] // PAT / direct login + GitHub Contents API signature allowed
    [InlineData("my-custom-bot[bot]", "web-flow", null, true)] // App slug + [bot] allowed
    [InlineData("abaci-app[bot]", "web-flow", "abaci-app", true)] // live App slug resolved from IBotIdentityProvider allowed
    public void IsOwnBotCommit_EvaluatesStrictAuthorAndCommitterInvariants(
        string authorLogin, string committerLogin, string? liveBotLogin, bool expected)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            BotLogin = "my-custom-bot"
        });
        var publisher = new GitHubPackageUpdatePublisher(Substitute.For<IGitHubService>(), options);
        var commit = new BranchCommitInfo("sha1", "msg", authorLogin, "", committerLogin, "");

        Assert.Equal(expected, publisher.IsOwnBotCommit(commit, liveBotLogin));
    }

    [Fact]
    public async Task PublishUpdateAsync_HumanCommitsSteppedAside_DeduplicatesCommentAcrossRepeatedSweeps()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 55);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(existingPr);

        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commitSha", "manual tweak", "dev-human", "human@example.com", "web-flow", "noreply@github.com"));

        var existingComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(
            existingComment,
            "A newer upstream version `8.22.0` is available for `curl`, but branch `autoupdate/curl` contains human commits by @dev-human. Automated update has stepped aside to prevent overwriting manual work.");
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 55, Arg.Any<CancellationToken>())
            .Returns(new[] { existingComment });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.True(res.Skipped);
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_ExistingPullRequest_ReappliesMissingRequiredLabelsUnion_AndFailsOnAddLabelsError()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns("existingSha");
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("existingSha", "bump", "aqua-rium", "", "web-flow", ""));
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/rust-zip/rust-zip.spec", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns("%global crate_name zip\nVersion: 2.0.0\n");

        var onlyStaleLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(onlyStaleLabel, GitHubLabels.StatusStaleExempt);

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 88);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(existingPr, new List<Label> { onlyStaleLabel });

        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns(existingPr);
        gitHubMock.UpdatePullRequestAsync("owner", "repo", 88, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existingPr);

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false }));
        var candidate = new UpdateCandidate("rust-zip", "2.0.0", "2.1.0", CandidateOrigin.RustCrate, "https://evidence");
        var rewrite = new SpecRewriteResult(true, "updated", null, HasChanged: true);

        var okRes = await publisher.PublishUpdateAsync(candidate, rewrite, "owner/repo", "main", "SPECS/rust-zip/rust-zip.spec");
        Assert.True(okRes.Success);
        await gitHubMock.Received(1).AddLabelsAsync(
            "owner",
            "repo",
            88,
            Arg.Is<string[]>(labels =>
                labels.Contains(GitHubLabels.AutoUpdate) &&
                labels.Contains(GitHubLabels.AutoUpdateRust) &&
                !labels.Contains(GitHubLabels.StatusStaleExempt)),
            Arg.Any<CancellationToken>());

        // Now simulate AddLabelsAsync persistent failure -> PublishUpdateAsync fails instead of leaving orphaned PR
        gitHubMock.ClearReceivedCalls();
        gitHubMock.AddLabelsAsync("owner", "repo", 88, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("422 Label error"));

        var failRes = await publisher.PublishUpdateAsync(candidate, rewrite, "owner/repo", "main", "SPECS/rust-zip/rust-zip.spec");
        Assert.False(failRes.Success);
        Assert.False(failRes.Skipped);
        Assert.Contains("Failed to apply required labels", failRes.SkipReason);
        await gitHubMock.Received(2).AddLabelsAsync("owner", "repo", 88, Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }
}

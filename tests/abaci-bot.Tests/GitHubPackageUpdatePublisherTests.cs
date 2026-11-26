using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class GitHubPackageUpdatePublisherTests
{
    private static IGitHubService CreateGitHubMock()
    {
        var gh = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var cas = (IOptimisticFileContentGateway)gh;
        cas.GetFileContentWithShaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var content = await gh.GetFileContentAsync(
                    ci.ArgAt<string>(0),
                    ci.ArgAt<string>(1),
                    ci.ArgAt<string>(2),
                    ci.ArgAt<string>(3),
                    ci.ArgAt<CancellationToken>(4));
                return string.IsNullOrEmpty(content) ? null : new RepositoryFileContent(content, "mock-blob-sha");
            });
        cas.UpdateFileContentIfUnchangedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await gh.UpdateFileContentAsync(
                ci.ArgAt<string>(0),
                ci.ArgAt<string>(1),
                ci.ArgAt<string>(2),
                ci.ArgAt<string>(3),
                ci.ArgAt<string>(4),
                ci.ArgAt<string>(5),
                ci.ArgAt<CancellationToken>(7)));
        return gh;
    }

    [Fact]
    public void Constructor_WithoutOptimisticFileContentGateway_ThrowsArgumentException()
    {
        var plainGitHub = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());

        Assert.Throws<ArgumentException>(() => new GitHubPackageUpdatePublisher(plainGitHub, options));
    }

    [Fact]
    public async Task PublishUpdateAsync_BranchAlreadyAtTargetVersion_SkipsWithoutPushing()
    {
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Latest commit by bot
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 42);
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(existingPr);

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
        var gitHubMock = CreateGitHubMock();
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
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Automated Contents API commit authored by bot and signed by GitHub's web-flow committer
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "SPECS: bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
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
        var gitHubMock = CreateGitHubMock();
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
        var publisher = new GitHubPackageUpdatePublisher(CreateGitHubMock(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
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
        var gitHubMock = CreateGitHubMock();
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
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((string?)null); // Branch does not exist
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl.spec", "baseSha456", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
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
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl.spec", "baseSha456", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
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
        var gitHubMock = CreateGitHubMock();
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
        var gitHubMock = CreateGitHubMock();
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
        var gitHubMock = CreateGitHubMock();
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
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/rust-anstream/rust-anstream.spec", "baseSha456", Arg.Any<CancellationToken>())
            .Returns("%global crate_name anstream\nVersion: 0.6.0\n");

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
    [InlineData("my-custom-bot", "web-flow", "abaci-app", false)] // configured fallback ignored once live App identity resolves
    public void IsOwnBotCommit_EvaluatesStrictAuthorAndCommitterInvariants(
        string authorLogin, string committerLogin, string? liveBotLogin, bool expected)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            BotLogin = "my-custom-bot"
        });
        var publisher = new GitHubPackageUpdatePublisher(CreateGitHubMock(), options);
        var commit = new BranchCommitInfo("sha1", "msg", authorLogin, "", committerLogin, "");

        Assert.Equal(expected, publisher.IsOwnBotCommit(commit, liveBotLogin));
    }

    [Fact]
    public async Task PublishUpdateAsync_HumanCommitsSteppedAside_DeduplicatesCommentAcrossRepeatedSweeps()
    {
        var gitHubMock = CreateGitHubMock();
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
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns("existingSha");
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("existingSha", "bump", "aqua-rium", "", "web-flow", ""));
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/rust-zip/rust-zip.spec", "autoupdate/rust-zip", Arg.Any<CancellationToken>())
            .Returns("%global crate_name zip\nVersion: 2.0.0\n");
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/rust-zip/rust-zip.spec", "main", Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task PublishUpdateAsync_NewBranch_ChecksLiveBaseSpec_SkipsWhenUpdatedOrNoAutoUpdate_AndFailsClosedOnError()
    {
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });

        // 1. Live main already upgraded past target -> skip without creating branch
        var ghUpgraded = CreateGitHubMock();
        ghUpgraded.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghUpgraded.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("baseSha1");
        ghUpgraded.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "baseSha1", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n");

        var pub1 = new GitHubPackageUpdatePublisher(ghUpgraded, options);
        var res1 = await pub1.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.True(res1.Success);
        Assert.True(res1.Skipped);
        await ghUpgraded.DidNotReceiveWithAnyArgs().CreateOrUpdateRefAsync(default!, default!, default!, default!, default, default);

        // 2. Live main added #!NoAutoUpdate -> skip without creating branch
        var ghHeld = CreateGitHubMock();
        ghHeld.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghHeld.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("baseSha2");
        ghHeld.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "baseSha2", Arg.Any<CancellationToken>())
            .Returns("#!NoAutoUpdate: hold\nName: curl\nVersion: 8.21.0\n");

        var pub2 = new GitHubPackageUpdatePublisher(ghHeld, options);
        var res2 = await pub2.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.True(res2.Success);
        Assert.True(res2.Skipped);
        Assert.Contains("NoAutoUpdate", res2.SkipReason);
        await ghHeld.DidNotReceiveWithAnyArgs().CreateOrUpdateRefAsync(default!, default!, default!, default!, default, default);

        // 3. Reading live main throws -> fail closed without creating branch
        var ghErr = CreateGitHubMock();
        ghErr.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghErr.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("baseSha3");
        ghErr.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "baseSha3", Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new HttpRequestException("503 Unavailable"));

        var pub3 = new GitHubPackageUpdatePublisher(ghErr, options);
        var res3 = await pub3.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.False(res3.Success);
        Assert.False(res3.Skipped);
        Assert.Contains("fail-closed", res3.SkipReason);
        await ghErr.DidNotReceiveWithAnyArgs().CreateOrUpdateRefAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task PublishUpdateAsync_ExistingBranchHonorsMainNoAutoUpdate_AndNewBranchReRewritesAgainstLiveMain()
    {
        var rewriters = new ISpecVersionRewriter[] { new PlainSpecVersionRewriter(), new RustCrateSpecRewriter() };
        var opts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            SkipPackagesWithPatches = true,
            AllowCrossMajorVersion = false
        });

        // 1. Existing bot branch skips if live main added #!NoAutoUpdate (P1)
        var ghExisting = CreateGitHubMock();
        ghExisting.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns("botCommitSha");
        ghExisting.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("botCommitSha", "bump", "aqua-rium", "", "web-flow", ""));
        ghExisting.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
        ghExisting.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("#!NoAutoUpdate: hold\nName: curl\nVersion: 8.21.0\n");

        var pub1 = new GitHubPackageUpdatePublisher(ghExisting, opts, specRewriters: rewriters);
        var res1 = await pub1.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://ev"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.True(res1.Success);
        Assert.True(res1.Skipped);
        Assert.Contains("NoAutoUpdate", res1.SkipReason);
        await ghExisting.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);

        // 2. New branch skips if live main added Patch1: since tarball snapshot (P2 admission drift)
        var ghPatched = CreateGitHubMock();
        ghPatched.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghPatched.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainShaPatch");
        ghPatched.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "mainShaPatch", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\nPatch1: security.patch\n");

        var pub2 = new GitHubPackageUpdatePublisher(ghPatched, opts, specRewriters: rewriters);
        var res2 = await pub2.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://ev"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.True(res2.Success);
        Assert.True(res2.Skipped);
        Assert.Contains("Patch directives", res2.SkipReason);
        await ghPatched.DidNotReceiveWithAnyArgs().CreateOrUpdateRefAsync(default!, default!, default!, default!, default, default);

        // 3. New branch re-rewrites live main SPEC so new BuildRequires on main is preserved (P2 TOCTOU)
        var ghReRewrite = CreateGitHubMock();
        ghReRewrite.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghReRewrite.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainShaFresh");
        ghReRewrite.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "mainShaFresh", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.5\nBuildRequires: zlib-devel\n");
        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 200);
        ghReRewrite.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        string? committedContent = null;
        ghReRewrite.When(g => g.UpdateFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", Arg.Any<CancellationToken>()))
            .Do(ci => committedContent = ci.ArgAt<string>(4));

        var pub3 = new GitHubPackageUpdatePublisher(ghReRewrite, opts, specRewriters: rewriters);
        var res3 = await pub3.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://ev"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.True(res3.Success);
        Assert.False(res3.Skipped);
        Assert.NotNull(committedContent);
        Assert.Contains("Version: 8.22.0", committedContent!, StringComparison.Ordinal);
        Assert.Contains("BuildRequires: zlib-devel", committedContent!, StringComparison.Ordinal);
        await ghReRewrite.Received(1).CreatePullRequestAsync(
            "owner", "repo", "SPECS: bump curl 8.21.5 -> 8.22.0", "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // 4. Existing bot branch ALSO re-rewrites against live main SPEC so new non-version lines on main are preserved (O1)
        var ghExistingReRewrite = CreateGitHubMock();
        ghExistingReRewrite.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns("existingBranchSha");
        ghExistingReRewrite.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("existingBranchSha", "bump", "aqua-rium", "", "web-flow", ""));
        ghExistingReRewrite.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
        ghExistingReRewrite.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\nBuildRequires: foo\n");
        var openExistingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(openExistingPr, 201);
        ghExistingReRewrite.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns(openExistingPr);
        ghExistingReRewrite.UpdatePullRequestAsync("owner", "repo", 201, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(openExistingPr);

        string? existingCommittedContent = null;
        ghExistingReRewrite.When(g => g.UpdateFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", Arg.Any<CancellationToken>()))
            .Do(ci => existingCommittedContent = ci.ArgAt<string>(4));

        var pub4 = new GitHubPackageUpdatePublisher(ghExistingReRewrite, opts, specRewriters: rewriters);
        var res4 = await pub4.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://ev"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.True(res4.Success);
        Assert.False(res4.Skipped);
        Assert.Contains("Version: 8.22.0", existingCommittedContent!, StringComparison.Ordinal);
        Assert.Contains("BuildRequires: foo", existingCommittedContent!, StringComparison.Ordinal);

        // 5. Blank or missing live base SPEC fails closed (O10) without creating branch or writing content
        var ghBlankLive = CreateGitHubMock();
        ghBlankLive.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghBlankLive.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainShaBlank");
        ghBlankLive.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "mainShaBlank", Arg.Any<CancellationToken>())
            .Returns("   ");

        var pub5 = new GitHubPackageUpdatePublisher(ghBlankLive, opts, specRewriters: rewriters);
        var res5 = await pub5.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://ev"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");
        Assert.False(res5.Success);
        Assert.False(res5.Skipped);
        Assert.Contains("fail-closed", res5.SkipReason, StringComparison.OrdinalIgnoreCase);
        await ghBlankLive.DidNotReceiveWithAnyArgs().CreateOrUpdateRefAsync(default!, default!, default!, default!, default, default);
        await ghBlankLive.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PublishUpdateAsync_OrphanBranchAlreadyAtTargetVersion_CreatesPrWithoutDuplicateCommit()
    {
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n");
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 99);
        typeof(PullRequest).GetProperty(nameof(PullRequest.HtmlUrl))?.SetValue(createdPr, "https://github.com/openRuyi-Project/openRuyi/pull/99");
        gitHubMock.CreatePullRequestAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);
        gitHubMock.AddLabelsAsync("openRuyi-Project", "openRuyi", 99, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

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
        Assert.Equal(99, result.PullRequestNumber);
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreatePullRequestAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Also verify real two-attempt failure/recovery: sweep 1 pushes content and CreatePullRequestAsync throws;
        // sweep 2 finds existing orphan branch at target version without an open PR and creates PR without pushing again.
        var ghRetry = CreateGitHubMock();
        var prAttempt = 0;
        ghRetry.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(_ => prAttempt == 0 ? null : "commit_sha_123");
        ghRetry.GetRefShaAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns("main_sha_1");
        ghRetry.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        ghRetry.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));
        ghRetry.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main_sha_1", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
        ghRetry.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
        ghRetry.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n");
        ghRetry.CreatePullRequestAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                prAttempt++;
                if (prAttempt == 1)
                {
                    throw new HttpRequestException("502 Bad Gateway");
                }

                return createdPr;
            });
        ghRetry.AddLabelsAsync("openRuyi-Project", "openRuyi", 99, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var retryPublisher = new GitHubPackageUpdatePublisher(ghRetry, options);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            retryPublisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec"));
        var retryResult = await retryPublisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(retryResult.Success);
        Assert.False(retryResult.Skipped);
        Assert.Equal(99, retryResult.PullRequestNumber);
        await ghRetry.Received(1).UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ghRetry.Received(2).CreatePullRequestAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // If baseBranch gained #!NoAutoUpdate in the meantime, abort without opening a PR for the orphan branch.
        var ghOptOut = CreateGitHubMock();
        ghOptOut.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");
        ghOptOut.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        ghOptOut.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));
        ghOptOut.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n");
        ghOptOut.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("#!NoAutoUpdate: manual hold\nName: curl\nVersion: 8.21.0\n");

        var optOutPublisher = new GitHubPackageUpdatePublisher(ghOptOut, options);
        var optOutResult = await optOutPublisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(optOutResult.Success);
        Assert.True(optOutResult.Skipped);
        Assert.Contains("NoAutoUpdate", optOutResult.SkipReason, StringComparison.OrdinalIgnoreCase);
        await ghOptOut.DidNotReceiveWithAnyArgs().CreatePullRequestAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PublishUpdateAsync_LiveBotIdentityUnavailable_FailsClosedOnExistingBranch()
    {
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string>(null!));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options, botIdentity: botIdentity);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Contains("Live bot identity unavailable", result.SkipReason, StringComparison.Ordinal);
        await gitHubMock.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PublishUpdateAsync_OrphanBranchWithLiveMainDrift_ReRewritesAndCommitsBeforeCreatingPr()
    {
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));
        // Orphan branch still carries old main's structure at target version 8.22.0.
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\nSummary: old\n");
        // Live main merged an updated Summary after the branch was originally written.
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "main", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\nSummary: new\n");

        string? pushedContent = null;
        gitHubMock.When(g => g.UpdateFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", Arg.Any<CancellationToken>()))
            .Do(ci => pushedContent = ci.ArgAt<string>(4));

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 100);
        gitHubMock.CreatePullRequestAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);
        gitHubMock.AddLabelsAsync("openRuyi-Project", "openRuyi", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });
        var publisher = new GitHubPackageUpdatePublisher(
            gitHubMock,
            options,
            specRewriters: new ISpecVersionRewriter[] { new PlainSpecVersionRewriter() });
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var staleRewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\nSummary: old\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, staleRewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.False(result.Skipped);
        Assert.Equal(100, result.PullRequestNumber);
        Assert.NotNull(pushedContent);
        Assert.Contains("Version: 8.22.0", pushedContent!, StringComparison.Ordinal);
        Assert.Contains("Summary: new", pushedContent!, StringComparison.Ordinal);
        await gitHubMock.Received(1).UpdateFileContentAsync(
            "openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreatePullRequestAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_ExplicitEmptyRewriters_FailsClosedWithoutWritingCachedContent()
    {
        var gitHubMock = CreateGitHubMock();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainSha");
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "mainSha", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");

        var publisher = new GitHubPackageUpdatePublisher(
            gitHubMock,
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false }),
            specRewriters: Array.Empty<ISpecVersionRewriter>());

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl/curl.spec");

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Contains("No spec rewriters configured", result.SkipReason, StringComparison.Ordinal);
        await gitHubMock.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PublishUpdateAsync_DefaultConstructorRewritesLiveMain_OrphanRemoteAssetPreserved_AndExistingBranchRaceFailsClosed()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        // 1. Default constructor (specRewriters omitted) re-rewrites against live main and preserves newer main edits on version bump.
        var ghDefault = CreateGitHubMock();
        ghDefault.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns("branchSha1");
        ghDefault.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("liveMainSha");
        ghDefault.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("branchSha1", "bump curl 8.21.0", "aqua-rium", "", "web-flow", ""));
        ghDefault.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n#!RemoteAsset: curl.tar.xz sha256=old\n");
        ghDefault.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "liveMainSha", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.20.0\nBuildRequires: maintainer-added\n#!RemoteAsset: curl.tar.xz sha256=base\n");

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 301);
        ghDefault.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns(existingPr);
        ghDefault.UpdatePullRequestAsync("owner", "repo", 301, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(existingPr);

        string? pushedDefault = null;
        ghDefault.When(g => g.UpdateFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", Arg.Any<CancellationToken>()))
            .Do(ci => pushedDefault = ci.ArgAt<string>(4));

        var pubDefault = new GitHubPackageUpdatePublisher(ghDefault, opts);
        var bumpRes = await pubDefault.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.20.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence"),
            new SpecRewriteResult(true, "stale-cached-content\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");

        Assert.True(bumpRes.Success);
        Assert.NotNull(pushedDefault);
        Assert.Contains("Version: 8.22.0", pushedDefault!, StringComparison.Ordinal);
        Assert.Contains("BuildRequires: maintainer-added", pushedDefault!, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-cached-content", pushedDefault!, StringComparison.Ordinal);

        // 2. Orphan branch already at target version with #!RemoteAsset backfill and no non-RemoteAsset drift on main preserves backfill.
        var ghOrphan = CreateGitHubMock();
        ghOrphan.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns("orphanSha");
        ghOrphan.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainSha");
        ghOrphan.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((PullRequest?)null);
        ghOrphan.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("orphanSha", "backfill", "aqua-rium", "", "web-flow", ""));
        ghOrphan.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n#!RemoteAsset: curl.tar.xz sha256=backfilled-new\n");
        ghOrphan.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "mainSha", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n#!RemoteAsset: curl.tar.xz sha256=old-main\n");

        var createdOrphanPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdOrphanPr, 302);
        ghOrphan.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdOrphanPr);

        var pubOrphan = new GitHubPackageUpdatePublisher(ghOrphan, opts);
        var orphanRes = await pubOrphan.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");

        Assert.True(orphanRes.Success);
        Assert.False(orphanRes.Skipped);
        Assert.Equal(302, orphanRes.PullRequestNumber);
        await ghOrphan.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);

        // 3. Existing branch ref SHA moves between ownership check and write -> fails closed without writing.
        var ghRace = CreateGitHubMock();
        ghRace.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("branchShaInitial", "branchShaHumanPushed");
        ghRace.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainSha");
        ghRace.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("branchShaInitial", "bump", "aqua-rium", "", "web-flow", ""));
        ghRace.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");
        ghRace.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "mainSha", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");

        var pubRace = new GitHubPackageUpdatePublisher(ghRace, opts);
        var raceRes = await pubRace.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");

        Assert.False(raceRes.Success);
        Assert.False(raceRes.Skipped);
        Assert.Contains("changed concurrently", raceRes.SkipReason, StringComparison.Ordinal);
        await ghRace.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PublishUpdateAsync_WithOptimisticGateway_PassesExpectedBlobSha_AndFailsClosedOn409Conflict()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        // 1. Happy path: captures BlobSha from live main and commits via UpdateFileContentIfUnchangedAsync
        var ghCas = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var casGateway = (IOptimisticFileContentGateway)ghCas;
        ghCas.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((string?)null);
        ghCas.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainSha");
        ghCas.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((PullRequest?)null);
        casGateway.GetFileContentWithShaAsync("owner", "repo", "SPECS/curl/curl.spec", "mainSha", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFileContent("Name: curl\nVersion: 8.21.0\n", "blob-main-sha"));
        casGateway.UpdateFileContentIfUnchangedAsync(
                "owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", "blob-main-sha", Arg.Any<CancellationToken>())
            .Returns("new-commit-sha");

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 401);
        ghCas.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var pubOk = new GitHubPackageUpdatePublisher(ghCas, opts, optimisticFileGateway: casGateway);
        var okRes = await pubOk.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");

        Assert.True(okRes.Success);
        await casGateway.Received(1).UpdateFileContentIfUnchangedAsync(
            "owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Is<string>(s => s.Contains("8.22.0")), "autoupdate/curl", "blob-main-sha", Arg.Any<CancellationToken>());
        await ghCas.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);

        // 2. 409 Conflict during optimistic update -> fails closed without opening/keeping PR writes
        var ghConflict = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var conflictGateway = (IOptimisticFileContentGateway)ghConflict;
        ghConflict.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns("branchSha");
        ghConflict.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>()).Returns("mainSha");
        ghConflict.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns((PullRequest?)null);
        ghConflict.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("branchSha", "bump", "aqua-rium", "", "web-flow", ""));
        conflictGateway.GetFileContentWithShaAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFileContent("Name: curl\nVersion: 8.21.0\n", "blob-branch-sha"));
        conflictGateway.GetFileContentWithShaAsync("owner", "repo", "SPECS/curl/curl.spec", "mainSha", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFileContent("Name: curl\nVersion: 8.21.0\n", "blob-main-sha"));
        conflictGateway.UpdateFileContentIfUnchangedAsync(
                "owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", "blob-branch-sha", Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new ApiException("Conflict", HttpStatusCode.Conflict));

        var pubConflict = new GitHubPackageUpdatePublisher(ghConflict, opts, optimisticFileGateway: conflictGateway);
        var conflictRes = await pubConflict.PublishUpdateAsync(
            new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence"),
            new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", HasChanged: true),
            "owner/repo", "main", "SPECS/curl/curl.spec");

        Assert.False(conflictRes.Success);
        Assert.False(conflictRes.Skipped);
        Assert.Contains("optimistic concurrency conflict", conflictRes.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionDiWiring_ResolvesIOptimisticFileContentGatewayToGitHubService_InPublisherAndBackfiller()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<PackageUpdateOptions>(_ => { });
        services.AddSingleton(_ => new GitHubService(123456, pem, 789012));
        services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());
        services.AddSingleton<IOptimisticFileContentGateway>(sp => sp.GetRequiredService<GitHubService>());
        services.AddSingleton<IRemoteAssetBackfiller, RemoteAssetBackfiller>();
        services.AddSingleton<IPackageUpdatePublisher, GitHubPackageUpdatePublisher>();

        using var sp = services.BuildServiceProvider();
        var githubService = sp.GetRequiredService<GitHubService>();
        var casGateway = sp.GetRequiredService<IOptimisticFileContentGateway>();
        var publisher = Assert.IsType<GitHubPackageUpdatePublisher>(sp.GetRequiredService<IPackageUpdatePublisher>());
        var backfiller = Assert.IsType<RemoteAssetBackfiller>(sp.GetRequiredService<IRemoteAssetBackfiller>());

        Assert.Same(githubService, casGateway);
        var publisherGateway = typeof(GitHubPackageUpdatePublisher)
            .GetField("_optimisticFileGateway", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(publisher);
        var backfillerGateway = typeof(RemoteAssetBackfiller)
            .GetField("_optimisticFileGateway", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(backfiller);
        Assert.Same(githubService, publisherGateway);
        Assert.Same(githubService, backfillerGateway);
    }
}

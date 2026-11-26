using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class PackageUpdateServiceTests
{
    [Fact]
    public async Task RunSweepAsync_CleansUpInactiveFailedPrs_PastTtl()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");

        // An inactive failed PR last updated 10 days ago (TTL is 7 days)
        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 88);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        typeof(Issue).GetProperty(nameof(Issue.User))?.SetValue(stalePr, botUser);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "sha88");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 88, Arg.Any<CancellationToken>())
            .Returns(fullPr);
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha88", "bump", "aqua-rium", "bot@example.com", "web-flow", "noreply@github.com"));

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, "sha88");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow.AddDays(-9));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new Octokit.StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            FailedPrTtlDays = 7,
            MaxConcurrentPullRequests = 5
        });

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Verify inactive failed PR #88 was closed
        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 88, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RecordFailure_EntersCooldown_AfterMaxConsecutiveFailures()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            MaxConsecutiveFailures = 3,
            GiveUpIssueCooldownDays = 30
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        // Initial state: not in cooldown
        Assert.False(service.IsInCooldown("failing-pkg"));

        // Record 1st and 2nd failure
        var entered1 = service.RecordFailure("failing-pkg");
        Assert.False(entered1);
        Assert.False(service.IsInCooldown("failing-pkg"));

        var entered2 = service.RecordFailure("failing-pkg");
        Assert.False(entered2);
        Assert.False(service.IsInCooldown("failing-pkg"));

        // 3rd failure reaches MaxConsecutiveFailures (3) and triggers cooldown
        var entered3 = service.RecordFailure("failing-pkg");
        Assert.True(entered3);
        Assert.True(service.IsInCooldown("failing-pkg"));
    }

    [Fact]
    public async Task RunSweepAsync_DoesNotDeleteBranch_WhenHeadRepoIsForkOrPrefixMismatch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 89);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        var prefixPrIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(prefixPrIssue, 90);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(prefixPrIssue, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(prefixPrIssue, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr, prefixPrIssue });

        // PR #89 from a fork repository ("external-fork/openRuyi"): ignored completely at Tier 1
        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        var forkRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(forkRepo, "external-fork/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, forkRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);

        // PR #90 same-repo bot PR on non-autoupdate/ branch ("manual/curl"): closed on TTL without deleting branch
        var sameRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(sameRepo, "openRuyi-Project/openRuyi");
        var prefixMismatchHead = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(prefixMismatchHead, "manual/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(prefixMismatchHead, "sha90");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(prefixMismatchHead, sameRepo);
        var prefixMismatchPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(prefixMismatchPr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(prefixMismatchPr, prefixMismatchHead);

        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 89, Arg.Any<CancellationToken>())
            .Returns(fullPr);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 90, Arg.Any<CancellationToken>())
            .Returns(prefixMismatchPr);
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "manual/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha90", "manual bump", "aqua-rium", "bot@example.com", "web-flow", "noreply@github.com"));

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, "sha90");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow.AddDays(-9));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new Octokit.StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "manual/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            FailedPrTtlDays = 7,
            MaxConcurrentPullRequests = 5
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Fork PR #89 must receive zero workflow queries, label self-healing, closures, or branch deletions.
        await gitHubMock.DidNotReceive().ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().AddLabelsAsync("openRuyi-Project", "openRuyi", 89, Arg.Any<string[]>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CloseIssueAsync("openRuyi-Project", "openRuyi", 89, Arg.Any<CancellationToken>());

        // Prefix-mismatch same-repo bot PR #90 is closed past TTL without deleting the non-autoupdate/ branch.
        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 90, Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_DryRun_DoesNotEnsureLabels()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await gitHubMock.DidNotReceive().EnsureLabelsExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publisher_AttachesStatusStaleExempt_WhenPublishingPr()
    {
        var gitHubMock = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var casMock = (IOptimisticFileContentGateway)gitHubMock;
        casMock.GetFileContentWithShaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var content = await gitHubMock.GetFileContentAsync(
                    ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<string>(3), ci.ArgAt<CancellationToken>(4));
                return string.IsNullOrEmpty(content) ? null : new RepositoryFileContent(content, "mock-blob-sha");
            });
        casMock.UpdateFileContentIfUnchangedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await gitHubMock.UpdateFileContentAsync(
                ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<string>(3), ci.ArgAt<string>(4), ci.ArgAt<string>(5), ci.ArgAt<CancellationToken>(7)));
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout.openruyi.cn", false);
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.2.2\nRelease: 1\n", null, true);

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 77);
        typeof(PullRequest).GetProperty(nameof(PullRequest.HtmlUrl))?.SetValue(createdPr, "https://github.com/pr/77");

        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha123");
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "baseSha123", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.2.1\nRelease: 1\n");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.CreatePullRequestAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        // Verify StatusStaleExempt and AutoUpdate were attached
        await gitHubMock.Received(1).AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 77,
            Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdate) && l.Contains(GitHubLabels.StatusStaleExempt)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_StabilizationWindow_SkipsRecentCandidates_UnlessSecurityAdvisory()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // curl: published 10 hours ago, not security -> should be skipped by 72h window
        var routineCandidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout", false,
            PublishedAt: DateTimeOffset.UtcNow.AddHours(-10));
        // openssl: published 10 hours ago, IS security advisory -> should bypass 72h window
        var securityCandidate = new UpdateCandidate("openssl", "3.0.0", "3.0.1", CandidateOrigin.Lookout, "https://lookout", true,
            PublishedAt: DateTimeOffset.UtcNow.AddHours(-10));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { routineCandidate, securityCandidate });

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriterMock.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "Name: openssl\nVersion: 3.0.1\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 101));

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("openssl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("openssl", "openssl", "Name: openssl\nVersion: 3.0.0\nRelease: 1\n", "SPECS/openssl/openssl.spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            CooldownHours = 72,
            RequestThrottleMilliseconds = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // curl was skipped due to 72h cooldown
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            routineCandidate, Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // openssl was published because IsSecurityAdvisory bypassed stabilization cooldown
        await publisherMock.Received(1).PublishUpdateAsync(
            securityCandidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/openssl/openssl.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_UsesSpecCatalog_WhenAvailable()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("rust-aho-corasick", "0.7.18", "0.7.20", CandidateOrigin.RustCrate, string.Empty, false);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        var specContext = new SpecContext("rust-aho-corasick", "rust-aho-corasick-0.7", "Name: rust-aho-corasick\nVersion: 0.7.18\n", "SPECS/rust-aho-corasick-0.7/rust-aho-corasick.spec");
        specCatalogMock.GetSpecAsync("rust-aho-corasick", Arg.Any<CancellationToken>())
            .Returns(specContext);

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(specContext).Returns(true);
        rewriterMock.Rewrite(specContext, "0.7.20")
            .Returns(new SpecRewriteResult(true, "Name: rust-aho-corasick\nVersion: 0.7.20\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(candidate, Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 102));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            CooldownHours = 0,
            RequestThrottleMilliseconds = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await specCatalogMock.Received(1).GetSpecAsync("rust-aho-corasick", Arg.Any<CancellationToken>());
        await publisherMock.Received(1).PublishUpdateAsync(
            candidate, Arg.Any<SpecRewriteResult>(),
            "openRuyi-Project/openRuyi", "main", "SPECS/rust-aho-corasick-0.7/rust-aho-corasick.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResetCooldown_ClearsCooldownAndFailureCount()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            MaxConsecutiveFailures = 2,
            GiveUpIssueCooldownDays = 30
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        // Record 2 failures to enter cooldown
        service.RecordFailure("test-pkg");
        service.RecordFailure("test-pkg");
        Assert.True(service.IsInCooldown("test-pkg"));

        // Reset cooldown
        service.ResetCooldown("test-pkg");
        Assert.False(service.IsInCooldown("test-pkg"));
    }

    [Fact]
    public async Task RunSweepAsync_DeletesBranch_WhenHeadRepoMatchesTargetRepo()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 95);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "sha95");

        var targetRepoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(targetRepoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, targetRepoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);

        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 95, Arg.Any<CancellationToken>())
            .Returns(fullPr);
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha95", "bump", "aqua-rium", "bot@example.com", "web-flow", "noreply@github.com"));

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, "sha95");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow.AddDays(-9));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new Octokit.StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            FailedPrTtlDays = 7,
            MaxConcurrentPullRequests = 5
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 95, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ResetsCooldown_WhenTrackingIssueIsClosed()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout", false,
            PublishedAt: DateTimeOffset.UtcNow.AddDays(-5));
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2.1\n", "SPECS/curl/curl.spec"));

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriterMock.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "Name: curl\nVersion: 8.2.2\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 201));

        // Mock issue created when entering cooldown
        var createdIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(createdIssue, 888);
        typeof(Issue).GetProperty(nameof(Issue.State))?.SetValue(createdIssue, new StringEnum<ItemState>(ItemState.Open));

        gitHubMock.CreateIssueAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(createdIssue);
        gitHubMock.GetIssueAsync("openRuyi-Project", "openRuyi", 888, Arg.Any<CancellationToken>())
            .Returns(createdIssue);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConsecutiveFailures = 1,
            IssueOnGivingUp = true,
            CooldownHours = 0,
            RequestThrottleMilliseconds = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        // 1. Record a failure to trigger cooldown and file tracking issue #888
        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 88);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 88, Arg.Any<CancellationToken>()).Returns(fullPr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        // Trigger failure processing
        await service.RunSweepAsync();
        Assert.True(service.IsInCooldown("curl"));

        // 2. Next sweep: tracking issue #888 is closed by human -> should reset cooldown and publish update
        typeof(Issue).GetProperty(nameof(Issue.State))?.SetValue(createdIssue, new StringEnum<ItemState>(ItemState.Closed));
        await service.RunSweepAsync();

        Assert.False(service.IsInCooldown("curl"));
        await publisherMock.Received(1).PublishUpdateAsync(
            candidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ResetsCooldown_WhenManuallyUpdatedOnMain()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout", false);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        // Spec on main already at 8.2.2 (human manually upgraded!)
        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2.2\nRelease: 1\n", "SPECS/curl/curl.spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConsecutiveFailures = 1,
            CooldownHours = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        // Put package in cooldown
        service.RecordFailure("curl");
        Assert.True(service.IsInCooldown("curl"));

        // Sweep runs and detects spec already at target version -> resets cooldown
        await service.RunSweepAsync();

        Assert.False(service.IsInCooldown("curl"));
    }

    [Fact]
    public async Task RunSweepAsync_AllowsSecurityAdvisories_WhenRoutineConcurrentLimitReached()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // 1 open active PR currently running
        var activePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(activePr, 101);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(activePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(activePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { activePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/active-pkg");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>())
            .Returns(fullPr);
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/active-pkg", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkflowRun>()); // Still active/pending, not failed

        // Two candidates: one security advisory, one routine update
        var securityCandidate = new UpdateCandidate("openssl", "3.0.0", "3.0.1", CandidateOrigin.Lookout, "https://lookout", true);
        var routineCandidate = new UpdateCandidate("curl", "8.2.0", "8.2.1", CandidateOrigin.Lookout, "https://lookout", false);

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { routineCandidate, securityCandidate });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("openssl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("openssl", "openssl", "Name: openssl\nVersion: 3.0.0\nRelease: 1\n", "SPECS/openssl/openssl.spec"));
        specCatalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2.0\nRelease: 1\n", "SPECS/curl/curl.spec"));

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriterMock.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(callInfo => new SpecRewriteResult(true, $"Name: {callInfo.Arg<SpecContext>().PackageName}\nVersion: {callInfo.Arg<string>()}\nRelease: 1\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, null, 202, "https://pr/202"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConcurrentPullRequests = 1, // Only 1 routine PR allowed, which is already occupied by PR #101!
            CooldownHours = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Security advisory MUST be published thanks to dedicated concurrency allowance
        await publisherMock.Received(1).PublishUpdateAsync(
            securityCandidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/openssl/openssl.spec", Arg.Any<CancellationToken>());

        // Routine candidate MUST NOT be published as routine concurrent limit (1) was reached
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            routineCandidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_AndStopAsync_RunBackgroundServiceLifecycle()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = false, // Disabled: skips sweep
            TargetRepository = "openRuyi-Project/openRuyi",
            CheckIntervalHours = 1
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunSweepAsync_HandlesInvalidTargetRepoAndRateLimitExhaustion()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var optionsInvalid = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "invalid-repo-format"
        });

        var service1 = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), optionsInvalid),
            Substitute.For<ISpecCatalog>(),
            optionsInvalid,
            Substitute.For<IRemoteAssetBackfiller>());

        // Must return without throwing
        await service1.RunSweepAsync();
        await gitHubMock.DidNotReceive().GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>());

        // Rate limit below threshold
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(100, DateTimeOffset.UtcNow.AddHours(1), 10, DateTimeOffset.UtcNow.AddMinutes(1)));

        var optionsLowRate = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            MinRateLimitRemaining = 500
        });

        var service2 = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), optionsLowRate),
            Substitute.For<ISpecCatalog>(),
            optionsLowRate,
            Substitute.For<IRemoteAssetBackfiller>());

        await service2.RunSweepAsync();
        await gitHubMock.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_FiltersNoAutoUpdate_Patches_AndCrossMajor()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidateOptOut = new UpdateCandidate("pkg-optout", "1.0", "1.1", CandidateOrigin.Lookout, "https://lookout");
        var candidatePatch = new UpdateCandidate("pkg-patch", "1.0", "1.1", CandidateOrigin.Lookout, "https://lookout");
        var candidateMajor = new UpdateCandidate("pkg-major", "1.0", "2.0", CandidateOrigin.Lookout, "https://lookout");
        var candidateNoRewriter = new UpdateCandidate("pkg-norewriter", "1.0", "1.1", CandidateOrigin.Lookout, "https://lookout");

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { candidateOptOut, candidatePatch, candidateMajor, candidateNoRewriter });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("pkg-optout", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkg-optout", "pkg-optout", "#!NoAutoUpdate: pinned\nVersion: 1.0\n", "SPECS/pkg-optout.spec"));
        specCatalogMock.GetSpecAsync("pkg-patch", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkg-patch", "pkg-patch", "Patch0: security.patch\nVersion: 1.0\n", "SPECS/pkg-patch.spec"));
        specCatalogMock.GetSpecAsync("pkg-major", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkg-major", "pkg-major", "Version: 1.0\n", "SPECS/pkg-major.spec"));
        specCatalogMock.GetSpecAsync("pkg-norewriter", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkg-norewriter", "pkg-norewriter", "Version: 1.0\n", "SPECS/pkg-norewriter.spec"));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            TargetRepository = "owner/repo",
            SkipPackagesWithPatches = true,
            AllowCrossMajorVersion = false,
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(), // No rewriter
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Zero PRs should be published
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndRecordFailure_RespectsDryRunAndIssueOnGivingUpFlags()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 301);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/failing-dryrun");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 301, Arg.Any<CancellationToken>()).Returns(fullPr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/failing-dryrun", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = true, // DryRun = true: must NOT create tracking issue on GitHub
            TargetRepository = "owner/repo",
            MaxConsecutiveFailures = 1,
            IssueOnGivingUp = true
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Verify issue was NOT created due to DryRun
        await gitHubMock.DidNotReceive().CreateIssueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ReclaimsInactiveFailedBotPr_AndDeletesBranch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 401);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/inactive-pkg");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "sha999");
        var headRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, headRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 401, Arg.Any<CancellationToken>()).Returns(fullPr);
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/inactive-pkg", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha999", "bump", "aqua-rium", "bot@example.com", "web-flow", "noreply@github.com"));

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, "sha999");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/inactive-pkg", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            FailedPrTtlDays = 7,
            BranchPrefix = "autoupdate/"
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 401, Arg.Is<string>(s => s.Contains("failing CI and inactive")), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 401, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/inactive-pkg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_TriggersRemoteAssetBackfiller_WhenRemoteAssetWorkflowFails()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var prIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(prIssue, 501);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(prIssue, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(prIssue, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prIssue });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "sha501");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 501, Arg.Any<CancellationToken>()).Returns(fullPr);
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha501", "SPECS: bump curl", "aqua-rium", "bot@openruyi.org", "web-flow", "noreply@github.com"));

        var remoteAssetRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Id))?.SetValue(remoteAssetRun, 9999L);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Name))?.SetValue(remoteAssetRun, "RemoteAsset Verification");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(remoteAssetRun, "sha501");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(remoteAssetRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(remoteAssetRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { remoteAssetRun });

        var backfillerMock = Substitute.For<IRemoteAssetBackfiller>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            backfillerMock);

        await service.RunSweepAsync();

        await backfillerMock.Received(1).TryBackfillAsync("owner", "repo", "autoupdate/curl", 501, 9999L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_SkipsCandidatesWithInvalidPackageNameOrVersion()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate1 = new UpdateCandidate("invalid name with spaces", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty);
        var candidate2 = new UpdateCandidate("curl", "1.0", "1.1$badversion", CandidateOrigin.Lookout, string.Empty);

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate1, candidate2 });

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_EnforcesMaxCandidatesPerSweep_AndSecurityConcurrencyAllowance()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // 1 active PR already open
        var activePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(activePr, 10);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(activePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(activePr, new List<Label> { autoUpdateLabel });
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { activePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
        var activeHead = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(activeHead, "autoupdate/active");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(activeHead, repoObj);
        var activeFullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(activeFullPr, 10);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(activeFullPr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(activeFullPr, activeHead);
        gitHubMock.GetPullRequestAsync("owner", "repo", 10, Arg.Any<CancellationToken>()).Returns(activeFullPr);
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/active", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkflowRun>());

        var routineCandidate = new UpdateCandidate("curl", "8.0", "8.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: false);
        var securityCandidate = new UpdateCandidate("openssl", "3.0", "3.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true);

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { routineCandidate, securityCandidate });

        var specCatalog = Substitute.For<ISpecCatalog>();
        var curlSpec = new SpecContext("curl", "curl", "Name: curl\nVersion: 8.0\n", "SPECS/curl/curl.spec");
        var opensslSpec = new SpecContext("openssl", "openssl", "Name: openssl\nVersion: 3.0\n", "SPECS/openssl/openssl.spec");
        specCatalog.GetSpecAsync("curl", Arg.Any<CancellationToken>()).Returns(curlSpec);
        specCatalog.GetSpecAsync("openssl", Arg.Any<CancellationToken>()).Returns(opensslSpec);

        var rewriter = Substitute.For<ISpecVersionRewriter>();
        rewriter.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriter.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "updated content", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 20));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            MaxConcurrentPullRequests = 1, // Concurrency limit 1 (active count is 1)
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriter },
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Routine candidate was skipped because activePrCount (1) >= MaxConcurrentPullRequests (1)
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            routineCandidate, Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Security candidate was published because activePrCount (1) < MaxConcurrentPullRequests (1) + Allowance (5)
        await publisherMock.Received(1).PublishUpdateAsync(
            securityCandidate, Arg.Any<SpecRewriteResult>(), "owner/repo", "main", "SPECS/openssl/openssl.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_SkipsCandidateWhenNoRewriterCanHandleOrRewriteHasNoChange()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate1 = new UpdateCandidate("curl", "8.0", "8.1", CandidateOrigin.Lookout, string.Empty);
        var candidate2 = new UpdateCandidate("tar", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty);

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate1, candidate2 });

        var specCatalog = Substitute.For<ISpecCatalog>();
        var curlSpec = new SpecContext("curl", "curl", "Name: curl\nVersion: 8.0\n", "SPECS/curl/curl.spec");
        var tarSpec = new SpecContext("tar", "tar", "Name: tar\nVersion: 1.0\n", "SPECS/tar/tar.spec");
        specCatalog.GetSpecAsync("curl", Arg.Any<CancellationToken>()).Returns(curlSpec);
        specCatalog.GetSpecAsync("tar", Arg.Any<CancellationToken>()).Returns(tarSpec);

        var rewriter = Substitute.For<ISpecVersionRewriter>();
        rewriter.CanHandle(curlSpec).Returns(false); // No rewriter can handle curl
        rewriter.CanHandle(tarSpec).Returns(true);
        rewriter.Rewrite(tarSpec, "1.1").Returns(new SpecRewriteResult(false, tarSpec.Content, "Regex match failure", false)); // Rewrite failed

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriter },
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ResetsCooldownWhenMainBranchAlreadyAtOrPastTargetVersion()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("curl", "8.0", "8.1", CandidateOrigin.Lookout, string.Empty);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalog = Substitute.For<ISpecCatalog>();
        // Main branch is already at 8.2 (past target 8.1)
        var specContext = new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2\nRelease: 1\n", "SPECS/curl/curl.spec");
        specCatalog.GetSpecAsync("curl", Arg.Any<CancellationToken>()).Returns(specContext);

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        // Skipped because already past target
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_SkipsCandidate_WhenOpenAutoUpdateBlockedIssueExists()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var blockedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(blockedIssue, 999);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(blockedIssue, "[Auto-Update: Blocked] Repeated CI failures on package curl");

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { blockedIssue });
        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var candidate = new UpdateCandidate("curl", "8.0", "8.1", CandidateOrigin.Lookout, string.Empty);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalog = Substitute.For<ISpecCatalog>();
        var specContext = new SpecContext("curl", "curl", "Name: curl\nVersion: 8.0\nRelease: 1\n", "SPECS/curl/curl.spec");
        specCatalog.GetSpecAsync("curl", Arg.Any<CancellationToken>()).Returns(specContext);

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(specContext).Returns(true);
        rewriterMock.Rewrite(specContext, "8.1").Returns(new SpecRewriteResult(true, "Name: curl\nVersion: 8.1\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_SkipsCandidate_WhenOpenPrCarriesAutoUpdateBlockedLabel()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.AutoUpdateBlocked);

        var openPrIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(openPrIssue, 505);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(openPrIssue, (DateTimeOffset?)DateTimeOffset.UtcNow);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(openPrIssue, new List<Label> { blockedLabel });

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { openPrIssue });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/zlib");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 505, Arg.Any<CancellationToken>()).Returns(fullPr);
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/zlib", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkflowRun>());

        var candidate = new UpdateCandidate("zlib", "1.3", "1.4", CandidateOrigin.Lookout, string.Empty);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalog = Substitute.For<ISpecCatalog>();
        var specContext = new SpecContext("zlib", "zlib", "Name: zlib\nVersion: 1.3\nRelease: 1\n", "SPECS/zlib/zlib.spec");
        specCatalog.GetSpecAsync("zlib", Arg.Any<CancellationToken>()).Returns(specContext);

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            CooldownHours = 0,
            MaxConcurrentPullRequests = 5
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_WhenBlockedIssueSearchThrows_FailsClosedAndDefersCandidatePublishing()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Issue>>>(_ => throw new InvalidOperationException("Search unavailable"));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new UpdateCandidate("curl", "8.0", "8.1", CandidateOrigin.Lookout, string.Empty) });
        var publisherMock = Substitute.For<IPackageUpdatePublisher>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        var ex = await Record.ExceptionAsync(() => service.RunSweepAsync());
        Assert.Null(ex);
        await sourceMock.DidNotReceive().GetCandidatesAsync(Arg.Any<CancellationToken>());
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_WhenOpenPrSearchThrows_FailsClosedAndDefersCandidatePublishing()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Issue>>>(_ => throw new HttpRequestException("Search API timeout"));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new UpdateCandidate("zlib", "1.3", "1.4", CandidateOrigin.Lookout, string.Empty) });
        var publisherMock = Substitute.For<IPackageUpdatePublisher>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        var ex = await Record.ExceptionAsync(() => service.RunSweepAsync());
        Assert.Null(ex);
        await sourceMock.DidNotReceive().GetCandidatesAsync(Arg.Any<CancellationToken>());
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailedPr_AcrossTwoServiceInstancesSharingBotStateStore_DeduplicatesAfterRestart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg-failedpr-restart-{Guid.NewGuid():N}");
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var failedPrIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(failedPrIssue, 99);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(failedPrIssue, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-1));
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(failedPrIssue, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { failedPrIssue });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "sha99");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 99, Arg.Any<CancellationToken>()).Returns(fullPr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, "sha99");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            MaxConsecutiveFailures = 2,
            GiveUpIssueCooldownDays = 30
        });
        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });

        try
        {
            using (var store1 = new JsonBotStateStore(stateOpts))
            {
                var service1 = new PackageUpdateService(
                    gitHubMock,
                    Array.Empty<IUpdateCandidateSource>(),
                    Array.Empty<ISpecVersionRewriter>(),
                    Substitute.For<IPackageUpdatePublisher>(),
                    new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                    Substitute.For<ISpecCatalog>(),
                    options,
                    Substitute.For<IRemoteAssetBackfiller>(),
                    stateStore: store1);

                await service1.RunSweepAsync();
                Assert.Equal(1, store1.GetPackageFailure("owner", "repo", "curl")?.ConsecutiveFailures);
                Assert.False(service1.IsInCooldown("curl"));
            }

            // Simulate process restart with a brand new store2 + service2 encountering the SAME PR #99
            using var store2 = new JsonBotStateStore(stateOpts);
            var service2 = new PackageUpdateService(
                gitHubMock,
                Array.Empty<IUpdateCandidateSource>(),
                Array.Empty<ISpecVersionRewriter>(),
                Substitute.For<IPackageUpdatePublisher>(),
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                Substitute.For<ISpecCatalog>(),
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: store2);

            await service2.RunSweepAsync();
            Assert.Equal(1, store2.GetPackageFailure("owner", "repo", "curl")?.ConsecutiveFailures);
            Assert.False(service2.IsInCooldown("curl"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_WithLookoutAndRustSources_PublishesBothWithoutStarvation()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var lookoutCandidates = Enumerable.Range(0, 10)
            .Select(i => new UpdateCandidate($"pkg-{i:D2}", "1.0.0", "1.1.0", CandidateOrigin.Lookout, string.Empty))
            .ToList();
        var rustCandidates = new[]
        {
            new UpdateCandidate("rust-acpi", "0.2.0", "0.2.1", CandidateOrigin.RustCrate, string.Empty)
        };

        var lookoutSource = Substitute.For<IUpdateCandidateSource>();
        lookoutSource.SourceName.Returns("Lookout");
        lookoutSource.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(lookoutCandidates);

        var rustSource = Substitute.For<IUpdateCandidateSource>();
        rustSource.SourceName.Returns("RustCrates");
        rustSource.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(rustCandidates);

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                var ver = pkg.StartsWith("rust-", StringComparison.OrdinalIgnoreCase) ? "0.2.0" : "1.0.0";
                return new SpecContext(pkg, pkg, $"Name: {pkg}\nVersion: {ver}\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var rewriter = Substitute.For<ISpecVersionRewriter>();
        rewriter.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriter.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "updated", null, HasChanged: true));

        var publishedOriginCounts = new Dictionary<CandidateOrigin, int>();
        var publisher = Substitute.For<IPackageUpdatePublisher>();
        publisher.PublishUpdateAsync(
                Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cand = call.Arg<UpdateCandidate>();
                publishedOriginCounts[cand.Origin] = publishedOriginCounts.GetValueOrDefault(cand.Origin) + 1;
                return new PublishResult(true, false, PullRequestNumber: 500 + publishedOriginCounts.Values.Sum());
            });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            MaxCandidatesPerSweep = 3,
            MaxConcurrentPullRequests = 10,
            RequestThrottleMilliseconds = 0,
            CooldownHours = 0
        });
        var healthState = new RuntimeHealthState();

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { lookoutSource, rustSource },
            new[] { rewriter },
            publisher,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            healthState: healthState);

        await service.RunSweepAsync();

        Assert.Equal(2, publishedOriginCounts[CandidateOrigin.Lookout]);
        Assert.Equal(1, publishedOriginCounts[CandidateOrigin.RustCrate]);
        Assert.Equal(2, healthState.GetSourceHealth()["Lookout"].LastPublishedCount);
        Assert.Equal(1, healthState.GetSourceHealth()["RustCrates"].LastPublishedCount);
    }

    [Fact]
    public async Task RunSweepAsync_WhenFirstCandidateThrows_ContinuesToSecondCandidateAndRunsObsVerifier()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidates = new[]
        {
            new UpdateCandidate("bad-pkg", "1.0.0", "1.1.0", CandidateOrigin.Lookout, string.Empty),
            new UpdateCandidate("good-pkg", "1.0.0", "1.1.0", CandidateOrigin.Lookout, string.Empty)
        };
        var source = Substitute.For<IUpdateCandidateSource>();
        source.SourceName.Returns("Lookout");
        source.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(candidates);

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"Name: {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var rewriter = Substitute.For<ISpecVersionRewriter>();
        rewriter.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriter.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "updated", null, HasChanged: true));

        var publisher = Substitute.For<IPackageUpdatePublisher>();
        publisher.PublishUpdateAsync(
                Arg.Is<UpdateCandidate>(c => c.PackageName == "bad-pkg"), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<PublishResult>(_ => throw new HttpRequestException("422 validation failed"));
        publisher.PublishUpdateAsync(
                Arg.Is<UpdateCandidate>(c => c.PackageName == "good-pkg"), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 601));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            MaxCandidatesPerSweep = 5,
            RequestThrottleMilliseconds = 0,
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { source },
            new[] { rewriter },
            publisher,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisher.Received(1).PublishUpdateAsync(
            Arg.Is<UpdateCandidate>(c => c.PackageName == "good-pkg"),
            Arg.Any<SpecRewriteResult>(), "owner/repo", "main", "SPECS/good-pkg/good-pkg.spec", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).SearchMergedPullRequestsAsync(
            "owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_DryRun_DoesNotWritePackageFailureToBotStateStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg-dryrun-state-{Guid.NewGuid():N}");
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 701);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 701, Arg.Any<CancellationToken>()).Returns(fullPr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "owner/repo",
            MaxConsecutiveFailures = 1
        });
        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });

        try
        {
            using var store = new JsonBotStateStore(stateOpts);
            var service = new PackageUpdateService(
                gitHubMock,
                Array.Empty<IUpdateCandidateSource>(),
                Array.Empty<ISpecVersionRewriter>(),
                Substitute.For<IPackageUpdatePublisher>(),
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                Substitute.For<ISpecCatalog>(),
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: store);

            await service.RunSweepAsync();

            Assert.Null(store.GetPackageFailure("owner", "repo", "curl"));
            Assert.False(service.IsInCooldown("curl"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_WhenObsBlockedTrackingIssueExists_SkipsBlockedPackage()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var blockedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(blockedIssue, 811);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(blockedIssue, ObsPostMergeVerifier.BuildObsBlockedIssueTitle("curl"));

        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { blockedIssue });
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var candidate = new UpdateCandidate("curl", "8.0.0", "8.1.0", CandidateOrigin.Lookout, string.Empty);
        var source = Substitute.For<IUpdateCandidateSource>();
        source.SourceName.Returns("Lookout");
        source.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.0.0\n", "SPECS/curl/curl.spec"));

        var publisher = Substitute.For<IPackageUpdatePublisher>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { source },
            Array.Empty<ISpecVersionRewriter>(),
            publisher,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisher.DidNotReceiveWithAnyArgs().PublishUpdateAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RunSweepAsync_PersistsSchedulerOriginOffsetAcrossServiceInstances()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg-scheduler-offset-{Guid.NewGuid():N}");
        try
        {
            var gitHubMock = Substitute.For<IGitHubService>();
            gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
                .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
            gitHubMock.SearchStaleCandidatesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>()));
            gitHubMock.SearchMergedPullRequestsAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PullRequest>>(Array.Empty<PullRequest>()));

            var lookoutCandidate = new UpdateCandidate("lookout-one", "1.0.0", "1.1.0", CandidateOrigin.Lookout, string.Empty);
            var rustCandidate = new UpdateCandidate("rust-one", "1.0.0", "1.1.0", CandidateOrigin.RustCrate, string.Empty);

            var lookoutSource = Substitute.For<IUpdateCandidateSource>();
            lookoutSource.SourceName.Returns("Lookout");
            lookoutSource.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { lookoutCandidate });

            var rustSource = Substitute.For<IUpdateCandidateSource>();
            rustSource.SourceName.Returns("RustCrates");
            rustSource.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { rustCandidate });

            var specCatalog = Substitute.For<ISpecCatalog>();
            specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var pkg = call.Arg<string>();
                    return new SpecContext(pkg, pkg, $"Name: {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
                });

            var rewriter = Substitute.For<ISpecVersionRewriter>();
            rewriter.CanHandle(Arg.Any<SpecContext>()).Returns(true);
            rewriter.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
                .Returns(new SpecRewriteResult(true, "updated", null, HasChanged: true));

            var publishedByOrigin = new Dictionary<CandidateOrigin, int>();
            var publisher = Substitute.For<IPackageUpdatePublisher>();
            publisher.PublishUpdateAsync(
                    Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var candidate = call.Arg<UpdateCandidate>();
                    publishedByOrigin[candidate.Origin] = publishedByOrigin.GetValueOrDefault(candidate.Origin) + 1;
                    return new PublishResult(true, false, PullRequestNumber: 900 + publishedByOrigin.Values.Sum());
                });

            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "owner/repo",
                MaxCandidatesPerSweep = 1,
                MaxConcurrentPullRequests = 10,
                RequestThrottleMilliseconds = 0,
                CooldownHours = 0
            });
            var stateOptions = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using var stateStore = new JsonBotStateStore(stateOptions);

            var service1 = new PackageUpdateService(
                gitHubMock,
                new[] { lookoutSource, rustSource },
                new[] { rewriter },
                publisher,
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                specCatalog,
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: stateStore);
            await service1.RunSweepAsync();

            var service2 = new PackageUpdateService(
                gitHubMock,
                new[] { lookoutSource, rustSource },
                new[] { rewriter },
                publisher,
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                specCatalog,
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: stateStore);
            await service2.RunSweepAsync();

            Assert.Equal(1, publishedByOrigin[CandidateOrigin.Lookout]);
            Assert.Equal(1, publishedByOrigin[CandidateOrigin.RustCrate]);
            Assert.Equal("2", stateStore.GetCursorWatermark(PackageUpdateService.GetSchedulerOffsetStateKey(options.Value.TargetRepository)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_WhenRequiredLabelsCannotBeVerified_DefersPublishAndStillRunsObsVerifier()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            Obs = new ObsOptions { RequireObsSuccessAfterMerge = true },
            MaxConcurrentPullRequests = 5
        });

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.SourceName.Returns("RustCrate");
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new UpdateCandidate("rust-zip", "0.6.6", "2.1.3", CandidateOrigin.RustCrate, "https://crates.io/crates/zip") });

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
        inventoryGateway.EnsureRequiredLabelsVerifiedAsync(
            "openRuyi-Project",
            "openRuyi",
            Arg.Any<IEnumerable<(string Name, string Color, string Description)>>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            obsVerifier,
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            inventoryGateway: inventoryGateway);

        await service.RunSweepAsync();

        await publisherMock.DidNotReceiveWithAnyArgs().PublishUpdateAsync(default!, default!, default!, default!, default, default);
        await gitHubMock.Received(1).SearchMergedPullRequestsAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_BranchPrefixInventory_SelfHealsMissingLabelsAndEnforcesDeduplication()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        var unlabeledPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(unlabeledPr, 77);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(unlabeledPr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(unlabeledPr, "Update rust-zip to 2.1.3");
        typeof(PullRequest).GetProperty(nameof(PullRequest.UpdatedAt))?.SetValue(unlabeledPr, DateTimeOffset.UtcNow);
        typeof(PullRequest).GetProperty(nameof(PullRequest.CreatedAt))?.SetValue(unlabeledPr, DateTimeOffset.UtcNow);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(unlabeledPr, Array.Empty<Label>());
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/rust-zip");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(unlabeledPr, headRef);

        var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
        inventoryGateway.EnsureRequiredLabelsVerifiedAsync(
            "openRuyi-Project",
            "openRuyi",
            Arg.Any<IEnumerable<(string Name, string Color, string Description)>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);
        inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync(
            "openRuyi-Project",
            "openRuyi",
            "autoupdate/",
            Arg.Any<CancellationToken>())
            .Returns(new[] { unlabeledPr });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConcurrentPullRequests = 5
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            inventoryGateway: inventoryGateway);

        await service.RunSweepAsync();

        await gitHubMock.Received(1).AddLabelsAsync(
            "openRuyi-Project",
            "openRuyi",
            77,
            Arg.Is<string[]>(labels =>
                labels.Contains(GitHubLabels.AutoUpdate) &&
                labels.Contains(GitHubLabels.StatusStaleExempt) &&
                labels.Contains(GitHubLabels.AutoUpdateRust)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ZeroCandidates_ResetsRuntimeHealthPublishCounters()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var healthState = new RuntimeHealthState();
        healthState.RecordSourceSweepStats("RustCrate", publishedCount: 4, skippedCount: 2, oldestCandidatePublishedAt: DateTimeOffset.UtcNow.AddDays(-1));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.SourceName.Returns("RustCrate");
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UpdateCandidate>());

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConcurrentPullRequests = 5
        });

        var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
        inventoryGateway.EnsureRequiredLabelsVerifiedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            healthState: healthState,
            inventoryGateway: inventoryGateway);

        await service.RunSweepAsync();

        var snapshot = healthState.GetSourceHealth()["RustCrate"];
        Assert.Equal("healthy", snapshot.Status);
        Assert.Equal(0, snapshot.LastPublishedCount);
        Assert.Equal(0, snapshot.LastSkippedCount);
        Assert.Null(snapshot.OldestCandidatePublishedAt);
    }

    [Fact]
    public void RecordFailure_DecaysStaleFailureCount_AfterGiveUpCooldownDays()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"abaci-decay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var staleTime = DateTimeOffset.UtcNow.AddDays(-35);
            File.WriteAllText(
                Path.Combine(tempDir, "bot-state.json"),
                $$"""
                {
                  "Repositories": {
                    "openRuyi-Project/openRuyi": {
                      "PackageFailures": {
                        "old-pkg": {
                          "ConsecutiveFailures": 2,
                          "CooldownUntil": null,
                          "LastFailureAt": "{{staleTime:O}}"
                        }
                      }
                    }
                  }
                }
                """);

            using var stateStore = new JsonBotStateStore(Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir
            }));

            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                TargetRepository = "openRuyi-Project/openRuyi",
                MaxConsecutiveFailures = 3,
                GiveUpIssueCooldownDays = 30
            });
            var gitHubMock = Substitute.For<IGitHubService>();
            var service = new PackageUpdateService(
                gitHubMock,
                Array.Empty<IUpdateCandidateSource>(),
                Array.Empty<ISpecVersionRewriter>(),
                Substitute.For<IPackageUpdatePublisher>(),
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                Substitute.For<ISpecCatalog>(),
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: stateStore);

            // Stale 2-failure count (>30 days old) decays back to 1 instead of triggering cooldown at 3.
            Assert.False(service.RecordFailure("old-pkg"));
            Assert.False(service.IsInCooldown("old-pkg"));
            Assert.False(service.RecordFailure("old-pkg"));
            Assert.True(service.RecordFailure("old-pkg"));
            Assert.True(service.IsInCooldown("old-pkg"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_FailingPrsBoundToMaxConcurrentMinusOne_AndHardCapBlocksAtTwiceMax()
    {
        static (Issue Issue, PullRequest Pr, WorkflowRun FailedRun) MakeFailingOpenPr(int number, string pkg)
        {
            var autoLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
            typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoLabel, GitHubLabels.AutoUpdate);

            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
            typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(issue, $"Update {pkg} to 1.0.0");
            typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(issue, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-1));
            typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(issue, new List<Label> { autoLabel });

            var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
            typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
            var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");

            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(pr, botUser);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, $"Update {pkg} to 1.0.0");
            typeof(PullRequest).GetProperty(nameof(PullRequest.UpdatedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddHours(-1));
            typeof(PullRequest).GetProperty(nameof(PullRequest.CreatedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddHours(-1));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new[] { autoLabel });
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, $"autoupdate/{pkg}");
            typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, $"sha-{number}");
            typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);

            var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, $"sha-{number}");
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
            return (issue, pr, failedRun);
        }

        async Task<int> RunWithFailingCountAsync(int failingCount)
        {
            var gitHubMock = Substitute.For<IGitHubService>();
            gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
                .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
            var entries = Enumerable.Range(1, failingCount).Select(i => MakeFailingOpenPr(i, $"failing-{i}")).ToList();
            gitHubMock.SearchStaleCandidatesAsync(
                "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<Issue>());
            gitHubMock.SearchStaleCandidatesAsync(
                "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(entries.Select(p => p.Issue).ToList());
            gitHubMock.SearchMergedPullRequestsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<PullRequest>());
            foreach (var entry in entries)
            {
                gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", entry.Pr.Number, Arg.Any<CancellationToken>())
                    .Returns(entry.Pr);
                gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", entry.Pr.Head.Ref, Arg.Any<CancellationToken>())
                    .Returns(new[] { entry.FailedRun });
            }

            var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
            inventoryGateway.EnsureRequiredLabelsVerifiedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>())
                .Returns(true);
            inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync("openRuyi-Project", "openRuyi", "autoupdate/", Arg.Any<CancellationToken>())
                .Returns(entries.Select(p => p.Pr).ToList());

            var sourceMock = Substitute.For<IUpdateCandidateSource>();
            sourceMock.SourceName.Returns("Lookout");
            sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
                .Returns(new[] { new UpdateCandidate("curl", "0.9.0", "0.9.1", CandidateOrigin.Lookout, "https://example.org/curl") });

            var catalogMock = Substitute.For<ISpecCatalog>();
            var spec = new SpecContext("curl", "curl", "Name: curl\nVersion: 0.9.0\n", "SPECS/curl/curl.spec");
            catalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>()).Returns(spec);

            var rewriterMock = Substitute.For<ISpecVersionRewriter>();
            rewriterMock.CanHandle(spec).Returns(true);
            rewriterMock.Rewrite(spec, "0.9.1").Returns(new SpecRewriteResult(true, "Name: curl\nVersion: 0.9.1\n", null, true));

            var published = 0;
            var publisherMock = Substitute.For<IPackageUpdatePublisher>();
            publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    published++;
                    return new PublishResult(true, false, null, 200);
                });

            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi",
                MaxConcurrentPullRequests = 5
            });

            var service = new PackageUpdateService(
                gitHubMock,
                new[] { sourceMock },
                new[] { rewriterMock },
                publisherMock,
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
                catalogMock,
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                inventoryGateway: inventoryGateway);

            await service.RunSweepAsync();
            return published;
        }

        // 5 failing PRs leave 1 slot open (bounded to MaxConcurrent - 1 = 4) -> healthy curl still published.
        Assert.Equal(1, await RunWithFailingCountAsync(5));

        // 10 total open PRs hits hardCap (MaxConcurrent * 2 = 10) -> blocks new PR creation.
        Assert.Equal(0, await RunWithFailingCountAsync(10));
    }

    [Fact]
    public async Task RunSweepAsync_DryRun_DoesNotCommitRustCursorOrSchedulerOffset_AndNextLiveSweepProcessesSameSlice()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg-dryrun-cursor-{Guid.NewGuid():N}");
        var crates = new[] { "rust-a", "rust-b", "rust-c", "rust-d" };
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var handler = new StaticHttpResponseHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var rewriter = Substitute.For<ISpecVersionRewriter>();
        rewriter.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriter.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "updated", null, HasChanged: true));

        var publishedLive = new List<string>();
        var publisher = Substitute.For<IPackageUpdatePublisher>();
        publisher.PublishUpdateAsync(
                Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cand = call.Arg<UpdateCandidate>();
                publishedLive.Add(cand.PackageName);
                return new PublishResult(true, false, null, 300);
            });

        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
        try
        {
            var dryOptions = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = true,
                TargetRepository = "openRuyi-Project/openRuyi",
                MaxCandidatesPerSweep = 2,
                MaxConcurrentPullRequests = 5,
                RequestThrottleMilliseconds = 0,
                CooldownHours = 0,
                RustCratePoller = new RustCratePollerOptions
                {
                    Enabled = true,
                    IndexBaseUrl = "https://index.crates.io",
                    MaxCratesPerSweep = 2,
                    RequestDelayMilliseconds = 0
                }
            });

            var rustKey = RustCrateVersionSource.GetCursorStateKey(dryOptions.Value.TargetRepository);
            var offsetKey = PackageUpdateService.GetSchedulerOffsetStateKey(dryOptions.Value.TargetRepository);

            using (var dryStore = new JsonBotStateStore(stateOpts))
            {
                var drySource = new RustCrateVersionSource(new HttpClient(handler), dryOptions, specCatalog: specCatalog, stateStore: dryStore);
                var dryService = new PackageUpdateService(
                    gitHubMock,
                    new[] { drySource },
                    new[] { rewriter },
                    publisher,
                    new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), dryOptions),
                    specCatalog,
                    dryOptions,
                    Substitute.For<IRemoteAssetBackfiller>(),
                    stateStore: dryStore);

                await dryService.RunSweepAsync();

                Assert.Null(dryStore.GetCursorWatermark(rustKey));
                Assert.Null(dryStore.GetCursorWatermark(offsetKey));
            }

            // Switching to DryRun=false must start from rust-a, rust-b (not rust-c, rust-d)
            publishedLive.Clear();
            var liveOptions = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi",
                MaxCandidatesPerSweep = 2,
                MaxConcurrentPullRequests = 5,
                RequestThrottleMilliseconds = 0,
                CooldownHours = 0,
                RustCratePoller = new RustCratePollerOptions
                {
                    Enabled = true,
                    IndexBaseUrl = "https://index.crates.io",
                    MaxCratesPerSweep = 2,
                    RequestDelayMilliseconds = 0
                }
            });
            var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
            inventoryGateway.EnsureRequiredLabelsVerifiedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>())
                .Returns(true);
            inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<PullRequest>());

            using var liveStore = new JsonBotStateStore(stateOpts);
            var liveSource = new RustCrateVersionSource(new HttpClient(handler), liveOptions, specCatalog: specCatalog, stateStore: liveStore);
            var liveService = new PackageUpdateService(
                gitHubMock,
                new[] { liveSource },
                new[] { rewriter },
                publisher,
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), liveOptions),
                specCatalog,
                liveOptions,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: liveStore,
                inventoryGateway: inventoryGateway);

            await liveService.RunSweepAsync();

            Assert.Equal(new[] { "rust-a", "rust-b" }, publishedLive);
            Assert.Equal("rust-b", liveStore.GetCursorWatermark(rustKey));
            Assert.Equal("1", liveStore.GetCursorWatermark(offsetKey));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_WhenMainSpecAdvancedPastCandidateCurrentVersion_UsesMainVersionForCrossMajorAndTitle()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        // Stale candidate discovery says 1.0.0 -> 2.1.0 (which looks cross-major),
        // while main branch SPEC was already bumped manually to 2.0.0 (same major as 2.1.0).
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.SourceName.Returns("Lookout");
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new UpdateCandidate("foo", "1.0.0", "2.1.0", CandidateOrigin.Lookout, "https://lookout/foo") });

        var spec = new SpecContext("foo", "foo", "Name: foo\nVersion: 2.0.0\n", "SPECS/foo/foo.spec");
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetSpecAsync("foo", Arg.Any<CancellationToken>()).Returns(spec);

        var rewriter = Substitute.For<ISpecVersionRewriter>();
        rewriter.CanHandle(spec).Returns(true);
        rewriter.Rewrite(spec, "2.1.0").Returns(new SpecRewriteResult(true, "Name: foo\nVersion: 2.1.0\n", null, HasChanged: true));

        UpdateCandidate? publishedCandidate = null;
        var publisher = Substitute.For<IPackageUpdatePublisher>();
        publisher.PublishUpdateAsync(
                Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                publishedCandidate = call.Arg<UpdateCandidate>();
                return new PublishResult(true, false, null, 601);
            });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            AllowCrossMajorVersion = false,
            CooldownHours = 0,
            RequestThrottleMilliseconds = 0
        });
        var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
        inventoryGateway.EnsureRequiredLabelsVerifiedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriter },
            publisher,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            inventoryGateway: inventoryGateway);

        await service.RunSweepAsync();

        Assert.NotNull(publishedCandidate);
        Assert.Equal("2.0.0", publishedCandidate!.CurrentVersion);
        Assert.Equal("2.1.0", publishedCandidate.TargetVersion);
    }

    [Fact]
    public async Task RunSweepAsync_PrefixInventory_IgnoresHumanAuthoredAutoupdateBranchPr()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var humanUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(humanUser, "human-maintainer");
        var humanRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(humanRepo, "owner/repo");
        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/manual-fix");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "sha-human");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, humanRepo);

        var humanPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(humanPr, 707);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(humanPr, humanUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.UpdatedAt))?.SetValue(humanPr, DateTimeOffset.UtcNow.AddDays(-20));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(humanPr, Array.Empty<Label>());
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(humanPr, head);
        gitHubMock.GetPullRequestAsync("owner", "repo", 707, Arg.Any<CancellationToken>()).Returns(humanPr);

        var inventoryGateway = Substitute.For<IOpenPullRequestInventoryGateway>();
        inventoryGateway.EnsureRequiredLabelsVerifiedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        inventoryGateway.ListOpenPullRequestsByHeadPrefixAsync("owner", "repo", "autoupdate/", Arg.Any<CancellationToken>())
            .Returns(new[] { humanPr });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            BotLogin = "aqua-rium",
            FailedPrTtlDays = 7
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            inventoryGateway: inventoryGateway);

        await service.RunSweepAsync();

        await gitHubMock.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().CloseIssueAsync(default!, default!, default, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().DeleteRefAsync(default!, default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().ListWorkflowRunsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RunSweepAsync_InactiveFailedBotPrWithHumanCommit_DoesNotCloseOrDeleteBranch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var autoLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoLabel, GitHubLabels.AutoUpdate);
        var staleIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(staleIssue, 808);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(staleIssue, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-12));
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(staleIssue, new List<Label> { autoLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { staleIssue });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "sha-human-commit");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 808);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(pr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
        gitHubMock.GetPullRequestAsync("owner", "repo", 808, Arg.Any<CancellationToken>()).Returns(pr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(failedRun, "sha-human-commit");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow.AddDays(-11));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        // Branch received a human maintainer commit on GitHub Web UI -> Author is human!
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha-human-commit", "fix spec", "maintainer-alice", "alice@example.com", "web-flow", "noreply@github.com"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            BotLogin = "aqua-rium",
            FailedPrTtlDays = 7
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await gitHubMock.DidNotReceive().CloseIssueAsync("owner", "repo", 808, Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_CursorKeysAreScopedByTargetRepository_AndIsolatedOnCompleteSweepFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg-repo-cursors-{Guid.NewGuid():N}");
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var throwingCursor = Substitute.For<IUpdateCandidateSource, ICandidateSweepCursor>();
        throwingCursor.SourceName.Returns("BrokenCursor");
        throwingCursor.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UpdateCandidate>());
        ((ICandidateSweepCursor)throwingCursor).When(c => c.CompleteSweep(Arg.Any<IReadOnlySet<string>>(), Arg.Any<IReadOnlySet<string>?>()))
            .Do(_ => throw new InvalidOperationException("cursor write failure"));

        var healthyCursor = Substitute.For<IUpdateCandidateSource, ICandidateSweepCursor>();
        healthyCursor.SourceName.Returns("HealthyCursor");
        healthyCursor.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UpdateCandidate>());

        try
        {
            using var store = new JsonBotStateStore(Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir }));
            var optsRepoA = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "sandbox/openRuyi"
            });
            var optsRepoB = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi"
            });

            var healthState = new RuntimeHealthState();
            var svcA = new PackageUpdateService(
                gitHubMock,
                new[] { throwingCursor, healthyCursor },
                Array.Empty<ISpecVersionRewriter>(),
                Substitute.For<IPackageUpdatePublisher>(),
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), optsRepoA),
                Substitute.For<ISpecCatalog>(),
                optsRepoA,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: store,
                healthState: healthState);

            var svcB = new PackageUpdateService(
                gitHubMock,
                Array.Empty<IUpdateCandidateSource>(),
                Array.Empty<ISpecVersionRewriter>(),
                Substitute.For<IPackageUpdatePublisher>(),
                new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), optsRepoB),
                Substitute.For<ISpecCatalog>(),
                optsRepoB,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: store);

            await svcA.RunSweepAsync();
            await svcB.RunSweepAsync();

            // CompleteSweep exception on first source does not block subsequent cursor commits or health stats
            ((ICandidateSweepCursor)healthyCursor).Received(1).CompleteSweep(Arg.Any<IReadOnlySet<string>>(), Arg.Any<IReadOnlySet<string>?>());
            Assert.True(healthState.GetSourceHealth().ContainsKey("HealthyCursor"));

            // Repository-scoped scheduler offset keys stay isolated even when sharing one BotState store
            var keyA = PackageUpdateService.GetSchedulerOffsetStateKey("sandbox/openRuyi");
            var keyB = PackageUpdateService.GetSchedulerOffsetStateKey("openRuyi-Project/openRuyi");
            Assert.NotEqual(keyA, keyB);
            Assert.Equal("1", store.GetCursorWatermark(keyA));
            Assert.Equal("1", store.GetCursorWatermark(keyB));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_SkipsRemoteAssetBackfill_WhenBranchCommitIsHumanOrLookupFails()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var prIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(prIssue, 502);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(prIssue, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(prIssue, new List<Label> { autoLabel });
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prIssue });

        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(fullPr, botUser);
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "sha502");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("owner", "repo", 502, Arg.Any<CancellationToken>()).Returns(fullPr);

        var remoteAssetRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Id))?.SetValue(remoteAssetRun, 8888L);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Name))?.SetValue(remoteAssetRun, "RemoteAsset Verification");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(remoteAssetRun, "sha502");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(remoteAssetRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(remoteAssetRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { remoteAssetRun });

        var backfillerMock = Substitute.For<IRemoteAssetBackfiller>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        // 1. Human commit on branch -> must skip backfill
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha502", "manual commit", "human-dev", "dev@example.com", "web-flow", ""));

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            backfillerMock);

        await service.RunSweepAsync();
        await backfillerMock.DidNotReceiveWithAnyArgs().TryBackfillAsync(default!, default!, default!, default, default, default);

        // 2. Commit lookup throws -> fail-closed skip backfill
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns<Task<BranchCommitInfo?>>(_ => throw new HttpRequestException("API timeout"));
        await service.RunSweepAsync();
        await backfillerMock.DidNotReceiveWithAnyArgs().TryBackfillAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public void DryRunMode_IsInCooldownAndResetCooldown_DoNotClearPersistedFailureState()
    {
        var stateStoreMock = Substitute.For<IBotStateStore>();
        stateStoreMock.GetPackageFailure("owner", "repo", "curl")
            .Returns(new PackageFailureEntry(3, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(-31)));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "owner/repo"
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            stateStore: stateStoreMock);

        Assert.False(service.IsInCooldown("curl"));
        service.ResetCooldown("curl");
        stateStoreMock.DidNotReceiveWithAnyArgs().ClearPackageFailure(default!, default!, default!);
    }

    [Fact]
    public async Task RunSweepAsync_WhenPublisherReturnsNonSkippedFailure_IncrementsFailedAndDoesNotReportProcessed()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("rust-fail", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("rust-fail", "rust-fail", "%global crate_name fail\nVersion: 1.0.0\n", "SPECS/rust-fail/rust-fail.spec"));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), "owner/repo", "main", "SPECS/rust-fail/rust-fail.spec", Arg.Any<CancellationToken>())
            .Returns(new PublishResult(false, false, "GitHub API 502 Bad Gateway"));

        var cursorSource = new FakeCursorCandidateSource(
            CandidateOrigin.RustCrate,
            new[] { new UpdateCandidate("rust-fail", "1.0.0", "1.0.1", CandidateOrigin.RustCrate, "https://crates.io") });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            RequestThrottleMilliseconds = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new IUpdateCandidateSource[] { cursorSource },
            new ISpecVersionRewriter[] { new RustCrateSpecRewriter() },
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        Assert.NotNull(cursorSource.LastCompletedProcessedPackages);
        Assert.DoesNotContain("rust-fail", cursorSource.LastCompletedProcessedPackages!);

        // Also verify ObsBlockedUntil triggers IsInCooldown independently of CI failure counts
        var obsStoreMock = Substitute.For<IBotStateStore>();
        obsStoreMock.GetPackageFailure("owner", "repo", "curl")
            .Returns(new PackageFailureEntry(0, null, null, DateTimeOffset.UtcNow.AddDays(5)));
        var svcWithObsBlock = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            stateStore: obsStoreMock);
        Assert.True(svcWithObsBlock.IsInCooldown("curl"));
        svcWithObsBlock.ResetCooldown("curl");
        obsStoreMock.Received(1).ClearObsBlockCooldown("owner", "repo", "curl");
    }

    [Fact]
    public void IsInCooldown_UsesStateStoreAsSingleTruthSource_AndRecordFailureCountsUnderObsBlock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg-obs-truth-{Guid.NewGuid():N}");
        try
        {
            using var store = new JsonBotStateStore(Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir
            }));
            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "owner/repo",
                MaxConsecutiveFailures = 3
            });

            var service = new PackageUpdateService(
                Substitute.For<IGitHubService>(),
                Array.Empty<IUpdateCandidateSource>(),
                Array.Empty<ISpecVersionRewriter>(),
                Substitute.For<IPackageUpdatePublisher>(),
                new ObsPostMergeVerifier(Substitute.For<IGitHubService>(), Substitute.For<IObsBuildService>(), options, stateStore: store),
                Substitute.For<ISpecCatalog>(),
                options,
                Substitute.For<IRemoteAssetBackfiller>(),
                stateStore: store);

            // 1. Simulate ObsPostMergeVerifier persisting OBS block -> IsInCooldown is true
            store.SetObsBlockCooldown("owner", "repo", "curl", DateTimeOffset.UtcNow.AddDays(10));
            Assert.True(service.IsInCooldown("curl"));

            // 2. While OBS block is active, new CI failure must still increment ConsecutiveFailures (O5)
            var hitMax = service.RecordFailure("curl");
            Assert.False(hitMax);
            Assert.Equal(1, store.GetPackageFailure("owner", "repo", "curl")?.ConsecutiveFailures);

            // 3. OBS recovery clears persisted OBS block directly via store -> IsInCooldown becomes false immediately without ResetCooldown (B3)
            Assert.True(store.ClearObsBlockCooldown("owner", "repo", "curl"));
            Assert.False(service.IsInCooldown("curl"));
            Assert.Equal(1, store.GetPackageFailure("owner", "repo", "curl")?.ConsecutiveFailures);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSweepAsync_AdmitsAndPublishesCalVerTzdataUpdate_WhenAllowCrossMajorVersionFalse()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("tzdata", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("tzdata", "tzdata", "Name: tzdata\nVersion: 2026c\nRelease: 1\n", "SPECS/tzdata/tzdata.spec"));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.SourceName.Returns("Lookout");
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UpdateCandidate("tzdata", "2026c", "2026d", CandidateOrigin.Lookout, "https://lookout.openruyi.cn/pkg/tzdata",
                    PublishedAt: DateTimeOffset.UtcNow.AddDays(-5))
            });

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(
                Arg.Is<UpdateCandidate>(c => c.PackageName == "tzdata" && c.TargetVersion == "2026d"),
                Arg.Any<SpecRewriteResult>(),
                "owner/repo",
                "main",
                "SPECS/tzdata/tzdata.spec",
                Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, null, 310, "https://github.com/owner/repo/pull/310"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            AllowCrossMajorVersion = false,
            SkipPackagesWithPatches = true,
            CooldownHours = 72,
            RequestThrottleMilliseconds = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new ISpecVersionRewriter[] { new PlainSpecVersionRewriter() },
            publisherMock,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalogMock,
            options,
            Substitute.For<IRemoteAssetBackfiller>());

        await service.RunSweepAsync();

        await publisherMock.Received(1).PublishUpdateAsync(
            Arg.Is<UpdateCandidate>(c => c.PackageName == "tzdata" && c.CurrentVersion == "2026c" && c.TargetVersion == "2026d"),
            Arg.Is<SpecRewriteResult>(r => r.Success && r.HasChanged && r.UpdatedContent.Contains("Version: 2026d", StringComparison.Ordinal)),
            "owner/repo",
            "main",
            "SPECS/tzdata/tzdata.spec",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_WhenLiveIdentityUnavailable_DoesNotCloseOrDeletePrAuthoredByConfiguredBotLogin()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var humanUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(humanUser, "aqua-rium");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "headSha1");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);

        var openHumanPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(openHumanPr, 410);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(openHumanPr, "SPECS: bump curl -> 8.22.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.UpdatedAt))?.SetValue(openHumanPr, DateTimeOffset.UtcNow.AddDays(-20));
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(openHumanPr, humanUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(openHumanPr, head);

        gitHubMock.GetPullRequestAsync("owner", "repo", 410, Arg.Any<CancellationToken>()).Returns(openHumanPr);
        var failedRun = (Octokit.WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.WorkflowRun));
        typeof(Octokit.WorkflowRun).GetProperty(nameof(Octokit.WorkflowRun.HeadSha))?.SetValue(failedRun, "headSha1");
        typeof(Octokit.WorkflowRun).GetProperty(nameof(Octokit.WorkflowRun.Conclusion))?.SetValue(
            failedRun,
            new Octokit.StringEnum<Octokit.WorkflowRunConclusion>(Octokit.WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var inventoryMock = Substitute.For<IOpenPullRequestInventoryGateway>();
        inventoryMock.ListOpenPullRequestsByHeadPrefixAsync("owner", "repo", "autoupdate/", Arg.Any<CancellationToken>())
            .Returns(new[] { openHumanPr });

        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new HttpRequestException("API timeout"));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.SourceName.Returns("Lookout");
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UpdateCandidate>());

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            BotLogin = "aqua-rium",
            FailedPrTtlDays = 7,
            RequestThrottleMilliseconds = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new ISpecVersionRewriter[] { new PlainSpecVersionRewriter() },
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options, botIdentity: botIdentity),
            Substitute.For<ISpecCatalog>(),
            options,
            Substitute.For<IRemoteAssetBackfiller>(),
            inventoryGateway: inventoryMock,
            botIdentity: botIdentity);

        await service.RunSweepAsync();

        await gitHubMock.DidNotReceive().CloseIssueAsync("owner", "repo", 410, Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_HumanPrCarryingAutoUpdateLabel_SameRepoOrFork_PerformsZeroWrites()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);

        static (Issue Issue, PullRequest Pr) MakeHumanAutoUpdatePr(int number, string author, string headRepoFullName, Label label)
        {
            var user = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
            typeof(User).GetProperty(nameof(User.Login))?.SetValue(user, author);
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
            typeof(Issue).GetProperty(nameof(Issue.User))?.SetValue(issue, user);
            typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(issue, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-20));
            typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(issue, new List<Label> { label });

            var repo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repo, headRepoFullName);
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
            typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, $"sha-{number}");
            typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repo);

            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(pr, user);
            typeof(PullRequest).GetProperty(nameof(PullRequest.UpdatedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddDays(-20));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new[] { label });
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
            return (issue, pr);
        }

        var (sameRepoIssue, sameRepoPr) = MakeHumanAutoUpdatePr(901, "human-maintainer", "owner/repo", autoUpdateLabel);
        var (forkIssue, forkPr) = MakeHumanAutoUpdatePr(902, "external-contributor", "external/repo", autoUpdateLabel);

        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sameRepoIssue, forkIssue });
        gitHubMock.GetPullRequestAsync("owner", "repo", 901, Arg.Any<CancellationToken>()).Returns(sameRepoPr);
        gitHubMock.GetPullRequestAsync("owner", "repo", 902, Arg.Any<CancellationToken>()).Returns(forkPr);

        // Even if base repo's autoupdate/curl tip is a bot commit and remoteasset failed, neither human PR may touch it.
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("sha-bot-base", "bump curl", "aqua-rium", "bot@example.com", "web-flow", ""));

        var backfiller = Substitute.For<IRemoteAssetBackfiller>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            BotLogin = "aqua-rium",
            FailedPrTtlDays = 7,
            MaxConsecutiveFailures = 1,
            IssueOnGivingUp = true
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            backfiller);

        await service.RunSweepAsync();

        await gitHubMock.DidNotReceiveWithAnyArgs().ListWorkflowRunsAsync(default!, default!, default, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!, default);
        await backfiller.DidNotReceiveWithAnyArgs().TryBackfillAsync(default!, default!, default!, default, default, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().CloseIssueAsync(default!, default!, default, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().DeleteRefAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunSweepAsync_ForkPrWithBotAuthorCollidingBaseBranch_NeverTriggersBackfill()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");

        var forkRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(forkRepo, "forker/repo");
        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "sha-fork");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, forkRepo);

        var forkIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(forkIssue, 903);
        typeof(Issue).GetProperty(nameof(Issue.User))?.SetValue(forkIssue, botUser);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(forkIssue, (DateTimeOffset?)DateTimeOffset.UtcNow);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(forkIssue, new List<Label> { autoUpdateLabel });

        var forkPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(forkPr, 903);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(forkPr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(forkPr, head);

        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { forkIssue });
        gitHubMock.GetPullRequestAsync("owner", "repo", 903, Arg.Any<CancellationToken>()).Returns(forkPr);

        var backfiller = Substitute.For<IRemoteAssetBackfiller>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            BotLogin = "aqua-rium"
        });

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            Substitute.For<ISpecCatalog>(),
            options,
            backfiller);

        await service.RunSweepAsync();

        await backfiller.DidNotReceiveWithAnyArgs().TryBackfillAsync(default!, default!, default!, default, default, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().ListWorkflowRunsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RunSweepAsync_BotPrWhoseBranchTipShaMovedOrHasHumanCommit_SkipsBackfillAndClose_WhileCountingSlot()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubMock.SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        var botUser = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(botUser, "aqua-rium");
        var repo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repo, "owner/repo");

        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "pr-head-sha");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repo);

        var prIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(prIssue, 904);
        typeof(Issue).GetProperty(nameof(Issue.User))?.SetValue(prIssue, botUser);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(prIssue, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-20));
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(prIssue, new List<Label> { autoUpdateLabel });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 904);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(pr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.UpdatedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddDays(-20));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new[] { autoUpdateLabel });
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);

        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prIssue });
        gitHubMock.GetPullRequestAsync("owner", "repo", 904, Arg.Any<CancellationToken>()).Returns(pr);

        var remoteAssetFailed = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Id))?.SetValue(remoteAssetFailed, 7777L);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Name))?.SetValue(remoteAssetFailed, "RemoteAsset Verification");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.HeadSha))?.SetValue(remoteAssetFailed, "pr-head-sha");
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(remoteAssetFailed, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { remoteAssetFailed });

        // Branch tip SHA moved away from pr-head-sha (even though authored by bot), OR human commit on tip.
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new BranchCommitInfo("different-sha", "bump curl", "aqua-rium", "bot@example.com", "web-flow", ""));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.SourceName.Returns("Lookout");
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new UpdateCandidate("zlib", "1.3", "1.4", CandidateOrigin.Lookout, "https://lookout/zlib") });

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetSpecAsync("zlib", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("zlib", "zlib", "Name: zlib\nVersion: 1.3\n", "SPECS/zlib/zlib.spec"));

        var publisher = Substitute.For<IPackageUpdatePublisher>();
        var backfiller = Substitute.For<IRemoteAssetBackfiller>();

        // Keep MaxConcurrentPullRequests = 2 with 1 healthy PR (#905) alongside failed PR #904 (total = 2)
        // to verify PR #904 still occupies a concurrency slot while backfill and TTL closure stay blocked.
        var healthyIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(healthyIssue, 905);
        typeof(Issue).GetProperty(nameof(Issue.User))?.SetValue(healthyIssue, botUser);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(healthyIssue, (DateTimeOffset?)DateTimeOffset.UtcNow);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(healthyIssue, new List<Label> { autoUpdateLabel });

        var healthyHead = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(healthyHead, "autoupdate/healthy");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(healthyHead, "healthy-sha");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(healthyHead, repo);
        var healthyPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(healthyPr, 905);
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(healthyPr, botUser);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(healthyPr, healthyHead);

        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prIssue, healthyIssue });
        gitHubMock.GetPullRequestAsync("owner", "repo", 905, Arg.Any<CancellationToken>()).Returns(healthyPr);
        gitHubMock.ListWorkflowRunsAsync("owner", "repo", "autoupdate/healthy", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkflowRun>());

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            BotLogin = "aqua-rium",
            FailedPrTtlDays = 7,
            MaxConcurrentPullRequests = 2,
            CooldownHours = 0
        });

        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new ISpecVersionRewriter[] { new PlainSpecVersionRewriter() },
            publisher,
            new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options),
            specCatalog,
            options,
            backfiller);

        await service.RunSweepAsync();

        await backfiller.DidNotReceiveWithAnyArgs().TryBackfillAsync(default!, default!, default!, default, default, default);
        await gitHubMock.DidNotReceive().CloseIssueAsync("owner", "repo", 904, Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
        // PR #904 was counted in failedActiveCount alongside healthy PR #905 (total = 2 >= MaxConcurrentPullRequests 2), so zlib is NOT published.
        await publisher.DidNotReceiveWithAnyArgs().PublishUpdateAsync(default!, default!, default!, default!, default, default);
    }

    private sealed class FakeCursorCandidateSource(CandidateOrigin origin, IReadOnlyList<UpdateCandidate> items)
        : IUpdateCandidateSource, ICandidateSweepCursor
    {
        public string SourceName => origin.ToString();
        public IReadOnlySet<string>? LastCompletedProcessedPackages { get; private set; }
        public Task<IReadOnlyList<UpdateCandidate>> GetCandidatesAsync(CancellationToken ct = default) => Task.FromResult(items);
        public void CompleteSweep(IReadOnlySet<string> processedPackageNames, IReadOnlySet<string>? failedPackageNames = null)
            => LastCompletedProcessedPackages = processedPackageNames;
    }

    private sealed class StaticHttpResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StaticHttpResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}

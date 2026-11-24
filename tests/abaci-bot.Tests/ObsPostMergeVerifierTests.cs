using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class ObsPostMergeVerifierTests
{
    [Fact]
    public async Task VerifyMergedPullRequestsAsync_UpdatesExistingComment_WhenStatusChanges()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        // 1. Mock merged PR
        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        // 2. Mock OBS service returning synchronized revision and Failed status
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        // 3. Mock existing comment showing 'Building' status
        var existingComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(existingComment, 999L);
        var oldBody = """
            ### OBS Build Verification
            - **Project**: `openruyi`
            - **Package**: `curl`
            - **Repository / Arch**: `riscv64` / `riscv64`
            - **Status**: `Building` (`building`)
            - **Synchronized Commit**: `sha123`
            """;
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(existingComment, oldBody);

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(new[] { existingComment });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // Verify UpdateCommentAsync was invoked on comment 999 with Failed status
        await gitHubMock.Received(1).UpdateCommentAsync(
            "openRuyi-Project", "openRuyi", 999L, Arg.Is<string>(b => b.Contains("`Failed`")), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 42, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_SkipsComment_WhenAlreadyUpToDate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        var existingComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(existingComment, 999L);
        var matchingBody = """
            ### OBS Build Verification
            - **Project**: `openruyi`
            - **Package**: `curl`
            - **Repository / Arch**: `riscv64` / `riscv64`
            - **Status**: `Succeeded` (`succeeded`)
            - **Synchronized Commit**: `sha123`
            """;
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(existingComment, matchingBody);

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(new[] { existingComment });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        await gitHubMock.DidNotReceive().UpdateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_RemovesBlockedLabel_WhenBuildSucceeds()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");

        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.AutoUpdateBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new[] { blockedLabel });

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        await gitHubMock.Received(1).RemoveLabelAsync("openRuyi-Project", "openRuyi", 42, GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotAddBlockedLabel_WhenStatusIsBuildingOrUnknown()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        // Building status
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Building, "building"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        await gitHubMock.DidNotReceive().AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 42, Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsMergedBranch_WhenBuildSucceeded()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 50);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha456");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "headSha456");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("headSha456");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeSha456");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 50, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // Verify DeleteRefAsync was invoked for the merged autoupdate branch
        await gitHubMock.Received(1).DeleteRefAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotReclaimBranch_WhenDryRunOrFork()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        // DryRun = true
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = true,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 51);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha789");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        var forkRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(forkRepo, "external-fork/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, forkRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeSha789");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 51, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // DidNotReceive DeleteRefAsync
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotReclaimBranch_WhenBranchShaMovedForward()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 52);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeShaOriginal");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "originalMergedSha");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        // The branch has moved to a new commit!
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("newerCommitSha");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeShaOriginal");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 52, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // MUST NOT delete branch because branch head moved
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotReclaimBranch_WhenOpenPrExistsOnBranch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 53);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeShaOriginal");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "headSha456");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("headSha456");

        // A new in-flight PR is reusing this head ref!
        var inFlightPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(inFlightPr, 54);
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(inFlightPr);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeShaOriginal");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 53, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // MUST NOT delete branch because an open PR is reusing it
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsMergedBranch_WhenObsFailedAndPastTtl()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            FailedPrTtlDays = 7,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 55);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha1");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddDays(-10));

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "headSha1");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("headSha1");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeSha1");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 55, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // MUST delete branch because it aged past TTL even though OBS failed
        await gitHubMock.Received(1).DeleteRefAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_Skips_WhenTitleMismatchOrRevisionNotSynchronized()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        // PR with title not matching SPECS bump pattern
        var prNonMatching = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(prNonMatching, 60);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(prNonMatching, "docs: update readme");

        // PR where OBS service revision does NOT match merge commit sha
        var prWaitingObs = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(prWaitingObs, 61);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(prWaitingObs, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(prWaitingObs, "newSha");

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prNonMatching, prWaitingObs });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("oldShaDifferentFromNewSha");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // No build query or comment should be created
        await obsServiceMock.DidNotReceive().GetPackageBuildResultAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("invalid_repo")]
    [InlineData("")]
    public async Task VerifyMergedPullRequestsAsync_ReturnsEarly_OnInvalidRepository(string targetRepo)
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var verifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));

        await verifier.VerifyMergedPullRequestsAsync(targetRepo);
        await gitHubMock.DidNotReceive().SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_AddsBlockedLabel_OnFailedBuild()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 70);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaFail");

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaFail");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        gitHubMock.GetIssueCommentsAsync("owner", "repo", 70, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 70, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 70, Arg.Is<string>(c => c.Contains("`Failed`")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsBranch_AndRemovesBlockedLabel_OnSucceededBuild()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 80);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaSuccess");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "shaHeadPr");
        var headRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, headRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.AutoUpdateBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new List<Label> { blockedLabel });

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaSuccess");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("shaHeadPr");

        gitHubMock.GetIssueCommentsAsync("owner", "repo", 80, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 80, GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 80, Arg.Is<string>(c => c.Contains("`Succeeded`")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryReclaimMergedBranch_SkipsWhenOpenPrExists_OrHeadShaMoved()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/"
        });

        // PR 1: open PR is reusing the branch
        var pr1 = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr1, 91);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr1, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr1, "shaMerged1");

        var headRef1 = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef1, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef1, "shaHead1");
        var headRepo1 = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo1, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef1, headRepo1);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr1, headRef1);

        // PR 2: head branch SHA moved forward
        var pr2 = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr2, 92);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr2, "SPECS: bump openssl to 3.1.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr2, "shaMerged2");

        var headRef2 = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef2, "autoupdate/openssl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef2, "shaHead2_old");
        var headRepo2 = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo2, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef2, headRepo2);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr2, headRef2);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr1, pr2 });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>()).Returns("shaMerged1");
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "openssl", Arg.Any<CancellationToken>()).Returns("shaMerged2");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "repo", "arch", ObsStatusCode.Succeeded, "succeeded"));
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "openssl", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "openssl", "repo", "arch", ObsStatusCode.Succeeded, "succeeded"));

        // PR 1 has an active open PR reusing the branch
        var activeOpenPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(activeOpenPr, 999);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(activeOpenPr);

        // PR 2 has no open PR, but current branch SHA is different from merged PR head SHA
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/openssl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/openssl", Arg.Any<CancellationToken>())
            .Returns("shaHead2_newMovedAhead");

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        // Neither branch should be deleted
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/openssl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_HandlesExceptionsGracefully()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PullRequest>>(_ => throw new HttpRequestException("Network failure"));

        var verifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
    }

    [Theory]
    [InlineData("https://obs.distro.example:8443", "obs.distro.example:8443/public")]
    [InlineData("custom-obs-host", "custom-obs-host/public")]
    [InlineData("", "OBS /public")]
    public async Task VerifyMergedPullRequestsAsync_CommentReflectsConfiguredObsBaseUrl(string obsBaseUrl, string expectedEndpoint)
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions
            {
                BaseUrl = obsBaseUrl,
                Project = "distro",
                TargetRepository = "standard",
                TargetArch = "x86_64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 77);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump bash to 5.2");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha777");

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        obsServiceMock.GetPackageServiceRevisionAsync("distro", "bash", Arg.Any<CancellationToken>())
            .Returns("sha777");
        obsServiceMock.GetPackageBuildResultAsync("distro", "bash", "standard", "x86_64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("distro", "bash", "standard", "x86_64", ObsStatusCode.Succeeded, "succeeded"));
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 77, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            77,
            Arg.Is<string>(body => body.Contains($"Verified via `{expectedEndpoint}` read-model.")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_RequireObsSuccessAfterMerge_CreatesBlockedTrackingIssueIdempotently_AndClosesOnRecovery()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 90);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.22.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaObs90");

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaObs90");
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 90, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var expectedTitle = ObsPostMergeVerifier.BuildObsBlockedIssueTitle("curl");
        var createdIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(createdIssue, 950);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(createdIssue, expectedTitle);

        // Sweep 1: no existing blocked tracking issue; OBS build fails -> creates tracking issue once
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 0, false));
        gitHubMock.CreateIssueAsync(
                "owner", "repo", expectedTitle, Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(createdIssue);

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options, pagedIssueSearch: pagedSearchMock);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).AddLabelsAsync(
            "owner", "repo", 90, Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreateIssueAsync(
            "owner", "repo", expectedTitle, Arg.Any<string>(),
            Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdateBlocked) && l.Contains(GitHubLabels.StatusStaleExempt)),
            Arg.Any<CancellationToken>());

        // Sweep 2: tracking issue already open; OBS still fails -> must NOT create duplicate tracking issue
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { createdIssue }, 1, false));
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
        await gitHubMock.Received(1).CreateIssueAsync(
            "owner", "repo", expectedTitle, Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());

        // Sweep 3: OBS recovers to Succeeded -> removes Blocked label on PR & closes tracking issue
        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.AutoUpdateBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new List<Label> { blockedLabel });
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 90, GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreateCommentAsync(
            "owner", "repo", 950, Arg.Is<string>(b => b.Contains("OBS build succeeded", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 950, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DeferWhenStaleJobPreDatesMerge_AndRecoverBlockedIssueBeyondSevenDaysOrMainUpgrade()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var freshnessMock = Substitute.For<IObsBuildFreshnessGateway>();
        var specCatalogMock = Substitute.For<ISpecCatalog>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var mergedAt = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 91);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl 8.21.0 -> 8.22.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaNew91");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(pr, mergedAt);
        var headBranch = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headBranch, "autoupdate/curl");
        var headRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headBranch, headRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headBranch);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>()).Returns("shaNew91");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        // 1. Stale job (starttime < MergedAt & mismatched xsrcmd5) must NOT delete branch or close anything!
        freshnessMock.GetPackageBuildFreshnessAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildFreshness(true, "hash-new", "hash-old", mergedAt.AddHours(-2), ObsStatusCode.Succeeded));

        // Beyond 7-day open blocked issues:
        // - pkg-obs-recovered: no merged PR in 7d window, OBS now succeeds cleanly
        // - pkg-main-upgraded: main branch moved beyond recorded version 1.0.0
        var obsRecoveredIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(obsRecoveredIssue, 701);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(obsRecoveredIssue, ObsPostMergeVerifier.BuildObsBlockedIssueTitle("pkg-obs-recovered"));
        typeof(Issue).GetProperty(nameof(Issue.CreatedAt))?.SetValue(obsRecoveredIssue, mergedAt.AddDays(-14));

        var mainUpgradedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(mainUpgradedIssue, 702);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(mainUpgradedIssue, ObsPostMergeVerifier.BuildObsBlockedIssueTitle("pkg-main-upgraded"));
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(mainUpgradedIssue, "- **Package Version**: `1.0.0`\n<!-- abaci-obs-blocked: pkg-main-upgraded -->");
        typeof(Issue).GetProperty(nameof(Issue.CreatedAt))?.SetValue(mainUpgradedIssue, mergedAt.AddDays(-20));

        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { obsRecoveredIssue, mainUpgradedIssue }, 2, false));

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "pkg-obs-recovered", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "pkg-obs-recovered", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));
        freshnessMock.GetPackageBuildFreshnessAsync("openruyi", "pkg-obs-recovered", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildFreshness(true, "hash-r", "hash-r", mergedAt.AddDays(-1), ObsStatusCode.Succeeded));

        specCatalogMock.GetSpecAsync("pkg-main-upgraded", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkg-main-upgraded", "pkg-main-upgraded", "Name: pkg-main-upgraded\nVersion: 1.0.1\n", "SPECS/pkg-main-upgraded/pkg-main-upgraded.spec"));

        var verifier = new ObsPostMergeVerifier(
            gitHubMock, obsServiceMock, options,
            pagedIssueSearch: pagedSearchMock,
            freshnessGateway: freshnessMock,
            specCatalog: specCatalogMock);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        // PR #91 with stale OBS job must NOT trigger branch deletion
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());

        // Both >7-day open blocked issues must recover and close automatically
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 701, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 702, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileOpenObsBlockedIssuesAsync_LateRecovery_ReclaimsMergedBranchFromBlockedIssuePrNumber()
    {
        var mergedAt = DateTimeOffset.UtcNow.AddDays(-20);
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", string.Empty, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var obsServiceMock = Substitute.For<IObsBuildService>();
        var freshnessMock = Substitute.For<IObsBuildFreshnessGateway>();
        var specCatalogMock = Substitute.For<ISpecCatalog>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();

        static Issue MakeBlockedIssue(int number, string pkg, string version, int mergedPrNumber, DateTimeOffset createdAt)
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
            typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(issue, ObsPostMergeVerifier.BuildObsBlockedIssueTitle(pkg));
            typeof(Issue).GetProperty(nameof(Issue.CreatedAt))?.SetValue(issue, createdAt);
            typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(issue, $"""
                Package `{pkg}` failed post-merge Open Build Service verification after merging PR #{mergedPrNumber}.
                - **Package Version**: `{version}`
                <!-- abaci-obs-blocked: {pkg} -->
                """);
            return issue;
        }

        static PullRequest MakeMergedPr(int number, string branch, string sha)
        {
            var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, branch);
            typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, sha);
            typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);
            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
            return pr;
        }

        var lateObsIssue = MakeBlockedIssue(801, "pkg-late-obs", "1.0.0", 811, mergedAt);
        var mainUpgradedIssue = MakeBlockedIssue(802, "pkg-main-up", "1.0.0", 812, mergedAt);

        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { lateObsIssue, mainUpgradedIssue }, 2, false));

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "pkg-late-obs", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "pkg-late-obs", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));
        freshnessMock.GetPackageBuildFreshnessAsync("openruyi", "pkg-late-obs", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildFreshness(true, "hash-ok", "hash-ok", mergedAt.AddDays(1), ObsStatusCode.Succeeded));

        specCatalogMock.GetSpecAsync("pkg-main-up", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkg-main-up", "pkg-main-up", "Name: pkg-main-up\nVersion: 1.0.2\n", "SPECS/pkg-main-up/pkg-main-up.spec"));

        gitHubMock.GetPullRequestAsync("owner", "repo", 811, Arg.Any<CancellationToken>())
            .Returns(MakeMergedPr(811, "autoupdate/pkg-late-obs", "sha-811"));
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/pkg-late-obs", Arg.Any<CancellationToken>())
            .Returns("sha-811");

        gitHubMock.GetPullRequestAsync("owner", "repo", 812, Arg.Any<CancellationToken>())
            .Returns(MakeMergedPr(812, "autoupdate/pkg-main-up", "sha-812"));
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/pkg-main-up", Arg.Any<CancellationToken>())
            .Returns(MakeMergedPr(999, "autoupdate/pkg-main-up", "sha-new"));

        var verifier = new ObsPostMergeVerifier(
            gitHubMock, obsServiceMock, options,
            pagedIssueSearch: pagedSearchMock,
            freshnessGateway: freshnessMock,
            specCatalog: specCatalogMock);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 801, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 802, Arg.Any<CancellationToken>());
        // Late OBS recovery reclaims safe branch...
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/pkg-late-obs", Arg.Any<CancellationToken>());
        // ...while branch reused by an active open PR (#999) is protected.
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/pkg-main-up", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ObsStatusBlocked_TreatedAsTransientWithoutBlocking()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 91);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaBlocked");
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaBlocked");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Blocked, "blocked"));
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 91, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.DidNotReceive().AddLabelsAsync("owner", "repo", 91, Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_WhenBlockedIssueCreationFails_SetsFallbackCooldown_AndClearsOnRecovery()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        var stateStoreMock = Substitute.For<IBotStateStore>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            MaxConsecutiveFailures = 3,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 92);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaFail");
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 92, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 0, false));
        gitHubMock.CreateIssueAsync("owner", "repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns<Task<Issue>>(_ => throw new InvalidOperationException("Issue creation disabled"));

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaFail");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(
                new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"),
                new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        var freshnessMock = Substitute.For<IObsBuildFreshnessGateway>();
        freshnessMock.GetPackageBuildFreshnessAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildFreshness(true, "hash-recovered", "hash-recovered", DateTimeOffset.UtcNow, ObsStatusCode.Succeeded));

        var verifier = new ObsPostMergeVerifier(
            gitHubMock,
            obsServiceMock,
            options,
            pagedIssueSearch: pagedSearchMock,
            freshnessGateway: freshnessMock,
            stateStore: stateStoreMock);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
        stateStoreMock.Received(1).SetObsBlockCooldown("owner", "repo", "curl", Arg.Is<DateTimeOffset>(d => d > DateTimeOffset.UtcNow.AddDays(25)));
        stateStoreMock.DidNotReceiveWithAnyArgs().SetPackageFailure(default!, default!, default!, default, default);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
        stateStoreMock.Received(1).ClearObsBlockCooldown("owner", "repo", "curl");
        stateStoreMock.DidNotReceiveWithAnyArgs().ClearPackageFailure(default!, default!, default!);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_WhenTrackingIssueCreatedLater_ClearsFallbackCooldownImmediately()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        var stateStoreMock = Substitute.For<IBotStateStore>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64", RequireObsSuccessAfterMerge = true }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 920);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaFail");

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 920, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 0, false));

        var createdIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(createdIssue, 888);

        gitHubMock.CreateIssueAsync("owner", "repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<Issue>(new HttpRequestException("temporary search/issue failure")),
                _ => Task.FromResult(createdIssue));

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>()).Returns("shaFail");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options, pagedIssueSearch: pagedSearchMock, stateStore: stateStoreMock);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
        stateStoreMock.Received(1).SetObsBlockCooldown("owner", "repo", "curl", Arg.Any<DateTimeOffset>());

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
        stateStoreMock.Received(1).ClearObsBlockCooldown("owner", "repo", "curl");
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_IgnoresHumanAuthoredMergedPullRequestSharingTitleConvention()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64", RequireObsSuccessAfterMerge = true }
        });

        var humanAuthor = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(humanAuthor, "human-maintainer");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "humanSha");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);

        var humanPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(humanPr, 921);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(humanPr, "SPECS: bump curl to 8.22.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(humanPr, "mergeHuman");
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(humanPr, humanAuthor);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(humanPr, head);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { humanPr });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options, botIdentity: botIdentity);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await obsServiceMock.DidNotReceiveWithAnyArgs().GetPackageServiceRevisionAsync(default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().DeleteRefAsync(default!, default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsBranchPastTtl_EvenWhenObsRevisionMissingOrMismatched_AndUsesDynamicLookbackWindow()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            FailedPrTtlDays = 10,
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64" }
        });

        static PullRequest MakeOldMergedPr(int number, string pkg, string mergeSha, string headSha)
        {
            var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, $"autoupdate/{pkg}");
            typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, headSha);
            typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);
            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, $"SPECS: bump {pkg} to 2.0");
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, mergeSha);
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddDays(-11));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
            return pr;
        }

        var prMissingRev = MakeOldMergedPr(93, "pkg-missing", "mergeA", "headA");
        var prMismatchRev = MakeOldMergedPr(94, "pkg-mismatch", "mergeB", "headB");

        DateTimeOffset capturedSince = default;
        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Do<DateTimeOffset>(d => capturedSince = d), Arg.Any<CancellationToken>())
            .Returns(new[] { prMissingRev, prMismatchRev });
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/pkg-missing", Arg.Any<CancellationToken>()).Returns("headA");
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/pkg-mismatch", Arg.Any<CancellationToken>()).Returns("headB");

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "pkg-missing", Arg.Any<CancellationToken>()).Returns((string?)null);
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "pkg-mismatch", Arg.Any<CancellationToken>()).Returns("olderSha");

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        Assert.True(capturedSince <= DateTimeOffset.UtcNow.AddDays(-11), $"Expected dynamic lookback >= 12 days, saw {capturedSince}");
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/pkg-missing", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/pkg-mismatch", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "liveSha")]
    [InlineData("", "liveSha")]
    [InlineData("expectedSha", null)]
    public async Task TryReclaimMergedBranch_SkipsFailClosed_WhenHeadOrRemoteShaMissing(string? headSha, string? currentRemoteSha)
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64" }
        });

        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, headSha);
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);
        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 95);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>()).Returns("mergeSha");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>()).Returns(currentRemoteSha);
        gitHubMock.GetIssueCommentsAsync("owner", "repo", 95, Arg.Any<CancellationToken>()).Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_LargeFailedPrTtlDays_ClampsLookbackWindowAndTtlWithoutOverflow()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            FailedPrTtlDays = int.MaxValue - 1,
            GiveUpIssueCooldownDays = 30,
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64", RequireObsSuccessAfterMerge = true }
        });

        DateTimeOffset largeCapturedSince = default;
        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Do<DateTimeOffset>(d => largeCapturedSince = d), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequest>());
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 0, false));

        var largeTtlVerifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options, pagedIssueSearch: pagedSearchMock);
        await largeTtlVerifier.VerifyMergedPullRequestsAsync("owner/repo");
        var lookbackSpanDays = (DateTimeOffset.UtcNow - largeCapturedSince).TotalDays;
        Assert.InRange(lookbackSpanDays, 364.5, 365.5);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_HandlesBrokenStatus_ShaPrefix_NonShaWarningDedup_TtlReclaim_AndPerPrIsolation()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        var freshnessMock = Substitute.For<IObsBuildFreshnessGateway>();
        var stateStoreMock = Substitute.For<IBotStateStore>();

        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 0, false));

        static PullRequest MakeMergedPr(int number, string pkg, string? mergeSha, DateTimeOffset mergedAt, string headSha)
        {
            var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "owner/repo");
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, $"autoupdate/{pkg}");
            typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, headSha);
            typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(head, repoObj);
            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, $"SPECS: bump {pkg} 1.0 -> 2.0");
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, mergeSha);
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(pr, mergedAt);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
            return pr;
        }

        var normalOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            FailedPrTtlDays = 7,
            GiveUpIssueCooldownDays = 30,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var oldTime = DateTimeOffset.UtcNow.AddDays(-8);
        var prThrows = MakeMergedPr(101, "pkg-throws", "aaaa1111bbbb2222", DateTimeOffset.UtcNow.AddDays(-1), "h101");
        var prNoMergeSha = MakeMergedPr(102, "pkg-nomergesHA", null, oldTime, "h102");
        var prDirtyOld = MakeMergedPr(103, "pkg-dirty", "cccc3333dddd4444", oldTime, "h103");
        var prNonShaTag = MakeMergedPr(104, "pkg-tag", "eeee5555ffff6666", DateTimeOffset.UtcNow.AddDays(-1), "h104");
        var prBrokenPrefixSha = MakeMergedPr(105, "pkg-broken", "1234567890abcdef", DateTimeOffset.UtcNow.AddDays(-1), "h105");

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prThrows, prNoMergeSha, prDirtyOld, prNonShaTag, prBrokenPrefixSha });
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/pkg-nomergesHA", Arg.Any<CancellationToken>()).Returns("h102");
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/pkg-dirty", Arg.Any<CancellationToken>()).Returns("h103");
        gitHubMock.GetIssueCommentsAsync("owner", "repo", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<IssueComment>());

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "pkg-throws", Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new HttpRequestException("OBS 502"));
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "pkg-dirty", Arg.Any<CancellationToken>()).Returns("cccc3333dddd4444");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "pkg-dirty", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "pkg-dirty", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded", IsDirty: true));

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "pkg-tag", Arg.Any<CancellationToken>()).Returns("v1.2.3");
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "pkg-broken", Arg.Any<CancellationToken>()).Returns("1234567");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "pkg-broken", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "pkg-broken", "riscv64", "riscv64", ObsStatusCode.Broken, "broken"));

        var logs = new List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)>();
        var verifier = new ObsPostMergeVerifier(
            gitHubMock, obsServiceMock, normalOpts,
            logger: new VerifierLogger(logs),
            pagedIssueSearch: pagedSearchMock,
            freshnessGateway: freshnessMock,
            stateStore: stateStoreMock);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/pkg-nomergesHA", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/pkg-dirty", Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().AddLabelsAsync("owner", "repo", 104, Arg.Any<string[]>(), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 105, Arg.Is<string[]>(a => a.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());

        // Second sweep verifies non-SHA revision warning is deduplicated per (pkg, PR, rev) (O6)
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
        var nonShaWarnings = logs.Where(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            l.Message.Contains("pkg-tag", StringComparison.Ordinal)).ToList();
        Assert.Single(nonShaWarnings);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_EmptyOwnBotLogins_FailsClosedForAuthoredPr()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string>(null!));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BotLogin = string.Empty,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64", RequireObsSuccessAfterMerge = true }
        });

        var author = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(author, "anyone-user");
        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 922);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.22.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha922");
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(pr, author);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options, botIdentity: botIdentity);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await obsServiceMock.DidNotReceiveWithAnyArgs().GetPackageServiceRevisionAsync(default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().DeleteRefAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_LiveIdentityFailure_DoesNotFallbackToConfiguredHumanBotLogin()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new HttpRequestException("API timeout"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64", RequireObsSuccessAfterMerge = true }
        });

        var humanAuthor = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(humanAuthor, "aqua-rium");
        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 923);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.22.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha923");
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(pr, humanAuthor);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options, botIdentity: botIdentity);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await obsServiceMock.DidNotReceiveWithAnyArgs().GetPackageServiceRevisionAsync(default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().DeleteRefAsync(default!, default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_WhenTrackingIssueCloseFails_PreservesObsBlockCooldown()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();
        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        var stateStoreMock = Substitute.For<IBotStateStore>();
        var freshnessMock = Substitute.For<IObsBuildFreshnessGateway>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            Obs = new ObsOptions { Project = "openruyi", TargetRepository = "riscv64", TargetArch = "riscv64", RequireObsSuccessAfterMerge = true }
        });

        static PullRequest MakePr(int number, string pkg, string sha)
        {
            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, $"SPECS: bump {pkg} to 1.0");
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, sha);
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddHours(-2));
            return pr;
        }

        var curlPr = MakePr(924, "curl", "shaCurl1");
        var zlibPr = MakePr(925, "zlib", "shaZlib1");
        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { curlPr, zlibPr });

        var openBlockedCurl = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(openBlockedCurl, 701);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(openBlockedCurl, "[Auto-Update: Blocked] Post-merge OBS build failure on package curl");

        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { openBlockedCurl }, 1, false));
        gitHubMock.CloseIssueAsync("owner", "repo", 701, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("502 Bad Gateway"));

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>()).Returns("shaCurl1");
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "zlib", Arg.Any<CancellationToken>()).Returns("shaZlib1");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "zlib", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "zlib", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        freshnessMock.GetPackageBuildFreshnessAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildFreshness(true, "hashC", "hashC", DateTimeOffset.UtcNow, ObsStatusCode.Succeeded));
        freshnessMock.GetPackageBuildFreshnessAsync("openruyi", "zlib", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildFreshness(true, "hashZ", "hashZ", DateTimeOffset.UtcNow, ObsStatusCode.Succeeded));

        var verifier = new ObsPostMergeVerifier(
            gitHubMock, obsServiceMock, options,
            pagedIssueSearch: pagedSearchMock,
            freshnessGateway: freshnessMock,
            stateStore: stateStoreMock);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        stateStoreMock.DidNotReceive().ClearObsBlockCooldown("owner", "repo", "curl");
        stateStoreMock.Received(1).ClearObsBlockCooldown("owner", "repo", "zlib");
    }

    private sealed class VerifierLogger(List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> entries)
        : Microsoft.Extensions.Logging.ILogger<ObsPostMergeVerifier>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add((logLevel, formatter(state, exception)));
    }
}

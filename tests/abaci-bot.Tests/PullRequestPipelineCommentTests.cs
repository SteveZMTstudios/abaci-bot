using System.Net;
using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class PullRequestPipelineCommentTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly LabelMutexEngine _mutexEngine = new();

    private PullRequestContext CreateContext(string headSha = "head123")
    {
        var pr = new PullRequest();
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, Array.Empty<Label>());
        _gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 77,
            headSha: headSha,
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Test PR",
            body: "PR body",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "opened",
            existingLabels: Array.Empty<string>(),
            prAction: PullRequestAction.Opened);
    }

    private class TestCommentModule : IPullRequestModule
    {
        private readonly string _comment;

        public TestCommentModule(string comment, int priority)
        {
            _comment = comment;
            Priority = priority;
            ModuleName = $"TestModule_{priority}";
        }

        public string ModuleName { get; }
        public int Priority { get; }
        public bool ShouldProcess(PullRequestContext context) => true;

        public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
        {
            context.AddPendingComment(_comment);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConsolidatesMultipleModuleComments_IntoSingleGitHubComment()
    {
        var module1 = new TestCommentModule("#### Module 1 Warning: Check SPDX", 10);
        var module2 = new TestCommentModule("#### Module 2 Notice: CVE detected", 20);

        var pipeline = new PullRequestPipeline(new[] { module1, module2 }, _mutexEngine, _gitHub);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync(
            "openRuyi-Project",
            "openRuyi",
            77,
            Arg.Is<string>(c =>
                c.Contains("abaci-bot Review Notes") &&
                c.Contains("Module 1 Warning") &&
                c.Contains("Module 2 Notice")),
            Arg.Any<CancellationToken>());

        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingComments_DoesNotCallCreateComment()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NonRetriableCommentApiThrows_DoesNotCrashPipeline()
    {
        var module = new TestCommentModule("Some warning", 10);
        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        var forbiddenResp = Substitute.For<IResponse>();
        forbiddenResp.StatusCode.Returns(HttpStatusCode.Forbidden);
        _gitHub.CreateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ForbiddenException(forbiddenResp));

        // Non-retriable comment failure records health and completes without crashing pipeline
        await pipeline.ExecuteAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_EditedEventWithNoLabelDiffAndNoStateTransition_PerformsZeroWrites()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                var ctx = callInfo.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.Rebuild1001To2500);
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
            });

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);

        var pr = new PullRequest();
        var label1 = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(label1, GitHubLabels.Rebuild1001To2500);
        var label2 = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(label2, GitHubLabels.WorkflowBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new[] { label1, label2 });

        _gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        var context = new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 77,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Test PR edited title",
            body: "PR body",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "edited",
            existingLabels: new[] { GitHubLabels.Rebuild1001To2500, GitHubLabels.WorkflowBlocked },
            prAction: PullRequestAction.Edited);

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.DidNotReceive().AddLabelsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().RemoveLabelAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithTargetMilestone_CallsSetIssueMilestoneAsync()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                var ctx = callInfo.Arg<PullRequestContext>();
                ctx.TargetMilestone = 42;
            });

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.Received(1).SetIssueMilestoneAsync("openRuyi-Project", "openRuyi", 77, 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TargetMilestoneThrowsException_CatchesGracefullyWithoutRollingBack()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                var ctx = callInfo.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowComplete);
                ctx.TargetMilestone = 42;
            });

        _gitHub.SetIssueMilestoneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Milestone API failure"));

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        var ex = await Record.ExceptionAsync(() => pipeline.ExecuteAsync(context, CancellationToken.None));
        Assert.Null(ex);
        await _gitHub.Received(1).AddLabelsAsync(
            "openRuyi-Project",
            "openRuyi",
            77,
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.WorkflowComplete)),
            Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().RemoveLabelAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), GitHubLabels.WorkflowComplete, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TargetMilestoneCancelled_RethrowsOperationCanceledException()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                var ctx = callInfo.Arg<PullRequestContext>();
                ctx.TargetMilestone = 42;
            });

        _gitHub.SetIssueMilestoneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NonGateCommentCreateFailsOnceThenSucceeds_RetriesInline()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
                ctx.AddPendingComment("> [!TIP]\n> SPDX notice", includeGeneralDocs: true);
            });

        var commentAttempts = 0;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                commentAttempts++;
                if (commentAttempts == 1)
                {
                    return Task.FromException(new HttpRequestException("transient 502", null, HttpStatusCode.BadGateway));
                }
                return Task.CompletedTask;
            });

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub, commentRetryDelayAsync: (_, _) => Task.CompletedTask);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, commentAttempts);
        await _gitHub.DidNotReceive().RemoveLabelAsync(
            "openRuyi-Project", "openRuyi", 77, GitHubLabels.WorkflowBlocked, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RebuildGateCommentTransient503_RetainsWorkflowBlocked_SingleAttempt_RecordsHealthAndRethrows()
    {
        var health = new RuntimeHealthState();
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
                ctx.AddPendingComment($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}", includeGeneralDocs: true);
            });

        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ =>
                Task.FromException(new HttpRequestException("persistent 503", null, HttpStatusCode.ServiceUnavailable)));

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub, runtimeHealth: health);
        var context = CreateContext();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(context, CancellationToken.None));
        Assert.IsType<HttpRequestException>(ex.InnerException);

        // Rebuild-gate comment relies on WebhookDispatcher retries rather than nested 3x3 inline retries
        await _gitHub.Received(1).CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().RemoveLabelAsync(
            "openRuyi-Project", "openRuyi", 77, GitHubLabels.WorkflowBlocked, Arg.Any<CancellationToken>());
        Assert.Contains("PullRequestPipeline", health.GetLastError().LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ExecuteAsync_RebuildGateCommentNonRetriable4xx_RetainsWorkflowBlocked_RecordsHealthWithoutThrowing(HttpStatusCode statusCode)
    {
        var health = new RuntimeHealthState();
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
                ctx.AddPendingComment($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}", includeGeneralDocs: true);
            });

        var resp = Substitute.For<IResponse>();
        resp.StatusCode.Returns(statusCode);
        Exception apiEx = statusCode switch
        {
            HttpStatusCode.Forbidden => new ForbiddenException(resp),
            HttpStatusCode.NotFound => new NotFoundException(resp),
            _ => new ApiException("Unprocessable Entity", statusCode)
        };

        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(apiEx));

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub, runtimeHealth: health);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().RemoveLabelAsync(
            "openRuyi-Project", "openRuyi", 77, GitHubLabels.WorkflowBlocked, Arg.Any<CancellationToken>());
        Assert.NotNull(health.GetLastError().LastError);
    }

    [Fact]
    public async Task ExecuteAsync_NonGateCommentFailsWithNewlyAddedWorkflowBlocked_RetainsBlockCapsAtTwoAttemptsAndSurfacesTransientError()
    {
        var health = new RuntimeHealthState();
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
                ctx.AddPendingComment("> [!TIP]\n> SPDX notice without rebuild marker", includeGeneralDocs: true);
            });

        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ =>
                Task.FromException(new HttpRequestException("persistent 503", null, HttpStatusCode.ServiceUnavailable)));

        var pipeline = new PullRequestPipeline(
            new[] { module }, _mutexEngine, _gitHub, commentRetryDelayAsync: (_, _) => Task.CompletedTask, runtimeHealth: health);
        var context = CreateContext();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(context, CancellationToken.None));
        Assert.IsType<HttpRequestException>(ex.InnerException);

        await _gitHub.Received(2).CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().RemoveLabelAsync(
            "openRuyi-Project", "openRuyi", 77, GitHubLabels.WorkflowBlocked, Arg.Any<CancellationToken>());
        Assert.NotNull(health.GetLastError().LastError);
    }

    [Fact]
    public async Task ExecuteAsync_ConsolidatedSpdxAndRebuildGateComment_PreservesSpdxSectionAcrossRetry()
    {
        var spdxModule = Substitute.For<IPullRequestModule>();
        spdxModule.Priority.Returns(60);
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        spdxModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.ComplianceLicenseIssue);
                if (!ctx.ExistingLabels.Contains(GitHubLabels.ComplianceLicenseIssue))
                {
                    ctx.AddPendingComment("> [!TIP]\n> **SPDX Notice**\n> Invalid license expression", includeGeneralDocs: true);
                }
            });

        var rebuildModule = Substitute.For<IPullRequestModule>();
        rebuildModule.Priority.Returns(80);
        rebuildModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        rebuildModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
                ctx.AddPendingComment($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}", includeGeneralDocs: true);
            });

        var postedBodies = new List<string>();
        var commentCall = 0;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                commentCall++;
                postedBodies.Add(ci.ArgAt<string>(3));
                if (commentCall == 1)
                {
                    return Task.FromException(new HttpRequestException("502 Bad Gateway", null, HttpStatusCode.BadGateway));
                }

                return Task.CompletedTask;
            });

        var pipeline = new PullRequestPipeline(
            new[] { spdxModule, rebuildModule },
            _mutexEngine,
            _gitHub,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask);

        // Attempt 1: labels succeed, consolidated SPDX + RebuildGate comment hits 502 -> propagates for dispatcher retry
        var contextAttempt1 = CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(contextAttempt1, CancellationToken.None));

        // Attempt 2: remote PR already carries Compliance: License Issue so spdxModule skips emitting SPDX comment
        var refreshedPrAfterAttempt1 = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(refreshedPrAfterAttempt1, new StringEnum<ItemState>(ItemState.Open));
        var spdxLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(spdxLabel, GitHubLabels.ComplianceLicenseIssue);
        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(refreshedPrAfterAttempt1, new[] { spdxLabel, blockedLabel });
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PullRequest?>(refreshedPrAfterAttempt1));

        var contextAttempt2 = CreateContext();
        await pipeline.ExecuteAsync(contextAttempt2, CancellationToken.None);

        Assert.Equal(2, postedBodies.Count);
        Assert.Contains("Invalid license expression", postedBodies[1], StringComparison.Ordinal);
        Assert.Contains(GovernanceCommentFormatter.RebuildGateCommentMarker, postedBodies[1], StringComparison.Ordinal);
        // Rebuild-gate marker must appear exactly once
        Assert.Equal(1, postedBodies[1].Split(GovernanceCommentFormatter.RebuildGateCommentMarker, StringSplitOptions.None).Length - 1);

        // Attempt 3: carryover cleared after Attempt 2 succeeded, so unrelated subsequent run without new comments posts nothing
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(false);
        rebuildModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(false);
        await pipeline.ExecuteAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(2, postedBodies.Count);
    }

    [Fact]
    public async Task ExecuteAsync_CarryoverPinnedToHeadSha_EvictsStaleCommentsWhenCommitAdvances()
    {
        var spdxModule = Substitute.For<IPullRequestModule>();
        spdxModule.Priority.Returns(60);
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        spdxModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                if (string.Equals(ctx.HeadSha, "sha-old", StringComparison.Ordinal))
                {
                    ctx.AddPendingComment("> [!TIP]\n> Old commit SPDX error", includeGeneralDocs: true);
                }
            });

        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new HttpRequestException("502", null, HttpStatusCode.BadGateway)));

        var pipeline = new PullRequestPipeline(
            new[] { spdxModule },
            _mutexEngine,
            _gitHub,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask);

        // Head commit A fails to post SPDX comment -> stored in carryover keyed by sha-old
        await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(CreateContext("sha-old"), CancellationToken.None));

        // Now head advances to sha-new where SPDX issue is fixed; old comment must be evicted and never posted on sha-new
        _gitHub.ClearReceivedCalls();
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await pipeline.ExecuteAsync(CreateContext("sha-new"), CancellationToken.None);

        await _gitHub.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NonGateSpdxOnlyTransientFailure_PropagatesForDispatcherRetryAndPreservesComment()
    {
        var spdxModule = Substitute.For<IPullRequestModule>();
        spdxModule.Priority.Returns(60);
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        spdxModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.ComplianceLicenseIssue);
                if (!ctx.ExistingLabels.Contains(GitHubLabels.ComplianceLicenseIssue))
                {
                    ctx.AddPendingComment("> [!TIP]\n> Pure SPDX-only report", includeGeneralDocs: true);
                }
            });

        var postedBodies = new List<string>();
        var calls = 0;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                calls++;
                postedBodies.Add(ci.ArgAt<string>(3));
                return calls <= 2
                    ? Task.FromException(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable))
                    : Task.CompletedTask;
            });

        var pipeline = new PullRequestPipeline(
            new[] { spdxModule },
            _mutexEngine,
            _gitHub,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask);

        // Attempt 1 exhausts 2 inline tries and throws CommentPostingTransientException for WebhookDispatcher retry
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(CreateContext("sha-1"), CancellationToken.None));
        Assert.IsType<HttpRequestException>(thrown.InnerException);
        Assert.Equal(2, calls);

        // Attempt 2 (dispatcher retry on same headSha after label already applied) posts preserved SPDX comment
        var prWithLabel = new PullRequest();
        var spdxLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(spdxLabel, GitHubLabels.ComplianceLicenseIssue);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(prWithLabel, new[] { spdxLabel });
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns(prWithLabel);

        await pipeline.ExecuteAsync(CreateContext("sha-1"), CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Contains("Pure SPDX-only report", postedBodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGateCommentCreatedBeforeTimeout_SkipsCarriedOverGateOnRetryWhenConfirmedOnRemote()
    {
        var confirmedOnRemote = false;
        var rebuildModule = Substitute.For<IPullRequestModule>();
        rebuildModule.Priority.Returns(80);
        rebuildModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        rebuildModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);
                if (confirmedOnRemote)
                {
                    ctx.Items[abaci_bot.Modules.RebuildImpactModule.RebuildGateCommentConfirmedOnRemoteItemKey] = true;
                    return;
                }

                ctx.AddPendingComment($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}", includeGeneralDocs: true);
            });

        var createCalls = 0;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                createCalls++;
                // Simulate comment created remotely on Attempt 1, followed by transient HTTP response timeout.
                return Task.FromException(new HttpRequestException("Gateway timeout after commit", null, HttpStatusCode.GatewayTimeout));
            });

        var pipeline = new PullRequestPipeline(
            new[] { rebuildModule },
            _mutexEngine,
            _gitHub,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask);

        // Attempt 1 throws transient error and saves gate comment into carryover.
        await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(CreateContext("sha-timeout"), CancellationToken.None));
        Assert.Equal(1, createCalls);

        // Attempt 2 sees the gate comment already live on GitHub (RebuildGateCommentConfirmedOnRemoteItemKey = true).
        confirmedOnRemote = true;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await pipeline.ExecuteAsync(CreateContext("sha-timeout"), CancellationToken.None);

        // Carried-over gate comment must be suppressed and cleared without reposting.
        Assert.Equal(1, createCalls);

        // Subsequent run without confirmation must also remain silent because carryover was cleared.
        confirmedOnRemote = false;
        rebuildModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(false);
        await pipeline.ExecuteAsync(CreateContext("sha-timeout"), CancellationToken.None);
        Assert.Equal(1, createCalls);
    }

    [Fact]
    public async Task ExecuteAsync_UsesLiveBotIdentityDisplayName_InConsolidatedCommentHeader()
    {
        var spdxModule = Substitute.For<IPullRequestModule>();
        spdxModule.Priority.Returns(60);
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        spdxModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<PullRequestContext>().AddPendingComment("> [!TIP]\n> Fix SPDX tag", includeGeneralDocs: true));

        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot-test[bot]");

        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new Options.BotStateOptions
            {
                GeneralDocumentationLinks = new List<Options.DocumentationLink>
                {
                    new() { Title = "Review Docs", Url = "https://docs.openruyi.org/review" }
                }
            }));

        string? capturedBody = null;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedBody = ci.ArgAt<string>(3);
                return Task.CompletedTask;
            });

        var pipeline = new PullRequestPipeline(
            new[] { spdxModule },
            _mutexEngine,
            _gitHub,
            formatter: formatter,
            botIdentity: botIdentity);

        await pipeline.ExecuteAsync(CreateContext("sha-dyn-name"), CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.StartsWith("Thank you for your contribution! We have identified a few points that require your attention:", capturedBody, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot-test Review Notes", capturedBody, StringComparison.Ordinal);
        Assert.Contains("[Review Docs](https://docs.openruyi.org/review)", capturedBody, StringComparison.Ordinal);
        Assert.True(
            capturedBody.IndexOf("Fix SPDX tag", StringComparison.Ordinal) <
            capturedBody.IndexOf("For more details, see:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_FindingsWithoutIncludeGeneralDocsOrModuleLinks_OmitsReferenceLinksFooter()
    {
        var cveModule = Substitute.For<IPullRequestModule>();
        cveModule.Priority.Returns(70);
        cveModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        cveModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<PullRequestContext>().AddPendingComment("> [!NOTE]\n> CVE-2025-0001 summary", includeGeneralDocs: false));

        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new Options.BotStateOptions
            {
                GeneralDocumentationLinks = new List<Options.DocumentationLink>
                {
                    new() { Title = "Review Docs", Url = "https://docs.openruyi.org/review" }
                }
            }));

        string? capturedBody = null;
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedBody = ci.ArgAt<string>(3);
                return Task.CompletedTask;
            });

        var pipeline = new PullRequestPipeline(new[] { cveModule }, _mutexEngine, _gitHub, formatter: formatter);
        await pipeline.ExecuteAsync(CreateContext("sha-no-docs"), CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.Contains("CVE-2025-0001 summary", capturedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("For more details, see:", capturedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("https://docs.openruyi.org/review", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_CarriedOverComments_Survive130MinutesWithRefreshFailureAndExpireAfter3Hours()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new Options.BotStateOptions
            {
                GeneralDocumentationLinks = new List<Options.DocumentationLink>
                {
                    new() { Title = "SPDX Guide", Url = "https://docs.openruyi.org/spdx" }
                }
            }));

        var spdxModule = Substitute.For<IPullRequestModule>();
        spdxModule.Priority.Returns(60);
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        spdxModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.ComplianceLicenseIssue);
                if (!ctx.ExistingLabels.Contains(GitHubLabels.ComplianceLicenseIssue))
                {
                    ctx.AddPendingComment("> [!TIP]\n> Preserved 130m SPDX finding", includeGeneralDocs: true);
                }
            });

        var failComments = true;
        var postedBodies = new List<string>();
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (failComments)
                {
                    return Task.FromException(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests));
                }

                postedBodies.Add(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var openPr = new PullRequest();
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());

        var prWithLabel = new PullRequest();
        var spdxLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(spdxLabel, GitHubLabels.ComplianceLicenseIssue);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(prWithLabel, new[] { spdxLabel });

        var pipeline = new PullRequestPipeline(
            new[] { spdxModule },
            _mutexEngine,
            _gitHub,
            formatter: formatter,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask,
            timeProvider: timeProvider);

        static PullRequestContext BuildRawContext(IGitHubService gh, string headSha) => new(
            gitHubService: gh,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 77,
            headSha: headSha,
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Test PR",
            body: "PR body",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "opened",
            existingLabels: Array.Empty<string>(),
            prAction: PullRequestAction.Opened);

        // T0: Attempt 1 fails transiently on comments after label applied -> saves carryover (3h TTL)
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns(openPr);
        var thrown1 = await Assert.ThrowsAnyAsync<Exception>(
            () => pipeline.ExecuteAsync(BuildRawContext(_gitHub, "sha-ttl-130"), CancellationToken.None));
        Assert.Equal("CommentPostingTransientException", thrown1.GetType().Name);

        // T0 + 65m: Attempt 2 fails transiently during GetPullRequestAsync refresh (before FlushPendingComments)
        timeProvider.Advance(TimeSpan.FromMinutes(65));
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns<PullRequest?>(_ => throw new HttpRequestException("65m refresh timeout", null, HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => pipeline.ExecuteAsync(BuildRawContext(_gitHub, "sha-ttl-130"), CancellationToken.None));

        // T0 + 130m (> 2h old TTL, < 3h new TTL): Attempt 3 sees label on PR (module adds nothing),
        // so ONLY carried-over IncludeGeneralDocs section + reference footer are delivered.
        timeProvider.Advance(TimeSpan.FromMinutes(65));
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns(prWithLabel);
        failComments = false;

        await pipeline.ExecuteAsync(BuildRawContext(_gitHub, "sha-ttl-130"), CancellationToken.None);

        var delivered = Assert.Single(postedBodies);
        Assert.Contains("Preserved 130m SPDX finding", delivered, StringComparison.Ordinal);
        Assert.Contains("[SPDX Guide](https://docs.openruyi.org/spdx)", delivered, StringComparison.Ordinal);
        Assert.True(
            delivered.IndexOf("Preserved 130m SPDX finding", StringComparison.Ordinal) <
            delivered.IndexOf("For more details, see:", StringComparison.Ordinal));

        // T0 -> fail -> T0 + 181m (> 3h TTL) -> expired carryover produces 0 CreateCommentAsync writes
        postedBodies.Clear();
        failComments = true;
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns(openPr);
        await Assert.ThrowsAnyAsync<Exception>(
            () => pipeline.ExecuteAsync(BuildRawContext(_gitHub, "sha-ttl-expired"), CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromMinutes(181));
        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<CancellationToken>())
            .Returns(prWithLabel);
        failComments = false;

        await pipeline.ExecuteAsync(BuildRawContext(_gitHub, "sha-ttl-expired"), CancellationToken.None);
        Assert.Empty(postedBodies);
    }

    [Fact]
    public async Task ExecuteAsync_PendingNoticeErrors_HandleNonRetriableAndTransientFailures()
    {
        var noticeModule = Substitute.For<IPullRequestModule>();
        noticeModule.Priority.Returns(22);
        noticeModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        noticeModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<PullRequestContext>().PendingNotices.Add(
                new NoticeIntent(GovernanceCommentFormatter.WipDraftTipMarker, "> [!TIP]\n> Notice body")));

        var health = new RuntimeHealthState();
        var pipeline = new PullRequestPipeline(new[] { noticeModule }, _mutexEngine, _gitHub, runtimeHealth: health);

        var forbiddenResp = Substitute.For<IResponse>();
        forbiddenResp.StatusCode.Returns(HttpStatusCode.Forbidden);
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ForbiddenException(forbiddenResp));

        // Non-retriable notice error is recorded without throwing or reverting labels
        await pipeline.ExecuteAsync(CreateContext("sha-notice-403"), CancellationToken.None);
        Assert.Contains("Non-retriable notice failure", health.GetLastError().LastError);

        // Transient notice error wraps into CommentPostingTransientException for dispatcher retry
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable));

        var thrownNotice = await Assert.ThrowsAnyAsync<Exception>(
            () => pipeline.ExecuteAsync(CreateContext("sha-notice-503"), CancellationToken.None));
        Assert.Equal("CommentPostingTransientException", thrownNotice.GetType().Name);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset initialUtcNow) => _utcNow = initialUtcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}

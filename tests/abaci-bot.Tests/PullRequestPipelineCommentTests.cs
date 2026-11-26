using System.Net;
using abaci_bot.Contexts;
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
            context.PendingComments.Add(_comment);
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
                ctx.PendingComments.Add("> [!TIP]\n> SPDX notice");
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
                ctx.PendingComments.Add($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}");
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
                ctx.PendingComments.Add($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}");
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
                ctx.PendingComments.Add("> [!TIP]\n> SPDX notice without rebuild marker");
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
                    ctx.PendingComments.Add("> [!TIP]\n> **SPDX Notice**\n> Invalid license expression");
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
                ctx.PendingComments.Add($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}");
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
                    ctx.PendingComments.Add("> [!TIP]\n> Old commit SPDX error");
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
                    ctx.PendingComments.Add("> [!TIP]\n> Pure SPDX-only report");
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

                ctx.PendingComments.Add($"> [!IMPORTANT]\n> Hold\n{GovernanceCommentFormatter.RebuildGateCommentMarker}");
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
            .Do(ci => ci.Arg<PullRequestContext>().PendingComments.Add("> [!TIP]\n> Fix SPDX tag"));

        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot-test[bot]");

        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new Options.RebuildImpactOptions
            {
                VerificationGuideLinks = new List<Options.VerificationReferenceLink>
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
    }
}

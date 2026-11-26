using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;
using Xunit;

namespace abaci_bot.Tests;

public class WebhookDispatcherTests
{
    [Fact]
    public async Task TryEnqueue_ReturnsBeforeWorkCompletes_AndProcessesConcurrently()
    {
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            capacity: 16,
            maxConcurrency: 2,
            maxAttempts: 1);

        await dispatcher.StartAsync(CancellationToken.None);

        using var bothEntered = new CountdownEvent(2);
        var releaseWorkers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(dispatcher.TryEnqueue("del-1", "pull_request:opened", async _ =>
        {
            bothEntered.Signal();
            await releaseWorkers.Task;
        }));

        Assert.True(dispatcher.TryEnqueue("del-2", "pull_request:synchronize", async _ =>
        {
            bothEntered.Signal();
            await releaseWorkers.Task;
        }));

        // Both enqueues returned without waiting for releaseWorkers; now both concurrent workers must enter barrier
        Assert.True(bothEntered.Wait(TimeSpan.FromSeconds(5)), "Expected 2 concurrent dispatcher workers to enter simultaneously");
        Assert.False(releaseWorkers.Task.IsCompleted);

        releaseWorkers.SetResult();
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteWorkItemWithRetryAsync_TransientFailure_RetriesUpToMaxAttemptsAndSucceeds()
    {
        var recordedDelays = new List<TimeSpan>();
        var health = new RuntimeHealthState();
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: health,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 3,
            retryDelaySelector: attempt => TimeSpan.FromMilliseconds(attempt * 5),
            delayAsync: (delay, _) =>
            {
                recordedDelays.Add(delay);
                return Task.CompletedTask;
            });

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var permanentFailureCalled = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(dispatcher.TryEnqueue(
            "del-transient",
            "pull_request:opened",
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new HttpRequestException("upstream 502", null, HttpStatusCode.BadGateway);
                }

                completed.TrySetResult();
                return ValueTask.CompletedTask;
            },
            onPermanentFailure: () => permanentFailureCalled = true));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10) }, recordedDelays);
        Assert.False(permanentFailureCalled);
        Assert.Null(health.GetLastError().LastError);
    }

    [Fact]
    public async Task ExecuteWorkItemWithRetryAsync_SecondaryRateLimitAnd429_RetriesBeforeSucceeding()
    {
        var recordedDelays = new List<TimeSpan>();
        var health = new RuntimeHealthState();
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: health,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 3,
            retryDelaySelector: attempt => TimeSpan.FromMilliseconds(attempt * 5),
            delayAsync: (delay, _) =>
            {
                recordedDelays.Add(delay);
                return Task.CompletedTask;
            });

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var permanentFailureCalled = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(dispatcher.TryEnqueue(
            "del-rate-limit",
            "pull_request:opened",
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw (SecondaryRateLimitExceededException)RuntimeHelpers.GetUninitializedObject(
                        typeof(SecondaryRateLimitExceededException));
                }

                if (attempts == 2)
                {
                    var resp = Substitute.For<IResponse>();
                    resp.StatusCode.Returns(HttpStatusCode.TooManyRequests);
                    throw new ApiException(resp);
                }

                completed.TrySetResult();
                return ValueTask.CompletedTask;
            },
            onPermanentFailure: () => permanentFailureCalled = true));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60) }, recordedDelays);
        Assert.False(permanentFailureCalled);
        Assert.Null(health.GetLastError().LastError);
    }

    [Fact]
    public async Task ExecuteWorkItemWithRetryAsync_NonRetriableFailure_DoesNotRetry_ReleasesClaim_AndRecordsHealth()
    {
        var delayCalls = 0;
        var health = new RuntimeHealthState();
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: health,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 3,
            retryDelaySelector: _ => TimeSpan.FromMilliseconds(10),
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var releasedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(dispatcher.TryEnqueue(
            "del-bad",
            "pull_request:opened",
            _ =>
            {
                attempts++;
                throw new ArgumentException("Malformed webhook state");
            },
            onPermanentFailure: () => releasedTcs.TrySetResult()));

        await releasedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(0, delayCalls);
        var (lastError, _) = health.GetLastError();
        Assert.NotNull(lastError);
        Assert.Contains("WebhookDispatcher", lastError, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", lastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteWorkItemWithRetryAsync_PipelineNonRetriableRefreshFailure_DoesNotRetryAndReleasesClaim()
    {
        var delayCalls = 0;
        var dispatcherHealth = new RuntimeHealthState();
        var pipelineHealth = new RuntimeHealthState();
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: dispatcherHealth,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 3,
            retryDelaySelector: _ => TimeSpan.FromMilliseconds(10),
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.Forbidden);
        response.Headers.Returns(new Dictionary<string, string>());
        var forbiddenEx = new ForbiddenException(response);
        gitHub.GetPullRequestAsync("owner", "repo", 88, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Octokit.PullRequest?>(forbiddenEx));

        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        var pipeline = new PullRequestPipeline(
            new[] { module },
            new LabelMutexEngine(),
            gitHub,
            runtimeHealth: pipelineHealth);

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var releasedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(dispatcher.TryEnqueue(
            "del-pipeline-403",
            "pull_request:opened",
            async ct =>
            {
                attempts++;
                var context = new PullRequestContext(
                    gitHub,
                    "captains",
                    headers: null,
                    PullRequestEventType.PullRequest,
                    "owner",
                    "repo",
                    prNumber: 88,
                    headSha: "head-sha-88",
                    baseBranch: "main",
                    isDraft: false,
                    isMerged: false,
                    title: "Forbidden PR",
                    body: null,
                    senderLogin: "alice",
                    authorLogin: "alice",
                    action: "opened",
                    existingLabels: Array.Empty<string>(),
                    cancellationToken: ct);
                await pipeline.ExecuteAsync(context, ct);
            },
            onPermanentFailure: () => releasedTcs.TrySetResult()));

        await releasedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(0, delayCalls);
        Assert.Contains(nameof(RefreshOutcome.NonRetriableFailure), pipelineHealth.GetLastError().LastError);
        Assert.Contains(nameof(NonRetriablePipelineException), dispatcherHealth.GetLastError().LastError);
    }

    [Fact]
    public async Task StopAsync_ExhaustedTransientRetries_DrainsQueueAndReleasesClaim()
    {
        var health = new RuntimeHealthState();
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: health,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 3,
            retryDelaySelector: _ => TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask);

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var released = false;

        Assert.True(dispatcher.TryEnqueue(
            "del-exhaust",
            "workflow_run:completed",
            _ =>
            {
                attempts++;
                throw new HttpRequestException("service unavailable", null, HttpStatusCode.ServiceUnavailable);
            },
            onPermanentFailure: () => released = true));

        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.True(released);
        Assert.NotNull(health.GetLastError().LastError);
    }

    [Fact]
    public async Task TryEnqueue_WhenQueueFull_ReturnsFalseImmediatelyWithoutBlocking()
    {
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            capacity: 1,
            maxConcurrency: 1,
            maxAttempts: 1);

        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.StartAsync(CancellationToken.None);

        // Item 1 occupies the single worker
        Assert.True(dispatcher.TryEnqueue("del-1", "pull_request:opened", async _ =>
        {
            workerEntered.TrySetResult();
            await releaseWorker.Task;
        }));
        await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Item 2 fills the single-slot bounded buffer
        Assert.True(dispatcher.TryEnqueue("del-2", "pull_request:opened", _ => ValueTask.CompletedTask));

        // Item 3 must fail fast immediately without blocking
        Assert.False(dispatcher.TryEnqueue("del-3", "pull_request:opened", _ => ValueTask.CompletedTask));

        releaseWorker.SetResult();
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WhenForcedShutdownTimeoutCancelsDrain_ReleasesClaimsForDroppedQueuedItems()
    {
        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 1);

        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeInterruptedReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var droppedClaimReleased = false;

        await dispatcher.StartAsync(CancellationToken.None);

        Assert.True(dispatcher.TryEnqueue(
            "del-active",
            "pull_request:opened",
            async ct =>
            {
                activeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
            onPermanentFailure: () => activeInterruptedReleased.TrySetResult()));

        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(dispatcher.TryEnqueue(
            "del-queued-dropped",
            "workflow_run:completed",
            _ => ValueTask.CompletedTask,
            onPermanentFailure: () => droppedClaimReleased = true));

        using var shutdownCts = new CancellationTokenSource();
        var stopTask = dispatcher.StopAsync(shutdownCts.Token);
        shutdownCts.Cancel();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        await activeInterruptedReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(droppedClaimReleased);
    }

    [Fact]
    public async Task ExecuteWorkItemWithRetryAsync_RateLimitExceededWithFutureReset_UsesNonBlockingDelayedReEnqueueWithoutStarvingWorker()
    {
        var capturedDelay = TimeSpan.Zero;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRateLimitDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var health = new RuntimeHealthState();

        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: health,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 2,
            retryDelaySelector: _ => TimeSpan.FromMilliseconds(10),
            delayAsync: (delay, ct) =>
            {
                capturedDelay = delay;
                delayStarted.TrySetResult();
                return releaseRateLimitDelay.Task.WaitAsync(ct);
            },
            inlineRetryBlockThreshold: TimeSpan.FromSeconds(15),
            maxRateLimitDelay: TimeSpan.FromMinutes(65));

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var del1Succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var del2Succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permanentFailureCalled = false;

        var resetEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "etag",
            new RateLimit(5000, 0, resetEpoch),
            TimeSpan.Zero);
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.Forbidden);
        response.ApiInfo.Returns(apiInfo);
        var rateLimitEx = new RateLimitExceededException(response);

        Assert.True(dispatcher.TryEnqueue(
            "del-ratelimit-1h",
            "workflow_run:completed",
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw rateLimitEx;
                }

                del1Succeeded.TrySetResult();
                return ValueTask.CompletedTask;
            },
            onPermanentFailure: () => permanentFailureCalled = true));

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capturedDelay > TimeSpan.FromMinutes(50), $"Expected rate-limit delay >50m, got {capturedDelay}");
        Assert.True(capturedDelay <= TimeSpan.FromMinutes(65), $"Expected rate-limit delay <=65m cap, got {capturedDelay}");

        // Worker must NOT be blocked waiting for 1h delay; second delivery executes immediately on single worker
        Assert.True(dispatcher.TryEnqueue(
            "del-fast-while-waiting",
            "pull_request:opened",
            _ =>
            {
                del2Succeeded.TrySetResult();
                return ValueTask.CompletedTask;
            }));
        await del2Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts);

        // Once rate-limit window ends, del-ratelimit-1h re-enqueues at Attempt 2 and succeeds
        releaseRateLimitDelay.SetResult();
        await del1Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.False(permanentFailureCalled);
        Assert.Null(health.GetLastError().LastError);
    }

    [Fact]
    public async Task ExecuteWorkItemWithRetryAsync_PipelineCommentRateLimitWrappedException_UsesRetryAfterDelay()
    {
        var capturedDelays = new List<TimeSpan>();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRateLimitDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherHealth = new RuntimeHealthState();
        var pipelineHealth = new RuntimeHealthState();

        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            runtimeHealth: dispatcherHealth,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 2,
            retryDelaySelector: _ => TimeSpan.FromMilliseconds(10),
            delayAsync: (delay, ct) =>
            {
                capturedDelays.Add(delay);
                delayStarted.TrySetResult();
                return releaseRateLimitDelay.Task.WaitAsync(ct);
            },
            inlineRetryBlockThreshold: TimeSpan.FromSeconds(15),
            maxRateLimitDelay: TimeSpan.FromMinutes(65));

        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (Octokit.PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequest));
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.Labels))
            ?.SetValue(freshPr, Array.Empty<Label>());
        gitHub.GetPullRequestAsync("owner", "repo", 77, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Octokit.PullRequest?>(freshPr));

        var resetEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "etag",
            new RateLimit(5000, 0, resetEpoch),
            TimeSpan.Zero);
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.Forbidden);
        response.ApiInfo.Returns(apiInfo);
        var rateLimitEx = new RateLimitExceededException(response);

        const string commentBody = "Review note carried across rate limit retry";
        var commentCallCount = 0;
        var pipelineSucceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gitHub.CreateCommentAsync("owner", "repo", 77, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Fail both inline attempts of FlushPendingCommentsAsync on dispatcher attempt 1 so CommentPostingTransientException surfaces.
                if (Interlocked.Increment(ref commentCallCount) <= 2)
                {
                    return Task.FromException<IssueComment>(rateLimitEx);
                }

                pipelineSucceeded.TrySetResult();
                return Task.FromResult((IssueComment)RuntimeHelpers.GetUninitializedObject(typeof(IssueComment)));
            });

        var moduleRuns = 0;
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref moduleRuns) == 1)
                {
                    ci.Arg<PullRequestContext>().PendingComments.Add(commentBody);
                }

                return Task.CompletedTask;
            });

        var pipeline = new PullRequestPipeline(
            new[] { module },
            new LabelMutexEngine(),
            gitHub,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask,
            runtimeHealth: pipelineHealth);

        await dispatcher.StartAsync(CancellationToken.None);

        var attempts = 0;
        var permanentFailureCalled = false;
        Assert.True(dispatcher.TryEnqueue(
            "del-comment-ratelimit-wrapped",
            "pull_request:opened",
            async ct =>
            {
                Interlocked.Increment(ref attempts);
                var context = new PullRequestContext(
                    gitHub,
                    "captains",
                    headers: null,
                    PullRequestEventType.PullRequest,
                    "owner",
                    "repo",
                    prNumber: 77,
                    headSha: "head-sha-77",
                    baseBranch: "main",
                    isDraft: false,
                    isMerged: false,
                    title: "Test PR",
                    body: null,
                    senderLogin: "alice",
                    authorLogin: "alice",
                    action: "opened",
                    existingLabels: Array.Empty<string>(),
                    cancellationToken: ct);
                await pipeline.ExecuteAsync(context, ct);
            },
            onPermanentFailure: () => permanentFailureCalled = true));

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var capturedDelay = Assert.Single(capturedDelays);
        Assert.True(capturedDelay > TimeSpan.FromMinutes(50), $"Expected rate-limit delay >50m from unwrapped CommentPostingTransientException, got {capturedDelay}");
        Assert.True(capturedDelay <= TimeSpan.FromMinutes(65), $"Expected rate-limit delay <=65m cap, got {capturedDelay}");

        releaseRateLimitDelay.SetResult();
        await pipelineSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.False(permanentFailureCalled);
        Assert.Null(dispatcherHealth.GetLastError().LastError);
        Assert.Contains("Transient comment failure", pipelineHealth.GetLastError().LastError);
        await gitHub.Received(3).CreateCommentAsync(
            "owner",
            "repo",
            77,
            Arg.Is<string>(b => b.Contains(commentBody, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_WithPendingDelayedRateLimitRetry_CancelsTimerAndReleasesClaimPromptly()
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaimTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var dispatcher = new WebhookDispatcher(
            NullLogger<WebhookDispatcher>.Instance,
            capacity: 8,
            maxConcurrency: 1,
            maxAttempts: 3,
            retryDelaySelector: _ => TimeSpan.Zero,
            delayAsync: (delay, ct) =>
            {
                delayStarted.TrySetResult();
                return Task.Delay(delay, ct);
            },
            inlineRetryBlockThreshold: TimeSpan.FromSeconds(15),
            maxRateLimitDelay: TimeSpan.FromMinutes(65));

        await dispatcher.StartAsync(CancellationToken.None);

        Assert.True(dispatcher.TryEnqueue(
            "del-secondary-shutdown",
            "pull_request:opened",
            _ => throw (SecondaryRateLimitExceededException)RuntimeHelpers.GetUninitializedObject(
                typeof(SecondaryRateLimitExceededException)),
            onPermanentFailure: () => releaseClaimTcs.TrySetResult()));

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // StopAsync cancels the 60s delayed retry timer promptly without hanging and releases the delivery claim
        await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await releaseClaimTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_WithoutPriorStopAsync_DoesNotThrowObjectDisposedException()
    {
        var dispatcher = new WebhookDispatcher(NullLogger<WebhookDispatcher>.Instance);
        dispatcher.Dispose();
        dispatcher.Dispose();
    }

    [Fact]
    public async Task MapGitHubWebhooks_WhenDispatcherOverloaded_ReturnsHttp503WithRetryAfter()
    {
        const string webhookSecret = "test-webhook-secret";
        var mockDispatcher = Substitute.For<IWebhookDispatcher>();
        mockDispatcher.TryEnqueue(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, ValueTask>>(),
                Arg.Any<Action?>())
            .Returns(false);

        var gitHubMock = Substitute.For<IGitHubService>();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHubApp:TeamName"] = "captains" })
            .Build();
        var mutex = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(Array.Empty<IPullRequestModule>(), mutex, gitHubMock);
        var lockManager = new PullRequestLockManager();
        var processor = new GitHubWebhookProcessor(gitHubMock, cfg, pipeline, lockManager, mutex, dispatcher: mockDispatcher);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<WebhookEventProcessor>(processor);
        builder.Services.Configure<GitHubWebhookOptions>(o =>
            o.ExceptionHandler = WebhookDispatcher.HandleWebhookExceptionAsync);

        var app = builder.Build();
        app.MapGitHubWebhooks("/api/webhook", webhookSecret);
        await app.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var baseUrl = addresses!.Addresses.First();

            var link = new { href = "https://api.github.com/repos/owner/repo/pulls/1" };
            var user = new
            {
                login = "alice",
                id = 1L,
                avatar_url = "https://avatars.githubusercontent.com/u/1",
                url = "https://api.github.com/users/alice",
                type = "User"
            };
            var repo = new
            {
                id = 1L,
                node_id = "R_1",
                name = "repo",
                full_name = "owner/repo",
                owner = user,
                html_url = "https://github.com/owner/repo",
                url = "https://api.github.com/repos/owner/repo"
            };
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "opened",
                number = 1L,
                pull_request = new
                {
                    id = 1L,
                    node_id = "PR_1",
                    url = "https://api.github.com/repos/owner/repo/pulls/1",
                    html_url = "https://github.com/owner/repo/pull/1",
                    diff_url = "https://github.com/owner/repo/pull/1.diff",
                    patch_url = "https://github.com/owner/repo/pull/1.patch",
                    issue_url = "https://api.github.com/repos/owner/repo/issues/1",
                    commits_url = "https://api.github.com/repos/owner/repo/pulls/1/commits",
                    review_comments_url = "https://api.github.com/repos/owner/repo/pulls/1/comments",
                    review_comment_url = "https://api.github.com/repos/owner/repo/pulls/comments{/number}",
                    comments_url = "https://api.github.com/repos/owner/repo/issues/1/comments",
                    statuses_url = "https://api.github.com/repos/owner/repo/statuses/abc",
                    number = 1L,
                    state = "open",
                    title = "Test",
                    draft = false,
                    user,
                    assignees = Array.Empty<object>(),
                    requested_reviewers = Array.Empty<object>(),
                    requested_teams = Array.Empty<object>(),
                    labels = Array.Empty<object>(),
                    head = new { label = "alice:feat", @ref = "feat", sha = "abc", user },
                    @base = new { label = "owner:main", @ref = "main", sha = "def", user, repo },
                    _links = new
                    {
                        self = link,
                        html = link,
                        issue = link,
                        comments = link,
                        review_comments = link,
                        review_comment = link,
                        commits = link,
                        statuses = link
                    },
                    author_association = "NONE",
                    mergeable_state = "clean"
                },
                repository = repo,
                sender = user
            });
            var signatureBytes = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(webhookSecret),
                Encoding.UTF8.GetBytes(payloadJson));
            var signatureHeader = $"sha256={Convert.ToHexStringLower(signatureBytes)}";

            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/webhook")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-GitHub-Event", "pull_request");
            request.Headers.Add("X-GitHub-Delivery", "del-http-503-test");
            request.Headers.Add("X-Hub-Signature-256", signatureHeader);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

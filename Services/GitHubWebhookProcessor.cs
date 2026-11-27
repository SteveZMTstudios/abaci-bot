using System.Runtime.ExceptionServices;
using System.Text.Json;
using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.PullRequestReviewComment;
using Octokit.Webhooks.Events.WorkflowRun;

namespace abaci_bot.Services;

/// <summary>
/// Webhook event ingestion and dispatch coordinator.
/// Acts as a thin dispatcher that validates incoming payload context, builds the domain
/// <see cref="PullRequestContext"/>, acquires fine-grained per-PR locks, and delegates governance
/// evaluation to the <see cref="PullRequestPipeline"/>.
/// </summary>
public class GitHubWebhookProcessor : WebhookEventProcessor
{
    private const int DefaultMaxTrackedDeliveries = 4096;
    private static readonly TimeSpan DeliveryDeduplicationTtl = TimeSpan.FromMinutes(10);

    internal int MaxTrackedDeliveries { get; set; } = DefaultMaxTrackedDeliveries;

    private readonly IGitHubService _github;
    private readonly IConfiguration _config;
    private readonly PullRequestPipeline _pipeline;
    private readonly PullRequestLockManager _lockManager;
    private readonly LabelMutexEngine _mutexEngine;
    private readonly ILogger<GitHubWebhookProcessor>? _logger;
    private readonly BotDetectionOptions _botOptions;
    private readonly AbiComplianceOptions _abiOptions;
    private readonly PackageUpdateOptions _packageUpdateOptions;
    private readonly IArtifactService? _artifactService;
    private readonly IAbiDiffService? _abiDiffService;
    private readonly IFileContentCache? _fileCache;
    private readonly TimeProvider _timeProvider;
    private readonly IBotIdentityProvider? _botIdentity;
    private readonly IWebhookDispatcher? _dispatcher;
    private readonly IOpenPullRequestInventoryGateway? _prInventoryGateway;
    private readonly object _deliveryLock = new();
    private readonly Dictionary<string, DateTimeOffset> _seenDeliveries = new(StringComparer.OrdinalIgnoreCase);

    public GitHubWebhookProcessor(
        IGitHubService github,
        IConfiguration config,
        PullRequestPipeline pipeline,
        PullRequestLockManager lockManager,
        LabelMutexEngine? mutexEngine = null,
        ILogger<GitHubWebhookProcessor>? logger = null,
        IOptions<BotDetectionOptions>? botOptions = null,
        IArtifactService? artifactService = null,
        IAbiDiffService? abiDiffService = null,
        IFileContentCache? fileCache = null,
        IOptions<AbiComplianceOptions>? abiOptions = null,
        TimeProvider? timeProvider = null,
        IBotIdentityProvider? botIdentity = null,
        IOptions<PackageUpdateOptions>? packageUpdateOptions = null,
        IWebhookDispatcher? dispatcher = null,
        IOpenPullRequestInventoryGateway? prInventoryGateway = null)
    {
        _github = github;
        _config = config;
        _pipeline = pipeline;
        _lockManager = lockManager;
        _mutexEngine = mutexEngine ?? new LabelMutexEngine();
        _logger = logger;
        _botOptions = botOptions?.Value
            ?? config.GetSection("BotDetection").Get<BotDetectionOptions>()
            ?? new BotDetectionOptions();
        _abiOptions = abiOptions?.Value
            ?? config.GetSection("AbiCompliance").Get<AbiComplianceOptions>()
            ?? new AbiComplianceOptions();
        _packageUpdateOptions = packageUpdateOptions?.Value
            ?? config.GetSection("PackageUpdate").Get<PackageUpdateOptions>()
            ?? new PackageUpdateOptions();
        _artifactService = artifactService;
        _abiDiffService = abiDiffService;
        _fileCache = fileCache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _botIdentity = botIdentity ?? github as IBotIdentityProvider;
        _dispatcher = dispatcher;
        _prInventoryGateway = prInventoryGateway ?? github as IOpenPullRequestInventoryGateway;
    }

    /// <summary>
    /// Convenience constructor for standalone testing and default pipeline assembly.
    /// </summary>
    public GitHubWebhookProcessor(IGitHubService github, IConfiguration config)
        : this(github, config, CreateDefaultPipeline(github), new PullRequestLockManager(), new LabelMutexEngine())
    {
    }

    /// <summary>
    /// Instantiates the default governance pipeline with built-in modules for unit tests.
    /// </summary>
    internal static PullRequestPipeline CreateDefaultPipeline(IGitHubService github)
    {
        var mutexEngine = new LabelMutexEngine();
        var modules = new IPullRequestModule[]
        {
            new WorkflowStateModule(),
            new UserContributionModule(),
            new BuildSystemAnalysisModule(),
            new AiAssistanceModule()
        };
        return new PullRequestPipeline(modules, mutexEngine, github);
    }

    protected override ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDeliveryDedupAsync(headers, $"pull_request.{action}", async ct =>
        {
            if (!TryGetRepositoryContext(pullRequestEvent.Repository, out var owner, out var repo))
                return;

            var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
            var context = PullRequestContext.FromPullRequestEvent(_github, teamName, headers, pullRequestEvent, action, _fileCache, ct);

            using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, ct))
            {
                await _pipeline.ExecuteAsync(context, ct);
            }
        }, cancellationToken);
    }

    protected override ValueTask ProcessPullRequestReviewWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewEvent pullRequestReviewEvent,
        PullRequestReviewAction action,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDeliveryDedupAsync(headers, $"pull_request_review.{action}", async ct =>
        {
            // Ignore non-Submitted review actions (e.g. Dismissed, Edited) to prevent unintended state transitions
            if (action != PullRequestReviewAction.Submitted)
                return;

            if (!TryGetRepositoryContext(pullRequestReviewEvent.Repository, out var owner, out var repo))
                return;

            if (!HasSender(pullRequestReviewEvent.Sender))
                return;

            var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
            var context = PullRequestContext.FromReviewEvent(_github, teamName, headers, pullRequestReviewEvent, action, _fileCache, ct);

            using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, ct))
            {
                await _pipeline.ExecuteAsync(context, ct);
            }
        }, cancellationToken);
    }

    protected override ValueTask ProcessPullRequestReviewCommentWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewCommentEvent pullRequestReviewCommentEvent,
        PullRequestReviewCommentAction action,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDeliveryDedupAsync(headers, $"pull_request_review_comment.{action}", async ct =>
        {
            // Only react to newly created inline review comments; ignore edits and deletions
            if (action != PullRequestReviewCommentAction.Created)
                return;

            if (!TryGetRepositoryContext(pullRequestReviewCommentEvent.Repository, out var owner, out var repo))
                return;

            if (!HasSender(pullRequestReviewCommentEvent.Sender))
                return;

            var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
            var context = PullRequestContext.FromReviewCommentEvent(_github, teamName, headers, pullRequestReviewCommentEvent, action, _fileCache, ct);

            using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, ct))
            {
                await _pipeline.ExecuteAsync(context, ct);
            }
        }, cancellationToken);
    }

    protected override ValueTask ProcessIssueCommentWebhookAsync(
        WebhookHeaders headers,
        IssueCommentEvent issueCommentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDeliveryDedupAsync(headers, $"issue_comment.{action}", async ct =>
        {
            // Only react to newly created comments; ignore edits and deletions
            if (action != IssueCommentAction.Created)
                return;

            if (!HasSender(issueCommentEvent.Sender))
                return;

            // Handle regular issue comments: instant un-stale for non-PR issues
            if (issueCommentEvent.Issue.PullRequest == null)
            {
                if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var issueOwner, out var issueRepo))
                    return;

                var existingLabels = GetLabelNames(issueCommentEvent.Issue.Labels);
                if (existingLabels.Contains(GitHubLabels.StatusStale))
                {
                    var senderLogin = issueCommentEvent.Sender!.Login ?? string.Empty;
                    var senderType = issueCommentEvent.Sender.Type?.ToString();

                    if (!BotDetectionHelper.IsBot(senderLogin, senderType, _botOptions.Whitelist) &&
                        !await BotCommitOwnership.IsOwnBotLoginAsync(_botIdentity, senderLogin, _packageUpdateOptions.BotLogin, ct))
                    {
                        using (await _lockManager.AcquireLockAsync(issueOwner, issueRepo, (int)issueCommentEvent.Issue.Number, ct))
                        {
                            try
                            {
                                await _github.RemoveLabelAsync(issueOwner, issueRepo, (int)issueCommentEvent.Issue.Number, GitHubLabels.StatusStale, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Failed to remove label '{Label}' from issue {Owner}/{Repo}#{Number}",
                                    GitHubLabels.StatusStale, issueOwner, issueRepo, issueCommentEvent.Issue.Number);
                            }
                        }
                    }
                }
                return;
            }

            if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var owner, out var repo))
                return;

            var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
            var context = PullRequestContext.FromIssueCommentEvent(_github, teamName, headers, issueCommentEvent, action, _fileCache, ct);

            using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, ct))
            {
                await _pipeline.ExecuteAsync(context, ct);
            }
        }, cancellationToken);
    }

    internal sealed record WorkflowRunPayload(
        string Action,
        string Owner,
        string Repo,
        long RunId,
        string? WorkflowName,
        string? WorkflowPath,
        string? HeadSha,
        string? HeadBranch,
        string? Event,
        string? Conclusion,
        string? HeadRepositoryFullName,
        string? HeadRepositoryOwnerLogin,
        IReadOnlyList<int> PullRequestNumbers);

    private sealed record TolerantWorkflowRunWebhookEvent(WorkflowRunPayload Payload) : WebhookEvent;

    public override WebhookEvent DeserializeWebhookEvent(WebhookHeaders headers, string body)
    {
        try
        {
            return base.DeserializeWebhookEvent(headers, body);
        }
        catch (JsonException ex) when (string.Equals(headers.Event, "workflow_run", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseTolerantWorkflowRun(body, out var payload))
            {
                _logger?.LogWarning(
                    ex,
                    "Octokit.Webhooks failed strict deserialization for workflow_run (action={Action}, runId={RunId}); falling back to tolerant payload extraction.",
                    payload.Action,
                    payload.RunId);
                return new TolerantWorkflowRunWebhookEvent(payload);
            }

            throw;
        }
    }

    public override ValueTask ProcessWebhookAsync(
        WebhookHeaders headers,
        WebhookEvent webhookEvent,
        CancellationToken cancellationToken = default)
    {
        if (webhookEvent is TolerantWorkflowRunWebhookEvent tolerant)
        {
            return ExecuteWithDeliveryDedupAsync(
                headers,
                $"workflow_run.{tolerant.Payload.Action}",
                ct => ProcessWorkflowRunPayloadCoreAsync(headers, tolerant.Payload, ct),
                cancellationToken);
        }

        return base.ProcessWebhookAsync(headers, webhookEvent, cancellationToken);
    }

    protected override ValueTask ProcessWorkflowRunWebhookAsync(
        WebhookHeaders headers,
        WorkflowRunEvent workflowRunEvent,
        WorkflowRunAction action,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithDeliveryDedupAsync(
            headers,
            $"workflow_run.{action}",
            ct => ProcessWorkflowRunCoreAsync(headers, workflowRunEvent, action, ct),
            cancellationToken);
    }

    private static bool TryParseTolerantWorkflowRun(string body, out WorkflowRunPayload payload)
    {
        payload = default!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var action = TryGetStringProperty(root, "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                return false;
            }

            if (!root.TryGetProperty("repository", out var repoEl) || repoEl.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var owner = repoEl.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.Object
                ? TryGetStringProperty(ownerEl, "login")
                : null;
            var repo = TryGetStringProperty(repoEl, "name");
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                return false;
            }

            if (!root.TryGetProperty("workflow_run", out var runEl) || runEl.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var runId = runEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var parsedId)
                ? parsedId
                : 0L;

            string? headRepoFullName = null;
            string? headRepoOwnerLogin = null;
            if (runEl.TryGetProperty("head_repository", out var headRepoEl) && headRepoEl.ValueKind == JsonValueKind.Object)
            {
                headRepoFullName = TryGetStringProperty(headRepoEl, "full_name");
                if (headRepoEl.TryGetProperty("owner", out var headOwnerEl) && headOwnerEl.ValueKind == JsonValueKind.Object)
                {
                    headRepoOwnerLogin = TryGetStringProperty(headOwnerEl, "login");
                }
            }

            var prNumbers = new List<int>();
            if (runEl.TryGetProperty("pull_requests", out var prsEl) && prsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var prEl in prsEl.EnumerateArray())
                {
                    if (prEl.ValueKind == JsonValueKind.Object &&
                        prEl.TryGetProperty("number", out var numEl) &&
                        numEl.ValueKind == JsonValueKind.Number &&
                        numEl.TryGetInt32(out var prNum))
                    {
                        prNumbers.Add(prNum);
                    }
                }
            }

            payload = new WorkflowRunPayload(
                Action: action,
                Owner: owner,
                Repo: repo,
                RunId: runId,
                WorkflowName: TryGetStringProperty(runEl, "name"),
                WorkflowPath: TryGetStringProperty(runEl, "path"),
                HeadSha: TryGetStringProperty(runEl, "head_sha"),
                HeadBranch: TryGetStringProperty(runEl, "head_branch"),
                Event: TryGetStringProperty(runEl, "event"),
                Conclusion: TryGetStringProperty(runEl, "conclusion"),
                HeadRepositoryFullName: headRepoFullName,
                HeadRepositoryOwnerLogin: headRepoOwnerLogin,
                PullRequestNumbers: prNumbers);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryGetStringProperty(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private async ValueTask ExecuteWithDeliveryDedupAsync(
        WebhookHeaders? headers,
        string eventSummary,
        Func<CancellationToken, ValueTask> action,
        CancellationToken httpCancellationToken)
    {
        var deliveryId = headers?.Delivery;
        if (_dispatcher != null)
        {
            if (!string.IsNullOrWhiteSpace(deliveryId) && !TryClaimDelivery(deliveryId))
            {
                _logger?.LogInformation("Skipping duplicate GitHub webhook delivery {DeliveryId}", deliveryId);
                return;
            }

            try
            {
                var accepted = _dispatcher.TryEnqueue(
                    deliveryId ?? "<no-delivery-id>",
                    eventSummary,
                    action,
                    onPermanentFailure: !string.IsNullOrWhiteSpace(deliveryId)
                        ? () => ReleaseDelivery(deliveryId)
                        : null);
                if (!accepted)
                {
                    if (!string.IsNullOrWhiteSpace(deliveryId))
                    {
                        ReleaseDelivery(deliveryId);
                    }

                    _logger?.LogWarning(
                        "Webhook dispatcher queue is full; rejecting delivery '{DeliveryId}' ({EventSummary}) with HTTP 503.",
                        deliveryId ?? "<no-delivery-id>", eventSummary);
                    throw new WebhookDispatcherOverloadedException(
                        $"Webhook dispatcher queue is full for event '{eventSummary}'.");
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(deliveryId))
                {
                    ReleaseDelivery(deliveryId);
                }
                throw;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            await action(httpCancellationToken);
            return;
        }

        if (!TryClaimDelivery(deliveryId))
        {
            _logger?.LogInformation("Skipping duplicate GitHub webhook delivery {DeliveryId}", deliveryId);
            return;
        }

        try
        {
            await action(httpCancellationToken);
        }
        catch
        {
            ReleaseDelivery(deliveryId);
            throw;
        }
    }

    private bool TryClaimDelivery(string deliveryId)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_deliveryLock)
        {
            if (_seenDeliveries.TryGetValue(deliveryId, out var seenAt) &&
                now - seenAt < DeliveryDeduplicationTtl)
            {
                return false;
            }

            if (_seenDeliveries.Count >= MaxTrackedDeliveries)
            {
                var expiredKeys = _seenDeliveries
                    .Where(kv => now - kv.Value >= DeliveryDeduplicationTtl)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var expired in expiredKeys)
                {
                    _seenDeliveries.Remove(expired);
                }

                if (_seenDeliveries.Count >= MaxTrackedDeliveries)
                {
                    var oldestKey = _seenDeliveries.MinBy(kv => kv.Value).Key;
                    if (oldestKey != null)
                    {
                        _seenDeliveries.Remove(oldestKey);
                    }
                }
            }

            _seenDeliveries[deliveryId] = now;
            return true;
        }
    }

    private void ReleaseDelivery(string deliveryId)
    {
        lock (_deliveryLock)
        {
            _seenDeliveries.Remove(deliveryId);
        }
    }

    private ValueTask ProcessWorkflowRunCoreAsync(
        WebhookHeaders headers,
        WorkflowRunEvent workflowRunEvent,
        WorkflowRunAction action,
        CancellationToken cancellationToken)
    {
        if (action != WorkflowRunAction.Completed)
        {
            return ValueTask.CompletedTask;
        }

        if (!TryGetRepositoryContext(workflowRunEvent.Repository, out var owner, out var repo))
        {
            return ValueTask.CompletedTask;
        }

        var run = workflowRunEvent.WorkflowRun;
        var prNumbers = run?.PullRequests?.Select(pr => (int)pr.Number).ToList() ?? [];
        var payload = new WorkflowRunPayload(
            Action: "completed",
            Owner: owner,
            Repo: repo,
            RunId: run?.Id ?? 0,
            WorkflowName: run?.Name,
            WorkflowPath: run?.Path,
            HeadSha: run?.HeadSha,
            HeadBranch: run?.HeadBranch,
            Event: run?.Event,
            Conclusion: run?.Conclusion?.StringValue,
            HeadRepositoryFullName: run?.HeadRepository?.FullName,
            HeadRepositoryOwnerLogin: run?.HeadRepository?.Owner?.Login,
            PullRequestNumbers: prNumbers);

        return ProcessWorkflowRunPayloadCoreAsync(headers, payload, cancellationToken);
    }

    private async ValueTask ProcessWorkflowRunPayloadCoreAsync(
        WebhookHeaders headers,
        WorkflowRunPayload payload,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(payload.Action, "completed", StringComparison.OrdinalIgnoreCase))
            return;

        var owner = payload.Owner;
        var repo = payload.Repo;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return;

        var runId = payload.RunId;
        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var runHeadSha = payload.HeadSha;
        var prNumbers = payload.PullRequestNumbers;

        // Pre-fetch PRs and guard against outdated runs before performing heavy artifact downloads
        var validPrs = new List<Octokit.PullRequest>();
        if (prNumbers.Count > 0)
        {
            foreach (var prNumber in prNumbers)
            {
                var fullPr = await _github.GetPullRequestAsync(owner, repo, prNumber, cancellationToken);
                if (fullPr == null)
                {
                    _logger?.LogWarning("PR #{PrNumber} not found in {Owner}/{Repo} during workflow_run processing; skipping", prNumber, owner, repo);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(runHeadSha) &&
                    !string.IsNullOrWhiteSpace(fullPr.Head?.Sha) &&
                    !string.Equals(runHeadSha, fullPr.Head.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogInformation(
                        "Workflow run {RunId} head SHA '{RunHeadSha}' does not match PR #{PrNumber} current HEAD '{PrHeadSha}'; skipping outdated run to prevent stale overwrite",
                        runId, runHeadSha, prNumber, fullPr.Head.Sha);
                    continue;
                }

                validPrs.Add(fullPr);
            }
        }
        else
        {
            var runEvent = payload.Event;
            var headOwner = payload.HeadRepositoryOwnerLogin;
            var headBranch = payload.HeadBranch;
            var headRepoFullName = payload.HeadRepositoryFullName;
            var isPullRequestOrForkRun =
                string.Equals(runEvent, "pull_request", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runEvent, "pull_request_target", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(headRepoFullName) &&
                 !string.Equals(headRepoFullName, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase));

            if (!isPullRequestOrForkRun || string.IsNullOrWhiteSpace(runHeadSha) || _prInventoryGateway == null)
            {
                return;
            }

            var resolvedPrs = await _prInventoryGateway.FindOpenPullRequestsByHeadShaAsync(
                owner,
                repo,
                headOwner,
                headBranch,
                runHeadSha,
                cancellationToken);

            foreach (var resolvedPr in resolvedPrs)
            {
                validPrs.Add(resolvedPr);
            }

            if (validPrs.Count > 0)
            {
                _logger?.LogInformation(
                    "Resolved {Count} open PR(s) via head SHA fallback for workflow run {RunId} (event={Event}, head={HeadOwner}:{HeadBranch}@{HeadSha})",
                    validPrs.Count, runId, runEvent ?? "<unknown>", headOwner ?? "<unknown>", headBranch ?? "<unknown>", runHeadSha);
            }
            else
            {
                _logger?.LogWarning(
                    "Workflow run {RunId} (event={Event}, head={HeadOwner}:{HeadBranch}@{HeadSha}) had empty pull_requests and matched no open PR in {Owner}/{Repo}; skipping ABI analysis",
                    runId, runEvent ?? "<unknown>", headOwner ?? "<unknown>", headBranch ?? "<unknown>", runHeadSha, owner, repo);
                return;
            }
        }

        if (validPrs.Count == 0)
            return;

        // Determine if this workflow is authorized for ABI compliance checks
        var workflowName = payload.WorkflowName;
        var workflowPath = payload.WorkflowPath;
        var workflowNames = _abiOptions.WorkflowNames != null && _abiOptions.WorkflowNames.Count > 0
            ? _abiOptions.WorkflowNames
            : new List<string> { "build", "package", "rpm" };

        var isAbiWorkflow = IsAuthorizedWorkflow(workflowName, workflowPath, workflowNames);

        if (!isAbiWorkflow)
        {
            _logger?.LogInformation(
                "Workflow '{WorkflowName}' (path: '{WorkflowPath}') is not in authorized AbiCompliance:WorkflowNames [{ConfiguredNames}]; skipping ABI diff analysis",
                workflowName, workflowPath, string.Join(", ", workflowNames));
        }

        // Perform ABI diff analysis if artifact & abiDiff services are available, run succeeded, and workflow matches
        AbiDiffResult? abiResult = null;
        var conclusion = payload.Conclusion;
        var isSuccess = string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase);

        if (isAbiWorkflow && _artifactService != null && _abiDiffService != null && runId > 0 && isSuccess)
        {
            try
            {
                var tempExtractionDir = _config.GetSection("AbiCompliance").GetValue<string>("TempExtractionDirectory");
                var baseTemp = !string.IsNullOrWhiteSpace(tempExtractionDir)
                    ? tempExtractionDir
                    : Path.GetTempPath();
                var tempDir = Path.Combine(baseTemp, "abaci_abi_" + Guid.NewGuid().ToString("N"));
                try
                {
                    IReadOnlyList<string> prRpms;
                    try
                    {
                        prRpms = await _artifactService.DownloadRunRpmArtifactsAsync(owner, repo, runId, tempDir, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to download or extract RPM artifacts for workflow run {RunId}", runId);
                        abiResult = AbiDiffResult.NotPerformed($"Failed to download or extract RPM artifacts: {ex.Message}");
                        prRpms = Array.Empty<string>();
                    }

                    if (abiResult == null)
                    {
                        var diffResults = new List<AbiDiffResult>();

                        foreach (var prRpm in prRpms)
                        {
                            var fileName = Path.GetFileName(prRpm);

                            // 1. Filter out source packages (.src.rpm) and debug packages (-debuginfo, -debugsource)
                            if (!ArtifactService.IsEligibleBinaryRpm(fileName))
                            {
                                _logger?.LogInformation("Skipping non-binary or auxiliary RPM package '{FileName}' from ABI comparison", fileName);
                                continue;
                            }

                            // 2. Pre-scan: if package contains no shared library binaries, skip ABI comparison.
                            // Fail-closed: unparseable/corrupted RPMs must not be silently skipped as "no libraries".
                            bool hasSharedLibs;
                            try
                            {
                                hasSharedLibs = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync(prRpm, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed to inspect RPM package '{FileName}' for shared libraries; recording incomplete analysis", fileName);
                                diffResults.Add(AbiDiffResult.NotPerformed($"RPM package '{fileName}' could not be parsed: {ex.Message}"));
                                continue;
                            }

                            if (!hasSharedLibs)
                            {
                                _logger?.LogInformation("Skipping RPM package '{FileName}' from ABI comparison: contains no shared library binaries", fileName);
                                continue;
                            }

                            var pkgName = ArtifactService.ExtractPackageName(fileName);
                            var prArch = ArtifactService.ExtractPackageArch(fileName);
                            var prDebuginfo = ArtifactService.FindMatchingDebuginfoRpm(prRpm, prRpms);
                            var baselineRpm = await _artifactService.DownloadBaselineRpmAsync(pkgName, tempDir, prArch, cancellationToken);
                            if (!string.IsNullOrEmpty(baselineRpm) && File.Exists(baselineRpm))
                            {
                                var debuginfoPkg = !string.IsNullOrWhiteSpace(prDebuginfo)
                                    ? ArtifactService.ExtractPackageName(Path.GetFileName(prDebuginfo))
                                    : pkgName;
                                var baselineDebuginfo = !string.IsNullOrWhiteSpace(prDebuginfo)
                                    ? await _artifactService.DownloadBaselineDebuginfoRpmAsync(debuginfoPkg, tempDir, prArch, cancellationToken)
                                    : null;
                                var diff = (!string.IsNullOrWhiteSpace(baselineDebuginfo) || !string.IsNullOrWhiteSpace(prDebuginfo))
                                    ? await _abiDiffService.CompareRpmsAsync(baselineRpm, prRpm, baselineDebuginfo, prDebuginfo, cancellationToken)
                                    : await _abiDiffService.CompareRpmsAsync(baselineRpm, prRpm, cancellationToken);
                                diffResults.Add(diff);
                            }
                            else
                            {
                                diffResults.Add(AbiDiffResult.NotPerformed($"Baseline RPM package not available for package '{pkgName}'"));
                            }
                        }

                        if (diffResults.Count > 0)
                        {
                            var anyBreaking = diffResults.Any(d => d.IsBreakingChange);
                            var anyFailed = diffResults.Any(d => !d.Succeeded);
                            var anyIncomplete = diffResults.Any(d => d.IsIncomplete);
                            var anyAbiChange = diffResults.Any(d => d.HasAbiChange);

                            var allSucceeded = diffResults.All(d => d.Succeeded);

                            var combinedDetails = string.Join("\n\n", diffResults
                                .Select(d => d.Details)
                                .Where(s => !string.IsNullOrWhiteSpace(s)));

                            var errorMessages = diffResults
                                .Where(d => !d.Succeeded && !string.IsNullOrWhiteSpace(d.ErrorMessage))
                                .Select(d => d.ErrorMessage!)
                                .Distinct()
                                .ToList();
                            var combinedError = errorMessages.Count > 0 ? string.Join("; ", errorMessages) : null;

                            abiResult = new AbiDiffResult(
                                Succeeded: allSucceeded,
                                HasAbiChange: anyAbiChange,
                                IsBreakingChange: anyBreaking,
                                Details: combinedDetails,
                                ErrorMessage: combinedError,
                                ToolMissing: diffResults.Any(d => d.ToolMissing),
                                IsIncomplete: anyIncomplete,
                                HasFailedChecks: anyFailed);
                        }
                        else if (prRpms.Count == 0)
                        {
                            abiResult = AbiDiffResult.NotPerformed("No RPM artifacts found for workflow run");
                        }
                        else
                        {
                            abiResult = new AbiDiffResult(
                                Succeeded: true,
                                HasAbiChange: false,
                                IsBreakingChange: false,
                                Details: "RPM packages contain no shared library binaries");
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore cleanup error */ }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to perform ABI diff analysis for run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            }
        }

        List<Exception>? transientFailures = null;
        List<Exception>? nonRetriableFailures = null;
        foreach (var pr in validPrs)
        {
            var prNumber = pr.Number;
            using (await _lockManager.AcquireLockAsync(owner, repo, prNumber, cancellationToken))
            {
                try
                {
                    // Re-fetch current PR under the lock to verify the commit hasn't advanced during heavy artifact downloading / diffing
                    var currentPr = await _github.GetPullRequestAsync(owner, repo, prNumber, cancellationToken);
                    if (currentPr == null)
                    {
                        _logger?.LogWarning("PR #{PrNumber} not found under lock in {Owner}/{Repo}; skipping", prNumber, owner, repo);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(runHeadSha) &&
                        !string.IsNullOrWhiteSpace(currentPr.Head?.Sha) &&
                        !string.Equals(runHeadSha, currentPr.Head.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation(
                            "PR #{PrNumber} head advanced from '{RunHeadSha}' to '{CurrentHeadSha}' during workflow processing; discarding outdated run result",
                            prNumber, runHeadSha, currentPr.Head.Sha);
                        continue;
                    }

                    var context = PullRequestContext.FromWorkflowRun(_github, teamName, owner, repo, currentPr, abiResult, headers, _fileCache, cancellationToken);
                    await _pipeline.ExecuteAsync(context, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (TransientErrorPolicy.IsNonRetriable(ex))
                {
                    _logger?.LogError(ex, "Non-retriable failure processing workflow run for PR #{PrNumber} in {Owner}/{Repo}",
                        prNumber, owner, repo);
                    nonRetriableFailures ??= new List<Exception>();
                    nonRetriableFailures.Add(ex);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Transient failure processing workflow run for PR #{PrNumber} in {Owner}/{Repo}; will propagate for retry.",
                        prNumber, owner, repo);
                    transientFailures ??= new List<Exception>();
                    transientFailures.Add(ex);
                }
            }
        }

        if (transientFailures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(transientFailures[0]).Throw();
        }
        else if (transientFailures is { Count: > 1 })
        {
            throw new AggregateException(
                $"One or more pull requests encountered transient errors while processing workflow run {runId} in {owner}/{repo}.",
                transientFailures);
        }
        else if (nonRetriableFailures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(nonRetriableFailures[0]).Throw();
        }
        else if (nonRetriableFailures is { Count: > 1 })
        {
            throw new AggregateException(
                $"One or more pull requests encountered non-retriable errors while processing workflow run {runId} in {owner}/{repo}.",
                nonRetriableFailures);
        }
    }

    #region Domain Parsing & Helper Methods

    /// <summary>
    /// Extracts a case-insensitive set of non-empty label names from a webhook label collection.
    /// </summary>
    public static HashSet<string> GetLabelNames(IEnumerable<Octokit.Webhooks.Models.Label>? labels)
    {
        return labels?
            .Select(label => label.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates and extracts repository owner and name from the webhook repository payload.
    /// </summary>
    public static bool TryGetRepositoryContext(
        Octokit.Webhooks.Models.Repository? repository,
        out string owner,
        out string repo)
    {
        owner = repository?.Owner?.Login ?? string.Empty;
        repo = repository?.Name ?? string.Empty;

        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    /// <summary>
    /// Checks whether the webhook event sender is present and non-empty.
    /// </summary>
    public static bool HasSender(Octokit.Webhooks.Models.User? sender) =>
        !string.IsNullOrWhiteSpace(sender?.Login);

    /// <summary>
    /// Evaluates whether a workflow name or workflow file path matches any of the configured patterns.
    /// Supports substring matching (as documented in README and configuration), filename matching, and glob wildcards (e.g. "*", "build*").
    /// Rejects unverified workflow identities (both name and path missing) unless wildcard "*" is configured.
    /// </summary>
    internal static bool IsAuthorizedWorkflow(string? workflowName, string? workflowPath, IReadOnlyList<string> configuredNames)
    {
        if (configuredNames == null || configuredNames.Count == 0)
            return false;

        if (configuredNames.Contains("*"))
            return true;

        if (string.IsNullOrWhiteSpace(workflowName) && string.IsNullOrWhiteSpace(workflowPath))
            return false; // Reject unverified workflow identities

        var pathFileName = !string.IsNullOrEmpty(workflowPath) ? Path.GetFileName(workflowPath) : null;
        var pathFileNameWithoutExt = !string.IsNullOrEmpty(workflowPath) ? Path.GetFileNameWithoutExtension(workflowPath) : null;

        foreach (var pattern in configuredNames)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var trimmedPattern = pattern.Trim();

            // Glob pattern with wildcards, e.g. "build*", "*rpm*"
            if (trimmedPattern.Contains('*'))
            {
                var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(trimmedPattern).Replace("\\*", ".*") + "$";
                var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!string.IsNullOrEmpty(workflowName) && regex.IsMatch(workflowName))
                    return true;
                if (!string.IsNullOrEmpty(pathFileName) && regex.IsMatch(pathFileName))
                    return true;
                if (!string.IsNullOrEmpty(pathFileNameWithoutExt) && regex.IsMatch(pathFileNameWithoutExt))
                    return true;
            }
            else
            {
                // Substring match (case-insensitive) against workflow name, filename, or filename without extension
                if (!string.IsNullOrEmpty(workflowName) && workflowName.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(pathFileName) && pathFileName.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(pathFileNameWithoutExt) && pathFileNameWithoutExt.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    #endregion
}

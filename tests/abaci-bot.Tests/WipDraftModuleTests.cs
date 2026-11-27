using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Xunit;
using PullRequestAction = Octokit.Webhooks.Events.PullRequest.PullRequestAction;

namespace abaci_bot.Tests;

public class WipDraftModuleTests
{
    private const string DraftMarker = GovernanceCommentFormatter.WipDraftTipMarker;
    private const string ReadyMarker = GovernanceCommentFormatter.WipReadyTipMarker;
    private const string PrUrl = "https://github.com/openRuyi-Project/openRuyi/pull/298";

    private static WipDraftModule CreateModule(
        bool enabled = true,
        bool dryRun = false,
        IBotIdentityProvider? botIdentity = null,
        bool omitIdentityProvider = false,
        string? botPat = null,
        string botLogin = "",
        RuntimeHealthState? runtimeHealth = null)
    {
        if (!omitIdentityProvider && botIdentity == null)
        {
            botIdentity = Substitute.For<IBotIdentityProvider>();
            botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot[bot]");
        }

        return new WipDraftModule(
            Microsoft.Extensions.Options.Options.Create(new WipDraftOptions { Enabled = enabled, DryRun = dryRun }),
            new GovernanceCommentFormatter(),
            botIdentity,
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                BotPat = botPat ?? string.Empty,
                BotLogin = botLogin
            }),
            runtimeHealth: runtimeHealth);
    }

    private static PullRequestContext CreateContext(
        string title,
        bool isDraft,
        PullRequestAction? prAction = null,
        bool isClosed = false,
        IReadOnlyList<IssueComment>? existingComments = null,
        IGitHubService? customGitHub = null,
        string locale = "en",
        string? previousTitle = null)
    {
        var resolvedAction = prAction ?? PullRequestAction.Edited;
        var gitHub = customGitHub ?? Substitute.For<IGitHubService>();
        if (customGitHub == null)
        {
            gitHub.GetIssueCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(existingComments ?? Array.Empty<IssueComment>());
        }

        var context = new PullRequestContext(
            gitHub,
            "captains",
            headers: null,
            PullRequestEventType.PullRequest,
            "openRuyi-Project",
            "openRuyi",
            prNumber: 298,
            headSha: "sha-298",
            baseBranch: "main",
            isDraft: isDraft,
            isMerged: isClosed ? true : null,
            title: title,
            body: "WIP mention in body should not matter",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: resolvedAction.ToString() ?? "edited",
            existingLabels: Array.Empty<string>(),
            prAction: resolvedAction,
            previousTitle: previousTitle);
        context.PreferredLocale = locale;
        return context;
    }

    private static IssueComment CreateBotComment(
        long id,
        string body,
        string login = "abaci-bot[bot]",
        AccountType accountType = AccountType.Bot)
    {
        var user = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(user, login);
        typeof(User).GetProperty(nameof(User.Type))?.SetValue(user, (AccountType?)accountType);

        var comment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(comment, id);
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(comment, body);
        typeof(IssueComment).GetProperty(nameof(IssueComment.User))?.SetValue(comment, user);
        return comment;
    }

    [Theory]
    [InlineData("WIP: add curl spec", true)]
    [InlineData("WIP-fix build", true)]
    [InlineData("WIP. fix build", true)]
    [InlineData("WIP, add feature", true)]
    [InlineData("WIP：修复构建", true)]
    [InlineData("  wip  ", true)]
    [InlineData("[WIP] add curl spec", true)]
    [InlineData("[wip] add curl spec", true)]
    [InlineData("(WIP) add curl spec", true)]
    [InlineData("feat: [WIP] refactor", true)]
    [InlineData("feat: (wip) refactor", true)]
    [InlineData("feat: add wip flag", false)]
    [InlineData("fix something wip", false)]
    [InlineData("SWIPE helper optimization", false)]
    [InlineData("WIPEOUT cleanup", false)]
    [InlineData("WIP2 experiment", false)]
    [InlineData("feat: add package", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void WipTitleHelper_IsWip_MatchesOnlyLeadingOrBracketedWipTokens(string? title, bool expected)
    {
        Assert.Equal(expected, WipTitleHelper.IsWip(title));
    }

    [Fact]
    public void ShouldProcess_FiltersByActionAndEnabledFlag()
    {
        var module = CreateModule(enabled: true);
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Opened)));
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Reopened)));
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Edited)));
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Synchronize)));
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", true, PullRequestAction.ConvertedToDraft)));
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.ReadyForReview)));
        Assert.True(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Closed, isClosed: true)));
        Assert.False(module.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Labeled)));

        var disabled = CreateModule(enabled: false);
        Assert.False(disabled.ShouldProcess(CreateContext("WIP: test", false, PullRequestAction.Opened)));
    }

    [Fact]
    public async Task Reconcile_WipTitleAndReadyPr_NoExistingTip_QueuesDraftTipCreate()
    {
        var module = CreateModule();
        var context = CreateContext("[WIP] update glibc", isDraft: false, PullRequestAction.Opened);

        await module.ProcessAsync(context, CancellationToken.None);

        var notice = Assert.Single(context.PendingNotices);
        Assert.False(notice.Delete);
        Assert.Null(notice.ExistingCommentId);
        Assert.Equal(DraftMarker, notice.Marker);
        Assert.StartsWith("Thank you for your contribution!", notice.Body, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot Review Notes", notice.Body, StringComparison.Ordinal);
        Assert.Contains("> [!TIP]", notice.Body, StringComparison.Ordinal);
        Assert.Contains("**Convert to draft**", notice.Body, StringComparison.Ordinal);
        Assert.Contains(PrUrl, notice.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", notice.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task Reconcile_WipTitleAndReadyPr_SameDraftTipExists_IsIdempotentZeroWrites()
    {
        var formatter = new GovernanceCommentFormatter();
        var expectedBody = formatter.FormatWipTip(WipTipKind.Draft, PrUrl, "en");
        var module = CreateModule();
        var context = CreateContext(
            "WIP: update glibc",
            isDraft: false,
            PullRequestAction.Synchronize,
            existingComments: new[] { CreateBotComment(101, expectedBody) });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.PendingNotices);
    }

    [Fact]
    public async Task Reconcile_WipTitleAndReadyPr_ReadyTipExists_UpdatesToDraftTip()
    {
        var formatter = new GovernanceCommentFormatter();
        var readyBody = formatter.FormatWipTip(WipTipKind.Ready, PrUrl, "en");
        var module = CreateModule();
        var context = CreateContext(
            "WIP: re-added",
            isDraft: false,
            PullRequestAction.Edited,
            existingComments: new[] { CreateBotComment(202, readyBody) });

        await module.ProcessAsync(context, CancellationToken.None);

        var notice = Assert.Single(context.PendingNotices);
        Assert.False(notice.Delete);
        Assert.Equal(202, notice.ExistingCommentId);
        Assert.Equal(DraftMarker, notice.Marker);
        Assert.Contains("**Convert to draft**", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_WipTitleAndDraftPr_DeletesExistingDraftOrReadyTips()
    {
        var formatter = new GovernanceCommentFormatter();
        var module = CreateModule();
        var existing = new[]
        {
            CreateBotComment(301, formatter.FormatWipTip(WipTipKind.Draft, PrUrl, "en")),
            CreateBotComment(302, formatter.FormatWipTip(WipTipKind.Ready, PrUrl, "en"))
        };
        var context = CreateContext("WIP: draft", isDraft: true, PullRequestAction.ConvertedToDraft, existingComments: existing);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Equal(2, context.PendingNotices.Count);
        Assert.All(context.PendingNotices, n => Assert.True(n.Delete));
        Assert.Contains(context.PendingNotices, n => n.ExistingCommentId == 301);
        Assert.Contains(context.PendingNotices, n => n.ExistingCommentId == 302);
    }

    [Fact]
    public async Task Reconcile_NoWipTitleAndDraftPr_RespectsManualDraftAndPromotesOldDraftTip()
    {
        var formatter = new GovernanceCommentFormatter();
        var module = CreateModule();

        // Manual draft with no prior bot TIP -> do nothing
        var manualDraftContext = CreateContext("Normal feature PR", isDraft: true, PullRequestAction.Opened);
        await module.ProcessAsync(manualDraftContext, CancellationToken.None);
        Assert.Empty(manualDraftContext.PendingNotices);

        // Bot previously posted draft TIP, contributor removed WIP -> update to ready TIP
        var transitionContext = CreateContext(
            "Feature PR ready now",
            isDraft: true,
            PullRequestAction.Edited,
            existingComments: new[] { CreateBotComment(401, formatter.FormatWipTip(WipTipKind.Draft, PrUrl, "en")) });
        await module.ProcessAsync(transitionContext, CancellationToken.None);

        var notice = Assert.Single(transitionContext.PendingNotices);
        Assert.False(notice.Delete);
        Assert.Equal(401, notice.ExistingCommentId);
        Assert.Equal(ReadyMarker, notice.Marker);
        Assert.Contains("**Ready for review**", notice.Body, StringComparison.Ordinal);

        // Subsequent synchronize while ready TIP is up to date -> zero writes
        var currentReady = formatter.FormatWipTip(WipTipKind.Ready, PrUrl, "en");
        var repeatContext = CreateContext(
            "Feature PR ready now",
            isDraft: true,
            PullRequestAction.Synchronize,
            existingComments: new[] { CreateBotComment(401, currentReady) });
        await module.ProcessAsync(repeatContext, CancellationToken.None);
        Assert.Empty(repeatContext.PendingNotices);
    }

    [Fact]
    public async Task Reconcile_NoWipTitleAndReadyPr_DeletesAnyExistingTip()
    {
        var formatter = new GovernanceCommentFormatter();
        var module = CreateModule();
        var context = CreateContext(
            "Clean PR",
            isDraft: false,
            PullRequestAction.ReadyForReview,
            existingComments: new[] { CreateBotComment(501, formatter.FormatWipTip(WipTipKind.Ready, PrUrl, "en")) });

        await module.ProcessAsync(context, CancellationToken.None);

        var notice = Assert.Single(context.PendingNotices);
        Assert.True(notice.Delete);
        Assert.Equal(501, notice.ExistingCommentId);
    }

    [Fact]
    public async Task Reconcile_ClosedPr_AlwaysDeletesExistingTip()
    {
        var formatter = new GovernanceCommentFormatter();
        var module = CreateModule();
        var context = CreateContext(
            "WIP: abandoned",
            isDraft: false,
            PullRequestAction.Closed,
            isClosed: true,
            existingComments: new[] { CreateBotComment(601, formatter.FormatWipTip(WipTipKind.Draft, PrUrl, "en")) });

        await module.ProcessAsync(context, CancellationToken.None);

        var notice = Assert.Single(context.PendingNotices);
        Assert.True(notice.Delete);
        Assert.Equal(601, notice.ExistingCommentId);
    }

    [Fact]
    public async Task Reconcile_IgnoresSpdxOrGovernanceCommentWithInjectedMarker()
    {
        var formatter = new GovernanceCommentFormatter();
        var module = CreateModule();

        var injectedMidBody =
            "Thank you for your contribution!\n\n## abaci-bot Review Notes\n\n" +
            "> [!TIP]\n> **SPDX License Tag Update Suggested**\n> File: `SPECS/foo/" + DraftMarker + ".spec`";

        var realReadyTip = formatter.FormatWipTip(WipTipKind.Ready, PrUrl, "en");

        var contextMid = CreateContext(
            "WIP: test injection",
            isDraft: false,
            PullRequestAction.Edited,
            existingComments: new[]
            {
                CreateBotComment(10, injectedMidBody),
                CreateBotComment(11, realReadyTip)
            });

        await module.ProcessAsync(contextMid, CancellationToken.None);

        var noticeMid = Assert.Single(contextMid.PendingNotices);
        Assert.Equal(11, noticeMid.ExistingCommentId);
        Assert.False(noticeMid.Delete);
        Assert.DoesNotContain(contextMid.PendingNotices, n => n.ExistingCommentId == 10);

        // Trailing injected marker inside governance comment must also never be treated as a TIP
        var injectedTrailing =
            "Thank you for your contribution!\n\n## abaci-bot Review Notes\n\n" +
            "> [!TIP]\n> **SPDX License Tag Update Suggested**\n" + DraftMarker;

        var contextTrailing = CreateContext(
            "WIP: trailing injection",
            isDraft: false,
            PullRequestAction.Opened,
            existingComments: new[] { CreateBotComment(10, injectedTrailing) });

        await module.ProcessAsync(contextTrailing, CancellationToken.None);

        var createdNotice = Assert.Single(contextTrailing.PendingNotices);
        Assert.Null(createdNotice.ExistingCommentId);
        Assert.False(createdNotice.Delete);
        Assert.DoesNotContain(contextTrailing.PendingNotices, n => n.ExistingCommentId == 10);
    }

    [Fact]
    public async Task Reconcile_WhenBotIdentityUnavailable_FailsClosedAndIgnoresOtherBots()
    {
        var formatter = new GovernanceCommentFormatter();
        var health = new RuntimeHealthState();
        var module = CreateModule(omitIdentityProvider: true, botPat: null, runtimeHealth: health);

        var thirdPartyTip = CreateBotComment(
            702,
            formatter.FormatWipTip(WipTipKind.Draft, PrUrl, "en"),
            login: "dependabot[bot]",
            accountType: AccountType.Bot);

        var context = CreateContext(
            "Clean manual draft PR",
            isDraft: true,
            PullRequestAction.Edited,
            existingComments: new[] { thirdPartyTip });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.PendingNotices);
        Assert.Contains("Own bot identity unresolved", health.GetLastError().LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_IgnoresHumanOrOtherBotMarkers_AndHonorsDryRun()
    {
        var formatter = new GovernanceCommentFormatter();
        var validTipBody = formatter.FormatWipTip(WipTipKind.Draft, PrUrl, "en");

        var comments = new[]
        {
            CreateBotComment(701, validTipBody, login: "human-user", accountType: AccountType.User),
            CreateBotComment(702, validTipBody, login: "dependabot[bot]", accountType: AccountType.Bot)
        };

        var module = CreateModule(dryRun: false);
        var context = CreateContext("Clean PR", isDraft: false, PullRequestAction.Edited, existingComments: comments);
        await module.ProcessAsync(context, CancellationToken.None);
        Assert.Empty(context.PendingNotices);

        var dryRunModule = CreateModule(dryRun: true);
        var dryContext = CreateContext("WIP: dry run", isDraft: false, PullRequestAction.Opened);
        await dryRunModule.ProcessAsync(dryContext, CancellationToken.None);
        Assert.Empty(dryContext.PendingNotices);
    }

    [Fact]
    public async Task Reconcile_CleanSynchronize_SkipsIssueCommentsFetch()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var module = CreateModule();
        var context = CreateContext("Normal ready PR", isDraft: false, PullRequestAction.Synchronize, customGitHub: gitHub);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.PendingNotices);
        await gitHub.DidNotReceive().GetIssueCommentsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WipDraftModule_WithRealPipeline_ReconcilesCreateUpdateDeleteAndSecondRunNoOp()
    {
        var gitHub = Substitute.For<IGitHubService, IEphemeralCommentGateway>();
        var identity = Substitute.For<IGitHubService, IBotIdentityProvider>();
        var botIdentity = Substitute.For<IBotIdentityProvider>();
        botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot[bot]");

        var openPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(openPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());
        gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<CancellationToken>())
            .Returns(openPr);

        var liveComments = new List<IssueComment>();
        long nextId = 1000;
        gitHub.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<CancellationToken>())
            .Returns(_ => liveComments.ToList());
        gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var created = CreateBotComment(++nextId, ci.ArgAt<string>(3));
                liveComments.Add(created);
                return Task.FromResult(created);
            });
        gitHub.UpdateCommentAsync("openRuyi-Project", "openRuyi", Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<long>(2);
                var body = ci.ArgAt<string>(3);
                liveComments.RemoveAll(c => c.Id == id);
                liveComments.Add(CreateBotComment(id, body));
                return Task.CompletedTask;
            });
        ((IEphemeralCommentGateway)gitHub).DeleteCommentAsync("openRuyi-Project", "openRuyi", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<long>(2);
                liveComments.RemoveAll(c => c.Id == id);
                return Task.CompletedTask;
            });

        var module = CreateModule(botIdentity: botIdentity);
        var pipeline = new PullRequestPipeline(
            new[] { module },
            new LabelMutexEngine(),
            gitHub,
            commentGateway: (IEphemeralCommentGateway)gitHub);

        // 1. WIP ready -> creates draft TIP
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(openPr, "WIP: feature");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(openPr, false);
        await pipeline.ExecuteAsync(
            CreateContext("WIP: feature", isDraft: false, PullRequestAction.Opened, customGitHub: gitHub),
            CancellationToken.None);
        Assert.Single(liveComments);
        Assert.Contains(DraftMarker, liveComments[0].Body, StringComparison.Ordinal);

        // 2. WIP removed while draft -> updates same comment to ready TIP
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(openPr, "feature ready");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(openPr, true);
        await pipeline.ExecuteAsync(
            CreateContext("feature ready", isDraft: true, PullRequestAction.Edited, customGitHub: gitHub),
            CancellationToken.None);
        Assert.Single(liveComments);
        Assert.Contains(ReadyMarker, liveComments[0].Body, StringComparison.Ordinal);

        // 3. Marked ready for review -> deletes TIP
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(openPr, false);
        await pipeline.ExecuteAsync(
            CreateContext("feature ready", isDraft: false, PullRequestAction.ReadyForReview, customGitHub: gitHub),
            CancellationToken.None);
        Assert.Empty(liveComments);

        // 4. Repeat ready -> zero writes
        await gitHub.Received(1).CreateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHub.Received(1).UpdateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ((IEphemeralCommentGateway)gitHub).Received(1).DeleteCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());

        await pipeline.ExecuteAsync(
            CreateContext("feature ready", isDraft: false, PullRequestAction.Edited, customGitHub: gitHub),
            CancellationToken.None);
        await gitHub.Received(1).CreateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pipeline_FlushesNoticesCreateUpdateDelete_AndHandles404AndBodyWipSilence()
    {
        var gitHub = Substitute.For<IGitHubService, IEphemeralCommentGateway>();
        var gateway = (IEphemeralCommentGateway)gitHub;
        var openPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(openPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(openPr, false);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(openPr, "WIP: test");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());
        gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<CancellationToken>())
            .Returns(openPr);

        // Non-WIP title with WIP in body must produce zero notices
        var bodyOnlyWipContext = CreateContext("Fix parser bug", isDraft: false, PullRequestAction.Opened);
        var module = CreateModule();
        await module.ProcessAsync(bodyOnlyWipContext, CancellationToken.None);
        Assert.Empty(bodyOnlyWipContext.PendingNotices);

        // Full pipeline flush: create + update(404 ignored) + delete(404 ignored)
        gateway.DeleteCommentAsync("openRuyi-Project", "openRuyi", 902, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new NotFoundException("gone", System.Net.HttpStatusCode.NotFound));
        gitHub.UpdateCommentAsync("openRuyi-Project", "openRuyi", 901, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new NotFoundException("gone", System.Net.HttpStatusCode.NotFound));

        var stubModule = Substitute.For<IPullRequestModule>();
        stubModule.Priority.Returns(22);
        stubModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        stubModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.PendingNotices.Add(new NoticeIntent(DraftMarker, "Create tip"));
                ctx.PendingNotices.Add(new NoticeIntent(ReadyMarker, "Update tip", ExistingCommentId: 901));
                ctx.PendingNotices.Add(new NoticeIntent(DraftMarker, null, ExistingCommentId: 902, Delete: true));
            });

        var health = new RuntimeHealthState();
        var pipeline = new PullRequestPipeline(
            new[] { stubModule },
            new LabelMutexEngine(),
            gitHub,
            runtimeHealth: health,
            commentGateway: gateway);

        var pipelineContext = new PullRequestContext(
            gitHub,
            "captains",
            headers: null,
            PullRequestEventType.PullRequest,
            "openRuyi-Project",
            "openRuyi",
            298,
            "sha-298",
            "main",
            false,
            null,
            "WIP: test",
            null,
            "alice",
            "alice",
            "opened",
            Array.Empty<string>(),
            prAction: PullRequestAction.Opened);

        await pipeline.ExecuteAsync(pipelineContext, CancellationToken.None);

        await gitHub.Received(1).CreateCommentAsync("openRuyi-Project", "openRuyi", 298, "Create tip", Arg.Any<CancellationToken>());
        await gitHub.Received(1).UpdateCommentAsync("openRuyi-Project", "openRuyi", 901, "Update tip", Arg.Any<CancellationToken>());
        await gateway.Received(1).DeleteCommentAsync("openRuyi-Project", "openRuyi", 902, Arg.Any<CancellationToken>());
        Assert.Empty(pipelineContext.PendingNotices);
        Assert.Null(health.GetLastError().LastError);
    }

    [Fact]
    public async Task TryParseTipKind_AllowsSpecialDocumentationLinkTitles_AndRejectsSpoofedSpdxTip()
    {
        var formatter = new GovernanceCommentFormatter();
        var specialLinks = new List<DocumentationLink>
        {
            new() { Title = "PR Review Notes", TitleZh = "审查说明详情", Url = "https://docs.openruyi.org/review-notes" },
            new() { Title = "Docs <!-- note -->", Url = "https://docs.openruyi.org/html-note" },
            new() { Title = "Docs ```fence```", Url = "https://docs.openruyi.org/fence" }
        };

        foreach (var locale in new[] { "en", "zh-CN" })
        {
            var draftTip = formatter.FormatWipTip(WipTipKind.Draft, PrUrl, locale, specialLinks);
            Assert.Contains(locale == "en" ? "For more details, see:" : "更多参考文档：", draftTip, StringComparison.Ordinal);
            Assert.True(WipDraftModule.TryParseTipKind(draftTip, out var parsedDraft));
            Assert.Equal(WipTipKind.Draft, parsedDraft);

            var readyTip = formatter.FormatWipTip(WipTipKind.Ready, PrUrl, locale, specialLinks);
            Assert.Contains(locale == "en" ? "For more details, see:" : "更多参考文档：", readyTip, StringComparison.Ordinal);
            Assert.True(WipDraftModule.TryParseTipKind(readyTip, out var parsedReady));
            Assert.Equal(WipTipKind.Ready, parsedReady);
        }

        // SPDX TIP with an appended WIP marker must still be rejected by template lead-line check
        var spoofedSpdxTip = "> [!TIP]\n> **SPDX License Tag Update Suggested**\n> - Foo\n" + DraftMarker;
        Assert.False(WipDraftModule.TryParseTipKind(spoofedSpdxTip, out _));

        // Consecutive synchronizes with "PR Review Notes" DocumentationLink must NOT duplicate TIP comment
        var createdTips = new List<string>();
        var gitHub = Substitute.For<IGitHubService>();
        var openPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(openPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(openPr, "WIP: add feature");
        gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<CancellationToken>()).Returns(openPr);
        gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                createdTips.Add(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var options = Microsoft.Extensions.Options.Options.Create(new WipDraftOptions
        {
            Enabled = true,
            DryRun = false,
            DocumentationLinks = specialLinks
        });
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot[bot]");
        var realModule = new WipDraftModule(options, formatter, identity);
        var pipeline = new PullRequestPipeline(new[] { realModule }, new LabelMutexEngine(), gitHub);

        var run1Comments = Array.Empty<IssueComment>();
        gitHub.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 298, Arg.Any<CancellationToken>())
            .Returns(_ => run1Comments.Length == 0
                ? Array.Empty<IssueComment>()
                : new[] { CreateBotComment(701, createdTips[0]) });

        var ctx1 = CreateContext("WIP: add feature", isDraft: false, PullRequestAction.Opened);
        await pipeline.ExecuteAsync(new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "openRuyi-Project", "openRuyi",
            298, "sha-1", "main", false, false, "WIP: add feature", null, "alice", "alice", "opened",
            Array.Empty<string>(), prAction: PullRequestAction.Opened), CancellationToken.None);

        Assert.Single(createdTips);
        run1Comments = new[] { CreateBotComment(701, createdTips[0]) };

        await pipeline.ExecuteAsync(new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "openRuyi-Project", "openRuyi",
            298, "sha-2", "main", false, false, "WIP: add feature", null, "alice", "alice", "synchronize",
            Array.Empty<string>(), prAction: PullRequestAction.Synchronize), CancellationToken.None);

        Assert.Single(createdTips);
        await gitHub.DidNotReceive().UpdateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_ConvertToDraftThenRemoveWipTitle_PostsReadyTip_ThenReadyForReviewDeletesTip()
    {
        var botState = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
        {
            GeneralDocumentationLinks = new List<DocumentationLink>
            {
                new() { Title = "Contributor Guide", TitleZh = "贡献指南", Url = "https://docs.openruyi.org/contributing" }
            }
        });
        var formatter = new GovernanceCommentFormatter(botState);
        var wipOptions = Microsoft.Extensions.Options.Options.Create(new WipDraftOptions
        {
            Enabled = true,
            DryRun = false,
            DocumentationLinks = new List<DocumentationLink>
            {
                new() { Title = "Draft PR Guide", TitleZh = "草稿规范", Url = "https://docs.openruyi.org/draft" }
            }
        });
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot-test[bot]");
        var module = new WipDraftModule(wipOptions, formatter, identity);

        // 1. Open WIP ready PR -> posts framed Draft TIP using live bot display name & merged footer links
        var ctx1 = CreateContext("WIP: feat", isDraft: false, PullRequestAction.Opened);
        await module.ProcessAsync(ctx1, CancellationToken.None);

        var draftNotice = Assert.Single(ctx1.PendingNotices);
        Assert.False(draftNotice.Delete);
        Assert.Equal(DraftMarker, draftNotice.Marker);
        Assert.NotNull(draftNotice.Body);
        Assert.StartsWith("Thank you for your contribution!", draftNotice.Body, StringComparison.Ordinal);
        Assert.Contains("## abaci-bot-test Review Notes", draftNotice.Body, StringComparison.Ordinal);
        Assert.Contains("> [!TIP]", draftNotice.Body, StringComparison.Ordinal);
        Assert.Contains("For more details, see:\n- [Draft PR Guide](https://docs.openruyi.org/draft)\n- [Contributor Guide](https://docs.openruyi.org/contributing)", draftNotice.Body.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(WipDraftModule.TryParseTipKind(draftNotice.Body, out var parsedDraft));
        Assert.Equal(WipTipKind.Draft, parsedDraft);

        // 2. Contributor clicks "Convert to draft" while title still has WIP -> deletes Draft TIP
        var ctx2 = CreateContext(
            "WIP: feat",
            isDraft: true,
            PullRequestAction.ConvertedToDraft,
            existingComments: new[] { CreateBotComment(801, draftNotice.Body, login: "abaci-bot-test[bot]") });
        await module.ProcessAsync(ctx2, CancellationToken.None);

        var deleteDraftNotice = Assert.Single(ctx2.PendingNotices);
        Assert.True(deleteDraftNotice.Delete);
        Assert.Equal(801L, deleteDraftNotice.ExistingCommentId);

        // 3. Contributor removes "WIP: " from title while still in draft -> creates Ready TIP
        var ctx3 = CreateContext(
            "feat",
            isDraft: true,
            PullRequestAction.Edited,
            existingComments: Array.Empty<IssueComment>(),
            previousTitle: "WIP: feat");
        await module.ProcessAsync(ctx3, CancellationToken.None);

        var readyNotice = Assert.Single(ctx3.PendingNotices);
        Assert.False(readyNotice.Delete);
        Assert.Null(readyNotice.ExistingCommentId);
        Assert.Equal(ReadyMarker, readyNotice.Marker);
        Assert.NotNull(readyNotice.Body);
        Assert.Contains("**Ready for review**", readyNotice.Body, StringComparison.Ordinal);
        Assert.True(WipDraftModule.TryParseTipKind(readyNotice.Body, out var parsedReady));
        Assert.Equal(WipTipKind.Ready, parsedReady);

        // 4. Control: manual draft renamed without prior WIP title -> stays untouched
        var manualDraftEditCtx = CreateContext(
            "feat v2",
            isDraft: true,
            PullRequestAction.Edited,
            existingComments: Array.Empty<IssueComment>(),
            previousTitle: "Initial design");
        await module.ProcessAsync(manualDraftEditCtx, CancellationToken.None);
        Assert.Empty(manualDraftEditCtx.PendingNotices);

        // 5. Contributor clicks "Ready for review" -> deletes Ready TIP
        var ctx5 = CreateContext(
            "feat",
            isDraft: false,
            PullRequestAction.ReadyForReview,
            existingComments: new[] { CreateBotComment(802, readyNotice.Body, login: "abaci-bot-test[bot]") });
        await module.ProcessAsync(ctx5, CancellationToken.None);

        var deleteReadyNotice = Assert.Single(ctx5.PendingNotices);
        Assert.True(deleteReadyNotice.Delete);
        Assert.Equal(802L, deleteReadyNotice.ExistingCommentId);
    }

    [Fact]
    public void TryParseTipKind_RejectsFramedConsolidatedCommentWithOtherSectionAndWipMarker()
    {
        var consolidatedWithOtherSection = string.Join(
            "\n",
            "Thank you for your contribution! We have identified a few points that require your attention:",
            string.Empty,
            "## abaci-bot Review Notes",
            string.Empty,
            "### CVE Vulnerability Breakdown",
            "- CVE-2026-0001 detected in sample spec",
            string.Empty,
            "> [!TIP]",
            "> This pull request title contains `WIP`. If it is not ready for review, use **Convert to draft** under the pull request description.",
            ">",
            $"> [Open the pull request]({PrUrl})",
            string.Empty,
            DraftMarker);

        Assert.False(WipDraftModule.TryParseTipKind(consolidatedWithOtherSection, out _));
    }
}

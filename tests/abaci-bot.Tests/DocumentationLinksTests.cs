using System.Runtime.CompilerServices;
using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;
using PullRequest = Octokit.PullRequest;

namespace abaci_bot.Tests;

public class DocumentationLinksTests
{
    private static readonly DocumentationLink SpdxLink = new()
    {
        Title = "SPDX Guide",
        TitleZh = "SPDX 指南",
        Url = "https://docs.openruyi.org/spdx"
    };

    private static readonly DocumentationLink AbiLink = new()
    {
        Title = "ABI Guide",
        TitleZh = "ABI 指南",
        Url = "https://docs.openruyi.org/abi"
    };

    private static readonly DocumentationLink GeneralLink = new()
    {
        Title = "Contribution Guide",
        TitleZh = "贡献指南",
        Url = "https://docs.openruyi.org/contributing"
    };

    [Fact]
    public void DocumentationLinkFormatter_AppendFooterAndBeforeMarker_FormatsAndDeduplicates()
    {
        Assert.Equal("Base body", DocumentationLinkFormatter.AppendFooter("Base body", Array.Empty<DocumentationLink>(), false));
        Assert.Equal("Base body\n\n<!-- m -->", DocumentationLinkFormatter.AppendFooterBeforeMarker("Base body\n\n<!-- m -->", "<!-- m -->", null, false));

        var appended = DocumentationLinkFormatter.AppendFooter("Line 1  ", new[] { SpdxLink, GeneralLink }, isChinese: false)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(
            "Line 1\n\nFor more details, see:\n- [SPDX Guide](https://docs.openruyi.org/spdx)\n- [Contribution Guide](https://docs.openruyi.org/contributing)",
            appended);

        var withMarker = DocumentationLinkFormatter.AppendFooterBeforeMarker(
                "> Notice\n\n<!-- marker -->",
                "<!-- marker -->",
                new[] { SpdxLink },
                isChinese: true)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(
            "> Notice\n\n更多参考文档：\n- [SPDX 指南](https://docs.openruyi.org/spdx)\n\n<!-- marker -->",
            withMarker);

        var withoutMarkerFallback = DocumentationLinkFormatter.AppendFooterBeforeMarker(
                "> Notice",
                "<!-- missing -->",
                new[] { SpdxLink },
                isChinese: false)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.EndsWith("- [SPDX Guide](https://docs.openruyi.org/spdx)", withoutMarkerFallback, StringComparison.Ordinal);

        var onlyFooter = DocumentationLinkFormatter.AppendFooter(string.Empty, new[] { SpdxLink }, isChinese: false)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal("For more details, see:\n- [SPDX Guide](https://docs.openruyi.org/spdx)", onlyFooter);
    }

    [Fact]
    public void FormatConsolidatedFooter_CombinesModuleAndGeneralLinksDynamically_AndDeduplicates()
    {
        var duplicateGeneralUrl = new DocumentationLink
        {
            Title = "Duplicate Contributing",
            Url = "https://docs.openruyi.org/contributing"
        };
        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                GeneralDocumentationLinks = new List<DocumentationLink> { GeneralLink }
            }));

        // Module links + General links -> merged & deduplicated by exact trimmed URL
        var withGeneral = formatter.FormatConsolidatedFooter(
                "zh-CN",
                moduleDocumentationLinks: new[] { SpdxLink, duplicateGeneralUrl },
                includeGeneralDocs: true)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(
            "更多参考文档：\n- [SPDX 指南](https://docs.openruyi.org/spdx)\n- [Duplicate Contributing](https://docs.openruyi.org/contributing)",
            withGeneral);

        // includeGeneralDocs: false -> module links only, General links omitted
        var moduleOnly = formatter.FormatConsolidatedFooter(
            "en",
            moduleDocumentationLinks: new[] { AbiLink },
            includeGeneralDocs: false);
        Assert.Contains("[ABI Guide](https://docs.openruyi.org/abi)", moduleOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("contributing", moduleOnly, StringComparison.OrdinalIgnoreCase);

        // No module links and includeGeneralDocs: false -> empty
        Assert.Equal(string.Empty, formatter.FormatConsolidatedFooter("en", Array.Empty<DocumentationLink>(), includeGeneralDocs: false));
    }

    [Fact]
    public async Task PullRequestPipeline_ConsolidatedFooter_IncludesOnlyTriggeredModuleLinksPlusGeneralLinks()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var openPr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(openPr, new StringEnum<ItemState>(ItemState.Open));
        gitHub.GetPullRequestAsync("owner", "repo", 12, Arg.Any<CancellationToken>()).Returns(openPr);

        string? postedComment = null;
        gitHub.CreateCommentAsync("owner", "repo", 12, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                postedComment = ci.ArgAt<string>(3);
                return Task.CompletedTask;
            });

        var spdxModule = Substitute.For<IPullRequestModule>();
        spdxModule.Priority.Returns(60);
        spdxModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        spdxModule.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<PullRequestContext>().AddPendingComment(
                "> [!TIP]\n> SPDX warning",
                includeGeneralDocs: true,
                documentationLinks: new[] { SpdxLink }));

        var abiModule = Substitute.For<IPullRequestModule>();
        abiModule.Priority.Returns(85);
        abiModule.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(false);

        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                GeneralDocumentationLinks = new List<DocumentationLink> { GeneralLink }
            }));

        var pipeline = new PullRequestPipeline(
            new[] { spdxModule, abiModule },
            new LabelMutexEngine(),
            gitHub,
            formatter: formatter);

        var context = new PullRequestContext(
            gitHub,
            "captains",
            headers: null,
            PullRequestEventType.PullRequest,
            "owner",
            "repo",
            12,
            "sha-12",
            "main",
            isDraft: false,
            isMerged: false,
            title: "Test",
            body: null,
            senderLogin: "author",
            authorLogin: "author",
            action: "opened",
            existingLabels: Array.Empty<string>(),
            prAction: PullRequestAction.Opened);

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(postedComment);
        Assert.Contains("[SPDX Guide](https://docs.openruyi.org/spdx)", postedComment, StringComparison.Ordinal);
        Assert.Contains("[Contribution Guide](https://docs.openruyi.org/contributing)", postedComment, StringComparison.Ordinal);
        Assert.DoesNotContain(AbiLink.Url, postedComment, StringComparison.Ordinal);
    }

    [Fact]
    public void Validators_RejectInvalidDocumentationLinksAcrossAllModulesAndBotState()
    {
        var badLinks = new List<DocumentationLink>
        {
            new() { Title = "Bad", Url = "   " }
        };

        Assert.True(new BotStateOptionsValidator().Validate(null, new BotStateOptions { GeneralDocumentationLinks = badLinks }).Failed);
        Assert.True(new StaleLifecycleOptionsValidator().Validate(null, new StaleLifecycleOptions { DocumentationLinks = badLinks }).Failed);
        Assert.True(new CveActiveWatchOptionsValidator().Validate(null, new CveActiveWatchOptions { DocumentationLinks = badLinks }).Failed);
        Assert.True(new ConversationGuardOptionsValidator().Validate(null, new ConversationGuardOptions { DocumentationLinks = badLinks }).Failed);
        Assert.True(new PackageUpdateOptionsValidator().Validate(null, new PackageUpdateOptions { DocumentationLinks = badLinks }).Failed);

        var spdxValidator = new DocumentationLinksOptionsValidator<SpdxComplianceOptions>(o => o.DocumentationLinks);
        var abiValidator = new DocumentationLinksOptionsValidator<AbiComplianceOptions>(o => o.DocumentationLinks);
        var cveValidator = new DocumentationLinksOptionsValidator<CveSecurityOptions>(o => o.DocumentationLinks);
        var wipValidator = new DocumentationLinksOptionsValidator<WipDraftOptions>(o => o.DocumentationLinks);

        Assert.True(spdxValidator.Validate(null, new SpdxComplianceOptions()).Succeeded);
        Assert.True(spdxValidator.Validate(null, new SpdxComplianceOptions { DocumentationLinks = badLinks }).Failed);
        Assert.True(abiValidator.Validate(null, new AbiComplianceOptions { DocumentationLinks = badLinks }).Failed);
        Assert.True(cveValidator.Validate(null, new CveSecurityOptions { DocumentationLinks = badLinks }).Failed);
        Assert.True(wipValidator.Validate(null, new WipDraftOptions { DocumentationLinks = badLinks }).Failed);
    }

    [Fact]
    public void FormatWipTip_And_StaleLifecycleOptions_RenderConfiguredDocumentationLinks()
    {
        var formatter = new GovernanceCommentFormatter();
        var links = new List<DocumentationLink> { SpdxLink };

        var tipEn = formatter.FormatWipTip(WipTipKind.Draft, "https://github.com/owner/repo/pull/1", "en", links);
        Assert.Contains("> **See also**:", tipEn, StringComparison.Ordinal);
        Assert.Contains("> - [SPDX Guide](https://docs.openruyi.org/spdx)", tipEn, StringComparison.Ordinal);
        Assert.True(WipDraftModule.TryParseTipKind(tipEn, out var parsedKind));
        Assert.Equal(WipTipKind.Draft, parsedKind);

        var tipZh = formatter.FormatWipTip(WipTipKind.Ready, "https://github.com/owner/repo/pull/1", "zh-CN", links);
        Assert.Contains("> **另请参阅**：", tipZh, StringComparison.Ordinal);
        Assert.Contains("> - [SPDX 指南](https://docs.openruyi.org/spdx)", tipZh, StringComparison.Ordinal);
        Assert.True(WipDraftModule.TryParseTipKind(tipZh, out var parsedReadyKind));
        Assert.Equal(WipTipKind.Ready, parsedReadyKind);

        var staleOpts = new StaleLifecycleOptions { DocumentationLinks = links };
        Assert.Contains(
            "For more details, see:\n- [SPDX Guide](https://docs.openruyi.org/spdx)",
            staleOpts.FormatStaleComment().Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "For more details, see:\n- [SPDX Guide](https://docs.openruyi.org/spdx)",
            staleOpts.FormatCloseComment().Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CveActiveWatchAndConversationGuard_AppendConfiguredDocumentationLinks()
    {
        // 1. CveActiveWatch auto-close resolution comment includes DocumentationLinks
        var gitHubCve = Substitute.For<IGitHubService>();
        gitHubCve.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        var existingIssue = (Issue)RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 99);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in curl");
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(existingIssue, "<!-- abaci-cve-package: curl -->");
        gitHubCve.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        var scanner = Substitute.For<IPackageVulnerabilityScanner>();
        scanner.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "curl" }));

        var cveOpts = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            DocumentationLinks = new List<DocumentationLink> { AbiLink }
        });
        var watchService = new CveActiveWatchService(
            gitHubCve,
            scanner,
            cveOpts,
            catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.0") }));

        await watchService.RunScanSweepAsync();

        await gitHubCve.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            99,
            Arg.Is<string>(b => b.Contains("[ABI Guide](https://docs.openruyi.org/abi)")),
            Arg.Any<CancellationToken>());

        // 2. ConversationGuard non-ephemeral reply appends DocumentationLinks, ephemeral reply does not
        var gitHubGuard = Substitute.For<IGitHubService>();
        var perm = Substitute.For<IRepositoryPermissionService>();
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");
        var ephemeral = Substitute.For<IEphemeralReplyService>();

        var pr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        gitHubGuard.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>()).Returns(pr);

        perm.GetUserPermissionAsync("owner", "repo", "maintainer", Arg.Any<CancellationToken>()).Returns(RepositoryAccessLevel.Maintain);
        perm.GetUserPermissionAsync("owner", "repo", "outsider", Arg.Any<CancellationToken>()).Returns(RepositoryAccessLevel.Read);

        var guardService = new ConversationGuardService(
            gitHubGuard,
            perm,
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
            {
                Enabled = true,
                DryRun = false,
                RepositoryAllowList = new List<string> { "owner/repo" },
                DocumentationLinks = new List<DocumentationLink> { SpdxLink }
            }),
            botIdentity: identity,
            ephemeralReplies: ephemeral);

        var ctxMaintainer = new PullRequestContext(
            gitHubGuard, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42,
            "sha", "main", false, false, "Title", null, "maintainer", "author", "created", Array.Empty<string>());
        await guardService.HandleCommandAsync(ctxMaintainer, "@abaci-bot unlock", "maintainer");

        await gitHubGuard.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(b => b.Contains("This conversation is not currently locked.") &&
                                b.Contains("[SPDX Guide](https://docs.openruyi.org/spdx)")),
            Arg.Any<CancellationToken>());

        var ctxOutsider = new PullRequestContext(
            gitHubGuard, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42,
            "sha", "main", false, false, "Title", null, "outsider", "author", "created", Array.Empty<string>());
        await guardService.HandleCommandAsync(ctxOutsider, "@abaci-bot unlock", "outsider");

        await ephemeral.Received(1).PostEphemeralReplyAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(b => !b.Contains("https://docs.openruyi.org/spdx")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RealModules_ConsolidatedFooter_DeduplicatesBodyUrlsAndCombinesGeneralLinks()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var openPr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(openPr, new StringEnum<ItemState>(ItemState.Open));
        gitHub.GetPullRequestAsync("owner", "repo", 55, Arg.Any<CancellationToken>()).Returns(openPr);

        var specFile = (PullRequestFile)RuntimeHelpers.GetUninitializedObject(typeof(PullRequestFile));
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.FileName))?.SetValue(specFile, "SPECS/demo/demo.spec");
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Status))?.SetValue(specFile, "modified");
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Sha))?.SetValue(specFile, "blob-1");
        gitHub.GetPullRequestFilesAsync("owner", "repo", 55, Arg.Any<CancellationToken>()).Returns(new[] { specFile });
        gitHub.GetFileContentAsync("owner", "repo", "SPECS/demo/demo.spec", "sha-55", Arg.Any<CancellationToken>())
            .Returns("Name: demo\nVersion: 1.0\nLicense: CustomUnknown\n");

        string? postedComment = null;
        gitHub.CreateCommentAsync("owner", "repo", 55, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                postedComment = ci.ArgAt<string>(3);
                return Task.CompletedTask;
            });

        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                GeneralDocumentationLinks = new List<DocumentationLink> { GeneralLink }
            }));

        var spdxOptions = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            DocumentationLinks = new List<DocumentationLink>
            {
                // Already inline in SPDX body -> must be excluded from footer
                new() { Title = "SPDX Inline List", Url = "https://spdx.org/licenses/" },
                SpdxLink
            }
        });
        var spdxService = new SpdxLicenseService(
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            spdxOptions);
        var spdxModule = new SpdxComplianceModule(spdxService, spdxOptions, formatter);

        var abiOptions = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            DocumentationLinks = new List<DocumentationLink> { AbiLink }
        });
        var abiModule = new AbiComplianceModule(Substitute.For<IAbiDiffService>(), abiOptions, formatter);

        var rebuildOptions = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            DocumentationLinks = new List<DocumentationLink>
            {
                new() { Title = "Rebuild Un-triggered", Url = "https://docs.openruyi.org/rebuild" }
            }
        });
        var rebuildDeps = Substitute.For<IRpmDependencyService>();
        rebuildDeps.IsMetadataLoaded.Returns(false);
        var rebuildModule = new RebuildImpactModule(
            rebuildDeps,
            rebuildOptions,
            new ManualGuideRebuildVerificationOrchestrator(formatter, rebuildOptions));

        var pipeline = new PullRequestPipeline(
            new IPullRequestModule[] { spdxModule, rebuildModule, abiModule },
            new LabelMutexEngine(),
            gitHub,
            formatter: formatter);

        var ctx = new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "owner", "repo", 55,
            "sha-55", "main", false, false, "Update demo", null, "alice", "alice", "opened",
            Array.Empty<string>(), prAction: PullRequestAction.Opened);
        ctx.AbiResult = new AbiDiffResult(true, true, true, "Removed sym_foo()");

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(postedComment);
        // Inline SPDX body URL appears once total and not repeated in footer
        Assert.Equal(1, postedComment.Split("https://spdx.org/licenses/", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("SPDX Inline List", postedComment, StringComparison.Ordinal);
        // Triggered modules' links + general link appear once, un-triggered rebuild link absent
        Assert.Equal(1, postedComment.Split(SpdxLink.Url, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, postedComment.Split(AbiLink.Url, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, postedComment.Split(GeneralLink.Url, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("https://docs.openruyi.org/rebuild", postedComment, StringComparison.Ordinal);
        Assert.True(
            postedComment.IndexOf("Removed sym_foo()", StringComparison.Ordinal) <
            postedComment.IndexOf("For more details, see:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PullRequestPipeline_CarryoverAndDuplicateBodies_MergeSymmetricallyAndPreserveLinks()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var openPr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(openPr, Array.Empty<Label>());
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(openPr, new StringEnum<ItemState>(ItemState.Open));
        gitHub.GetPullRequestAsync("owner", "repo", 78, Arg.Any<CancellationToken>()).Returns(openPr);

        var postedBodies = new List<string>();
        var failNext = false;
        gitHub.CreateCommentAsync("owner", "repo", 78, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (failNext)
                {
                    return Task.FromException(new System.Net.Http.HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable));
                }

                postedBodies.Add(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var formatter = new GovernanceCommentFormatter(
            Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                GeneralDocumentationLinks = new List<DocumentationLink> { GeneralLink }
            }));

        const string dupBody = "> [!TIP]\n> Shared warning section";
        var moduleA = Substitute.For<IPullRequestModule>();
        moduleA.Priority.Returns(10);
        moduleA.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        var emitFullMetadata = true;
        moduleA.When(m => m.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                if (emitFullMetadata)
                {
                    ci.Arg<PullRequestContext>().AddPendingComment(dupBody, includeGeneralDocs: false);
                    ci.Arg<PullRequestContext>().AddPendingComment(dupBody, includeGeneralDocs: true, documentationLinks: new[] { SpdxLink });
                }
                else
                {
                    ci.Arg<PullRequestContext>().AddPendingComment(dupBody, includeGeneralDocs: false, documentationLinks: null);
                }
            });

        var pipeline = new PullRequestPipeline(
            new[] { moduleA },
            new LabelMutexEngine(),
            gitHub,
            formatter: formatter,
            commentRetryDelayAsync: (_, _) => Task.CompletedTask);

        // Case 1: without carryover, duplicate same-body sections collapse to 1 while preserving merged links/flag
        await pipeline.ExecuteAsync(new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "owner", "repo", 78,
            "sha-dup", "main", false, false, "T", null, "a", "a", "opened",
            Array.Empty<string>(), prAction: PullRequestAction.Opened), CancellationToken.None);

        var nonCarryoverMsg = Assert.Single(postedBodies);
        Assert.Equal(1, nonCarryoverMsg.Split("Shared warning section", StringSplitOptions.None).Length - 1);
        Assert.Contains(SpdxLink.Url, nonCarryoverMsg, StringComparison.Ordinal);
        Assert.Contains(GeneralLink.Url, nonCarryoverMsg, StringComparison.Ordinal);

        // Case 2: carried section with IncludeGeneralDocs=true + SpdxLink survives when retry re-emits same body with false/null
        postedBodies.Clear();
        failNext = true;
        await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "owner", "repo", 78,
            "sha-carry", "main", false, false, "T", null, "a", "a", "opened",
            Array.Empty<string>(), prAction: PullRequestAction.Opened), CancellationToken.None));

        emitFullMetadata = false;
        failNext = false;
        await pipeline.ExecuteAsync(new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "owner", "repo", 78,
            "sha-carry", "main", false, false, "T", null, "a", "a", "opened",
            Array.Empty<string>(), prAction: PullRequestAction.Opened), CancellationToken.None);

        var carriedDelivered = Assert.Single(postedBodies);
        Assert.Equal(1, carriedDelivered.Split("Shared warning section", StringSplitOptions.None).Length - 1);
        Assert.Contains(SpdxLink.Url, carriedDelivered, StringComparison.Ordinal);
        Assert.Contains(GeneralLink.Url, carriedDelivered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationGuardFallback_And_TrackingIssues_IncludeDocumentationLinksBeforeMarkers()
    {
        // 1. ConversationGuard lock failure updates tracked announcement with DocumentationLinks
        var gitHubGuard = Substitute.For<IGitHubService>();
        var perm = Substitute.For<IRepositoryPermissionService>();
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("owner", "repo", 42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(701, "node-701"));
        gitHubGuard.IsIssueLockedAsync("owner", "repo", 42, Arg.Any<CancellationToken>()).Returns(false);
        gitHubGuard.LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("lock failure"));
        perm.GetUserPermissionAsync("owner", "repo", "maintainer", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Maintain);

        var guardService = new ConversationGuardService(
            gitHubGuard,
            perm,
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
            {
                Enabled = true,
                DryRun = false,
                RepositoryAllowList = new List<string> { "owner/repo" },
                DocumentationLinks = new List<DocumentationLink> { SpdxLink }
            }),
            botIdentity: identity,
            commentGateway: gateway);

        var ctx = new PullRequestContext(
            gitHubGuard, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42,
            "sha", "main", false, false, "Title", null, "maintainer", "author", "created", Array.Empty<string>());
        await guardService.HandleCommandAsync(ctx, "@abaci-bot lock", "maintainer");

        await gitHubGuard.Received(1).UpdateCommentAsync(
            "owner",
            "repo",
            701,
            Arg.Is<string>(b => b.Contains("Failed to lock this conversation", StringComparison.Ordinal) &&
                                b.Contains(SpdxLink.Url, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // When tracked announcement update fails, fallback compensation CreateCommentAsync also includes DocumentationLinks
        gitHubGuard.ClearReceivedCalls();
        gitHubGuard.UpdateCommentAsync("owner", "repo", 701, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("update failed"));
        await guardService.HandleCommandAsync(ctx, "@abaci-bot lock", "maintainer");
        await gitHubGuard.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(b => b.Contains("Failed to lock this conversation", StringComparison.Ordinal) &&
                                b.Contains(SpdxLink.Url, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // 2. CveActiveWatchService.GenerateIssueBody places links before <!-- abaci-cve-package: ... -->
        var finding = new PackageVulnerabilityFinding(
            "curl",
            "8.0",
            new[] { "CVE-2025-1000" },
            new[] { new CveReport("CVE-2025-1000", 7.5, GitHubLabels.Severity2, "OSV", "Summary") });
        var cveBody = CveActiveWatchService.GenerateIssueBody(finding, new[] { AbiLink });
        Assert.EndsWith("<!-- abaci-cve-package: curl -->", cveBody, StringComparison.Ordinal);
        Assert.Contains(AbiLink.Url, cveBody, StringComparison.Ordinal);
        Assert.True(cveBody.IndexOf(AbiLink.Url, StringComparison.Ordinal) <
                    cveBody.IndexOf("<!-- abaci-cve-package: curl -->", StringComparison.Ordinal));

        // 3. ObsPostMergeVerifier places PackageUpdate:DocumentationLinks before <!-- abaci-obs-blocked: ... -->
        var gitHubObs = Substitute.For<IGitHubService>();
        var mergedPr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(mergedPr, 303);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(mergedPr, "SPECS: bump zlib 1.2 -> 1.3");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(mergedPr, "deadbeef1234");
        var prUser = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(prUser, "abaci-bot[bot]");
        typeof(PullRequest).GetProperty(nameof(PullRequest.User))?.SetValue(mergedPr, prUser);
        gitHubObs.SearchMergedPullRequestsAsync("owner", "repo", string.Empty, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { mergedPr });
        gitHubObs.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());
        gitHubObs.GetIssueCommentsAsync("owner", "repo", 303, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var obsService = Substitute.For<IObsBuildService>();
        obsService.GetPackageServiceRevisionAsync("openruyi", "zlib", Arg.Any<CancellationToken>())
            .Returns("deadbeef1234");
        obsService.GetPackageBuildResultAsync("openruyi", "zlib", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "zlib", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        var obsIdentity = Substitute.For<IBotIdentityProvider>();
        obsIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot[bot]");

        var verifier = new ObsPostMergeVerifier(
            gitHubObs,
            obsService,
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                DryRun = false,
                TargetRepository = "owner/repo",
                DocumentationLinks = new List<DocumentationLink> { SpdxLink },
                Obs = new ObsOptions { RequireObsSuccessAfterMerge = true }
            }),
            botIdentity: obsIdentity);

        await verifier.VerifyMergedPullRequestsAsync("owner/repo", CancellationToken.None);

        await gitHubObs.Received(1).CreateIssueAsync(
            "owner",
            "repo",
            Arg.Is<string>(t => t.Contains("zlib", StringComparison.Ordinal)),
            Arg.Is<string>(b => b.Contains(SpdxLink.Url, StringComparison.Ordinal) &&
                                b.IndexOf(SpdxLink.Url, StringComparison.Ordinal) <
                                b.IndexOf("<!-- abaci-obs-blocked: zlib -->", StringComparison.Ordinal)),
            Arg.Any<string[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Validators_PassUrlsThroughVerbatim_AndPreservePathCaseSensitivity_AndSnapshotSectionLinks()
    {
        // Maintainer URLs pass through verbatim without parsing or re-encoding
        var longUrl = "https://docs.openruyi.org/long?" + new string('a', 2100);
        var trustedLinks = new List<DocumentationLink>
        {
            new() { Title = "Wiki Function", Url = "https://example.org/Function_(x)" },
            new() { Title = "Internal Mirror", Url = "https://user:pass@host.internal/guide" },
            new() { Title = "Relative Path", Url = "/docs/contributing" },
            new() { Title = "Long Query", Url = longUrl }
        };
        var trustedFailures = new List<string>();
        DocumentationLinkValidator.Validate(trustedLinks, "DocumentationLinks", trustedFailures);
        Assert.Empty(trustedFailures);

        // Rendering outputs exact configured URLs verbatim
        var rendered = DocumentationLinkFormatter.FormatSeeAlsoFooter(trustedLinks, isChinese: false);
        Assert.Contains("[Wiki Function](https://example.org/Function_(x))", rendered, StringComparison.Ordinal);
        Assert.Contains("[Internal Mirror](https://user:pass@host.internal/guide)", rendered, StringComparison.Ordinal);
        Assert.Contains("[Relative Path](/docs/contributing)", rendered, StringComparison.Ordinal);

        // Unicode line/paragraph separators in titles remain rejected
        var titleFailures = new List<string>();
        DocumentationLinkValidator.Validate(
            new List<DocumentationLink>
            {
                new() { Title = "LineSep\u2028Title", TitleZh = "Para\u2029Sep", Url = "https://docs.openruyi.org/ok" }
            },
            "DocumentationLinks",
            titleFailures);
        Assert.Contains(titleFailures, f => f.Contains(".Title ", StringComparison.Ordinal));
        Assert.Contains(titleFailures, f => f.Contains(".TitleZh ", StringComparison.Ordinal));

        // Paths differing only in case must not falsely collide as duplicates
        var caseSensitiveFailures = new List<string>();
        DocumentationLinkValidator.Validate(
            new List<DocumentationLink>
            {
                new() { Title = "Upper", Url = "https://docs.openruyi.org/Guide" },
                new() { Title = "Lower", Url = "https://docs.openruyi.org/guide" }
            },
            "DocumentationLinks",
            caseSensitiveFailures);
        Assert.Empty(caseSensitiveFailures);

        // AddPendingComment deep-clones the caller list and elements so post-enqueue mutations do not alter queued links
        var gitHub = Substitute.For<IGitHubService>();
        var context = new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.PullRequest, "owner", "repo", 1,
            "sha", "main", false, false, "T", null, "a", "a", "opened",
            Array.Empty<string>(), prAction: PullRequestAction.Opened);
        var callerLink = new DocumentationLink { Title = SpdxLink.Title, TitleZh = SpdxLink.TitleZh, Url = SpdxLink.Url };
        var mutableList = new List<DocumentationLink> { callerLink };
        context.AddPendingComment("body", includeGeneralDocs: false, documentationLinks: mutableList);
        callerLink.Title = "Mutated Title";
        callerLink.Url = "https://mutated.example.org/other";
        mutableList.Clear();
        var queued = Assert.Single(context.PendingComments[0].DocumentationLinks!);
        Assert.Equal(SpdxLink.Title, queued.Title);
        Assert.Equal(SpdxLink.Url, queued.Url);

        // ExtractNormalizedHttpUrls preserves balanced parentheses in bare and Markdown links while trimming delimiters
        var bodyUrls = DocumentationLinkFormatter.ExtractNormalizedHttpUrls(
            new[]
            {
                "See https://example.org/a(b). and [fn](https://example.org/Function_(x)) plus (https://example.org/plain)."
            });
        Assert.Contains("https://example.org/a(b)", bodyUrls);
        Assert.Contains("https://example.org/Function_(x)", bodyUrls);
        Assert.Contains("https://example.org/plain", bodyUrls);
        Assert.DoesNotContain("https://example.org/a", bodyUrls);

        // Dedup excludes https://example.org/a(b) when present inline, without falsely excluding https://example.org/a
        var footerAfterBody = DocumentationLinkFormatter.FormatSeeAlsoFooter(
            new List<DocumentationLink>
            {
                new() { Title = "Prefix A", Url = "https://example.org/a" },
                new() { Title = "Paren AB", Url = "https://example.org/a(b)" }
            },
            isChinese: false,
            excludedUrls: bodyUrls);
        Assert.Contains("[Prefix A](https://example.org/a)", footerAfterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Paren AB", footerAfterBody, StringComparison.Ordinal);
    }
}

using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Tests;

public class BotControlPlaneAndLocaleTests
{
    [Theory]
    [InlineData("@abaci-bot lock", "abaci-bot", "lock", "")]
    [InlineData("@ABACI-BOT: hide @spammer", "abaci-bot", "hide", "@spammer")]
    [InlineData("@abaci-bot[bot], ignore dnf5 broken upstream", "abaci-bot", "ignore", "dnf5 broken upstream")]
    [InlineData("  @aqua-rium status etcd", "aqua-rium", "status", "etcd")]
    [InlineData("@abaci-bot lang zh\r\nextra line", "abaci-bot", "lang", "zh")]
    public void BotCommandParser_TryParse_ValidMentionCommands(
        string body, string botLogin, string expectedVerb, string expectedArgs)
    {
        var ok = BotCommandParser.TryParse(body, botLogin, out var cmd);

        Assert.True(ok);
        Assert.Equal(expectedVerb, cmd.Verb);
        Assert.Equal(expectedArgs, cmd.Arguments);
    }

    [Theory]
    [InlineData("/lock", "abaci-bot")]
    [InlineData("@otherbot lock", "abaci-bot")]
    [InlineData("@abaci-bot", "abaci-bot")]
    [InlineData("@abaci-bot   ", "abaci-bot")]
    [InlineData("hello @abaci-bot lock", "abaci-bot")]
    [InlineData("", "abaci-bot")]
    public void BotCommandParser_TryParse_InvalidOrNonMention_ReturnsFalse(string body, string botLogin)
    {
        Assert.False(BotCommandParser.TryParse(body, botLogin, out _));
    }

    [Fact]
    public void JsonBotStateStore_PersistsIgnoresFailuresAndLocalesAcrossInstances()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using (var store1 = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance))
            {
                store1.SetIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5", "upstream regression", "alice");
                store1.SetPackageFailure("openRuyi-Project", "openRuyi", "etcd", 3, DateTimeOffset.UtcNow.AddDays(30));
                store1.SetUserLocale("bob", "zh");
            }

            using var store2 = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance);
            var ignored = store2.GetIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5");
            Assert.NotNull(ignored);
            Assert.Equal("upstream regression", ignored!.Reason);
            Assert.Equal("alice", ignored.IgnoredBy);

            var failure = store2.GetPackageFailure("openRuyi-Project", "openRuyi", "etcd");
            Assert.NotNull(failure);
            Assert.Equal(3, failure!.ConsecutiveFailures);
            Assert.NotNull(failure.CooldownUntil);

            Assert.Equal("zh", store2.GetUserLocale("bob"));

            Assert.True(store2.RemoveIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5"));
            Assert.Null(store2.GetIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5"));

            Assert.True(store2.ClearUserLocale("bob"));
            Assert.Null(store2.GetUserLocale("bob"));

            Assert.True(store2.ClearPackageFailure("openRuyi-Project", "openRuyi", "etcd"));
            Assert.Null(store2.GetPackageFailure("openRuyi-Project", "openRuyi", "etcd"));
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
    public void JsonBotStateStore_CorruptJson_DegradesGracefully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "bot-state.json"), "{ invalid json");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using var store = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance);
            Assert.Null(store.GetUserLocale("alice"));
            Assert.Empty(store.GetIgnoredPackages("owner", "repo"));
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
    public void LocaleResolver_EnforcesForcedVsDefaultRepositoryPolicies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"locale-test-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                DefaultLocale = "en",
                RepositoryForcedLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["org/forced-zh"] = "zh"
                },
                RepositoryDefaultLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["org/default-zh"] = "zh"
                }
            });
            using var store = new JsonBotStateStore(opts);
            store.SetUserLocale("alice", "en");
            store.SetUserLocale("bob", "zh");

            var resolver = new LocaleResolver(opts, store);

            // Forced repo disables user selection and overrides user preference
            Assert.False(resolver.IsUserSelectionAllowed("org", "forced-zh", out var forced));
            Assert.Equal("zh", forced);
            Assert.Equal("zh", resolver.ResolveForReply("org", "forced-zh", "alice"));

            // Repo default allows user selection; user preference wins over repo default
            Assert.True(resolver.IsUserSelectionAllowed("org", "default-zh", out _));
            Assert.Equal("en", resolver.ResolveForReply("org", "default-zh", "alice"));
            Assert.Equal("zh", resolver.ResolveForReply("org", "default-zh", "carol"));

            // Global fallback
            Assert.Equal("zh", resolver.ResolveForReply("org", "other", "bob"));
            Assert.Equal("en", resolver.ResolveForReply("org", "other", "carol"));

            // Bot PRs ignore user preferences and follow Forced -> RepoDefault -> GlobalDefault
            Assert.Equal("zh", resolver.ResolveForBotPullRequest("org", "forced-zh"));
            Assert.Equal("zh", resolver.ResolveForBotPullRequest("org", "default-zh"));
            Assert.Equal("en", resolver.ResolveForBotPullRequest("org", "other"));
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
    public async Task BotControlPlaneService_IgnoreUnignoreStatusAndLangFlow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-test-{Guid.NewGuid():N}");
        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                DefaultLocale = "en",
                RepositoryForcedLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["org/locked-lang"] = "en"
                }
            });
            using var store = new JsonBotStateStore(stateOpts);
            var localeResolver = new LocaleResolver(stateOpts, store);
            var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());

            var gitHub = Substitute.For<IGitHubService>();
            var perm = Substitute.For<IRepositoryPermissionService>();
            perm.GetUserPermissionAsync("org", "repo", "maintainer", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Maintain);
            perm.GetUserPermissionAsync("org", "repo", "outsider", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Read);

            var specCatalog = Substitute.For<ISpecCatalog>();
            specCatalog.GetSpecAsync("dnf5", Arg.Any<CancellationToken>())
                .Returns(new SpecContext("dnf5", "SPECS/dnf5/dnf5.spec", "Name: dnf5\nVersion: 5.4.2\n", "sha"));

            var service = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts, specCatalog);

            var context = new PullRequestContext(
                gitHubService: gitHub,
                teamName: "captains",
                headers: null,
                eventType: PullRequestEventType.IssueComment,
                owner: "org",
                repo: "repo",
                prNumber: 10,
                headSha: "sha",
                baseBranch: "main",
                isDraft: false,
                isMerged: false,
                title: "test",
                body: "@abaci-bot ignore dnf5 broken",
                senderLogin: "maintainer",
                authorLogin: "maintainer",
                action: "created",
                existingLabels: Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            // 1. Non-maintainer cannot ignore
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("ignore", "dnf5"), "outsider");
            Assert.Null(store.GetIgnoredPackage("org", "repo", "dnf5"));

            // 2. Maintainer ignores dnf5
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("ignore", "dnf5 broken"), "maintainer");
            Assert.Equal("broken", store.GetIgnoredPackage("org", "repo", "dnf5")?.Reason);

            // 3. Status reports runtime ignore
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("status", "dnf5"), "outsider");
            await gitHub.Received().CreateCommentAsync(
                "org", "repo", 10,
                Arg.Is<string>(s => s.Contains("Runtime ignore", StringComparison.Ordinal) && s.Contains("broken", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            // 4. User switches language to zh
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("lang", "zh"), "outsider");
            Assert.Equal("zh", store.GetUserLocale("outsider"));

            // 5. Maintainer unignores dnf5
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("unignore", "dnf5"), "maintainer");
            Assert.Null(store.GetIgnoredPackage("org", "repo", "dnf5"));

            // 6. Forced locale repo rejects lang command
            var forcedContext = new PullRequestContext(
                gitHubService: gitHub,
                teamName: "captains",
                headers: null,
                eventType: PullRequestEventType.IssueComment,
                owner: "org",
                repo: "locked-lang",
                prNumber: 11,
                headSha: "sha",
                baseBranch: "main",
                isDraft: false,
                isMerged: false,
                title: "test",
                body: "@abaci-bot lang zh",
                senderLogin: "outsider",
                authorLogin: "outsider",
                action: "created",
                existingLabels: Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            await service.HandleCommandAsync(forcedContext, "abaci-bot", new ParsedBotCommand("lang", "zh"), "outsider");
            await gitHub.Received().CreateCommentAsync(
                "org", "locked-lang", 11,
                Arg.Is<string>(s => s.Contains("personal language selection is disabled", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>());
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
    public void GeneratePullRequestBody_NoMixedChineseInEnglish_AndSupportsChineseLocale()
    {
        var candidate = new UpdateCandidate("dnf5", "5.4.2", "5.4.8", CandidateOrigin.Lookout, "https://lookout.openruyi.cn/packages/dnf5");

        var enBody = GitHubPackageUpdatePublisher.GeneratePullRequestBody(candidate, "en", "abaci-bot-test");
        Assert.DoesNotContain("如需停止", enBody, StringComparison.Ordinal);
        Assert.Contains("@abaci-bot-test ignore dnf5", enBody, StringComparison.Ordinal);
        Assert.Contains("#!NoAutoUpdate: <reason>", enBody, StringComparison.Ordinal);

        var zhBody = GitHubPackageUpdatePublisher.GeneratePullRequestBody(candidate, "zh", "abaci-bot-test");
        Assert.Contains("如需停止本包的自动更新", zhBody, StringComparison.Ordinal);
        Assert.Contains("@abaci-bot-test ignore dnf5", zhBody, StringComparison.Ordinal);
    }

    [Fact]
    public void BotStateOptionsValidator_RejectsInvalidValues_AndAcceptsValid()
    {
        var validator = new BotStateOptionsValidator();
        Assert.True(validator.Validate(null, new BotStateOptions()).Succeeded);

        var bad = new BotStateOptions
        {
            StateDirectory = "relative-state",
            LocaleRetentionDays = 0,
            DefaultLocale = "fr",
            RepositoryForcedLocales = new Dictionary<string, string> { ["badrepo"] = "de" }
        };
        var result = validator.Validate(null, bad);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("StateDirectory", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("LocaleRetentionDays", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("DefaultLocale", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("RepositoryForcedLocales", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildIdentity_ReturnsNonEmptyNonSensitiveMetadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.Version));
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.Commit));
        Assert.NotEqual("unknown", BuildIdentity.Commit);
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.ImageDigest));
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.BuildTime));
        Assert.Contains(BuildIdentity.Deployment, new[] { "container", "systemd", "binary" });
    }

    [Fact]
    public void JsonBotStateStore_PrunesExpiredUserLocalesOnStartup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var stateJson = $$"""
            {
              "UserLocales": {
                "expiredUser": { "Locale": "zh", "UpdatedAt": "{{DateTimeOffset.UtcNow.AddDays(-200):O}}" },
                "activeUser": { "Locale": "zh", "UpdatedAt": "{{DateTimeOffset.UtcNow.AddDays(-10):O}}" }
              },
              "Repositories": {}
            }
            """;
        File.WriteAllText(Path.Combine(tempDir, "bot-state.json"), stateJson);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                LocaleRetentionDays = 180
            });
            using var store = new JsonBotStateStore(opts);
            Assert.Null(store.GetUserLocale("expiredUser"));
            Assert.Equal("zh", store.GetUserLocale("activeUser"));
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
    public async Task BotControlPlaneModule_RoutesOnlyControlPlaneCommands()
    {
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");
        var service = Substitute.For<IBotControlPlaneService>();
        var gitHub = Substitute.For<IGitHubService>();
        var module = new BotControlPlaneModule(identity, service, NullLogger<BotControlPlaneModule>.Instance);

        Assert.Equal(96, module.Priority);
        Assert.Equal("BotControlPlaneModule", module.ModuleName);

        PullRequestContext MakeCtx(string body, IssueCommentAction? action = null, PullRequestEventType evt = PullRequestEventType.IssueComment) =>
            new(gitHub, "captains", null, evt, "org", "repo", 1, "sha", "main", false, false, "t", body, "alice", "alice", "created", Array.Empty<string>(), commentAction: action ?? IssueCommentAction.Created);

        Assert.False(module.ShouldProcess(MakeCtx("hello")));
        Assert.False(module.ShouldProcess(MakeCtx("@abaci-bot status", IssueCommentAction.Edited)));
        Assert.False(module.ShouldProcess(MakeCtx("@abaci-bot status", evt: PullRequestEventType.PullRequest)));
        Assert.True(module.ShouldProcess(MakeCtx("@abaci-bot status")));

        // Ignores ConversationGuard verb and other bot
        await module.ProcessAsync(MakeCtx("@abaci-bot lock"), CancellationToken.None);
        await module.ProcessAsync(MakeCtx("@otherbot ignore dnf5"), CancellationToken.None);
        await service.DidNotReceiveWithAnyArgs().HandleCommandAsync(default!, default!, default!, default!, default);

        // Routes status
        var validCtx = MakeCtx("@abaci-bot status dnf5");
        await module.ProcessAsync(validCtx, CancellationToken.None);
        await service.Received(1).HandleCommandAsync(validCtx, "abaci-bot", new ParsedBotCommand("status", "dnf5"), "alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BotControlPlaneService_CoversStatusBranchInferenceLangResetAndCallerLocale()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-extra-{Guid.NewGuid():N}");
        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using var store = new JsonBotStateStore(stateOpts);
            store.SetUserLocale("zh-caller", "zh");
            store.SetPackageFailure("org", "repo", "dnf5", 2, DateTimeOffset.UtcNow.AddDays(5));

            var localeResolver = new LocaleResolver(stateOpts, store);
            var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
            var gitHub = Substitute.For<IGitHubService>();
            var perm = Substitute.For<IRepositoryPermissionService>();
            perm.GetUserPermissionAsync("org", "repo", "zh-caller", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Read);

            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/dnf5");
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
            gitHub.GetPullRequestAsync("org", "repo", 20, Arg.Any<CancellationToken>()).Returns(pr);

            var specCatalog = Substitute.For<ISpecCatalog>();
            specCatalog.GetSpecAsync("dnf5", Arg.Any<CancellationToken>())
                .Returns(new SpecContext("dnf5", "SPECS/dnf5/dnf5.spec", "#!NoAutoUpdate: hold\nName: dnf5\n", "sha"));

            var service = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts, specCatalog);
            var ctx = new PullRequestContext(
                gitHub, "captains", null, PullRequestEventType.IssueComment, "org", "repo", 20, "sha", "main",
                false, false, "bump", "@abaci-bot status", "zh-caller", "en-author", "created", Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            // 1. Status without args infers dnf5 from autoupdate/dnf5 and replies in caller's zh locale
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("status", ""), "zh-caller");
            await gitHub.Received().CreateCommentAsync(
                "org", "repo", 20,
                Arg.Is<string>(s => s.Contains("软件包 `dnf5` 自动更新状态", StringComparison.Ordinal) && s.Contains("连续失败 2 次", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            // 2. Invalid lang usage & lang reset
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("lang", "fr"), "zh-caller");
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("lang", "reset"), "zh-caller");
            Assert.Null(store.GetUserLocale("zh-caller"));

            // 3. Non-maintainer unignore denied
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("unignore", "dnf5"), "zh-caller");
            await gitHub.Received().CreateCommentAsync(
                "org", "repo", 20,
                Arg.Is<string>(s => s.Contains("Only repository maintainers", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
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
    public void JsonBotStateStore_UnwritableStateDirectory_FailsFast()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"bot-state-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = Path.Combine(blockingFile, "nested")
            });

            var ex = Assert.Throws<InvalidOperationException>(() => new JsonBotStateStore(opts));
            Assert.Contains("is not writable", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(blockingFile))
            {
                File.Delete(blockingFile);
            }
        }
    }

    [Fact]
    public async Task ManualGuideRebuildVerificationOrchestrator_HonorsRequestedLocale()
    {
        var formatter = new GovernanceCommentFormatter();
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions());
        var orchestrator = new ManualGuideRebuildVerificationOrchestrator(formatter, options);

        var request = new RebuildVerificationRequest(
            "org", "repo", 99, "sha", "main",
            new[] { "openssl" }, 1500, 1001, Locale: "zh");

        var result = await orchestrator.RequestVerificationAsync(request);
        Assert.Equal(RebuildVerificationState.ManualVerificationRequired, result.State);
        Assert.Contains("Rebuild 影响范围暂缓合并", result.AdvisoryComment, StringComparison.Ordinal);
    }

    [Fact]
    public void BotStatePathResolver_AlwaysReturnsRootedAbsoluteDirectory()
    {
        var defaultDir = BotStatePathResolver.ResolveStateDirectory(null);
        Assert.True(Path.IsPathRooted(defaultDir));
        Assert.False(string.Equals(defaultDir, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

        var explicitRelative = BotStatePathResolver.ResolveStateDirectory("relative-state");
        Assert.True(Path.IsPathRooted(explicitRelative));
    }

    [Theory]
    [InlineData("1.0.0+0a5160cd06da1234", null, false, "0a5160cd06da")]
    [InlineData("1.0.0+0a5160cd06da1234-dirty", null, false, "0a5160cd06da-dirty")]
    [InlineData("1.0.0+unknown", "0a5160cd06da1234", false, "0a5160cd06da")]
    [InlineData("1.0.0", "0a5160cd06da1234", true, "0a5160cd06da-dirty")]
    [InlineData("1.0.0", "0a5160cd06da-dirty", false, "0a5160cd06da-dirty")]
    [InlineData("1.0.0", null, true, "unknown")]
    public void BuildIdentity_ResolveCommitFrom_HandlesCleanDirtyAndFallback(
        string? informationalVersion, string? envCommit, bool envDirty, string expected)
    {
        Assert.Equal(expected, BuildIdentity.ResolveCommitFrom(informationalVersion, envCommit, envDirty));
    }

    [Fact]
    public void UpdateAdmission_SupportsRpmEpochInValidationAndComparison()
    {
        Assert.True(UpdateAdmission.IsValidTargetVersion("1:2.0"));
        Assert.True(UpdateAdmission.IsValidTargetVersion("2:1.2.3~rc1"));
        Assert.False(UpdateAdmission.IsValidTargetVersion("abc:2.0"));

        Assert.True(UpdateAdmission.CompareRpmVersions("1:1.0", "2.0") > 0);
        Assert.True(UpdateAdmission.CompareRpmVersions("2.0", "1:1.0") < 0);
        Assert.Equal(0, UpdateAdmission.CompareRpmVersions("1:2.0", "1:2.0"));
        Assert.True(UpdateAdmission.CompareRpmVersions("2:1.0", "1:9.9") > 0);
    }

    [Fact]
    public void JsonBotStateStore_PersistsRecordedFailedPrDeduplication()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-failedpr-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using (var store1 = new JsonBotStateStore(opts))
            {
                Assert.True(store1.TryMarkFailedPrRecorded("org", "repo", 101));
                Assert.False(store1.TryMarkFailedPrRecorded("org", "repo", 101));
            }

            using var store2 = new JsonBotStateStore(opts);
            Assert.False(store2.TryMarkFailedPrRecorded("org", "repo", 101));
            Assert.True(store2.TryMarkFailedPrRecorded("org", "repo", 102));
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
    public async Task EphemeralReplyService_PersistsAcrossRestart_DeletesById_AndRetriesTransientFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ephemeral-test-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("org", "repo", 42, "Usage hint", Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(501L, "IC_kwDO501"));

        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                EphemeralReplyDeleteMinutes = 5
            });
            using (var store1 = new JsonBotStateStore(opts))
            {
                var service1 = new EphemeralReplyService(gitHub, store1, opts, gateway, NullLogger<EphemeralReplyService>.Instance, timeProvider);
                await service1.PostEphemeralReplyAsync("org", "repo", 42, "Usage hint");
                Assert.Equal(0, await service1.ProcessDueRepliesAsync());
            }

            // Simulate process restart with new store and service instance
            using var store2 = new JsonBotStateStore(opts);
            var service2 = new EphemeralReplyService(gitHub, store2, opts, gateway, NullLogger<EphemeralReplyService>.Instance, timeProvider);

            // Advance 6 min -> transient failure on first delete schedules retry (+1m)
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            gateway.DeleteCommentAsync("org", "repo", 501L, Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new HttpRequestException("transient"));

            Assert.Equal(0, await service2.ProcessDueRepliesAsync());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow()));

            // Advance 1 min -> retry succeeds and removes entry from store
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            gateway.DeleteCommentAsync("org", "repo", 501L, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Assert.Equal(1, await service2.ProcessDueRepliesAsync());
            await gateway.Received(2).DeleteCommentAsync("org", "repo", 501L, Arg.Any<CancellationToken>());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow().AddHours(1)));
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
    public async Task ConversationGuardService_AuthorConfirmationPrompt_UsesConfirmTimeoutInsteadOf5MinDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cg-ttl-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("owner", "repo", 42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(777L, "IC_kwDO777"));
        var perm = Substitute.For<IRepositoryPermissionService>();
        perm.GetUserPermissionAsync("owner", "repo", "author-user", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Read);
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");

        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                EphemeralReplyDeleteMinutes = 5
            });
            using var store = new JsonBotStateStore(stateOpts);
            var ephemeral = new EphemeralReplyService(gitHub, store, stateOpts, gateway, timeProvider: timeProvider);

            var cgOpts = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
            {
                Enabled = true,
                DryRun = false,
                AuthorLockConfirmTimeoutMinutes = 15
            });
            var service = new ConversationGuardService(
                gitHub, perm, cgOpts, NullLogger<ConversationGuardService>.Instance, timeProvider,
                botIdentity: identity, ephemeralReplies: ephemeral);

            var ctx = new PullRequestContext(
                gitHub, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42, "sha", "main",
                false, false, "Title", "@abaci-bot lock", "author-user", "author-user", "created", Array.Empty<string>());

            await service.HandleCommandAsync(ctx, "@abaci-bot lock", "author-user");

            // At +6 minutes (past default 5m), confirmation prompt must still be alive
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal(0, await ephemeral.ProcessDueRepliesAsync());

            // At +15 minutes, confirmation prompt is deleted
            timeProvider.Advance(TimeSpan.FromMinutes(9));
            Assert.Equal(1, await ephemeral.ProcessDueRepliesAsync());
            await gateway.Received(1).DeleteCommentAsync("owner", "repo", 777L, Arg.Any<CancellationToken>());
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
    public async Task EphemeralReplyService_ActionMinimize_PersistsNodeIdAcrossRestart_AndRetriesWhenFalse()
    {
        const long largeCommentId = 7_059_426_126L;
        const string nodeId = "IC_kwDO_601";
        var tempDir = Path.Combine(Path.GetTempPath(), $"ephemeral-min-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("org", "repo", 42, "Usage hint", Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(largeCommentId, nodeId));

        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                EphemeralReplyDeleteMinutes = 5,
                EphemeralReplyAction = EphemeralReplyAction.Minimize
            });
            using (var store1 = new JsonBotStateStore(opts))
            {
                var service1 = new EphemeralReplyService(gitHub, store1, opts, gateway, timeProvider: timeProvider);
                await service1.PostEphemeralReplyAsync("org", "repo", 42, "Usage hint");
            }

            // Simulate process restart with new store and service instance
            using var store2 = new JsonBotStateStore(opts);
            var service2 = new EphemeralReplyService(gitHub, store2, opts, gateway, timeProvider: timeProvider);

            // First attempt returns false -> must NOT remove pending entry, must schedule retry (+1m)
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            gateway.MinimizeCommentAsync("org", "repo", largeCommentId, nodeId, "OUTDATED", Arg.Any<CancellationToken>())
                .Returns(false);

            Assert.Equal(0, await service2.ProcessDueRepliesAsync());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow()));
            Assert.Single(store2.GetDueEphemeralComments(timeProvider.GetUtcNow().AddMinutes(1)));

            // Retry returns true -> clears pending entry using persisted NodeId
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            gateway.MinimizeCommentAsync("org", "repo", largeCommentId, nodeId, "OUTDATED", Arg.Any<CancellationToken>())
                .Returns(true);

            Assert.Equal(1, await service2.ProcessDueRepliesAsync());
            await gateway.Received(2).MinimizeCommentAsync("org", "repo", largeCommentId, nodeId, "OUTDATED", Arg.Any<CancellationToken>());
            await gateway.DidNotReceive().DeleteCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow().AddHours(1)));
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
    public void BotStateOptionsValidator_RejectsInvalidEphemeralReplyAction()
    {
        var validator = new BotStateOptionsValidator();
        Assert.True(validator.Validate(null, new BotStateOptions { EphemeralReplyAction = EphemeralReplyAction.Minimize }).Succeeded);

        var bad = new BotStateOptions { EphemeralReplyAction = (EphemeralReplyAction)99 };
        var result = validator.Validate(null, bad);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("EphemeralReplyAction", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthResponseBuilder_SeparatesDefaultLivenessFromVerboseDiagnostics_AndTracksStartupUptimeAndLoggerErrors()
    {
        var initialTime = new DateTimeOffset(2026, 11, 21, 7, 41, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(initialTime);
        var runtimeHealth = new RuntimeHealthState(timeProvider);

        // Advance 45 seconds BEFORE first health request
        timeProvider.Advance(TimeSpan.FromSeconds(45));

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger("abaci_bot.Services.TestService");

        // Warnings must NOT populate lastError
        logger.LogWarning("Transient warning that should not set lastError");

        var minimalJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: false,
            runtimeHealth,
            new PackageUpdateOptions(),
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"status\":\"healthy\"", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abiEngine", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("conversationGuardPermission", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("packageUpdatePermission", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("uptimeSeconds", minimalJson, StringComparison.Ordinal);

        var firstVerboseJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: true,
            runtimeHealth,
            new PackageUpdateOptions { Enabled = true },
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"uptimeSeconds\":45", firstVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"lastError\":null", firstVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"conversationGuardPermission\":\"disabled\"", firstVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"packageUpdatePermission\":\"disabled\"", firstVerboseJson, StringComparison.Ordinal);

        // Capability probe statuses come from instance RuntimeHealthState rather than static mutable fields
        runtimeHealth.SetCapabilityStatus("conversationGuardPermission", "verified");
        runtimeHealth.SetCapabilityStatus("packageUpdatePermission", "verified");

        // Error logged via host ILogger populates lastError and redacts tokens
        logger.LogError(new InvalidOperationException("bad token ghp_secret123"), "Sweep failed");

        var secondVerboseJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: true,
            runtimeHealth,
            new PackageUpdateOptions { Enabled = true },
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"capabilities\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"abiEngine\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"conversationGuardPermission\":\"verified\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"packageUpdatePermission\":\"verified\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"modules\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("TestService: Sweep failed (InvalidOperationException: bad token [REDACTED])", secondVerboseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_secret123", secondVerboseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationGuardService_ConfirmWithTrailingColon_Succeeds()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var perm = Substitute.For<IRepositoryPermissionService>();
        perm.GetUserPermissionAsync("owner", "repo", "author-user", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Read);
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");

        var opts = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = true, DryRun = false });
        var service = new ConversationGuardService(gitHub, perm, opts, NullLogger<ConversationGuardService>.Instance, botIdentity: identity);

        var ctx = new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42, "sha", "main",
            false, false, "Title", "@abaci-bot lock", "author-user", "author-user", "created", Array.Empty<string>());

        await service.HandleCommandAsync(ctx, "@abaci-bot lock", "author-user");
        await service.HandleCommandAsync(ctx, "@abaci-bot lock confirm:", "author-user");

        await gitHub.Received(1).LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void JsonBotStateStore_SecondInstanceOnSameDirectory_FailsFast_AndSucceedsAfterDispose()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-lock-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using (var store1 = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance))
            {
                store1.SetUserLocale("alice", "zh");

                var ex = Assert.Throws<InvalidOperationException>(() => new JsonBotStateStore(opts));
                Assert.Contains("already locked", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("single active instance", ex.Message, StringComparison.OrdinalIgnoreCase);
            }

            // Lock released cleanly after Dispose(); second startup succeeds and reads persisted state
            using var store2 = new JsonBotStateStore(opts);
            Assert.Equal("zh", store2.GetUserLocale("alice"));
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
    public void HealthResponseBuilder_IncludesVerboseSources_RedactsAuthorizationAndPrivateKey_AndDIResolvesCleanly()
    {
        var initialTime = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(initialTime);
        var runtimeHealth = new RuntimeHealthState(timeProvider);

        runtimeHealth.RecordError(
            "LookoutVersionSource",
            new HttpRequestException("Authorization: Basic dXNlcjpwYXNz while reading -----BEGIN RSA PRIVATE KEY----- key"));
        runtimeHealth.RecordSourceOutcome(
            "Lookout",
            "failed",
            0,
            "Request failed with Authorization: Bearer super-secret-jwt");
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        runtimeHealth.RecordSourceOutcome("RustCrates", "healthy", 3);

        var minimalJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: false,
            runtimeHealth,
            new PackageUpdateOptions(),
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"status\":\"healthy\"", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sources\"", minimalJson, StringComparison.Ordinal);

        var verboseJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: true,
            runtimeHealth,
            new PackageUpdateOptions { Enabled = true },
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"sources\"", verboseJson, StringComparison.Ordinal);
        Assert.Contains("\"Lookout\"", verboseJson, StringComparison.Ordinal);
        Assert.Contains("\"RustCrates\"", verboseJson, StringComparison.Ordinal);
        Assert.Contains("\"Status\":\"failed\"", verboseJson, StringComparison.Ordinal);
        Assert.Contains("\"ConsecutiveFailures\":1", verboseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcjpwYXNz", verboseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-jwt", verboseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", verboseJson, StringComparison.Ordinal);

        // Verify DI construction of GitHubSpecCatalog, LookoutVersionSource, and RustCrateVersionSource
        var tempDir = Path.Combine(Path.GetTempPath(), $"di-smoke-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            services.AddSingleton(runtimeHealth);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir }));
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
            services.AddSingleton<IBotStateStore, JsonBotStateStore>();
            services.AddSingleton(Substitute.For<IGitHubService>());
            services.AddSingleton(Substitute.For<IRepositoryArchiveGateway>());
            services.AddSingleton<IFileContentCache, FileContentCache>();
            services.Configure<FileCacheOptions>(_ => { });
            services.AddSingleton<ISpecCatalog, GitHubSpecCatalog>();
            services.AddSingleton<IPagedIssueSearchGateway>(Substitute.For<IPagedIssueSearchGateway>());
            services.AddSingleton<IOpenPullRequestInventoryGateway>(Substitute.For<IOpenPullRequestInventoryGateway>());
            services.AddSingleton<IObsBuildService>(Substitute.For<IObsBuildService>());
            services.AddSingleton<IObsBuildFreshnessGateway>(Substitute.For<IObsBuildFreshnessGateway>());
            services.AddSingleton<IPackageUpdatePublisher>(Substitute.For<IPackageUpdatePublisher>());
            services.AddSingleton<IRemoteAssetBackfiller>(Substitute.For<IRemoteAssetBackfiller>());
            services.AddSingleton<ObsPostMergeVerifier>();
            services.AddSingleton<PackageUpdateService>();
            services.AddSingleton<LookoutVersionSource>();
            services.AddSingleton<RustCrateVersionSource>();

            using var provider = services.BuildServiceProvider();
            Assert.IsType<GitHubSpecCatalog>(provider.GetRequiredService<ISpecCatalog>());
            Assert.NotNull(provider.GetRequiredService<IOpenPullRequestInventoryGateway>());
            Assert.NotNull(provider.GetRequiredService<IObsBuildFreshnessGateway>());
            Assert.NotNull(provider.GetRequiredService<ObsPostMergeVerifier>());
            Assert.NotNull(provider.GetRequiredService<PackageUpdateService>());
            Assert.NotNull(provider.GetRequiredService<LookoutVersionSource>());
            Assert.NotNull(provider.GetRequiredService<RustCrateVersionSource>());
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
    public void JsonBotStateStore_PersistsCursorWatermarksAcrossRestart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-cursor-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using (var store1 = new JsonBotStateStore(opts))
            {
                Assert.Null(store1.GetCursorWatermark("rust-crates"));
                store1.SetCursorWatermark("rust-crates", "rust-serde");
                Assert.Equal("rust-serde", store1.GetCursorWatermark("rust-crates"));
            }

            using (var store2 = new JsonBotStateStore(opts))
            {
                Assert.Equal("rust-serde", store2.GetCursorWatermark("rust-crates"));
                store2.SetCursorWatermark("rust-crates", null);
                Assert.Null(store2.GetCursorWatermark("rust-crates"));
            }

            using var store3 = new JsonBotStateStore(opts);
            Assert.Null(store3.GetCursorWatermark("rust-crates"));
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
    public async Task BotControlPlaneService_OnLockedConversation_WhenReplyReturnsForbidden_DoesNotThrowAndPreservesState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-locked-{Guid.NewGuid():N}");
        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                DefaultLocale = "en"
            });
            using var store = new JsonBotStateStore(stateOpts);
            var localeResolver = new LocaleResolver(stateOpts, store);
            var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());

            var gitHub = Substitute.For<IGitHubService>();
            var forbiddenEx = new ForbiddenException(Substitute.For<IResponse>());
            gitHub.CreateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw forbiddenEx);

            var perm = Substitute.For<IRepositoryPermissionService>();
            perm.GetUserPermissionAsync("org", "repo", "maintainer", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Maintain);
            perm.GetUserPermissionAsync("org", "repo", "outsider", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Read);

            var service = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts);
            var context = new PullRequestContext(
                gitHubService: gitHub,
                teamName: "captains",
                headers: null,
                eventType: PullRequestEventType.IssueComment,
                owner: "org",
                repo: "repo",
                prNumber: 99,
                headSha: "sha",
                baseBranch: "main",
                isDraft: false,
                isMerged: false,
                title: "locked PR",
                body: "@abaci-bot status",
                senderLogin: "maintainer",
                authorLogin: "maintainer",
                action: "created",
                existingLabels: Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            Assert.Null(await Record.ExceptionAsync(() => service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("status", "curl"), "outsider")));
            Assert.Null(await Record.ExceptionAsync(() => service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("ignore", "curl regression"), "maintainer")));
            Assert.Equal("regression", store.GetIgnoredPackage("org", "repo", "curl")?.Reason);

            Assert.Null(await Record.ExceptionAsync(() => service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("lang", "zh"), "outsider")));
            Assert.Equal("zh", store.GetUserLocale("outsider"));

            Assert.Null(await Record.ExceptionAsync(() => service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("unignore", "curl"), "maintainer")));
            Assert.Null(store.GetIgnoredPackage("org", "repo", "curl"));
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
    public void JsonBotStateStore_CorruptJson_LogsErrorAndCreatesCorruptBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-corrupt-err-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var stateFile = Path.Combine(tempDir, "bot-state.json");
        File.WriteAllText(stateFile, "{ invalid json");

        var runtimeHealth = new RuntimeHealthState();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger<JsonBotStateStore>();

        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using var store = new JsonBotStateStore(opts, logger);

            Assert.Null(store.GetUserLocale("alice"));
            Assert.Empty(store.GetIgnoredPackages("owner", "repo"));
            Assert.False(File.Exists(stateFile));
            var backups = Directory.GetFiles(tempDir, "bot-state.json.corrupt.*");
            Assert.Single(backups);
            Assert.Equal("{ invalid json", File.ReadAllText(backups[0]));
            Assert.NotNull(runtimeHealth.GetLastError().LastError);
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
    public void JsonBotStateStore_WhenSaveStateFails_ThrowsAndRollsBackInMemoryControlPlaneState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-rollback-{Guid.NewGuid():N}");
        var runtimeHealth = new RuntimeHealthState();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger<JsonBotStateStore>();

        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using var store = new JsonBotStateStore(opts, logger);
            store.SetIgnoredPackage("org", "repo", "keep-me", "valid", "alice");
            store.SetUserLocale("alice", "en");

            // Blocking bot-state.json.tmp with a directory deterministically forces SaveState write failure.
            Directory.CreateDirectory(Path.Combine(tempDir, "bot-state.json.tmp"));

            Assert.ThrowsAny<Exception>(() => store.SetIgnoredPackage("org", "repo", "fail-pkg", "reason", "bob"));
            Assert.Null(store.GetIgnoredPackage("org", "repo", "fail-pkg"));
            Assert.NotNull(store.GetIgnoredPackage("org", "repo", "keep-me"));

            Assert.ThrowsAny<Exception>(() => store.RemoveIgnoredPackage("org", "repo", "keep-me"));
            Assert.NotNull(store.GetIgnoredPackage("org", "repo", "keep-me"));

            Assert.ThrowsAny<Exception>(() => store.SetUserLocale("alice", "zh"));
            Assert.Equal("en", store.GetUserLocale("alice"));

            Assert.ThrowsAny<Exception>(() => store.ClearUserLocale("alice"));
            Assert.Equal("en", store.GetUserLocale("alice"));

            Assert.NotNull(runtimeHealth.GetLastError().LastError);
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
    public async Task BotControlPlaneService_WhenStatePersistenceThrows_PostsExplicitFailureReplyInsteadOfSuccess()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var perm = Substitute.For<IRepositoryPermissionService>();
        perm.GetUserPermissionAsync("org", "repo", "maintainer", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Maintain);
        perm.GetUserPermissionAsync("org", "repo", "outsider", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Read);

        var failingStore = Substitute.For<IBotStateStore>();
        failingStore.When(s => s.SetIgnoredPackage(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()))
            .Do(_ => throw new IOException("Read-only state directory"));
        failingStore.When(s => s.RemoveIgnoredPackage(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new IOException("Read-only state directory"));
        failingStore.When(s => s.SetUserLocale(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new IOException("Read-only state directory"));
        failingStore.When(s => s.ClearUserLocale(Arg.Any<string>()))
            .Do(_ => throw new IOException("Read-only state directory"));

        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { DefaultLocale = "en" });
        var localeResolver = new LocaleResolver(stateOpts, failingStore);
        var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var service = new BotControlPlaneService(gitHub, perm, failingStore, localeResolver, pkgOpts);

        var context = new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.IssueComment, "org", "repo", 88, "sha", "main",
            false, false, "test", "@abaci-bot ignore dnf5", "maintainer", "maintainer", "created", Array.Empty<string>(),
            commentAction: IssueCommentAction.Created);

        await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("ignore", "dnf5"), "maintainer");
        await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("unignore", "dnf5"), "maintainer");
        await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("lang", "zh"), "outsider");
        await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("lang", "reset"), "outsider");

        await gitHub.Received(3).CreateCommentAsync(
            "org", "repo", 88,
            Arg.Is<string>(s => s.Contains("Failed to persist state to disk", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await gitHub.Received(1).CreateCommentAsync(
            "org", "repo", 88,
            Arg.Is<string>(s => s.Contains("状态持久化失败", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await gitHub.DidNotReceive().CreateCommentAsync(
            "org", "repo", 88,
            Arg.Is<string>(s => s.Contains("is now ignored", StringComparison.Ordinal) ||
                                s.Contains("has been removed", StringComparison.Ordinal) ||
                                s.Contains("已将您的回复语言偏好设置为", StringComparison.Ordinal) ||
                                s.Contains("has been cleared", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JsonBotStateStore_ObsBlockAndCiCooldown_AreOrthogonal_AndStatusReportsThemSeparately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-obs-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            using (var store = new JsonBotStateStore(opts))
            {
                var ciUntil = DateTimeOffset.UtcNow.AddDays(30);
                var obsUntil = DateTimeOffset.UtcNow.AddDays(15);

                store.SetPackageFailure("org", "repo", "curl", 2, ciUntil);
                store.SetObsBlockCooldown("org", "repo", "curl", obsUntil);

                var both = store.GetPackageFailure("org", "repo", "curl");
                Assert.NotNull(both);
                Assert.Equal(2, both!.ConsecutiveFailures);
                Assert.Equal(ciUntil, both.CooldownUntil);
                Assert.Equal(obsUntil, both.ObsBlockedUntil);

                // Clearing OBS block must preserve CI failure count and 30d CI cooldown
                Assert.True(store.ClearObsBlockCooldown("org", "repo", "curl"));
                var afterClearObs = store.GetPackageFailure("org", "repo", "curl");
                Assert.NotNull(afterClearObs);
                Assert.Equal(2, afterClearObs!.ConsecutiveFailures);
                Assert.Equal(ciUntil, afterClearObs.CooldownUntil);
                Assert.Null(afterClearObs.ObsBlockedUntil);

                // Clearing CI failure when OBS block is active must preserve OBS block
                store.SetObsBlockCooldown("org", "repo", "curl", obsUntil);
                Assert.True(store.ClearPackageFailure("org", "repo", "curl"));
                var afterClearCi = store.GetPackageFailure("org", "repo", "curl");
                Assert.NotNull(afterClearCi);
                Assert.Equal(0, afterClearCi!.ConsecutiveFailures);
                Assert.Null(afterClearCi.CooldownUntil);
                Assert.Equal(obsUntil, afterClearCi.ObsBlockedUntil);

                // Status report shows OBS block without fabricating consecutive CI failures
                var gitHub = Substitute.For<IGitHubService>();
                var perm = Substitute.For<IRepositoryPermissionService>();
                var localeResolver = new LocaleResolver(opts, store);
                var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
                var cp = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts);
                var ctx = new PullRequestContext(
                    gitHub, "captains", null, PullRequestEventType.IssueComment, "org", "repo", 77, "sha", "main",
                    false, false, "bump", "@abaci-bot status curl", "alice", "alice", "created", Array.Empty<string>(),
                    commentAction: IssueCommentAction.Created);

                await cp.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("status", "curl"), "alice");
                await gitHub.Received(1).CreateCommentAsync(
                    "org", "repo", 77,
                    Arg.Is<string>(s =>
                        s.Contains("Failure cooldown**: None", StringComparison.Ordinal) &&
                        s.Contains("OBS post-merge block**: Cooldown until", StringComparison.Ordinal) &&
                        !s.Contains("consecutive failure", StringComparison.Ordinal)),
                    Arg.Any<CancellationToken>());
            }

            // Re-load from disk confirms orthogonality persisted cleanly
            using var storeReloaded = new JsonBotStateStore(opts);
            var reloaded = storeReloaded.GetPackageFailure("org", "repo", "curl");
            Assert.NotNull(reloaded);
            Assert.Equal(0, reloaded!.ConsecutiveFailures);
            Assert.NotNull(reloaded.ObsBlockedUntil);
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
    public void JsonBotStateStore_RemainingMutatorsRollbackOnSaveFailure_AndCorruptBackupFailureThrowsFailFast()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-mut-rollback-{Guid.NewGuid():N}");
        var blockerDir = Path.Combine(tempDir, "bot-state.json.tmp");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir, LocaleRetentionDays = 30 });
            using (var store = new JsonBotStateStore(opts))
            {
                store.SetPackageFailure("org", "repo", "curl", 1, DateTimeOffset.UtcNow.AddDays(5));
                store.SetCursorWatermark("cursor-k", "v1");
                store.AddPendingEphemeralComment(new PendingEphemeralComment("org", "repo", 10, 100L, DateTimeOffset.UtcNow.AddMinutes(-5), 0));

                Directory.CreateDirectory(blockerDir);

                Assert.ThrowsAny<Exception>(() => store.SetPackageFailure("org", "repo", "curl", 3, DateTimeOffset.UtcNow.AddDays(30)));
                Assert.Equal(1, store.GetPackageFailure("org", "repo", "curl")?.ConsecutiveFailures);

                Assert.ThrowsAny<Exception>(() => store.ClearPackageFailure("org", "repo", "curl"));
                Assert.Equal(1, store.GetPackageFailure("org", "repo", "curl")?.ConsecutiveFailures);

                Assert.ThrowsAny<Exception>(() => store.SetObsBlockCooldown("org", "repo", "zlib", DateTimeOffset.UtcNow.AddDays(10)));
                Assert.Null(store.GetPackageFailure("org", "repo", "zlib"));

                Assert.ThrowsAny<Exception>(() => store.TryMarkFailedPrRecorded("org", "repo", 55));

                Assert.ThrowsAny<Exception>(() => store.SetCursorWatermark("cursor-k", "v2"));
                Assert.Equal("v1", store.GetCursorWatermark("cursor-k"));
                Assert.ThrowsAny<Exception>(() => store.SetCursorWatermark("cursor-k", null));
                Assert.Equal("v1", store.GetCursorWatermark("cursor-k"));

                Assert.ThrowsAny<Exception>(() => store.RecordEphemeralRetry("org", "repo", 100L, DateTimeOffset.UtcNow.AddMinutes(5)));
                Assert.Equal(0, store.GetDueEphemeralComments(DateTimeOffset.UtcNow)[0].RetryCount);

                Assert.ThrowsAny<Exception>(() => store.RemovePendingEphemeralComment("org", "repo", 100L));
                Assert.Single(store.GetDueEphemeralComments(DateTimeOffset.UtcNow));

                Assert.ThrowsAny<Exception>(() => store.AddPendingEphemeralComment(
                    new PendingEphemeralComment("org", "repo", 11, 200L, DateTimeOffset.UtcNow.AddMinutes(-1))));
                Assert.Single(store.GetDueEphemeralComments(DateTimeOffset.UtcNow));

                Directory.Delete(blockerDir, recursive: true);

                // Retrying previously failed cursor watermark succeeds instead of hitting stale memory equality
                store.SetCursorWatermark("cursor-k", "v2");
                Assert.Equal("v2", store.GetCursorWatermark("cursor-k"));
            }

            // Fail-fast when corrupt bot-state.json cannot be moved aside for backup (cross-platform seam, works on Windows and Linux)
            var stateFilePath = Path.Combine(tempDir, "bot-state.json");
            File.WriteAllText(stateFilePath, "{ invalid-json");
            var ex = Assert.Throws<InvalidOperationException>(() => new JsonBotStateStore(
                opts,
                logger: null,
                fileReader: null,
                fileMover: (_, _) => throw new IOException("simulated move failure")));
            Assert.Contains("back up corrupt bot state file", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(stateFilePath));

            // Fail-fast WITHOUT renaming or clearing valid bot-state.json on filesystem read I/O error
            const string validJson = "{\"Cursors\":{\"k\":\"v1\"}}";
            File.WriteAllText(stateFilePath, validJson);
            var readEx = Assert.Throws<InvalidOperationException>(() => new JsonBotStateStore(
                opts,
                logger: null,
                fileReader: _ => throw new IOException("simulated read EIO"),
                fileMover: null));
            Assert.Contains("Failed to read bot state file", readEx.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(validJson, File.ReadAllText(stateFilePath));
            Assert.Empty(Directory.GetFiles(tempDir, "*.corrupt.*"));
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
    public async Task EphemeralReplyService_WhenRecordRetryThrows_DoesNotLogMisleadingExhaustedRetries()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.DeleteCommentAsync("org", "repo", 500L, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("503 Unavailable"));

        var stateStore = Substitute.For<IBotStateStore>();
        stateStore.GetDueEphemeralComments(Arg.Any<DateTimeOffset>())
            .Returns(new[] { new PendingEphemeralComment("org", "repo", 10, 500L, DateTimeOffset.UtcNow.AddMinutes(-1), RetryCount: 0) });
        stateStore.When(s => s.RecordEphemeralRetry("org", "repo", 500L, Arg.Any<DateTimeOffset>(), Arg.Any<int>()))
            .Do(_ => throw new IOException("disk full"));

        var capturedLogs = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
            b.AddProvider(new CapturingLoggerProvider(capturedLogs)));
        var logger = loggerFactory.CreateLogger<EphemeralReplyService>();

        var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { EphemeralReplyAction = EphemeralReplyAction.Delete });
        using var service = new EphemeralReplyService(gitHub, stateStore, opts, gateway, logger);

        var processed = await service.ProcessDueRepliesAsync();
        Assert.Equal(0, processed);
        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Error && l.Message.Contains("Failed to persist retry schedule", StringComparison.Ordinal));
        Assert.DoesNotContain(capturedLogs, l => l.Message.Contains("Exhausted retries", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeHealthState_SanitizeMessage_RedactsPosixAndWindowsPaths_PreservingUrlsAndRepoSlugs()
    {
        var state = new RuntimeHealthState();
        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException(@"Cannot open C:\secret\appsettings.json or /state/bot-state.json while contacting https://api.osv.dev/v1/querybatch for openRuyi-Project/openRuyi"),
            "Persist failure on SPECS/curl/curl.spec");

        var (lastError1, _) = state.GetLastError();
        Assert.NotNull(lastError1);
        Assert.DoesNotContain(@"C:\secret\appsettings.json", lastError1!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/state/bot-state.json", lastError1!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH]", lastError1!, StringComparison.Ordinal);
        Assert.Contains("https://api.osv.dev/v1/querybatch", lastError1!, StringComparison.Ordinal);
        Assert.Contains("openRuyi-Project/openRuyi", lastError1!, StringComparison.Ordinal);
        Assert.Contains("SPECS/curl/curl.spec", lastError1!, StringComparison.Ordinal);

        // Also cover space-containing Windows paths, UNC shares, file:// URIs, '+' POSIX paths, and /api + /health routes
        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException(@"Read C:\Program Files\abaci-bot\appsettings.json, \\fileserver\share\bot-state.json, file:///etc/secret.pem, /var/lib/g++/config.json"),
            "Route /api/webhook and /health?verbose=true");

        var (lastError2, _) = state.GetLastError();
        Assert.NotNull(lastError2);
        Assert.DoesNotContain("Program Files", lastError2!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\fileserver\share", lastError2!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:///etc/secret.pem", lastError2!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/lib/g++/config.json", lastError2!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH]", lastError2!, StringComparison.Ordinal);
        Assert.Contains("/api/webhook", lastError2!, StringComparison.Ordinal);
        Assert.Contains("/health?verbose=true", lastError2!, StringComparison.Ordinal);

        // Windows forward-slash drive path, UNC share spaces, file:// spaces, and spaced leaf filename (split cleanly under 240-char cap).
        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException("Failed opening C:/Users/steve/secret.txt with https://api.osv.dev/v1/vulns"),
            "Retry");
        var (lastError3, _) = state.GetLastError();
        Assert.NotNull(lastError3);
        Assert.DoesNotContain("C:/Users", lastError3!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", lastError3!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH]", lastError3!, StringComparison.Ordinal);
        Assert.Contains("https://api.osv.dev/v1/vulns", lastError3!, StringComparison.Ordinal);

        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException(@"Cannot open C:\Users\steve\My Secret File.txt during sync"),
            "Retry");
        var (lastError4, _) = state.GetLastError();
        Assert.NotNull(lastError4);
        Assert.DoesNotContain("Secret", lastError4!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH] during sync", lastError4!, StringComparison.Ordinal);

        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException(@"Cannot read \\server\My Share\file.txt on host"),
            "Retry");
        var (lastError5, _) = state.GetLastError();
        Assert.NotNull(lastError5);
        Assert.DoesNotContain("Share", lastError5!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH] on host", lastError5!, StringComparison.Ordinal);

        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException("Blocked file:///C:/My Folder/x.txt from load"),
            "Retry");
        var (lastError6, _) = state.GetLastError();
        Assert.NotNull(lastError6);
        Assert.DoesNotContain("Folder", lastError6!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH] from load", lastError6!, StringComparison.Ordinal);

        // POSIX spaced path segments and leaf filename while still keeping repo slug intact
        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException("Read /mnt/my data/secret file.txt during sync of openRuyi-Project/openRuyi"),
            "Retry");
        var (lastError7, _) = state.GetLastError();
        Assert.NotNull(lastError7);
        Assert.DoesNotContain("data/", lastError7!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", lastError7!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file.txt", lastError7!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH] during sync of openRuyi-Project/openRuyi", lastError7!, StringComparison.Ordinal);

        state.RecordError(
            "abaci_bot.Services.Test",
            new IOException("Missing /state/bot-state.json for openRuyi-Project/openRuyi"),
            "Retry");
        var (lastError8, _) = state.GetLastError();
        Assert.NotNull(lastError8);
        Assert.DoesNotContain("/state/bot-state.json", lastError8!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED_PATH] for openRuyi-Project/openRuyi", lastError8!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BotControlPlaneService_StatusQuery_DisplaysOpenBlockedIssue_AndDegradesCleanlyOnSearchError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bcp-blocked-status-{Guid.NewGuid():N}");
        try
        {
            using var store = new JsonBotStateStore(Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir }));
            var gitHub = Substitute.For<IGitHubService>();
            var perm = Substitute.For<IRepositoryPermissionService>();
            var pagedSearch = Substitute.For<IPagedIssueSearchGateway>();
            var blockedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(blockedIssue, 123);
            typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(blockedIssue, "<!-- abaci-obs-blocked: curl -->");

            pagedSearch.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
                .Returns(new GitHubSearchPageResult<Issue>(new[] { blockedIssue }, 1, false));

            var localeOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { DefaultLocale = "en", StateDirectory = tempDir });
            var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
            var localeResolver = new LocaleResolver(localeOpts, store);
            var svc = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts, pagedIssueSearch: pagedSearch);

            var enCtx = new PullRequestContext(
                gitHubService: gitHub,
                teamName: "captains",
                headers: null,
                eventType: PullRequestEventType.IssueComment,
                owner: "owner",
                repo: "repo",
                prNumber: 50,
                headSha: "sha",
                baseBranch: "main",
                isDraft: false,
                isMerged: false,
                title: "test",
                body: "@abaci-bot status curl",
                senderLogin: "alice",
                authorLogin: "alice",
                action: "created",
                existingLabels: Array.Empty<string>(),
                commentAction: Octokit.Webhooks.Events.IssueComment.IssueCommentAction.Created);

            string? enReply = null;
            gitHub.When(g => g.CreateCommentAsync("owner", "repo", 50, Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(ci => enReply = ci.ArgAt<string>(3));

            await svc.HandleCommandAsync(enCtx, "abaci-bot", new ParsedBotCommand("status", "curl"), "alice");
            Assert.NotNull(enReply);
            Assert.Contains("- **Open blocked issue**: #123", enReply!, StringComparison.Ordinal);

            // Switch caller to Chinese locale and confirm localization
            store.SetUserLocale("alice", "zh");
            string? zhReply = null;
            gitHub.When(g => g.CreateCommentAsync("owner", "repo", 50, Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(ci => zhReply = ci.ArgAt<string>(3));

            await svc.HandleCommandAsync(enCtx, "abaci-bot", new ParsedBotCommand("status", "curl"), "alice");
            Assert.NotNull(zhReply);
            Assert.Contains("- **阻塞跟踪 Issue**：#123", zhReply!, StringComparison.Ordinal);

            // Fail-soft: search exception must still post status report without throwing
            pagedSearch.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
                .Returns<Task<GitHubSearchPageResult<Issue>>>(_ => throw new HttpRequestException("Search 503"));
            string? fallbackReply = null;
            gitHub.When(g => g.CreateCommentAsync("owner", "repo", 50, Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(ci => fallbackReply = ci.ArgAt<string>(3));

            await svc.HandleCommandAsync(enCtx, "abaci-bot", new ParsedBotCommand("status", "curl"), "alice");
            Assert.NotNull(fallbackReply);
            Assert.Contains("- **失败冷却**：无", fallbackReply!, StringComparison.Ordinal);

            // S5 + S8: truncated search index surfaces note, and decayed failure (>30d) shows None/无
            var decayedDir = Path.Combine(tempDir, "decayed");
            Directory.CreateDirectory(decayedDir);
            var decayedStatePath = Path.Combine(decayedDir, "bot-state.json");
            File.WriteAllText(decayedStatePath, $$"""
                {
                  "Repositories": {
                    "owner/repo": {
                      "PackageFailures": {
                        "curl": {
                          "ConsecutiveFailures": 2,
                          "CooldownUntil": null,
                          "ObsBlockedUntil": null,
                          "LastFailureAt": "{{DateTimeOffset.UtcNow.AddDays(-45):O}}"
                        }
                      }
                    }
                  }
                }
                """);
            using var decayedStore = new JsonBotStateStore(Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = decayedDir
            }));
            var truncatedSearch = Substitute.For<IPagedIssueSearchGateway>();
            truncatedSearch.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>())
                .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 1200, true));

            var decayedSvc = new BotControlPlaneService(
                gitHub,
                perm,
                decayedStore,
                new LocaleResolver(localeOpts, decayedStore),
                pkgOpts,
                pagedIssueSearch: truncatedSearch);

            string? truncatedEnReply = null;
            gitHub.When(g => g.CreateCommentAsync("owner", "repo", 50, Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(ci => truncatedEnReply = ci.ArgAt<string>(3));
            await decayedSvc.HandleCommandAsync(enCtx, "abaci-bot", new ParsedBotCommand("status", "curl"), "alice");
            Assert.NotNull(truncatedEnReply);
            Assert.Contains("- **Failure cooldown**: None", truncatedEnReply!, StringComparison.Ordinal);
            Assert.Contains("search index truncated", truncatedEnReply!, StringComparison.OrdinalIgnoreCase);

            decayedStore.SetUserLocale("alice", "zh");
            string? truncatedZhReply = null;
            gitHub.When(g => g.CreateCommentAsync("owner", "repo", 50, Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(ci => truncatedZhReply = ci.ArgAt<string>(3));
            await decayedSvc.HandleCommandAsync(enCtx, "abaci-bot", new ParsedBotCommand("status", "curl"), "alice");
            Assert.NotNull(truncatedZhReply);
            Assert.Contains("- **失败冷却**：无", truncatedZhReply!, StringComparison.Ordinal);
            Assert.Contains("搜索索引已截断", truncatedZhReply!, StringComparison.Ordinal);
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
    public void JsonBotStateStore_NullEntriesAndMissingOrUnreadableFile_HandledSafely()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"abaci-state-s1-s2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var statePath = Path.Combine(tempDir, "bot-state.json");
            File.WriteAllText(statePath, """
                {
                  "UserLocales": { "alice": null, "bob": { "Locale": "zh", "UpdatedAt": "2026-11-24T00:00:00Z" } },
                  "PendingEphemeralComments": [ null ],
                  "Cursors": { "k1": null, "k2": "v2" },
                  "Repositories": {
                    "empty/repo": null,
                    "org/repo": {
                      "IgnoredPackages": { "nullpkg": null },
                      "PackageFailures": { "nullfail": null },
                      "RecordedFailedPrs": [1, 2]
                    }
                  }
                }
                """);

            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir
            });

            using (var store = new JsonBotStateStore(opts))
            {
                Assert.Null(store.GetUserLocale("alice"));
                Assert.Equal("zh", store.GetUserLocale("bob"));
                Assert.Equal("v2", store.GetCursorWatermark("k2"));
                Assert.Null(store.GetCursorWatermark("k1"));
                Assert.Empty(store.GetIgnoredPackages("org", "repo"));
                Assert.False(store.TryMarkFailedPrRecorded("org", "repo", 1));
            }

            // Missing file from fileReader returns empty state cleanly (FileNotFoundException).
            using (var missingStore = new JsonBotStateStore(
                opts,
                logger: null,
                fileReader: _ => throw new FileNotFoundException("missing", statePath),
                fileMover: null))
            {
                Assert.Null(missingStore.GetUserLocale("bob"));
            }

            // Unreadable file fails closed even when File.Exists would return false or reader denies access.
            var ex = Assert.Throws<InvalidOperationException>(() => new JsonBotStateStore(
                opts,
                logger: null,
                fileReader: _ => throw new UnauthorizedAccessException("permission denied"),
                fileMover: null));
            Assert.Contains("Refusing to back up or start with empty state", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class CapturingLoggerProvider(List<(LogLevel Level, string Message)> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);
        public void Dispose() { }

        private sealed class CapturingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public MutableTimeProvider(DateTimeOffset initial) => _utcNow = initial;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}

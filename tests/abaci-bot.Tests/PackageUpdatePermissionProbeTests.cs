using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class PackageUpdatePermissionProbeTests
{
    private readonly IPackageUpdatePermissionProbeGateway _gateway = Substitute.For<IPackageUpdatePermissionProbeGateway>();

    private static IConfiguration BuildConfig(
        string? teamName = "captains",
        bool cveEnabled = false,
        string? cveRepo = null,
        bool milestoneEnabled = false,
        IReadOnlyList<string>? milestoneRepos = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["GitHubApp:TeamName"] = teamName
        };
        if (cveEnabled)
        {
            values["CveActiveWatch:Enabled"] = "true";
            values["CveActiveWatch:TargetRepository"] = cveRepo;
        }
        if (milestoneEnabled)
        {
            values["MilestoneSync:Enabled"] = "true";
            if (milestoneRepos != null)
            {
                for (var i = 0; i < milestoneRepos.Count; i++)
                {
                    values[$"MilestoneSync:RepositoryAllowList:{i}"] = milestoneRepos[i];
                }
            }
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task StartAsync_WhenDisabledAndNoBotPatConversationGuard_SetsDisabledWithoutProbing()
    {
        var runtimeHealth = new RuntimeHealthState();
        var probe = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);

        Assert.Equal("disabled", probe.Status);
        Assert.Equal("disabled", runtimeHealth.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.DidNotReceiveWithAnyArgs().VerifyPackageUpdateWritePermissionAsync(default!, default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().VerifyBotPatTeamReadPermissionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenEnabledAndDryRunWithoutBotPat_SetsDryRunWithoutProbingWrite()
    {
        var runtimeHealth = new RuntimeHealthState();
        var probe = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = true,
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("dry-run", probe.Status);
        Assert.Equal("dry-run", runtimeHealth.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.DidNotReceiveWithAnyArgs().VerifyPackageUpdateWritePermissionAsync(default!, default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().VerifyBotPatTeamReadPermissionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenEnabledAndLive_VerifiesWritePermission_AndAbortsOnFailure()
    {
        var healthOk = new RuntimeHealthState();
        var probeOk = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthOk);

        await probeOk.StartAsync(CancellationToken.None);

        Assert.Equal("verified", probeOk.Status);
        Assert.Equal("verified", healthOk.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.Received(1).VerifyPackageUpdateWritePermissionAsync("openRuyi-Project", "openRuyi", Arg.Any<CancellationToken>());

        _gateway.ClearReceivedCalls();
        _gateway.VerifyPackageUpdateWritePermissionAsync("openRuyi-Project", "openRuyi", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Contents='read'"));

        var healthFail = new RuntimeHealthState();
        var probeFail = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthFail);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probeFail.StartAsync(CancellationToken.None));
        Assert.Equal("failed", probeFail.Status);
        Assert.Equal("failed", healthFail.GetCapabilityStatus("packageUpdatePermission"));
        Assert.Contains("Contents: Read and write", ex.Message);
        Assert.Contains("Actions: Read-only", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenBotPatSetEvenWithPackageUpdateDisabled_VerifiesTeamReadAcrossActiveAndFallbackRepos()
    {
        _gateway.VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "openRuyi", "captains", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("missing read:org"));

        var healthFail = new RuntimeHealthState();
        var probeFail = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = false,
                BotPat = "ghp_pat_without_read_org"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
            {
                Enabled = true,
                DryRun = true,
                RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
            }),
            BuildConfig("captains"),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthFail);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probeFail.StartAsync(CancellationToken.None));

        Assert.Equal("failed", probeFail.Status);
        Assert.Equal("failed", healthFail.GetCapabilityStatus("packageUpdatePermission"));
        Assert.Contains("read:org", ex.Message);
        await _gateway.DidNotReceiveWithAnyArgs().VerifyPackageUpdateWritePermissionAsync(default!, default!, default);

        // When all optional modules are disabled, BotPat still switches GitHubService globally so TargetRepository fallback is probed
        _gateway.ClearReceivedCalls();
        _gateway.VerifyBotPatTeamReadPermissionAsync(Arg.Any<string>(), Arg.Any<string>(), "captains", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var healthFallback = new RuntimeHealthState();
        var probeFallback = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = false,
                BotPat = "ghp_valid_pat",
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig("captains"),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthFallback);

        await probeFallback.StartAsync(CancellationToken.None);

        Assert.Equal("verified", probeFallback.Status);
        Assert.Equal("verified", healthFallback.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.Received(1).VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "openRuyi", "captains", Arg.Any<CancellationToken>());

        // Deduplicates team probes once per unique organization across CveActiveWatch + MilestoneSync allowlists
        _gateway.ClearReceivedCalls();
        var healthMulti = new RuntimeHealthState();
        var probeMulti = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = false,
                BotPat = "ghp_valid_pat",
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(
                teamName: "captains",
                cveEnabled: true,
                cveRepo: "openRuyi-Project/openRuyi",
                milestoneEnabled: true,
                milestoneRepos: new[] { "openRuyi-Project/openRuyi", "openRuyi-Project/homepage", "other-org/packages" }),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthMulti);

        await probeMulti.StartAsync(CancellationToken.None);

        Assert.Equal("verified", healthMulti.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.Received(1).VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "openRuyi", "captains", Arg.Any<CancellationToken>());
        await _gateway.DidNotReceive().VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "homepage", "captains", Arg.Any<CancellationToken>());
        await _gateway.Received(1).VerifyBotPatTeamReadPermissionAsync("other-org", "packages", "captains", Arg.Any<CancellationToken>());

        // BotPat + invalid fallback TargetRepository with no module targets fails fast
        var healthBadFallback = new RuntimeHealthState();
        var probeBadFallback = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = false,
                BotPat = "ghp_valid_pat",
                TargetRepository = "not-a-repo"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig("captains"),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthBadFallback);

        var exBad = await Assert.ThrowsAsync<InvalidOperationException>(() => probeBadFallback.StartAsync(CancellationToken.None));
        Assert.Equal("failed", healthBadFallback.GetCapabilityStatus("packageUpdatePermission"));
        Assert.Contains("TargetRepository", exBad.Message);
    }

    [Fact]
    public async Task StartAsync_WhenCveEnabledWithOmittedTargetRepository_UsesOptionsDefaultAndProbesOrgTeam()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHubApp:TeamName"] = "captains",
                ["CveActiveWatch:Enabled"] = "true"
                // CveActiveWatch:TargetRepository intentionally omitted to verify CveActiveWatchOptions default applies
            })
            .Build();

        var runtimeHealth = new RuntimeHealthState();
        var probe = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = false,
                BotPat = "ghp_valid_pat",
                TargetRepository = "custom-org/custom-repo"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            config,
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("verified", runtimeHealth.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.Received(1).VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "openRuyi", "captains", Arg.Any<CancellationToken>());
        await _gateway.DidNotReceive().VerifyBotPatTeamReadPermissionAsync("custom-org", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_DiRegistration_ResolvesOptionsAndRuntimeHealthState()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHubApp:TeamName"] = "captains",
                ["PackageUpdate:Enabled"] = "false",
                ["PackageUpdate:BotPat"] = "ghp_valid_pat",
                ["CveActiveWatch:Enabled"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<RuntimeHealthState>();
        services.AddSingleton(_gateway);
        services.Configure<PackageUpdateOptions>(config.GetSection("PackageUpdate"));
        services.Configure<ConversationGuardOptions>(config.GetSection("ConversationGuard"));
        services.Configure<CveActiveWatchOptions>(config.GetSection("CveActiveWatch"));
        services.Configure<MilestoneSyncOptions>(config.GetSection("MilestoneSync"));
        services.AddSingleton<PackageUpdatePermissionProbe>();

        await using var sp = services.BuildServiceProvider();
        var probe = sp.GetRequiredService<PackageUpdatePermissionProbe>();
        var health = sp.GetRequiredService<RuntimeHealthState>();

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("verified", health.GetCapabilityStatus("packageUpdatePermission"));
        await _gateway.Received(1).VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "openRuyi", "captains", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_MultipleInstances_KeepIsolatedRuntimeHealthCapabilities()
    {
        var healthA = new RuntimeHealthState();
        var probeA = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthA);
        await probeA.StartAsync(CancellationToken.None);

        var healthB = new RuntimeHealthState();
        var probeB = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig(),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            healthB);
        await probeB.StartAsync(CancellationToken.None);

        Assert.Equal("verified", healthA.GetCapabilityStatus("packageUpdatePermission"));
        Assert.Equal("disabled", healthB.GetCapabilityStatus("packageUpdatePermission"));
    }

    [Fact]
    public async Task StartAsync_WhenFineGrainedPatReturnsPartial_SetsPartialStatusAndPublishesHealth()
    {
        _gateway.ClearReceivedCalls();
        _gateway.VerifyPackageUpdateWritePermissionAsync("openRuyi-Project", "openRuyi", Arg.Any<CancellationToken>())
            .Returns("partial");
        _gateway.VerifyBotPatTeamReadPermissionAsync("openRuyi-Project", "openRuyi", "captains", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var health = new RuntimeHealthState();
        var probe = new PackageUpdatePermissionProbe(
            Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Enabled = true,
                DryRun = false,
                BotPat = "github_pat_finegrained",
                TargetRepository = "openRuyi-Project/openRuyi"
            }),
            Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = false }),
            BuildConfig("captains"),
            _gateway,
            NullLogger<PackageUpdatePermissionProbe>.Instance,
            health);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("partial", probe.Status);
        Assert.Equal("partial", health.GetCapabilityStatus("packageUpdatePermission"));
    }
}

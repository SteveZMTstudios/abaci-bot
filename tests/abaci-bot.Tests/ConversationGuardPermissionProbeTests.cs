using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class ConversationGuardPermissionProbeTests
{
    private readonly IRepositoryPermissionService _permissionService = Substitute.For<IRepositoryPermissionService>();

    [Fact]
    public async Task StartAsync_WhenDisabled_SetsDisabledAndSkipsProbe()
    {
        var runtimeHealth = new RuntimeHealthState();
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = false,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);

        Assert.Equal("disabled", probe.Status);
        Assert.Equal("disabled", runtimeHealth.GetCapabilityStatus("conversationGuardPermission"));
        await _permissionService.DidNotReceiveWithAnyArgs().VerifyCollaboratorPermissionProbeAsync(default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenEnabledWithNoRepositories_SetsNoRepositories()
    {
        var runtimeHealth = new RuntimeHealthState();
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string>()
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("no-repositories", probe.Status);
        Assert.Equal("no-repositories", runtimeHealth.GetCapabilityStatus("conversationGuardPermission"));
        await _permissionService.DidNotReceiveWithAnyArgs().VerifyCollaboratorPermissionProbeAsync(default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenProbeSucceeds_SetsVerified()
    {
        var runtimeHealth = new RuntimeHealthState();
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("verified", probe.Status);
        Assert.Equal("verified", runtimeHealth.GetCapabilityStatus("conversationGuardPermission"));
        await _permissionService.Received(1).VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>());
        await _permissionService.DidNotReceiveWithAnyArgs().VerifyArchivePermissionProbeAsync(default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_WhenArchiveEnabledAndProbeSucceeds_VerifiesArchivePermissionAndSetsVerified()
    {
        var runtimeHealth = new RuntimeHealthState();
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            ArchiveEnabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance,
            runtimeHealth);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("verified", probe.Status);
        Assert.Equal("verified", runtimeHealth.GetCapabilityStatus("conversationGuardPermission"));
        await _permissionService.Received(1).VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>());
        await _permissionService.Received(1).VerifyArchivePermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenArchiveEnabledAndArchiveProbeFails_AbortsStartupWithFatalException()
    {
        var runtimeHealth = new RuntimeHealthState();
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            ArchiveEnabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        _permissionService.VerifyArchivePermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Administration='read'"));

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance,
            runtimeHealth);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));

        Assert.Equal("failed", probe.Status);
        Assert.Equal("failed", runtimeHealth.GetCapabilityStatus("conversationGuardPermission"));
        Assert.Contains("PackageUpdate:BotPat", ex.Message);
        Assert.Contains("cannot execute archivePullRequest", ex.Message);
        Assert.Contains("ArchiveEnabled=false", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenProbeThrowsForbidden_AbortsStartupWithFatalException()
    {
        var runtimeHealth = new RuntimeHealthState();
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        _permissionService.VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ForbiddenException(CreateResponse(HttpStatusCode.Forbidden)));

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance,
            runtimeHealth);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));

        Assert.Equal("failed", probe.Status);
        Assert.Equal("failed", runtimeHealth.GetCapabilityStatus("conversationGuardPermission"));
        Assert.Contains("Metadata: Read-only", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenCancelled_RethrowsOperationCanceledException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });

        _permissionService.VerifyCollaboratorPermissionProbeAsync("owner", "repo", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());

        var probe = new ConversationGuardPermissionProbe(
            options,
            _permissionService,
            NullLogger<ConversationGuardPermissionProbe>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.StartAsync(CancellationToken.None));
    }

    private static IResponse CreateResponse(HttpStatusCode statusCode)
    {
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(statusCode);
        return response;
    }
}

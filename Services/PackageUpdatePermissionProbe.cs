using abaci_bot.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Startup probe that verifies all required repository permissions (<c>Contents: Read and write</c>,
/// <c>Pull requests: Read and write</c>, <c>Issues: Read and write</c>, and <c>Actions: Read-only</c> in GitHub App mode;
/// <c>push+</c> repository access and read-only <c>Actions</c> in PAT mode) when <see cref="PackageUpdateOptions.Enabled"/>
/// is live (<see cref="PackageUpdateOptions.DryRun"/> is <see langword="false"/>), and verifies organization team-read
/// permission once per unique organization whenever global <c>PackageUpdate:BotPat</c> and <c>GitHubApp:TeamName</c>
/// are configured (falling back to <see cref="PackageUpdateOptions.TargetRepository"/> when no optional module is active).
/// </summary>
public class PackageUpdatePermissionProbe : IHostedService
{
    private readonly PackageUpdateOptions _packageUpdate;
    private readonly ConversationGuardOptions _conversationGuard;
    private readonly CveActiveWatchOptions _cveActiveWatch;
    private readonly MilestoneSyncOptions _milestoneSync;
    private readonly string? _teamName;
    private readonly IPackageUpdatePermissionProbeGateway _probeGateway;
    private readonly ILogger<PackageUpdatePermissionProbe> _logger;
    private readonly RuntimeHealthState? _runtimeHealth;

    public string Status { get; private set; } = "disabled";

    public PackageUpdatePermissionProbe(
        IOptions<PackageUpdateOptions> packageUpdateOptions,
        IOptions<ConversationGuardOptions> conversationGuardOptions,
        IConfiguration configuration,
        IPackageUpdatePermissionProbeGateway probeGateway,
        ILogger<PackageUpdatePermissionProbe> logger,
        RuntimeHealthState? runtimeHealth = null,
        IOptions<CveActiveWatchOptions>? cveActiveWatchOptions = null,
        IOptions<MilestoneSyncOptions>? milestoneSyncOptions = null)
    {
        _packageUpdate = packageUpdateOptions.Value;
        _conversationGuard = conversationGuardOptions.Value;
        _cveActiveWatch = cveActiveWatchOptions?.Value ?? BindOptions<CveActiveWatchOptions>(configuration, "CveActiveWatch");
        _milestoneSync = milestoneSyncOptions?.Value ?? BindOptions<MilestoneSyncOptions>(configuration, "MilestoneSync");
        _teamName = configuration["GitHubApp:TeamName"]?.Trim();
        _probeGateway = probeGateway;
        _logger = logger;
        _runtimeHealth = runtimeHealth;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var hasBotPat = !string.IsNullOrWhiteSpace(_packageUpdate.BotPat);
        var shouldProbeTeam = hasBotPat && !string.IsNullOrWhiteSpace(_teamName);

        if (!_packageUpdate.Enabled && !shouldProbeTeam)
        {
            SetStatus("disabled");
            _logger.LogInformation("PackageUpdate permission probe: DISABLED in configuration.");
            return;
        }

        try
        {
            if (_packageUpdate.Enabled && !_packageUpdate.DryRun)
            {
                if (!TryParseRepo(_packageUpdate.TargetRepository, out var targetOwner, out var targetRepo))
                {
                    throw new InvalidOperationException(
                        $"PackageUpdate:TargetRepository '{_packageUpdate.TargetRepository}' must be formatted as 'owner/repo'.");
                }

                await _probeGateway.VerifyPackageUpdateWritePermissionAsync(targetOwner, targetRepo, cancellationToken);
            }

            if (shouldProbeTeam)
            {
                var teamProbeRepos = CollectTeamProbeRepositories();
                if (teamProbeRepos.Count == 0)
                {
                    throw new InvalidOperationException(
                        "PackageUpdate:BotPat and GitHubApp:TeamName are configured, but no valid 'owner/repo' target was found for organization team-read probing. " +
                        "Configure a valid 'owner/repo' PackageUpdate:TargetRepository or clear PackageUpdate:BotPat.");
                }

                foreach (var (owner, repo) in teamProbeRepos)
                {
                    await _probeGateway.VerifyBotPatTeamReadPermissionAsync(owner, repo, _teamName!, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus("failed");
            var fatalMessage =
                $"PackageUpdate startup permission probe failed ({ex.Message}). " +
                "Ensure the GitHub App holds 'Contents: Read and write', 'Pull requests: Read and write', 'Issues: Read and write', and 'Actions: Read-only' when PackageUpdate:Enabled=true (and DryRun=false), " +
                "or if PackageUpdate:BotPat is configured, ensure the PAT holds 'Contents: write', 'Issues: write', 'Pull requests: write', " +
                "'Actions: read', 'Organization Members: read' (classic 'read:org'), and 'push+' repository access (required when PackageUpdate runs live or ConversationGuard:Enabled=true).";
            _logger.LogCritical(ex, "{FatalMessage}", fatalMessage);
            throw new InvalidOperationException(fatalMessage, ex);
        }

        SetStatus((_packageUpdate.Enabled && _packageUpdate.DryRun) ? "dry-run" : "verified");
        _logger.LogInformation(
            "PackageUpdate permission probe completed with status '{Status}' (Enabled={Enabled}, DryRun={DryRun}, HasBotPat={HasBotPat}).",
            Status,
            _packageUpdate.Enabled,
            _packageUpdate.DryRun,
            hasBotPat);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SetStatus(string status)
    {
        Status = status;
        _runtimeHealth?.SetCapabilityStatus("packageUpdatePermission", status);
    }

    private List<(string Owner, string Repo)> CollectTeamProbeRepositories()
    {
        var repos = new List<(string Owner, string Repo)>();
        // Organization team membership is org-scoped (owner/teamName), so probing one repo per unique owner
        // avoids redundant API calls across multi-repository allowlists.
        var seenOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_packageUpdate.Enabled && TryParseRepo(_packageUpdate.TargetRepository, out var targetOwner, out var targetRepo))
        {
            AddRepository(repos, seenOwners, targetOwner, targetRepo);
        }

        if (_conversationGuard.Enabled)
        {
            foreach (var entry in RepositoryAllowList.ParseRepositories(
                         _conversationGuard.RepositoryAllowList,
                         _logger,
                         "ConversationGuard RepositoryAllowList"))
            {
                AddRepository(repos, seenOwners, entry.Owner, entry.Repo);
            }
        }

        if (_cveActiveWatch.Enabled &&
            TryParseRepo(_cveActiveWatch.TargetRepository, out var cveOwner, out var cveRepo))
        {
            AddRepository(repos, seenOwners, cveOwner, cveRepo);
        }

        if (_milestoneSync.Enabled)
        {
            foreach (var entry in RepositoryAllowList.ParseRepositories(
                         _milestoneSync.RepositoryAllowList,
                         _logger,
                         "MilestoneSync RepositoryAllowList"))
            {
                AddRepository(repos, seenOwners, entry.Owner, entry.Repo);
            }
        }

        // BotPat switches GitHubService credentials globally (affecting WorkflowStateModule team lookups on webhooks),
        // so fall back to TargetRepository whenever no optional module supplied a probe repository.
        if (repos.Count == 0 && TryParseRepo(_packageUpdate.TargetRepository, out var fallbackOwner, out var fallbackRepo))
        {
            AddRepository(repos, seenOwners, fallbackOwner, fallbackRepo);
        }

        return repos;
    }

    private static void AddRepository(
        List<(string Owner, string Repo)> repos,
        HashSet<string> seenOwners,
        string owner,
        string repo)
    {
        if (seenOwners.Add(owner))
        {
            repos.Add((owner, repo));
        }
    }

    private static T BindOptions<T>(IConfiguration configuration, string sectionName) where T : class, new()
    {
        var options = new T();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }

    private static bool TryParseRepo(string? repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        var parts = repository.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        owner = parts[0];
        repo = parts[1];
        return true;
    }
}

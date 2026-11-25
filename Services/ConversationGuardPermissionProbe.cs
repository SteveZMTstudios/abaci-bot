using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Startup probe that verifies whether the GitHub App installation holds elevated permissions
/// required to query collaborator permissions when <see cref="ConversationGuardOptions.Enabled"/> is true.
/// Fails fast on startup when elevated permissions are missing so governance never silently degrades.
/// </summary>
public class ConversationGuardPermissionProbe : IHostedService
{
    private readonly ConversationGuardOptions _options;
    private readonly IRepositoryPermissionService _permissionService;
    private readonly ILogger<ConversationGuardPermissionProbe> _logger;
    private readonly RuntimeHealthState? _runtimeHealth;

    public string Status { get; private set; } = "disabled";

    public ConversationGuardPermissionProbe(
        IOptions<ConversationGuardOptions> options,
        IRepositoryPermissionService permissionService,
        ILogger<ConversationGuardPermissionProbe> logger,
        RuntimeHealthState? runtimeHealth = null)
    {
        _options = options.Value;
        _permissionService = permissionService;
        _logger = logger;
        _runtimeHealth = runtimeHealth;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            SetStatus("disabled");
            _logger.LogInformation("ConversationGuard permission probe: DISABLED in configuration (minimal GitHub App permissions active).");
            return;
        }

        var repositories = RepositoryAllowList.ParseRepositories(
            _options.RepositoryAllowList,
            _logger,
            "ConversationGuard RepositoryAllowList");

        if (repositories.Count == 0)
        {
            SetStatus("no-repositories");
            _logger.LogWarning("ConversationGuard is enabled but RepositoryAllowList has no valid 'owner/repo' targets; skipping permission probe.");
            return;
        }

        foreach (var (owner, repo) in repositories)
        {
            try
            {
                await _permissionService.VerifyCollaboratorPermissionProbeAsync(owner, repo, cancellationToken);
                if (_options.ArchiveEnabled)
                {
                    await _permissionService.VerifyArchivePermissionProbeAsync(owner, repo, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetStatus("failed");
                var fatalMessage = _options.ArchiveEnabled
                    ? $"ConversationGuard startup permission probe failed on '{owner}/{repo}' ({ex.Message}). " +
                      "When ConversationGuard:Enabled=true and ArchiveEnabled=true, archiving/unarchiving pull requests via GraphQL " +
                      "requires a user PAT (PackageUpdate:BotPat) with 'push+' access (for collaborator probing), 'triage+' access (for archive), " +
                      "plus global PAT scopes ('Contents: write', 'Issues: write', 'Pull requests: write', 'Actions: read', 'Organization Members: read' / 'read:org'); " +
                      "GitHub App installation tokens cannot execute archivePullRequest even with Administration: Read & write. " +
                      "Configure a compliant PackageUpdate:BotPat, set ConversationGuard:ArchiveEnabled=false (recommended for App-only deployments), " +
                      "or set ConversationGuard:Enabled=false."
                    : $"ConversationGuard startup permission probe failed on '{owner}/{repo}' ({ex.Message}). " +
                      "When ConversationGuard:Enabled=true (including DryRun=true so authorization simulation matches production), " +
                      "GitHub App collaborator permission queries require mandatory 'Metadata: Read-only' permission (or in PAT mode, " +
                      "'push+' repository access plus global PAT scopes: Contents/Issues/Pull requests write, Actions read, and read:org / Organization Members read). " +
                      "Ensure the required permission is granted or set ConversationGuard:Enabled=false.";
                _logger.LogCritical(ex, "{FatalMessage}", fatalMessage);
                throw new InvalidOperationException(fatalMessage, ex);
            }
        }

        SetStatus("verified");
        _logger.LogInformation(
            "ConversationGuard permission probe verified collaborator access across {Count} repository(ies) (DryRun={DryRun}).",
            repositories.Count,
            _options.DryRun);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SetStatus(string status)
    {
        Status = status;
        _runtimeHealth?.SetCapabilityStatus("conversationGuardPermission", status);
    }
}

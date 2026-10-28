using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// PR Target release branch classification module (Priority: 25).
/// Classifies pull requests into Target: Rolling or Target: LTS based on target repository and base branch.
/// Automatically harmonizes with LabelMutexEngine for single-choice Target label mutual exclusion.
/// </summary>
public class TargetClassificationModule : IPullRequestModule
{
    private readonly TargetClassificationOptions _options;

    public static readonly IReadOnlyList<string> ManagedTargetLabels = new[]
    {
        GitHubLabels.TargetRolling,
        GitHubLabels.TargetLts
    };

    public string ModuleName => "TargetClassificationModule";

    public int Priority => 25;

    public TargetClassificationModule(IOptions<TargetClassificationOptions>? options = null)
    {
        _options = options?.Value ?? new TargetClassificationOptions();
    }

    /// <summary>
    /// Evaluates PR lifecycle actions that may affect target branch classification.
    /// <para>
    /// Note on Edited action handling (N-12):
    /// We intentionally process PullRequestAction.Edited without inspecting payload 'changes.base'.
    /// Target classification is a pure in-memory calculation (reading context.BaseBranch with zero network/file I/O),
    /// and ReconcileLabels produces no label additions or removals if the base branch remains unchanged.
    /// This design avoids fragile payload introspection while remaining completely idempotent and zero-cost.
    /// </para>
    /// </summary>
    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.Edited);
    }

    public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var desiredTarget = DetermineTarget(context);
        context.ReconcileLabels(ManagedTargetLabels, desiredTarget);
        return Task.CompletedTask;
    }

    private string? DetermineTarget(PullRequestContext context)
    {
        var baseBranch = context.BaseBranch;

        // 1. LTS branch prefix matching on unified repository (primary workflow: e.g. "lts/24.03", "lts-24.03")
        if (!string.IsNullOrWhiteSpace(baseBranch) &&
            _options.LtsBranchPrefixes.Any(p => baseBranch.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return GitHubLabels.TargetLts;
        }

        // 2. Dedicated LTS repositories (optional / legacy compatibility: e.g. openRuyi-LTS)
        if (_options.LtsRepositories.Any(r => string.Equals(r, context.Repo, StringComparison.OrdinalIgnoreCase)))
        {
            return GitHubLabels.TargetLts;
        }

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            return GetFallbackTarget();
        }

        // 3. Rolling standard branches (main, master, rolling)
        if (_options.RollingBranches.Any(b => string.Equals(b, baseBranch, StringComparison.OrdinalIgnoreCase)))
        {
            return GitHubLabels.TargetRolling;
        }

        // 4. Default target fallback for custom or unmapped base branches
        return GetFallbackTarget();
    }

    private string? GetFallbackTarget()
    {
        if (string.Equals(_options.DefaultTarget, "LTS", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubLabels.TargetLts;
        }

        if (string.Equals(_options.DefaultTarget, "Rolling", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubLabels.TargetRolling;
        }

        // null or unrecognized string leaves target unclassified (silent)
        return null;
    }
}

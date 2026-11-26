using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// RPM Spec build system and CI file analysis module (Priority: 30).
/// Inspects modified file paths to assign CI and build system (autotools, cmake, meson, etc.) labels.
/// Reconciles its complete managed-label set to clear stale classifications.
/// </summary>
public partial class BuildSystemAnalysisModule : IPullRequestModule
{
    private readonly ILogger<BuildSystemAnalysisModule>? _logger;

    public BuildSystemAnalysisModule(ILogger<BuildSystemAnalysisModule>? logger = null)
    {
        _logger = logger;
    }

    public string ModuleName => "BuildSystemAnalysisModule";

    public int Priority => 30;

    /// <summary>
    /// Complete set of labels managed by this module across CI and build systems.
    /// Excludes Target:* release branch labels, which are governed by TargetClassificationModule.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ManagedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GitHubLabels.Ci,
        GitHubLabels.BuildSystemAutotools,
        GitHubLabels.BuildSystemCmake,
        GitHubLabels.BuildSystemGolangModule,
        GitHubLabels.BuildSystemGolang,
        GitHubLabels.BuildSystemRustCrate,
        GitHubLabels.BuildSystemRust,
        GitHubLabels.BuildSystemMeson,
        GitHubLabels.BuildSystemPyproject,
        GitHubLabels.BuildSystemMisc
    };

    IReadOnlyCollection<string> IPullRequestModule.ManagedLabels => ManagedLabels;

    [GeneratedRegex(@"^\s*BuildSystem\s*:\s*([a-zA-Z0-9_\-]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BuildSystemRegex();

    /// <summary>
    /// Evaluates file changes whenever pull request contents are created or updated.
    /// </summary>
    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var files = await context.ChangedFiles.Value;
        var desiredLabels = new ConcurrentBag<string>();

        foreach (var file in files)
        {
            // Workflows and CI configuration modifications
            if (FileClassificationHelper.IsCiFile(file.FileName))
            {
                desiredLabels.Add(GitHubLabels.Ci);
            }
        }

        var specFiles = files
            .Where(f => !string.Equals(f.Status, "removed", StringComparison.OrdinalIgnoreCase) &&
                        f.FileName.EndsWith(".spec", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int missingSpecCount = 0;
        if (specFiles.Count > 0)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(specFiles, parallelOptions, async (file, ct) =>
            {
                string content;
                try
                {
                    content = await context.GetFileContentAsync(file.FileName, context.HeadSha, file.Sha, ct);
                }
                catch (Exception ex) when (
                    !TransientErrorPolicy.IsRateLimitOrAbuse(ex) &&
                    (ex is NotFoundException or ForbiddenException ||
                     ex is ApiException { StatusCode: HttpStatusCode.Gone }))
                {
                    // Isolate unavailable fork .spec reads (403/404/410) so remaining pipeline modules (Status: New, Target:*, Type:*)
                    // are not aborted and existing BuildSystem:* labels are not erroneously stripped.
                    Interlocked.Increment(ref missingSpecCount);
                    _logger?.LogWarning(
                        ex,
                        "Spec file '{FileName}' returned inaccessible status for {Owner}/{Repo}#{PrNumber} (head={HeadSha}); skipping BuildSystem classification rewrite.",
                        file.FileName, context.Owner, context.Repo, context.PrNumber, context.HeadSha);
                    return;
                }

                // Identify declared build systems in RPM spec files (flexible whitespace handling)
                var match = BuildSystemRegex().Match(content);

                if (match.Success)
                {
                    var system = match.Groups[1].Value.ToLowerInvariant();
                    switch (system)
                    {
                        case "autotools":
                            desiredLabels.Add(GitHubLabels.BuildSystemAutotools);
                            break;
                        case "cmake":
                            desiredLabels.Add(GitHubLabels.BuildSystemCmake);
                            break;
                        case "golangmodule":
                            desiredLabels.Add(GitHubLabels.BuildSystemGolangModule);
                            break;
                        case "golang":
                            desiredLabels.Add(GitHubLabels.BuildSystemGolang);
                            break;
                        case "rustcrate":
                            desiredLabels.Add(GitHubLabels.BuildSystemRustCrate);
                            break;
                        case "rust":
                            desiredLabels.Add(GitHubLabels.BuildSystemRust);
                            break;
                        case "meson":
                            desiredLabels.Add(GitHubLabels.BuildSystemMeson);
                            break;
                        case "pyproject":
                            desiredLabels.Add(GitHubLabels.BuildSystemPyproject);
                            break;
                        default:
                            desiredLabels.Add(GitHubLabels.BuildSystemMisc);
                            break;
                    }
                }
                else
                {
                    desiredLabels.Add(GitHubLabels.BuildSystemMisc);
                }
            });
        }

        var uniqueLabels = new HashSet<string>(desiredLabels, StringComparer.OrdinalIgnoreCase);

        if (Volatile.Read(ref missingSpecCount) > 0)
        {
            if (uniqueLabels.Contains(GitHubLabels.Ci))
            {
                context.LabelsToAdd.Add(GitHubLabels.Ci);
            }
            return;
        }

        // Reconcile complete managed label set: add current desired labels, remove stale ones
        context.ReconcileLabels(ManagedLabels, label => uniqueLabels.Contains(label));
    }
}

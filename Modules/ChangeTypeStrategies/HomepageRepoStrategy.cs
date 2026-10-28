using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit;

namespace abaci_bot.Modules.ChangeTypeStrategies;

/// <summary>
/// Change type classification strategy for the community portal and documentation homepage repository.
/// Categorizes PRs into Type: Frontend and Type: Documentation.
/// </summary>
public class HomepageRepoStrategy : IChangeTypeStrategy
{
    private readonly ChangeTypeClassificationOptions _options;

    public HomepageRepoStrategy(IOptions<ChangeTypeClassificationOptions>? options = null)
    {
        _options = options?.Value ?? new ChangeTypeClassificationOptions();
    }

    public int Priority => 10;

    public bool AppliesTo(string repositoryName)
    {
        return _options.HomepageRepositories.Any(r => string.Equals(r, repositoryName, StringComparison.OrdinalIgnoreCase));
    }

    public Task EvaluateChangeTypesAsync(PullRequestContext context, IReadOnlyList<PullRequestFile> files)
    {
        if (files.Count == 0)
            return Task.CompletedTask;

        bool hasFrontendChanges = false;
        bool allDocs = true;

        foreach (var file in files)
        {
            var path = file.FileName;

            if (!FileClassificationHelper.IsDocumentationFile(path, _options))
            {
                allDocs = false;
            }

            bool isFrontend = _options.FrontendExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (isFrontend)
            {
                hasFrontendChanges = true;
            }
        }

        if (hasFrontendChanges)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeFrontend);
        }

        // Pure documentation changes (exclusively docs, mutually exclusive with frontend changes)
        if (allDocs && !hasFrontendChanges)
        {
            context.LabelsToAdd.Add(GitHubLabels.TypeDocumentation);
        }

        return Task.CompletedTask;
    }
}

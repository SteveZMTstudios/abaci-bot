using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.PullRequest;

namespace soroban_bot.Services;

public class GitHubWebhookProcessor : WebhookEventProcessor
{
    private readonly GitHubService _github;

    public GitHubWebhookProcessor(GitHubService github)
    {
        _github = github;
    }

    protected override async ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
    {
        var prNumber = (int)pullRequestEvent.PullRequest.Number;
        var owner = pullRequestEvent.Repository.Owner.Login;
        var repo = pullRequestEvent.Repository.Name;
        var headSha = pullRequestEvent.PullRequest.Head.Sha;

        // Handle PR when opened or synchronized (new commit)
        if (action == PullRequestAction.Opened || action == PullRequestAction.Synchronize)
        {
            // Get user email from PR commits (via base repo API, works for both public and private forks)
            string? userEmail = null;
            try
            {
                userEmail = await _github.GetPullRequestAuthorEmailAsync(owner, repo, prNumber);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to get commit author email for PR #{prNumber}: {ex.Message}");
            }
            
            // First, let's check if the user is using an educational email domain.
            await AnalyzeUserAndLabelPR(owner, repo, prNumber, userEmail);
            // Then, let's analyze the content of the PR and add labels accordingly.
            await AnalyzeFilesAndLabelPR(owner, repo, prNumber, headSha);
        }
        // When PR is closed, remove/cleanup labels
        else if (action == PullRequestAction.Closed)
        {
            await _github.RemoveLabelAsync(owner, repo, prNumber, "needs-review");
        }
    }

    private async Task AnalyzeUserAndLabelPR(string owner, string repo, int prNumber, string? userEmail)
    {
        var domain = ExtractEmailDomain(userEmail);
        if (string.IsNullOrWhiteSpace(domain))
            return;

        // Staff are excluded
        if (domain.Equals("iscas.ac.cn", StringComparison.OrdinalIgnoreCase))
            return;

        var labelsToAdd = new List<string>();

        // For intern, there are several types of E-mail:
        // 1) xxx.oerv@isrc.iscas.ac.cn
        // 2) xxx.or@isrc.iscas.ac.cn
        // 3) xxx.riscv@isrc.iscas.ac.cn
        if (IsInternEmail(userEmail))
            labelsToAdd.Add("Community: Student contribution");
        else
        // Any other domain counted as community contribution.
            labelsToAdd.Add("Community: Contribution");

        if (labelsToAdd.Any())
        {
            await _github.AddLabelsAsync(owner, repo, prNumber, labelsToAdd.Distinct().ToArray());
        }

    }

    private async Task AnalyzeFilesAndLabelPR(string owner, string repo, int prNumber, string sha)
    {
        var files = await _github.GetPullRequestFilesAsync(owner, repo, prNumber);
        var labelsToAdd = new List<string>();

        foreach (var file in files)
        {
            if (file.FileName.StartsWith(".github/"))
                labelsToAdd.Add("CI");

            // Check the file content only when the file is not removed.
            if (file.Status != "removed")
            {
                var content = await _github.GetFileContentAsync(owner, repo, file.FileName, sha);
                // Check if the file have "BuildSystem" line.
                // If it does, Add the corresponding label.
                // Otherwise, Add the "BuildSystem: misc" tag instead.
                // If it's not a RPM spec file, don't check the content.
                if (file.FileName.EndsWith(".spec"))
                {
                    if (content.Contains("BuildSystem:    autotools", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: autotools");
                    else if (content.Contains("BuildSystem:    cmake", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: cmake");
                    else if (content.Contains("BuildSystem:    golangmodule", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: golangmodule");
                    else if (content.Contains("BuildSystem:    golang", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: golang");
                    else if (content.Contains("BuildSystem:    rustcrate", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: rustcrate");
                    else if (content.Contains("BuildSystem:    rust", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: rust");
                    else if (content.Contains("BuildSystem:    meson", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: meson");
                    else if (content.Contains("BuildSystem:    pyproject", StringComparison.OrdinalIgnoreCase))
                        labelsToAdd.Add("BuildSystem: pyproject");
                    else
                        labelsToAdd.Add("BuildSystem: misc");
                }
            }

            // TODO: What if, LTS.
            labelsToAdd.Add("Target: Rolling");
            
        }

        if (labelsToAdd.Any())
        {
            await _github.AddLabelsAsync(owner, repo, prNumber, labelsToAdd.Distinct().ToArray());
        }
    }

    private static string? ExtractEmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        return email[(atIndex + 1)..].Trim();
    }

    private static bool IsInternEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalizedEmail = email.Trim();

        return normalizedEmail.EndsWith(".oerv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".or@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".riscv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase);
    }
}

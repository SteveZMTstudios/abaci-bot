using abaci_bot.Services;
using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="ConversationGuardOptions"/>.
/// </summary>
public class ConversationGuardOptionsValidator : IValidateOptions<ConversationGuardOptions>
{
    private readonly IConfiguration? _configuration;

    public ConversationGuardOptionsValidator(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, ConversationGuardOptions options)
    {
        var failures = new List<string>();

        if (options.Enabled &&
            options.ArchiveEnabled &&
            _configuration != null &&
            string.IsNullOrWhiteSpace(_configuration["PackageUpdate:BotPat"]))
        {
            failures.Add(
                $"{nameof(options.ArchiveEnabled)} cannot be enabled in GitHub App-only mode because GitHub's " +
                "archivePullRequest GraphQL mutation requires a user role (triage or higher) " +
                "and always returns FORBIDDEN for GitHub App installation tokens. Set ConversationGuard:ArchiveEnabled=false " +
                "(recommended) or configure a triage+ user PAT via PackageUpdate:BotPat.");
        }

        if (options.InactiveCloseDays is < 7 or > 365)
        {
            failures.Add($"{nameof(options.InactiveCloseDays)} must be between 7 and 365 (got {options.InactiveCloseDays}).");
        }

        if (options.SweepIntervalHours is < 1 or > 72)
        {
            failures.Add($"{nameof(options.SweepIntervalHours)} must be between 1 and 72 (got {options.SweepIntervalHours}).");
        }

        if (options.AuthorLockConfirmTimeoutMinutes is < 1 or > 60)
        {
            failures.Add($"{nameof(options.AuthorLockConfirmTimeoutMinutes)} must be between 1 and 60 (got {options.AuthorLockConfirmTimeoutMinutes}).");
        }

        if (options.MaxLocksPerSweep is < 1 or > 500)
        {
            failures.Add($"{nameof(options.MaxLocksPerSweep)} must be between 1 and 500 (got {options.MaxLocksPerSweep}).");
        }

        foreach (var invalid in RepositoryAllowList.FindInvalidEntries(options.RepositoryAllowList))
        {
            failures.Add($"{nameof(options.RepositoryAllowList)} entry '{invalid}' must be formatted as 'owner/repo'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

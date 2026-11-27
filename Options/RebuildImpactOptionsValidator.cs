using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="RebuildImpactOptions"/>.
/// Ensures rebuild thresholds and optional documentation links are well-formed before startup completes.
/// </summary>
public class RebuildImpactOptionsValidator : IValidateOptions<RebuildImpactOptions>
{
    public ValidateOptionsResult Validate(string? name, RebuildImpactOptions options)
    {
        var failures = new List<string>();

        if (options.RefreshIntervalHours <= 0)
        {
            failures.Add($"{nameof(options.RefreshIntervalHours)} must be positive (got {options.RefreshIntervalHours}).");
        }

        if (options.BlockThreshold < 0)
        {
            failures.Add($"{nameof(options.BlockThreshold)} cannot be negative (got {options.BlockThreshold}).");
        }

        if (options.MaxDepth is <= 0)
        {
            failures.Add($"{nameof(options.MaxDepth)} must be null or >= 1 (got {options.MaxDepth}).");
        }

        if (!string.Equals(options.CalculationMode, "Transitive", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.CalculationMode, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{nameof(options.CalculationMode)} must be 'Transitive' or 'Direct' (got '{options.CalculationMode}').");
        }

        DocumentationLinkValidator.Validate(options.DocumentationLinks, nameof(options.DocumentationLinks), failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

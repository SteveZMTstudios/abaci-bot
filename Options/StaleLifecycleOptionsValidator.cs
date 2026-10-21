using Microsoft.Extensions.Options;

namespace abaci_bot.Options;

/// <summary>
/// Startup validator for <see cref="StaleLifecycleOptions"/>.
/// Enforces fail-fast bounds checking so negative or invalid parameters do not lead to accidental bulk operations.
/// </summary>
public class StaleLifecycleOptionsValidator : IValidateOptions<StaleLifecycleOptions>
{
    public ValidateOptionsResult Validate(string? name, StaleLifecycleOptions options)
    {
        var failures = new List<string>();

        if (options.StaleDays <= 0)
        {
            failures.Add($"{nameof(options.StaleDays)} must be positive (got {options.StaleDays}).");
        }

        if (options.CloseDays <= 0)
        {
            failures.Add($"{nameof(options.CloseDays)} must be positive (got {options.CloseDays}).");
        }

        if (options.CheckIntervalHours <= 0)
        {
            failures.Add($"{nameof(options.CheckIntervalHours)} must be positive (got {options.CheckIntervalHours}).");
        }

        if (options.RequestThrottleMilliseconds < 0)
        {
            failures.Add($"{nameof(options.RequestThrottleMilliseconds)} cannot be negative (got {options.RequestThrottleMilliseconds}).");
        }

        if (options.MinRateLimitRemaining < 0)
        {
            failures.Add($"{nameof(options.MinRateLimitRemaining)} cannot be negative (got {options.MinRateLimitRemaining}).");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

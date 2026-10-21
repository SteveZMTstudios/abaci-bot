using abaci_bot.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace abaci_bot.Tests;

public class StaleLifecycleOptionsValidatorTests
{
    private readonly StaleLifecycleOptionsValidator _validator = new();

    [Fact]
    public void Validate_ValidOptions_ReturnsSuccess()
    {
        var options = new StaleLifecycleOptions
        {
            StaleDays = 30,
            CloseDays = 7,
            CheckIntervalHours = 24,
            RequestThrottleMilliseconds = 500,
            MinRateLimitRemaining = 100
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void Validate_NonPositiveStaleDays_ReturnsFailure(int staleDays)
    {
        var options = new StaleLifecycleOptions
        {
            StaleDays = staleDays,
            CloseDays = 7,
            CheckIntervalHours = 24
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.StaleDays)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-7)]
    public void Validate_NonPositiveCloseDays_ReturnsFailure(int closeDays)
    {
        var options = new StaleLifecycleOptions
        {
            StaleDays = 30,
            CloseDays = closeDays,
            CheckIntervalHours = 24
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.CloseDays)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveCheckIntervalHours_ReturnsFailure(int hours)
    {
        var options = new StaleLifecycleOptions
        {
            StaleDays = 30,
            CloseDays = 7,
            CheckIntervalHours = hours
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.CheckIntervalHours)));
    }

    [Fact]
    public void Validate_NegativeThrottle_ReturnsFailure()
    {
        var options = new StaleLifecycleOptions
        {
            StaleDays = 30,
            CloseDays = 7,
            CheckIntervalHours = 24,
            RequestThrottleMilliseconds = -100
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.RequestThrottleMilliseconds)));
    }

    [Fact]
    public void Validate_NegativeRateLimitRemaining_ReturnsFailure()
    {
        var options = new StaleLifecycleOptions
        {
            StaleDays = 30,
            CloseDays = 7,
            CheckIntervalHours = 24,
            MinRateLimitRemaining = -10
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(nameof(options.MinRateLimitRemaining)));
    }

    [Fact]
    public void ServiceCollection_WithValidateOnStart_ThrowsOptionsValidationExceptionOnStartup()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddOptions<StaleLifecycleOptions>()
            .Configure(o =>
            {
                o.StaleDays = -5; // Invalid
            })
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<StaleLifecycleOptions>, StaleLifecycleOptionsValidator>();

        var sp = services.BuildServiceProvider();
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
        {
            _ = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StaleLifecycleOptions>>().Value;
        });
    }
}

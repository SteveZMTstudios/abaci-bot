using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class CandidateSchedulerTests
{
    [Fact]
    public async Task ScheduleAndExecuteAsync_InterleavesOriginsByPublishedOutcome_PreventingStarvation()
    {
        var baseTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new List<UpdateCandidate>();
        for (int i = 0; i < 10; i++)
        {
            candidates.Add(new UpdateCandidate(
                $"lookout-pkg-{i:D2}", "1.0.0", "1.1.0", CandidateOrigin.Lookout, string.Empty,
                PublishedAt: baseTime.AddHours(i)));
        }
        candidates.Add(new UpdateCandidate(
            "rust-only-pkg", "0.1.0", "0.2.0", CandidateOrigin.RustCrate, string.Empty,
            PublishedAt: baseTime.AddHours(5)));

        var published = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 3,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(3, result.TotalPublished);
        Assert.Equal(new[] { "lookout-pkg-00", "rust-only-pkg", "lookout-pkg-01" }, published);
        Assert.Equal(2, result.ByOrigin[CandidateOrigin.Lookout].Published);
        Assert.Equal(10, result.ByOrigin[CandidateOrigin.Lookout].Discovered);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.RustCrate].Published);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.RustCrate].Discovered);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_SkippedCandidatesDoNotWasteSlotOrStarveOtherOrigin()
    {
        var baseTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new UpdateCandidate("lookout-skip-1", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, PublishedAt: baseTime.AddHours(1)),
            new UpdateCandidate("lookout-skip-2", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, PublishedAt: baseTime.AddHours(2)),
            new UpdateCandidate("lookout-ok", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, PublishedAt: baseTime.AddHours(3)),
            new UpdateCandidate("rust-ok", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty, PublishedAt: baseTime.AddHours(4))
        };

        var published = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 2,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                if (c.PackageName.StartsWith("lookout-skip", StringComparison.Ordinal))
                    return Task.FromResult(false);

                published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(2, result.TotalPublished);
        Assert.Equal(new[] { "lookout-ok", "rust-ok" }, published);
        Assert.Equal(2, result.ByOrigin[CandidateOrigin.Lookout].Skipped);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.Lookout].Published);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.RustCrate].Published);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_RotatesStartingOriginAcrossSweepsWhenQuotaIsOne()
    {
        var candidates = new[]
        {
            new UpdateCandidate("lookout-a", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty),
            new UpdateCandidate("rust-a", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty)
        };

        var sweep1Published = new List<string>();
        var r1 = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 1,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                sweep1Published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        var sweep2Published = new List<string>();
        var r2 = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 1,
            startOriginOffset: r1.NextOriginOffset,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                sweep2Published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(new[] { "lookout-a" }, sweep1Published);
        Assert.Equal(new[] { "rust-a" }, sweep2Published);
        Assert.Equal(2, r2.NextOriginOffset);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_IsolatesPerCandidateException_AndContinuesSweep()
    {
        var baseTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new UpdateCandidate("lookout-throws", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, PublishedAt: baseTime.AddHours(1)),
            new UpdateCandidate("lookout-ok", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, PublishedAt: baseTime.AddHours(2)),
            new UpdateCandidate("rust-ok", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty, PublishedAt: baseTime.AddHours(3))
        };

        var errors = new List<string>();
        var published = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 2,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                if (c.PackageName == "lookout-throws")
                    throw new HttpRequestException("422 Unprocessable Entity");
                published.Add(c.PackageName);
                return Task.FromResult(true);
            },
            onCandidateError: (c, _) => errors.Add(c.PackageName));

        Assert.Equal(2, result.TotalPublished);
        Assert.Equal(new[] { "lookout-throws" }, errors);
        Assert.Equal(new[] { "lookout-ok", "rust-ok" }, published);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.Lookout].Failed);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_PrioritizesSecurityAdvisoriesBeforeRoutine_AndOldestPublishedFirst()
    {
        var baseTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new UpdateCandidate("routine-newer", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: false, PublishedAt: baseTime.AddDays(5)),
            new UpdateCandidate("routine-older", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: false, PublishedAt: baseTime.AddDays(1)),
            new UpdateCandidate("security-advisory", "2.0", "2.1", CandidateOrigin.RustCrate, string.Empty, IsSecurityAdvisory: true, PublishedAt: baseTime.AddDays(10))
        };

        var order = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 3,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                order.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(new[] { "security-advisory", "routine-older", "routine-newer" }, order);
        Assert.Equal(baseTime.AddDays(1), result.ByOrigin[CandidateOrigin.Lookout].OldestPublishedAt);
        Assert.Equal(baseTime.AddDays(10), result.ByOrigin[CandidateOrigin.RustCrate].OldestPublishedAt);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_ReservesRoutineSlotWhenSecurityAdvisoriesFillCap()
    {
        var securityCandidates = Enumerable.Range(0, 3)
            .Select(i => new UpdateCandidate($"security-{i}", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true))
            .ToList();
        var rustRoutine = new UpdateCandidate("rust-routine", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty);

        var published = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            securityCandidates.Append(rustRoutine).ToList(),
            maxPublishes: 3,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(3, result.TotalPublished);
        Assert.Equal(new[] { "security-0", "security-1", "rust-routine" }, published);
        Assert.Equal(2, result.ByOrigin[CandidateOrigin.Lookout].Published);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.RustCrate].Published);
        Assert.Equal(3, result.ProcessedPackageNames.Count);
        Assert.DoesNotContain("security-2", result.ProcessedPackageNames);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_MaxOneAlternatesSecurityAndRoutineAcrossSweeps()
    {
        var security = new UpdateCandidate("security-only", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true);
        var routine = new UpdateCandidate("rust-routine", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty);
        var candidates = new[] { security, routine };

        var sweep1 = new List<string>();
        var r1 = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 1,
            startOriginOffset: 0,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                sweep1.Add(c.PackageName);
                return Task.FromResult(true);
            });

        var sweep2 = new List<string>();
        await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 1,
            startOriginOffset: r1.NextOriginOffset,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                sweep2.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(new[] { "security-only" }, sweep1);
        Assert.Equal(new[] { "rust-routine" }, sweep2);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_ReservedRoutineSlotReachesRustWhenOffsetStartsAtRust()
    {
        var candidates = new List<UpdateCandidate>
        {
            new("security-0", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true),
            new("security-1", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true),
            new("security-2", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true),
            new("lookout-routine", "2.0", "2.1", CandidateOrigin.Lookout, string.Empty),
            new("rust-routine", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty)
        };

        var published = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 3,
            startOriginOffset: 1,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(3, result.TotalPublished);
        Assert.Contains("rust-routine", published);
        Assert.Equal(1, result.ByOrigin[CandidateOrigin.RustCrate].Published);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_StopsTierWhenCanAttemptReturnsFalse_AndExcludesUnattemptedFromProcessed()
    {
        var candidates = new List<UpdateCandidate>
        {
            new("sec-0", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true),
            new("sec-1", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true),
            new("routine-0", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty),
            new("rust-0", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty)
        };

        var attemptedPackages = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 4,
            startOriginOffset: 0,
            canAttempt: (candidate, publishedSoFar) =>
                candidate.IsSecurityAdvisory || publishedSoFar < 1,
            tryPublishAsync: (c, _) =>
            {
                attemptedPackages.Add(c.PackageName);
                return Task.FromResult(true);
            });

        // Routine tier is blocked by canAttempt once sec-0 (+sec-1) already published >= 1 PR.
        Assert.Equal(2, result.TotalPublished);
        Assert.Equal(new[] { "sec-0", "sec-1" }, attemptedPackages);
        Assert.Equal(2, result.ProcessedPackageNames.Count);
        Assert.Contains("sec-0", result.ProcessedPackageNames);
        Assert.Contains("sec-1", result.ProcessedPackageNames);
        Assert.DoesNotContain("routine-0", result.ProcessedPackageNames);
        Assert.DoesNotContain("rust-0", result.ProcessedPackageNames);
        Assert.Equal(2, result.ByOrigin[CandidateOrigin.Lookout].Attempted);
        Assert.Equal(0, result.ByOrigin[CandidateOrigin.RustCrate].Attempted);
    }

    [Fact]
    public async Task ScheduleAndExecuteAsync_WithIntMinValueStartOffset_DoesNotOverflow()
    {
        var candidates = new List<UpdateCandidate>
        {
            new("sec-0", "1.0", "1.1", CandidateOrigin.Lookout, string.Empty, IsSecurityAdvisory: true),
            new("routine-0", "0.1", "0.2", CandidateOrigin.RustCrate, string.Empty)
        };

        var published = new List<string>();
        var result = await CandidateScheduler.ScheduleAndExecuteAsync(
            candidates,
            maxPublishes: 1,
            startOriginOffset: int.MinValue,
            canAttempt: (_, _) => true,
            tryPublishAsync: (c, _) =>
            {
                published.Add(c.PackageName);
                return Task.FromResult(true);
            });

        Assert.Equal(1, result.TotalPublished);
        Assert.Single(published);
        Assert.True(result.NextOriginOffset >= 0);
    }
}

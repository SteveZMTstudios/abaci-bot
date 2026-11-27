using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class AbiDiffServiceTests
{
    [Fact]
    public async Task ProcessAbiDiffRunner_MissingRpmFiles_ReturnsFailureWithoutInvokingProcess()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            AbipkgdiffExecutablePath = "nonexistent-abidiff-bin"
        });
        var runner = new ProcessAbiDiffRunner(options);

        var result = await runner.RunDiffAsync("C:/nonexistent/old.rpm", "C:/nonexistent/new.rpm");

        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing);
        Assert.Contains("Target RPM file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ProcessAbiDiffRunner_ExecutableNotFound_ReturnsToolMissingTrue()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "nonexistent-abidiff-tool-12345XYZ"
            });
            var runner = new ProcessAbiDiffRunner(options);

            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.True(result.ToolMissing);
            Assert.Contains("not found", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task AbiDiffService_DisabledInOptions_ReturnsFailureImmediately()
    {
        var runner = Substitute.For<IAbiDiffRunner>();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = false
        });
        var service = new AbiDiffService(runner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        Assert.False(result.Succeeded);
        Assert.Contains("disabled", result.Details);
        await runner.DidNotReceive().RunDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbiDiffService_Enabled_DelegatesToRunner()
    {
        var runner = Substitute.For<IAbiDiffRunner>();
        runner.RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Clean")));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true
        });
        var service = new AbiDiffService(runner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        Assert.True(result.Succeeded);
        Assert.Equal("Clean", result.Details);
        await runner.Received(1).RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbiDiffService_WithDebuginfo_DelegatesToDebuginfoRunnerOverload()
    {
        var runner = Substitute.For<IAbiDiffRunner>();
        runner.RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Clean with DWARF")));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true
        });
        var service = new AbiDiffService(runner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm");

        Assert.True(result.Succeeded);
        Assert.Equal("Clean with DWARF", result.Details);
        await runner.Received(1).RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ProcessAbiDiffRunner_BuildStartInfo_ConstructsAccurateAbipkgdiffCliContract()
    {
        var startInfo = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "/tmp/old.rpm", "/tmp/new.rpm");

        Assert.Equal("abipkgdiff", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(4, startInfo.ArgumentList.Count);
        Assert.Equal("--private-dso", startInfo.ArgumentList[0]);
        Assert.Equal("--dso-only", startInfo.ArgumentList[1]);
        Assert.Equal("/tmp/old.rpm", startInfo.ArgumentList[2]);
        Assert.Equal("/tmp/new.rpm", startInfo.ArgumentList[3]);
    }

    [Fact]
    public void InterpretExitCode_BitmaskInterpretations_AccuratelyDifferentiatesAbiChanges()
    {
        // 0 = clean
        var res0 = ProcessAbiDiffRunner.InterpretExitCode(0, "clean");
        Assert.True(res0.Succeeded);
        Assert.False(res0.HasAbiChange);
        Assert.False(res0.IsBreakingChange);

        // 4 = ABI change
        var res4 = ProcessAbiDiffRunner.InterpretExitCode(4, "diff");
        Assert.True(res4.Succeeded);
        Assert.True(res4.HasAbiChange);
        Assert.False(res4.IsBreakingChange);

        // 8 = Breaking change
        var res8 = ProcessAbiDiffRunner.InterpretExitCode(8, "breaking");
        Assert.True(res8.Succeeded);
        Assert.True(res8.HasAbiChange);
        Assert.True(res8.IsBreakingChange);

        // 1 or 2 = tool error
        var res1 = ProcessAbiDiffRunner.InterpretExitCode(1, "", "error");
        Assert.False(res1.Succeeded);
        Assert.Contains("abipkgdiff reported tool failure", res1.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithMissingRpmSupportStderr_ReturnsToolMissingFailure()
    {
        const string missingRpmStderr = "abipkgdiff: Support for rpm hasn't been enabled.  Please consider enabling it at package configure time";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", missingRpmStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("Support for rpm hasn't been enabled", result.ErrorMessage);
        Assert.Contains("missing RPM support", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_Ubuntu2404MissingRpm2cpio_ReturnsToolMissingFailure()
    {
        // Real-world Ubuntu 24.04 WSL output when abipkgdiff is present but rpm2cpio is missing:
        const string ubuntu2404Stderr =
            "rpm2cpio: command not found\n" +
            "cpio: premature end of archive\n" +
            "abipkgdiff: Error while extracting package .../demo-lib-2.0-1.x86_64.rpm\n" +
            "rpm2cpio: command not found\n" +
            "cpio: premature end of archive\n" +
            "abipkgdiff: Error while extracting package .../demo-lib-1.0-1.x86_64.rpm";

        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", ubuntu2404Stderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("missing tool or package extraction failure", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithEmptyStdoutAndNonEmptyStderr_ReturnsFailure()
    {
        const string unknownStderr = "some internal extraction failure occurred";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", unknownStderr);

        Assert.False(result.Succeeded);
        Assert.Contains("produced no comparison output", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithGenericErrorStderr_ReturnsFailureWithoutToolMissing()
    {
        const string errorStderr = "abipkgdiff: error: failed to open archive file";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, "", errorStderr);

        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing);
        Assert.Contains("abipkgdiff reported error on stderr", result.ErrorMessage);
    }

    [Fact]
    public async Task ProcessAbiDiffRunner_RpmSupportMissing_CachesUnavailableAndShortCircuitsSubsequentCalls()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var runner = new ProcessAbiDiffRunner(options);

        Assert.False(runner.IsRpmSupportUnavailable);

        runner.MarkRpmSupportUnavailable("abipkgdiff reported missing RPM support: Support for rpm hasn't been enabled.");
        Assert.True(runner.IsRpmSupportUnavailable);

        // Subsequent calls should short-circuit immediately without needing files on disk
        var result = await runner.RunDiffAsync("/nonexistent/old.rpm", "/nonexistent/new.rpm");

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("Support for rpm hasn't been enabled", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeOneWithMissingRpmSupportStderr_ReturnsToolMissingFailure()
    {
        // Ubuntu 22.04 exits with code 1 when libabigail lacks RPM support
        const string missingRpmStderr = "abipkgdiff: Support for rpm hasn't been enabled.  Please consider enabling it at package configure time";
        var result = ProcessAbiDiffRunner.InterpretExitCode(1, "", missingRpmStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("Support for rpm hasn't been enabled", result.ErrorMessage);
        Assert.Contains("missing RPM support", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCode134AbortCrash_ReturnsToolMissingFailure()
    {
        const string abortStderr =
            "mkdir: cannot create directory '/home/abaci': Permission denied\n" +
            "abipkgdiff: ../../tools/abipkgdiff.cc:811: package::extracted_packages_parent_dir(): Assertion `__abg_cond__' failed.\n" +
            "Aborted (core dumped)";

        var result = ProcessAbiDiffRunner.InterpretExitCode(134, "", abortStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("crashed or aborted", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCode137OomKilledWithoutAbortKeyword_ReturnsToolMissingFailure()
    {
        // Tests pure exitCode >= 128 branch without any "Aborted" or "Assertion" keywords in stderr
        const string killedStderr = "Killed\n";
        var result = ProcessAbiDiffRunner.InterpretExitCode(137, "", killedStderr);

        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("crashed or aborted (exit code: 137)", result.ErrorMessage);
    }

    [Fact]
    public void InterpretExitCode_ExitCode3InputError_ReturnsFailureWithoutToolMissing()
    {
        const string inputErrorStderr = "abipkgdiff: Catch2-3.14.0-999.or.src.rpm should be an RPM file";

        var result = ProcessAbiDiffRunner.InterpretExitCode(3, "", inputErrorStderr);

        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing); // Must NOT trigger fallback on invalid input
        Assert.Contains("input error", result.ErrorMessage);
    }

    [Fact]
    public async Task AbiDiffService_PrimaryRunnerToolMissing_FallsBackToManagedRunnerWhenEnabled()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        primaryRunner.RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Missing tool",
                ErrorMessage: "abipkgdiff executable not found",
                ToolMissing: true)));

        var fallbackRunner = new ManagedElfDiffRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = true
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        // Fallback was invoked (it fails on nonexistent files, not on missing tool)
        Assert.False(result.Succeeded);
        Assert.False(result.ToolMissing);
        Assert.Contains("Target RPM file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task AbiDiffService_PrimaryRunnerToolMissingWithDebuginfo_PassesDebuginfoToFallbackRunner()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        primaryRunner.RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Missing tool",
                ErrorMessage: "abipkgdiff executable not found",
                ToolMissing: true)));

        var fallbackRunner = Substitute.For<IAbiDiffRunner>();
        fallbackRunner.RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Managed symbol diff clean",
                IsIncomplete: true)));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = true
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm");

        Assert.True(result.Succeeded);
        Assert.True(result.IsIncomplete);
        await fallbackRunner.Received(1).RunDiffAsync("old.rpm", "new.rpm", "old-dbg.rpm", "new-dbg.rpm", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DependencyInjection_ResolvesAbiDiffServiceWithManagedFallback()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton<ManagedElfDiffRunner>();
        services.AddSingleton<ProcessAbiDiffRunner>();
        services.AddSingleton<IAbiDiffRunner>(sp => sp.GetRequiredService<ProcessAbiDiffRunner>());
        services.AddSingleton<IAbiDiffService>(sp =>
            new AbiDiffService(
                sp.GetRequiredService<ProcessAbiDiffRunner>(),
                sp.GetService<ManagedElfDiffRunner>(),
                sp.GetRequiredService<IOptions<AbiComplianceOptions>>(),
                sp.GetService<ILogger<AbiDiffService>>()));

        using var sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<IAbiDiffService>() as AbiDiffService;

        Assert.NotNull(service);
        Assert.IsType<ProcessAbiDiffRunner>(service.PrimaryRunner);
        Assert.IsType<ManagedElfDiffRunner>(service.FallbackRunner);
    }

    [Fact]
    public async Task AbiEngineProbe_MissingNativeWithoutAllowManagedFallback_ThrowsInvalidOperationException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool-xyz",
            EnableFallback = true,
            AllowManagedFallback = false
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        var prevEnv = Environment.GetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));
            Assert.Contains("unavailable or lacks RPM support", ex.Message);
            Assert.Contains("Startup aborted", ex.Message);
            Assert.Contains("https://github.com/openRuyi-Project/abaci-bot/blob/master/docs/modules/abi-compliance.md#5-abi-engine-tiers-and-operational-guidelines", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", prevEnv);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_MissingNativeWithAllowManagedFallback_SetsActiveEngineTierManaged()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool-xyz",
            EnableFallback = true,
            AllowManagedFallback = true
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("managed", AbiEngineProbe.ActiveEngineTier);
    }

    [Fact]
    public async Task AbiEngineProbe_MissingNativeWithEnvOverride_SetsActiveEngineTierManaged()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool-xyz",
            EnableFallback = true,
            AllowManagedFallback = false
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        var prevEnv = Environment.GetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", "1");
            await probe.StartAsync(CancellationToken.None);
            Assert.Equal("managed", AbiEngineProbe.ActiveEngineTier);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ABACI_ALLOW_MANAGED_FALLBACK", prevEnv);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_Disabled_SetsActiveEngineTierDisabled()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = false
        });
        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);

        Assert.Equal("disabled", AbiEngineProbe.ActiveEngineTier);
    }

    [Fact]
    public async Task AbiDiffService_PrimaryRunnerToolMissing_DoesNotFallBackWhenDisabled()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        primaryRunner.RunDiffAsync("old.rpm", "new.rpm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "Missing tool",
                ErrorMessage: "abipkgdiff executable not found",
                ToolMissing: true)));

        var fallbackRunner = new ManagedElfDiffRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = false
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);

        var result = await service.CompareRpmsAsync("old.rpm", "new.rpm");

        // Returns primary result without fallback
        Assert.False(result.Succeeded);
        Assert.True(result.ToolMissing);
        Assert.Contains("abipkgdiff executable not found", result.ErrorMessage);
    }

    [Fact]
    public async Task AbiEngineProbe_StrictEngineWithUnavailableTool_ThrowsInvalidOperationException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StrictEngine = true,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AbiEngineProbe_StandardModeWithUnavailableTool_DoesNotThrow()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StrictEngine = false,
            AllowManagedFallback = true,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AbiEngineProbe_StandardModeWithFallbackDisabled_LogsErrorAndDoesNotThrow()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StrictEngine = false,
            AllowManagedFallback = true,
            EnableFallback = false,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);
        await probe.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AbiEngineProbe_Disabled_DoesNotProbe()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = false,
            StrictEngine = true,
            AbipkgdiffExecutablePath = "nonexistent-tool-strictly-required-999"
        });

        var logger = Substitute.For<ILogger<AbiEngineProbe>>();
        var probe = new AbiEngineProbe(options, logger);

        await probe.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithRemovedBinariesInStdout_ReturnsBreakingChange()
    {
        const string stdout =
            "Removed binaries:\n" +
            "  [D] /usr/lib64/libdemo.so.1.0, SONAME: libdemo.so.1\n" +
            "Added binaries:\n" +
            "  [A] /usr/lib64/libdemo.so.2.0, SONAME: libdemo.so.2\n";

        var result = ProcessAbiDiffRunner.InterpretExitCode(0, stdout, string.Empty);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.True(result.IsBreakingChange);
        Assert.Equal(stdout, result.Details);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithAddedBinariesInStdout_ReturnsAbiChange()
    {
        const string stdout =
            "Added binaries:\n" +
            "  [A] /usr/lib64/libplugin.so.1.0, SONAME: libplugin.so.1\n";

        var result = ProcessAbiDiffRunner.InterpretExitCode(0, stdout, string.Empty);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
    }

    [Fact]
    public void InterpretExitCode_ExitCodeZeroWithBenignWarningStderr_ReturnsCleanSuccess()
    {
        const string warningStderr = "abipkgdiff: warning: could not find debug info file for /usr/lib64/libfoo.so.1";
        var result = ProcessAbiDiffRunner.InterpretExitCode(0, string.Empty, warningStderr);

        Assert.True(result.Succeeded);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Equal(warningStderr, result.Details);
    }

    [Fact]
    public void BuildStartInfo_IncludesPrivateDsoAndDsoOnlyAndDebugOptions()
    {
        var startInfoWithDso = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "old.rpm", "new.rpm", privateDso: true, dsoOnly: true);
        Assert.Contains("--private-dso", startInfoWithDso.ArgumentList);
        Assert.Contains("--dso-only", startInfoWithDso.ArgumentList);

        var startInfoWithoutDso = ProcessAbiDiffRunner.BuildStartInfo("abipkgdiff", "old.rpm", "new.rpm", privateDso: false, dsoOnly: false);
        Assert.DoesNotContain("--private-dso", startInfoWithoutDso.ArgumentList);
        Assert.DoesNotContain("--dso-only", startInfoWithoutDso.ArgumentList);

        var startInfoWithDebug = ProcessAbiDiffRunner.BuildStartInfo(
            "abipkgdiff", "old.rpm", "new.rpm",
            privateDso: true, dsoOnly: true,
            oldDebuginfoPath: "old-debug.rpm", newDebuginfoPath: "new-debug.rpm");
        Assert.Contains("--d1", startInfoWithDebug.ArgumentList);
        Assert.Contains("old-debug.rpm", startInfoWithDebug.ArgumentList);
        Assert.Contains("--d2", startInfoWithDebug.ArgumentList);
        Assert.Contains("new-debug.rpm", startInfoWithDebug.ArgumentList);

        var startInfoSingleOld = ProcessAbiDiffRunner.BuildStartInfo(
            "abipkgdiff", "old.rpm", "new.rpm",
            oldDebuginfoPath: "old-debug.rpm", newDebuginfoPath: null);
        Assert.Contains("--d1", startInfoSingleOld.ArgumentList);
        Assert.DoesNotContain("--d2", startInfoSingleOld.ArgumentList);

        var startInfoSingleNew = ProcessAbiDiffRunner.BuildStartInfo(
            "abipkgdiff", "old.rpm", "new.rpm",
            oldDebuginfoPath: null, newDebuginfoPath: "new-debug.rpm");
        Assert.DoesNotContain("--d1", startInfoSingleNew.ArgumentList);
        Assert.Contains("--d2", startInfoSingleNew.ArgumentList);
    }

    [Fact]
    public void AbiEngineProbe_GetProbeRpmV4AndV6_ReturnsValidRpmBytes()
    {
        var probeRpmV4 = AbiEngineProbe.GetProbeRpmV4();
        Assert.NotNull(probeRpmV4);
        Assert.True(probeRpmV4.Length > 96);
        Assert.Equal(0xED, probeRpmV4[0]);
        Assert.Equal(0xAB, probeRpmV4[1]);
        Assert.Equal(0xEE, probeRpmV4[2]);
        Assert.Equal(0xDB, probeRpmV4[3]);
        Assert.Equal(3, probeRpmV4[4]); // RPM v4 major version in lead is 3

        var probeRpmV6 = AbiEngineProbe.GetProbeRpmV6();
        Assert.NotNull(probeRpmV6);
        Assert.True(probeRpmV6.Length > 96);
        Assert.Equal(0xED, probeRpmV6[0]);
        Assert.Equal(0xAB, probeRpmV6[1]);
        Assert.Equal(0xEE, probeRpmV6[2]);
        Assert.Equal(0xDB, probeRpmV6[3]);
        Assert.Equal(4, probeRpmV6[4]); // RPM v6 major version in lead is 4
    }

    [Fact]
    public async Task AbiEngineProbe_ProbeNativeToolAsync_Cancelled_ReturnsFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync("dotnet", cts.Token);
        Assert.False(available);
        Assert.False(rpm);
        Assert.Contains("timed out", diagnostic);
    }

    [Fact]
    public void AbiEngineProbe_ProbeNativeTool_SynchronousWrapper_Works()
    {
        var (available, rpm, diagnostic) = AbiEngineProbe.ProbeNativeTool("nonexistent-tool-xyz-123");
        Assert.False(available);
        Assert.False(rpm);
        Assert.Contains("cannot be started", diagnostic);
    }

    [Fact]
    public async Task AbiEngineProbe_ProbeNativeToolAsync_WithNonAbipkgdiffBinary_ReportsRpmSupportUnavailable()
    {
        var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync("dotnet");
        Assert.True(available);
        Assert.False(rpm); // dotnet does not support abipkgdiff RPM self-diff!
        Assert.Contains("probe failed", diagnostic);
    }

    private static string CreateFakeExecutableScript(string windowsBatchContent, string unixShellContent)
    {
        var isWindows = OperatingSystem.IsWindows();
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake_tool_{Guid.NewGuid():N}" + (isWindows ? ".cmd" : ".sh"));
        if (isWindows)
        {
            File.WriteAllText(scriptPath, windowsBatchContent);
        }
        else
        {
            File.WriteAllText(scriptPath, unixShellContent);
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return scriptPath;
    }

    [Fact]
    public async Task AbiEngineProbe_NativeToolWithV6Support_DetectsSuccessfully()
    {
        var originalV6 = AbiEngineProbe.NativeSupportsV6;
        var scriptPath = CreateFakeExecutableScript(
            "@echo off\r\nif \"%1\"==\"--version\" (echo abipkgdiff 2.4 & exit /b 0)\r\nexit /b 0\r\n",
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then\n  echo \"abipkgdiff 2.4\"\n  exit 0\nfi\nexit 0\n");

        try
        {
            var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync(scriptPath);
            Assert.True(available);
            Assert.True(rpm);
            Assert.True(AbiEngineProbe.NativeSupportsV6);
            Assert.Contains("v4 and v6 support", diagnostic);

            var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                Enabled = true,
                AbipkgdiffExecutablePath = scriptPath
            });
            var logger = Substitute.For<ILogger<AbiEngineProbe>>();
            var probe = new AbiEngineProbe(options, logger);
            await probe.StartAsync(CancellationToken.None);
        }
        finally
        {
            AbiEngineProbe.NativeSupportsV6 = originalV6;
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_NativeToolWithoutRpmSupport_DetectsDisabledRpm()
    {
        var scriptPath = CreateFakeExecutableScript(
            "@echo off\r\necho Support for rpm hasn't been enabled 1>&2\r\nexit /b 1\r\n",
            "#!/bin/sh\necho \"Support for rpm hasn't been enabled\" >&2\nexit 1\n");

        try
        {
            var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync(scriptPath);
            Assert.True(available);
            Assert.False(rpm);
            Assert.Contains("without RPM support", diagnostic);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task AbiEngineProbe_NativeToolMissingRpm2Cpio_DetectsMissingExtractionTool()
    {
        var scriptPath = CreateFakeExecutableScript(
            "@echo off\r\nif \"%1\"==\"--version\" (echo abipkgdiff 2.4 & exit /b 0)\r\necho rpm2cpio: command not found 1>&2\r\nexit /b 127\r\n",
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then\n  echo \"abipkgdiff 2.4\"\n  exit 0\nfi\necho \"rpm2cpio: command not found\" >&2\nexit 127\n");

        try
        {
            var (available, rpm, diagnostic) = await AbiEngineProbe.ProbeNativeToolAsync(scriptPath);
            Assert.True(available);
            Assert.False(rpm);
            Assert.Contains("lacks rpm2cpio extraction tool", diagnostic);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ProcessAbiDiffRunner_NonExistentDebuginfoPaths_LogsWarning()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();
        var logger = Substitute.For<ILogger<ProcessAbiDiffRunner>>();
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool"
        });
        var runner = new ProcessAbiDiffRunner(options, logger);

        try
        {
            var result = await runner.RunDiffAsync(tempOld, tempNew, "missing-old-debug.rpm", "missing-new-debug.rpm");

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("missing-old-debug.rpm")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("missing-new-debug.rpm")),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 4 }, true)]
    [InlineData(new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 6 }, true)]
    [InlineData(new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 3 }, false)]
    [InlineData(new byte[] { 0xED, 0xAB, 0xEE }, false)]
    [InlineData(new byte[] { 0x00, 0xAB, 0xEE, 0xDB, 4 }, false)]
    [InlineData(new byte[] { 0xED, 0x00, 0xEE, 0xDB, 4 }, false)]
    [InlineData(new byte[] { 0xED, 0xAB, 0x00, 0xDB, 4 }, false)]
    [InlineData(new byte[] { 0xED, 0xAB, 0xEE, 0x00, 4 }, false)]
    public void IsRpmV6_AccuratelyDifferentiatesRpmFormats(byte[] headerBytes, bool expectedV6)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_rpmv6_{Guid.NewGuid():N}.rpm");
        try
        {
            File.WriteAllBytes(tempFile, headerBytes);
            var isV6 = AbiDiffService.IsRpmV6(tempFile);
            Assert.Equal(expectedV6, isV6);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsRpmV6_MissingFile_ReturnsFalse()
    {
        var isV6 = AbiDiffService.IsRpmV6("non-existent-missing-file-path-999.rpm");
        Assert.False(isV6);
    }

    [Fact]
    public async Task CompareRpmsAsync_WithRpmV6PackageAndNoNativeV6Support_FallsBackDirectlyToFallbackRunner()
    {
        var primaryRunner = Substitute.For<IAbiDiffRunner>();
        var fallbackRunner = Substitute.For<IAbiDiffRunner>();
        fallbackRunner.RunDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbiDiffResult(true, false, false, "Fallback Clean")));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            EnableFallback = true
        });

        var service = new AbiDiffService(primaryRunner, fallbackRunner, options);
        Assert.Same(primaryRunner, service.PrimaryRunner);
        Assert.Same(fallbackRunner, service.FallbackRunner);

        var tempV4 = Path.Combine(Path.GetTempPath(), $"v4_{Guid.NewGuid():N}.rpm");
        var tempV6 = Path.Combine(Path.GetTempPath(), $"v6_{Guid.NewGuid():N}.rpm");

        var originalV6 = AbiEngineProbe.NativeSupportsV6;
        AbiEngineProbe.NativeSupportsV6 = false;

        try
        {
            File.WriteAllBytes(tempV4, new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 3 });
            File.WriteAllBytes(tempV6, new byte[] { 0xED, 0xAB, 0xEE, 0xDB, 4 });

            var result = await service.CompareRpmsAsync(tempV4, tempV6);
            Assert.True(result.Succeeded);
            Assert.Equal("Fallback Clean", result.Details);
            await fallbackRunner.Received(1).RunDiffAsync(tempV4, tempV6, null, null, Arg.Any<CancellationToken>());
            await primaryRunner.DidNotReceive().RunDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AbiEngineProbe.NativeSupportsV6 = originalV6;
            if (File.Exists(tempV4)) File.Delete(tempV4);
            if (File.Exists(tempV6)) File.Delete(tempV6);
        }
    }

    [Fact]
    public void BuildStartInfo_WithWorkingDirectoryAndSigpipeReset_ConfiguresEnvWrapperAndTempDir()
    {
        var startInfo = ProcessAbiDiffRunner.BuildStartInfo(
            "/usr/bin/abipkgdiff",
            "/tmp/old.rpm",
            "/tmp/new.rpm",
            privateDso: true,
            dsoOnly: true,
            oldDebuginfoPath: "/tmp/old-debuginfo.rpm",
            newDebuginfoPath: "/tmp/new-debuginfo.rpm",
            workingDirectory: "/tmp/abaci-abi-123",
            resetSigpipeWithEnv: true);

        Assert.Equal("/usr/bin/env", startInfo.FileName);
        Assert.Equal("/tmp/abaci-abi-123", startInfo.WorkingDirectory);
        Assert.Equal("/tmp/abaci-abi-123", startInfo.Environment["TMPDIR"]);
        Assert.Equal(
            new[]
            {
                "--default-signal=PIPE",
                "/usr/bin/abipkgdiff",
                "--private-dso",
                "--dso-only",
                "--d1",
                "/tmp/old-debuginfo.rpm",
                "--d2",
                "/tmp/new-debuginfo.rpm",
                "/tmp/old.rpm",
                "/tmp/new.rpm"
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void IsTransientWriteError_MatchesArchiveWriteErrors_AndRejectsOtherFailures()
    {
        var failed = new AbiDiffResult(false, false, false, "Error writing archive: Write error", "failed");
        Assert.True(ProcessAbiDiffRunner.IsTransientWriteError(failed, "Error writing archive: Write error"));
        Assert.True(ProcessAbiDiffRunner.IsTransientWriteError(failed, "write error on pipe"));
        Assert.False(ProcessAbiDiffRunner.IsTransientWriteError(failed, "some other failure"));
        Assert.False(ProcessAbiDiffRunner.IsTransientWriteError(failed, string.Empty));

        var succeeded = new AbiDiffResult(true, false, false, "clean");
        Assert.False(ProcessAbiDiffRunner.IsTransientWriteError(succeeded, "Error writing archive: Write error"));

        var toolMissing = new AbiDiffResult(false, false, false, "missing", "missing", ToolMissing: true);
        Assert.False(ProcessAbiDiffRunner.IsTransientWriteError(toolMissing, "Error writing archive: Write error"));
    }

    [Fact]
    public async Task RunDiffAsync_TransientWriteError_RetriesOnceWithFreshWorkDirAndSucceeds()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r75_retry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var oldRpm = Path.Combine(tempRoot, "libfoo-1.0-1.x86_64.rpm");
        var newRpm = Path.Combine(tempRoot, "libfoo-1.0-2.x86_64.rpm");
        File.WriteAllText(oldRpm, "old-rpm-bytes");
        File.WriteAllText(newRpm, "new-rpm-bytes");

        var recordedWorkDirs = new List<string>();
        var attempts = 0;
        var runner = new ProcessAbiDiffRunner(
            Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "/usr/bin/abipkgdiff",
                TempExtractionDirectory = tempRoot
            }),
            logger: null,
            processExecutor: (startInfo, _) =>
            {
                attempts++;
                recordedWorkDirs.Add(startInfo.WorkingDirectory);
                Assert.True(Directory.Exists(startInfo.WorkingDirectory));
                Assert.Equal(startInfo.WorkingDirectory, startInfo.Environment["TMPDIR"]);
                Assert.Equal("/usr/bin/env", startInfo.FileName);
                Assert.Equal("--default-signal=PIPE", startInfo.ArgumentList[0]);
                Assert.Equal("/usr/bin/abipkgdiff", startInfo.ArgumentList[1]);

                return Task.FromResult(attempts == 1
                    ? (0, string.Empty, "Error writing archive: Write error")
                    : (4, "Functions changes summary: 0 Removed, 0 Changed, 1 Added function", string.Empty));
            },
            supportsEnvDefaultSigpipe: () => true);

        try
        {
            var result = await runner.RunDiffAsync(oldRpm, newRpm);

            Assert.True(result.Succeeded);
            Assert.True(result.HasAbiChange);
            Assert.False(result.IsBreakingChange);
            Assert.Equal(2, attempts);
            Assert.Equal(2, recordedWorkDirs.Count);
            Assert.NotEqual(recordedWorkDirs[0], recordedWorkDirs[1]);
            Assert.Empty(Directory.EnumerateDirectories(tempRoot, $"{ProcessAbiDiffRunner.RetainedWorkDirPrefix}*"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunDiffAsync_PersistentWriteError_RetriesOncePreservesSnapshotAndPrunesBeyondFive()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r75_persist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var oldRpm = Path.Combine(tempRoot, "libassuan-3.0.2-6.2.x86_64.rpm");
        var newRpm = Path.Combine(tempRoot, "libassuan-3.0.2-999.x86_64.rpm");
        var oldDebug = Path.Combine(tempRoot, "libassuan-debuginfo-3.0.2-6.2.x86_64.rpm");
        var newDebug = Path.Combine(tempRoot, "libassuan-debuginfo-3.0.2-999.x86_64.rpm");
        File.WriteAllText(oldRpm, "old-rpm");
        File.WriteAllText(newRpm, "new-rpm");
        File.WriteAllText(oldDebug, "old-debug");
        File.WriteAllText(newDebug, "new-debug");

        var logger = Substitute.For<ILogger<ProcessAbiDiffRunner>>();
        var totalProcessCalls = 0;
        var runner = new ProcessAbiDiffRunner(
            Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "/usr/bin/abipkgdiff",
                TempExtractionDirectory = tempRoot
            }),
            logger: logger,
            processExecutor: (_, _) =>
            {
                totalProcessCalls++;
                return Task.FromResult((0, string.Empty, "Error writing archive: Write error"));
            },
            supportsEnvDefaultSigpipe: () => false);

        try
        {
            AbiDiffResult? lastResult = null;
            for (var i = 0; i < 6; i++)
            {
                lastResult = await runner.RunDiffAsync(oldRpm, newRpm, oldDebug, newDebug);
            }

            Assert.NotNull(lastResult);
            Assert.False(lastResult.Succeeded);
            Assert.False(lastResult.HasAbiChange);
            Assert.Contains("Write error", lastResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            // Each of the 6 calls executes initial + 1 bounded retry = 12 attempts total.
            Assert.Equal(12, totalProcessCalls);

            var retainedDirs = Directory.EnumerateDirectories(tempRoot, $"{ProcessAbiDiffRunner.RetainedWorkDirPrefix}*").ToList();
            Assert.Equal(ProcessAbiDiffRunner.MaxRetainedFailureSnapshots, retainedDirs.Count);

            var sampleDir = retainedDirs[0];
            var diagPath = Path.Combine(sampleDir, ProcessAbiDiffRunner.DiagnosticFileName);
            Assert.True(File.Exists(diagPath));
            var diagContent = File.ReadAllText(diagPath);
            Assert.Contains("/usr/bin/abipkgdiff", diagContent, StringComparison.Ordinal);
            Assert.Contains("TMPDIR:", diagContent, StringComparison.Ordinal);
            Assert.Contains("HOME:", diagContent, StringComparison.Ordinal);
            Assert.Contains("ExitCode: 0", diagContent, StringComparison.Ordinal);
            Assert.Contains("Error writing archive: Write error", diagContent, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(sampleDir, "old-" + Path.GetFileName(oldRpm))));
            Assert.True(File.Exists(Path.Combine(sampleDir, "new-" + Path.GetFileName(newRpm))));
            Assert.True(File.Exists(Path.Combine(sampleDir, "old-debug-" + Path.GetFileName(oldDebug))));
            Assert.True(File.Exists(Path.Combine(sampleDir, "new-debug-" + Path.GetFileName(newDebug))));

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("diagnostic snapshot", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunDiffAsync_WhenInputFileRemovedBeforeSnapshot_StillReturnsOriginalAbiDiffResult()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r76_missing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var oldRpm = Path.Combine(tempRoot, "libfoo-1.0-1.x86_64.rpm");
        var newRpm = Path.Combine(tempRoot, "libfoo-1.0-2.x86_64.rpm");
        File.WriteAllText(oldRpm, "old-rpm");
        File.WriteAllText(newRpm, "new-rpm");

        var runner = new ProcessAbiDiffRunner(
            Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "/usr/bin/abipkgdiff",
                TempExtractionDirectory = tempRoot
            }),
            logger: null,
            processExecutor: (_, _) =>
            {
                if (File.Exists(oldRpm))
                {
                    File.Delete(oldRpm);
                }

                return Task.FromResult((1, string.Empty, "abipkgdiff fatal comparison error"));
            },
            supportsEnvDefaultSigpipe: () => false);

        try
        {
            var result = await runner.RunDiffAsync(oldRpm, newRpm);

            Assert.False(result.Succeeded);
            Assert.Contains("abipkgdiff reported tool failure (exit code: 1)", result.ErrorMessage, StringComparison.Ordinal);

            var retainedDir = Assert.Single(Directory.EnumerateDirectories(tempRoot, $"{ProcessAbiDiffRunner.RetainedWorkDirPrefix}*"));
            var diagText = File.ReadAllText(Path.Combine(retainedDir, ProcessAbiDiffRunner.DiagnosticFileName));
            Assert.Contains("(missing)", diagText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void PruneRetainedFailureDirectories_EnforcesMaxTotalBytesWhileKeepingNewestSnapshot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r76_sizecap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseUtc = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
            var dirs = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var dir = Path.Combine(tempRoot, $"{ProcessAbiDiffRunner.RetainedWorkDirPrefix}snap{i}");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, ProcessAbiDiffRunner.DiagnosticFileName), new byte[20]);
                File.WriteAllBytes(Path.Combine(dir, "payload.rpm"), new byte[100]);
                Directory.SetLastWriteTimeUtc(dir, baseUtc.AddMinutes(i));
                dirs.Add(dir);
            }

            // Each directory is 120 bytes; cap at 250 bytes keeps newest 2 (snap2 + snap1) and prunes oldest (snap0).
            ProcessAbiDiffRunner.PruneRetainedFailureDirectories(tempRoot, maxRetained: 5, maxTotalBytes: 250);

            Assert.False(Directory.Exists(dirs[0]));
            Assert.True(Directory.Exists(dirs[1]));
            Assert.True(Directory.Exists(dirs[2]));

            // Tight cap below single snapshot size still preserves the newest snapshot (snap2) for post-mortem triage.
            ProcessAbiDiffRunner.PruneRetainedFailureDirectories(tempRoot, maxRetained: 5, maxTotalBytes: 10);

            Assert.False(Directory.Exists(dirs[1]));
            Assert.True(Directory.Exists(dirs[2]));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunDiffAsync_WhenDiagnosticFileWriteFails_CleansUpOrphanWorkDirAndReturnsOriginalResult()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r77_orphan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var oldRpm = Path.Combine(tempRoot, "libfoo-1.0-1.x86_64.rpm");
        var newRpm = Path.Combine(tempRoot, "libfoo-1.0-2.x86_64.rpm");
        File.WriteAllText(oldRpm, "old-rpm");
        File.WriteAllText(newRpm, "new-rpm");

        var logger = Substitute.For<ILogger<ProcessAbiDiffRunner>>();
        string? capturedWorkDir = null;
        var runner = new ProcessAbiDiffRunner(
            Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "/usr/bin/abipkgdiff",
                TempExtractionDirectory = tempRoot
            }),
            logger: logger,
            processExecutor: (startInfo, _) =>
            {
                capturedWorkDir = startInfo.WorkingDirectory;
                // Pre-create a subdirectory with the exact diagnostic.err name to force File.WriteAllText failure cross-platform.
                Directory.CreateDirectory(Path.Combine(capturedWorkDir, ProcessAbiDiffRunner.DiagnosticFileName));
                return Task.FromResult((1, string.Empty, "abipkgdiff fatal comparison error"));
            },
            supportsEnvDefaultSigpipe: () => false);

        try
        {
            var result = await runner.RunDiffAsync(oldRpm, newRpm);

            Assert.False(result.Succeeded);
            Assert.Contains("abipkgdiff reported tool failure (exit code: 1)", result.ErrorMessage, StringComparison.Ordinal);
            Assert.NotNull(capturedWorkDir);
            Assert.False(Directory.Exists(capturedWorkDir));

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("Failed to persist full ABI diagnostic snapshot", StringComparison.Ordinal)),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunDiffAsync_WhenWorkDirCleanupThrows_LogsWorkDirWarningAndPreservesAbiDiffResult()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r78_cleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var oldRpm = Path.Combine(tempRoot, "libfoo-1.0-1.x86_64.rpm");
        var newRpm = Path.Combine(tempRoot, "libfoo-1.0-2.x86_64.rpm");
        File.WriteAllText(oldRpm, "old-rpm");
        File.WriteAllText(newRpm, "new-rpm");

        var logger = Substitute.For<ILogger<ProcessAbiDiffRunner>>();
        string? capturedWorkDir = null;
        var runner = new ProcessAbiDiffRunner(
            Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "/usr/bin/abipkgdiff",
                TempExtractionDirectory = tempRoot
            }),
            logger: logger,
            processExecutor: (startInfo, _) =>
            {
                capturedWorkDir = startInfo.WorkingDirectory;
                return Task.FromResult((0, string.Empty, string.Empty));
            },
            supportsEnvDefaultSigpipe: () => false,
            deleteDirectory: _ => throw new IOException("simulated read-only filesystem"));

        try
        {
            var result = await runner.RunDiffAsync(oldRpm, newRpm);

            Assert.True(result.Succeeded);
            Assert.False(result.HasAbiChange);
            Assert.NotNull(capturedWorkDir);

            logger.Received().Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(o =>
                    o.ToString()!.Contains("Failed to delete temporary ABI working directory", StringComparison.Ordinal) &&
                    o.ToString()!.Contains(capturedWorkDir, StringComparison.Ordinal)),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ProcessAbiDiffRunner_InterpretExitCode_127And126_ReturnToolMissingTrue()
    {
        var envNotFound = ProcessAbiDiffRunner.InterpretExitCode(
            127,
            string.Empty,
            "env: 'nonexistent-abipkgdiff': No such file or directory");
        Assert.False(envNotFound.Succeeded);
        Assert.True(envNotFound.ToolMissing);
        Assert.Contains("not found", envNotFound.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var bare127 = ProcessAbiDiffRunner.InterpretExitCode(127, string.Empty, string.Empty);
        Assert.False(bare127.Succeeded);
        Assert.True(bare127.ToolMissing);
        Assert.Contains("not found", bare127.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var permissionDenied = ProcessAbiDiffRunner.InterpretExitCode(
            126,
            string.Empty,
            "env: '/usr/bin/abipkgdiff': Permission denied");
        Assert.False(permissionDenied.Succeeded);
        Assert.True(permissionDenied.ToolMissing);
        Assert.Contains("cannot be invoked", permissionDenied.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var bare126 = ProcessAbiDiffRunner.InterpretExitCode(126, string.Empty, string.Empty);
        Assert.False(bare126.Succeeded);
        Assert.True(bare126.ToolMissing);
        Assert.Contains("cannot be invoked", bare126.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunDiffAsync_EnvWrapperExit127_ReturnsToolMissingTrueAndCleansUpWorkDir()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"abaci_r79_env127_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var oldRpm = Path.Combine(tempRoot, "libfoo-1.0-1.x86_64.rpm");
        var newRpm = Path.Combine(tempRoot, "libfoo-1.0-2.x86_64.rpm");
        File.WriteAllText(oldRpm, "old-rpm");
        File.WriteAllText(newRpm, "new-rpm");

        var logger = Substitute.For<ILogger<ProcessAbiDiffRunner>>();
        var runner = new ProcessAbiDiffRunner(
            Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
            {
                AbipkgdiffExecutablePath = "nonexistent-abipkgdiff-tool",
                TempExtractionDirectory = tempRoot
            }),
            logger: logger,
            processExecutor: (startInfo, _) =>
            {
                Assert.Equal("/usr/bin/env", startInfo.FileName);
                Assert.Equal("--default-signal=PIPE", startInfo.ArgumentList[0]);
                return Task.FromResult((127, string.Empty, "env: 'nonexistent-abipkgdiff-tool': No such file or directory"));
            },
            supportsEnvDefaultSigpipe: () => true);

        try
        {
            var result = await runner.RunDiffAsync(oldRpm, newRpm);

            Assert.False(result.Succeeded);
            Assert.True(result.ToolMissing);
            Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateDirectories(tempRoot, $"{ProcessAbiDiffRunner.RetainedWorkDirPrefix}*"));

            logger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Is<object>(o => o.ToString()!.Contains("could not be executed", StringComparison.Ordinal)),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}

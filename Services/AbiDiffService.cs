using System.Diagnostics;
using System.Globalization;
using System.Text;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Default runner that executes the system 'abipkgdiff' command-line binary.
/// Gracefully falls back if abipkgdiff is not installed or when running on non-Linux platforms.
/// </summary>
public class ProcessAbiDiffRunner : IAbiDiffRunner
{
    internal const string RetainedWorkDirPrefix = "abaci-abi-";
    internal const string DiagnosticFileName = "diagnostic.err";
    internal const int MaxRetainedFailureSnapshots = 5;
    internal const long MaxRetainedFailureBytes = 512L * 1024 * 1024;

    internal delegate Task<(int ExitCode, string Stdout, string Stderr)> ProcessExecutorDelegate(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);

    private static readonly Lazy<bool> CachedEnvDefaultSigpipeSupport = new(ProbeEnvDefaultSigpipeCore);

    private readonly AbiComplianceOptions _options;
    private readonly ILogger<ProcessAbiDiffRunner>? _logger;
    private readonly ProcessExecutorDelegate _processExecutor;
    private readonly Func<bool> _supportsEnvDefaultSigpipe;
    private readonly Action<string> _deleteDirectory;
    private volatile bool _rpmSupportUnavailable;
    private string? _cachedRpmErrorMessage;

    internal bool IsRpmSupportUnavailable => _rpmSupportUnavailable;

    internal void MarkRpmSupportUnavailable(string reason)
    {
        _rpmSupportUnavailable = true;
        _cachedRpmErrorMessage = reason;
    }

    public ProcessAbiDiffRunner(
        IOptions<AbiComplianceOptions> options,
        ILogger<ProcessAbiDiffRunner>? logger = null)
        : this(options, logger, null, null, null)
    {
    }

    internal ProcessAbiDiffRunner(
        IOptions<AbiComplianceOptions> options,
        ILogger<ProcessAbiDiffRunner>? logger,
        ProcessExecutorDelegate? processExecutor,
        Func<bool>? supportsEnvDefaultSigpipe = null,
        Action<string>? deleteDirectory = null)
    {
        _options = options.Value;
        _logger = logger;
        _processExecutor = processExecutor ?? ExecuteProcessAsync;
        _supportsEnvDefaultSigpipe = supportsEnvDefaultSigpipe ?? (() => CachedEnvDefaultSigpipeSupport.Value);
        _deleteDirectory = deleteDirectory ?? (static path => Directory.Delete(path, recursive: true));
    }

    private bool _privateDsoSupported = true;

    /// <summary>
    /// Builds the ProcessStartInfo configured to execute abipkgdiff.
    /// Exposes process construction for testing CLI contracts without starting external processes.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string oldRpmPath,
        string newRpmPath,
        bool privateDso = true,
        bool dsoOnly = true,
        string? oldDebuginfoPath = null,
        string? newDebuginfoPath = null,
        string? workingDirectory = null,
        bool resetSigpipeWithEnv = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = resetSigpipeWithEnv ? "/usr/bin/env" : executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.Environment["TMPDIR"] = workingDirectory;
        }
        if (resetSigpipeWithEnv)
        {
            startInfo.ArgumentList.Add("--default-signal=PIPE");
            startInfo.ArgumentList.Add(executablePath);
        }
        if (privateDso)
        {
            startInfo.ArgumentList.Add("--private-dso");
        }
        if (dsoOnly)
        {
            startInfo.ArgumentList.Add("--dso-only");
        }
        if (!string.IsNullOrWhiteSpace(oldDebuginfoPath))
        {
            startInfo.ArgumentList.Add("--d1");
            startInfo.ArgumentList.Add(oldDebuginfoPath);
        }
        if (!string.IsNullOrWhiteSpace(newDebuginfoPath))
        {
            startInfo.ArgumentList.Add("--d2");
            startInfo.ArgumentList.Add(newDebuginfoPath);
        }
        startInfo.ArgumentList.Add(oldRpmPath);
        startInfo.ArgumentList.Add(newRpmPath);
        return startInfo;
    }

    public Task<AbiDiffResult> RunDiffAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
        => RunDiffAsync(oldRpmPath, newRpmPath, null, null, ct);

    public async Task<AbiDiffResult> RunDiffAsync(
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath,
        CancellationToken ct = default)
    {
        if (_rpmSupportUnavailable)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: _cachedRpmErrorMessage ?? "abipkgdiff binary lacks RPM package support.",
                ErrorMessage: _cachedRpmErrorMessage ?? "abipkgdiff binary lacks RPM package support.",
                ToolMissing: true);
        }

        if (!File.Exists(oldRpmPath) || !File.Exists(newRpmPath))
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"Target RPM file not found: old='{oldRpmPath}', new='{newRpmPath}'");
        }

        if (!string.IsNullOrWhiteSpace(oldDebuginfoPath) && !File.Exists(oldDebuginfoPath))
        {
            _logger?.LogWarning("Specified old debuginfo RPM was not found on disk: {Path}", oldDebuginfoPath);
        }
        if (!string.IsNullOrWhiteSpace(newDebuginfoPath) && !File.Exists(newDebuginfoPath))
        {
            _logger?.LogWarning("Specified new debuginfo RPM was not found on disk: {Path}", newDebuginfoPath);
        }

        var effectiveOldDebug = !string.IsNullOrWhiteSpace(oldDebuginfoPath) && File.Exists(oldDebuginfoPath) ? oldDebuginfoPath : null;
        var effectiveNewDebug = !string.IsNullOrWhiteSpace(newDebuginfoPath) && File.Exists(newDebuginfoPath) ? newDebuginfoPath : null;
        var baseTemp = ResolveBaseTempDirectory();
        var resetSigpipe = _supportsEnvDefaultSigpipe();
        const int maxAttempts = 2;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 300));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        string? currentWorkDir = null;
        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                currentWorkDir = Path.Combine(baseTemp, RetainedWorkDirPrefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(currentWorkDir);

                var usePrivateDso = _privateDsoSupported;
                var startInfo = BuildStartInfo(
                    _options.AbipkgdiffExecutablePath,
                    oldRpmPath,
                    newRpmPath,
                    usePrivateDso,
                    dsoOnly: true,
                    effectiveOldDebug,
                    effectiveNewDebug,
                    currentWorkDir,
                    resetSigpipe);

                var (exitCode, stdout, stderr) = await _processExecutor(startInfo, effectiveCt);

                if (usePrivateDso && exitCode is not (126 or 127) && (exitCode & 2) != 0 &&
                    (stderr.Contains("private-dso", StringComparison.OrdinalIgnoreCase) || stderr.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)))
                {
                    _privateDsoSupported = false;
                    _logger?.LogWarning("abipkgdiff executable does not support --private-dso; retrying without this option");

                    startInfo = BuildStartInfo(
                        _options.AbipkgdiffExecutablePath,
                        oldRpmPath,
                        newRpmPath,
                        privateDso: false,
                        dsoOnly: true,
                        effectiveOldDebug,
                        effectiveNewDebug,
                        currentWorkDir,
                        resetSigpipe);
                    (exitCode, stdout, stderr) = await _processExecutor(startInfo, effectiveCt);
                }

                var result = InterpretExitCode(exitCode, stdout, stderr);
                if (attempt < maxAttempts && IsTransientWriteError(result, stderr))
                {
                    _logger?.LogWarning(
                        "abipkgdiff reported a transient archive write error on attempt {Attempt}/{MaxAttempts}; retrying in a fresh working directory. WorkDir={WorkDir}, Stderr={Stderr}",
                        attempt,
                        maxAttempts,
                        currentWorkDir,
                        stderr.Trim());
                    TryDeleteWorkDirectory(currentWorkDir);
                    currentWorkDir = null;
                    continue;
                }

                if (result.ToolMissing && result.ErrorMessage?.Contains("rpm", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _rpmSupportUnavailable = true;
                    _cachedRpmErrorMessage = result.ErrorMessage;
                    _logger?.LogError("abipkgdiff on host system lacks RPM support. Short-circuiting future diffs: {Error}", result.ErrorMessage);
                }

                if (result.Succeeded)
                {
                    var hasPairedDebuginfo = effectiveOldDebug != null && effectiveNewDebug != null;
                    var isAsymmetricalDebuginfo = (effectiveOldDebug != null) ^ (effectiveNewDebug != null);

                    if (!hasPairedDebuginfo || isAsymmetricalDebuginfo)
                    {
                        var details = result.Details;
                        if (isAsymmetricalDebuginfo)
                        {
                            var note = "[Native Analysis: Debuginfo was provided for only one package side; type-level validation was omitted]";
                            details = string.IsNullOrWhiteSpace(details) ? note : $"{details}\n{note}";
                        }

                        result = result with
                        {
                            IsIncomplete = true,
                            Details = details
                        };
                    }

                    TryDeleteWorkDirectory(currentWorkDir);
                    currentWorkDir = null;
                    return result;
                }

                if (exitCode is 126 or 127)
                {
                    _logger?.LogError(
                        "Libabigail abipkgdiff executable '{Path}' could not be executed (exit code: {ExitCode}): {Error}",
                        _options.AbipkgdiffExecutablePath,
                        exitCode,
                        result.ErrorMessage);
                    TryDeleteWorkDirectory(currentWorkDir);
                    currentWorkDir = null;
                    return result;
                }

                PersistFailureSnapshot(
                    currentWorkDir,
                    baseTemp,
                    startInfo,
                    exitCode,
                    stdout,
                    stderr,
                    oldRpmPath,
                    newRpmPath,
                    effectiveOldDebug,
                    effectiveNewDebug);
                currentWorkDir = null;
                return result;
            }

            // Unreachable under normal loop termination, kept for compiler exhaustiveness.
            return AbiDiffResult.NotPerformed("abipkgdiff did not produce a comparison result.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryDeleteWorkDirectory(currentWorkDir);
            _logger?.LogWarning("abipkgdiff process timed out after {Timeout} seconds.", _options.TimeoutSeconds);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff process timed out after {_options.TimeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteWorkDirectory(currentWorkDir);
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            TryDeleteWorkDirectory(currentWorkDir);
            _logger?.LogError(ex, "Libabigail abipkgdiff executable '{Path}' not found on host system. ABI compliance checks cannot run.", _options.AbipkgdiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff executable not found: {ex.Message}",
                ToolMissing: true);
        }
        catch (FileNotFoundException ex)
        {
            TryDeleteWorkDirectory(currentWorkDir);
            _logger?.LogError(ex, "Libabigail abipkgdiff executable '{Path}' not found on host system. ABI compliance checks cannot run.", _options.AbipkgdiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff executable not found: {ex.Message}",
                ToolMissing: true);
        }
        catch (Exception ex)
        {
            TryDeleteWorkDirectory(currentWorkDir);
            _logger?.LogWarning(ex, "Failed to run abipkgdiff executable '{Path}'. Skipping ABI analysis.", _options.AbipkgdiffExecutablePath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff execution failed: {ex.Message}");
        }
    }

    internal string ResolveBaseTempDirectory()
    {
        return !string.IsNullOrWhiteSpace(_options.TempExtractionDirectory)
            ? _options.TempExtractionDirectory
            : Path.GetTempPath();
    }

    internal static bool IsTransientWriteError(AbiDiffResult result, string? stderr)
    {
        if (result.Succeeded || result.ToolMissing || string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        return stderr.Contains("Error writing archive", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("Write error", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ProbeEnvDefaultSigpipeCore()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/env"))
        {
            return false;
        }

        Process? process = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--default-signal=PIPE");
            startInfo.ArgumentList.Add("true");

            process = new Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup during capability probe
            }

            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill errors during cancellation
            }

            throw;
        }
    }

    private void PersistFailureSnapshot(
        string workDir,
        string baseTemp,
        ProcessStartInfo startInfo,
        int exitCode,
        string stdout,
        string stderr,
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath)
    {
        var diagnosticWritten = false;
        try
        {
            var commandLine = FormatCommandLine(startInfo);
            var tmpDir = startInfo.Environment.TryGetValue("TMPDIR", out var configuredTmpDir)
                ? configuredTmpDir
                : Environment.GetEnvironmentVariable("TMPDIR");
            var homeDir = Environment.GetEnvironmentVariable("HOME");
            var diagnostic = BuildFailureDiagnostic(
                commandLine,
                workDir,
                tmpDir,
                homeDir,
                exitCode,
                stdout,
                stderr,
                oldRpmPath,
                newRpmPath,
                oldDebuginfoPath,
                newDebuginfoPath);

            _logger?.LogWarning("abipkgdiff comparison failed; retaining diagnostic snapshot in '{WorkDir}'.\n{Diagnostic}", workDir, diagnostic);

            Directory.CreateDirectory(workDir);
            File.WriteAllText(Path.Combine(workDir, DiagnosticFileName), diagnostic, Encoding.UTF8);
            diagnosticWritten = true;

            TryCopyRpmForSnapshot(workDir, "old-", oldRpmPath, _logger);
            TryCopyRpmForSnapshot(workDir, "new-", newRpmPath, _logger);
            TryCopyRpmForSnapshot(workDir, "old-debug-", oldDebuginfoPath, _logger);
            TryCopyRpmForSnapshot(workDir, "new-debug-", newDebuginfoPath, _logger);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist full ABI diagnostic snapshot inside '{WorkDir}'.", workDir);
            if (!diagnosticWritten)
            {
                // Avoid leaving un-prunable orphan directories when diagnostic.err could not be created.
                TryDeleteWorkDirectory(workDir);
            }
        }

        PruneRetainedFailureDirectories(baseTemp, MaxRetainedFailureSnapshots, MaxRetainedFailureBytes, _logger);
    }

    internal static void PruneRetainedFailureDirectories(
        string baseTemp,
        int maxRetained,
        long maxTotalBytes = MaxRetainedFailureBytes,
        ILogger? logger = null)
    {
        if (maxRetained < 0 || maxTotalBytes < 0 || !Directory.Exists(baseTemp))
        {
            return;
        }

        try
        {
            var orderedSnapshots = Directory.EnumerateDirectories(baseTemp, $"{RetainedWorkDirPrefix}*")
                .Where(dir => File.Exists(Path.Combine(dir, DiagnosticFileName)))
                .Select(dir => new DirectoryInfo(dir))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .ToList();

            var keptCount = 0;
            long keptBytes = 0;
            foreach (var snapshot in orderedSnapshots)
            {
                var snapshotBytes = GetDirectorySizeBytes(snapshot.FullName);
                var withinCount = keptCount < maxRetained;
                var withinSize = keptCount == 0 || keptBytes + snapshotBytes <= maxTotalBytes;
                if (withinCount && withinSize)
                {
                    keptCount++;
                    keptBytes += snapshotBytes;
                    continue;
                }

                TryDeleteDirectory(snapshot.FullName, logger);
            }
        }
        catch
        {
            // Retention pruning must never mask the primary ABI diff result
        }
    }

    private static long GetDirectorySizeBytes(string directoryPath)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Ignore files disappearing concurrently during size calculation
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildFailureDiagnostic(
        string commandLine,
        string workDir,
        string? tmpDir,
        string? homeDir,
        int exitCode,
        string stdout,
        string stderr,
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Command: {commandLine}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"WorkingDirectory: {workDir}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"TMPDIR: {tmpDir ?? "<unset>"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"HOME: {homeDir ?? "<unset>"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ExitCode: {exitCode}");
        sb.AppendLine("Inputs:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  old: {DescribeFile(oldRpmPath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  new: {DescribeFile(newRpmPath)}");
        if (!string.IsNullOrWhiteSpace(oldDebuginfoPath))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  oldDebuginfo: {DescribeFile(oldDebuginfoPath)}");
        }
        if (!string.IsNullOrWhiteSpace(newDebuginfoPath))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  newDebuginfo: {DescribeFile(newDebuginfoPath)}");
        }
        sb.AppendLine("--- stdout ---");
        sb.AppendLine(string.IsNullOrEmpty(stdout) ? "<empty>" : stdout.TrimEnd());
        sb.AppendLine("--- stderr ---");
        sb.AppendLine(string.IsNullOrEmpty(stderr) ? "<empty>" : stderr.TrimEnd());
        return sb.ToString();
    }

    private static string FormatCommandLine(ProcessStartInfo startInfo)
    {
        var parts = new List<string>(1 + startInfo.ArgumentList.Count) { QuoteCommandArgument(startInfo.FileName) };
        foreach (var arg in startInfo.ArgumentList)
        {
            parts.Add(QuoteCommandArgument(arg));
        }

        return string.Join(' ', parts);
    }

    private static string QuoteCommandArgument(string value)
    {
        return string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace)
            ? $"\"{value}\""
            : value;
    }

    private static string DescribeFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return $"{path} (missing)";
            }

            var info = new FileInfo(path);
            var permissions = !OperatingSystem.IsWindows()
                ? File.GetUnixFileMode(path).ToString()
                : info.Attributes.ToString();
            return string.Create(CultureInfo.InvariantCulture, $"{path} (size={info.Length} bytes, mode={permissions})");
        }
        catch (Exception ex)
        {
            return $"{path} (metadata unavailable: {ex.Message})";
        }
    }

    private static void TryCopyRpmForSnapshot(
        string workDir,
        string prefix,
        string? sourcePath,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        var fileName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(workDir, prefix + fileName);
        try
        {
            File.Copy(sourcePath, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to copy RPM '{SourcePath}' into diagnostic snapshot '{WorkDir}'.", sourcePath, workDir);
            try
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            catch
            {
                // Best-effort cleanup of partial RPM copy
            }
        }
    }

    private void TryDeleteWorkDirectory(string? path)
        => TryDeleteDirectory(path, _logger, _deleteDirectory);

    private static void TryDeleteDirectory(
        string? path,
        ILogger? logger = null,
        Action<string>? deleteAction = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                var remove = deleteAction ?? (static dir => Directory.Delete(dir, recursive: true));
                remove(path);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete temporary ABI working directory '{WorkDir}'.", path);
        }
    }

    /// <summary>
    /// Interprets the bitmask return code from Libabigail abipkgdiff / abidiff.
    /// Prioritizes stderr capability checks (e.g. missing RPM support) before bitmask evaluation
    /// to accommodate differences across Linux distributions (such as exit code 1 on Ubuntu).
    /// </summary>
    public static AbiDiffResult InterpretExitCode(int exitCode, string stdout, string stderr = "")
    {
        // Libabigail abipkgdiff return code bitmask:
        // 0 = no change
        // 1 = error (ABIDIFF_ERROR / ABIPKGDIFF_ERROR)
        // 2 = usage error (ABIDIFF_USAGE_ERROR)
        // 4 = ABI change detected (ABIDIFF_ABI_CHANGE)
        // 8 = ABI incompatible / breaking change (ABIDIFF_ABI_INCOMPATIBLE_CHANGE)
        const int abidiffError = 1;
        const int abidiffUsage = 2;
        const int abidiffChange = 4;
        const int abidiffIncompatible = 8;

        // POSIX shells and /usr/bin/env exit with 127 (not found) or 126 (cannot invoke);
        // Libabigail's bitmask only uses bits 0..15, so these codes unambiguously indicate a missing/unusable tool.
        if (exitCode == 127)
        {
            var trimmedStderr = stderr.Trim();
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: trimmedStderr,
                ErrorMessage: string.IsNullOrEmpty(trimmedStderr)
                    ? "abipkgdiff executable not found (exit code: 127)"
                    : $"abipkgdiff executable not found: {trimmedStderr}",
                ToolMissing: true);
        }

        if (exitCode == 126)
        {
            var trimmedStderr = stderr.Trim();
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: trimmedStderr,
                ErrorMessage: string.IsNullOrEmpty(trimmedStderr)
                    ? "abipkgdiff executable cannot be invoked (exit code: 126)"
                    : $"abipkgdiff executable cannot be invoked: {trimmedStderr}",
                ToolMissing: true);
        }

        // 1. Inspect stderr for missing tools / extraction failure / crashes first.
        // Ubuntu 22.04 exits with code 1 when libabigail lacks RPM support.
        // Ubuntu 24.04 exits with code 0 when rpm2cpio is missing, writing:
        // "rpm2cpio: command not found", "cpio: premature end of archive", "abipkgdiff: Error while extracting package".
        // Fatal aborts (e.g. exit code 134 SIGABRT when $HOME is missing) are also classified as tool/environment unavailable.
        // Identifying these as tool/engine missing allows automatic fallback to the managed ELF runner.
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (stderr.Contains("Support for rpm hasn't been enabled", StringComparison.OrdinalIgnoreCase))
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: "abipkgdiff reported missing RPM support: Support for rpm hasn't been enabled.",
                    ToolMissing: true);
            }

            var isToolOrExtractionMissing =
                stderr.Contains("rpm2cpio: command not found", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Error while extracting package", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("premature end of archive", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Malformed number", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("07070X", StringComparison.OrdinalIgnoreCase);

            if (isToolOrExtractionMissing)
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: $"abipkgdiff reported missing tool or package extraction failure: {stderr.Trim()}",
                    ToolMissing: true);
            }

            // Fatal process crashes or environment errors (e.g. missing home directory causing mkdir failure and abort)
            var isCrashOrEnvironmentFailure =
                exitCode >= 128 ||
                stderr.Contains("Aborted", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("cannot create directory", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Assertion `__abg_cond__'", StringComparison.OrdinalIgnoreCase);

            if (isCrashOrEnvironmentFailure)
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: $"abipkgdiff process crashed or aborted (exit code: {exitCode}): {stderr.Trim()}",
                    ToolMissing: true);
            }

            // Explicit input file error (e.g. source RPM or missing file); must NOT fallback to avoid false-breaking reports
            var isInputError =
                stderr.Contains("should be an RPM file", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("The input file", StringComparison.OrdinalIgnoreCase);

            if (isInputError)
            {
                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: $"abipkgdiff reported input error (exit code: {exitCode}): {stderr.Trim()}",
                    ToolMissing: false);
            }
        }
        else if (exitCode >= 128)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"abipkgdiff process terminated by signal (exit code: {exitCode})",
                ToolMissing: true);
        }

        // 2. Handle execution errors and usage errors
        if ((exitCode & abidiffError) != 0 || (exitCode & abidiffUsage) != 0)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                ErrorMessage: $"abipkgdiff reported tool failure (exit code: {exitCode})");
        }

        // 3. Handle zero exit code with non-empty stderr
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            var hasExplicitError =
                stderr.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("fail", StringComparison.OrdinalIgnoreCase);

            if (hasExplicitError)
            {
                var msg = string.IsNullOrWhiteSpace(stdout)
                    ? $"abipkgdiff reported error on stderr: produced no comparison output ({stderr.Trim()})"
                    : $"abipkgdiff reported error on stderr: {stderr.Trim()}";

                return new AbiDiffResult(
                    Succeeded: false,
                    HasAbiChange: false,
                    IsBreakingChange: false,
                    Details: stderr.Trim(),
                    ErrorMessage: msg);
            }
        }

        var hasChange = (exitCode & (abidiffChange | abidiffIncompatible)) != 0;
        var isBreaking = (exitCode & abidiffIncompatible) != 0;

        // Inspect stdout for SONAME changes, removed binaries, and added binaries.
        // Libabigail abipkgdiff exits with code 0 on removed or added binaries,
        // but removes are breaking changes and additions are ABI changes.
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            if (stdout.Contains("Removed binaries:", StringComparison.OrdinalIgnoreCase))
            {
                isBreaking = true;
                hasChange = true;
            }
            else if (stdout.Contains("Added binaries:", StringComparison.OrdinalIgnoreCase))
            {
                hasChange = true;
            }
        }

        return new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: hasChange,
            IsBreakingChange: isBreaking,
            Details: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }
}

/// <summary>
/// Deep module coordinating binary ABI comparison and reporting.
/// Transparently handles native abipkgdiff execution and falls back to
/// managed ELF/RPM symbol analysis when native tools or RPM support are unavailable.
/// </summary>
public class AbiDiffService : IAbiDiffService
{
    private readonly IAbiDiffRunner _runner;
    private readonly IAbiDiffRunner? _fallbackRunner;
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<AbiDiffService>? _logger;

    public AbiDiffService(
        IAbiDiffRunner runner,
        IOptions<AbiComplianceOptions> options,
        ILogger<AbiDiffService>? logger = null)
        : this(runner, null, options, logger)
    {
    }

    public AbiDiffService(
        IAbiDiffRunner runner,
        IAbiDiffRunner? fallbackRunner,
        IOptions<AbiComplianceOptions> options,
        ILogger<AbiDiffService>? logger = null)
    {
        _runner = runner;
        _fallbackRunner = fallbackRunner;
        _options = options.Value;
        _logger = logger;
    }

    internal IAbiDiffRunner PrimaryRunner => _runner;
    internal IAbiDiffRunner? FallbackRunner => _fallbackRunner;

    public Task<AbiDiffResult> CompareRpmsAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
        => CompareRpmsAsync(oldRpmPath, newRpmPath, null, null, ct);

    public async Task<AbiDiffResult> CompareRpmsAsync(
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "ABI compliance scanning is disabled in configuration.");
        }

        // Check if either RPM is v6 (lead major version >= 4) and native engine is not verified for v6 support
        var isV6Package = IsRpmV6(oldRpmPath) || IsRpmV6(newRpmPath);
        if (isV6Package && !AbiEngineProbe.NativeSupportsV6 && _options.EnableFallback && _fallbackRunner != null)
        {
            _logger?.LogInformation("Package is RPM v6 and native runner lacks v6 support. Falling back to managed ELF symbol diff runner directly.");
            return await _fallbackRunner.RunDiffAsync(oldRpmPath, newRpmPath, oldDebuginfoPath, newDebuginfoPath, ct);
        }

        var result = (!string.IsNullOrWhiteSpace(oldDebuginfoPath) || !string.IsNullOrWhiteSpace(newDebuginfoPath))
            ? await _runner.RunDiffAsync(oldRpmPath, newRpmPath, oldDebuginfoPath, newDebuginfoPath, ct)
            : await _runner.RunDiffAsync(oldRpmPath, newRpmPath, ct);

        // Fall back to managed ELF runner if primary runner lacks tool or RPM capability
        if (_options.EnableFallback && _fallbackRunner != null && (!result.Succeeded && result.ToolMissing))
        {
            _logger?.LogInformation("Primary ABI runner unavailable ({Reason}). Falling back to managed ELF symbol diff runner.", result.ErrorMessage);
            var fallbackResult = await _fallbackRunner.RunDiffAsync(oldRpmPath, newRpmPath, oldDebuginfoPath, newDebuginfoPath, ct);
            return fallbackResult;
        }

        return result;
    }

    /// <summary>
    /// Inspects the RPM package lead to determine if it is formatted according to RPM v6 specifications (lead major >= 4).
    /// Returns false if the file is missing, inaccessible, or not a verified RPM v6 package, delegating comprehensive
    /// file validation and I/O error logging to the downstream diff pipeline.
    /// </summary>
    internal static bool IsRpmV6(string rpmPath)
    {
        try
        {
            if (!File.Exists(rpmPath)) return false;
            using var fs = new FileStream(rpmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            var header = new byte[5];
            var read = fs.Read(header, 0, 5);
            return read >= 5 && header[0] == 0xED && header[1] == 0xAB && header[2] == 0xEE && header[3] == 0xDB && header[4] >= 4;
        }
        catch
        {
            return false;
        }
    }
}

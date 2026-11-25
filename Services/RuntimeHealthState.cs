using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace abaci_bot.Services;

/// <summary>
/// Immutable per-source health snapshot surfaced under <c>/health?verbose=true</c> (<c>sources</c>).
/// </summary>
public sealed record SourceHealthSnapshot(
    string Status,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    int LastCandidateCount,
    int ConsecutiveFailures,
    string? LastError,
    int LastPublishedCount = 0,
    int LastSkippedCount = 0,
    DateTimeOffset? OldestCandidatePublishedAt = null);

/// <summary>
/// Tracks process startup time, per-source polling health, and the most recent sanitized
/// error/critical log entry for <c>/health?verbose=true</c> diagnostics without leaking credentials.
/// </summary>
public sealed partial class RuntimeHealthState
{
    private readonly TimeProvider _timeProvider;
    private readonly object _errorLock = new();
    private readonly Dictionary<string, SourceHealthSnapshot> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;

    [GeneratedRegex(
        @"(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|Bearer\s+[A-Za-z0-9\-._~+/]+=*|Authorization:\s*(?:(?:Bearer|Basic|Token)\s+)?\S+|-----BEGIN [A-Z ]*PRIVATE KEY-----(?:[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretTokenRegex();

    [GeneratedRegex(
        @"file://(?:/?[A-Za-z]:)?(?:[\\/](?:[^\s\\/:""'<>|,;)\]]+(?: [^\s\\/:""'<>|,;)\]]+){0,3})(?=[\\/]))*[\\/](?:[^\s\\/:""'<>|,;)\].]+(?: [^\s\\/:""'<>|,;)\].]+){1,4}(?:\.[A-Za-z0-9]{1,10}){1,2}(?=$|[\s""'<>|,;)\]:])|[^\s\\""'<>|,;)\]]*)|file://[^\s""'<>|,;)\]]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex FileUriPathRegex();

    [GeneratedRegex(
        @"\\\\[A-Za-z0-9._-]+\\(?:[A-Za-z0-9.$_-]+(?: [A-Za-z0-9.$_-]+){0,3}(?=[\\/])|[A-Za-z0-9.$_-]+)(?:(?:[\\/](?:[^\s\\/:""'<>|,;)\]]+(?: [^\s\\/:""'<>|,;)\]]+){0,3})(?=[\\/]))*[\\/](?:[^\s\\/:""'<>|,;)\].]+(?: [^\s\\/:""'<>|,;)\].]+){1,4}(?:\.[A-Za-z0-9]{1,10}){1,2}(?=$|[\s""'<>|,;)\]:])|[^\s\\""'<>|,;)\]]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])[A-Za-z]:(?=[\\/])(?!//)(?:[\\/](?:[^\s\\/:""'<>|,;)\]]+(?: [^\s\\/:""'<>|,;)\]]+){0,3})(?=[\\/]))*[\\/](?:[^\s\\/:""'<>|,;)\].]+(?: [^\s\\/:""'<>|,;)\].]+){1,4}(?:\.[A-Za-z0-9]{1,10}){1,2}(?=$|[\s""'<>|,;)\]:])|[^\s\\""'<>|,;)\]]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDrivePathRegex();

    [GeneratedRegex(
        @"(?<=^|[\s""'(\[=])/(?!api(?:/|$|\s|\?)|health(?:/|$|\s|\?))(?:[A-Za-z0-9._+-]+/|[A-Za-z0-9_+-]+(?: [A-Za-z0-9_+-]+){1,3}/)+(?:[A-Za-z0-9_+-]+(?: [A-Za-z0-9_+-]+){1,4}(?:\.[A-Za-z0-9]{1,10}){1,2}(?=$|[\s""'<>|,;)\]:])|[A-Za-z0-9._+-]*/?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PosixPathRegex();

    public DateTimeOffset StartedAtUtc { get; }

    public RuntimeHealthState(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        StartedAtUtc = _timeProvider.GetUtcNow();
    }

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    public long GetUptimeSeconds() =>
        (long)Math.Max(0, (_timeProvider.GetUtcNow() - StartedAtUtc).TotalSeconds);

    public (string? LastError, DateTimeOffset? LastErrorAt) GetLastError()
    {
        lock (_errorLock)
        {
            return (_lastError, _lastErrorAt);
        }
    }

    public void SetCapabilityStatus(string capabilityName, string status)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return;
        }

        lock (_errorLock)
        {
            _capabilities[capabilityName.Trim()] = string.IsNullOrWhiteSpace(status)
                ? "disabled"
                : status.Trim();
        }
    }

    public string GetCapabilityStatus(string capabilityName, string defaultStatus = "disabled")
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return defaultStatus;
        }

        lock (_errorLock)
        {
            return _capabilities.TryGetValue(capabilityName.Trim(), out var status)
                ? status
                : defaultStatus;
        }
    }

    public void RecordError(string? source, Exception? exception, string? message = null)
    {
        var shortSource = ShortenSource(source);
        var detail = exception != null
            ? $"{exception.GetType().Name}: {exception.Message}"
            : (message ?? "Unknown error");

        if (exception != null && !string.IsNullOrWhiteSpace(message) &&
            !message.Contains(exception.Message, StringComparison.Ordinal))
        {
            detail = $"{message} ({detail})";
        }

        var combined = string.IsNullOrWhiteSpace(shortSource)
            ? detail
            : $"{shortSource}: {detail}";

        var sanitized = SanitizeMessage(combined);

        lock (_errorLock)
        {
            _lastError = sanitized;
            _lastErrorAt = _timeProvider.GetUtcNow();
        }
    }

    public void RecordSourceOutcome(string sourceName, string status, int candidateCount, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return;
        }

        var normalizedName = sourceName.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? "healthy"
            : status.Trim().ToLowerInvariant();
        var now = _timeProvider.GetUtcNow();
        var sanitizedError = string.IsNullOrWhiteSpace(error) ? null : SanitizeMessage(error);

        lock (_errorLock)
        {
            _sources.TryGetValue(normalizedName, out var previous);
            SourceHealthSnapshot updated = normalizedStatus switch
            {
                "healthy" => new SourceHealthSnapshot(
                    Status: "healthy",
                    LastAttemptAt: now,
                    LastSuccessAt: now,
                    LastCandidateCount: candidateCount,
                    ConsecutiveFailures: 0,
                    LastError: null,
                    LastPublishedCount: previous?.LastPublishedCount ?? 0,
                    LastSkippedCount: previous?.LastSkippedCount ?? 0,
                    OldestCandidatePublishedAt: previous?.OldestCandidatePublishedAt),
                "degraded" => new SourceHealthSnapshot(
                    Status: "degraded",
                    LastAttemptAt: now,
                    LastSuccessAt: previous?.LastSuccessAt,
                    LastCandidateCount: candidateCount,
                    ConsecutiveFailures: (previous?.ConsecutiveFailures ?? 0) + 1,
                    LastError: sanitizedError ?? previous?.LastError,
                    LastPublishedCount: previous?.LastPublishedCount ?? 0,
                    LastSkippedCount: previous?.LastSkippedCount ?? 0,
                    OldestCandidatePublishedAt: previous?.OldestCandidatePublishedAt),
                "failed" => new SourceHealthSnapshot(
                    Status: "failed",
                    LastAttemptAt: now,
                    LastSuccessAt: previous?.LastSuccessAt,
                    LastCandidateCount: candidateCount,
                    ConsecutiveFailures: (previous?.ConsecutiveFailures ?? 0) + 1,
                    LastError: sanitizedError ?? previous?.LastError,
                    LastPublishedCount: previous?.LastPublishedCount ?? 0,
                    LastSkippedCount: previous?.LastSkippedCount ?? 0,
                    OldestCandidatePublishedAt: previous?.OldestCandidatePublishedAt),
                _ => new SourceHealthSnapshot(
                    Status: normalizedStatus,
                    LastAttemptAt: now,
                    LastSuccessAt: previous?.LastSuccessAt,
                    LastCandidateCount: 0,
                    ConsecutiveFailures: 0,
                    LastError: null,
                    LastPublishedCount: 0,
                    LastSkippedCount: 0,
                    OldestCandidatePublishedAt: null)
            };

            _sources[normalizedName] = updated;
        }
    }

    public void RecordSourceSweepStats(
        string sourceName,
        int publishedCount,
        int skippedCount,
        DateTimeOffset? oldestCandidatePublishedAt)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return;
        }

        var normalizedName = sourceName.Trim();
        var now = _timeProvider.GetUtcNow();

        lock (_errorLock)
        {
            if (_sources.TryGetValue(normalizedName, out var existing))
            {
                _sources[normalizedName] = existing with
                {
                    LastPublishedCount = publishedCount,
                    LastSkippedCount = skippedCount,
                    OldestCandidatePublishedAt = oldestCandidatePublishedAt
                };
            }
            else
            {
                _sources[normalizedName] = new SourceHealthSnapshot(
                    Status: "healthy",
                    LastAttemptAt: now,
                    LastSuccessAt: now,
                    LastCandidateCount: 0,
                    ConsecutiveFailures: 0,
                    LastError: null,
                    LastPublishedCount: publishedCount,
                    LastSkippedCount: skippedCount,
                    OldestCandidatePublishedAt: oldestCandidatePublishedAt);
            }
        }
    }

    public IReadOnlyDictionary<string, SourceHealthSnapshot> GetSourceHealth()
    {
        lock (_errorLock)
        {
            return new Dictionary<string, SourceHealthSnapshot>(_sources, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string SanitizeMessage(string message)
    {
        var sanitized = SecretTokenRegex().Replace(message, "[REDACTED]");
        sanitized = FileUriPathRegex().Replace(sanitized, "[REDACTED_PATH]");
        sanitized = UncPathRegex().Replace(sanitized, "[REDACTED_PATH]");
        sanitized = WindowsDrivePathRegex().Replace(sanitized, "[REDACTED_PATH]");
        sanitized = PosixPathRegex().Replace(sanitized, "[REDACTED_PATH]");
        return sanitized.Length > 240 ? sanitized[..240] : sanitized;
    }

    private static string? ShortenSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var trimmed = source.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < trimmed.Length
            ? trimmed[(lastDot + 1)..]
            : trimmed;
    }
}

/// <summary>
/// Captures <see cref="LogLevel.Error"/> and <see cref="LogLevel.Critical"/> log events
/// across the host into <see cref="RuntimeHealthState"/> so <c>/health?verbose=true</c>
/// surfaces the latest runtime failure automatically.
/// </summary>
public sealed class RuntimeErrorLoggerProvider : ILoggerProvider
{
    private readonly RuntimeHealthState _state;

    public RuntimeErrorLoggerProvider(RuntimeHealthState state)
    {
        _state = state;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RuntimeErrorLogger(_state, categoryName);

    public void Dispose()
    {
    }

    private sealed class RuntimeErrorLogger : ILogger
    {
        private readonly RuntimeHealthState _state;
        private readonly string _categoryName;

        public RuntimeErrorLogger(RuntimeHealthState state, string categoryName)
        {
            _state = state;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error)
            {
                return;
            }

            try
            {
                var message = formatter(state, exception);
                _state.RecordError(_categoryName, exception, message);
            }
            catch
            {
                // Logging instrumentation must never throw back into callers.
            }
        }
    }
}

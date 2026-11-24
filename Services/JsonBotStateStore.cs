using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Atomic JSON-file-backed implementation of <see cref="IBotStateStore"/>.
/// Persists state to <c>bot-state.json</c> under <see cref="BotStateOptions.StateDirectory"/>
/// and holds an OS-level exclusive lock on <c>bot-state.lock</c> so at most one instance
/// writes to a given directory at a time.
/// </summary>
public sealed class JsonBotStateStore : IBotStateStore, IDisposable
{
    private readonly object _lock = new();
    private readonly string _stateFilePath;
    private readonly string _lockFilePath;
    private readonly TimeSpan _localeRetentionMaxAge;
    private readonly ILogger<JsonBotStateStore>? _logger;
    private readonly Func<string, string> _fileReader;
    private readonly Action<string, string> _fileMover;
    private FileStream? _directoryLockStream;
    private PersistedBotState _state = new();

    public string StateDirectory { get; }

    public JsonBotStateStore(
        IOptions<BotStateOptions> options,
        ILogger<JsonBotStateStore>? logger = null)
        : this(options, logger, fileReader: null, fileMover: null)
    {
    }

    internal JsonBotStateStore(
        IOptions<BotStateOptions> options,
        ILogger<JsonBotStateStore>? logger,
        Func<string, string>? fileReader,
        Action<string, string>? fileMover)
    {
        _logger = logger;
        _fileReader = fileReader ?? File.ReadAllText;
        _fileMover = fileMover ?? File.Move;
        var retentionDays = options.Value.LocaleRetentionDays > 0 ? options.Value.LocaleRetentionDays : 180;
        _localeRetentionMaxAge = TimeSpan.FromDays(retentionDays);
        var (resolvedDir, isEphemeral) = BotStatePathResolver.ResolveStateDirectoryWithStatus(options.Value.StateDirectory);
        StateDirectory = resolvedDir;
        if (isEphemeral)
        {
            _logger?.LogWarning(
                "BotState:StateDirectory is not configured and no persistent home/XDG data directory is writable; falling back to ephemeral temp directory '{StateDirectory}'. Persisted ignores, cooldowns, and language preferences will not survive restarts.",
                StateDirectory);
        }

        _stateFilePath = Path.Combine(StateDirectory, "bot-state.json");
        _lockFilePath = Path.Combine(StateDirectory, "bot-state.lock");
        EnsureStateDirectoryWritable();
        AcquireExclusiveDirectoryLock();
        try
        {
            LoadState();
            PruneExpiredUserLocales(_localeRetentionMaxAge);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AcquireExclusiveDirectoryLock()
    {
        try
        {
            _directoryLockStream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            _logger?.LogError(
                ex,
                "BotState:StateDirectory '{StateDirectory}' is already locked by another running abaci-bot instance ({LockFile})",
                StateDirectory,
                _lockFilePath);
            throw new InvalidOperationException(
                $"BotState:StateDirectory '{StateDirectory}' is already locked by another running abaci-bot instance. Only a single active instance may share a state directory.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "Cannot open exclusive state lock file '{LockFile}'", _lockFilePath);
            throw new InvalidOperationException(
                $"BotState:StateDirectory '{StateDirectory}' lock file '{_lockFilePath}' cannot be opened exclusively.",
                ex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _directoryLockStream?.Dispose();
            _directoryLockStream = null;
        }
    }

    private void EnsureStateDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var probePath = Path.Combine(StateDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger?.LogError(ex, "BotState:StateDirectory '{StateDirectory}' is not writable", StateDirectory);
            throw new InvalidOperationException(
                $"BotState:StateDirectory '{StateDirectory}' is not writable. Ensure the directory exists and is writable by the bot user (uid 1654 in Docker).",
                ex);
        }
    }

    public string? GetUserLocale(string userLogin)
    {
        if (string.IsNullOrWhiteSpace(userLogin))
        {
            return null;
        }

        lock (_lock)
        {
            return _state.UserLocales.TryGetValue(userLogin.Trim(), out var entry)
                ? entry.Locale
                : null;
        }
    }

    public void SetUserLocale(string userLogin, string locale)
    {
        if (string.IsNullOrWhiteSpace(userLogin) || string.IsNullOrWhiteSpace(locale))
        {
            return;
        }

        var normalizedUser = userLogin.Trim();
        var normalizedLocale = locale.Trim();
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow - _localeRetentionMaxAge;
            var expired = _state.UserLocales
                .Where(kv => kv.Value.UpdatedAt < cutoff)
                .ToList();
            foreach (var kv in expired)
            {
                _state.UserLocales.Remove(kv.Key);
            }

            var hadPrevious = _state.UserLocales.TryGetValue(normalizedUser, out var previousEntry);
            _state.UserLocales[normalizedUser] = new UserLocaleEntry(normalizedLocale, DateTimeOffset.UtcNow);
            try
            {
                SaveState();
            }
            catch
            {
                if (hadPrevious && previousEntry != null)
                {
                    _state.UserLocales[normalizedUser] = previousEntry;
                }
                else
                {
                    _state.UserLocales.Remove(normalizedUser);
                }

                foreach (var kv in expired)
                {
                    _state.UserLocales.TryAdd(kv.Key, kv.Value);
                }
                throw;
            }
        }
    }

    public bool ClearUserLocale(string userLogin)
    {
        if (string.IsNullOrWhiteSpace(userLogin))
        {
            return false;
        }

        var normalizedUser = userLogin.Trim();
        lock (_lock)
        {
            if (!_state.UserLocales.TryGetValue(normalizedUser, out var previousEntry))
            {
                return false;
            }

            _state.UserLocales.Remove(normalizedUser);
            try
            {
                SaveState();
                return true;
            }
            catch
            {
                _state.UserLocales[normalizedUser] = previousEntry;
                throw;
            }
        }
    }

    public int PruneExpiredUserLocales(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
            var expired = _state.UserLocales
                .Where(kv => kv.Value.UpdatedAt < cutoff)
                .ToList();

            if (expired.Count == 0)
            {
                return 0;
            }

            foreach (var kv in expired)
            {
                _state.UserLocales.Remove(kv.Key);
            }

            try
            {
                SaveState();
                return expired.Count;
            }
            catch
            {
                foreach (var kv in expired)
                {
                    _state.UserLocales[kv.Key] = kv.Value;
                }
                throw;
            }
        }
    }

    public IgnoredPackageEntry? GetIgnoredPackage(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return null;
            }

            return repoState.IgnoredPackages.TryGetValue(packageName.Trim(), out var entry)
                ? entry
                : null;
        }
    }

    public IReadOnlyList<IgnoredPackageEntry> GetIgnoredPackages(string owner, string repo)
    {
        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return Array.Empty<IgnoredPackageEntry>();
            }

            return repoState.IgnoredPackages.Values
                .OrderBy(e => e.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void SetIgnoredPackage(string owner, string repo, string packageName, string? reason, string ignoredBy)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey, out var hadRepo);

            var hadPrevious = repoState.IgnoredPackages.TryGetValue(normalizedPkg, out var previousEntry);
            repoState.IgnoredPackages[normalizedPkg] = new IgnoredPackageEntry(
                normalizedPkg,
                string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ignoredBy,
                DateTimeOffset.UtcNow);
            try
            {
                SaveState();
            }
            catch
            {
                if (hadPrevious && previousEntry != null)
                {
                    repoState.IgnoredPackages[normalizedPkg] = previousEntry;
                }
                else
                {
                    repoState.IgnoredPackages.Remove(normalizedPkg);
                    if (!hadRepo)
                    {
                        _state.Repositories.Remove(repoKey);
                    }
                }
                throw;
            }
        }
    }

    public bool RemoveIgnoredPackage(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return false;
            }

            if (!repoState.IgnoredPackages.TryGetValue(normalizedPkg, out var previousEntry))
            {
                return false;
            }

            repoState.IgnoredPackages.Remove(normalizedPkg);
            try
            {
                SaveState();
                return true;
            }
            catch
            {
                repoState.IgnoredPackages[normalizedPkg] = previousEntry;
                throw;
            }
        }
    }

    public PackageFailureEntry? GetPackageFailure(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return null;
            }

            return repoState.PackageFailures.TryGetValue(packageName.Trim(), out var entry)
                ? entry
                : null;
        }
    }

    public void SetPackageFailure(string owner, string repo, string packageName, int consecutiveFailures, DateTimeOffset? cooldownUntil)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey, out var hadRepo);

            var hadPrevious = repoState.PackageFailures.TryGetValue(normalizedPkg, out var previousEntry);
            repoState.PackageFailures[normalizedPkg] = new PackageFailureEntry(
                consecutiveFailures,
                cooldownUntil,
                DateTimeOffset.UtcNow,
                previousEntry?.ObsBlockedUntil);
            try
            {
                SaveState();
            }
            catch
            {
                RestorePackageFailure(repoState, repoKey, hadRepo, normalizedPkg, hadPrevious, previousEntry);
                throw;
            }
        }
    }

    public bool ClearPackageFailure(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState) ||
                !repoState.PackageFailures.TryGetValue(normalizedPkg, out var previousEntry))
            {
                return false;
            }

            bool cleared;
            if (previousEntry.ObsBlockedUntil.HasValue)
            {
                if (previousEntry.ConsecutiveFailures == 0 && !previousEntry.CooldownUntil.HasValue && !previousEntry.LastFailureAt.HasValue)
                {
                    return false;
                }

                repoState.PackageFailures[normalizedPkg] = new PackageFailureEntry(
                    0,
                    null,
                    null,
                    previousEntry.ObsBlockedUntil);
                cleared = true;
            }
            else
            {
                cleared = repoState.PackageFailures.Remove(normalizedPkg);
            }

            if (cleared)
            {
                try
                {
                    SaveState();
                }
                catch
                {
                    repoState.PackageFailures[normalizedPkg] = previousEntry;
                    throw;
                }
            }

            return cleared;
        }
    }

    public void SetObsBlockCooldown(string owner, string repo, string packageName, DateTimeOffset blockedUntil)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey, out var hadRepo);

            var hadPrevious = repoState.PackageFailures.TryGetValue(normalizedPkg, out var previousEntry);
            repoState.PackageFailures[normalizedPkg] = previousEntry != null
                ? previousEntry with { ObsBlockedUntil = blockedUntil }
                : new PackageFailureEntry(0, null, null, blockedUntil);
            try
            {
                SaveState();
            }
            catch
            {
                RestorePackageFailure(repoState, repoKey, hadRepo, normalizedPkg, hadPrevious, previousEntry);
                throw;
            }
        }
    }

    public bool ClearObsBlockCooldown(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState) ||
                !repoState.PackageFailures.TryGetValue(normalizedPkg, out var previousEntry) ||
                !previousEntry.ObsBlockedUntil.HasValue)
            {
                return false;
            }

            if (previousEntry.ConsecutiveFailures > 0 || previousEntry.CooldownUntil.HasValue || previousEntry.LastFailureAt.HasValue)
            {
                repoState.PackageFailures[normalizedPkg] = previousEntry with { ObsBlockedUntil = null };
            }
            else
            {
                repoState.PackageFailures.Remove(normalizedPkg);
            }

            try
            {
                SaveState();
                return true;
            }
            catch
            {
                repoState.PackageFailures[normalizedPkg] = previousEntry;
                throw;
            }
        }
    }

    private void RestorePackageFailure(
        RepoState repoState,
        string repoKey,
        bool hadRepo,
        string normalizedPkg,
        bool hadPrevious,
        PackageFailureEntry? previousEntry)
    {
        if (hadPrevious && previousEntry != null)
        {
            repoState.PackageFailures[normalizedPkg] = previousEntry;
            return;
        }

        repoState.PackageFailures.Remove(normalizedPkg);
        if (!hadRepo)
        {
            _state.Repositories.Remove(repoKey);
        }
    }

    public bool TryMarkFailedPrRecorded(string owner, string repo, int prNumber)
    {
        if (prNumber <= 0)
        {
            return false;
        }

        const int maxTrackedPrsPerRepo = 2048;
        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey, out var hadRepo);

            if (!repoState.RecordedFailedPrs.Add(prNumber))
            {
                return false;
            }

            int? evictedOldest = null;
            if (repoState.RecordedFailedPrs.Count > maxTrackedPrsPerRepo)
            {
                var oldest = repoState.RecordedFailedPrs.Min();
                repoState.RecordedFailedPrs.Remove(oldest);
                evictedOldest = oldest;
            }

            try
            {
                SaveState();
                return true;
            }
            catch
            {
                repoState.RecordedFailedPrs.Remove(prNumber);
                if (evictedOldest.HasValue)
                {
                    repoState.RecordedFailedPrs.Add(evictedOldest.Value);
                }

                if (!hadRepo)
                {
                    _state.Repositories.Remove(repoKey);
                }
                throw;
            }
        }
    }

    public void AddPendingEphemeralComment(PendingEphemeralComment entry)
    {
        if (entry.CommentId <= 0 || string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.Repo))
        {
            return;
        }

        lock (_lock)
        {
            var previousEntries = _state.PendingEphemeralComments.Where(e =>
                e.CommentId == entry.CommentId &&
                string.Equals(e.Owner, entry.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase)).ToList();

            _state.PendingEphemeralComments.RemoveAll(e =>
                e.CommentId == entry.CommentId &&
                string.Equals(e.Owner, entry.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase));
            _state.PendingEphemeralComments.Add(entry);
            try
            {
                SaveState();
            }
            catch
            {
                _state.PendingEphemeralComments.RemoveAt(_state.PendingEphemeralComments.Count - 1);
                if (previousEntries.Count > 0)
                {
                    _state.PendingEphemeralComments.AddRange(previousEntries);
                }
                throw;
            }
        }
    }

    public IReadOnlyList<PendingEphemeralComment> GetDueEphemeralComments(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _state.PendingEphemeralComments
                .Where(e => e.DeleteAt <= now)
                .ToList();
        }
    }

    public void RemovePendingEphemeralComment(string owner, string repo, long commentId)
    {
        lock (_lock)
        {
            var removedEntries = _state.PendingEphemeralComments.Where(e =>
                e.CommentId == commentId &&
                string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase)).ToList();
            if (removedEntries.Count == 0)
            {
                return;
            }

            _state.PendingEphemeralComments.RemoveAll(e =>
                e.CommentId == commentId &&
                string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase));
            try
            {
                SaveState();
            }
            catch
            {
                _state.PendingEphemeralComments.AddRange(removedEntries);
                throw;
            }
        }
    }

    public bool RecordEphemeralRetry(string owner, string repo, long commentId, DateTimeOffset nextAttemptAt, int maxRetries = 5)
    {
        lock (_lock)
        {
            var idx = _state.PendingEphemeralComments.FindIndex(e =>
                e.CommentId == commentId &&
                string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return false;
            }

            var current = _state.PendingEphemeralComments[idx];
            var nextRetry = current.RetryCount + 1;
            if (nextRetry >= maxRetries)
            {
                _state.PendingEphemeralComments.RemoveAt(idx);
                try
                {
                    SaveState();
                    return false;
                }
                catch
                {
                    _state.PendingEphemeralComments.Insert(idx, current);
                    throw;
                }
            }

            _state.PendingEphemeralComments[idx] = current with { DeleteAt = nextAttemptAt, RetryCount = nextRetry };
            try
            {
                SaveState();
                return true;
            }
            catch
            {
                _state.PendingEphemeralComments[idx] = current;
                throw;
            }
        }
    }

    public string? GetCursorWatermark(string cursorKey)
    {
        if (string.IsNullOrWhiteSpace(cursorKey))
        {
            return null;
        }

        lock (_lock)
        {
            return _state.Cursors.TryGetValue(cursorKey.Trim(), out var watermark) &&
                   !string.IsNullOrWhiteSpace(watermark)
                ? watermark
                : null;
        }
    }

    public void SetCursorWatermark(string cursorKey, string? watermark)
    {
        if (string.IsNullOrWhiteSpace(cursorKey))
        {
            return;
        }

        var normalizedKey = cursorKey.Trim();
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(watermark))
            {
                if (!_state.Cursors.TryGetValue(normalizedKey, out var previousWatermark))
                {
                    return;
                }

                _state.Cursors.Remove(normalizedKey);
                try
                {
                    SaveState();
                }
                catch
                {
                    _state.Cursors[normalizedKey] = previousWatermark;
                    throw;
                }
                return;
            }

            var normalizedWatermark = watermark.Trim();
            var hadExisting = _state.Cursors.TryGetValue(normalizedKey, out var existing);
            if (hadExisting && string.Equals(existing, normalizedWatermark, StringComparison.Ordinal))
            {
                return;
            }

            _state.Cursors[normalizedKey] = normalizedWatermark;
            try
            {
                SaveState();
            }
            catch
            {
                if (hadExisting && existing != null)
                {
                    _state.Cursors[normalizedKey] = existing;
                }
                else
                {
                    _state.Cursors.Remove(normalizedKey);
                }
                throw;
            }
        }
    }

    private RepoState GetOrCreateRepoState(string repoKey, out bool existedBefore)
    {
        existedBefore = _state.Repositories.TryGetValue(repoKey, out var repoState) && repoState != null;
        if (!existedBefore)
        {
            repoState = new RepoState();
            _state.Repositories[repoKey] = repoState;
        }

        return repoState!;
    }

    private static string FormatRepoKey(string owner, string repo) => $"{owner}/{repo}";

    private void LoadState()
    {
        string json;
        try
        {
            json = _fileReader(_stateFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(
                ex,
                "Failed to read bot state file {Path} due to filesystem I/O or permission error; refusing to overwrite or start with empty state",
                _stateFilePath);
            throw new InvalidOperationException(
                $"Failed to read bot state file '{_stateFilePath}'. Refusing to back up or start with empty state on I/O error.",
                ex);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<PersistedBotState>(json);
            if (loaded == null)
            {
                _logger?.LogError("Bot state file {Path} deserialized to null; backing up corrupt file and starting with empty state", _stateFilePath);
                BackupCorruptStateFile();
                _state = new PersistedBotState();
                return;
            }

            _state = new PersistedBotState
            {
                UserLocales = CopyNonNullMap(loaded.UserLocales),
                Repositories = new Dictionary<string, RepoState>(StringComparer.OrdinalIgnoreCase),
                PendingEphemeralComments = loaded.PendingEphemeralComments?.Where(c => c is not null).ToList() ?? new List<PendingEphemeralComment>(),
                Cursors = CopyNonNullMap(loaded.Cursors)
            };

            if (loaded.Repositories != null)
            {
                foreach (var (repoKey, repoVal) in loaded.Repositories)
                {
                    if (string.IsNullOrWhiteSpace(repoKey) || repoVal is null)
                    {
                        continue;
                    }

                    _state.Repositories[repoKey] = new RepoState
                    {
                        IgnoredPackages = CopyNonNullMap(repoVal.IgnoredPackages),
                        PackageFailures = CopyNonNullMap(repoVal.PackageFailures),
                        RecordedFailedPrs = repoVal.RecordedFailedPrs != null
                            ? new HashSet<int>(repoVal.RecordedFailedPrs)
                            : new HashSet<int>()
                    };
                }
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to deserialize corrupt bot state JSON from {Path}; backing up corrupt file and starting with empty state", _stateFilePath);
            BackupCorruptStateFile();
            _state = new PersistedBotState();
        }
    }

    private static Dictionary<string, TValue> CopyNonNullMap<TValue>(IDictionary<string, TValue>? source)
        where TValue : class
    {
        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private void BackupCorruptStateFile()
    {
        if (!File.Exists(_stateFilePath))
        {
            return;
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = $"{_stateFilePath}.corrupt.{timestamp}";
            if (File.Exists(backupPath))
            {
                backupPath = $"{_stateFilePath}.corrupt.{timestamp}.{Guid.NewGuid():N}";
            }

            _fileMover(_stateFilePath, backupPath);
        }
        catch (Exception backupEx)
        {
            _logger?.LogError(backupEx, "Failed to preserve corrupt bot state file at {Path}; refusing to start with empty state", _stateFilePath);
            throw new InvalidOperationException(
                $"Failed to back up corrupt bot state file '{_stateFilePath}'. Refusing to start with empty state that would overwrite corrupt evidence.",
                backupEx);
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var json = JsonSerializer.Serialize(_state);
            var tempPath = _stateFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist bot state to {Path}", _stateFilePath);
            throw;
        }
    }

    private sealed class PersistedBotState
    {
        public Dictionary<string, UserLocaleEntry> UserLocales { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RepoState> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PendingEphemeralComment> PendingEphemeralComments { get; set; } = new();
        public Dictionary<string, string> Cursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record UserLocaleEntry(string Locale, DateTimeOffset UpdatedAt);

    private sealed class RepoState
    {
        public Dictionary<string, IgnoredPackageEntry> IgnoredPackages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PackageFailureEntry> PackageFailures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> RecordedFailedPrs { get; set; } = new();
    }
}

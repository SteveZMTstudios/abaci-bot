using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Built-in fallback update candidate source querying the index.crates.io sparse index.
/// Operates independently of external trackers, using 4-tier hashing and NDJSON streaming.
/// Filters yanked and pre-release versions, strictly enforcing branch constraints.
/// </summary>
public class RustCrateVersionSource : IUpdateCandidateSource, ICandidateSweepCursor
{
    public const string CursorStateKey = "rust-crates";

    private readonly HttpClient _httpClient;
    private readonly RustCratePollerOptions _options;
    private readonly ILogger<RustCrateVersionSource>? _logger;
    private readonly ISpecCatalog? _specCatalog;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SpecContext>>>? _specCatalogProvider;
    private readonly RuntimeHealthState? _healthState;
    private readonly IBotStateStore? _stateStore;
    private readonly string _cursorStateKey;
    private string? _lastWatermark;
    private string? _pendingSweepWatermark;
    private bool _hasPendingSweepCommit;
    private IReadOnlyList<string> _allSortedNames = Array.Empty<string>();
    private IReadOnlyList<string> _lastCandidateNames = Array.Empty<string>();

    private static readonly Regex CrateNameRegex = new(
        @"^%global\s+crate_name\s+(?<name>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public string SourceName => "RustCrates";

    public static string GetCursorStateKey(string? targetRepository) =>
        string.IsNullOrWhiteSpace(targetRepository)
            ? CursorStateKey
            : $"{CursorStateKey}:{targetRepository.Trim().ToLowerInvariant()}";

    public RustCrateVersionSource(
        HttpClient httpClient,
        IOptions<PackageUpdateOptions> options,
        ILogger<RustCrateVersionSource>? logger = null,
        ISpecCatalog? specCatalog = null,
        Func<CancellationToken, Task<IReadOnlyList<SpecContext>>>? specCatalogProvider = null,
        RuntimeHealthState? healthState = null,
        IBotStateStore? stateStore = null)
    {
        _httpClient = httpClient;
        _options = options.Value.RustCratePoller;
        _logger = logger;
        _specCatalog = specCatalog;
        _specCatalogProvider = specCatalogProvider;
        _healthState = healthState;
        _stateStore = stateStore;
        _cursorStateKey = GetCursorStateKey(options.Value.TargetRepository);
    }

    /// <summary>
    /// Generates the canonical sparse index path using crates.io 4-tier hashing rules.
    /// 1 char: 1/{name}
    /// 2 chars: 2/{name}
    /// 3 chars: 3/{first_char}/{name}
    /// 4+ chars: {first_2}/{next_2}/{name}
    /// </summary>
    public static string GetSparseIndexPath(string crateName)
    {
        if (string.IsNullOrWhiteSpace(crateName))
            throw new ArgumentException("Crate name cannot be empty", nameof(crateName));

        var name = crateName.Trim().ToLowerInvariant();
        return name.Length switch
        {
            1 => $"1/{name}",
            2 => $"2/{name}",
            3 => $"3/{name[0]}/{name}",
            _ => $"{name[..2]}/{name[2..4]}/{name}"
        };
    }

    public async Task<IReadOnlyList<UpdateCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.IndexBaseUrl))
        {
            _logger?.LogInformation("Rust crates poller is disabled or IndexBaseUrl is empty");
            _healthState?.RecordSourceOutcome(SourceName, "disabled", 0);
            return Array.Empty<UpdateCandidate>();
        }

        var currentWatermark = _lastWatermark ?? _stateStore?.GetCursorWatermark(_cursorStateKey);
        _pendingSweepWatermark = null;
        _hasPendingSweepCommit = false;
        _lastCandidateNames = Array.Empty<string>();
        var specs = await LoadRustSpecsForSweepAsync(currentWatermark, ct);
        if (specs.Count == 0)
        {
            _logger?.LogInformation("No Rust crate SPEC files found to evaluate");
            _healthState?.RecordSourceOutcome(SourceName, "healthy", 0);
            return Array.Empty<UpdateCandidate>();
        }

        var candidates = new List<UpdateCandidate>();
        var baseUrl = _options.IndexBaseUrl.TrimEnd('/');
        int processedCount = 0;
        int queriedCount = 0;
        int failedCount = 0;
        int transientFailedCount = 0;
        string? lastError = null;
        string? candidateWatermark = null;
        bool hitTransientError = false;

        foreach (var spec in specs)
        {
            if (ct.IsCancellationRequested)
                break;

            if (_options.MaxCratesPerSweep > 0 && processedCount >= _options.MaxCratesPerSweep)
            {
                _logger?.LogInformation("Reached MaxCratesPerSweep limit ({Limit}) for this sweep", _options.MaxCratesPerSweep);
                break;
            }

            var crateMatch = CrateNameRegex.Match(spec.Content);
            if (!crateMatch.Success)
            {
                if (!hitTransientError)
                {
                    candidateWatermark = spec.PackageName;
                }
                continue;
            }

            var crateName = crateMatch.Groups["name"].Value.Trim();
            var currentVersion = ExtractCurrentVersion(spec.Content);
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                if (!hitTransientError)
                {
                    candidateWatermark = spec.PackageName;
                }
                continue;
            }

            // Deduce branch via unified UpdateAdmission helper
            var branch = UpdateAdmission.DeduceBranch(spec.DirectoryName, crateName);

            var indexPath = GetSparseIndexPath(crateName);
            var url = $"{baseUrl}/{indexPath}";

            processedCount++;
            if (_options.RequestDelayMilliseconds > 0)
            {
                await Task.Delay(_options.RequestDelayMilliseconds, ct);
            }

            queriedCount++;
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCount++;
                transientFailedCount++;
                hitTransientError = true;
                lastError = $"{crateName}: {ex.GetType().Name}: {ex.Message}";
                _logger?.LogWarning(ex, "Failed to query sparse index for crate {Crate} at {Url}", crateName, url);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                failedCount++;
                lastError = $"{crateName}: HTTP {(int)response.StatusCode} ({response.StatusCode})";
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Advance past permanent 404s so deleted/unlisted crates cannot wedge the catalog cursor.
                    if (!hitTransientError)
                    {
                        candidateWatermark = spec.PackageName;
                    }
                }
                else
                {
                    transientFailedCount++;
                    hitTransientError = true;
                }
                _logger?.LogWarning("Sparse index returned status {StatusCode} for crate {Crate} at {Url}", response.StatusCode, crateName, url);
                continue;
            }

            if (!hitTransientError)
            {
                candidateWatermark = spec.PackageName;
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var versions = new List<CrateIndexEntry>();

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<CrateIndexEntry>(line);
                    if (entry != null && !entry.Yanked && !string.IsNullOrWhiteSpace(entry.Version))
                    {
                        // Exclude pre-releases (hyphens in Version tag are forbidden in RPM)
                        if (!entry.Version.Contains('-'))
                        {
                            versions.Add(entry);
                        }
                    }
                }
                catch
                {
                    // Ignore malformed line
                }
            }

            if (versions.Count == 0)
                continue;

            // Filter compatible with branch
            var compatibleVersions = versions
                .Where(v => RustCrateSpecRewriter.IsVersionCompatibleWithBranch(v.Version, branch))
                .ToList();

            if (compatibleVersions.Count == 0)
                continue;

            // Pick latest based on semver / appearance in index
            var targetEntry = SelectLatestVersion(compatibleVersions, currentVersion);
            if (targetEntry == null)
                continue;

            candidates.Add(new UpdateCandidate(
                PackageName: spec.PackageName,
                CurrentVersion: currentVersion,
                TargetVersion: targetEntry.Version,
                Origin: CandidateOrigin.RustCrate,
                EvidenceUrl: $"https://crates.io/crates/{crateName}/{targetEntry.Version}",
                PublishedAt: targetEntry.Pubtime));
        }

        if (candidateWatermark != null && (queriedCount == 0 || transientFailedCount < queriedCount))
        {
            // Stage watermark in memory only; CompleteSweep persists after scheduler completion (2PC cursor).
            _pendingSweepWatermark = candidateWatermark;
            _hasPendingSweepCommit = true;
        }

        _lastCandidateNames = candidates.Select(c => c.PackageName).ToList();

        if (queriedCount > 0 && failedCount == queriedCount)
        {
            _logger?.LogError(
                "Rust crate version source failed all {Failed}/{Queried} sparse index queries. Last error: {Error}",
                failedCount, queriedCount, lastError);
            _healthState?.RecordSourceOutcome(SourceName, "failed", 0, lastError);
        }
        else if (failedCount > 0)
        {
            _logger?.LogWarning(
                "Rust crate version source degraded ({Failed}/{Queried} sparse index queries failed, {Candidates} candidates discovered). Last error: {Error}",
                failedCount, queriedCount, candidates.Count, lastError);
            _healthState?.RecordSourceOutcome(SourceName, "degraded", candidates.Count, lastError);
        }
        else
        {
            _healthState?.RecordSourceOutcome(SourceName, "healthy", candidates.Count);
        }

        _logger?.LogInformation(
            "Rust crates poller discovered {Count} candidates from {Processed} queried crates (confirmedWatermark={ConfirmedWatermark}, pendingWatermark={PendingWatermark})",
            candidates.Count, processedCount, _lastWatermark ?? "<start>", _pendingSweepWatermark ?? "<unchanged>");
        return candidates;
    }

    public void CompleteSweep(IReadOnlySet<string> processedPackageNames)
    {
        if (_lastCandidateNames.Count > 0 && _allSortedNames.Count > 0)
        {
            var earliestUnprocessedIndex = -1;
            foreach (var candidateName in _lastCandidateNames)
            {
                if (processedPackageNames.Contains(candidateName))
                {
                    continue;
                }

                var index = IndexOfName(_allSortedNames, candidateName);
                if (index >= 0 && (earliestUnprocessedIndex < 0 || index < earliestUnprocessedIndex))
                {
                    earliestUnprocessedIndex = index;
                }
            }

            if (earliestUnprocessedIndex >= 0)
            {
                var previous = earliestUnprocessedIndex > 0
                    ? _allSortedNames[earliestUnprocessedIndex - 1]
                    : null;
                _lastWatermark = previous;
                _stateStore?.SetCursorWatermark(_cursorStateKey, previous);
                _pendingSweepWatermark = null;
                _hasPendingSweepCommit = false;
                _logger?.LogInformation(
                    "Rust crates poller rewound cursor before unprocessed candidate {Package} (watermark={Watermark})",
                    _allSortedNames[earliestUnprocessedIndex], previous ?? "<start>");
                return;
            }
        }

        if (_hasPendingSweepCommit)
        {
            _lastWatermark = _pendingSweepWatermark;
            _stateStore?.SetCursorWatermark(_cursorStateKey, _pendingSweepWatermark);
            _logger?.LogInformation(
                "Rust crates poller committed sweep watermark={Watermark}",
                _pendingSweepWatermark ?? "<start>");
        }

        _pendingSweepWatermark = null;
        _hasPendingSweepCommit = false;
    }

    private static int IndexOfName(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Selects the highest/latest version strictly newer than currentVersion using unified RPM/SemVer comparison.
    /// Rejects pre-releases and unparseable/non-version strings instead of blindly letting later entries overwrite valid ones.
    /// </summary>
    public static CrateIndexEntry? SelectLatestVersion(List<CrateIndexEntry> entries, string currentVersion)
    {
        if (entries == null || entries.Count == 0)
            return null;

        CrateIndexEntry? latest = null;
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Version))
                continue;

            var stripped = UpdateAdmission.StripVersionSuffix(entry.Version);
            if (!UpdateAdmission.IsValidTargetVersion(stripped) || !stripped.Any(char.IsDigit))
                continue;

            if (latest == null || UpdateAdmission.CompareRpmVersions(entry.Version, latest.Version) > 0)
            {
                latest = entry;
            }
        }

        if (latest == null)
            return null;

        if (string.Equals(latest.Version, currentVersion, StringComparison.Ordinal))
            return null;

        if (!string.IsNullOrWhiteSpace(currentVersion) &&
            UpdateAdmission.CompareRpmVersions(latest.Version, currentVersion) <= 0)
        {
            return null;
        }

        return latest;
    }

    private static string ExtractCurrentVersion(string specContent) =>
        UpdateAdmission.TryExtractCurrentVersion(specContent, out var version)
            ? version
            : string.Empty;

    internal static int FindWatermarkStartIndex(IReadOnlyList<string> sortedNames, string? lastWatermark)
    {
        if (sortedNames.Count == 0 || string.IsNullOrWhiteSpace(lastWatermark))
        {
            return 0;
        }

        for (int i = 0; i < sortedNames.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Compare(sortedNames[i], lastWatermark) > 0)
            {
                return i;
            }
        }

        return 0;
    }

    private async Task<IReadOnlyList<SpecContext>> LoadRustSpecsForSweepAsync(string? currentWatermark, CancellationToken ct)
    {
        _allSortedNames = Array.Empty<string>();
        if (_specCatalogProvider != null)
        {
            var providerSpecs = (await _specCatalogProvider(ct))
                .OrderBy(s => s.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (providerSpecs.Count == 0)
            {
                return Array.Empty<SpecContext>();
            }

            _allSortedNames = providerSpecs.Select(s => s.PackageName).ToList();
            var total = providerSpecs.Count;
            var startIndex = FindWatermarkStartIndex(_allSortedNames, currentWatermark);
            var limit = _options.MaxCratesPerSweep > 0 ? Math.Min(_options.MaxCratesPerSweep, total) : total;
            var result = new List<SpecContext>(limit);
            for (int i = 0; i < total && result.Count < limit; i++)
            {
                result.Add(providerSpecs[(startIndex + i) % total]);
            }
            return result;
        }

        if (_specCatalog != null)
        {
            // Slice sorted rust-* package names from durable watermark BEFORE calling GetSpecAsync.
            var allNames = await _specCatalog.GetAllPackageNamesAsync(ct);
            var rustNames = allNames
                .Where(n => n.StartsWith("rust-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (rustNames.Count == 0)
            {
                return Array.Empty<SpecContext>();
            }

            _allSortedNames = rustNames;
            var total = rustNames.Count;
            var startIndex = FindWatermarkStartIndex(rustNames, currentWatermark);
            var limit = _options.MaxCratesPerSweep > 0 ? Math.Min(_options.MaxCratesPerSweep, total) : total;
            var slicedSpecs = new List<SpecContext>(limit);

            for (int i = 0; i < total && slicedSpecs.Count < limit; i++)
            {
                if (ct.IsCancellationRequested)
                    break;

                var name = rustNames[(startIndex + i) % total];
                var spec = await _specCatalog.GetSpecAsync(name, ct);
                if (spec != null)
                {
                    slicedSpecs.Add(spec);
                }
            }

            return slicedSpecs;
        }

        var localSpecs = (await LoadLocalRustSpecsAsync(ct))
            .OrderBy(s => s.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (localSpecs.Count == 0)
        {
            return Array.Empty<SpecContext>();
        }

        var localTotal = localSpecs.Count;
        _allSortedNames = localSpecs.Select(s => s.PackageName).ToList();
        var localStart = FindWatermarkStartIndex(_allSortedNames, currentWatermark);
        var localLimit = _options.MaxCratesPerSweep > 0 ? Math.Min(_options.MaxCratesPerSweep, localTotal) : localTotal;
        var slicedLocal = new List<SpecContext>(localLimit);
        for (int i = 0; i < localTotal && slicedLocal.Count < localLimit; i++)
        {
            slicedLocal.Add(localSpecs[(localStart + i) % localTotal]);
        }
        return slicedLocal;
    }

    private async Task<IReadOnlyList<SpecContext>> LoadLocalRustSpecsAsync(CancellationToken ct)
    {
        // Default: discover from local SPECS/ directory if available
        var list = new List<SpecContext>();
        const string specsDir = "SPECS";
        if (!Directory.Exists(specsDir))
            return list;

        foreach (var dir in Directory.GetDirectories(specsDir, "rust-*"))
        {
            if (ct.IsCancellationRequested)
                break;

            var dirName = Path.GetFileName(dir);
            var specFiles = Directory.GetFiles(dir, "*.spec");
            if (specFiles.Length == 0)
                continue;

            var specFile = specFiles[0];
            try
            {
                var content = await File.ReadAllTextAsync(specFile, ct);
                var relPath = specFile.Replace('\\', '/');
                list.Add(new SpecContext(dirName, dirName, content, relPath));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read SPEC file {File}", specFile);
            }
        }

        return list;
    }

    public class CrateIndexEntry
    {
        [JsonPropertyName("vers")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("yanked")]
        public bool Yanked { get; set; }

        [JsonPropertyName("pubtime")]
        public DateTimeOffset? Pubtime { get; set; }
    }
}


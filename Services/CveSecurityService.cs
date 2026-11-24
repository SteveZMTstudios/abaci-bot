using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module implementing dual-source CVE querying (OSV.dev + NVD fallback)
/// with in-memory caching and graceful network degradation.
/// </summary>
public class CveSecurityService : ICveSecurityService, ICveNvdAttemptGateway
{
    internal const int MaxCacheEntries = 8192;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CveSecurityOptions _options;
    private readonly ILogger<CveSecurityService>? _logger;
    private readonly int _maxCacheEntries;
    private readonly object _cacheWriteLock = new();

    private readonly ConcurrentDictionary<string, (CveReport Report, DateTime Expiry)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, (CveReport Report, DateTime Expiry)> Cache => _cache;

    private static readonly string[] ParagraphSeparators = ["\r\n\r\n", "\n\n"];

    public CveSecurityService(
        IHttpClientFactory httpClientFactory,
        IOptions<CveSecurityOptions> options,
        ILogger<CveSecurityService>? logger = null)
        : this(httpClientFactory, options, logger, MaxCacheEntries)
    {
    }

    internal CveSecurityService(
        IHttpClientFactory httpClientFactory,
        IOptions<CveSecurityOptions> options,
        ILogger<CveSecurityService>? logger,
        int maxCacheEntries)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _maxCacheEntries = Math.Max(1, maxCacheEntries);
    }

    public async Task<CveReport> QueryCveAsync(string cveId, CancellationToken ct = default)
    {
        var (report, _) = await QueryCveCoreAsync(cveId, skipOsv: false, ct);
        return report;
    }

    public Task<(CveReport Report, bool NvdAttempted)> QueryCveWithStatusAsync(
        string cveId,
        bool skipOsv = false,
        CancellationToken ct = default)
        => QueryCveCoreAsync(cveId, skipOsv, ct);

    private async Task<(CveReport Report, bool NvdAttempted)> QueryCveCoreAsync(string cveId, bool skipOsv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cveId))
            return (new CveReport(cveId, null, null, CveReport.SourceUnknown), false);

        var normalizedCve = cveId.Trim().ToUpperInvariant();

        if (_cache.TryGetValue(normalizedCve, out var cached) &&
            cached.Expiry > DateTime.UtcNow &&
            !(skipOsv &&
              !cached.Report.CvssScore.HasValue &&
              string.Equals(cached.Report.Source, "OSV.dev", StringComparison.OrdinalIgnoreCase)))
        {
            return (cached.Report, false);
        }

        // 1. Try OSV.dev first (unless caller already retrieved OSV detail in the same resolution flow)
        var osvConfigured = !skipOsv && !string.IsNullOrWhiteSpace(_options.OsvApiBaseUrl);
        var osvAttempted = osvConfigured;
        bool osvFailed = false;
        CveReport? report = null;
        if (!skipOsv)
        {
            try
            {
                report = await TryQueryOsvAsync(normalizedCve, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                osvFailed = true;
            }
        }

        // 2. If OSV didn't provide a score, try NVD
        var nvdConfigured = !string.IsNullOrWhiteSpace(_options.NvdApiBaseUrl);
        var nvdAttempted = false;
        bool nvdFailed = false;
        if (report == null || report.CvssScore == null)
        {
            nvdAttempted = nvdConfigured;
            try
            {
                var nvdReport = await TryQueryNvdAsync(normalizedCve, ct);
                if (nvdReport != null && nvdReport.CvssScore != null)
                {
                    report = nvdReport;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                nvdFailed = true;
            }
        }

        if (report == null || report.CvssScore == null)
        {
            var attemptedSources = (osvAttempted ? 1 : 0) + (nvdAttempted ? 1 : 0);
            var failedSources = (osvFailed ? 1 : 0) + (nvdFailed ? 1 : 0);

            // Every source that was actually queried failed: this is a transient lookup failure.
            if (attemptedSources > 0 && failedSources == attemptedSources)
            {
                return (new CveReport(normalizedCve, null, null, CveReport.SourceQueryFailed, "Query timed out or failed to reach vulnerability databases."), nvdAttempted);
            }

            // One source failed while another answered (possibly with "no record"): the result is
            // incomplete, but it is not a total lookup failure and must not be cached as resolved.
            if (failedSources > 0)
            {
                return (new CveReport(normalizedCve, null, null, CveReport.SourcePartiallyFailed, "One vulnerability source was unavailable; details may be incomplete."), nvdAttempted);
            }
        }

        report ??= new CveReport(normalizedCve, null, null, CveReport.SourceUnknown, "Vulnerability details unavailable.");

        // Unknown or unscored entries use a shorter TTL so embargoed disclosures or pending CVSS ratings
        // are re-checked promptly; confirmed CVE details with CVSS scores use full CacheExpirationHours.
        var nowUtc = DateTime.UtcNow;
        var useShortTtl = string.Equals(report.Source, CveReport.SourceUnknown, StringComparison.OrdinalIgnoreCase)
            || !report.CvssScore.HasValue;
        var cacheDuration = useShortTtl
            ? TimeSpan.FromHours(_options.UnknownCacheExpirationHours)
            : TimeSpan.FromHours(_options.CacheExpirationHours);

        lock (_cacheWriteLock)
        {
            PruneCacheIfNeeded(nowUtc);
            _cache[normalizedCve] = (report, nowUtc.Add(cacheDuration));
        }

        return (report, nvdAttempted);
    }

    private void PruneCacheIfNeeded(DateTime nowUtc)
    {
        if (_cache.Count < _maxCacheEntries)
        {
            return;
        }

        foreach (var kv in _cache)
        {
            if (kv.Value.Expiry <= nowUtc)
            {
                _cache.TryRemove(kv.Key, out _);
            }
        }

        if (_cache.Count >= _maxCacheEntries)
        {
            var excess = _cache.Count - _maxCacheEntries + 1;
            var evictCount = Math.Max(excess, _maxCacheEntries / 4);
            foreach (var key in _cache
                         .OrderBy(kv => kv.Value.Expiry)
                         .Take(evictCount)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    public async Task<IReadOnlyList<CveReport>> QueryMultipleCvesAsync(IEnumerable<string> cveIds, CancellationToken ct = default)
    {
        var distinctIds = cveIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var throttler = new SemaphoreSlim(4);
        var tasks = distinctIds.Select(async id =>
        {
            await throttler.WaitAsync(ct);
            try
            {
                return await QueryCveAsync(id, ct);
            }
            finally
            {
                throttler.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    private async Task<CveReport?> TryQueryOsvAsync(string cveId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.OsvApiBaseUrl))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("OsvClient");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var url = $"{_options.OsvApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(cveId)}";
            var response = await client.GetAsync(url, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OSV returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            var root = doc.RootElement;
            string? summary = null;
            if (root.TryGetProperty("summary", out var summaryProp))
            {
                summary = summaryProp.GetString();
            }

            // Fall back to first paragraph of details if summary is empty
            if (string.IsNullOrWhiteSpace(summary) && root.TryGetProperty("details", out var detailsProp))
            {
                var details = detailsProp.GetString();
                if (!string.IsNullOrWhiteSpace(details))
                {
                    var firstPara = details.Split(ParagraphSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    summary = firstPara?.Trim();
                }
            }

            double? cvssScore = null;

            // Check database_specific for direct score
            if (root.TryGetProperty("database_specific", out var dbSpecific) &&
                dbSpecific.TryGetProperty("severity", out var dbSeverityProp))
            {
                if (dbSeverityProp.ValueKind == JsonValueKind.Number)
                    cvssScore = dbSeverityProp.GetDouble();
                else if (dbSeverityProp.ValueKind == JsonValueKind.String &&
                         double.TryParse(dbSeverityProp.GetString(), out var parsedScore))
                    cvssScore = parsedScore;
            }

            // Check severity array
            if (cvssScore == null && root.TryGetProperty("severity", out var severityArray) &&
                severityArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in severityArray.EnumerateArray())
                {
                    if (item.TryGetProperty("score", out var scoreProp))
                    {
                        var scoreStr = scoreProp.GetString();
                        if (!string.IsNullOrWhiteSpace(scoreStr))
                        {
                            // Could be direct number or CVSS vector like CVSS:3.1/AV:N/AC:L/...
                            if (double.TryParse(scoreStr, out var num))
                            {
                                cvssScore = num;
                                break;
                            }
                            else if (scoreStr.StartsWith("CVSS:3", StringComparison.OrdinalIgnoreCase) ||
                                     scoreStr.StartsWith("CVSS:4", StringComparison.OrdinalIgnoreCase))
                            {
                                var calculatedScore = CvssCalculator.CalculateBaseScore(scoreStr);
                                if (calculatedScore.HasValue)
                                {
                                    cvssScore = calculatedScore.Value;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            var severityLabel = MapCvssToSeverity(cvssScore);
            return new CveReport(cveId, cvssScore, severityLabel, "OSV.dev", summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Failed to query OSV for {CveId}. Falling back.", cveId);
            throw;
        }
    }

    private async Task<CveReport?> TryQueryNvdAsync(string cveId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.NvdApiBaseUrl))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("NvdClient");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var url = $"{_options.NvdApiBaseUrl}?cveId={Uri.EscapeDataString(cveId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_options.NvdApiKey))
            {
                request.Headers.Add("apiKey", _options.NvdApiKey);
            }

            var response = await client.SendAsync(request, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"NVD returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            var root = doc.RootElement;
            if (!root.TryGetProperty("vulnerabilities", out var vulns) || vulns.GetArrayLength() == 0)
                return null;

            var firstVuln = vulns[0];
            if (!firstVuln.TryGetProperty("cve", out var cveObj))
                return null;

            string? summary = null;
            if (cveObj.TryGetProperty("descriptions", out var descs) && descs.GetArrayLength() > 0)
            {
                summary = descs[0].GetProperty("value").GetString();
            }

            double? cvssScore = null;
            if (cveObj.TryGetProperty("metrics", out var metrics))
            {
                // Check cvssMetricV40 (CVSS v4.0)
                if (metrics.TryGetProperty("cvssMetricV40", out var v40) && v40.GetArrayLength() > 0)
                {
                    cvssScore = v40[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
                // Check cvssMetricV31
                else if (metrics.TryGetProperty("cvssMetricV31", out var v31) && v31.GetArrayLength() > 0)
                {
                    cvssScore = v31[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
                // Check cvssMetricV30
                else if (metrics.TryGetProperty("cvssMetricV30", out var v30) && v30.GetArrayLength() > 0)
                {
                    cvssScore = v30[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
                // Check cvssMetricV2
                else if (metrics.TryGetProperty("cvssMetricV2", out var v2) && v2.GetArrayLength() > 0)
                {
                    cvssScore = v2[0].GetProperty("cvssData").GetProperty("baseScore").GetDouble();
                }
            }

            var severityLabel = MapCvssToSeverity(cvssScore);
            return new CveReport(cveId, cvssScore, severityLabel, "NVD", summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Failed to query NVD for {CveId}.", cveId);
            throw;
        }
    }

    public static string? MapCvssToSeverity(double? score)
    {
        if (score == null)
            return null;

        var val = score.Value;
        if (val >= 9.0)
            return GitHubLabels.Severity1;
        if (val >= 7.0)
            return GitHubLabels.Severity2;
        if (val >= 4.0)
            return GitHubLabels.Severity3;
        return GitHubLabels.Severity4;
    }
}

using System.Xml;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Implementation of OBS REST API queries using public endpoints.
/// Strictly appends '?package=' to prevent massive whole-project 6000+ package XML downloads.
/// </summary>
public class ObsBuildService : IObsBuildService, IObsBuildFreshnessGateway
{
    private readonly HttpClient _httpClient;
    private readonly ObsOptions _options;
    private readonly ILogger<ObsBuildService>? _logger;

    public ObsBuildService(
        HttpClient httpClient,
        IOptions<PackageUpdateOptions> options,
        ILogger<ObsBuildService>? logger = null)
    {
        _httpClient = httpClient;
        _options = options.Value.Obs;
        _logger = logger;
    }

    public async Task<string?> GetPackageServiceRevisionAsync(string project, string package, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(package))
            return null;

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/public/source/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(package)}/_service";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("OBS _service returned {StatusCode} for {Project}/{Package}", response.StatusCode, project, package);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });

            bool insideParam = false;
            string? currentParamName = null;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "param")
                {
                    insideParam = true;
                    currentParamName = reader.GetAttribute("name");
                }
                else if (reader.NodeType == XmlNodeType.Text && insideParam)
                {
                    if (string.Equals(currentParamName, "revision", StringComparison.OrdinalIgnoreCase))
                    {
                        var revision = reader.Value?.Trim();
                        if (!string.IsNullOrEmpty(revision))
                        {
                            return revision;
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "param")
                {
                    insideParam = false;
                    currentParamName = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch or parse OBS _service for {Project}/{Package}", project, package);
        }

        return null;
    }

    public async Task<ObsPackageBuildResult> GetPackageBuildResultAsync(
        string project, string package, string repository, string arch, CancellationToken ct = default)
    {
        var defaultResult = new ObsPackageBuildResult(
            project, package, repository, arch, ObsStatusCode.Unknown, "unknown", "No response or failed to query");

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(package))
            return defaultResult;

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/public/build/{Uri.EscapeDataString(project)}/_result?package={Uri.EscapeDataString(package)}";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("OBS _result returned {StatusCode} for {Project}/{Package}", response.StatusCode, project, package);
                return defaultResult with { Details = $"HTTP {response.StatusCode}" };
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });

            string? currentRepo = null;
            string? currentArch = null;
            bool currentResultDirty = false;

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "result")
                {
                    currentRepo = reader.GetAttribute("repository");
                    currentArch = reader.GetAttribute("arch");
                    currentResultDirty = string.Equals(reader.GetAttribute("dirty"), "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Name == "status")
                {
                    var statusPkg = reader.GetAttribute("package");
                    var statusCodeStr = reader.GetAttribute("code") ?? string.Empty;
                    var details = reader.GetAttribute("details");
                    var statusDirty = string.Equals(reader.GetAttribute("dirty"), "true", StringComparison.OrdinalIgnoreCase);

                    // Check if matches the desired repository and arch if specified
                    bool repoMatch = string.IsNullOrEmpty(repository) || string.Equals(currentRepo, repository, StringComparison.OrdinalIgnoreCase);
                    bool archMatch = string.IsNullOrEmpty(arch) || string.Equals(currentArch, arch, StringComparison.OrdinalIgnoreCase);
                    bool pkgMatch = string.IsNullOrEmpty(statusPkg) || string.Equals(statusPkg, package, StringComparison.OrdinalIgnoreCase);

                    if (repoMatch && archMatch && pkgMatch)
                    {
                        var code = ParseStatusCode(statusCodeStr);
                        return new ObsPackageBuildResult(
                            project,
                            package,
                            currentRepo ?? repository,
                            currentArch ?? arch,
                            code,
                            statusCodeStr,
                            details,
                            currentResultDirty || statusDirty);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to query OBS _result for {Project}/{Package}", project, package);
        }

        return defaultResult;
    }

    public async Task<ObsPackageBuildFreshness?> GetPackageBuildFreshnessAsync(
        string project,
        string package,
        string repository,
        string arch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(package) ||
            string.IsNullOrWhiteSpace(repository) ||
            string.IsNullOrWhiteSpace(arch))
        {
            return null;
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var sourceUrl = $"{baseUrl}/public/source/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(package)}";

        bool isServiceReady = true;
        string? directorySrcMd5 = null;
        string? serviceXsrcMd5 = null;

        try
        {
            using var sourceResponse = await _httpClient.GetAsync(sourceUrl, ct);
            if (!sourceResponse.IsSuccessStatusCode)
            {
                _logger?.LogDebug("OBS /public/source returned {StatusCode} for {Project}/{Package}", sourceResponse.StatusCode, project, package);
                return null;
            }

            await using var sourceStream = await sourceResponse.Content.ReadAsStreamAsync(ct);
            using var sourceReader = XmlReader.Create(sourceStream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });
            while (await sourceReader.ReadAsync())
            {
                if (sourceReader.NodeType != XmlNodeType.Element)
                    continue;

                if (sourceReader.Name == "directory")
                {
                    directorySrcMd5 = sourceReader.GetAttribute("srcmd5")?.Trim();
                }
                else if (sourceReader.Name == "serviceinfo")
                {
                    var serviceCode = sourceReader.GetAttribute("code")?.Trim();
                    isServiceReady = string.IsNullOrEmpty(serviceCode) ||
                                     string.Equals(serviceCode, "succeeded", StringComparison.OrdinalIgnoreCase);
                    serviceXsrcMd5 = sourceReader.GetAttribute("xsrcmd5")?.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to inspect OBS source metadata for {Project}/{Package}", project, package);
            return null;
        }

        var sourceMd5 = !string.IsNullOrWhiteSpace(serviceXsrcMd5) ? serviceXsrcMd5 : directorySrcMd5;

        // OBS _jobhistory?package={pkg}&limit=5 returns the 5 newest jobs in ascending starttime order;
        // dependency-triggered rebuilds of the same commit preserve srcmd5 with newer starttime, so the latest job is always present.
        var jobHistoryUrl = $"{baseUrl}/public/build/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(repository)}/{Uri.EscapeDataString(arch)}/_jobhistory?package={Uri.EscapeDataString(package)}&limit=5";
        string? bestSrcMd5 = null;
        DateTimeOffset? bestStartTime = null;
        ObsStatusCode bestCode = ObsStatusCode.Unknown;
        long bestStartSeconds = long.MinValue;
        bool foundAnyJob = false;

        try
        {
            using var jobResponse = await _httpClient.GetAsync(jobHistoryUrl, ct);
            if (!jobResponse.IsSuccessStatusCode)
            {
                _logger?.LogDebug("OBS _jobhistory returned {StatusCode} for {Project}/{Repo}/{Arch}/{Package}",
                    jobResponse.StatusCode, project, repository, arch, package);
                return null;
            }

            await using var jobStream = await jobResponse.Content.ReadAsStreamAsync(ct);
            using var jobReader = XmlReader.Create(jobStream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true });
            while (await jobReader.ReadAsync())
            {
                if (jobReader.NodeType != XmlNodeType.Element || jobReader.Name != "jobhist")
                    continue;

                var jobPkg = jobReader.GetAttribute("package");
                if (!string.IsNullOrEmpty(jobPkg) && !string.Equals(jobPkg, package, StringComparison.OrdinalIgnoreCase))
                    continue;

                var startRaw = jobReader.GetAttribute("starttime");
                long startSeconds = long.TryParse(
                    startRaw,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedSec)
                    ? parsedSec
                    : long.MinValue;

                if (!foundAnyJob || startSeconds >= bestStartSeconds)
                {
                    foundAnyJob = true;
                    bestStartSeconds = startSeconds;
                    bestSrcMd5 = jobReader.GetAttribute("srcmd5")?.Trim();
                    bestCode = ParseStatusCode(jobReader.GetAttribute("code") ?? string.Empty);
                    bestStartTime = startSeconds >= 0 ? DateTimeOffset.FromUnixTimeSeconds(startSeconds) : null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to query OBS _jobhistory for {Project}/{Package}", project, package);
            return null;
        }

        if (!foundAnyJob)
        {
            return new ObsPackageBuildFreshness(isServiceReady, sourceMd5, null, null, ObsStatusCode.Unknown);
        }

        return new ObsPackageBuildFreshness(isServiceReady, sourceMd5, bestSrcMd5, bestStartTime, bestCode);
    }

    private static ObsStatusCode ParseStatusCode(string rawCode)
    {
        return rawCode.Trim().ToLowerInvariant() switch
        {
            "succeeded" => ObsStatusCode.Succeeded,
            "failed" => ObsStatusCode.Failed,
            "building" => ObsStatusCode.Building,
            "scheduled" => ObsStatusCode.Scheduled,
            "unresolvable" => ObsStatusCode.Unresolvable,
            "broken" => ObsStatusCode.Broken,
            "blocked" => ObsStatusCode.Blocked,
            "disabled" => ObsStatusCode.Disabled,
            "excluded" => ObsStatusCode.Excluded,
            _ => ObsStatusCode.Unknown
        };
    }
}


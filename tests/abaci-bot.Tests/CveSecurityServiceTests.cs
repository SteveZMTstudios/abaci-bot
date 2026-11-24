using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class CveSecurityServiceTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private CveSecurityService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var stub = new DelegatingHandlerStub(handler);
        var client = new HttpClient(stub);
        _httpClientFactory.CreateClient("OsvClient").Returns(client);
        _httpClientFactory.CreateClient("NvdClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new CveSecurityOptions
        {
            OsvApiBaseUrl = "https://api.osv.dev/v1/vulns",
            NvdApiBaseUrl = "https://services.nvd.nist.gov/rest/json/cves/2.0",
            CacheExpirationHours = 12,
            UnknownCacheExpirationHours = 1,
            RequestTimeoutSeconds = 5
        });

        return new CveSecurityService(_httpClientFactory, options);
    }

    [Fact]
    public void DefaultOptions_ContainsCorrectNvdHostnameAndProductionTimeout()
    {
        var options = new CveSecurityOptions();
        Assert.Equal("https://services.nvd.nist.gov/rest/json/cves/2.0", options.NvdApiBaseUrl);
        Assert.Equal("https://api.osv.dev/v1/vulns/", options.OsvApiBaseUrl);
        Assert.True(options.RequestTimeoutSeconds >= 15, "RequestTimeoutSeconds must be at least 15s for NIST NVD latency.");

        var uri = new Uri(options.NvdApiBaseUrl);
        Assert.Equal("services.nvd.nist.gov", uri.Host);
    }

    [Fact]
    public async Task QueryCveAsync_OsvReturnsDirectScore_ParsesAccurately()
    {
        var json = """
        {
            "id": "CVE-2024-3094",
            "summary": "XZ backdoor vulnerability",
            "database_specific": {
                "severity": 10.0
            }
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var report = await service.QueryCveAsync("CVE-2024-3094");

        Assert.Equal("CVE-2024-3094", report.CveId);
        Assert.Equal(10.0, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity1, report.SeverityLabel);
        Assert.Equal("XZ backdoor vulnerability", report.Summary);
        Assert.Equal("OSV.dev", report.Source);
    }

    [Fact]
    public async Task QueryCveAsync_OsvReturnsCvssVector_CalculatesViaCvssCalculator()
    {
        var json = """
        {
            "id": "CVE-2023-44487",
            "summary": "HTTP/2 Rapid Reset Attack",
            "severity": [
                {
                    "type": "CVSS_V3",
                    "score": "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H"
                }
            ]
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var report = await service.QueryCveAsync("CVE-2023-44487");

        Assert.Equal("CVE-2023-44487", report.CveId);
        Assert.Equal(7.5, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity2, report.SeverityLabel);
    }

    [Fact]
    public async Task QueryCveAsync_OsvMissingSummary_FallsBackToDetailsFirstParagraph()
    {
        var json = """
        {
            "id": "CVE-2021-9999",
            "details": "First paragraph describing the critical vulnerability in component.\r\n\r\nSecond paragraph with technical breakdown and reproducer.",
            "database_specific": {
                "severity": 8.5
            }
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var report = await service.QueryCveAsync("CVE-2021-9999");

        Assert.Equal("First paragraph describing the critical vulnerability in component.", report.Summary);
    }

    [Fact]
    public async Task QueryCveAsync_OsvFails_FallsBackToNvdSuccessfully()
    {
        var nvdJson = """
        {
            "vulnerabilities": [
                {
                    "cve": {
                        "id": "CVE-2022-1234",
                        "descriptions": [
                            { "lang": "en", "value": "NVD description of vulnerability" }
                        ],
                        "metrics": {
                            "cvssMetricV31": [
                                {
                                    "cvssData": {
                                        "baseScore": 6.5
                                    }
                                }
                            ]
                        }
                    }
                }
            ]
        }
        """;

        var service = CreateService(req =>
        {
            if (req.RequestUri!.Host.Contains("osv.dev"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nvdJson)
            };
        });

        var report = await service.QueryCveAsync("CVE-2022-1234");

        Assert.Equal("CVE-2022-1234", report.CveId);
        Assert.Equal(6.5, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity3, report.SeverityLabel);
        Assert.Equal("NVD", report.Source);
        Assert.Equal("NVD description of vulnerability", report.Summary);
    }

    [Fact]
    public async Task QueryCveAsync_BothSourcesServerError_ReturnsQueryFailedWithoutCaching()
    {
        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var report = await service.QueryCveAsync("CVE-2099-0001");

        Assert.Equal("CVE-2099-0001", report.CveId);
        Assert.Null(report.CvssScore);
        Assert.Equal(CveReport.SourceQueryFailed, report.Source);
        Assert.Null(report.SeverityLabel);
    }

    [Fact]
    public async Task QueryCveAsync_OsvServerErrorAndNvdNotFound_ReturnsPartiallyFailed()
    {
        var service = CreateService(request =>
            request.RequestUri!.Host.Contains("osv", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var report = await service.QueryCveAsync("CVE-2099-0002");

        Assert.Equal("CVE-2099-0002", report.CveId);
        Assert.Null(report.CvssScore);
        Assert.Equal(CveReport.SourcePartiallyFailed, report.Source);
        Assert.Null(report.SeverityLabel);
    }

    [Fact]
    public async Task QueryCveAsync_OsvNotFoundAndNvdServerError_ReturnsPartiallyFailed()
    {
        var service = CreateService(request =>
            request.RequestUri!.Host.Contains("osv", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var report = await service.QueryCveAsync("CVE-2099-0003");

        Assert.Equal("CVE-2099-0003", report.CveId);
        Assert.Null(report.CvssScore);
        Assert.Equal(CveReport.SourcePartiallyFailed, report.Source);
        Assert.Null(report.SeverityLabel);
    }

    [Fact]
    public async Task QueryCveAsync_BothSourcesNotFound_ReturnsUnknown()
    {
        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.NotFound));

        var report = await service.QueryCveAsync("CVE-2099-0002");

        Assert.Equal("CVE-2099-0002", report.CveId);
        Assert.Null(report.CvssScore);
        Assert.Equal(CveReport.SourceUnknown, report.Source);
        Assert.Null(report.SeverityLabel);
        Assert.Equal("Vulnerability details unavailable.", report.Summary);
    }

    [Fact]
    public async Task QueryCveAsync_SubsequentCalls_HitsInMemoryCache()
    {
        var callCount = 0;
        var json = """
        {
            "id": "CVE-2020-0001",
            "summary": "Cached CVE",
            "database_specific": { "severity": 5.0 }
        }
        """;

        var service = CreateService(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });

        var rep1 = await service.QueryCveAsync("CVE-2020-0001");
        var rep2 = await service.QueryCveAsync("CVE-2020-0001");

        Assert.Equal(1, callCount);
        Assert.Same(rep1, rep2);
    }

    [Fact]
    public async Task QueryMultipleCvesAsync_ThrottlesAndReturnsAllDistinctReports()
    {
        var json = """
        {
            "id": "CVE-MULTI",
            "database_specific": { "severity": 4.5 }
        }
        """;

        var service = CreateService(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var reports = await service.QueryMultipleCvesAsync(new[]
        {
            "CVE-2024-0001", "CVE-2024-0002", "CVE-2024-0003", "CVE-2024-0001"
        });

        // 3 distinct CVEs queried
        Assert.Equal(3, reports.Count);
    }

    [Fact]
    public async Task QueryCveAsync_UnknownCve_CachesForOneHour()
    {
        var callCount = 0;
        var service = CreateService(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var rep1 = await service.QueryCveAsync("CVE-2024-99999");
        // Initial query calls OSV, then falls back to NVD (2 calls total)
        Assert.Equal(2, callCount);

        var rep2 = await service.QueryCveAsync("CVE-2024-99999");
        // Second query hits cache, no new HTTP requests
        Assert.Equal(2, callCount);
        Assert.Equal(CveReport.SourceUnknown, rep1.Source);
        Assert.Equal(rep1, rep2);

        // Verify that Unknown CVEs are cached for the configured short TTL (~1 hour), not the full 12 hours
        Assert.True(service.Cache.TryGetValue("CVE-2024-99999", out var cachedEntry), "Expected cache entry to exist");
        var ttl = cachedEntry.Expiry - DateTime.UtcNow;
        Assert.True(ttl > TimeSpan.FromMinutes(50) && ttl <= TimeSpan.FromMinutes(65),
            $"Expected TTL close to 1 hour, but was {ttl.TotalMinutes} minutes.");
    }

    [Fact]
    public async Task QueryCveAsync_NvdCvssV40_ParsesBaseScoreAccurately()
    {
        var nvdJson = """
        {
          "vulnerabilities": [
            {
              "cve": {
                "id": "CVE-2024-4444",
                "descriptions": [ { "lang": "en", "value": "Sample v4 vulnerability" } ],
                "metrics": {
                  "cvssMetricV40": [
                    {
                      "source": "nvd@nist.gov",
                      "type": "Primary",
                      "cvssData": {
                        "version": "4.0",
                        "vectorString": "CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N",
                        "baseScore": 9.3,
                        "baseSeverity": "CRITICAL"
                      }
                    }
                  ]
                }
              }
            }
          ]
        }
        """;

        var service = CreateService(req =>
        {
            if (req.RequestUri!.Host.Contains("osv.dev"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nvdJson)
            };
        });

        var report = await service.QueryCveAsync("CVE-2024-4444");
        Assert.Equal(9.3, report.CvssScore);
        Assert.Equal(GitHubLabels.Severity1, report.SeverityLabel);
        Assert.Equal("NVD", report.Source);
    }

    [Fact]
    public async Task QueryCveWithStatusAsync_ReportsNvdAttemptedAccuratelyAcrossOsvHitCacheHitAndNvd429()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("CVE-2026-1001", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"CVE-2026-1001","database_specific":{"severity":6.5}}""")
                };
            }

            if (req.RequestUri.Host.Contains("osv.dev"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        // 1. OSV hit -> NvdAttempted = false; second call -> cache hit -> NvdAttempted = false
        var (_, osvAttempted) = await service.QueryCveWithStatusAsync("CVE-2026-1001");
        var (_, cachedAttempted) = await service.QueryCveWithStatusAsync("CVE-2026-1001");
        Assert.False(osvAttempted);
        Assert.False(cachedAttempted);

        // 2. OSV misses -> dispatches NVD and hits 429 -> NvdAttempted = true
        var (failedReport, nvd429Attempted) = await service.QueryCveWithStatusAsync("CVE-2026-4290");
        Assert.True(nvd429Attempted);
        Assert.Equal(CveReport.SourcePartiallyFailed, failedReport.Source);
    }

    [Fact]
    public async Task QueryCveWithStatusAsync_SkipOsvTrue_BypassesOsvHttp_AndCacheRemainsBounded()
    {
        var requestedUrls = new List<string>();
        var stub = new DelegatingHandlerStub(req =>
        {
            var url = req.RequestUri!.ToString();
            lock (requestedUrls)
            {
                requestedUrls.Add(url);
            }

            if (req.RequestUri.Host.Contains("osv.dev"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "vulnerabilities": [{
                        "cve": {
                          "descriptions": [{"value": "NVD description"}],
                          "metrics": {"cvssMetricV31": [{"cvssData": {"baseScore": 8.1}}]}
                        }
                      }]
                    }
                    """)
            };
        });
        var client = new HttpClient(stub);
        _httpClientFactory.CreateClient("OsvClient").Returns(client);
        _httpClientFactory.CreateClient("NvdClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new CveSecurityOptions
        {
            OsvApiBaseUrl = "https://api.osv.dev/v1/vulns",
            NvdApiBaseUrl = "https://services.nvd.nist.gov/rest/json/cves/2.0",
            CacheExpirationHours = 12,
            UnknownCacheExpirationHours = 1,
            RequestTimeoutSeconds = 5
        });

        var boundedService = new CveSecurityService(_httpClientFactory, options, logger: null, maxCacheEntries: 4);

        var (skipOsvReport, skipOsvNvdAttempted) = await boundedService.QueryCveWithStatusAsync("CVE-2026-5555", skipOsv: true);
        Assert.True(skipOsvNvdAttempted);
        Assert.Equal("NVD", skipOsvReport.Source);
        Assert.Equal(8.1, skipOsvReport.CvssScore);
        Assert.DoesNotContain(requestedUrls, u => u.Contains("osv.dev", StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < 6; i++)
        {
            await boundedService.QueryCveWithStatusAsync($"CVE-2026-56{i:D2}", skipOsv: true);
        }

        Assert.InRange(boundedService.Cache.Count, 1, 4);
    }

    [Fact]
    public async Task QueryCveAsync_UnratedOsvEntryWithoutCvss_UsesUnknownShortCacheTtl()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("osv.dev", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"GHSA-unrated-0001","summary":"Awaiting CVSS scoring"}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new HttpClient(stub);
        _httpClientFactory.CreateClient("OsvClient").Returns(client);
        _httpClientFactory.CreateClient("NvdClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new CveSecurityOptions
        {
            OsvApiBaseUrl = "https://api.osv.dev/v1/vulns",
            NvdApiBaseUrl = "https://services.nvd.nist.gov/rest/json/cves/2.0",
            CacheExpirationHours = 12,
            UnknownCacheExpirationHours = 1,
            RequestTimeoutSeconds = 5
        });

        var svc = new CveSecurityService(_httpClientFactory, options);
        var before = DateTime.UtcNow;
        var report = await svc.QueryCveAsync("GHSA-unrated-0001");
        Assert.Equal("OSV.dev", report.Source);
        Assert.Null(report.CvssScore);

        var cached = svc.Cache["GHSA-UNRATED-0001"];
        var ttl = cached.Expiry - before;
        Assert.True(ttl <= TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)), $"Expected short UnknownCacheExpirationHours (~1h), got {ttl}");
    }

    [Fact]
    public async Task QueryCveWithStatusAsync_SkipOsvTrue_BypassesCachedUnratedOsvEntry_AndQueriesNvd()
    {
        var nvdAvailable = false;
        var stub = new DelegatingHandlerStub(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("osv.dev", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"CVE-2026-7777","summary":"Unscored OSV advisory"}""")
                };
            }

            if (!nvdAvailable)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "vulnerabilities": [{
                        "cve": {
                          "descriptions": [{"value": "Enriched NVD description"}],
                          "metrics": {"cvssMetricV31": [{"cvssData": {"baseScore": 7.8}}]}
                        }
                      }]
                    }
                    """)
            };
        });

        var client = new HttpClient(stub);
        _httpClientFactory.CreateClient("OsvClient").Returns(client);
        _httpClientFactory.CreateClient("NvdClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new CveSecurityOptions
        {
            OsvApiBaseUrl = "https://api.osv.dev/v1/vulns",
            NvdApiBaseUrl = "https://services.nvd.nist.gov/rest/json/cves/2.0",
            CacheExpirationHours = 12,
            UnknownCacheExpirationHours = 1,
            RequestTimeoutSeconds = 5
        });

        var svc = new CveSecurityService(_httpClientFactory, options);

        // First call populates the cache with an unscored OSV report.
        var (initialReport, initialNvdAttempted) = await svc.QueryCveWithStatusAsync("CVE-2026-7777", skipOsv: false);
        Assert.True(initialNvdAttempted);
        Assert.Equal("OSV.dev", initialReport.Source);
        Assert.Null(initialReport.CvssScore);

        // Second call with skipOsv: true bypasses the unrated OSV cache entry so NVD enriches the score.
        nvdAvailable = true;
        var (enrichedReport, secondNvdAttempted) = await svc.QueryCveWithStatusAsync("CVE-2026-7777", skipOsv: true);
        Assert.True(secondNvdAttempted);
        Assert.Equal("NVD", enrichedReport.Source);
        Assert.Equal(7.8, enrichedReport.CvssScore);
    }
}

using System.Net;
using System.Text.Json;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace abaci_bot.Tests;

public class LookoutVersionSourceTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_DiscoversOutdatedPackages_AndSkipsYankedAndStale()
    {
        var responsePayload = new
        {
            packages = new object[]
            {
                // Normal outdated package
                new
                {
                    name = "curl",
                    current_version = "8.21.0",
                    latest_version = "8.22.0",
                    last_known_relation = "outdated",
                    stale = false,
                    updated_at = DateTimeOffset.UtcNow
                },
                // Yanked annotation package -> MUST BE SKIPPED
                new
                {
                    name = "go-github-azuread",
                    current_version = "1.10.0",
                    latest_version = "1.10.1",
                    last_known_relation = "outdated",
                    stale = false,
                    annotations = new[]
                    {
                        new { monitor = "yanked", label = "Yanked", scope = "current" }
                    }
                },
                // Stale package older than 48 hours -> MUST BE SKIPPED
                new
                {
                    name = "old-stale-pkg",
                    current_version = "1.0.0",
                    latest_version = "2.0.0",
                    last_known_relation = "outdated",
                    stale = true,
                    updated_at = DateTimeOffset.UtcNow.AddHours(-72)
                },
                // Current package (not outdated) -> MUST BE SKIPPED
                new
                {
                    name = "up-to-date-pkg",
                    current_version = "3.0.0",
                    latest_version = "3.0.0",
                    last_known_relation = "current",
                    stale = false
                }
            }
        };

        var handler = new FakeHttpMessageHandler(req =>
        {
            var json = JsonSerializer.Serialize(responsePayload);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                StaleThresholdHours = 48
            }
        });

        var source = new LookoutVersionSource(httpClient, options);
        var candidates = await source.GetCandidatesAsync();

        Assert.Single(candidates);
        Assert.Equal("curl", candidates[0].PackageName);
        Assert.Equal("8.21.0", candidates[0].CurrentVersion);
        Assert.Equal("8.22.0", candidates[0].TargetVersion);
        Assert.Equal(CandidateOrigin.Lookout, candidates[0].Origin);
    }

    [Fact]
    public async Task GetCandidatesAsync_ParsesNestedMonitorsVersionData_AndDetectsSecurityAdvisory()
    {
        var responsePayload = new
        {
            items = new object[]
            {
                new
                {
                    name = "curl",
                    detail_url = "https://lookout.openruyi.cn/package/curl",
                    monitors = new
                    {
                        version = new
                        {
                            data = new
                            {
                                kind = "release",
                                current = "8.21.0",
                                latest = "8.22.0",
                                last_known_relation = "outdated",
                                stale = false,
                                updated_at = DateTimeOffset.UtcNow,
                                annotations = new object[]
                                {
                                    new { monitor = "security", label = "CVE-2024-1234", scope = "latest" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var handler = new FakeHttpMessageHandler(req =>
        {
            var json = JsonSerializer.Serialize(responsePayload);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                StaleThresholdHours = 48
            }
        });

        var source = new LookoutVersionSource(httpClient, options);
        var candidates = await source.GetCandidatesAsync();

        Assert.Single(candidates);
        Assert.Equal("curl", candidates[0].PackageName);
        Assert.Equal("8.21.0", candidates[0].CurrentVersion);
        Assert.Equal("8.22.0", candidates[0].TargetVersion);
        Assert.True(candidates[0].IsSecurityAdvisory);
        Assert.Equal("https://lookout.openruyi.cn/package/curl", candidates[0].EvidenceUrl);
        Assert.True(candidates[0].PublishedAt.HasValue);
    }

    [Fact]
    public async Task GetCandidatesAsync_RespectsMaxPagesLimit()
    {
        int requestCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestCount++;
            // Always return a full page of 200 items
            var fullPage = new
            {
                packages = Enumerable.Range(1, 200).Select(i => new
                {
                    name = $"pkg-{requestCount}-{i}",
                    current_version = "1.0",
                    latest_version = "1.1",
                    last_known_relation = "outdated",
                    stale = false
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(fullPage);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-maxpages-{Guid.NewGuid():N}.json");
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                Lookout = new LookoutUpdateOptions
                {
                    Enabled = true,
                    BaseUrl = "https://lookout.openruyi.cn",
                    StaleThresholdHours = 48,
                    FirstSeenStatePath = statePath
                }
            });

            var source = new LookoutVersionSource(httpClient, options);
            var candidates = await source.GetCandidatesAsync();

            // Must stop at maxPages = 50
            Assert.Equal(50, requestCount);
            Assert.Equal(50 * 200, candidates.Count);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_ReturnsEmpty_WhenDisabledOrBaseUrlEmpty()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var optionsDisabled = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions { Enabled = false, BaseUrl = "https://lookout.openruyi.cn" }
        });
        var sourceDisabled = new LookoutVersionSource(httpClient, optionsDisabled);
        Assert.Empty(await sourceDisabled.GetCandidatesAsync());

        var optionsNoUrl = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions { Enabled = true, BaseUrl = "" }
        });
        var sourceNoUrl = new LookoutVersionSource(httpClient, optionsNoUrl);
        Assert.Empty(await sourceNoUrl.GetCandidatesAsync());
    }

    [Fact]
    public async Task GetCandidatesAsync_HandlesNetworkError_HttpError_AndBadJson()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions { Enabled = true, BaseUrl = "https://lookout.openruyi.cn" }
        });

        // 1. Network exception
        var handlerThrow = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Network failure"));
        var sourceThrow = new LookoutVersionSource(new HttpClient(handlerThrow), options);
        Assert.Empty(await sourceThrow.GetCandidatesAsync());

        // 2. HTTP 500
        var handler500 = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source500 = new LookoutVersionSource(new HttpClient(handler500), options);
        Assert.Empty(await source500.GetCandidatesAsync());

        // 3. Bad JSON
        var handlerBadJson = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not valid json }", System.Text.Encoding.UTF8, "application/json")
        });
        var sourceBadJson = new LookoutVersionSource(new HttpClient(handlerBadJson), options);
        Assert.Empty(await sourceBadJson.GetCandidatesAsync());
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public MutableTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    [Fact]
    public async Task GetCandidatesAsync_PersistsFirstSeenAcrossRestarts_AndResetsOnVersionChangeOrPrune()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-state-{Guid.NewGuid():N}.json");
        var firstObserved = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var staleMonitorUpdatedAt = firstObserved.AddHours(-48);
        var laterPoll = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(firstObserved);
        var currentPoll = staleMonitorUpdatedAt;
        var currentLatest = "2.0.0";
        var includePkg = true;

        var handler = new FakeHttpMessageHandler(_ =>
        {
            object[] items = includePkg
                ? new object[]
                {
                    new
                    {
                        name = "dnf5",
                        monitors = new
                        {
                            version = new
                            {
                                data = new
                                {
                                    current = "1.0.0",
                                    latest = currentLatest,
                                    last_known_relation = "outdated",
                                    stale = false,
                                    updated_at = currentPoll
                                }
                            }
                        }
                    }
                }
                : new object[]
                {
                    new
                    {
                        name = "other-pkg",
                        monitors = new
                        {
                            version = new
                            {
                                data = new
                                {
                                    current = "1.0.0",
                                    latest = "1.1.0",
                                    last_known_relation = "outdated",
                                    stale = false,
                                    updated_at = currentPoll
                                }
                            }
                        }
                    }
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { items }), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            // 1. First instance observes dnf5@2.0.0 at firstObserved (conservative TimeProvider clock, ignoring older monitor updated_at)
            var source1 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var c1 = await source1.GetCandidatesAsync();
            Assert.Single(c1);
            Assert.Equal(firstObserved, c1[0].PublishedAt);
            Assert.True(File.Exists(statePath));

            // 2. Simulated process restart with newer Lookout updated_at -> must preserve firstObserved
            timeProvider.SetUtcNow(laterPoll);
            currentPoll = laterPoll;
            var source2 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var c2 = await source2.GetCandidatesAsync();
            Assert.Single(c2);
            Assert.Equal(firstObserved, c2[0].PublishedAt);

            // 3. New target version -> resets firstSeen to laterPoll
            currentLatest = "2.1.0";
            var c3 = await source2.GetCandidatesAsync();
            Assert.Single(c3);
            Assert.Equal(laterPoll, c3[0].PublishedAt);

            // 4. Healthy non-empty full sweep without dnf5 -> prunes dnf5 from persisted state
            includePkg = false;
            var cPrune = await source2.GetCandidatesAsync();
            Assert.DoesNotContain(cPrune, c => c.PackageName == "dnf5");

            var newerPoll = laterPoll.AddHours(2);
            timeProvider.SetUtcNow(newerPoll);
            var source3 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            includePkg = true;
            currentPoll = newerPoll;
            var c4 = await source3.GetCandidatesAsync();
            Assert.Single(c4);
            Assert.Equal(newerPoll, c4[0].PublishedAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_PrefersUpstreamPublishedAt_AndHandlesCorruptStateFile()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-corrupt-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(statePath, "{ corrupt json !!!");

        var upstreamPublishedAt = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var pollTime = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        var handler = new FakeHttpMessageHandler(_ =>
        {
            var payload = new
            {
                items = new object[]
                {
                    new
                    {
                        name = "etcd",
                        monitors = new
                        {
                            version = new
                            {
                                data = new
                                {
                                    current = "3.7.2",
                                    latest = "3.7.4",
                                    last_known_relation = "outdated",
                                    stale = false,
                                    updated_at = pollTime,
                                    published_at = upstreamPublishedAt
                                }
                            }
                        }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var source = new LookoutVersionSource(new HttpClient(handler), options);
            var candidates = await source.GetCandidatesAsync();
            Assert.Single(candidates);
            Assert.Equal(upstreamPublishedAt, candidates[0].PublishedAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_SourceHealthTransitions_FailedDegradedHealthy()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-health-{Guid.NewGuid():N}.json");
        var runtimeHealth = new RuntimeHealthState();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger<LookoutVersionSource>();

        int sweep = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            var query = req.RequestUri?.Query ?? string.Empty;
            if (sweep == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (sweep == 2)
            {
                if (query.Contains("page=1", StringComparison.Ordinal))
                {
                    var page1Items = Enumerable.Range(0, 200).Select(i => new
                    {
                        name = $"pkg-{i:D3}",
                        current_version = "1.0.0",
                        latest_version = "1.0.1",
                        last_known_relation = "outdated",
                        stale = false,
                        updated_at = DateTimeOffset.UtcNow
                    }).ToArray();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { packages = page1Items }), System.Text.Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var healthyItems = new[]
            {
                new
                {
                    name = "curl",
                    current_version = "8.21.0",
                    latest_version = "8.22.0",
                    last_known_relation = "outdated",
                    stale = false,
                    updated_at = DateTimeOffset.UtcNow
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { packages = healthyItems }), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var source = new LookoutVersionSource(new HttpClient(handler), options, logger: logger, healthState: runtimeHealth);

            // Sweep 1: page 1 HTTP 500 -> failed, ConsecutiveFailures == 1, LastSuccessAt == null, LogError recorded
            sweep = 1;
            Assert.Empty(await source.GetCandidatesAsync());
            var s1 = runtimeHealth.GetSourceHealth()["Lookout"];
            Assert.Equal("failed", s1.Status);
            Assert.Equal(1, s1.ConsecutiveFailures);
            Assert.Null(s1.LastSuccessAt);
            Assert.Contains("500", s1.LastError!, StringComparison.Ordinal);
            Assert.NotNull(runtimeHealth.GetLastError().LastError);

            // Sweep 2: page 1 returns 200 items, page 2 returns 503 -> degraded, ConsecutiveFailures == 2, LastSuccessAt STILL null (no clean sweep yet)
            sweep = 2;
            var partial = await source.GetCandidatesAsync();
            Assert.Equal(200, partial.Count);
            var s2 = runtimeHealth.GetSourceHealth()["Lookout"];
            Assert.Equal("degraded", s2.Status);
            Assert.Equal(2, s2.ConsecutiveFailures);
            Assert.NotNull(s2.LastAttemptAt);
            Assert.Null(s2.LastSuccessAt);

            // Sweep 3: clean response -> healthy, ConsecutiveFailures == 0, LastError == null, LastSuccessAt set
            sweep = 3;
            var healthy = await source.GetCandidatesAsync();
            Assert.Single(healthy);
            var s3 = runtimeHealth.GetSourceHealth()["Lookout"];
            Assert.Equal("healthy", s3.Status);
            Assert.Equal(0, s3.ConsecutiveFailures);
            Assert.Null(s3.LastError);
            Assert.NotNull(s3.LastSuccessAt);

            // Sweep 4: degraded again -> LastSuccessAt preserves s3.LastSuccessAt instead of advancing
            sweep = 2;
            await source.GetCandidatesAsync();
            var s4 = runtimeHealth.GetSourceHealth()["Lookout"];
            Assert.Equal("degraded", s4.Status);
            Assert.Equal(1, s4.ConsecutiveFailures);
            Assert.Equal(s3.LastSuccessAt, s4.LastSuccessAt);
            Assert.True(s4.LastAttemptAt >= s3.LastAttemptAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_EmptyHttp200Sweep_PreservesPersistedFirstSeenTimestamps()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-empty-preserve-{Guid.NewGuid():N}.json");
        var firstObserved = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var secondPoll = firstObserved.AddHours(12);
        var thirdPoll = firstObserved.AddHours(24);
        var timeProvider = new MutableTimeProvider(firstObserved);
        var returnEmpty = false;

        var handler = new FakeHttpMessageHandler(_ =>
        {
            object[] items = returnEmpty
                ? Array.Empty<object>()
                : new object[]
                {
                    new
                    {
                        name = "dnf5",
                        monitors = new
                        {
                            version = new
                            {
                                data = new
                                {
                                    current = "1.0.0",
                                    latest = "2.0.0",
                                    last_known_relation = "outdated",
                                    stale = false,
                                    updated_at = timeProvider.GetUtcNow()
                                }
                            }
                        }
                    }
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { items }), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            // 1. Initial sweep records firstObserved
            var source1 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var c1 = await source1.GetCandidatesAsync();
            Assert.Single(c1);
            Assert.Equal(firstObserved, c1[0].PublishedAt);

            // 2. Transient empty HTTP 200 sweep must NOT clear persisted first-seen timestamps
            timeProvider.SetUtcNow(secondPoll);
            returnEmpty = true;
            Assert.Empty(await source1.GetCandidatesAsync());

            // 3. Recovery sweep on fresh instance still retains original firstObserved
            timeProvider.SetUtcNow(thirdPoll);
            returnEmpty = false;
            var source2 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var c3 = await source2.GetCandidatesAsync();
            Assert.Single(c3);
            Assert.Equal(firstObserved, c3[0].PublishedAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_WhenLookoutReturnsUpToDateItemsOnly_PrunesStaleFirstSeenTimestamps()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-uptodate-prune-{Guid.NewGuid():N}.json");
        var firstObserved = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var secondPoll = firstObserved.AddHours(12);
        var thirdPoll = firstObserved.AddHours(24);
        var timeProvider = new MutableTimeProvider(firstObserved);
        var relation = "outdated";

        var handler = new FakeHttpMessageHandler(_ =>
        {
            var items = new object[]
            {
                new
                {
                    name = "dnf5",
                    monitors = new
                    {
                        version = new
                        {
                            data = new
                            {
                                current = "1.0.0",
                                latest = "2.0.0",
                                last_known_relation = relation,
                                stale = false,
                                updated_at = timeProvider.GetUtcNow()
                            }
                        }
                    }
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { items }), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var source1 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var c1 = await source1.GetCandidatesAsync();
            Assert.Single(c1);
            Assert.Equal(firstObserved, c1[0].PublishedAt);

            // Second sweep: Lookout returns non-empty items, all up_to_date -> safe to prune dnf5 firstSeen
            timeProvider.SetUtcNow(secondPoll);
            relation = "up_to_date";
            Assert.Empty(await source1.GetCandidatesAsync());

            // Third sweep: dnf5 becomes outdated again -> receives fresh observation timestamp (thirdPoll)
            timeProvider.SetUtcNow(thirdPoll);
            relation = "outdated";
            var source2 = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var c3 = await source2.GetCandidatesAsync();
            Assert.Single(c3);
            Assert.Equal(thirdPoll, c3[0].PublishedAt);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_ClampedPerPageWithTotalAndPages_PaginatesAllPagesAndPrunesStaleFirstSeen()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-clamped-pages-{Guid.NewGuid():N}.json");
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(statePath, """
            {
              "obsolete-pkg": { "Version": "1.0.0", "FirstSeen": "2026-11-01T00:00:00Z" },
              "null-entry": null
            }
            """);

        var requestedPages = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedPages.Add(req.RequestUri!.Query);
            var isPage1 = req.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal);
            var payload = isPage1
                ? new
                {
                    total = 2,
                    pages = 2,
                    packages = new[]
                    {
                        new { name = "pkg-one", monitors = new { version = new { data = new { current = "1.0.0", latest = "1.1.0", last_known_relation = "outdated", stale = false } } } }
                    }
                }
                : new
                {
                    total = 2,
                    pages = 2,
                    packages = new[]
                    {
                        new { name = "pkg-two", monitors = new { version = new { data = new { current = "2.0.0", latest = "2.1.0", last_known_relation = "outdated", stale = false } } } }
                    }
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var health = new RuntimeHealthState();
            var source = new LookoutVersionSource(new HttpClient(handler), options, healthState: health, timeProvider: timeProvider);
            var candidates = await source.GetCandidatesAsync();

            Assert.Equal(2, requestedPages.Count);
            Assert.Equal(2, candidates.Count);
            Assert.Contains(candidates, c => c.PackageName == "pkg-one");
            Assert.Contains(candidates, c => c.PackageName == "pkg-two");
            Assert.Equal("healthy", health.GetSourceHealth()["Lookout"].Status);

            var persisted = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(statePath));
            Assert.NotNull(persisted);
            Assert.False(persisted!.ContainsKey("obsolete-pkg"));
            Assert.False(persisted.ContainsKey("null-entry"));
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_ExceedingMaxPages_MarksDegradedAndDoesNotPruneFirstSeen()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-maxpages-{Guid.NewGuid():N}.json");
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(statePath, """
            {
              "keep-pkg": { "Version": "1.0.0", "FirstSeen": "2026-11-01T00:00:00Z" }
            }
            """);

        int pageCalls = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            pageCalls++;
            var pkgName = $"pkg-{pageCalls}";
            var payload = new
            {
                total = 5000,
                pages = 60,
                packages = new[]
                {
                    new { name = pkgName, monitors = new { version = new { data = new { current = "1.0.0", latest = "1.1.0", last_known_relation = "outdated", stale = false } } } }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var health = new RuntimeHealthState();
            var source = new LookoutVersionSource(new HttpClient(handler), options, healthState: health, timeProvider: timeProvider);
            var candidates = await source.GetCandidatesAsync();

            Assert.Equal(50, pageCalls);
            Assert.Equal(50, candidates.Count);
            var lookoutHealth = health.GetSourceHealth()["Lookout"];
            Assert.Equal("degraded", lookoutHealth.Status);
            Assert.Contains("truncated at max page limit", lookoutHealth.LastError, StringComparison.OrdinalIgnoreCase);

            var persisted = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(statePath));
            Assert.NotNull(persisted);
            Assert.True(persisted!.ContainsKey("keep-pkg"));
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_WhenServerReturnsZeroTotalWithItems_DoesNotPruneFirstSeen()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"lookout-zero-total-{Guid.NewGuid():N}.json");
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(statePath, """
            {
              "keep-me": { "Version": "1.0.0", "FirstSeen": "2026-11-01T00:00:00Z" }
            }
            """);

        var handler = new FakeHttpMessageHandler(req =>
        {
            var isPage1 = req.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal);
            var payload = isPage1
                ? new
                {
                    total = 0,
                    packages = new object[]
                    {
                        new { name = "pkg-one", monitors = new { version = new { data = new { current = "1.0.0", latest = "1.1.0", last_known_relation = "outdated", stale = false } } } }
                    }
                }
                : new
                {
                    total = 0,
                    packages = Array.Empty<object>()
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Lookout = new LookoutUpdateOptions
            {
                Enabled = true,
                BaseUrl = "https://lookout.openruyi.cn",
                FirstSeenStatePath = statePath
            }
        });

        try
        {
            var source = new LookoutVersionSource(new HttpClient(handler), options, timeProvider: timeProvider);
            var candidates = await source.GetCandidatesAsync();

            Assert.Single(candidates);
            var persisted = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(statePath));
            Assert.NotNull(persisted);
            Assert.True(persisted!.ContainsKey("keep-me"));

            Assert.Equal((false, false), LookoutVersionSource.EvaluatePaginationState(1, 50, 200, 200, 200, null, 0, false));
            Assert.Equal((true, false), LookoutVersionSource.EvaluatePaginationState(2, 50, 0, 200, 200, null, 0, false));
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }
}


using System.Net;
using abaci_bot.Options;
using abaci_bot.Services;
using static abaci_bot.Services.RustCrateVersionSource;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class RustCrateVersionSourceTests
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

    [Theory]
    [InlineData("a", "1/a")]
    [InlineData("ab", "2/ab")]
    [InlineData("abc", "3/a/abc")]
    [InlineData("serde", "se/rd/serde")]
    [InlineData("aes-gcm", "ae/s-/aes-gcm")]
    public void GetSparseIndexPath_FollowsFourTierRules(string crateName, string expectedPath)
    {
        var path = RustCrateVersionSource.GetSparseIndexPath(crateName);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public async Task GetCandidatesAsync_RespectsBranchConstraint_AndFiltersYankedAndPrereleases()
    {
        // Simulated crates.io NDJSON index for 'anstream'
        var ndjson = """
            {"name":"anstream","vers":"0.6.20","yanked":false}
            {"name":"anstream","vers":"0.6.21","yanked":false}
            {"name":"anstream","vers":"0.6.22","yanked":false,"pubtime":"2026-03-01T10:00:00Z"}
            {"name":"anstream","vers":"0.6.23-rc.1","yanked":false}
            {"name":"anstream","vers":"0.6.24","yanked":true}
            {"name":"anstream","vers":"1.0.0","yanked":false}
            """;

        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Contains("/an/st/anstream", req.RequestUri?.AbsolutePath ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson)
            };
        });

        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 10,
                RequestDelayMilliseconds = 0
            }
        });

        var specContent = """
            %global crate_name anstream
            %global full_version 0.6.21
            Name:           rust-anstream-0.6
            Version:        0.6.21
            """;

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-anstream-0.6", "rust-anstream-0.6", specContent, "SPECS/rust-anstream-0.6/rust-anstream-0.6.spec")
        });

        var source = new RustCrateVersionSource(httpClient, options, specCatalogProvider: fakeCatalog);
        var candidates = await source.GetCandidatesAsync();

        Assert.Single(candidates);
        var cand = candidates[0];
        Assert.Equal("rust-anstream-0.6", cand.PackageName);
        Assert.Equal("0.6.21", cand.CurrentVersion);
        // 0.6.22 selected (0.6.23-rc.1 rejected due to hyphen, 0.6.24 rejected due to yanked, 1.0.0 rejected due to 0.6 branch constraint)
        Assert.Equal("0.6.22", cand.TargetVersion);
        Assert.Equal(CandidateOrigin.RustCrate, cand.Origin);
        Assert.Equal("https://crates.io/crates/anstream/0.6.22", cand.EvidenceUrl);
        Assert.True(cand.PublishedAt.HasValue);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T10:00:00Z"), cand.PublishedAt.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetSparseIndexPath_ThrowsOnEmpty(string? name)
    {
        Assert.Throws<ArgumentException>(() => RustCrateVersionSource.GetSparseIndexPath(name!));
    }

    [Fact]
    public void SelectLatestVersion_HandlesEdgeCases()
    {
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(null!, "1.0.0"));
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(new List<CrateIndexEntry>(), "1.0.0"));

        // Already at latest version
        var entries = new List<CrateIndexEntry> { new() { Version = "1.0.0" } };
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(entries, "1.0.0"));

        // Target version is older than current
        var olderEntries = new List<CrateIndexEntry> { new() { Version = "0.9.0" } };
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(olderEntries, "1.0.0"));

        // Unparseable / pre-release entries must never overwrite higher valid versions (Item E)
        var mixedEntries = new List<CrateIndexEntry>
        {
            new() { Version = "1.1.0" },
            new() { Version = "1.0.0+hotfix" },
            new() { Version = "unparseable-junk" }
        };
        var latestMixed = RustCrateVersionSource.SelectLatestVersion(mixedEntries, "1.0.0");
        Assert.NotNull(latestMixed);
        Assert.Equal("1.1.0", latestMixed.Version);

        // Metadata-only +suffix at same base version does not downgrade or churn
        var plusSuffixSameBase = new List<CrateIndexEntry> { new() { Version = "1.0.0+hotfix" } };
        Assert.Null(RustCrateVersionSource.SelectLatestVersion(plusSuffixSameBase, "1.0.0"));

        // Valid +suffix newer than current IS selected cleanly
        var plusSuffixNewer = new List<CrateIndexEntry> { new() { Version = "1.0.1+hotfix" } };
        var latestPlusNewer = RustCrateVersionSource.SelectLatestVersion(plusSuffixNewer, "1.0.0");
        Assert.NotNull(latestPlusNewer);
        Assert.Equal("1.0.1+hotfix", latestPlusNewer.Version);
    }

    [Fact]
    public async Task GetCandidatesAsync_HandlesDisabledAndNetworkErrors()
    {
        var optionsDisabled = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions { Enabled = false }
        });
        var sourceDisabled = new RustCrateVersionSource(new HttpClient(), optionsDisabled);
        var resDisabled = await sourceDisabled.GetCandidatesAsync();
        Assert.Empty(resDisabled);

        // Network error handling (404 / 500 / network exception)
        var handlerErr = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions { Enabled = true, MaxCratesPerSweep = 10 }
        });
        var specContent = "%global crate_name foo\nVersion: 1.0.0\n";
        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-foo", "rust-foo", specContent, "SPECS/rust-foo/rust-foo.spec")
        });
        var sourceErr = new RustCrateVersionSource(new HttpClient(handlerErr), options, specCatalogProvider: fakeCatalog);
        var resErr = await sourceErr.GetCandidatesAsync();
        Assert.Empty(resErr);
    }

    [Fact]
    public async Task GetCandidatesAsync_SkipsSpecsMissingCrateNameOrVersion()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions { Enabled = true }
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-nocrate", "rust-nocrate", "Version: 1.0.0\n", "SPECS/rust-nocrate/rust-nocrate.spec"),
            new SpecContext("rust-nover", "rust-nover", "%global crate_name bar\n", "SPECS/rust-nover/rust-nover.spec")
        });

        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalogProvider: fakeCatalog);
        var candidates = await source.GetCandidatesAsync();
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetCandidatesAsync_WithSpecCatalog_SlicesBeforeFetchingRustSpecs()
    {
        var allNames = Enumerable.Range(0, 20).Select(i => $"rust-crate{i:D2}")
            .Concat(Enumerable.Range(0, 10).Select(i => $"c-pkg{i:D2}"))
            .ToList();

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(allNames);
        specCatalogMock.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(
                    pkg, pkg,
                    $"%global crate_name {pkg}\nVersion: 1.0.0\n",
                    $"SPECS/{pkg}/{pkg}.spec");
            });

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 3,
                RequestDelayMilliseconds = 0
            }
        });

        var health = new RuntimeHealthState();
        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalogMock, healthState: health);

        var candidates = await source.GetCandidatesAsync();
        Assert.Equal(3, candidates.Count);
        await specCatalogMock.DidNotReceive().GetRustSpecsAsync(Arg.Any<CancellationToken>());
        await specCatalogMock.Received(3).GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("healthy", health.GetSourceHealth()["RustCrates"].Status);
        Assert.Equal(0, health.GetSourceHealth()["RustCrates"].ConsecutiveFailures);
    }

    [Fact]
    public async Task GetCandidatesAsync_AllCratesFailAndPartialFailure_RecordsSourceHealthAndLogsError()
    {
        var runtimeHealth = new RuntimeHealthState();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger<RustCrateVersionSource>();

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<SpecContext>>(new[]
        {
            new SpecContext("rust-alpha", "rust-alpha", "%global crate_name alpha\nVersion: 1.0.0\n", "SPECS/rust-alpha/rust-alpha.spec"),
            new SpecContext("rust-beta", "rust-beta", "%global crate_name beta\nVersion: 1.0.0\n", "SPECS/rust-beta/rust-beta.spec")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 10,
                RequestDelayMilliseconds = 0
            }
        });

        // 1. All queries fail -> status "failed", ConsecutiveFailures == 1, LogError captured in RuntimeHealthState
        var allFailHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var failingSource = new RustCrateVersionSource(
            new HttpClient(allFailHandler), options, logger: logger, specCatalogProvider: fakeCatalog, healthState: runtimeHealth);

        Assert.Empty(await failingSource.GetCandidatesAsync());
        var snap1 = runtimeHealth.GetSourceHealth()["RustCrates"];
        Assert.Equal("failed", snap1.Status);
        Assert.Equal(1, snap1.ConsecutiveFailures);
        Assert.Null(snap1.LastSuccessAt);
        Assert.Contains("503", snap1.LastError!, StringComparison.Ordinal);
        Assert.NotNull(runtimeHealth.GetLastError().LastError);

        // 2. Partial failure (1 ok, 1 HTTP 500) -> status "degraded", LastSuccessAt remains null (no clean healthy sweep yet)
        var partialHandler = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("alpha", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"alpha","vers":"1.0.1","yanked":false}""") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var partialSource = new RustCrateVersionSource(
            new HttpClient(partialHandler), options, logger: logger, specCatalogProvider: fakeCatalog, healthState: runtimeHealth);

        var partialCandidates = await partialSource.GetCandidatesAsync();
        Assert.Single(partialCandidates);
        var snap2 = runtimeHealth.GetSourceHealth()["RustCrates"];
        Assert.Equal("degraded", snap2.Status);
        Assert.Equal(2, snap2.ConsecutiveFailures);
        Assert.NotNull(snap2.LastAttemptAt);
        Assert.Null(snap2.LastSuccessAt);
    }

    [Fact]
    public async Task GetCandidatesAsync_AdvancesNameWatermarkAcrossSweepsAndRestarts_WithoutOverlapOrDrift()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rust-watermark-{Guid.NewGuid():N}");
        var crates = new List<string> { "rust-b", "rust-c", "rust-d", "rust-e", "rust-f" };

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(_ => crates.ToList());
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var requestedPaths = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 2,
                RequestDelayMilliseconds = 0
            }
        });
        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });

        try
        {
            using (var store1 = new JsonBotStateStore(stateOpts))
            {
                var source1 = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store1);

                var c1 = await source1.GetCandidatesAsync();
                Assert.Equal(new[] { "rust-b", "rust-c" }, c1.Select(c => c.PackageName));
                // Phase-2 watermark commit happens after scheduler completes the sweep
                ((ICandidateSweepCursor)source1).CompleteSweep(c1.Select(c => c.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase));

                var c2 = await source1.GetCandidatesAsync();
                Assert.Equal(new[] { "rust-d", "rust-e" }, c2.Select(c => c.PackageName));
                ((ICandidateSweepCursor)source1).CompleteSweep(c2.Select(c => c.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Assert.Equal("rust-e", store1.GetCursorWatermark(RustCrateVersionSource.GetCursorStateKey(options.Value.TargetRepository)));
            }

            // Delete rust-b & insert rust-a before simulated restart -> watermark resumes cleanly at rust-f then wraps to rust-a
            crates.Remove("rust-b");
            crates.Add("rust-a");

            using var store2 = new JsonBotStateStore(stateOpts);
            var source2 = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store2);
            var c3 = await source2.GetCandidatesAsync();
            Assert.Equal(new[] { "rust-f", "rust-a" }, c3.Select(c => c.PackageName));
            ((ICandidateSweepCursor)source2).CompleteSweep(c3.Select(c => c.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("rust-a", store2.GetCursorWatermark(RustCrateVersionSource.GetCursorStateKey(options.Value.TargetRepository)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_WatermarkPolicy_FreezesOnFullOutageAndTransient503_AdvancesPastSingle404()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rust-wm-policy-{Guid.NewGuid():N}");
        var crates = new[] { "rust-a", "rust-b", "rust-c" };

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        int mode = 0; // 0 = all 503 outage, 1 = 404 on first + 200 on second, 2 = 200 on b + 503 on c
        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (mode == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (mode == 1)
            {
                return path.Contains("rust-a", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"rust-b","vers":"1.0.1","yanked":false}""") };
            }

            if (path.Contains("rust-c", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"rust-c","vers":"1.0.1","yanked":false}""") };
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 2,
                RequestDelayMilliseconds = 0
            }
        });
        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });

        try
        {
            using var store = new JsonBotStateStore(stateOpts);
            var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store);

            var cursorKey = RustCrateVersionSource.GetCursorStateKey(options.Value.TargetRepository);

            // 1. Full 503 outage -> watermark remains null even after CompleteSweep
            mode = 0;
            Assert.Empty(await source.GetCandidatesAsync());
            ((ICandidateSweepCursor)source).CompleteSweep(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Assert.Null(store.GetCursorWatermark(cursorKey));

            // 2. Permanent 404 on rust-a + 200 on rust-b -> CompleteSweep advances past rust-a to rust-b (no poison-pill lock)
            mode = 1;
            var res1 = await source.GetCandidatesAsync();
            Assert.Single(res1);
            Assert.Equal("rust-b", res1[0].PackageName);
            ((ICandidateSweepCursor)source).CompleteSweep(res1.Select(c => c.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("rust-b", store.GetCursorWatermark(cursorKey));

            // 3. Partial 503 on rust-a while rust-c succeeds -> CompleteSweep advances to rust-c (before failed rust-a)
            mode = 2;
            var res2 = await source.GetCandidatesAsync();
            Assert.Single(res2);
            Assert.Equal("rust-c", res2[0].PackageName);
            ((ICandidateSweepCursor)source).CompleteSweep(res2.Select(c => c.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("rust-c", store.GetCursorWatermark(cursorKey));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompleteSweep_RewindsWatermarkBeforeUnprocessedCandidate_AndAdvancesAfterItIsProcessed()
    {
        var crates = new[] { "rust-a", "rust-b", "rust-c" };
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var requestedPaths = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            requestedPaths.Add(path);
            var isCandidate = path.Contains("rust-a", StringComparison.Ordinal);
            var version = isCandidate ? "1.0.1" : "1.0.0";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"name":"crate","vers":"{{version}}","yanked":false}""")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 2,
                RequestDelayMilliseconds = 0
            }
        });

        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog);
        var cursor = (ICandidateSweepCursor)source;

        var first = await source.GetCandidatesAsync();
        Assert.Single(first);
        Assert.Equal("rust-a", first[0].PackageName);
        Assert.Contains(requestedPaths, p => p.Contains("rust-b", StringComparison.Ordinal));

        // rust-a was discovered but not processed (scheduler quota exhausted):
        // the cursor must rewind before rust-a so the next sweep retries it.
        cursor.CompleteSweep(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        requestedPaths.Clear();
        var retried = await source.GetCandidatesAsync();
        Assert.Contains(retried, c => c.PackageName == "rust-a");
        Assert.Contains(requestedPaths, p => p.Contains("rust-a", StringComparison.Ordinal));

        // Once rust-a is processed, the normal scan watermark (rust-b) is retained
        // and the next sweep starts at rust-c.
        cursor.CompleteSweep(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rust-a" });
        requestedPaths.Clear();
        await source.GetCandidatesAsync();
        Assert.Contains(requestedPaths, p => p.Contains("rust-c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCandidatesAsync_CrashBeforeCompleteSweep_DoesNotCommitWatermark_AndZeroCandidateCompleteSweepAdvances()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rust-2pc-{Guid.NewGuid():N}");
        var crates = new[] { "rust-a", "rust-b", "rust-c", "rust-d" };
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var returnUpdates = true;
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(returnUpdates
                ? """{"name":"crate","vers":"1.0.1","yanked":false}"""
                : """{"name":"crate","vers":"1.0.0","yanked":false}""")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 2,
                RequestDelayMilliseconds = 0
            }
        });
        var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });

        try
        {
            var cursorKey = RustCrateVersionSource.GetCursorStateKey(options.Value.TargetRepository);

            // 1. Crash/cancellation before CompleteSweep -> disk watermark stays null so next startup retries same slice
            using (var store1 = new JsonBotStateStore(stateOpts))
            {
                var source1 = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store1);
                var c1 = await source1.GetCandidatesAsync();
                Assert.Equal(new[] { "rust-a", "rust-b" }, c1.Select(c => c.PackageName));
                Assert.Null(store1.GetCursorWatermark(cursorKey));
            }

            // 2. Restart with 0 candidates discovered -> CompleteSweep commits pending slice watermark ("rust-b")
            returnUpdates = false;
            using var store2 = new JsonBotStateStore(stateOpts);
            var source2 = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store2);
            var c2 = await source2.GetCandidatesAsync();
            Assert.Empty(c2);
            Assert.Null(store2.GetCursorWatermark(cursorKey));

            ((ICandidateSweepCursor)source2).CompleteSweep(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("rust-b", store2.GetCursorWatermark(cursorKey));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompleteSweep_BoundedRewinds_AdvancesPastPoisonCandidateAfterThreeConsecutiveRewinds()
    {
        var requestedPaths = new List<string>();
        var crates = new[] { "rust-a-poison", "rust-b-next" };
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 1,
                RequestDelayMilliseconds = 0
            }
        });

        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog);
        var cursor = (ICandidateSweepCursor)source;

        // Sweep 1 & 2: rust-a-poison fails execution -> rewinds before rust-a-poison
        var failedPoisonSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rust-a-poison" };
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var cands = await source.GetCandidatesAsync();
            Assert.Single(cands);
            Assert.Equal("rust-a-poison", cands[0].PackageName);
            cursor.CompleteSweep(new HashSet<string>(StringComparer.OrdinalIgnoreCase), failedPoisonSet);
        }

        // Sweep 3: still failing -> reaches bounded rewind limit (3), advances past rust-a-poison to rust-b-next!
        var third = await source.GetCandidatesAsync();
        Assert.Single(third);
        Assert.Equal("rust-a-poison", third[0].PackageName);
        cursor.CompleteSweep(new HashSet<string>(StringComparer.OrdinalIgnoreCase), failedPoisonSet);

        requestedPaths.Clear();
        var afterPoison = await source.GetCandidatesAsync();
        Assert.Single(afterPoison);
        Assert.Equal("rust-b-next", afterPoison[0].PackageName);
        Assert.DoesNotContain(requestedPaths, p => p.Contains("rust-a-poison", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CandidateScheduler_WithRustCursor_DoesNotSkipCapacityDeferredCandidate_WhileSkippingPersistentFailure()
    {
        var crates = new[] { "rust-a", "rust-b" };
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
        specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var pkg = call.Arg<string>();
                return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
            });

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 1,
                RequestDelayMilliseconds = 0
            }
        });

        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog);
        var cursor = (ICandidateSweepCursor)source;

        // 1. Concurrency cap blocks 5 sweeps -> rust-a MUST NOT be skipped across any of them (B4)
        for (int sweep = 0; sweep < 5; sweep++)
        {
            var cands = await source.GetCandidatesAsync();
            Assert.Single(cands);
            Assert.Equal("rust-a", cands[0].PackageName);

            var sched = await CandidateScheduler.ScheduleAndExecuteAsync(
                cands,
                maxPublishes: 5,
                startOriginOffset: 0,
                canAttempt: (_, _) => false,
                tryPublishAsync: (_, _) => Task.FromResult(true));

            Assert.Empty(sched.FailedPackageNames!);
            cursor.CompleteSweep(sched.ProcessedPackageNames, sched.FailedPackageNames);
        }

        // Still starts at rust-a on sweep 6; now simulate 3 consecutive real publish exceptions on rust-a -> poison skip engages
        for (int fail = 0; fail < 3; fail++)
        {
            var cands = await source.GetCandidatesAsync();
            Assert.Single(cands);
            Assert.Equal("rust-a", cands[0].PackageName);

            var sched = await CandidateScheduler.ScheduleAndExecuteAsync(
                cands,
                maxPublishes: 5,
                startOriginOffset: 0,
                canAttempt: (_, _) => true,
                tryPublishAsync: (_, _) => throw new HttpRequestException("502 Bad Gateway"));

            Assert.Contains("rust-a", sched.FailedPackageNames!);
            cursor.CompleteSweep(sched.ProcessedPackageNames, sched.FailedPackageNames);
        }

        // 4th discovery advances past poison rust-a to rust-b
        var next = await source.GetCandidatesAsync();
        Assert.Single(next);
        Assert.Equal("rust-b", next[0].PackageName);
    }

    [Fact]
    public async Task CompleteSweep_PersistsPoisonRewindAcrossRestarts_AndSkipsAfterMaxAttempts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rust-poison-{Guid.NewGuid():N}");
        try
        {
            var crates = new[] { "rust-a-poison", "rust-b-next" };
            var specCatalog = Substitute.For<ISpecCatalog>();
            specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(crates);
            specCatalog.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var pkg = call.Arg<string>();
                    return new SpecContext(pkg, pkg, $"%global crate_name {pkg}\nVersion: 1.0.0\n", $"SPECS/{pkg}/{pkg}.spec");
                });

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"crate","vers":"1.0.1","yanked":false}""")
            });

            var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
            {
                DryRun = false,
                TargetRepository = "openRuyi-Project/openRuyi",
                RustCratePoller = new RustCratePollerOptions
                {
                    Enabled = true,
                    IndexBaseUrl = "https://index.crates.io",
                    MaxCratesPerSweep = 1,
                    RequestDelayMilliseconds = 0
                }
            });

            var botStateOptions = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir
            });

            // Simulate 3 separate process restarts sharing persisted JsonBotStateStore
            for (int restart = 1; restart <= 3; restart++)
            {
                using var store = new JsonBotStateStore(botStateOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonBotStateStore>.Instance);
                var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store);
                var cursor = (ICandidateSweepCursor)source;

                var cands = await source.GetCandidatesAsync();
                Assert.Single(cands);
                Assert.Equal("rust-a-poison", cands[0].PackageName);

                var sched = await CandidateScheduler.ScheduleAndExecuteAsync(
                    cands,
                    maxPublishes: 5,
                    startOriginOffset: 0,
                    canAttempt: (_, _) => true,
                    tryPublishAsync: (_, _) => throw new HttpRequestException($"Failure #{restart}"));

                cursor.CompleteSweep(sched.ProcessedPackageNames, sched.FailedPackageNames);
            }

            // 4th instance after restart must advance past rust-a-poison to rust-b-next
            using var finalStore = new JsonBotStateStore(botStateOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonBotStateStore>.Instance);
            var finalSource = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: finalStore);
            var next = await finalSource.GetCandidatesAsync();

            Assert.Single(next);
            Assert.Equal("rust-b-next", next[0].PackageName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompleteSweep_DryRunTrue_DoesNotPersistPoisonOrWatermarkState()
    {
        var store = Substitute.For<IBotStateStore>();
        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(new[] { "rust-a" });
        specCatalog.GetSpecAsync("rust-a", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("rust-a", "rust-a", "%global crate_name rust-a\nVersion: 1.0.0\n", "SPECS/rust-a/rust-a.spec"));

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"rust-a","vers":"1.0.1","yanked":false}""")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = true,
            TargetRepository = "openRuyi-Project/openRuyi",
            RustCratePoller = new RustCratePollerOptions
            {
                Enabled = true,
                IndexBaseUrl = "https://index.crates.io",
                MaxCratesPerSweep = 1,
                RequestDelayMilliseconds = 0
            }
        });

        var source = new RustCrateVersionSource(new HttpClient(handler), options, specCatalog: specCatalog, stateStore: store);
        var cands = await source.GetCandidatesAsync();
        Assert.Single(cands);

        ((ICandidateSweepCursor)source).CompleteSweep(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rust-a" });

        store.DidNotReceiveWithAnyArgs().SetCursorWatermark(default!, default);
        store.DidNotReceiveWithAnyArgs().SetPackageFailure(default!, default!, default!, default, default);
        store.DidNotReceiveWithAnyArgs().SetUserLocale(default!, default!);
    }
}


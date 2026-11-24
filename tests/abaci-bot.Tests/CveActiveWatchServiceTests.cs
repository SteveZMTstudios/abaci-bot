using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class CveActiveWatchServiceTests
{
    [Fact]
    public async Task RunScanSweepAsync_CreatesRollupIssue_WhenVulnerabilitiesFound()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var sampleReport = new CveReport("CVE-2020-28493", 7.5, GitHubLabels.Severity2, "OSV", "ReDoS in Jinja2");
        var finding = new PackageVulnerabilityFinding("python-jinja2", "2.4.1", new[] { "CVE-2020-28493" }, new[] { sampleReport });

        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "python-jinja2" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("python-jinja2", "2.4.1")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // Verify issue creation with correct title and labels
        await gitHubMock.Received(1).CreateIssueAsync(
            "openRuyi-Project",
            "openRuyi",
            "[Security] Outstanding CVEs detected in python-jinja2",
            Arg.Is<string>(b => b.Contains("CVE-2020-28493") && b.Contains("ReDoS in Jinja2")),
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityDetected) && labels.Contains(GitHubLabels.Severity2) && labels.Contains(GitHubLabels.StatusStaleExempt)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_AutoClosesIssue_WhenVulnerabilityResolvedInLaterScan()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // An existing open issue for python-jinja2
        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 101);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in python-jinja2");

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(f => f.Contains(GitHubLabels.VulnerabilityDetected)), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        // Scan returns 0 findings now! (Vulnerability has been patched, but package was successfully scanned)
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "python-jinja2" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("python-jinja2", "2.11.3")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // Verify issue closure and label transitions
        await gitHubMock.Received(1).RemoveLabelAsync("openRuyi-Project", "openRuyi", 101, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(l => l.Contains(GitHubLabels.VulnerabilityResolved)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("CVE-2024-1234", "https://nvd.nist.gov/vuln/detail/CVE-2024-1234")]
    [InlineData("GHSA-xxxx-yyyy-zzzz", "https://github.com/advisories/GHSA-xxxx-yyyy-zzzz")]
    [InlineData("CGA-1234-5678", "https://osv.dev/vulnerability/CGA-1234-5678")]
    public void FormatVulnerabilityUrl_RoutesCorrectly(string id, string expectedUrl)
    {
        var url = CveActiveWatchService.FormatVulnerabilityUrl(id);
        Assert.Equal(expectedUrl, url);
    }

    [Fact]
    public void GenerateIssueBody_FormatsAdvisoryLinksProperly()
    {
        var cveReport = new CveReport("CVE-2024-1234", 9.8, GitHubLabels.Severity1, "OSV", "RCE in curl");
        var ghsaReport = new CveReport("GHSA-xxxx-yyyy-zzzz", 7.5, GitHubLabels.Severity2, "OSV", "Prototype pollution");
        var finding = new PackageVulnerabilityFinding("curl", "8.2.1", new[] { "CVE-2024-1234", "GHSA-xxxx-yyyy-zzzz" }, new[] { cveReport, ghsaReport });

        var body = CveActiveWatchService.GenerateIssueBody(finding);

        Assert.Contains("[CVE-2024-1234](https://nvd.nist.gov/vuln/detail/CVE-2024-1234)", body);
        Assert.Contains("[GHSA-xxxx-yyyy-zzzz](https://github.com/advisories/GHSA-xxxx-yyyy-zzzz)", body);
        Assert.DoesNotContain("nvd.nist.gov/vuln/detail/GHSA", body);
    }

    [Fact]
    public void CalculateMaxSeverity_ReturnsNull_WhenNoReportsHaveSeverity()
    {
        var emptyReports = Array.Empty<CveReport>();
        Assert.Null(CveActiveWatchService.CalculateMaxSeverity(emptyReports));

        var unratedReports = new[]
        {
            new CveReport("CVE-2024-9999", null, null, "OSV", "Unrated vuln")
        };
        Assert.Null(CveActiveWatchService.CalculateMaxSeverity(unratedReports));
    }

    [Fact]
    public async Task RunScanSweepAsync_ReconcilesSeverityLabels_RemovingObsoleteLabelsOnUpdate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        // 1. Existing issue carrying old Severity: 3 label
        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 202);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in python-jinja2");
        var oldSeverityLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(oldSeverityLabel, GitHubLabels.Severity3);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(existingIssue, new[] { oldSeverityLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(f => f.Contains(GitHubLabels.VulnerabilityDetected)), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        // 2. New scan detects upgraded Severity: 1
        var report = new CveReport("CVE-2024-9999", 9.8, GitHubLabels.Severity1, "OSV", "Critical RCE");
        var finding = new PackageVulnerabilityFinding("python-jinja2", "2.4.1", new[] { "CVE-2024-9999" }, new[] { report });

        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "python-jinja2" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("python-jinja2", "2.4.1")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // 3. Verify Severity: 3 was removed and Severity: 1 was added
        await gitHubMock.Received(1).RemoveLabelAsync("openRuyi-Project", "openRuyi", 202, GitHubLabels.Severity3, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 202, Arg.Is<string[]>(l => l.Contains(GitHubLabels.Severity1)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_DoesNotAddSeverityLabel_WhenFindingIsUnrated()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var report = new CveReport("CVE-2024-0000", null, null, "OSV", "Unrated");
        var finding = new PackageVulnerabilityFinding("curl", "8.2.1", new[] { "CVE-2024-0000" }, new[] { report });

        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "curl" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("curl", "8.2.1")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        // Verify issue was created with VulnerabilityDetected and StatusStaleExempt, but NO Severity label
        await gitHubMock.Received(1).CreateIssueAsync(
            "openRuyi-Project", "openRuyi",
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityDetected) &&
                                       labels.Contains(GitHubLabels.StatusStaleExempt) &&
                                       !labels.Any(l => l.StartsWith("Severity:"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_RotatesPackageCatalog_AcrossConsecutiveSweeps()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var capturedBatches = new List<List<string>>();

        scannerMock.ScanPackagesAsync(Arg.Do<IEnumerable<(string Name, string Version)>>(pkgs =>
        {
            capturedBatches.Add(pkgs.Select(p => p.Name).ToList());
        }), Arg.Any<CancellationToken>())
        .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string>()));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            PackagesPerSweep = 2
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("pkg1", "1.0"),
            ("pkg2", "1.0"),
            ("pkg3", "1.0"),
            ("pkg4", "1.0")
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);

        // First sweep: should take pkg1, pkg2
        await service.RunScanSweepAsync();
        Assert.Single(capturedBatches);
        Assert.Equal(new[] { "pkg1", "pkg2" }, capturedBatches[0]);

        // Second sweep: should rotate and take pkg3, pkg4
        await service.RunScanSweepAsync();
        Assert.Equal(2, capturedBatches.Count);
        Assert.Equal(new[] { "pkg3", "pkg4" }, capturedBatches[1]);

        // Third sweep: should wrap around and take pkg1, pkg2
        await service.RunScanSweepAsync();
        Assert.Equal(3, capturedBatches.Count);
        Assert.Equal(new[] { "pkg1", "pkg2" }, capturedBatches[2]);
    }

    [Fact]
    public async Task StartAsync_AndStopAsync_Lifecycle()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = false,
            TargetRepository = "owner/repo",
            CheckIntervalHours = 1
        });

        var service = new CveActiveWatchService(
            Substitute.For<IGitHubService>(),
            Substitute.For<IPackageVulnerabilityScanner>(),
            options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunScanSweepAsync_LoadsCatalogViaSpecCatalog_WhenCatalogProviderNull()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetAllPackageNamesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { "pkgA", "pkgB" });
        specCatalogMock.GetSpecAsync("pkgA", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkgA", "pkgA", "Name: pkgA\nVersion: 1.2.3\n", "SPECS/pkgA.spec"));
        specCatalogMock.GetSpecAsync("pkgB", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("pkgB", "pkgB", "No version line\n", "SPECS/pkgB.spec"));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var captured = new List<(string Name, string Version)>();
        scannerMock.ScanPackagesAsync(Arg.Do<IEnumerable<(string Name, string Version)>>(p => captured.AddRange(p)), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string>()));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            PackagesPerSweep = 0
        });

        // Pass specCatalog and null catalogProvider
        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, specCatalog: specCatalogMock);
        await service.RunScanSweepAsync();

        Assert.Single(captured);
        Assert.Equal("pkgA", captured[0].Name);
        Assert.Equal("1.2.3", captured[0].Version);
    }

    [Theory]
    [InlineData("invalid_repo")]
    [InlineData("")]
    public async Task RunScanSweepAsync_HandlesInvalidTargetRepo(string targetRepo)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            TargetRepository = targetRepo
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        var service = new CveActiveWatchService(gitHubMock, Substitute.For<IPackageVulnerabilityScanner>(), options);

        await service.RunScanSweepAsync();
        await gitHubMock.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_DefersSweep_WhenRateLimitLow()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(100, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options);
        await service.RunScanSweepAsync();

        await scannerMock.DidNotReceive().ScanPackagesAsync(
            Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_ReturnsEarly_WhenPackageCatalogEmpty()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            TargetRepository = "owner/repo"
        });

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(Array.Empty<(string, string)>());
        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        await scannerMock.DidNotReceive().ScanPackagesAsync(
            Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_UpdatesExistingVulnerabilityIssue_AndReconcilesSeverityLabels()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 88);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in curl");
        var oldSeverityLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(oldSeverityLabel, GitHubLabels.Severity4);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(existingIssue, new List<Label> { oldSeverityLabel });

        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        var report = new CveReport("CVE-2024-9999", 9.8, GitHubLabels.Severity1, "OSV", "Critical RCE");
        var finding = new PackageVulnerabilityFinding("curl", "8.2.1", new[] { "CVE-2024-9999" }, new[] { report });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "curl" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.2.1") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        await gitHubMock.Received(1).UpdateIssueAsync("owner", "repo", 88, "[Security] Outstanding CVEs detected in curl", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 88, GitHubLabels.Severity4, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 88, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.Severity1)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_AutoClosesResolvedVulnerabilityIssue()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var resolvedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(resolvedIssue, 99);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(resolvedIssue, "[Security] Outstanding CVEs detected in curl");
        var vulnLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(vulnLabel, GitHubLabels.VulnerabilityDetected);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(resolvedIssue, new List<Label> { vulnLabel });

        // Search returns this issue for reconciliation
        gitHubMock.SearchStaleCandidatesAsync("owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(q => q.Contains("is:issue is:open")), Arg.Any<CancellationToken>())
            .Returns(new[] { resolvedIssue });

        // Scanner reports curl was scanned successfully, with 0 findings (resolved!)
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "curl" }));

        var fakeCatalog = (CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.2.2") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: fakeCatalog);
        await service.RunScanSweepAsync();

        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 99, Arg.Is<string>(c => c.Contains("resolved")), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 99, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 99, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.VulnerabilityResolved)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_WithSpecCatalogAndPackagesPerSweep_SlicesBeforeFetchingSpecs()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var allNames = Enumerable.Range(0, 50).Select(i => $"pkg-{i:D2}").ToList();
        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(allNames);
        specCatalogMock.GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var name = call.Arg<string>();
                return new SpecContext(name, name, $"Name: {name}\nVersion: 1.0.0\n", $"SPECS/{name}/{name}.spec");
            });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var scannedBatches = new List<List<string>>();
        scannerMock.ScanPackagesAsync(Arg.Do<IEnumerable<(string Name, string Version)>>(pkgs =>
                scannedBatches.Add(pkgs.Select(p => p.Name).ToList())), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string>()));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "owner/repo",
            PackagesPerSweep = 5
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, specCatalog: specCatalogMock);

        await service.RunScanSweepAsync();
        await specCatalogMock.Received(5).GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(new[] { "pkg-00", "pkg-01", "pkg-02", "pkg-03", "pkg-04" }, scannedBatches[0]);

        await service.RunScanSweepAsync();
        await specCatalogMock.Received(10).GetSpecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(new[] { "pkg-05", "pkg-06", "pkg-07", "pkg-08", "pkg-09" }, scannedBatches[1]);
    }

    [Fact]
    public async Task RunScanSweepAsync_WithManyFindings_UsesSingleSearchApiCall()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 501);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in vuln-00");
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(existingIssue, new List<Label>());

        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existingIssue });

        var findings = Enumerable.Range(0, 40)
            .Select(i => new PackageVulnerabilityFinding(
                $"vuln-{i:D2}",
                "1.0.0",
                new[] { $"CVE-2026-{1000 + i}" },
                new[] { new CveReport($"CVE-2026-{1000 + i}", 8.5, GitHubLabels.Severity2, "OSV", "Sample CVE") }))
            .ToList();

        var scannedSet = findings.Select(f => f.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(findings, scannedSet));

        var catalogProvider = (CancellationToken _) => Task.FromResult<IReadOnlyList<(string, string)>>(
            findings.Select(f => (f.PackageName, f.Version)).ToList());

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: catalogProvider);
        await service.RunScanSweepAsync();

        // Only 1 index search for the entire sweep regardless of finding count (no 40+ per-finding search calls)
        await gitHubMock.Received(1).SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Is<string>(q => q.Contains(GitHubLabels.VulnerabilityDetected)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).UpdateIssueAsync("owner", "repo", 501, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.Received(39).CreateIssueAsync("owner", "repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_DefersSweep_WhenSearchRateLimitLow()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 2, DateTimeOffset.UtcNow.AddMinutes(1)));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        var catalogProvider = (CancellationToken _) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.0.0") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: catalogProvider);
        await service.RunScanSweepAsync();

        await scannerMock.DidNotReceive().ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_FailsClosed_WhenOpenIssueIndexSearchThrows()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
                "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Issue>>>(_ => throw new HttpRequestException("Search rate limit 403"));

        var finding = new PackageVulnerabilityFinding(
            "curl", "8.0.0", new[] { "CVE-2026-1000" },
            new[] { new CveReport("CVE-2026-1000", 7.5, GitHubLabels.Severity2, "OSV", "Bug") });
        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "curl" }));

        var catalogProvider = (CancellationToken _) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("curl", "8.0.0") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(gitHubMock, scannerMock, options, catalogProvider: catalogProvider);
        await service.RunScanSweepAsync();

        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().CloseIssueAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RunScanSweepAsync_WhenOpenIssueIndexIsTruncated_UpdatesIndexedIssuesAndDefersNewCreation()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var pagedIssues = new List<Issue>(500);
        for (int i = 0; i < 500; i++)
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, 1000 + i);
            var pkgName = i == 0 ? "indexed-pkg" : $"other-open-pkg-{i:D3}";
            typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(issue, $"[Security] Outstanding CVEs detected in {pkgName}");
            typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(issue, new List<Label>());
            pagedIssues.Add(issue);
        }

        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        pagedSearchMock.SearchOpenIssuesByLabelAsync(
                "owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(pagedIssues, TotalCount: 600, IsTruncated: true));

        var findings = new[]
        {
            new PackageVulnerabilityFinding(
                "indexed-pkg",
                "1.0.0",
                new[] { "CVE-2026-2001" },
                new[] { new CveReport("CVE-2026-2001", 8.0, GitHubLabels.Severity2, "OSV", "Indexed vuln") }),
            new PackageVulnerabilityFinding(
                "beyond-500-pkg",
                "2.0.0",
                new[] { "CVE-2026-2002" },
                new[] { new CveReport("CVE-2026-2002", 7.5, GitHubLabels.Severity2, "OSV", "Beyond truncation vuln") })
        };

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(findings, new HashSet<string> { "indexed-pkg", "beyond-500-pkg" }));

        var catalogProvider = (CancellationToken _) => Task.FromResult<IReadOnlyList<(string, string)>>(new[]
        {
            ("indexed-pkg", "1.0.0"),
            ("beyond-500-pkg", "2.0.0")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(
            gitHubMock, scannerMock, options, catalogProvider: catalogProvider, pagedIssueSearch: pagedSearchMock);

        await service.RunScanSweepAsync();

        // Indexed issue is updated normally; unindexed issue deferred fail-closed (zero CreateIssueAsync calls)
        await gitHubMock.Received(1).UpdateIssueAsync("owner", "repo", 1000, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task RunScanSweepAsync_WithCustomIssueLabel_RemovesConfiguredLabelOnResolution()
    {
        const string customLabel = "Security: Custom-CVE";
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var openIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(openIssue, 333);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(openIssue, "[Security] Outstanding CVEs detected in openssl");

        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", customLabel, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { openIssue }, TotalCount: 1, IsTruncated: false));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "openssl" }));

        var catalogProvider = (CancellationToken _) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("openssl", "3.2.0") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            IssueLabel = customLabel
        });

        var service = new CveActiveWatchService(
            gitHubMock, scannerMock, options, catalogProvider: catalogProvider, pagedIssueSearch: pagedSearchMock);

        await service.RunScanSweepAsync();

        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 333, customLabel, Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().RemoveLabelAsync("owner", "repo", 333, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).AddLabelsAsync(
            "owner", "repo", 333, Arg.Is<string[]>(l => l.Contains(GitHubLabels.VulnerabilityResolved)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 333, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_MatchesRenamedIssueViaBodyMarker_WithoutDuplicate_AndClosesOnResolution()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var priorFinding = new PackageVulnerabilityFinding(
            "python-jinja2",
            "2.4.0",
            new[] { "CVE-2020-00001" },
            new[] { new CveReport("CVE-2020-00001", 5.0, GitHubLabels.Severity3, "OSV", "Prior advisory") });
        var priorBody = CveActiveWatchService.GenerateIssueBody(priorFinding);
        Assert.Contains("<!-- abaci-cve-package: python-jinja2 -->", priorBody, StringComparison.Ordinal);

        var finding = new PackageVulnerabilityFinding(
            "python-jinja2",
            "2.4.1",
            new[] { "CVE-2020-28493" },
            new[] { new CveReport("CVE-2020-28493", 7.5, GitHubLabels.Severity2, "OSV", "ReDoS") });

        // Maintainer renamed the issue title so it no longer matches exact default title/prefix alone
        var renamedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(renamedIssue, 410);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(renamedIssue, "[Security] jinja2 upstream tracking (urgent)");
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(renamedIssue, priorBody);

        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { renamedIssue }, 1, false));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(
                new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "python-jinja2" }),
                new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "python-jinja2" }));

        var catalogProvider = (CancellationToken _) => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("python-jinja2", "2.4.1") });
        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(
            gitHubMock, scannerMock, options, catalogProvider: catalogProvider, pagedIssueSearch: pagedSearchMock);

        // Sweep 1: updates renamed issue in-place (keeping maintainer title) and does NOT create duplicate
        await service.RunScanSweepAsync();
        await gitHubMock.Received(1).UpdateIssueAsync(
            "owner", "repo", 410, "[Security] jinja2 upstream tracking (urgent)", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);

        // Sweep 2: vulnerability resolved -> closes renamed issue cleanly via unified identity
        await service.RunScanSweepAsync();
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 410, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TryExtractScanTargetFromSpec_ExtractsCrateAndPypiMetadata()
    {
        var rustSpec = """
            %global crate_name rand_core
            %global full_version 0.10.0+custom
            Version: 0.10.0
            """;
        var rustTarget = CveActiveWatchService.TryExtractScanTargetFromSpec("rust-rand-core-0.10", rustSpec);
        Assert.NotNull(rustTarget);
        Assert.Equal("crates.io", rustTarget.Ecosystem);
        Assert.Equal("rand_core", rustTarget.UpstreamName);
        Assert.Equal("0.10.0+custom", rustTarget.Version);

        var pythonSpec = """
            %global srcname requests
            Version: 2.32.0
            """;
        var pythonTarget = CveActiveWatchService.TryExtractScanTargetFromSpec("python3-requests", pythonSpec);
        Assert.NotNull(pythonTarget);
        Assert.Equal("PyPI", pythonTarget.Ecosystem);
        Assert.Equal("requests", pythonTarget.UpstreamName);

        var rpmTarget = CveActiveWatchService.TryExtractScanTargetFromSpec("curl", "Name: curl\nVersion: %{version}\n");
        Assert.NotNull(rpmTarget);
        Assert.Null(rpmTarget.Ecosystem);
        Assert.Equal("%{version}", rpmTarget.Version);
    }

    [Fact]
    public async Task RunScanSweepAsync_DefersFailClosed_WhenRateLimitSnapshotIsNullOrUnknown()
    {
        var snapshots = new GitHubRateLimitSnapshot?[]
        {
            null,
            new GitHubRateLimitSnapshot(null, null, 30, null),
            new GitHubRateLimitSnapshot(5000, null, null, null)
        };

        foreach (var snapshot in snapshots)
        {
            var gitHubMock = Substitute.For<IGitHubService>();
            gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>()).Returns(snapshot!);
            var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
            var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
            {
                Enabled = true,
                TargetRepository = "owner/repo"
            });

            var service = new CveActiveWatchService(
                gitHubMock,
                scannerMock,
                options,
                catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("python-jinja2", "2.4.1") }));

            await service.RunScanSweepAsync();

            await scannerMock.DidNotReceiveWithAnyArgs().ScanPackagesAsync(default!, default);
        }
    }

    [Fact]
    public async Task RunScanSweepAsync_DuplicatePackageFindings_DoesNotThrowAndDeduplicates()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        gitHubMock.SearchStaleCandidatesAsync(
            "owner", "repo", Arg.Any<DateTimeOffset>(), false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Issue>());

        var finding1 = new PackageVulnerabilityFinding(
            "python-jinja2", "2.4.1", new[] { "CVE-2020-28493" },
            new[] { new CveReport("CVE-2020-28493", 7.5, GitHubLabels.Severity2, "OSV", "Issue 1") });
        var finding2 = new PackageVulnerabilityFinding(
            "python-jinja2", "2.4.1", new[] { "CVE-2024-56326" },
            new[] { new CveReport("CVE-2024-56326", 5.5, GitHubLabels.Severity3, "OSV", "Issue 2") });

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(
                new[] { finding1, finding2 },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "python-jinja2" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(
            gitHubMock,
            scannerMock,
            options,
            catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("python-jinja2", "2.4.1") }));

        var ex = await Record.ExceptionAsync(() => service.RunScanSweepAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task RunScanSweepAsync_LegacyOpenNativeIssue_PostsIdempotentUnverifiedNoticeOnce()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var legacyIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(legacyIssue, 175);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(legacyIssue, "[Security] Outstanding CVEs detected in abseil-cpp");

        var existingNoticeComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(
            existingNoticeComment,
            $"migration note\n{CveActiveWatchService.UnverifiedNoticeMarker}");

        gitHubMock.GetIssueCommentsAsync("owner", "repo", 175, Arg.Any<CancellationToken>())
            .Returns(
                Array.Empty<IssueComment>(),
                new[] { existingNoticeComment });

        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { legacyIssue }, 1, false));

        var targetedScannerMock = Substitute.For<ITargetedPackageVulnerabilityScanner>();
        targetedScannerMock.ScanTargetsAsync(Arg.Any<IEnumerable<PackageScanTarget>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(
                Array.Empty<PackageVulnerabilityFinding>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SkippedUnverifiedCount: 1,
                SkippedInvalidVersionCount: 0,
                UnverifiedNativePackages: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abseil-cpp" }));

        var specCatalog = Substitute.For<ISpecCatalog>();
        specCatalog.GetAllPackageNamesAsync(Arg.Any<CancellationToken>()).Returns(new[] { "abseil-cpp" });
        specCatalog.GetSpecAsync("abseil-cpp", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("abseil-cpp", "abseil-cpp", "Name: abseil-cpp\nVersion: 20240722.0\n", "SPECS/abseil-cpp/abseil-cpp.spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo",
            PackagesPerSweep = 1,
            IncludeUnverifiedNativePackages = false
        });

        var service = new CveActiveWatchService(
            gitHubMock, targetedScannerMock, options, specCatalog: specCatalog, pagedIssueSearch: pagedSearchMock);

        // 1st sweep: posts one-time unverified notice without closing issue
        await service.RunScanSweepAsync();
        await gitHubMock.Received(1).CreateCommentAsync(
            "owner", "repo", 175, Arg.Is<string>(b => b.Contains(CveActiveWatchService.UnverifiedNoticeMarker)), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CloseIssueAsync("owner", "repo", 175, Arg.Any<CancellationToken>());

        // 2nd sweep: marker comment already present -> does not duplicate notice
        await service.RunScanSweepAsync();
        await gitHubMock.Received(1).CreateCommentAsync(
            "owner", "repo", 175, Arg.Is<string>(b => b.Contains(CveActiveWatchService.UnverifiedNoticeMarker)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GenerateIssueBody_UnverifiedEcosystem_IncludesBestEffortWarningBanner()
    {
        var finding = new PackageVulnerabilityFinding(
            "acl",
            "2.3.2",
            new[] { "CVE-2025-1111" },
            new[] { new CveReport("CVE-2025-1111", 5.5, GitHubLabels.Severity3, "OSV", "Best effort entry") },
            TotalVulnerabilityCount: 1,
            IsUnverifiedEcosystem: true);

        var body = CveActiveWatchService.GenerateIssueBody(finding);

        Assert.Contains("Best-Effort / Unverified Ecosystem", body, StringComparison.Ordinal);
        Assert.Contains("<!-- abaci-cve-package: acl -->", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunScanSweepAsync_UnverifiedNativeEmptyResult_DoesNotAutoCloseIssue_WhileAuthoritativeCloses()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        static Issue MakeOpenIssue(int number, string pkg)
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
            typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(issue, $"[Security] Outstanding CVEs detected in {pkg}");
            typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(issue, $"<!-- abaci-cve-package: {pkg} -->");
            return issue;
        }

        var zfsIssue = MakeOpenIssue(301, "zfs");
        var serdeIssue = MakeOpenIssue(302, "rust-serde");

        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { zfsIssue, serdeIssue }, 2, false));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(
                Array.Empty<PackageVulnerabilityFinding>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rust-serde" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            IncludeUnverifiedNativePackages = true,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(
            gitHubMock,
            scannerMock,
            options,
            catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("zfs", "2.4.4"), ("rust-serde", "1.0.219") }),
            pagedIssueSearch: pagedSearchMock);

        await service.RunScanSweepAsync();

        await gitHubMock.DidNotReceive().CloseIssueAsync("owner", "repo", 301, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CloseIssueAsync("owner", "repo", 302, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunScanSweepAsync_UnchangedExistingIssue_SkipsUpdateAndAddLabelsWrites()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var finding = new PackageVulnerabilityFinding(
            "python-jinja2",
            "2.4.1",
            new[] { "CVE-2020-28493" },
            new[] { new CveReport("CVE-2020-28493", 7.5, GitHubLabels.Severity2, "OSV", "ReDoS") });
        var expectedBody =
            "## Vulnerability Assessment for `python-jinja2` (2.4.1)\r\n\r\n" +
            "The following unpatched CVE vulnerabilities were actively detected in `python-jinja2`:\r\n\r\n" +
            "| CVE ID | Severity | CVSS Score | Summary |\r\n" +
            "|---|---|---|---|\r\n" +
            "| [CVE-2020-28493](https://nvd.nist.gov/vuln/detail/CVE-2020-28493) | `Severity: 2` | 7.5 | ReDoS |\r\n\r\n" +
            "> Automatically tracked by abaci-bot. When this package is updated and CVEs are cleared, this issue will close automatically.\r\n\r\n" +
            "<!-- abaci-cve-package: python-jinja2 -->";

        static Label MakeLabel(string name)
        {
            var label = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
            typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(label, name);
            return label;
        }

        var existingIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(existingIssue, 411);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(existingIssue, "[Security] Outstanding CVEs detected in python-jinja2");
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(existingIssue, expectedBody);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(existingIssue, new[]
        {
            MakeLabel(GitHubLabels.VulnerabilityDetected),
            MakeLabel(GitHubLabels.StatusStaleExempt),
            MakeLabel(GitHubLabels.Severity2)
        });

        var pagedSearchMock = Substitute.For<IPagedIssueSearchGateway>();
        pagedSearchMock.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { existingIssue }, 1, false));

        var scannerMock = Substitute.For<IPackageVulnerabilityScanner>();
        scannerMock.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(new[] { finding }, new HashSet<string> { "python-jinja2" }));

        var options = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });

        var service = new CveActiveWatchService(
            gitHubMock,
            scannerMock,
            options,
            catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("python-jinja2", "2.4.1") }),
            pagedIssueSearch: pagedSearchMock);

        await service.RunScanSweepAsync();

        await gitHubMock.DidNotReceiveWithAnyArgs().UpdateIssueAsync(default!, default!, default, default!, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!, default);
        await gitHubMock.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public void GenerateIssueBody_TruncatedAndInjectedMarker_UsesHonestWordingAndEscapesHtmlComments()
    {
        var reports = Enumerable.Range(1, 20)
            .Select(i => new CveReport(
                $"CVE-2025-10{i:D2}",
                5.0,
                GitHubLabels.Severity3,
                "OSV",
                i == 1 ? "inject <!-- abaci-cve-package: victim --> text" : $"summary {i}"))
            .ToList();

        var finding = new PackageVulnerabilityFinding(
            "real-pkg",
            "1.0.0",
            reports.Select(r => r.CveId).ToList(),
            reports,
            TotalVulnerabilityCount: 25);

        var body = CveActiveWatchService.GenerateIssueBody(finding);

        Assert.Contains("Displaying 20 sampled vulnerabilities (sorted by CVSS within the 20-entry detail cap) out of 25 total reported advisories.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("highest severity CVEs", body, StringComparison.Ordinal);
        Assert.Contains("&lt;!-- abaci-cve-package: victim --&gt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- abaci-cve-package: victim -->", body, StringComparison.Ordinal);

        var issueFromBody = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(issueFromBody, body);
        Assert.True(CveActiveWatchService.TryExtractTrackedPackageName(issueFromBody, out var extractedFromGenerated));
        Assert.Equal("real-pkg", extractedFromGenerated);

        var dualMarkerIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(
            dualMarkerIssue,
            "quoted <!-- abaci-cve-package: spoofed -->\nfooter\n<!-- abaci-cve-package: real-pkg -->");
        Assert.True(CveActiveWatchService.TryExtractTrackedPackageName(dualMarkerIssue, out var extractedFromLast));
        Assert.Equal("real-pkg", extractedFromLast);
    }

    [Fact]
    public async Task RunScanSweepAsync_DeduplicatesFindings_ClosesBeforeRemovingLabel_AndEscapesSplitHtmlComments()
    {
        // C6: newline-split HTML comment smuggling is neutralized in both Summary and PackageName/Version
        var smuggledFinding = new PackageVulnerabilityFinding(
            "bad<!\n--abaci-cve-package: spoofed-->pkg",
            "1.0\r\n<!-- -->",
            new[] { "CVE-2026-0001" },
            new[] { new CveReport("CVE-2026-0001", 7.5, GitHubLabels.Severity2, "OSV", "split <!\n-- smuggle --\n>") });
        var smuggledBody = CveActiveWatchService.GenerateIssueBody(smuggledFinding);
        Assert.Contains("&lt;!--", smuggledBody, StringComparison.Ordinal);
        Assert.DoesNotContain("--&gt;\npkg", smuggledBody, StringComparison.Ordinal);

        // C3: two findings for same package create ONLY one issue even if openIssuesByPackage started empty
        var gh1 = Substitute.For<IGitHubService>();
        gh1.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        var createdIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(createdIssue, 701);
        gh1.CreateIssueAsync("owner", "repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(createdIssue);

        var searchMock1 = Substitute.For<IPagedIssueSearchGateway>();
        searchMock1.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(Array.Empty<Issue>(), 0, false));

        var scanner1 = Substitute.For<IPackageVulnerabilityScanner>();
        scanner1.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(
                new[]
                {
                    new PackageVulnerabilityFinding("rust-dup", "1.0.0", new[] { "CVE-2026-1001" }, new[] { new CveReport("CVE-2026-1001", 7.5, GitHubLabels.Severity2, "OSV") }),
                    new PackageVulnerabilityFinding("rust-dup", "2.0.0", new[] { "CVE-2026-1002" }, new[] { new CveReport("CVE-2026-1002", 9.8, GitHubLabels.Severity1, "OSV") })
                },
                new HashSet<string>()));

        var opts = Microsoft.Extensions.Options.Options.Create(new CveActiveWatchOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "owner/repo"
        });
        var svc1 = new CveActiveWatchService(
            gh1, scanner1, opts,
            catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("rust-dup", "2.0.0") }),
            pagedIssueSearch: searchMock1);
        await svc1.RunScanSweepAsync();
        await gh1.Received(1).CreateIssueAsync(
            "owner",
            "repo",
            Arg.Any<string>(),
            Arg.Is<string>(b =>
                b.Contains("(2.0.0)", StringComparison.Ordinal) &&
                b.Contains("CVE-2026-1001", StringComparison.Ordinal) &&
                b.Contains("CVE-2026-1002", StringComparison.Ordinal)),
            Arg.Is<string[]>(l => l.Contains(GitHubLabels.Severity1) && !l.Contains(GitHubLabels.Severity2)),
            Arg.Any<CancellationToken>());

        // C4/C1/C2: if CloseIssueAsync fails on #702, NO comment or RemoveLabel executes, and #703 still closes!
        var ghCloseFail = Substitute.For<IGitHubService>();
        ghCloseFail.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));
        var openIssue1 = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(openIssue1, 702);
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(openIssue1, "<!-- abaci-cve-package: rust-fixed -->");
        var openIssue2 = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(openIssue2, 703);
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(openIssue2, "<!-- abaci-cve-package: rust-other -->");

        var searchMock2 = Substitute.For<IPagedIssueSearchGateway>();
        searchMock2.SearchOpenIssuesByLabelAsync("owner", "repo", GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new[] { openIssue1, openIssue2 }, 2, false));
        ghCloseFail.CloseIssueAsync("owner", "repo", 702, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("502 Bad Gateway"));

        var scanner2 = Substitute.For<IPackageVulnerabilityScanner>();
        scanner2.ScanPackagesAsync(Arg.Any<IEnumerable<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new PackageVulnerabilityScanResult(Array.Empty<PackageVulnerabilityFinding>(), new HashSet<string> { "rust-fixed", "rust-other" }));

        var svc2 = new CveActiveWatchService(
            ghCloseFail, scanner2, opts,
            catalogProvider: _ => Task.FromResult<IReadOnlyList<(string, string)>>(new[] { ("rust-fixed", "1.0.1"), ("rust-other", "1.0.1") }),
            pagedIssueSearch: searchMock2);
        await svc2.RunScanSweepAsync();

        await ghCloseFail.Received(1).CloseIssueAsync("owner", "repo", 702, Arg.Any<CancellationToken>());
        await ghCloseFail.DidNotReceive().CreateCommentAsync("owner", "repo", 702, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ghCloseFail.DidNotReceive().RemoveLabelAsync("owner", "repo", 702, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());

        await ghCloseFail.Received(1).AddLabelsAsync("owner", "repo", 703, Arg.Is<string[]>(l => l.Contains(GitHubLabels.VulnerabilityResolved)), Arg.Any<CancellationToken>());
        await ghCloseFail.Received(1).CloseIssueAsync("owner", "repo", 703, Arg.Any<CancellationToken>());
        await ghCloseFail.Received(1).CreateCommentAsync("owner", "repo", 703, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ghCloseFail.Received(1).RemoveLabelAsync("owner", "repo", 703, GitHubLabels.VulnerabilityDetected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GenerateIssueBody_HeuristicUpstreamAndBracketCveId_RenderBannerAndEscapeMarkdownLinks()
    {
        var finding = new PackageVulnerabilityFinding(
            "python-systemd",
            "235",
            new[] { "ADV-[bad]|(x)" },
            new[] { new CveReport("ADV-[bad]|(x)", 6.1, GitHubLabels.Severity3, "OSV", "Summary") },
            TotalVulnerabilityCount: 1,
            IsUnverifiedEcosystem: false,
            IsHeuristicUpstream: true);

        var body = CveActiveWatchService.GenerateIssueBody(finding);

        Assert.Contains("Heuristic Upstream Name Match", body, StringComparison.Ordinal);
        Assert.Contains("[ADV-\\[bad\\]\\|(x)](https://osv.dev/vulnerability/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("will close automatically", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will **not** close automatically", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIssueBody_PipeMarkerUrlEscapingAndUnverifiedFooter_PreservesMarkerAndEscapesUrls()
    {
        var unverifiedFinding = new PackageVulnerabilityFinding(
            "pkg|pipe",
            "1.0",
            new[] { "CVE-2026 9999", "GHSA-abc)123" },
            new[]
            {
                new CveReport("CVE-2026 9999", 7.0, GitHubLabels.Severity2, "NVD", "Test CVE"),
                new CveReport("GHSA-abc)123", 5.0, GitHubLabels.Severity3, "OSV", "Test GHSA")
            },
            TotalVulnerabilityCount: 2,
            IsUnverifiedEcosystem: true,
            IsHeuristicUpstream: false);

        var body = CveActiveWatchService.GenerateIssueBody(unverifiedFinding);

        Assert.Contains("<!-- abaci-cve-package: pkg|pipe -->", body, StringComparison.Ordinal);
        Assert.Contains("https://nvd.nist.gov/vuln/detail/CVE-2026%209999", body, StringComparison.Ordinal);
        Assert.Contains("https://github.com/advisories/GHSA-abc%29123", body, StringComparison.Ordinal);
        Assert.DoesNotContain("will close automatically", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will **not** close automatically", body, StringComparison.Ordinal);

        var fakeIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Body))?.SetValue(fakeIssue, body);
        Assert.True(CveActiveWatchService.TryExtractTrackedPackageName(fakeIssue, out var extracted));
        Assert.Equal("pkg|pipe", extracted);
    }
}

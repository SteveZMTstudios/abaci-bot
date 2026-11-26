using System.IO.Compression;
using System.Net;
using System.Text;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class RemoteAssetBackfillerTests
{
    private static IGitHubService CreateGitHubMock()
    {
        var gh = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var cas = (IOptimisticFileContentGateway)gh;
        cas.GetFileContentWithShaAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var content = await gh.GetFileContentAsync(
                    ci.ArgAt<string>(0),
                    ci.ArgAt<string>(1),
                    ci.ArgAt<string>(2),
                    ci.ArgAt<string>(3),
                    ci.ArgAt<CancellationToken>(4));
                return string.IsNullOrEmpty(content) ? null : new RepositoryFileContent(content, "mock-blob-sha");
            });
        cas.UpdateFileContentIfUnchangedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await gh.UpdateFileContentAsync(
                ci.ArgAt<string>(0),
                ci.ArgAt<string>(1),
                ci.ArgAt<string>(2),
                ci.ArgAt<string>(3),
                ci.ArgAt<string>(4),
                ci.ArgAt<string>(5),
                ci.ArgAt<CancellationToken>(7)));
        return gh;
    }

    [Fact]
    public void Constructor_WithoutOptimisticFileContentGateway_ThrowsArgumentException()
    {
        var plainGitHub = Substitute.For<IGitHubService>();
        Assert.Throws<ArgumentException>(() => new RemoteAssetBackfiller(plainGitHub));
    }

    [Fact]
    public async Task TryBackfillAsync_ReturnsFalse_WhenNoMatchingArtifactFound()
    {
        var gitHubMock = CreateGitHubMock();
        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Artifact>());

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.False(result);
        await gitHubMock.DidNotReceive().DownloadArtifactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBackfillAsync_ExtractsDiffAndPushesCommit_WhenValidArtifactExists()
    {
        var gitHubMock = CreateGitHubMock();

        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 777L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "remoteasset-diff");

        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });

        var diffText = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset: sha256:oldsha123456
            +#!RemoteAsset: sha256:newsha789012
            """;

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("diff.patch");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(diffText);
        }
        ms.Position = 0;

        gitHubMock.DownloadArtifactAsync("owner", "repo", 777L, Arg.Any<CancellationToken>())
            .Returns(ms);

        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\n#!RemoteAsset: sha256:oldsha123456\nVersion: 8.2.1\n");

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.True(result);
        await gitHubMock.Received(1).UpdateFileContentAsync(
            "owner",
            "repo",
            "SPECS/curl/curl.spec",
            "chore: update #!RemoteAsset sha256 checksums",
            "Name: curl\n#!RemoteAsset: sha256:newsha789012\nVersion: 8.2.1\n",
            "autoupdate/curl",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBackfillAsync_ReturnsFalse_WhenSpecContentUnchanged()
    {
        var gitHubMock = CreateGitHubMock();

        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 777L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "remoteasset-diff");

        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });

        // Diff changes line that does NOT exist in spec
        var diffText = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset: sha256:differentoldsha
            +#!RemoteAsset: sha256:newsha789012
            """;

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("diff.patch");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(diffText);
        }
        ms.Position = 0;

        gitHubMock.DownloadArtifactAsync("owner", "repo", 777L, Arg.Any<CancellationToken>())
            .Returns(ms);

        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\n#!RemoteAsset: sha256:alreadyupgraded\nVersion: 8.2.1\n");

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.False(result);
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("SPECS/curl/curl.spec", true)]
    [InlineData("SPECS/../etc/passwd", false)]
    [InlineData("SPECS/curl/sub/curl.spec", false)]
    [InlineData("README.md", false)]
    [InlineData("SPECS/curl/patch.diff", false)]
    public void IsSafeSpecPath_ValidatesStrictSpecLayout(string path, bool expected)
    {
        Assert.Equal(expected, RemoteAssetBackfiller.IsSafeSpecPath(path));
    }

    [Fact]
    public void ReplaceFirstExactLine_ReplacesOnlyFirstWholeLineMatch_PreservingLineEndings()
    {
        var original = "Name: curl\r\n  #!RemoteAsset: sha256:old\r\n# changelog\r\n#!RemoteAsset: sha256:old\r\n";
        var updated = RemoteAssetBackfiller.ReplaceFirstExactLine(original, "#!RemoteAsset: sha256:old", "#!RemoteAsset: sha256:new");

        Assert.Equal("Name: curl\r\n  #!RemoteAsset: sha256:new\r\n# changelog\r\n#!RemoteAsset: sha256:old\r\n", updated);
    }

    [Fact]
    public async Task TryBackfillAsync_RejectsPathTraversalInDiffHeader()
    {
        var gitHubMock = CreateGitHubMock();
        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 778L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "remoteasset-diff");

        gitHubMock.ListWorkflowArtifactsAsync("owner", "repo", 123L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });

        var diffText = """
            --- a/SPECS/../../evil.spec
            +++ b/SPECS/../../evil.spec
            @@ -1,1 +1,1 @@
            -#!RemoteAsset: sha256:a
            +#!RemoteAsset: sha256:b
            """;

        var ms = new MemoryStream(Encoding.UTF8.GetBytes(diffText));
        gitHubMock.DownloadArtifactAsync("owner", "repo", 778L, Arg.Any<CancellationToken>())
            .Returns(ms);

        var backfiller = new RemoteAssetBackfiller(gitHubMock);
        var result = await backfiller.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 123L);

        Assert.False(result);
        await gitHubMock.DidNotReceive().GetFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBackfillAsync_WithOptimisticGateway_UsesExpectedBlobSha_AndReturnsFalseOn409Conflict()
    {
        var diffText = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -1,2 +1,2 @@
             Name: curl
            -#!RemoteAsset: sha256:old
            +#!RemoteAsset: sha256:new
            """;

        // 1. Happy path via CAS gateway
        var ghOk = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var casOk = (IOptimisticFileContentGateway)ghOk;
        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 888L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "remoteasset-diff");
        ghOk.ListWorkflowArtifactsAsync("owner", "repo", 200L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });
        ghOk.DownloadArtifactAsync("owner", "repo", 888L, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(diffText)));
        casOk.GetFileContentWithShaAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFileContent("Name: curl\n#!RemoteAsset: sha256:old\n", "blob-sha-1"));
        casOk.UpdateFileContentIfUnchangedAsync(
                "owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", "blob-sha-1", Arg.Any<CancellationToken>())
            .Returns("commit-sha-ok");

        var backfillerOk = new RemoteAssetBackfiller(ghOk, optimisticFileGateway: casOk);
        var ok = await backfillerOk.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 200L);

        Assert.True(ok);
        await casOk.Received(1).UpdateFileContentIfUnchangedAsync(
            "owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), "Name: curl\n#!RemoteAsset: sha256:new\n", "autoupdate/curl", "blob-sha-1", Arg.Any<CancellationToken>());
        await ghOk.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);

        // 2. 409 Conflict -> returns false cleanly without falling back to blind UpdateFileContentAsync
        var ghConflict = Substitute.For<IGitHubService, IOptimisticFileContentGateway>();
        var casConflict = (IOptimisticFileContentGateway)ghConflict;
        ghConflict.ListWorkflowArtifactsAsync("owner", "repo", 201L, Arg.Any<CancellationToken>())
            .Returns(new[] { artifact });
        ghConflict.DownloadArtifactAsync("owner", "repo", 888L, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(diffText)));
        casConflict.GetFileContentWithShaAsync("owner", "repo", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFileContent("Name: curl\n#!RemoteAsset: sha256:old\n", "blob-sha-stale"));
        casConflict.UpdateFileContentIfUnchangedAsync(
                "owner", "repo", "SPECS/curl/curl.spec", Arg.Any<string>(), Arg.Any<string>(), "autoupdate/curl", "blob-sha-stale", Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new ApiException("Conflict", HttpStatusCode.Conflict));

        var backfillerConflict = new RemoteAssetBackfiller(ghConflict, optimisticFileGateway: casConflict);
        var conflictResult = await backfillerConflict.TryBackfillAsync("owner", "repo", "autoupdate/curl", 42, 201L);

        Assert.False(conflictResult);
        await ghConflict.DidNotReceiveWithAnyArgs().UpdateFileContentAsync(default!, default!, default!, default!, default!, default!, default);
    }
}

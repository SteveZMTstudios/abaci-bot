using System.IO.Compression;
using System.Text;
using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class ArtifactDiffExtractorTests
{
    [Fact]
    public async Task ExtractDiffContentAsync_ParsesRawDiffStream()
    {
        var rawDiff = """
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset: sha256:aaa
            +#!RemoteAsset: sha256:bbb
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawDiff));
        var result = await ArtifactDiffExtractor.ExtractDiffContentAsync(stream);

        Assert.Contains("+#!RemoteAsset: sha256:bbb", result);
    }

    [Fact]
    public async Task ExtractDiffContentAsync_ParsesZipArchiveDiffStream()
    {
        var rawDiff = """
            --- a/SPECS/foo/foo.spec
            +++ b/SPECS/foo/foo.spec
            @@ -1,2 +1,2 @@
            -#!RemoteAsset: sha256:old
            +#!RemoteAsset: sha256:new
            """;

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("pr-123-remoteassets.diff");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(rawDiff);
        }
        ms.Position = 0;

        var result = await ArtifactDiffExtractor.ExtractDiffContentAsync(ms);

        Assert.Contains("+#!RemoteAsset: sha256:new", result);
    }

    [Fact]
    public void ExtractFileRemoteAssetChanges_OnlyExtractsTargetFileHunks()
    {
        var multiDiff = """
            diff --git a/SPECS/curl/curl.spec b/SPECS/curl/curl.spec
            --- a/SPECS/curl/curl.spec
            +++ b/SPECS/curl/curl.spec
            @@ -10,2 +10,2 @@
            -#!RemoteAsset:sha256:curl_old
            +#!RemoteAsset:sha256:curl_new
            diff --git a/SPECS/git/git.spec b/SPECS/git/git.spec
            --- a/SPECS/git/git.spec
            +++ b/SPECS/git/git.spec
            @@ -20,2 +20,2 @@
            -#!RemoteAsset:sha256:git_old
            +#!RemoteAsset:sha256:git_new
            """;

        var curlChanges = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(multiDiff, "SPECS/curl/curl.spec");
        Assert.Single(curlChanges);
        Assert.Equal("#!RemoteAsset:sha256:curl_old", curlChanges[0].OldLine);
        Assert.Equal("#!RemoteAsset:sha256:curl_new", curlChanges[0].NewLine);

        var gitChanges = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(multiDiff, "SPECS/git/git.spec");
        Assert.Single(gitChanges);
        Assert.Equal("#!RemoteAsset:sha256:git_old", gitChanges[0].OldLine);
        Assert.Equal("#!RemoteAsset:sha256:git_new", gitChanges[0].NewLine);

        var otherChanges = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(multiDiff, "SPECS/other/other.spec");
        Assert.Empty(otherChanges);
    }

    [Fact]
    public async Task ExtractDiffContentAsync_OversizedNonSeekableStreamAndOversizedZipEntry_ReturnsEmptyWithoutOom()
    {
        // 1. Non-seekable stream exceeding 10 MiB hard cap aborts early and returns empty
        using var oversizedRaw = new NonSeekableRepeatingStream((byte)'+', 11 * 1024 * 1024);
        var rawResult = await ArtifactDiffExtractor.ExtractDiffContentAsync(oversizedRaw);
        Assert.Equal(string.Empty, rawResult);
        Assert.True(oversizedRaw.TotalBytesRead <= (10 * 1024 * 1024) + 16384);

        // 2. Highly-compressible ZIP entry exceeding 10 MiB decompressed cap returns empty without unbounded ReadToEnd
        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("bomb.diff");
            await using var entryStream = entry.Open();
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (int written = 0; written < 11 * 1024 * 1024; written += chunk.Length)
            {
                await entryStream.WriteAsync(chunk);
            }
        }
        zipMs.Position = 0;

        var zipResult = await ArtifactDiffExtractor.ExtractDiffContentAsync(zipMs);
        Assert.Equal(string.Empty, zipResult);
    }

    private sealed class NonSeekableRepeatingStream : Stream
    {
        private readonly byte _fill;
        private readonly long _totalLength;
        public long TotalBytesRead { get; private set; }

        public NonSeekableRepeatingStream(byte fill, long totalLength)
        {
            _fill = fill;
            _totalLength = totalLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _totalLength - TotalBytesRead;
            if (remaining <= 0)
                return 0;

            var toCopy = (int)Math.Min(count, remaining);
            Array.Fill(buffer, _fill, offset, toCopy);
            TotalBytesRead += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _totalLength - TotalBytesRead;
            if (remaining <= 0)
                return ValueTask.FromResult(0);

            var toCopy = (int)Math.Min(buffer.Length, remaining);
            buffer[..toCopy].Span.Fill(_fill);
            TotalBytesRead += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

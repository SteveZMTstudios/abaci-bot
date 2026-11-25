namespace abaci_bot.Services;

/// <summary>
/// Exception thrown when an archive download or decompressed entry stream exceeds its configured size limit.
/// </summary>
internal sealed class ArchiveSizeLimitExceededException : IOException
{
    public long LimitBytes { get; }

    public ArchiveSizeLimitExceededException(long limitBytes, string? message = null)
        : base(message ?? $"Archive stream exceeded maximum allowed limit of {limitBytes} bytes.")
    {
        LimitBytes = limitBytes;
    }
}

/// <summary>
/// Read-only wrapper stream enforcing an upper bound on total bytes read from an underlying stream,
/// aborting immediately on overrun so oversized payloads cannot buffer unboundedly in memory.
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly bool _leaveOpen;
    private readonly IDisposable? _ownedResource;
    private long _totalBytesRead;
    private int _disposed;

    public BoundedReadStream(
        Stream inner,
        long maxBytes,
        bool leaveOpen = false,
        IDisposable? ownedResource = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be positive.");
        }

        _inner = inner;
        _maxBytes = maxBytes;
        _leaveOpen = leaveOpen;
        _ownedResource = ownedResource;
    }

    public long TotalBytesRead => _totalBytesRead;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var maxToRead = ComputeAllowedReadCount(buffer.Length);
        var bytesRead = _inner.Read(buffer[..maxToRead]);
        RecordAndCheckBytesRead(bytesRead);
        return bytesRead;
    }

    public override int ReadByte()
    {
        Span<byte> single = stackalloc byte[1];
        return Read(single) == 0 ? -1 : single[0];
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var maxToRead = ComputeAllowedReadCount(buffer.Length);
        var bytesRead = await _inner.ReadAsync(buffer[..maxToRead], cancellationToken).ConfigureAwait(false);
        RecordAndCheckBytesRead(bytesRead);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int ComputeAllowedReadCount(int requested)
    {
        var remaining = _maxBytes - _totalBytesRead;
        if (remaining >= int.MaxValue)
        {
            return requested;
        }

        // Allow up to 1 byte past the limit so an exact-size stream can observe EOF (0) instead of prematurely failing.
        var capPlusOne = Math.Max(1L, remaining + 1);
        return (int)Math.Min(requested, capPlusOne);
    }

    private void RecordAndCheckBytesRead(int bytesRead)
    {
        if (bytesRead <= 0)
        {
            return;
        }

        _totalBytesRead += bytesRead;
        if (_totalBytesRead > _maxBytes)
        {
            throw new ArchiveSizeLimitExceededException(_maxBytes);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            if (!_leaveOpen)
            {
                _inner.Dispose();
            }

            _ownedResource?.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (!_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            _ownedResource?.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

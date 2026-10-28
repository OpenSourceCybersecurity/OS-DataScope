namespace DataScope.Scanner.Content;

/// <summary>
/// Read-only wrapper that throws once more than <c>limit</c> bytes have been read. Protects against archive
/// entries that decompress to far more than their headers declare.
/// </summary>
internal sealed class LimitedStream(Stream inner, long limit, Action<long>? onRead = null) : Stream
{
    private long read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        // Ask for one byte more than the limit allows, so going over is detected instead of silently cut short.
        var allowed = (int)Math.Min(buffer.Length, limit - read + 1);
        var n = inner.Read(buffer[..allowed]);
        read += n;
        if (read > limit)
        {
            throw new ContentLimitException("An archive entry expanded beyond the safety limit.");
        }
        onRead?.Invoke(n);
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

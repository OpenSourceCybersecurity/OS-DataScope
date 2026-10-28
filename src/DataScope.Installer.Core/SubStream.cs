using System;
using System.IO;

namespace DataScope.Installer;

/// <summary>A read-only, seekable window onto part of another stream: the zip inside the setup program.</summary>
public sealed class SubStream(Stream inner, long start, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var left = length - _position;
        if (left <= 0)
        {
            return 0;
        }
        inner.Seek(start + _position, SeekOrigin.Begin);
        var read = inner.Read(buffer, offset, (int)Math.Min(count, left));
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

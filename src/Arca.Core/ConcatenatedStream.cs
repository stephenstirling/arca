namespace Arca.Core;

/// <summary>Read-only, seekable view of several files one after another, so the pieces of a split archive read as one file.</summary>
internal sealed class ConcatenatedStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;

    public ConcatenatedStream(IReadOnlyList<string> paths)
    {
        _parts = new FileStream[paths.Count];
        _starts = new long[paths.Count];
        try
        {
            long offset = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                _parts[i] = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                _starts[i] = offset;
                offset += _parts[i].Length;
            }
            _length = offset;
        }
        catch
        {
            Dispose(true);
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            int index = PartAt(_position);
            var part = _parts[index];
            part.Position = _position - _starts[index];
            int n = part.Read(buffer);
            if (n == 0) break;   // a part shrank after it was opened
            _position += n;
            total += n;
            buffer = buffer[n..];
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    private int PartAt(long position)
    {
        int index = Array.BinarySearch(_starts, position);
        if (index < 0) index = ~index - 1;
        // Skip empty parts that share a start offset with the next one.
        while (index < _parts.Length - 1 && _starts[index + 1] <= position) index++;
        return index;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var part in _parts) part?.Dispose();
        base.Dispose(disposing);
    }
}

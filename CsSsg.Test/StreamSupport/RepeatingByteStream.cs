namespace CsSsg.Test.StreamSupport;

/// <summary>
/// This is a synthetic async-only read-only stream, whose ReadAsync fills a buffer with a constant value.
/// </summary>
/// <param name="b">The constant value to fill reads with</param>
/// <param name="length">The simulated stream length</param>
public class RepeatingByteStream(byte b, long length) : Stream
{
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not synchronously readable");
    
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var remaining = Math.Max(length - _position, 0);
        var toRead = (int)Math.Min(buffer.Length, remaining);
        buffer = buffer[..toRead];
        buffer.Span.Fill(b);
        _position += toRead;
        return new ValueTask<int>(toRead);
    }
        
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!Seekable)
            throw new NotSupportedException("stream is not seekable");
        switch (origin)
        {
            case SeekOrigin.Begin:
                Position = offset;
                break;
            case SeekOrigin.Current:
                Position += offset;
                break;
            case SeekOrigin.End:
                Position = Length + offset;
                break;
        }
        // "Seeking to any location beyond the length of the stream is supported."
        // so defer range check to within ReadAsync
        return Position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("stream is not writable and seekable");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not writable");

    public override bool CanRead => true;
    public override bool CanSeek => Seekable;
    public override bool CanWrite => false;

    public override long Length => Seekable ? length : throw new NotSupportedException("stream is not seekable");

    public override long Position
    {
        get => Seekable ? _position : throw new NotSupportedException("stream is not seekable");
        set
        {
            if (Seekable) _position = value;
            else throw new NotSupportedException("stream is not seekable");
        }
    }

    internal bool Seekable = false;
    
    // keep track of internal position so we can tell when to cut off reading
    private long _position;
}
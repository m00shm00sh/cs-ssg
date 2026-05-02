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
        => throw new NotSupportedException("stream is not seekable");

    public override void SetLength(long value)
        => throw new NotSupportedException("stream is not writable and seekable");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not writable");

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("stream is not writable");
    public override long Position
    {
        get => throw new NotSupportedException("stream is not seekable");
        set => throw new NotSupportedException("stream is not seekable");
    }

    // keep track of internal position so we can tell when to cut off reading
    internal long _position;

    // keep track of internal length to save constructor arg
    internal long _length => length;
}
namespace CsSsg.Test.StreamSupport;

/// <summary>
/// This is a synthetic async-only read-only stream, whose ReadAsync fills a buffer with a constant value.
/// </summary>
/// <param name="b">The constant value to fill reads with</param>
/// <param name="length">The simulated stream length</param>
internal class RepeatingByteStream(byte b, long length) : Stream
{
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not synchronously readable");
    
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = Math.Max(Length - Position, 0);
        var toRead = (int)Math.Min(length, remaining);
        buffer = buffer[..toRead];
        buffer.Span.Fill(b);
        Position += toRead;
        return new ValueTask<int>(toRead);
    }
        
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var remaining = Math.Max(Length - Position, 0);
        var toRead = (int)Math.Min(count, remaining);
        var bufAsSpan = buffer.AsSpan(offset, toRead);
        bufAsSpan.Fill(b);
        Position += toRead;
        return Task.FromResult(toRead);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
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
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; } = length;
    public override long Position { get; set; }
}
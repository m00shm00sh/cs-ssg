namespace CsSsg.Test.StreamSupport;

/// <summary>
/// This is a synthetic async-only read-only stream, whose ReadAsync fills a buffer with a constant value.
/// </summary>
/// <param name="b">The constant value to fill reads with</param>
/// <param name="count">The simulated stream length</param>
internal class RepeatingByteStream(byte b, long count) : Stream
{
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not synchronously readable");
    
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var remaining = Length - Position;
        var toRead = (int)Math.Min(count, remaining);
        var bufAsSpan = buffer.AsSpan(offset, toRead);
        bufAsSpan.Fill(b);
        Position += toRead;
        return Task.FromResult(toRead);
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("stream is not seekable");

    public override void SetLength(long value)
        => throw new NotSupportedException("stream is not writable and seekable");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not writable");

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; } = count;
    public override long Position { get; set; }
}
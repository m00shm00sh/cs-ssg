namespace CsSsg.Test.StreamSupport;

/// <summary>
/// Synthetic stream type for verifying stream usability checks.
/// </summary>
internal class DummyStream(bool canRead, bool canSeek, bool canWrite) : Stream
{
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not readable");
    
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("stream is not seekable");

    public override void SetLength(long value)
        => throw new NotSupportedException("stream is not writable and seekable");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("stream is not writable");

    public override bool CanRead => canRead;
    public override bool CanSeek => canSeek;
    public override bool CanWrite => canWrite;
    public override long Length => 0;
    public override long Position { get; set; }
}
using System.Data.Common;
using Microsoft.AspNetCore.WebUtilities;

namespace CsSsg.Src.Media;

internal static class StreamSupport
{
    // Microsoft.Aspnet.Http::DefaultBufferThreshold
    private const int ASPNET_DEFAULT_BUFFER_THRESHOLD = 1024 * 30;
    
    extension(Stream stream)
    {
        internal Stream ConstructBufferingReadStream()
            => new FileBufferingReadStream(stream, ASPNET_DEFAULT_BUFFER_THRESHOLD);

        internal async Task<bool> TryDrainThenRewindAsync(long? limit, CancellationToken token)
        {
            try
            {
                await stream.DrainAsync(limit, token);
                stream.Seek(0, SeekOrigin.Begin);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// A stream whose resource lifetime is tied to an associated DbReader.
/// <br/>
/// The typical use case for this is returning <c>BYTEA</c> streams from Npgsql reader because the reader's model
/// is the reader will be (async) disposed by the enclosing scope once the stream is done and the driver makes no
/// attempt to have Dispose and DisposeAsync dispose their semantically enclosing readers.
/// </summary>
/// <param name="inner">The read stream of the object</param>
/// <param name="reader">The database reader that owns the stream</param>
internal class StreamWrappingDataReader(Stream inner, DbDataReader reader) : Stream
{
    // the choice of methods/properties to delegate is taken from FileBufferingReadStream's choice
    // of overriden methods/properties. *All disposal-related safety is delegated to the underlying stream.*
    
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;
    
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush() => throw new NotSupportedException();

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    => inner.CopyToAsync(destination, bufferSize, cancellationToken);
    
    // these two methods is where all the fun happens
    
    protected override void Dispose(bool disposing)
    {
        inner.Dispose();
        reader.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await reader.DisposeAsync();
    }
    
}
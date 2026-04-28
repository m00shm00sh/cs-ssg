namespace CsSsg.Test.StreamSupport;

internal static class StreamSupportExtensions
{
    extension(Stream s)
    {
        internal async Task<byte[]> SaveToArrayAsync(CancellationToken token)
        {
            var contentBuf = new byte[s.Length];
            await s.CopyToAsync(new MemoryStream(contentBuf, true), token);
            return contentBuf;
        }
    }
}
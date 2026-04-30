namespace CsSsg.Test.StreamSupport;

public static class StreamSupportExtensions
{
    extension(Stream s)
    {
        public Task<byte[]> SaveToArrayAsync()
            => s.SaveToArrayAsync(CancellationToken.None);
        
        public async Task<byte[]> SaveToArrayAsync(CancellationToken token)
        {
            var contentBuf = new byte[s.Length];
            await s.CopyToAsync(new MemoryStream(contentBuf, true), token);
            return contentBuf;
        }
    }
}
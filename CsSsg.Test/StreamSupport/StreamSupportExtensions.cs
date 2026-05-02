namespace CsSsg.Test.StreamSupport;

public static class StreamSupportExtensions
{
    extension(Stream s)
    {
        public Task<byte[]> SaveToArrayAsync()
            => s.SaveToArrayAsync(CancellationToken.None);
        
        public async Task<byte[]> SaveToArrayAsync(CancellationToken token)
        {
            var stream = new MemoryStream();
            await s.CopyToAsync(stream, token);
            return stream.ToArray();
        }
    }
}
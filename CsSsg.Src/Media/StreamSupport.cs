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
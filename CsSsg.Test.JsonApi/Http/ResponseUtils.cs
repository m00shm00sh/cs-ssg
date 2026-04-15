using System.Net.Http.Json;

namespace CsSsg.Test.JsonApi.Http;

internal static class ResponseUtils
{
    extension(HttpResponseMessage res)
    {
        public async Task<TR?> ReadAsJsonAsync<TR>(CancellationToken token = default)
            =>  await res.Content.ReadFromJsonAsync<TR>(RequestUtils.JSON_OPTIONS, token);
    }
}
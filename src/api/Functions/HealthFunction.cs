using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace WorkIqProfileChat.Api.Functions;

public sealed class HealthFunction
{
    [Function("Health")]
    public static async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteAsJsonAsync(new { status = "ok" });
        return response;
    }
}

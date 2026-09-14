using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using WorkIqProfileChat.Api.Security;
using WorkIqProfileChat.Api.Services;

namespace WorkIqProfileChat.Api.Functions;

public sealed class StatusFunction(
    EasyAuthUserContext userContext,
    SecretExpiryService secretExpiryService,
    ILogger<StatusFunction> logger)
{
    [Function("Status")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = userContext.RequireAllowedUser(request);
            HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Cache-Control", "no-store");
            await response.WriteAsJsonAsync(
                await secretExpiryService.GetStatusAsync(cancellationToken),
                cancellationToken);
            return response;
        }
        catch (UnauthorizedAccessException)
        {
            return request.CreateResponse(HttpStatusCode.Forbidden);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The Work IQ OAuth credential expiry could not be checked.");
            return request.CreateResponse(HttpStatusCode.ServiceUnavailable);
        }
    }
}

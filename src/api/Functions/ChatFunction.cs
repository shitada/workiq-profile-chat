using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using WorkIqProfileChat.Api.Models;
using WorkIqProfileChat.Api.Security;
using WorkIqProfileChat.Api.Services;

namespace WorkIqProfileChat.Api.Functions;

public sealed class ChatFunction(
    EasyAuthUserContext userContext,
    FoundryChatService chatService,
    ILogger<ChatFunction> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    [Function("Chat")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        string correlationId = Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        try
        {
            _ = userContext.RequireAllowedUser(request);
            string userAssertion = userContext.RequireUserAssertion(request);
            ChatRequest chatRequest = await JsonSerializer.DeserializeAsync<ChatRequest>(
                request.Body,
                SerializerOptions,
                cancellationToken)
                ?? throw new ArgumentException("The request body is required.");

            ChatResponse result = await chatService.SendAsync(
                chatRequest,
                userAssertion,
                cancellationToken);

            return await JsonAsync(request, HttpStatusCode.OK, result, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(
                "Chat request rejected. CorrelationId={CorrelationId} Reason={Reason}",
                correlationId,
                exception.Message);
            return await ErrorAsync(
                request,
                HttpStatusCode.Forbidden,
                "access_denied",
                exception.Message,
                correlationId,
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return await ErrorAsync(
                request,
                HttpStatusCode.BadRequest,
                "invalid_request",
                exception.Message,
                correlationId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Chat request failed. CorrelationId={CorrelationId}",
                correlationId);
            return await ErrorAsync(
                request,
                HttpStatusCode.BadGateway,
                "upstream_error",
                "The profile service could not complete the request.",
                correlationId,
                cancellationToken);
        }
    }

    private static Task<HttpResponseData> ErrorAsync(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string code,
        string message,
        string correlationId,
        CancellationToken cancellationToken) =>
        JsonAsync(
            request,
            statusCode,
            new ApiError(code, message, correlationId),
            cancellationToken);

    private static async Task<HttpResponseData> JsonAsync<T>(
        HttpRequestData request,
        HttpStatusCode statusCode,
        T body,
        CancellationToken cancellationToken)
    {
        HttpResponseData response = request.CreateResponse(statusCode);
        response.Headers.Add("Cache-Control", "no-store");
        response.Headers.Add("Pragma", "no-cache");
        await response.WriteAsJsonAsync(body, cancellationToken);
        return response;
    }
}

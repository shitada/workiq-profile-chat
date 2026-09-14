using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using WorkIqProfileChat.Api.Models;
using WorkIqProfileChat.Api.Options;

namespace WorkIqProfileChat.Api.Services;

#pragma warning disable OPENAI001
public sealed class FoundryChatService(
    OnBehalfOfTokenProvider tokenProvider,
    IOptions<FoundryOptions> options,
    ILogger<FoundryChatService> logger)
{
    private const int MaximumMessageLength = 8_000;
    private readonly FoundryOptions _options = options.Value;

    public async Task<ChatResponse> SendAsync(
        ChatRequest request,
        string userAssertion,
        CancellationToken cancellationToken)
    {
        Validate(request);

        string foundryToken = await tokenProvider.GetFoundryTokenAsync(
            userAssertion,
            cancellationToken);

        AIProjectClient projectClient = new(
            new Uri(_options.ProjectEndpoint),
            new StaticAccessTokenCredential(foundryToken));

        ProjectResponsesClient responseClient = projectClient.ProjectOpenAIClient
            .GetProjectResponsesClientForAgent(_options.AgentName);

        CreateResponseOptions responseOptions = BuildResponseOptions(request);
        ResponseResult response = await responseClient.CreateResponseAsync(
            responseOptions,
            cancellationToken);

        logger.LogInformation(
            "Foundry response completed. ResponseId={ResponseId} OutputItems={OutputItemCount}",
            response.Id,
            response.OutputItems.Count);

        foreach (ResponseItem item in response.OutputItems)
        {
            if (item.AsAgentResponseItem() is OAuthConsentRequestResponseItem consent)
            {
                return new OAuthConsentRequiredResponse(
                    response.Id,
                    consent.ConsentLink.ToString());
            }

            if (item is McpToolCallApprovalRequestItem approval)
            {
                return new ToolApprovalRequiredResponse(
                    response.Id,
                    approval.Id,
                    approval.ServerLabel,
                    approval.ToolName,
                    approval.ToolArguments.ToString());
            }
        }

        string text = response.GetOutputText();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Foundry returned no user-visible response.");
        }

        return new ChatCompletedResponse(response.Id, text);
    }

    private static CreateResponseOptions BuildResponseOptions(ChatRequest request)
    {
        CreateResponseOptions options = new()
        {
            PreviousResponseId = request.PreviousResponseId
        };

        if (request.Approval is not null)
        {
            options.InputItems.Add(ResponseItem.CreateMcpApprovalResponseItem(
                request.Approval.RequestId,
                request.Approval.Approved));
            return options;
        }

        options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.Message!));
        return options;
    }

    private static void Validate(ChatRequest request)
    {
        if (request.Approval is not null)
        {
            if (string.IsNullOrWhiteSpace(request.PreviousResponseId) ||
                string.IsNullOrWhiteSpace(request.Approval.RequestId))
            {
                throw new ArgumentException(
                    "Approval continuation requires a previous response and approval request ID.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("A message is required.");
        }

        if (request.Message.Length > MaximumMessageLength)
        {
            throw new ArgumentException(
                $"The message must be {MaximumMessageLength} characters or fewer.");
        }

        if (request.ContinueAfterConsent && string.IsNullOrWhiteSpace(request.PreviousResponseId))
        {
            throw new ArgumentException(
                "OAuth continuation requires a previous response ID.");
        }
    }
}
#pragma warning restore OPENAI001

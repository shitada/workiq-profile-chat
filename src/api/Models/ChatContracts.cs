using System.Text.Json.Serialization;

namespace WorkIqProfileChat.Api.Models;

public sealed record ChatRequest(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("previousResponseId")] string? PreviousResponseId = null,
    [property: JsonPropertyName("continueAfterConsent")] bool ContinueAfterConsent = false,
    [property: JsonPropertyName("approval")] ApprovalResponse? Approval = null);

public sealed record ApprovalResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("approved")] bool Approved);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChatCompletedResponse), "completed")]
[JsonDerivedType(typeof(OAuthConsentRequiredResponse), "oauth_consent_required")]
[JsonDerivedType(typeof(ToolApprovalRequiredResponse), "tool_approval_required")]
public abstract record ChatResponse(
    [property: JsonPropertyName("responseId")] string ResponseId);

public sealed record ChatCompletedResponse(
    string ResponseId,
    [property: JsonPropertyName("text")] string Text)
    : ChatResponse(ResponseId);

public sealed record OAuthConsentRequiredResponse(
    string ResponseId,
    [property: JsonPropertyName("consentLink")] string ConsentLink)
    : ChatResponse(ResponseId);

public sealed record ToolApprovalRequiredResponse(
    string ResponseId,
    [property: JsonPropertyName("approvalRequestId")] string ApprovalRequestId,
    [property: JsonPropertyName("serverLabel")] string ServerLabel,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("toolArguments")] string ToolArguments)
    : ChatResponse(ResponseId);

public sealed record ApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

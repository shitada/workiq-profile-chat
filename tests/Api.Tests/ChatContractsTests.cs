using System.Text.Json;
using WorkIqProfileChat.Api.Models;

namespace WorkIqProfileChat.Api.Tests;

public sealed class ChatContractsTests
{
    [Fact]
    public void CompletedResponse_UsesStableTypeDiscriminator()
    {
        ChatResponse response = new ChatCompletedResponse("resp-1", "result");

        string json = JsonSerializer.Serialize(response);

        Assert.Contains("\"kind\":\"completed\"", json);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OAuthConsentResponse_UsesCamelCaseContract()
    {
        ChatResponse response = new OAuthConsentRequiredResponse(
            "resp-1",
            "https://logic-apis-eastus2.consent.azure-apim.net/login?data=opaque");

        string json = JsonSerializer.Serialize(response);

        Assert.Contains("\"kind\":\"oauth_consent_required\"", json);
        Assert.Contains("\"responseId\":\"resp-1\"", json);
        Assert.Contains("\"consentLink\":\"https://logic-apis-eastus2", json);
        Assert.DoesNotContain("\"ResponseId\"", json);
        Assert.DoesNotContain("\"ConsentLink\"", json);
    }
}

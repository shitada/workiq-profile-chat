using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using WorkIqProfileChat.Api.Options;

namespace WorkIqProfileChat.Api.Services;

public sealed class OnBehalfOfTokenProvider(
    HttpClient httpClient,
    IOptions<AuthenticationOptions> authenticationOptions,
    IOptions<FoundryOptions> foundryOptions)
{
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";
    private const string ClientAssertionType =
        "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private const string JwtBearerGrant =
        "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private readonly AuthenticationOptions _authentication = authenticationOptions.Value;
    private readonly FoundryOptions _foundry = foundryOptions.Value;

    public async Task<string> GetFoundryTokenAsync(
        string userAssertion,
        CancellationToken cancellationToken)
    {
        string tokenEndpoint =
            $"https://login.microsoftonline.com/{_authentication.TenantId}/oauth2/v2.0/token";

        Dictionary<string, string> form = new()
        {
            ["client_id"] = _authentication.ApiClientId,
            ["grant_type"] = JwtBearerGrant,
            ["requested_token_use"] = "on_behalf_of",
            ["assertion"] = userAssertion,
            ["scope"] = _foundry.Scope
        };

        if (!string.IsNullOrWhiteSpace(_authentication.LocalClientSecret))
        {
            form["client_secret"] = _authentication.LocalClientSecret;
        }
        else
        {
            ManagedIdentityCredential credential =
                string.IsNullOrWhiteSpace(_authentication.ManagedIdentityClientId)
                    ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                    : new ManagedIdentityCredential(
                        ManagedIdentityId.FromUserAssignedClientId(
                            _authentication.ManagedIdentityClientId));
            AccessToken assertion = await credential.GetTokenAsync(
                new TokenRequestContext([TokenExchangeScope]),
                cancellationToken);
            form["client_assertion_type"] = ClientAssertionType;
            form["client_assertion"] = assertion.Token;
        }

        using HttpResponseMessage response = await httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(form),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            TokenErrorResponse? tokenError = null;
            try
            {
                tokenError = await response.Content.ReadFromJsonAsync<TokenErrorResponse>(
                    cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                // Entra errors are expected to be JSON, but never expose a raw body.
            }

            string errorCode = tokenError?.Error ?? "unknown_error";
            string numericCodes = tokenError?.ErrorCodes is { Count: > 0 }
                ? string.Join(",", tokenError.ErrorCodes)
                : "none";
            string correlationId = tokenError?.CorrelationId ?? "unavailable";
            throw new InvalidOperationException(
                $"The delegated token exchange failed with HTTP {(int)response.StatusCode}; " +
                $"error={errorCode}; error_codes={numericCodes}; correlation_id={correlationId}.");
        }

        TokenResponse? token = await response.Content.ReadFromJsonAsync<TokenResponse>(
            cancellationToken: cancellationToken);

        return !string.IsNullOrWhiteSpace(token?.AccessToken)
            ? token.AccessToken
            : throw new InvalidOperationException("The delegated token exchange returned no access token.");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record TokenErrorResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_codes")] IReadOnlyList<int>? ErrorCodes,
        [property: JsonPropertyName("correlation_id")] string? CorrelationId);
}

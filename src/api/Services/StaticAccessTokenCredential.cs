using Azure.Core;

namespace WorkIqProfileChat.Api.Services;

internal sealed class StaticAccessTokenCredential(string token) : TokenCredential
{
    private readonly AccessToken _accessToken = new(token, DateTimeOffset.UtcNow.AddMinutes(5));

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) => _accessToken;

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) => ValueTask.FromResult(_accessToken);
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Http;

namespace GraphReportChat.Api;

public sealed class EasyAuth(Settings settings)
{
    public (UserIdentity User, string Assertion) Require(HttpRequest request) =>
        Validate(request.Headers["x-ms-client-principal"].FirstOrDefault(),
            request.Headers.Authorization.FirstOrDefault(), settings.Required("Authentication:TenantId"),
            settings.Required("Authentication:ApiClientId"), settings.Get("Authentication:RequiredRole", "ProfileChat.User"));

    // EasyAuth MUST require authentication and validate issuer/audience before traffic reaches this worker.
    // This header is a platform assertion, not a JWT signature verification substitute on an exposed local server.
    public static (UserIdentity User, string Assertion) Validate(string? encoded, string? authorization,
        string tenantId, string audience, string role)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 64000 ||
            !AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(bearer.Parameter))
            throw new ApiException(401, "authentication_required", "Entra bearer 認証が必要です。");
        try
        {
            using var principal = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var root = principal.RootElement;
            if (root.GetProperty("auth_typ").GetString() != "aad")
                throw new ApiException(401, "authentication_required", "Entra 認証が必要です。");
            var claims = root.GetProperty("claims").EnumerateArray().Select(c =>
                (Type: c.GetProperty("typ").GetString(), Value: c.GetProperty("val").GetString())).ToArray();
            string? Claim(params string[] names) => claims.FirstOrDefault(c => names.Contains(c.Type)).Value;
            string? tenant = Claim("tid", "http://schemas.microsoft.com/identity/claims/tenantid");
            string? oid = Claim("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
            if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(oid, out _) || !string.Equals(tenantId, tenant, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(403, "tenant_forbidden", "このテナントからはアクセスできません。");
            if (!claims.Any(c => c.Type is "roles" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role" && c.Value == role))
                throw new ApiException(403, "role_required", "アプリケーションの利用ロールが必要です。");
            var aud = Claim("aud");
            if (aud is not null && aud != audience && aud != $"api://{audience}")
                throw new ApiException(401, "audience_invalid", "API audience が一致しません。");
            return (new(tenant!.ToLowerInvariant(), oid!.ToLowerInvariant()), bearer.Parameter!);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ApiException(401, "principal_invalid", "認証情報が不正です。");
        }
    }
}

public interface ITokenExchange
{
    Task<string> Graph(string assertion, CancellationToken ct);
    Task<string> Foundry(string assertion, CancellationToken ct);
}
public sealed class TokenExchange(HttpClient http, Settings settings) : ITokenExchange
{
    public Task<string> Graph(string assertion, CancellationToken ct) => Exchange(assertion, "https://graph.microsoft.com/.default", ct);
    public Task<string> Foundry(string assertion, CancellationToken ct) =>
        Exchange(assertion, settings.Get("Foundry:Scope", "https://ai.azure.com/.default"), ct);

    private async Task<string> Exchange(string assertion, string scope, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = settings.Required("Authentication:ApiClientId"),
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["requested_token_use"] = "on_behalf_of",
            ["assertion"] = assertion,
            ["scope"] = scope
        };
        var secret = settings.Get("Authentication:LocalClientSecret");
        if (secret.Length != 0) form["client_secret"] = secret;
        else
        {
            var id = settings.Get("Authentication:ManagedIdentityClientId");
            var credential = new ManagedIdentityCredential(id.Length == 0 ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(id));
            var token = await credential.GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange/.default"]), ct);
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = token.Token;
        }
        using var response = await http.PostAsync($"https://login.microsoftonline.com/{settings.Required("Authentication:TenantId")}/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode == System.Net.HttpStatusCode.BadRequest ? 401 : 502,
                "delegated_token_failed", "委任トークン交換に失敗しました。サインインと管理者同意を確認してください。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("access_token", out var access) && !string.IsNullOrWhiteSpace(access.GetString())
            ? access.GetString()! : throw new ApiException(502, "delegated_token_failed", "委任トークンが返されませんでした。");
    }
}
public sealed class StaticTokenCredential(string token) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(token, DateTimeOffset.UtcNow.AddMinutes(5));
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

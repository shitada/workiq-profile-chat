using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;
using WorkIqProfileChat.Api.Options;

namespace WorkIqProfileChat.Api.Security;

public sealed class EasyAuthUserContext(IOptions<AuthenticationOptions> options)
{
    private const string PrincipalHeader = "x-ms-client-principal";
    private const string AuthorizationHeader = "authorization";
    private const string AccessTokenHeader = "x-ms-token-aad-access-token";
    private static readonly string[] ObjectIdClaimTypes =
    [
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        "oid"
    ];
    private static readonly string[] TenantIdClaimTypes =
    [
        "http://schemas.microsoft.com/identity/claims/tenantid",
        "tid"
    ];
    private static readonly string[] RoleClaimTypes =
    [
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        "roles"
    ];

    private readonly AuthenticationOptions _options = options.Value;

    public AuthenticatedUser RequireAllowedUser(HttpRequestData request)
    {
        EasyAuthPrincipal principal = ParsePrincipal(request);
        IReadOnlyList<EasyAuthClaim> claims = principal.Claims ?? [];

        string tenantId = GetRequiredClaim(claims, TenantIdClaimTypes, "tenant");
        if (!string.Equals(tenantId, _options.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The signed-in account belongs to a different tenant.");
        }

        bool isAllowed = claims.Any(claim =>
            RoleClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(claim.Value, _options.RequiredRole, StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            throw new UnauthorizedAccessException("The signed-in account is not assigned to this application.");
        }

        return new AuthenticatedUser(
            GetRequiredClaim(claims, ObjectIdClaimTypes, "object identifier"),
            tenantId);
    }

    public string RequireUserAssertion(HttpRequestData request)
    {
        if (request.Headers.TryGetValues(AuthorizationHeader, out IEnumerable<string>? authorizationValues))
        {
            string? bearer = authorizationValues.FirstOrDefault();
            if (bearer?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return bearer["Bearer ".Length..].Trim();
            }
        }

        if (request.Headers.TryGetValues(AccessTokenHeader, out IEnumerable<string>? tokenValues))
        {
            string? token = tokenValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        throw new UnauthorizedAccessException("A validated delegated access token was not provided.");
    }

    public static EasyAuthPrincipal ParsePrincipal(HttpRequestData request)
    {
        if (!request.Headers.TryGetValues(PrincipalHeader, out IEnumerable<string>? values))
        {
            throw new UnauthorizedAccessException("Azure App Service Authentication did not provide a user principal.");
        }

        string? encoded = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new UnauthorizedAccessException("The authenticated user principal is empty.");
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            return JsonSerializer.Deserialize<EasyAuthPrincipal>(Encoding.UTF8.GetString(bytes))
                ?? throw new UnauthorizedAccessException("The authenticated user principal is invalid.");
        }
        catch (FormatException exception)
        {
            throw new UnauthorizedAccessException("The authenticated user principal is malformed.", exception);
        }
        catch (JsonException exception)
        {
            throw new UnauthorizedAccessException("The authenticated user principal is malformed.", exception);
        }
    }

    private static string GetRequiredClaim(
        IReadOnlyList<EasyAuthClaim> claims,
        IReadOnlyCollection<string> claimTypes,
        string displayName)
    {
        string? value = claims.FirstOrDefault(claim =>
            claimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))?.Value;

        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new UnauthorizedAccessException($"The {displayName} claim is missing.");
    }
}

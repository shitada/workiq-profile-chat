using System.Text.Json.Serialization;

namespace WorkIqProfileChat.Api.Security;

public sealed record EasyAuthPrincipal(
    [property: JsonPropertyName("auth_typ")] string? AuthenticationType,
    [property: JsonPropertyName("name_typ")] string? NameClaimType,
    [property: JsonPropertyName("role_typ")] string? RoleClaimType,
    [property: JsonPropertyName("claims")] IReadOnlyList<EasyAuthClaim>? Claims);

public sealed record EasyAuthClaim(
    [property: JsonPropertyName("typ")] string Type,
    [property: JsonPropertyName("val")] string Value);

public sealed record AuthenticatedUser(string ObjectId, string TenantId);

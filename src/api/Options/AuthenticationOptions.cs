using System.ComponentModel.DataAnnotations;

namespace WorkIqProfileChat.Api.Options;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    [Required]
    public string TenantId { get; init; } = string.Empty;

    [Required]
    public string ApiClientId { get; init; } = string.Empty;

    [Required]
    public string RequiredRole { get; init; } = "ProfileChat.User";

    public string? ManagedIdentityClientId { get; init; }

    public string? LocalClientSecret { get; init; }

    public DateTimeOffset WorkIqClientSecretExpiresOn { get; init; }
}

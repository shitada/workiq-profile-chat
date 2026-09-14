using System.Text.Json.Serialization;

namespace WorkIqProfileChat.Api.Models;

public sealed record ServiceStatus(
    [property: JsonPropertyName("secretRotationRequired")] bool SecretRotationRequired,
    [property: JsonPropertyName("secretDaysRemaining")] int? SecretDaysRemaining);

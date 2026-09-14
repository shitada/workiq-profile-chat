using System.ComponentModel.DataAnnotations;

namespace WorkIqProfileChat.Api.Options;

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    [Required]
    [Url]
    public string ProjectEndpoint { get; init; } = string.Empty;

    [Required]
    public string AgentName { get; init; } = string.Empty;

    [Required]
    public string Scope { get; init; } = "https://ai.azure.com/.default";
}

using System.ClientModel;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;

string projectEndpoint = Required("AZURE_AI_PROJECT_ENDPOINT");
string connectionId = Required("WORKIQ_CONNECTION_ID");
string agentName = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME")
    ?? "workiq-profile-agent";
string modelDeploymentName = Environment.GetEnvironmentVariable("FOUNDRY_MODEL_DEPLOYMENT")
    ?? "gpt-4.1";
string instructionsPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "agent-instructions.md"));

if (!File.Exists(instructionsPath))
{
    throw new FileNotFoundException("Agent instructions were not found.", instructionsPath);
}

string instructions = await File.ReadAllTextAsync(instructionsPath);
AIProjectClient projectClient = new(
    new Uri(projectEndpoint),
    new DefaultAzureCredential());

BinaryData body = BinaryData.FromObjectAsJson(new
{
    definition = new
    {
        kind = "prompt",
        model = modelDeploymentName,
        instructions,
        tools = new[]
        {
            new
            {
                type = "mcp",
                server_label = "workiq",
                server_url = "https://workiq.svc.cloud.microsoft/mcp",
                project_connection_id = connectionId,
                allowed_tools = new[] { "ask" },
                require_approval = "always"
            }
        }
    }
});

ClientResult response = await projectClient.AgentAdministrationClient
    .CreateAgentVersionAsync(agentName, BinaryContent.Create(body));

using JsonDocument document = JsonDocument.Parse(response.GetRawResponse().Content);
string version = document.RootElement.GetProperty("version").GetString()
    ?? throw new InvalidOperationException("The created agent version is missing.");

Console.WriteLine($"Agent '{agentName}' version '{version}' is ready.");
Console.WriteLine($"AZD_ENV_AGENT_VERSION={version}");

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");

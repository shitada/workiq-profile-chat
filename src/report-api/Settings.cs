using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GraphReportChat.Api;

public sealed class Settings(IConfiguration configuration)
{
    public string Get(string key, string fallback = "") => configuration[key] ?? fallback;
    public string Required(string key) => string.IsNullOrWhiteSpace(Get(key))
        ? throw new ApiException(503, "configuration_missing", $"サーバー設定 {key} が必要です。") : Get(key);
    public string Provider
    {
        get
        {
            var provider = Get("Report:Provider", "graph");
            return provider is "graph" or "workiq" ? provider : throw new ApiException(503, "configuration_invalid", "取得方式の設定が不正です。");
        }
    }
    public int TestYear => int.TryParse(Get("Report:TestYear", "2026"), out var year) && year is >= 1 and <= 9998
        ? year : throw new ApiException(503, "configuration_invalid", "基準年が不正です。");
    public bool SharePointConfigured => Get("Report:SharePointDriveId").Length > 0 && Get("Report:SharePointFolderId").Length > 0;
    public string AcquisitionProfile => Get("WorkIq:AcquisitionProfile", WorkIqReadPlan.LegacyProfile) switch
    {
        WorkIqReadPlan.Profile => WorkIqReadPlan.Profile,
        WorkIqReadPlan.LegacyProfile => WorkIqReadPlan.LegacyProfile,
        _ => throw new ApiException(503, "configuration_invalid", "Work IQ取得profileが不正です。")
    };
}
public sealed class GenerationSpec
{
    public string Instructions { get; }
    public string CollectorInstructions { get; }
    public string VerifiedCollectorInstructions { get; }
    public string PromptHash { get; }
    public string ConfigHash { get; }
    public string SchemaHash { get; }
    public string EvidenceSchemaHash { get; }
    public GenerationConfig Config { get; }
    public GenerationSpec() : this(Path.Combine(AppContext.BaseDirectory, "reporting")) { }
    public GenerationSpec(string folder)
    {
        var instructions = File.ReadAllBytes(Path.Combine(folder, "report-instructions.md"));
        var config = File.ReadAllBytes(Path.Combine(folder, "generation-config.json"));
        Instructions = Encoding.UTF8.GetString(instructions).TrimStart('\uFEFF');
        CollectorInstructions = File.ReadAllText(Path.Combine(folder, "collector-instructions.md"));
        var verifiedInstructions = Path.Combine(folder, "verified-collector-instructions.md");
        VerifiedCollectorInstructions = File.Exists(verifiedInstructions) ? File.ReadAllText(verifiedInstructions) : "";
        PromptHash = Hash(instructions);
        ConfigHash = Hash(config);
        var schema = Path.Combine(folder, "report-schema.json");
        SchemaHash = File.Exists(schema) ? Hash(File.ReadAllBytes(schema)) : throw new InvalidOperationException("report-schema.json is required.");
        EvidenceSchemaHash = Hash(File.ReadAllBytes(Path.Combine(folder, "evidence-schema.json")));
        Config = JsonSerializer.Deserialize<GenerationConfig>(Encoding.UTF8.GetString(config).TrimStart('\uFEFF'), Json.Options)
            ?? throw new InvalidOperationException("Invalid generation configuration.");
        if (Config.InputTokenBudget < 1000 || Config.MaxOutputTokens < 100 || Config.MaxGraphCalls < 1)
            throw new InvalidOperationException("Invalid generation budget.");
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
public sealed record GenerationConfig(string Version, string ModelDeployment, string ExpectedModelVersion,
    float Temperature, int MaxOutputTokens, int InputTokenBudget, int MaxGraphCalls, int MaxConcurrency);

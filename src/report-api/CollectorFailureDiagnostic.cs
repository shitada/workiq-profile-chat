using System.Text.Json;

namespace GraphReportChat.Api;

public sealed record CollectorResponseInspection(CollectorFailureDiagnostic Failure, IReadOnlyList<WorkIqToolReply> Replies)
{
    public static CollectorResponseInspection Read(JsonElement response, bool verifiedProfile = false)
    {
        var replies = new List<WorkIqToolReply>();
        if (response.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || GraphCollector.S(item, "type") != "mcp_call") continue;
                string? name = GraphCollector.S(item, "name");
                if (!(name == "ask" || (verifiedProfile && name is "fetch" or "call_function")) ||
                    GraphCollector.S(item, "server_label") != "workiq")
                    throw new ApiException(502, "unexpected_tool", "許可されていない取得ツールです。");
                if (replies.Count < 8) replies.Add(WorkIqToolReplyReader.ReadCall(item, replies.Count + 1) with { ToolName = name });
            }
        return new(CollectorFailureDiagnostic.Read(response), replies);
    }
}

public sealed record CollectorFailureDiagnostic(string Status, string? Code, string? Message, string? IncompleteReason,
    string? UpstreamErrorCode = null, string? RecoveryAction = null, string? UpstreamRequestId = null)
{
    public static CollectorFailureDiagnostic Read(JsonElement response)
    {
        static string? Identifier(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return null;
            var text = field.GetString()!;
            return text.Length is > 0 and <= 80 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? text : "unknown";
        }
        string? code = null, message = null, reason = null, upstreamCode = null, recovery = null, requestId = null;
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = Identifier(error, "code");
            if (error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
            {
                string original = text.GetString()!;
                string? prefix = new[] { "ask", "fetch", "call_function" }
                    .Select(tool => $"An error occurred invoking '{tool}': ")
                    .FirstOrDefault(candidate => original.StartsWith(candidate, StringComparison.Ordinal));
                if (code == "tool_user_error" && prefix is not null && original.Length <= 65536)
                {
                    try
                    {
                        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(original[prefix.Length..]),
                            new JsonReaderOptions { MaxDepth = 16 });
                        using var detail = JsonDocument.ParseValue(ref reader);
                        if (detail.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            upstreamCode = Identifier(detail.RootElement, "errorCode");
                            recovery = Identifier(detail.RootElement, "recoveryAction");
                            if (detail.RootElement.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String &&
                                Guid.TryParse(id.GetString(), out var guid)) requestId = guid.ToString();
                        }
                    }
                    catch (JsonException) { /* Unrecognized service wording remains in the redacted diagnostic, never guessed. */ }
                }
                message = WorkIqToolReplyReader.Redact(original);
                if (message.Length > 2000) message = message[..2000] + " [省略]";
            }
        }
        if (response.TryGetProperty("incomplete_details", out var incomplete) && incomplete.ValueKind == JsonValueKind.Object)
            reason = Identifier(incomplete, "reason");
        return new(Identifier(response, "status") ?? "unknown", code, message, reason, upstreamCode, recovery, requestId);
    }
}

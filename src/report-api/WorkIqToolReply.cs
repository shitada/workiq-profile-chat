using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public sealed record WorkIqToolReply(int Sequence, string Format, bool IsError,
    int? StatusCode, string? ErrorCode, string Text, bool Truncated,
    string? RawText = null, bool RawTruncated = false, string? ToolName = null, string? TaskId = null);

public static class WorkIqToolReplyReader
{
    private const int MaximumText = 6000;
    private const int MaximumPayload = 256 * 1024;

    public static WorkIqToolReply SanitizeSaved(WorkIqToolReply reply)
    {
        var parsed = Read(JsonSerializer.SerializeToElement(reply.Text), reply.Sequence);
        (string Text, bool Truncated)? raw = reply.RawText is null ? null : CaptureRaw(JsonSerializer.SerializeToElement(reply.RawText));
        if (string.IsNullOrWhiteSpace(reply.Text) && reply.RawText is not null && !reply.RawTruncated)
        {
            var structured = Read(JsonSerializer.SerializeToElement(reply.RawText), reply.Sequence);
            if (structured.Format == "workiq_structured") parsed = structured;
        }
        return reply with
        {
            Text = parsed.Text,
            Format = parsed.Format is "workiq_response" or "workiq_structured" ? parsed.Format : reply.Format,
            IsError = reply.IsError || parsed.IsError,
            StatusCode = reply.StatusCode ?? parsed.StatusCode,
            ErrorCode = reply.ErrorCode ?? parsed.ErrorCode,
            Truncated = reply.Truncated || parsed.Truncated,
            RawText = raw?.Text,
            RawTruncated = reply.RawTruncated || (raw?.Truncated ?? false)
        };
    }

    public static WorkIqToolReply Read(JsonElement output, int sequence)
    {
        var texts = new List<string>();
        bool error = false, truncated = false;
        int? status = null;
        string? code = null;
        int visited = 0;
        bool responseFound = false, envelopeFound = false;
        bool structuredFound = false;
        int structuredResults = 0, structuredRecords = 0;

        void Text(string value)
        {
            string safe = Redact(value).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (texts.Contains(safe, StringComparer.Ordinal)) return;
            if (texts.Sum(t => t.Length) >= MaximumText) { truncated = true; return; }
            int remaining = MaximumText - texts.Sum(t => t.Length);
            if (safe.Length > remaining) { safe = safe[..remaining]; truncated = true; }
            if (!string.IsNullOrWhiteSpace(safe)) texts.Add(safe);
        }

        void Visit(JsonElement value, int depth)
        {
            if (depth > 8 || visited++ >= 64) { truncated = true; return; }
            if (value.ValueKind == JsonValueKind.String)
            {
                string raw = value.GetString()!;
                if (raw.Length > MaximumPayload) { truncated = true; return; }
                try
                {
                    using var nested = JsonDocument.Parse(raw);
                    // Do not repeatedly parse a JSON string containing itself.
                    if (nested.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    { Visit(nested.RootElement, depth + 1); return; }
                }
                catch (JsonException) { }
                // Some ask replies append a structured answer to the same human-readable text.
                int candidates = 0;
                for (int start = raw.IndexOf('{'); start >= 0 && candidates++ < 16; start = raw.IndexOf('{', start + 1))
                {
                    try
                    {
                        using var suffix = JsonDocument.Parse(raw.AsMemory(start));
                        if (!HasStructuredAnswer(suffix.RootElement)) continue;
                        Text(raw[..start]);
                        Visit(suffix.RootElement, depth + 1);
                        return;
                    }
                    catch (JsonException) { }
                }
                Text(raw);
                return;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in value.EnumerateArray().Take(16)) Visit(child, depth + 1);
                if (value.GetArrayLength() > 16) truncated = true;
                return;
            }
            if (value.ValueKind != JsonValueKind.Object) return;
            envelopeFound = true;
            if (value.TryGetProperty("results", out var structuredResultsValue) &&
                structuredResultsValue.ValueKind == JsonValueKind.Array)
                structuredFound = true;
            if (value.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True) error = true;
            if (value.TryGetProperty("statusCode", out var http) && http.TryGetInt32Safe(out int number) && number is >= 100 and <= 599)
            {
                if (number >= 400) { error = true; if (status is null or < 400) status = number; }
                else status ??= number;
                if (value.TryGetProperty("data", out var data))
                {
                    structuredFound = true;
                    structuredResults++;
                    if (number is >= 200 and < 300 && data.ValueKind == JsonValueKind.Object)
                        structuredRecords += data.TryGetProperty("value", out var records) && records.ValueKind == JsonValueKind.Array
                            ? records.GetArrayLength() : 1;
                }
            }
            if (value.TryGetProperty("error", out var serviceError) && serviceError.ValueKind == JsonValueKind.Object)
            {
                error = true;
                if (serviceError.TryGetProperty("statusCode", out var nestedHttp) &&
                    nestedHttp.TryGetInt32Safe(out int nestedStatus) && nestedStatus is >= 400 and <= 599)
                    if (status is null or < 400) status = nestedStatus;
                if ((serviceError.TryGetProperty("code", out var errorCode) || serviceError.TryGetProperty("type", out errorCode)) &&
                    errorCode.ValueKind == JsonValueKind.String)
                {
                    var candidate = errorCode.GetString()!;
                    if (candidate.Length <= 80 && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                        code ??= candidate;
                }
                if (serviceError.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    Text(message.GetString()!);
                else if (serviceError.TryGetProperty("content", out var errorContent))
                    Visit(errorContent, depth + 1);
            }
            if (value.TryGetProperty("response", out var response))
            {
                int before = texts.Count;
                Visit(response, depth + 1);
                responseFound |= texts.Count > before;
            }
            if (HasStructuredAnswer(value))
            {
                responseFound = true;
                Text(value.GetProperty("structuredResponse").GetProperty("answer").GetString()!);
            }
            // Only interpret documented text/envelope paths, never credential or conversation fields.
            if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                value.TryGetProperty("text", out var text))
                Visit(text, depth + 1);
            foreach (string property in new[] { "content", "structuredContent", "structuredResponse", "result", "results" })
                if (value.TryGetProperty(property, out var child)) Visit(child, depth + 1);
        }

        Visit(output, 0);
        if (structuredFound && texts.Count == 0)
            Text($"構造化応答：プレビューで確認した取得先 {structuredResults}件、返却レコード {structuredRecords}件。" +
                "日報の採用件数とは異なります。内容は下の受信JSONを確認してください。");
        var captured = CaptureRaw(output);
        return new(sequence, responseFound ? "workiq_response" : structuredFound ? "workiq_structured" : texts.Count > 0 ? "mcp_text" :
            envelopeFound ? "unrecognized_envelope" : "empty", error, status, code,
            string.Join("\n\n", texts.Distinct(StringComparer.Ordinal)), truncated, captured.Text, captured.Truncated);
    }

    public static WorkIqToolReply ReadCall(JsonElement item, int sequence)
    {
        item.TryGetProperty("output", out var output);
        item.TryGetProperty("error", out var error);
        var reply = Read(output, sequence);
        var status = GraphCollector.S(item, "status");
        bool failed = status is "failed" or "incomplete" ||
            (error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
             !(error.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(error.GetString())));
        if (!failed) return reply;
        // Keep just the result and failure, never copy the call arguments into diagnostics.
        var envelope = JsonSerializer.SerializeToElement(new
        {
            output = output.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : output,
            error = error.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : error,
            status
        }, Json.Options);
        var outer = Read(envelope, sequence);
        string errorText = error.ValueKind == JsonValueKind.String
            ? Read(JsonSerializer.SerializeToElement(new { response = error.GetString() }), sequence).Text
            : outer.Text;
        return reply with
        {
            IsError = true,
            Format = reply.Format == "empty" ? "mcp_error" : reply.Format,
            StatusCode = reply.StatusCode ?? outer.StatusCode,
            ErrorCode = reply.ErrorCode ?? outer.ErrorCode,
            Text = reply.Text.Length > 0 ? reply.Text : errorText,
            RawText = outer.RawText,
            RawTruncated = outer.RawTruncated,
            Truncated = reply.Truncated || outer.Truncated
        };
    }

    private static (string Text, bool Truncated) CaptureRaw(JsonElement output)
    {
        const int limit = 65536;
        bool truncated = false;
        int visited = 0;
        object? Sanitize(JsonElement value, int depth)
        {
            if (depth > 16 || ++visited > 4096) { truncated = true; return "[深さ・項目数上限で省略]"; }
            if (value.ValueKind == JsonValueKind.Object)
            {
                var fields = new Dictionary<string, object?>();
                foreach (var p in value.EnumerateObject())
                    fields[p.Name] = Regex.IsMatch(p.Name, @"(?i)token|secret|password|credential|authorization|cookie|conversation_?id|api.?key|headers")
                        ? "[除去]" : Sanitize(p.Value, depth + 1);
                return fields;
            }
            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Select(v => Sanitize(v, depth + 1)).ToArray();
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    if (nested.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        return JsonSerializer.Serialize(Sanitize(nested.RootElement, depth + 1), Json.Options);
                }
                catch (JsonException) { }
                return Redact(text);
            }
            return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : value.Clone();
        }
        if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return ("", false);
        string original = output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText();
        if (original.Length > MaximumPayload) return ("[受信応答が保存上限を超えたため省略]", true);
        var safe = Sanitize(output, 0);
        string text = safe as string ?? JsonSerializer.Serialize(safe, Json.Options);
        if (text.Length > limit) { text = text[..limit]; truncated = true; }
        return (text, truncated);
    }

    private static bool HasStructuredAnswer(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("structuredResponse", out var structured) && structured.ValueKind == JsonValueKind.Object &&
        structured.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String;

    private static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    public static string Redact(string text)
    {
        string safe = Regex.Replace(text, @"(?i)\bBearer\s+[^\s""<>]+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
            "[認証情報を除去]", RegexOptions.None, TimeSpan.FromSeconds(1));
        safe = Regex.Replace(safe, @"(?i)\b(access_token|refresh_token|client_secret|authorization_code)[""']?\s*[:=]\s*[""']?[^""'\s,}]+",
            "$1=[除去]", RegexOptions.None, TimeSpan.FromSeconds(1));
        safe = Regex.Replace(safe, @"(?i)\bconversation_?id[""']?\s*[:=]\s*[""']?[^""'\s,}]+",
            "conversationId=[除去]", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(safe, @"https?://[^\s<>""']+", match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[URLを除去]";
            if (uri.UserInfo.Length > 0 || uri.Host.EndsWith("microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Contains(".consent.", StringComparison.OrdinalIgnoreCase)) return "[認証URLを除去]";
            return uri.Query.Length > 0 || uri.Fragment.Length > 0 ? uri.GetLeftPart(UriPartial.Path) + "?[queryを除去]" : match.Value;
        }, RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}

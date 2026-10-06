using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public static class VerifiedWorkIqParser
{
    public const int MaximumEnvelopeBytes = 2 * 1024 * 1024;
    public const int ConservativeMessagePageLimit = 10;

    public static JsonElement Envelope(JsonElement output)
    {
        string text = OriginalText(output);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 48 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw WorkIqReadPlan.Error("workiq_envelope_invalid");
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw WorkIqReadPlan.Error("workiq_envelope_invalid"); }
    }

    private static string OriginalText(JsonElement output)
    {
        if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw WorkIqReadPlan.Error("workiq_output_missing");
        string text = output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaximumEnvelopeBytes)
            throw WorkIqReadPlan.Error("workiq_envelope_too_large");
        return text;
    }

    private static JsonElement KnownAskEnvelope(string original)
    {
        try
        {
            using var document = JsonDocument.Parse(original, new JsonDocumentOptions { MaxDepth = 48 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
        }
        catch (JsonException) { }
        int candidates = 0;
        for (int start = original.IndexOf('{'); start >= 0 && candidates++ < 16; start = original.IndexOf('{', start + 1))
        {
            try
            {
                using var suffix = JsonDocument.Parse(original.AsMemory(start), new JsonDocumentOptions { MaxDepth = 48 });
                if (suffix.RootElement.ValueKind == JsonValueKind.Object &&
                    new[] { "structuredResponse", "structuredContent", "isError", "error", "errorCode" }
                        .Any(name => suffix.RootElement.TryGetProperty(name, out _)))
                    return suffix.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return default;
    }

    public static void Process(RunState state, WorkIqReadTask task, JsonElement output, int evidenceBudget)
    {
        // Ask can be prose followed by a structured trailer; fetch requires its strict JSON envelope.
        if (task.ToolName == "ask")
        {
            string original = OriginalText(output);
            var askEnvelope = KnownAskEnvelope(original);
            var reply = WorkIqToolReplyReader.Read(output, 0);
            if (HasServiceError(askEnvelope) || reply.IsError ||
                Regex.IsMatch(original, @"(?i)\b(InternalError|AccessDenied|Forbidden|unauthorized|not authorized|access denied|policy denied|permission denied|insufficient permissions|authentication failed|authorization failed)\b|認証失敗|アクセス拒否|権限不足"))
                throw WorkIqReadPlan.Error("workiq_tool_failed");
            if (string.IsNullOrWhiteSpace(reply.Text) &&
                !(askEnvelope.ValueKind == JsonValueKind.Object &&
                  askEnvelope.TryGetProperty("response", out var answer) && answer.ValueKind == JsonValueKind.String &&
                  !string.IsNullOrWhiteSpace(answer.GetString())))
                throw WorkIqReadPlan.Error("workiq_ask_output_invalid");
            state.Source("chat").Increment("workiq.askResponses");
            // Prose (including embedded JSON) is a candidate only, never original message proof.
            state.Source("chat").Increment("workiq.metadataVerificationRequired");
            var candidateChats = CandidateChats(output).Concat(CandidateChats(askEnvelope))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            state.Source("chat").Increment("workiq.askCandidates", candidateChats.Length);
            if (candidateChats.Length > 0)
            {
                state.Source("chat").AcquisitionPath = "ask→fetch (source verification; ask claims unverified)";
                state.Incomplete("chat", "bounded_ask_candidates");
                WorkIqReadPlan.Fetch(state, "chat-messages", "chat",
                    candidateChats.Take(3).Select(id => WorkIqReadPlan.ChatMessagesPath(id, state.Period.PreviousBusinessDate!.Value)));
                return;
            }
            state.Source("chat").Increment("workiq.fallback");
            state.Source("chat").AcquisitionPath = "ask→fetch (structured fallback; ask match unverified)";
            state.CollectorFallbackObserved = true;
            WorkIqReadPlan.Fetch(state, "chats", "chat", ["/me/chats?$select=id,chatType&$top=3"]);
            return;
        }
        // Parse the complete original payload before the separate, redacted raw preview.
        var envelope = Envelope(output);
        if (!TryResults(envelope, out var results))
            throw WorkIqReadPlan.Error(HasServiceError(envelope) ? "workiq_tool_failed" : "workiq_results_missing");
        var paths = WorkIqReadPlan.Paths(task);
        if (results.GetArrayLength() != paths.Length)
            throw WorkIqReadPlan.Error("workiq_results_mismatch");
        var followups = new List<string>();
        int observedRows = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            var result = results[i];
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("statusCode", out var status) || status.ValueKind != JsonValueKind.Number ||
                !status.TryGetInt32(out int httpStatus) || httpStatus is < 100 or > 599)
            {
                Fail(state, task, "workiq_status_missing", 502);
                continue;
            }
            string? returnedPath = new[] { "entityUrl", "url", "path" }.Select(key => GraphCollector.S(result, key))
                .FirstOrDefault(path => !string.IsNullOrEmpty(path));
            if (returnedPath is not null && returnedPath != paths[i])
            {
                state.Source(task.Source).Increment("workiq.mismatch");
                Fail(state, task, "workiq_path_mismatch", 502);
                continue;
            }
            if (httpStatus is < 200 or >= 300 || HasServiceError(result))
            {
                Fail(state, task, "workiq_path_failed", httpStatus is >= 400 ? httpStatus : 502, ServiceCode(result));
                continue;
            }
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                Fail(state, task, "workiq_data_missing", 502);
                continue;
            }
            if (task.Purpose == "identity")
            {
                if (!Guid.TryParse(GraphCollector.S(data, "id"), out var actual) ||
                    !Guid.TryParse(state.SubjectId, out var expected) || actual != expected)
                    throw WorkIqReadPlan.Error("workiq_identity_mismatch");
                state.WorkIqIdentityVerified = true;
                WorkIqReadPlan.AfterIdentity(state);
                continue;
            }
            if (!data.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                rows.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Object))
            {
                Fail(state, task, "workiq_collection_invalid", 502);
                continue;
            }
            var source = state.Source(task.Source);
            source.Pages++;
            observedRows += rows.GetArrayLength();
            if (task.Purpose is "chat-messages" or "channel-posts" or "channel-replies" &&
                rows.GetArrayLength() >= ConservativeMessagePageLimit)
            {
                source.Increment("workiq.pageLimitReached");
                state.Incomplete(task.Source, "bounded_message_page_at_limit");
            }
            if (data.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString()))
            {
                source.Increment("workiq.nextPageRemaining");
                state.Incomplete(task.Source, "bounded_page_incomplete");
            }
            foreach (var row in rows.EnumerateArray())
            {
                string? segment = WorkIqReadPlan.Segment(row);
                switch (task.Purpose)
                {
                    case "calendar":
                        Calendar(state, task, row, i == 0 ? "activity" : "schedule", evidenceBudget);
                        break;
                    case "chats":
                        Discover(state, task, segment, followups, id =>
                            WorkIqReadPlan.ChatMessagesPath(id, state.Period.PreviousBusinessDate!.Value), 3);
                        break;
                    case "teams":
                        Discover(state, task, segment, followups, id => $"/teams/{id}/channels?$select=id,displayName,membershipType", 2);
                        break;
                    case "channels":
                        Discover(state, task, segment, followups, id =>
                            $"{paths[i].Split('?')[0]}/{id}/messages?$top=50", 3);
                        break;
                    case "chat-messages":
                    case "channel-posts":
                    case "channel-replies":
                        Message(state, task, row, paths[i].Split('?')[0], evidenceBudget);
                        if (task.Purpose == "channel-posts" && GraphCollector.S(row, "messageType") == "message")
                            Discover(state, task, segment, followups, id =>
                                $"{paths[i].Split('?')[0]}/{id}/replies?$top=50", 5);
                        break;
                    default:
                        throw WorkIqReadPlan.Error("collector_task_invalid");
                }

            }
        }
        if (task.Purpose == "identity" && !state.WorkIqIdentityVerified)
            throw WorkIqReadPlan.Error("workiq_identity_unavailable");
        int discoveryLimit = task.Purpose switch { "chats" => 3, "teams" => 2, "channels" => 3, _ => 0 };
        if ((discoveryLimit > 0 && observedRows >= discoveryLimit) ||
            (task.Purpose == "channel-posts" && followups.Count >= WorkIqReadPlan.MaximumPaths))
        {
            state.Source(task.Source).Increment("workiq.discoveryLimitReached");
            state.Incomplete(task.Source, "bounded_discovery_at_limit");
        }
        string? nextPurpose = task.Purpose switch
        {
            "chats" => "chat-messages", "teams" => "channels",
            "channels" => "channel-posts", "channel-posts" => "channel-replies", _ => null
        };
        if (nextPurpose is not null && followups.Count > 0)
            WorkIqReadPlan.Fetch(state, nextPurpose, task.Source, followups.Distinct(StringComparer.Ordinal));
    }

    public static string[] CandidateChats(JsonElement envelope)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int visited = 0;
        void Visit(JsonElement value, int depth)
        {
            if (depth > 16 || ++visited > 4096) return;
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) Visit(property.Value, depth + 1);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item, depth + 1);
            else if (value.ValueKind == JsonValueKind.String)
            {
                foreach (Match match in Regex.Matches(value.GetString()!, @"https://[^\s<>""\]\)]+"))
                {
                    if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
                        uri.Host is not ("teams.microsoft.com" or "teams.cloud.microsoft")) continue;
                    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length < 4 || segments[0] != "l" || segments[1] != "message") continue;
                    string id = Uri.UnescapeDataString(segments[2]);
                    if (!id.EndsWith("@thread.v2", StringComparison.Ordinal) ||
                        id.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%') || id.Length > 512) continue;
                    ids.Add(Uri.EscapeDataString(id));
                }
            }
        }
        Visit(envelope, 0);
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    private static void Discover(RunState state, WorkIqReadTask task, string? id, List<string> paths,
        Func<string, string> path, int maximum)
    {
        if (id is null)
        {
            state.Source(task.Source).Increment("workiq.metadataRejected");
            state.Incomplete(task.Source, "discovery_id_missing");
            return;
        }
        string target = path(id);
        if (paths.Contains(target, StringComparer.Ordinal)) return;
        if (paths.Count >= maximum)
        {
            state.Source(task.Source).Increment("workiq.discoveryExcluded");
            state.Incomplete(task.Source, "bounded_discovery_incomplete");
            return;
        }
        paths.Add(target);
    }

    public static void Fail(RunState state, WorkIqReadTask task, string code, int status = 502, string? serviceCode = null)
    {
        state.Incomplete(task.Source, code, true);
        state.Source(task.Source).Failure("workiq." + task.Purpose, status, serviceCode ?? code);
    }

    private static string? ServiceCode(JsonElement result)
    {
        var value = result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error : result;
        string code = GraphCollector.S(value, "code");
        if (code.Length == 0) code = GraphCollector.S(value, "errorCode");
        return code.Length is > 0 and <= 80 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            ? code : null;
    }

    private static bool TryResults(JsonElement envelope, out JsonElement results)
    {
        results = default;
        foreach (string wrapper in new[] { "structuredContent", "structuredResponse" })
        {
            if (!envelope.TryGetProperty(wrapper, out var structured) || structured.ValueKind != JsonValueKind.Object ||
                !structured.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            if (results.ValueKind == JsonValueKind.Array && !JsonElement.DeepEquals(results, rows))
                throw WorkIqReadPlan.Error("workiq_results_mismatch");
            results = rows;
        }
        if (results.ValueKind == JsonValueKind.Array) return true;
        // Some MCP servers return the identical envelope through content.text only.
        if (envelope.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || GraphCollector.S(item, "type") != "text" ||
                    GraphCollector.S(item, "text") is not { } text) continue;
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    if (nested.RootElement.ValueKind == JsonValueKind.Object &&
                        nested.RootElement.TryGetProperty("results", out var rows) && rows.ValueKind == JsonValueKind.Array)
                    { results = rows.Clone(); return true; }
                }
                catch (JsonException) { }
            }
        }
        return false;
    }

    private static bool HasServiceError(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True) return true;
        if (value.TryGetProperty("errorCode", out var serviceCode) && serviceCode.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(serviceCode.GetString())) return true;
        if (value.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return true;
        if (value.TryGetProperty("statusCode", out var status) && status.ValueKind == JsonValueKind.Number &&
            status.TryGetInt32(out int number) && number >= 400) return true;
        return (value.TryGetProperty("structuredContent", out var child) && HasServiceError(child)) ||
            (value.TryGetProperty("structuredResponse", out var response) && HasServiceError(response));
    }

    private static bool Observe(RunState state, WorkIqReadTask task, JsonElement row, string scope, string category)
    {
        string? id = GraphCollector.S(row, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            Reject(state, task, "workiq.metadataRejected", "source_id_missing");
            return false;
        }
        string key = GenerationSpec.Hash(Encoding.UTF8.GetBytes($"verified\0{task.Source}\0{scope}\0{id}\0{category}"));
        if (!state.Visited.Add(key))
        {
            state.Source(task.Source).Increment("duplicateEvidence");
            return false;
        }
        state.Source(task.Source).FetchedCount++;
        return true;
    }

    private static void Calendar(RunState state, WorkIqReadTask task, JsonElement row, string category, int budget)
    {
        if (!Observe(state, task, row, "calendar", category)) return;
        if (row.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
        {
            state.Source(task.Source).Increment("workiq.cancelledExcluded");
            return;
        }
        if (!row.TryGetProperty("start", out var start) || !row.TryGetProperty("end", out var end) ||
            !CalendarTime(start, out var from, out string precision) || !CalendarTime(end, out var until, out _) || until <= from)
        {
            Reject(state, task, "workiq.metadataRejected", "calendar_time_unverified");
            return;
        }
        var date = category == "activity" ? state.Period.PreviousBusinessDate!.Value : state.Period.ReportDate!.Value;
        var window = BusinessCalendar.StartUtc(date);
        if (from >= window.AddDays(1) || until <= window)
        {
            state.Source(task.Source).Increment("workiq.outOfPeriod");
            return;
        }
        string? subject = GraphCollector.S(row, "subject");
        if (string.IsNullOrWhiteSpace(subject))
        {
            Reject(state, task, "workiq.metadataRejected", "calendar_subject_missing");
            return;
        }
        bool? meeting = row.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array
            ? attendees.GetArrayLength() > 0 : null;
        bool allDay = row.TryGetProperty("isAllDay", out var day) && day.ValueKind == JsonValueKind.True;
        EvidenceNormalizer.Add(state, new("", "calendar", $"予定（参加・完了は未確認）: {subject}",
            EvidenceNormalizer.SafeUrl(GraphCollector.S(row, "webLink")), GraphCollector.S(row, "id"), from, state.SubjectId,
            category, until, meeting, AcquisitionTool: "fetch", VerificationStatus: "verified",
            TimestampPrecision: allDay ? "day" : precision, MessageKind: "calendar", ActivityStatus: "planned", TaskId: task.Id), budget);
    }

    private static void Message(RunState state, WorkIqReadTask task, JsonElement row, string scope, int budget)
    {
        if (!Observe(state, task, row, scope, "activity")) return;
        string? kind = GraphCollector.S(row, "messageType");
        if (kind == "systemEventMessage" ||
            (row.TryGetProperty("eventDetail", out var detail) && detail.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)))
        {
            state.Source(task.Source).Increment("workiq.excludedSystem");
            return;
        }
        if (kind != "message")
        {
            Reject(state, task, "workiq.unknownKind", "message_kind_unverified");
            return;
        }
        if (!ExactTime(GraphCollector.S(row, "createdDateTime"), out var timestamp, out string precision))
        {
            Reject(state, task, "workiq.metadataRejected", "message_time_unverified");
            return;
        }
        var start = BusinessCalendar.StartUtc(state.Period.PreviousBusinessDate!.Value);
        if (timestamp < start || timestamp >= start.AddDays(1) ||
            (precision == "minute" && timestamp.AddMinutes(1) > start.AddDays(1)))
        {
            state.Source(task.Source).Increment("workiq.outOfPeriod");
            return;
        }
        string? author = row.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object &&
            from.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                ? GraphCollector.S(user, "id") : null;
        if (!Guid.TryParse(author, out _) ||
            !row.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object ||
            GraphCollector.S(body, "content") is not { } rawText || string.IsNullOrWhiteSpace(rawText))
        {
            Reject(state, task, "workiq.metadataRejected", "message_author_or_content_unverified");
            return;
        }
        string text = GraphCollector.S(body, "contentType") == "html" ? EvidenceNormalizer.PlainText(rawText) : rawText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            Reject(state, task, "workiq.metadataRejected", "message_content_empty");
            return;
        }
        EvidenceNormalizer.Add(state, new("", task.Source,
            "発言原文（業務状態は未分類。実施済み成果と断定しない）: " + text,
            EvidenceNormalizer.SafeUrl(GraphCollector.S(row, "webUrl")), GraphCollector.S(row, "id"),
            timestamp, author, "activity", AcquisitionTool: "fetch", VerificationStatus: "verified",
            TimestampPrecision: precision, MessageKind: "ordinary", ActivityStatus: "unknown", TaskId: task.Id,
            SourceScope: scope), budget);
    }

    private static void Reject(RunState state, WorkIqReadTask task, string diagnostic, string reason)
    {
        state.Source(task.Source).Increment(diagnostic);
        state.Incomplete(task.Source, reason);
    }

    public static bool ExactTime(string? text, out DateTimeOffset value, out string precision)
    {
        value = default;
        precision = "second";
        if (text is null || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})$"))
            return false;
        precision = Regex.IsMatch(text, @"T\d{2}:\d{2}(Z|[+-])") ? "minute" : "second";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static bool CalendarTime(JsonElement input, out DateTimeOffset value, out string precision)
    {
        value = default; precision = "second";
        if (input.ValueKind != JsonValueKind.Object) return false;
        string? text = GraphCollector.S(input, "dateTime");
        if (ExactTime(text, out value, out precision)) return true;
        if (text is null || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?$") ||
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ||
            GraphCollector.S(input, "timeZone") is not { Length: > 0 } zone) return false;
        precision = text.Length == 16 ? "minute" : "second";
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local)) return false;
            value = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}

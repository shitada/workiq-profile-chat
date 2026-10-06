using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text;

namespace GraphReportChat.Api;

public sealed class GraphCollector(GraphTransport graph, Settings settings, GenerationSpec spec, BusinessCalendar calendar)
{
    private const string Root = GraphTransport.Root;
    private int EvidenceBudget => Math.Max(1000, spec.Config.InputTokenBudget - 8000);
    public void Initialize(RunState state)
    {
        if (state.Period.Kind != "daily")
        {
            state.Source("sharePoint");
            if (!settings.SharePointConfigured)
            {
                state.Incomplete("sharePoint", "report_folder_not_configured", true);
                return;
            }
            state.Queue.Add(new("files", Folder() + "/children?$top=50&$expand=listItem($expand=fields)", "sharePoint"));
            return;
        }
        foreach (var category in new[] { "activity", "schedule" })
        {
            var day = category == "activity" ? state.Period.PreviousBusinessDate!.Value : state.Period.ReportDate!.Value;
            var start = Uri.EscapeDataString(BusinessCalendar.StartUtc(day).ToString("O"));
            var end = Uri.EscapeDataString(BusinessCalendar.StartUtc(day.AddDays(1)).ToString("O"));
            state.Queue.Add(new("calendar", $"{Root}/me/calendarView?startDateTime={start}&endDateTime={end}&$top=50", "calendar", Category: category));
        }
        state.Queue.Add(new("chats", $"{Root}/me/chats?$top=50", "chat"));
        state.Queue.Add(new("teams", $"{Root}/me/joinedTeams", "channel"));
        state.Queue.Add(new("associatedTeams", $"{Root}/me/teamwork/associatedTeams", "channel"));
        foreach (var type in new[] { "calendar", "chat", "channel", "transcript" }) state.Source(type);
    }
    public async Task Collect(RunState state, string token, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
        slice.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            for (int i = 0; i < 8 && state.Queue.Count > 0; i++)
            {
                if (state.NextAttemptAt > DateTimeOffset.UtcNow) break;
                var work = state.Queue[0];
                try
                {
                    if (state.Metrics.GraphCalls >= spec.Config.MaxGraphCalls)
                    {
                        foreach (var remaining in state.Queue) state.Incomplete(remaining.SourceType, "api_budget_exhausted");
                        state.Queue.Clear();
                        break;
                    }
                    state.Source(work.SourceType).Increment($"{work.Kind}.requests");
                    var payload = await graph.Send(work.Url, token, state, spec.Config.MaxGraphCalls, slice.Token, text: work.Kind == "transcriptContent");
                    state.NextAttemptAt = null;
                    state.DeferredCount = 0;
                    state.Queue.RemoveAt(0);
                    state.Source(work.SourceType).Pages++;
                    state.Source(work.SourceType).Increment($"{work.Kind}.pages");
                    state.Source(work.SourceType).Increment($"{work.Kind}.http_{(int)payload.Status}");
                    Process(state, work, payload.Body);
                }
                catch (GraphDeferred)
                {
                    state.DeferredCount++;
                    if (state.DeferredCount >= 4)
                    {
                        state.Incomplete(work.SourceType, "retry_exhausted");
                        state.Queue.RemoveAt(0);
                        state.DeferredCount = 0;
                    }
                    break;
                }
                catch (GraphFailure error)
                {
                    state.Source(work.SourceType).Failure(work.Kind, error.Status, error.GraphCode);
                    state.Incomplete(work.SourceType, work.SourceType == "transcript"
                        ? $"{work.Kind}:{error.Message}" : error.Message, true);
                    if (state.Queue.Count > 0 && state.Queue[0] == work) state.Queue.RemoveAt(0);
                }
                catch (JsonException)
                {
                    state.Incomplete(work.SourceType, "invalid_source_response", true);
                    // Process runs after dequeue; don't remove the next independent work item.
                    if (state.Queue.Count > 0 && state.Queue[0] == work) state.Queue.RemoveAt(0);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally { state.Metrics.CollectionMilliseconds += clock.ElapsedMilliseconds; }
        if (state.Queue.Count == 0)
        {
            if (state.Period.Kind != "daily") MarkMissingDays(state);
            else
            {
                FinalizeDailyDiagnostics(state);
                EvidenceNormalizer.CalendarMetrics(state);
            }
        }
    }

    private string Folder() => $"{Root}/drives/{Esc(settings.Required("Report:SharePointDriveId"))}/items/{Esc(settings.Required("Report:SharePointFolderId"))}";
    public static string Esc(string value) => Uri.EscapeDataString(value);
    private void Enqueue(RunState state, WorkItem work)
    {
        if (state.Visited.Add($"{work.Kind}:{work.Url}:{work.Category}"))
        {
            if (state.Queue.Count >= 1500) state.Incomplete(work.SourceType, "queue_budget_exhausted");
            else state.Queue.Add(work);
        }
    }
    private void Process(RunState state, WorkItem work, string body)
    {
        if (work.Kind is "transcriptContent" or "fileContent")
        {
            if (work.ContentHash is not null && GenerationSpec.Hash(Encoding.UTF8.GetBytes(body)) != work.ContentHash)
            {
                state.Incomplete("sharePoint", "saved_report_content_hash_mismatch");
                return;
            }
            var content = work.Kind == "transcriptContent" ? EvidenceNormalizer.Transcript(body) : body;
            if (string.IsNullOrWhiteSpace(content))
            {
                state.Source(work.SourceType).Increment($"{work.Kind}.emptyContent");
                if (work.Kind == "transcriptContent") state.Incomplete("transcript", "transcript_content_empty", true);
                return;
            }
            EvidenceNormalizer.Add(state, new("", work.SourceType, content, work.Context,
                work.Url, SubjectId: work.Subject, Category: work.Category), EvidenceBudget);
            return;
        }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (work.Kind == "fileMetadata")
        {
            ReadSidecar(state, root, work.Context!);
            if (state.Queue.All(w => w.Kind != "fileMetadata")) QueueDailyContents(state);
            return;
        }
        if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing value array.");
        state.Source(work.SourceType).Increment($"{work.Kind}.items", values.GetArrayLength());
        if (values.GetArrayLength() == 0) state.Source(work.SourceType).Increment($"{work.Kind}.emptyPages");
        if (root.TryGetProperty("@odata.nextLink", out var next) && next.GetString() is { Length: > 0 } link)
        {
            GraphTransport.ValidateUri(link);
            Enqueue(state, work with { Url = link });
        }
        if (work.Kind == "meetings" && values.GetArrayLength() == 0)
            state.Incomplete("transcript", "online_meeting_lookup_empty", true);
        if (work.Kind == "transcripts" && values.GetArrayLength() == 0)
            state.Incomplete("transcript", "transcript_list_empty_recording_or_retention_unknown", true);
        foreach (var item in values.EnumerateArray())
        {
            state.Source(work.SourceType).FetchedCount++;
            var id = S(item, "id");
            if (id.Length == 0) { state.Incomplete(work.SourceType, "source_id_missing"); continue; }
            switch (work.Kind)
            {
                case "calendar": Calendar(state, item, work.Category!); break;
                case "chats":
                    var lower = BusinessCalendar.StartUtc(state.Period.PreviousBusinessDate!.Value).AddTicks(-1).ToString("O");
                    var filter = Esc($"lastModifiedDateTime gt {lower}");
                    Enqueue(state, new("messages", $"{Root}/me/chats/{Esc(id)}/messages?$top=50&$orderby=lastModifiedDateTime desc&$filter={filter}", "chat", Category: "activity"));
                    break;
                case "teams":
                case "associatedTeams":
                    Enqueue(state, new("allChannels", $"{Root}/teams/{Esc(id)}/allChannels", "channel", Context: id)); break;
                case "channels":
                case "allChannels":
                    var address = ChannelAddress(item, work.Context!);
                    if (S(item, "membershipType") == "shared") state.Source("channel").Increment("sharedChannels.items");
                    if (address is null) { state.Incomplete("channel", "shared_channel_host_unresolved"); break; }
                    Enqueue(state, new("messages", $"{address}/messages?$top=50",
                        "channel", Context: address, Category: "activity")); break;
                case "messages":
                    Message(state, item, work);
                    if (work.SourceType == "channel" && work.Context is not null)
                        Enqueue(state, new("replies", $"{work.Context}/messages/{Esc(id)}/replies?$top=50", "channel", Category: "activity"));
                    break;
                case "replies": Message(state, item, work); break;
                case "meetings":
                    Enqueue(state, new("transcripts", $"{Root}/me/onlineMeetings/{Esc(id)}/transcripts", "transcript", Subject: state.SubjectId, Context: S(item, "joinWebUrl"))); break;
                case "transcripts":
                    var created = Timestamp(S(item, "createdDateTime"));
                    if (created is null)
                    { state.Incomplete("transcript", "transcript_timestamp_missing"); break; }
                    var activityDay = state.Period.PreviousBusinessDate!.Value;
                    if (created < BusinessCalendar.StartUtc(activityDay) || created >= BusinessCalendar.StartUtc(activityDay.AddDays(1)))
                    { state.Source("transcript").Increment("transcripts.periodExcluded"); break; }
                    var content = $"{work.Url.Split('?')[0]}/{Esc(id)}/content?$format=text/vtt";
                    Enqueue(state, new("transcriptContent", content, "transcript", work.Subject, work.Context, "activity")); break;
                case "files": File(state, item); break;
            }
        }
        if (work.Kind == "files" && state.Queue.All(w => w.Kind != "files"))
        {
            foreach (var metadata in state.MetadataFiles.Values)
                Enqueue(state, new("fileMetadata", FileContentUrl(metadata.Id), "sharePoint", Context: metadata.Name));
            if (state.MetadataFiles.Count == 0) QueueDailyContents(state);
        }
    }
    private void Calendar(RunState state, JsonElement item, string category)
    {
        if (item.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True) return;
        var start = EventTime(item, "start");
        var end = EventTime(item, "end");
        var date = category == "activity" ? state.Period.PreviousBusinessDate!.Value : state.Period.ReportDate!.Value;
        if (start is null || end is null) { state.Incomplete("calendar", "calendar_time_missing"); return; }
        if (end <= BusinessCalendar.StartUtc(date) || start >= BusinessCalendar.StartUtc(date.AddDays(1))) return;
        state.Source("calendar").Increment($"{category}.periodMatched");
        if (category == "activity")
        {
            state.Source("transcript").Increment("activityCalendar.events");
            if (item.TryGetProperty("isOnlineMeeting", out var declaredMeeting) && declaredMeeting.ValueKind == JsonValueKind.True)
                state.Source("transcript").Increment("activityCalendar.onlineMeetingsDeclared");
        }
        string content = item.TryGetProperty("body", out var body) ? S(body, "content") : S(item, "bodyPreview");
        var eventText = $"{S(item, "subject")}\n予定表の枠: {start:O}〜{end:O}（参加実績ではありません）\n{content}";
        bool isMeeting = (item.TryGetProperty("isOnlineMeeting", out var meetingFlag) && meetingFlag.ValueKind == JsonValueKind.True) ||
            (item.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array && attendees.GetArrayLength() > 0);
        EvidenceNormalizer.Add(state, new("", "calendar", eventText, S(item, "webLink"), S(item, "id"), start, state.SubjectId, category, end, isMeeting), EvidenceBudget);
        if (category == "activity" && item.TryGetProperty("onlineMeeting", out var online) && online.ValueKind == JsonValueKind.Object)
        {
            string join = S(online, "joinUrl");
            if (join.Length > 0)
            {
                state.Source("transcript").Increment("activityCalendar.joinUrlsFound");
                Enqueue(state, new("meetings", $"{Root}/me/onlineMeetings?$filter={Esc($"JoinWebUrl eq '{join.Replace("'", "''")}'")}", "transcript"));
            }
        }
    }
    public static DateTimeOffset? EventTime(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var part)) return null;
        var value = S(part, "dateTime");
        var zone = S(part, "timeZone");
        if (value.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d\d:\d\d$"))
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset) ? offset : null;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        if (zone is "Tokyo Standard Time" or "Asia/Tokyo") return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
        if (zone is "UTC" or "Etc/UTC") return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), TimeSpan.Zero);
        return null;
    }
    private void Message(RunState state, JsonElement item, WorkItem work)
    {
        var timestamp = Timestamp(S(item, "createdDateTime"));
        if (timestamp is null) { state.Source(work.SourceType).Increment("messages.missingTimestamp"); state.Incomplete(work.SourceType, "message_time_missing"); return; }
        var date = state.Period.PreviousBusinessDate!.Value;
        if (timestamp < BusinessCalendar.StartUtc(date) || timestamp >= BusinessCalendar.StartUtc(date.AddDays(1)))
        { state.Source(work.SourceType).Increment("messages.periodExcluded"); return; }
        state.Source(work.SourceType).Increment("messages.periodMatched");
        if (S(item, "deletedDateTime").Length > 0)
        { state.Source(work.SourceType).Increment("messages.deleted"); return; }
        var content = item.TryGetProperty("body", out var body) ? S(body, "content") : "";
        if (string.IsNullOrWhiteSpace(EvidenceNormalizer.PlainText(content)))
        { state.Source(work.SourceType).Increment("messages.emptyBody"); return; }
        string author = "", displayName = "";
        if (item.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object &&
            from.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        { author = S(user, "id"); displayName = S(user, "displayName"); }
        string attribution = EvidenceNormalizer.Attribution(author, state.SubjectId);
        state.Source(work.SourceType).Increment(attribution switch
        {
            "self" => "messages.selfAuthored",
            "other" => "messages.otherAuthored",
            _ => "messages.unknownAuthored"
        });
        string attributionLabel = attribution switch
        {
            "self" => "本人",
            "other" => "他者",
            _ => "不明（送信者Object ID未取得または判定不可）"
        };
        EvidenceNormalizer.Add(state, new("", work.SourceType,
            $"発言者: {displayName} ({author}); 発言者の帰属: {attributionLabel}\n{content}",
            S(item, "webUrl"), $"{work.Url.Split('?')[0]}/{S(item, "id")}", timestamp, author, "activity"), EvidenceBudget);
    }
    public static string? ChannelAddress(JsonElement channel, string listingTeam)
    {
        string id = S(channel, "id"), address = S(channel, "@odata.id");
        if (address.Length > 0)
        {
            var uri = GraphTransport.ValidateUri(address);
            // allChannels identifies the host team; remove the documented /tenants segment,
            // but only for an exact channel resource (never arbitrary Graph operations).
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,
                @"^/v1\.0/(?:tenants/[^/]+/)?teams/(?<team>[^/]+)/channels/(?<channel>[^/]+)$");
            if (!match.Success || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                Uri.UnescapeDataString(match.Groups["channel"].Value) != id) return null;
            return $"{Root}/teams/{Esc(Uri.UnescapeDataString(match.Groups["team"].Value))}/channels/{Esc(id)}";
        }
        return S(channel, "membershipType") == "shared" ? null : $"{Root}/teams/{Esc(listingTeam)}/channels/{Esc(id)}";
    }
    private static void FinalizeDailyDiagnostics(RunState state)
    {
        var chat = state.Source("chat");
        if (chat.CollectionStatus == "complete" && chat.RetrievedCount == 0 && chat.Count == 0)
        {
            chat.Reason = chat.Diagnostics.GetValueOrDefault("chats.pages") > 0 && chat.Diagnostics.GetValueOrDefault("chats.items") == 0
                ? "no_accessible_chats_returned"
                : chat.Diagnostics.GetValueOrDefault("messages.items") == 0 ? "no_messages_returned_for_query"
                : chat.Diagnostics.GetValueOrDefault("messages.periodMatched") == 0 ? "no_messages_in_activity_period"
                : "no_usable_message_content";
        }
        var transcript = state.Source("transcript");
        if (transcript.RetrievedCount > 0 || transcript.Count > 0 || transcript.Reason is not null) return;
        string reason = transcript.Diagnostics.GetValueOrDefault("meetings.requests") == 0 &&
            transcript.Diagnostics.GetValueOrDefault("transcripts.requests") == 0 &&
            transcript.Diagnostics.GetValueOrDefault("transcriptContent.requests") == 0
            ? state.Source("calendar").CollectionStatus == "complete"
                ? "no_online_meeting_join_urls_in_activity_calendar"
                : "calendar_incomplete_no_online_meeting_join_urls"
            : transcript.Diagnostics.GetValueOrDefault("transcripts.periodExcluded") > 0
                ? "no_transcripts_in_activity_period" : "no_usable_transcript_content";
        state.Incomplete("transcript", reason, true);
    }
    private void File(RunState state, JsonElement item)
    {
        if (item.TryGetProperty("folder", out _)) return;
        var descriptor = new FolderFile(S(item, "id"), S(item, "name"), S(item, "webUrl"));
        if (descriptor.Name.EndsWith(".md.report.json", StringComparison.OrdinalIgnoreCase))
        {
            state.MetadataFiles[descriptor.Id] = descriptor;
            return;
        }
        if (!descriptor.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return;
        state.FolderFiles[descriptor.Id] = descriptor;
        if (!item.TryGetProperty("listItem", out var list) || !list.TryGetProperty("fields", out var fields))
            return;
        SelectDaily(state, fields, descriptor);
    }
    private string FileContentUrl(string id) =>
        $"{Root}/drives/{Esc(settings.Required("Report:SharePointDriveId"))}/items/{Esc(id)}/content";
    private void ReadSidecar(RunState state, JsonElement fields, string metadataName)
    {
        if (S(fields, "schemaVersion") != "1" ||
            !state.FolderFiles.TryGetValue(S(fields, "contentItemId"), out var file) ||
            file.Name != S(fields, "contentFileName") || metadataName != file.Name + ".report.json" ||
            S(fields, "contentSha256").Length != 64)
        {
            state.Incomplete("sharePoint", "report_metadata_pair_invalid");
            return;
        }
        SelectDaily(state, fields, file, S(fields, "contentSha256"));
    }
    private void SelectDaily(RunState state, JsonElement fields, FolderFile file, string? contentHash = null)
    {
        var subject = S(fields, "subjectId").ToLowerInvariant();
        var members = state.Period.Kind == "managerWeekly" ? state.Members : [state.SubjectId];
        if (!members.Contains(subject, StringComparer.OrdinalIgnoreCase) ||
            S(fields, "reportType") != "daily" || S(fields, "status") != "confirmed" ||
            S(fields, "method") != state.Provider) return;
        if (!DateOnly.TryParseExact(S(fields, "reportDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var reportDate))
        { state.Incomplete("sharePoint", "report_date_metadata_invalid"); return; }
        if (reportDate < state.Period.StartDate || reportDate > state.Period.EndDate) return;
        var revision = long.TryParse(S(fields, "revision"), out var version) ? version : 0;
        var key = $"{subject}:{reportDate:yyyy-MM-dd}";
        var descriptor = new SavedDaily(subject, reportDate, S(fields, "activityDate"), revision,
            FileContentUrl(file.Id), file.WebUrl, S(fields, "runId"), file.Name, contentHash);
        if (!state.DailyFiles.TryGetValue(key, out var previous) || CompareRevision(descriptor, previous) > 0) state.DailyFiles[key] = descriptor;
    }
    private static int CompareRevision(SavedDaily left, SavedDaily right)
    {
        var comparison = left.Revision.CompareTo(right.Revision);
        return comparison != 0 ? comparison : string.Compare(left.FileName, right.FileName, StringComparison.Ordinal);
    }
    private void QueueDailyContents(RunState state)
    {
        // Called again when listing finishes, including empty final pages.
        foreach (var daily in state.DailyFiles.Values.OrderBy(d => d.ReportDate).ThenBy(d => d.SubjectId))
            Enqueue(state, new("fileContent", daily.ContentUrl, "sharePoint", daily.SubjectId, daily.WebUrl,
                $"reportDate={daily.ReportDate:yyyy-MM-dd};activityDate={daily.ActivityDate};status=confirmed", daily.ContentHash));
    }
    private void MarkMissingDays(RunState state)
    {
        if (state.DailyFiles.Count > 0 && !state.Visited.Any(v => v.StartsWith("fileContent:", StringComparison.Ordinal)))
        { QueueDailyContents(state); return; }
        var members = state.Period.Kind == "managerWeekly" ? state.Members : [state.SubjectId];
        foreach (var member in members)
            for (var day = state.Period.StartDate; day <= state.Period.EndDate; day = day.AddDays(1))
                if (calendar.IsBusinessDay(day) && !state.DailyFiles.ContainsKey($"{member}:{day:yyyy-MM-dd}"))
                    state.Incomplete("sharePoint", $"missing_daily:{member}:{day:yyyy-MM-dd}");
        if (state.Source("sharePoint").Count == 0) state.Incomplete("sharePoint", "no_confirmed_daily_reports", true);
    }
    public static string S(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind is JsonValueKind.String ? value.GetString() ?? "" :
              value.ValueKind is JsonValueKind.Number ? value.GetRawText() : "" : "";
    private static DateTimeOffset? Timestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : null;
}

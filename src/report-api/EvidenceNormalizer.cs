using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public static class EvidenceNormalizer
{
    public const string SelectionVersion = "balanced-excerpts-v2-o200k";
    private static int Tokens(Evidence evidence) => evidence.TextTokenCount > 0 ? evidence.TextTokenCount : InputTokens.Count(evidence.Text);
    public static string Attribution(string? sender, string subject) =>
        Guid.TryParse(sender, out var senderId) && Guid.TryParse(subject, out var subjectId)
            ? senderId == subjectId ? "self" : "other"
            : "unknown";
    public static string GenerationInput(RunState state) => JsonSerializer.Serialize(new
    {
        period = state.Period,
        reportKind = state.Period.Kind, state.Period.ReportDate, state.Period.StartDate, state.Period.EndDate,
        state.Period.PreviousBusinessDate, timeZone = "Asia/Tokyo", subjectId = state.SubjectId,
        members = state.Members, userRequest = state.Message, existingDraft = state.Draft,
        evidence = state.Evidence,
        coverage = state.Coverage.Select(c => new { c.SourceType, c.Status, c.Count, c.Reason }),
        scheduledMeetingCount = state.Metrics.CalendarMeetingCount,
        scheduledMeetingMinutes = state.Metrics.ScheduledMeetingMinutes
    }, Json.Options);

    public static void ApplyBudget(RunState state, GenerationSpec spec)
    {
        int Size() => InputTokens.Count(GenerationInput(state)) + InputTokens.Count(spec.Instructions) + 1000;
        int Rank(Evidence item) => item.SourceType switch { "sharePoint" => 0, "calendar" => 1, "transcript" => 2, "chat" => 3, _ => 4 };
        state.Evidence = state.Evidence.OrderBy(Rank).ThenBy(e => e.Timestamp).ThenBy(e => e.SourceId, StringComparer.Ordinal).ToList();
        int size = Size();
        while (size > spec.Config.InputTokenBudget && state.Evidence.Count > 0)
        {
            ReduceLargestSource(state);
            size = Size();
        }
        if (size > spec.Config.InputTokenBudget) throw new ApiException(422, "input_budget_exceeded", "指示または編集案が共通入力上限を超えています。短くしてください。");
        state.Metrics.SourceCharacters = state.Evidence.Sum(e => e.Text.Length);
        state.Metrics.StagedTokens = state.Evidence.Sum(Tokens);
        state.Metrics.InputTokenEstimate = size;
        state.Metrics.InputSelectionVersion = SelectionVersion;
        state.Metrics.InputByteUpperBound = Encoding.UTF8.GetByteCount(GenerationInput(state)) + Encoding.UTF8.GetByteCount(spec.Instructions) + 1000;
        foreach (var source in state.Coverage)
        {
            source.Count = state.Evidence.Count(e => e.SourceType == source.SourceType);
            source.SentCount = source.Count;
        }
    }
    public static string PlainText(string text) => WebUtility.HtmlDecode(
        Regex.Replace(Regex.Replace(text, @"<(script|style)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase),
            "<[^>]+>", " ")).Trim();
    public static string Transcript(string text) => PlainText(Regex.Replace(text,
        @"(?m)^(WEBVTT.*|\d+|\d{2}:\d{2}:\d{2}[\.,]\d{3}\s+-->.*)\r?$", "")).Trim();

    public static bool Add(RunState state, Evidence evidence, int budget)
    {
        var source = state.Source(evidence.SourceType);
        string identity = GenerationSpec.Hash(Encoding.UTF8.GetBytes($"{evidence.SourceType}\0{evidence.SourceId}\0{evidence.Category}" +
            (evidence.SourceScope is null ? "" : $"\0{evidence.SourceScope}")));
        if (!state.SeenEvidence.Add(identity) ||
            state.Evidence.Any(e => e.SourceType == evidence.SourceType && e.SourceId == evidence.SourceId &&
                e.Category == evidence.Category && e.SourceScope == evidence.SourceScope))
        { source.Increment("duplicateEvidence"); return false; }
        var cleaned = evidence with
        {
            Text = evidence.VerificationStatus == "verified" ? evidence.Text.Trim() : PlainText(evidence.Text),
            Url = SafeUrl(evidence.Url),
            Attribution = evidence.SourceType is "chat" or "channel" ? Attribution(evidence.SubjectId, state.SubjectId) : null
        };
        if (cleaned.Text.Length == 0) return false;
        source.RetrievedCount++;
        int perItem = Math.Max(128, budget / 16);
        if (InputTokens.Count(cleaned.Text) > perItem)
        {
            cleaned = cleaned with { Text = InputTokens.Excerpt(cleaned.Text, perItem), Truncated = true };
            source.TruncatedCount++;
            state.Metrics.TruncatedEvidence++;
            state.InputIncomplete(cleaned.SourceType, "evidence_excerpt_truncated");
        }
        cleaned = cleaned with { TextTokenCount = InputTokens.Count(cleaned.Text) };
        if (state.NextEvidenceNumber == 0 && state.Evidence.Count > 0)
            state.NextEvidenceNumber = state.Evidence.Select(e => int.TryParse(e.Id.AsSpan(1), out var n) ? n : 0).Max();
        string id = $"E{++state.NextEvidenceNumber:D5}";
        state.Evidence.Add(cleaned with { Id = id });
        source.Count++;
        if (source.Status == "unavailable") source.Status = "partial";
        if (source.CollectionStatus == "unavailable") source.CollectionStatus = "partial";
        while (state.Evidence.Sum(Tokens) > budget || state.Evidence.Count > 256)
            ReduceLargestSource(state);
        state.Metrics.SourceCharacters = state.Evidence.Sum(e => e.Text.Length);
        state.Metrics.StagedTokens = state.Evidence.Sum(Tokens);
        return state.Evidence.Any(e => e.Id == id);
    }
    private static void ReduceLargestSource(RunState state)
    {
        var group = state.Evidence.GroupBy(e => e.SourceType)
            .OrderByDescending(g => g.Sum(Tokens)).ThenBy(g => g.Key, StringComparer.Ordinal).First();
        var victim = group.OrderByDescending(Tokens)
            .ThenBy(e => e.SubjectId == state.SubjectId ? 1 : 0).ThenByDescending(e => e.Timestamp).First();
        int tokens = Tokens(victim), index = state.Evidence.IndexOf(victim);
        if (tokens > 256 && state.Evidence.Count <= 256)
        {
            var text = InputTokens.Excerpt(victim.Text, tokens / 2);
            state.Evidence[index] = victim with { Text = text, Truncated = true, TextTokenCount = InputTokens.Count(text) };
            if (!victim.Truncated) { state.Source(victim.SourceType).TruncatedCount++; state.Metrics.TruncatedEvidence++; }
            state.InputIncomplete(victim.SourceType, "evidence_excerpt_truncated");
        }
        else
        {
            state.Evidence.RemoveAt(index);
            state.Source(victim.SourceType).Count--;
            state.Source(victim.SourceType).InputDroppedCount++;
            state.Metrics.DroppedEvidence++;
            state.InputIncomplete(victim.SourceType, "input_budget_exhausted");
        }
    }
    public static string? SafeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? value : null;

    public static void CalendarMetrics(RunState state)
    {
        var source = state.Coverage.FirstOrDefault(c => c.SourceType == "calendar");
        if (source?.Status != "complete") return;
        var dayStart = BusinessCalendar.StartUtc(state.Period.PreviousBusinessDate!.Value);
        var dayEnd = dayStart.AddDays(1);
        var windows = state.Evidence.Where(e => e.SourceType == "calendar" && e.Category == "activity" && e.IsMeeting == true &&
            e.Timestamp is not null && e.EndTime > e.Timestamp)
            .Select(e => (Start: e.Timestamp!.Value < dayStart ? dayStart : e.Timestamp.Value,
                End: e.EndTime!.Value > dayEnd ? dayEnd : e.EndTime.Value)).OrderBy(e => e.Start).ToArray();
        state.Metrics.CalendarMeetingCount = windows.Length;
        double minutes = 0;
        DateTimeOffset? start = null, end = null;
        foreach (var window in windows)
        {
            if (start is null) { start = window.Start; end = window.End; }
            else if (window.Start <= end) { if (window.End > end) end = window.End; }
            else { minutes += (end!.Value - start.Value).TotalMinutes; start = window.Start; end = window.End; }
        }
        if (start is not null) minutes += (end!.Value - start.Value).TotalMinutes;
        state.Metrics.ScheduledMeetingMinutes = minutes;
    }
}

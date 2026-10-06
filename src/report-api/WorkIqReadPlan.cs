using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public sealed class WorkIqReadTask
{
    public string Id { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Source { get; set; } = "";
    public string ToolName { get; set; } = "fetch";
    public string Arguments { get; set; } = "";
    public string Phase { get; set; } = "pending";
    public string? ResponseId { get; set; }
    public string? PreviousResponseId { get; set; }
    public string? ApprovalId { get; set; }
    public string? RawSourceHandle { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public bool Approved { get; set; }
    public int Turns { get; set; }
}

public static class WorkIqReadPlan
{
    public const string Profile = "verified-workiq-v1";
    public const string LegacyProfile = "legacy-ask-v1";
    public const int ToolBudget = 12;
    public const int MaximumTransportSubmissions = ToolBudget * 2 + 8;
    public const int MaximumPaths = 5;
    public static readonly string ProfileHash = GenerationSpec.Hash(Encoding.UTF8.GetBytes(
        Profile + ":identity-calendar-ask-chat-team-channel-replies:12:5:original-envelope-v1:" +
        typeof(WorkIqReadPlan).Assembly.ManifestModule.ModuleVersionId));

    public static string InstructionsHash(GenerationSpec spec) =>
        GenerationSpec.Hash(Encoding.UTF8.GetBytes(spec.VerifiedCollectorInstructions));

    public static string ConfigurationHash(Settings settings) => GenerationSpec.Hash(Encoding.UTF8.GetBytes(
        $"{ProfileHash}\0{settings.Get("Foundry:ProjectEndpoint")}\0{settings.Get("Foundry:CollectorAgentName")}" +
        $"\0{settings.Get("WorkIq:ConnectionId")}"));

    public static void Pin(RunState state, Settings settings, GenerationSpec spec)
    {
        state.AcquisitionProfile = settings.AcquisitionProfile;
        if (settings.AcquisitionProfile != Profile) return;
        if (string.IsNullOrWhiteSpace(spec.VerifiedCollectorInstructions))
            throw Error("collector_profile_missing");
        state.TaskProfileHash = ConfigurationHash(settings);
        state.CollectorInstructionsHash = InstructionsHash(spec);
    }

    public static void ValidateContinuation(RunState state, Settings settings, GenerationSpec spec)
    {
        if ((state.AcquisitionProfile ?? LegacyProfile) != settings.AcquisitionProfile ||
            (settings.AcquisitionProfile == Profile &&
             (state.TaskProfileHash != ConfigurationHash(settings) || state.CollectorInstructionsHash != InstructionsHash(spec))))
            throw new ApiException(409, "run_configuration_changed", "取得profile・指示が更新されました。新規取得してください。");
    }

    public static void Initialize(RunState state)
    {
        if (state.WorkIqTasks.Count > 0) return;
        if (state.Period.Kind != "daily" || state.Period.PreviousBusinessDate is null || state.Period.ReportDate is null)
            throw Error("collector_period_invalid");
        foreach (string source in new[] { "calendar", "chat", "channel", "transcript" }) state.Source(source);
        state.Incomplete("transcript", "verified_profile_transcript_unavailable", true);
        Fetch(state, "identity", "workiq", ["/me?$select=id,userPrincipalName"]);
    }

    public static void AfterIdentity(RunState state)
    {
        Fetch(state, "calendar", "calendar", [CalendarPath(state.Period.PreviousBusinessDate!.Value),
            CalendarPath(state.Period.ReportDate!.Value)]);
        Add(state, "chat-ask", "chat", "ask", JsonSerializer.Serialize(new
        {
            question = Question(state.Period.PreviousBusinessDate.Value), timeZone = "Asia/Tokyo"
        }, Json.Options));
        Fetch(state, "teams", "channel", ["/me/joinedTeams"]);
        state.Source("calendar").AcquisitionPath = "calendar.fetch";
        state.Source("chat").AcquisitionPath = "ask";
        state.Source("channel").AcquisitionPath = "fetch.discovery";
        state.Incomplete("channel", "bounded_discovery_shared_channels_not_explored");
    }

    public static string Question(DateOnly activityDate) =>
        $"Tell me what I discussed in Teams on {activityDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}. " +
        "Use Japan time for the date. Quote the actual messages and identify the other participant and sources. " +
        "Read only; do not create, modify, or send data.";

    public static string CalendarPath(DateOnly day)
    {
        var start = BusinessCalendar.StartUtc(day);
        string Utc(DateTimeOffset date) => Uri.EscapeDataString(date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        return $"/me/calendarView?startDateTime={Utc(start)}&endDateTime={Utc(start.AddDays(1))}" +
            "&$select=id,subject,start,end,isCancelled,isAllDay,type,webLink,attendees&$top=50";
    }

    public static string ChatMessagesPath(string encodedId, DateOnly day) =>
        $"/chats/{encodedId}/messages?$top=50&$orderby=createdDateTime%20desc&$filter=" +
        Uri.EscapeDataString("createdDateTime lt " + BusinessCalendar.StartUtc(day).AddDays(1)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    public static WorkIqReadTask? Fetch(RunState state, string purpose, string source, IEnumerable<string> paths) =>
        Add(state, purpose, source, "fetch", JsonSerializer.Serialize(new { entityUrls = paths.ToArray() }, Json.Options));

    public static WorkIqReadTask? Add(RunState state, string purpose, string source, string tool, string arguments)
    {
        string id = GenerationSpec.Hash(Encoding.UTF8.GetBytes($"{purpose}\0{tool}\0{arguments}"));
        if (state.WorkIqTasks.FirstOrDefault(t => t.Id == id) is { } existing) return existing;
        if (state.WorkIqTasks.Count >= ToolBudget)
        {
            state.Incomplete(source, "budget_exhausted", true);
            return null;
        }
        var task = new WorkIqReadTask { Id = id, Purpose = purpose, Source = source, ToolName = tool, Arguments = arguments };
        if (!IsSafeTask(task)) throw Error("collector_task_invalid");
        state.WorkIqTasks.Add(task);
        state.Source(source).Increment("workiq.tasksPlanned");
        return task;
    }

    public static string[] Paths(WorkIqReadTask task)
    {
        using var args = JsonDocument.Parse(task.Arguments);
        return args.RootElement.GetProperty("entityUrls").EnumerateArray().Select(p => p.GetString()!).ToArray();
    }

    public static string? Segment(JsonElement row)
    {
        string? value = GraphCollector.S(row, "id");
        return string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%')
            ? null : Uri.EscapeDataString(value);
    }

    public static bool IsSafeTask(WorkIqReadTask task)
    {
        try
        {
            using var args = JsonDocument.Parse(task.Arguments);
            var root = args.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (task.ToolName == "ask")
                return task.Purpose == "chat-ask" && root.EnumerateObject().Count() == 2 &&
                    GraphCollector.S(root, "timeZone") == "Asia/Tokyo" &&
                    GraphCollector.S(root, "question") is { } question &&
                    Regex.IsMatch(question, @"^Tell me what I discussed in Teams on [A-Za-z]+ \d{1,2}, \d{4}\. Use Japan time for the date\. Quote the actual messages and identify the other participant and sources\. Read only; do not create, modify, or send data\.$");
            if (task.ToolName != "fetch" || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("entityUrls", out var paths) || paths.ValueKind != JsonValueKind.Array ||
                paths.GetArrayLength() is < 1 or > MaximumPaths) return false;
            return paths.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String && SafePath(p.GetString()!));
        }
        catch (JsonException) { return false; }
    }

    private static bool SafePath(string path)
    {
        if (path.Length > 2048 || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') ||
            path.Any(char.IsControl)) return false;
        string resource = path.Split('?')[0];
        return resource is "/me" or "/me/calendarView" or "/me/chats" or "/me/joinedTeams" ||
            Regex.IsMatch(resource, @"^/chats/[A-Za-z0-9_%@.:-]+/messages$") ||
            Regex.IsMatch(resource, @"^/teams/[A-Za-z0-9_%@.:-]+/channels(?:/[A-Za-z0-9_%@.:-]+/messages(?:/[A-Za-z0-9_%@.:-]+/replies)?)?$");
    }

    public static void ValidateApproval(WorkIqReadTask task, string toolName, string arguments, string? serverLabel = "workiq")
    {
        if (task.Approved || !IsSafeTask(task) || toolName != task.ToolName || serverLabel != "workiq" ||
            !SameArguments(task.Arguments, arguments))
            throw Error("collector_approval_mismatch");
    }

    public static bool SameArguments(string expected, string actual)
    {
        try
        {
            using var planned = JsonDocument.Parse(expected);
            using var submitted = JsonDocument.Parse(actual);
            return JsonElement.DeepEquals(planned.RootElement, submitted.RootElement);
        }
        catch (JsonException) { return false; }
    }

    public static void PrepareSubmission(RunState state, WorkIqReadTask task, DateTimeOffset now, bool approval)
    {
        if (task.Phase is "submitted" or "uncertain") throw Error("collector_submission_uncertain");
        if (task.Turns >= 8) throw Error("collector_budget_exhausted");
        if (state.WorkIqTransportSubmissions >= MaximumTransportSubmissions)
            throw Error("collector_transport_budget_exhausted");
        if (!IsSafeTask(task) || (task.Purpose != "identity" && !state.WorkIqIdentityVerified))
            throw Error("collector_identity_required");
        if (approval)
        {
            if (task.Approved || task.ApprovalId is null) throw Error("collector_approval_mismatch");
            if (state.WorkIqToolSubmissions >= ToolBudget) throw Error("collector_budget_exhausted");
            task.Approved = true;
            state.WorkIqToolSubmissions++;
            state.Source(task.Source).Increment("workiq.tasksSubmitted");
            state.Source(task.Source).Increment("workiq." + task.ToolName);
        }
        task.StartedAt ??= now;
        task.Turns++;
        state.WorkIqTransportSubmissions++;
        state.Source(task.Source).Increment("workiq.transportSubmissions");
        task.PreviousResponseId = task.ResponseId;
        task.ResponseId = null;
        task.Phase = "submitted";
    }

    public static ApiException Error(string code) =>
        new(502, code, "Work IQの取得タスクを安全に検証できませんでした。自動再送・別経路への切替は行いません。");
}

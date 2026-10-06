using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphReportChat.Api;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 48
    };
}

public sealed record Period(string Kind, DateOnly? ReportDate, DateOnly StartDate, DateOnly EndDate, DateOnly? PreviousBusinessDate = null);
public sealed record PeriodInput(string Kind = "daily", DateOnly? ReportDate = null, DateOnly? StartDate = null, DateOnly? EndDate = null);
public sealed record ResolveRequest(string Message, PeriodInput? Period = null, string[]? Members = null);
public sealed record Choice(string Label, Period Period);
public sealed record Resolution(string Kind, string Message, Period? Period = null, Choice[]? Choices = null);
public sealed record Approval(string RequestId, bool Approved);
public sealed record GenerateRequest(string Message, PeriodInput? Period = null, string[]? Members = null,
    string? ContinuationToken = null, Approval? Approval = null, string? Draft = null, bool AllowPartial = false);
public sealed record SaveRequest(string RunId, string Text, string Status);
public sealed record UserIdentity(string TenantId, string ObjectId);
public sealed record Evidence(string Id, string SourceType, string Text, string? Url = null,
    string? SourceId = null, DateTimeOffset? Timestamp = null, string? SubjectId = null,
    string? Category = null, DateTimeOffset? EndTime = null, bool? IsMeeting = null, bool Truncated = false,
    int TextTokenCount = 0, string? Attribution = null, string? AcquisitionTool = null,
    string? VerificationStatus = null, string? TimestampPrecision = null, string? MessageKind = null,
    string? ActivityStatus = null, string? TaskId = null, string? SourceScope = null);
public sealed record SourceFailure(string Stage, int HttpStatus, string? GraphCode, int Count = 1);
public sealed class Coverage
{
    public string SourceType { get; set; } = "";
    public string Status { get; set; } = "complete";
    public int Count { get; set; }
    public int FetchedCount { get; set; }
    public int Pages { get; set; }
    public string? Reason { get; set; }
    public string? AcquisitionPath { get; set; }
    public string CollectionStatus { get; set; } = "complete";
    public string InputStatus { get; set; } = "complete";
    public int RetrievedCount { get; set; }
    public int SentCount { get; set; }
    public int TruncatedCount { get; set; }
    public int InputDroppedCount { get; set; }
    public Dictionary<string, int> Diagnostics { get; set; } = [];
    public List<SourceFailure> Errors { get; set; } = [];
    public void Increment(string key, int count = 1) => Diagnostics[key] = Diagnostics.GetValueOrDefault(key) + count;
    public void Failure(string stage, int status, string? code)
    {
        Increment("failedRequests");
        int index = Errors.FindIndex(e => e.Stage == stage && e.HttpStatus == status && e.GraphCode == code);
        if (index >= 0) Errors[index] = Errors[index] with { Count = Errors[index].Count + 1 };
        else if (Errors.Count < 64) Errors.Add(new(stage, status, code));
    }
}
public sealed class Metrics
{
    public bool TokenUsageAvailable { get; set; } = true;
    public long CollectionMilliseconds { get; set; }
    public long GenerationMilliseconds { get; set; }
    public long SaveMilliseconds { get; set; }
    public int SaveGraphCalls { get; set; }
    public int SaveRetries { get; set; }
    public int GraphCalls { get; set; }
    public int Retries { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CollectorInputTokens { get; set; }
    public int CollectorOutputTokens { get; set; }
    public int CollectorPollRequests { get; set; }
    public long CollectorWallMilliseconds { get; set; }
    public int RepairCount { get; set; }
    public int DroppedEvidence { get; set; }
    public int SourceCharacters { get; set; }
    public int InputByteUpperBound { get; set; }
    public int InputTokenEstimate { get; set; }
    public string InputSelectionVersion { get; set; } = "";
    public int StagedTokens { get; set; }
    public int TruncatedEvidence { get; set; }
    public Dictionary<string, string> SourceStatus { get; set; } = [];
    public Dictionary<string, int> SourceCounts { get; set; } = [];
    public string PromptHash { get; set; } = "";
    public string GenerationConfigHash { get; set; } = "";
    public string SchemaHash { get; set; } = "";
    public string EvidenceSchemaHash { get; set; } = "";
    public string GenerationSpecVersion { get; set; } = "";
    public string ModelDeployment { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string? CollectorAgentVersion { get; set; }
    public bool Edited { get; set; }
    public int? CalendarMeetingCount { get; set; }
    public double? ScheduledMeetingMinutes { get; set; }
}
public sealed record WorkItem(string Kind, string Url, string SourceType, string? Subject = null,
    string? Context = null, string? Category = null, string? ContentHash = null);
public sealed record SavedDaily(string SubjectId, DateOnly ReportDate, string ActivityDate, long Revision,
    string ContentUrl, string WebUrl, string RunId, string FileName, string? ContentHash = null);
public sealed record FolderFile(string Id, string Name, string WebUrl);
public sealed class RunState
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public Period Period { get; set; } = new("daily", null, default, default);
    public string Message { get; set; } = "";
    public string? Draft { get; set; }
    public string[] Members { get; set; } = [];
    public List<Evidence> Evidence { get; set; } = [];
    public List<Coverage> Coverage { get; set; } = [];
    public List<WorkItem> Queue { get; set; } = [];
    public HashSet<string> Visited { get; set; } = [];
    public Metrics Metrics { get; set; } = new();
    public string Phase { get; set; } = "collect";
    public string? CollectorResponseId { get; set; }
    public string? CollectorPendingResponseId { get; set; }
    public DateTimeOffset? CollectorPendingSince { get; set; }
    public DateTimeOffset? CollectorLastCheckAt { get; set; }
    public int CollectorPollFailures { get; set; }
    public int CollectorRateLimitRetries { get; set; }
    public DateTimeOffset? CollectorResumeAt { get; set; }
    public bool CollectorFormatOnly { get; set; }
    public string? PendingApprovalId { get; set; }
    public string? Text { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public bool PartialAccepted { get; set; }
    public int CollectorTurns { get; set; }
    public int CollectorRepairAttempts { get; set; }
    public string? CollectorRepairReason { get; set; }
    public int CollectorAskCalls { get; set; }
    public int CollectorAskErrors { get; set; }
    public int CollectorToolOutputCharacters { get; set; }
    public int CollectorToolResponses { get; set; }
    public int CollectorMissingToolOutputs { get; set; }
    public int CollectorQuestionCharacters { get; set; }
    public int CollectorTimeZoneRequests { get; set; }
    public bool CollectorFallbackObserved { get; set; }
    public List<WorkIqToolReply> WorkIqReplies { get; set; } = [];
    public string? AcquisitionProfile { get; set; }
    public string? TaskProfileHash { get; set; }
    public string? CollectorInstructionsHash { get; set; }
    public bool WorkIqIdentityVerified { get; set; }
    public int WorkIqToolSubmissions { get; set; }
    public int WorkIqTransportSubmissions { get; set; }
    public List<WorkIqReadTask> WorkIqTasks { get; set; } = [];
    public int SaveRevision { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int DeferredCount { get; set; }
    public Dictionary<string, SavedDaily> DailyFiles { get; set; } = [];
    public Dictionary<string, FolderFile> FolderFiles { get; set; } = [];
    public Dictionary<string, FolderFile> MetadataFiles { get; set; } = [];
    public HashSet<string> SeenEvidence { get; set; } = [];
    public int NextEvidenceNumber { get; set; }

    public Coverage Source(string type)
    {
        var source = Coverage.FirstOrDefault(c => c.SourceType == type);
        if (source is null) Coverage.Add(source = new() { SourceType = type });
        return source;
    }
    public void Incomplete(string type, string reason, bool unavailable = false)
    {
        var source = Source(type);
        source.Status = unavailable && source.Count == 0 ? "unavailable" : "partial";
        source.CollectionStatus = source.Status;
        if (source.Reason?.Contains(reason, StringComparison.Ordinal) != true)
            source.Reason = source.Reason is null ? reason : $"{source.Reason}; {reason}";
    }
    public void InputIncomplete(string type, string reason)
    {
        var source = Source(type);
        source.Status = "partial";
        source.InputStatus = "partial";
        if (source.Reason?.Contains(reason, StringComparison.Ordinal) != true)
            source.Reason = source.Reason is null ? reason : $"{source.Reason}; {reason}";
    }
}
public sealed record ReportFailureDetails(Period Period, IReadOnlyList<Coverage> Coverage, Metrics Metrics,
    string? DiagnosticRunId = null, bool RestartRequired = false);
public sealed class ApiException(int status, string code, string message, ReportFailureDetails? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public ReportFailureDetails? Details { get; } = details;
    public FailureDiagnostic? Diagnostic { get; init; }
}

using OpenAI.Responses;
using Azure.AI.Projects;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace GraphReportChat.Api;

public static class CollectorBackground
{
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(10);

    public static AIProjectClientOptions ClientOptions() => new()
    {
        RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
    };

    public static CreateResponseOptions CreateOptions(RunState state) => new()
    {
        PreviousResponseId = state.CollectorResponseId,
        MaxOutputTokenCount = 10000,
        MaxToolCallCount = 6,
        ToolChoice = state.CollectorFormatOnly ? ResponseToolChoice.CreateNoneChoice() : null,
        StoredOutputEnabled = true,
        BackgroundModeEnabled = true
    };

    public static bool ScheduleRateLimitRecovery(RunState state, JsonElement response, DateTimeOffset now)
    {
        if (GraphCollector.S(response, "status") != "failed" ||
            !response.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            GraphCollector.S(error, "code") != "rate_limit_exceeded" ||
            string.IsNullOrWhiteSpace(state.CollectorResponseId) || state.CollectorPendingResponseId is not null ||
            state.CollectorRateLimitRetries >= 2 || state.CollectorTurns >= 8) return false;
        state.CollectorRateLimitRetries++;
        state.CollectorResumeAt = now.AddSeconds(65 * state.CollectorRateLimitRetries);
        state.CollectorFormatOnly = state.CollectorToolResponses > state.CollectorAskErrors;
        return true;
    }

    public static object RateLimitProgress(RunState state, DateTimeOffset now) => new
    {
        kind = "progress", continuationToken = state.Id, automaticPolling = true,
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(((state.CollectorResumeAt ?? now) - now).TotalSeconds)),
        message = "モデルのトークン処理量上限に達しました。取得済みの応答を保持し、待機後に自動再開します（最大2回）。"
    };

    public static bool Expired(RunState state, DateTimeOffset now) =>
        state.CollectorPendingSince is { } started && now - started >= MaximumWait;

    public static void RecordWait(RunState state, DateTimeOffset now)
    {
        if ((state.CollectorPendingResponseId is not null || state.CollectorResumeAt is not null) && state.CollectorLastCheckAt is { } previous)
            state.Metrics.CollectionMilliseconds += Math.Max(0, (long)(now - previous).TotalMilliseconds);
        state.CollectorLastCheckAt = null;
    }

    public static bool Track(RunState state, string responseId, ResponseStatus? status, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ApiException(502, "collector_response_id_missing", "取得処理の応答IDが返されませんでした。重複実行を避けるため自動再送しません。");
        if (state.CollectorPendingResponseId is { } expected && expected != responseId)
            throw new ApiException(502, "collector_response_mismatch", "取得処理の応答IDが一致しません。");
        if (status == ResponseStatus.Queued || status == ResponseStatus.InProgress)
        {
            state.CollectorPendingResponseId = responseId;
            state.CollectorPendingSince ??= now;
            state.PendingApprovalId = null;
            state.CollectorPollFailures = 0;
            return true;
        }
        if (state.CollectorPendingSince is { } start)
            state.Metrics.CollectorWallMilliseconds += Math.Max(0, (long)(now - start).TotalMilliseconds);
        state.CollectorPendingResponseId = null;
        state.CollectorPendingSince = null;
        state.CollectorPollFailures = 0;
        return false;
    }

    public static object Progress(RunState state, bool transientFailure = false) => new
    {
        kind = "progress",
        continuationToken = state.Id,
        retryAfterSeconds = transientFailure ? 5 : 3,
        automaticPolling = true,
        message = transientFailure
            ? "進捗の確認通信を再試行しています。同じ取得処理を確認し、Work IQへの取得要求は再送しません。"
            : "Work IQがバックグラウンドで取得中です。自動で結果を確認します（最大10分）。"
    };

    public static bool RetryPoll(RunState state, Exception error, DateTimeOffset now)
    {
        if (state.CollectorPendingResponseId is null || Expired(state, now) || state.CollectorPollFailures >= 3) return false;
        var diagnostic = ServiceFailure.Describe(error, "collector_poll");
        bool transient = diagnostic.Category is "timeout" or "transport" or "throttled" ||
            diagnostic.Causes.Any(c => c.HttpStatus is 408 or 500 or 502 or 503 or 504);
        if (!transient) return false;
        state.CollectorPollFailures++;
        return true;
    }
}

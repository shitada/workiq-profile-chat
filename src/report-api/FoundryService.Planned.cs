using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Azure.AI.Extensions.OpenAI;
using OpenAI.Responses;

namespace GraphReportChat.Api;

public sealed partial class FoundryService
{
    public static bool AllowsVerifiedReads(JsonElement allowed)
    {
        if (allowed.ValueKind == JsonValueKind.Object)
        {
            if (allowed.EnumerateObject().Any(p => p.Name is not ("tool_names" or "read_only")) ||
                (allowed.TryGetProperty("read_only", out var readOnly) &&
                 readOnly.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) ||
                !allowed.TryGetProperty("tool_names", out allowed)) return false;
        }
        return allowed.ValueKind == JsonValueKind.Array && allowed.GetArrayLength() == 3 &&
            allowed.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String) &&
            allowed.EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "ask", "call_function", "fetch" });
    }

    public static CreateResponseOptions PlannedOptions(WorkIqReadTask task, bool approval)
    {
        var options = new CreateResponseOptions
        {
            PreviousResponseId = task.PreviousResponseId,
            BackgroundModeEnabled = true, StoredOutputEnabled = true,
            MaxToolCallCount = 1, MaxOutputTokenCount = 1000,
            ToolChoice = approval ? null : ResponseToolChoice.CreateRequiredChoice()
        };
        if (approval)
            options.InputItems.Add(ResponseItem.CreateMcpApprovalResponseItem(task.ApprovalId!, true));
        else
            options.InputItems.Add(ResponseItem.CreateUserMessageItem(
                $"Call {task.ToolName} exactly once with arguments {task.Arguments}"));
        return options;
    }

    public static JsonElement ValidateCompletedCall(WorkIqReadTask task, JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw WorkIqReadPlan.Error("workiq_output_missing");
        var calls = output.EnumerateArray().Where(i => GraphCollector.S(i, "type") == "mcp_call").ToArray();
        if (!task.Approved || calls.Length != 1) throw WorkIqReadPlan.Error("collector_call_mismatch");
        var call = calls[0];
        if (GraphCollector.S(call, "name") != task.ToolName || GraphCollector.S(call, "server_label") != "workiq" ||
            GraphCollector.S(call, "arguments") is not { } args || !WorkIqReadPlan.SameArguments(task.Arguments, args))
            throw WorkIqReadPlan.Error("collector_call_mismatch");
        return call;
    }

    private async Task<object?> CollectVerified(RunState state, GenerateRequest request, string assertion,
        Func<CancellationToken, Task> persist, CancellationToken ct)
    {
        WorkIqReadPlan.ValidateContinuation(state, settings, spec);
        if (request.Approval is not null)
            throw new ApiException(400, "approval_mismatch", "固定済みread taskの承認はサーバーで検証します。");
        WorkIqReadPlan.Initialize(state);
        var task = state.WorkIqTasks.FirstOrDefault(t => t.Phase is not ("completed" or "failed" or "uncertain"));
        if (task is null)
        {
            state.Phase = "collected";
            EvidenceNormalizer.CalendarMetrics(state);
            return null;
        }
        if (task.Phase == "submitted")
        {
            task.Phase = "uncertain";
            VerifiedWorkIqParser.Fail(state, task, "collector_submission_uncertain");
            await persist(ct);
            if (task.Purpose == "identity") throw WorkIqReadPlan.Error("collector_submission_uncertain");
            return PlannedProgress(state);
        }
        var clock = Stopwatch.StartNew();
        CollectorBackground.RecordWait(state, DateTimeOffset.UtcNow);
        string stage = "collector_definition";
        try
        {
            var project = await Client(assertion, ct, collector: true);
            var client = await ValidatedClient(project, true, ct);
            if (task.StartedAt is { } started && DateTimeOffset.UtcNow - started >= CollectorBackground.MaximumWait)
            {
                if (state.CollectorPendingResponseId is { } expiredId)
                {
                    try
                    {
                        using var cancelLimit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await client.CancelResponseAsync(expiredId, cancelLimit.Token);
                    }
                    catch (Exception error)
                    {
                        state.Source(task.Source).Increment($"collector.cancel.{ServiceFailure.Describe(error, "collector_cancel").Category}");
                    }
                }
                throw WorkIqReadPlan.Error("collector_background_expired");
            }
            ClientResult<ResponseResult> result;
            bool approval = task.Phase == "approval-ready";
            if (task.ResponseId is { } responseId && !approval && task.Phase != "oauth")
            {
                stage = "collector_poll";
                state.Metrics.CollectorPollRequests++;
                using var pollLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollLimit.CancelAfter(TimeSpan.FromSeconds(30));
                try { result = await client.GetResponseAsync(responseId, pollLimit.Token); }
                catch (Exception error) when (!ct.IsCancellationRequested && CollectorBackground.RetryPoll(state, error, DateTimeOffset.UtcNow))
                { return CollectorBackground.Progress(state, true); }
            }
            else
            {
                // Intent, exact arguments and budget are durable BEFORE either POST. An interrupted POST is never replayed.
                WorkIqReadPlan.PrepareSubmission(state, task, DateTimeOffset.UtcNow, approval);
                state.CollectorPendingResponseId = null;
                await persist(ct);
                stage = "collector_request";
                using var submitLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                submitLimit.CancelAfter(TimeSpan.FromSeconds(45));
                try { result = await client.CreateResponseAsync(PlannedOptions(task, approval), submitLimit.Token); }
                catch (Exception)
                {
                    task.Phase = "uncertain";
                    using var saveLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await persist(saveLimit.Token);
                    throw WorkIqReadPlan.Error("collector_submission_uncertain");
                }
            }
            var response = result.Value;
            if (string.IsNullOrWhiteSpace(response.Id) || (task.ResponseId is { } expected && expected != response.Id))
                throw WorkIqReadPlan.Error("collector_response_mismatch");
            task.ResponseId = response.Id;
            task.RawSourceHandle = response.Id;
            task.Phase = "waiting";
            state.CollectorResponseId = response.Id;
            bool waiting = CollectorBackground.Track(state, response.Id, response.Status, DateTimeOffset.UtcNow);
            await persist(ct);
            if (waiting) return CollectorBackground.Progress(state);
            RecordUsage(state, response, true);
            using var raw = JsonDocument.Parse(result.GetRawResponse().Content);
            var approvals = response.OutputItems.OfType<McpToolCallApprovalRequestItem>().ToArray();
            var consents = response.OutputItems.Select(i => i.AsAgentResponseItem()).OfType<OAuthConsentRequestResponseItem>().ToArray();
            var calls = raw.RootElement.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Where(i => GraphCollector.S(i, "type") == "mcp_call").ToArray() : [];
            if (approvals.Length > 1 || consents.Length > 1 ||
                (approvals.Length > 0 && (calls.Length > 0 || consents.Length > 0)) ||
                (consents.Length > 0 && calls.Length > 0))
                throw WorkIqReadPlan.Error("collector_call_mismatch");
            if (consents.Length == 1)
            {
                if (task.Approved) throw WorkIqReadPlan.Error("collector_call_mismatch");
                string link = EvidenceNormalizer.SafeUrl(consents[0].ConsentLink.ToString())
                    ?? throw WorkIqReadPlan.Error("invalid_consent_url");
                task.Phase = "oauth";
                await persist(ct);
                return new { kind = "oauth_consent_required", continuationToken = state.Id, consentLink = link,
                    message = "Work IQへの本人OAuth同意後、続行してください。" };
            }
            if (approvals.Length == 1)
            {
                var approvalItem = approvals[0];
                string? server = items.EnumerateArray()
                    .Where(i => GraphCollector.S(i, "type") == "mcp_approval_request")
                    .Select(i => GraphCollector.S(i, "server_label")).SingleOrDefault();
                WorkIqReadPlan.ValidateApproval(task, approvalItem.ToolName, approvalItem.ToolArguments.ToString(), server);
                task.ApprovalId = approvalItem.Id;
                task.Phase = "approval-ready";
                await persist(ct);
                return PlannedProgress(state);
            }
            if (calls.Length == 0 && (response.Error is not null || response.Status != ResponseStatus.Completed))
            {
                var failure = CollectorFailureDiagnostic.Read(raw.RootElement);
                if (state.WorkIqReplies.Count < 8)
                    state.WorkIqReplies.Add(new(state.WorkIqReplies.Count + 1, "foundry_error", true, null,
                        failure.UpstreamErrorCode ?? failure.Code, failure.Message ?? "Work IQの元応答は返されませんでした。",
                        false, ToolName: task.ToolName, TaskId: task.Id));
                state.CollectorMissingToolOutputs++;
                throw WorkIqReadPlan.Error(failure.UpstreamErrorCode == "InternalError" ? "workiq_internal_error" : "workiq_response_failed");
            }
            var completedCall = ValidateCompletedCall(task, raw.RootElement);
            completedCall.TryGetProperty("output", out var original);
            try
            {
                if (response.Error is not null || response.Status != ResponseStatus.Completed ||
                    GraphCollector.S(completedCall, "status") is "failed" or "incomplete" ||
                    (completedCall.TryGetProperty("error", out var callError) &&
                     callError.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)))
                    throw WorkIqReadPlan.Error("workiq_tool_failed");
                VerifiedWorkIqParser.Process(state, task, original, spec.Config.InputTokenBudget - 8000);
            }
            finally
            {
                if (state.WorkIqReplies.Count < 8)
                    state.WorkIqReplies.Add(WorkIqToolReplyReader.ReadCall(completedCall, state.WorkIqReplies.Count + 1)
                        with { ToolName = task.ToolName, TaskId = task.Id });
                state.CollectorToolResponses++;
                if (task.ToolName == "ask") state.CollectorAskCalls++;
            }
            task.Phase = "completed";
            state.Source(task.Source).Increment("workiq.tasksCompleted");
            await persist(ct);
            return PlannedProgress(state);
        }
        catch (ApiException error) when (task.Purpose != "identity" &&
            error.Code is not ("collector_approval_mismatch" or "collector_call_mismatch" or "collector_response_mismatch" or "delegated_token_failed") &&
            error.Status != 409 && error.Status != 503)
        {
            task.Phase = error.Code == "collector_submission_uncertain" ? "uncertain" : "failed";
            VerifiedWorkIqParser.Fail(state, task, error.Code, error.Status);
            state.CollectorPendingResponseId = null;
            state.CollectorPendingSince = null;
            await persist(ct);
            return PlannedProgress(state);
        }
        catch (Exception error) when (error is not (ApiException or OperationCanceledException))
        {
            // GET failures do not trigger alternate acquisition or an unknown POST replay.
            var wrapped = ServiceFailure.Wrap(error, stage, FailureDetails(state));
            task.Phase = "failed";
            VerifiedWorkIqParser.Fail(state, task, wrapped.Code, wrapped.Status);
            await persist(ct);
            if (task.Purpose == "identity") throw wrapped;
            return PlannedProgress(state);
        }
        finally
        {
            state.Metrics.CollectionMilliseconds += clock.ElapsedMilliseconds;
            if (state.CollectorPendingResponseId is not null) state.CollectorLastCheckAt = DateTimeOffset.UtcNow;
        }
    }

    private static object PlannedProgress(RunState state) => new
    {
        kind = "progress", continuationToken = state.Id, automaticPolling = true, retryAfterSeconds = 1,
        message = $"検証済みread taskを順次処理しています（実ツール承認 {state.WorkIqToolSubmissions}/{WorkIqReadPlan.ToolBudget}）。"
    };
}

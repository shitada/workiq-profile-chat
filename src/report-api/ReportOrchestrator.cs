using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GraphReportChat.Api;

public sealed class ReportOrchestrator(Settings settings, GenerationSpec spec, PeriodResolver resolver,
    RunStore store, ITokenExchange tokens, GraphCollector collector, FoundryService foundry,
    SharePointReports reports, ILogger<ReportOrchestrator> logger)
{
    public async Task<object> Generate(GenerateRequest request, UserIdentity user, string assertion, CancellationToken ct)
    {
        if ((request.Message?.Length ?? 0) > 8000 || (request.Draft?.Length ?? 0) > 60000)
            throw new ApiException(400, "input_too_long", "指示は8,000文字、編集案は60,000文字以内です。");
        string handle;
        if (request.ContinuationToken is null)
        {
            if (request.Approval is not null) throw new ApiException(400, "approval_mismatch", "承認には現在の継続ハンドルが必要です。");
            var resolution = resolver.ResolveForGeneration(new(request.Message ?? "", request.Period, request.Members));
            if (resolution.Kind != "resolved") throw new ApiException(422, "period_confirmation_required", resolution.Message);
            var members = NormalizeMembers(request.Members, resolution.Period!.Kind);
            var state = new RunState
            {
                TenantId = user.TenantId, SubjectId = user.ObjectId, Provider = settings.Provider,
                Period = resolution.Period!, Message = request.Message ?? "", Draft = request.Draft, Members = members,
                Metrics = NewMetrics(request.Draft is not null)
            };
            if (state.Provider == "graph" || state.Period.Kind != "daily") collector.Initialize(state);
            else WorkIqReadPlan.Pin(state, settings, spec);
            await store.Create(state, ct);
            handle = state.Id;
        }
        else handle = request.ContinuationToken;
        await using var lease = await store.Acquire(handle, user, settings.Provider, ct);
        var run = lease.State;
        if (run.Provider == "workiq" && run.Period.Kind == "daily")
            WorkIqReadPlan.ValidateContinuation(run, settings, spec);
        if (run.Metrics.InputSelectionVersion != EvidenceNormalizer.SelectionVersion)
            throw new ApiException(409, "run_selection_version_changed", "情報取得・入力選択方式が更新されました。継続ではなく新規生成してください。");
        if (run.Metrics.PromptHash != spec.PromptHash || run.Metrics.GenerationConfigHash != spec.ConfigHash ||
            run.Metrics.SchemaHash != spec.SchemaHash || run.Metrics.EvidenceSchemaHash != spec.EvidenceSchemaHash ||
            run.Metrics.AgentVersion != settings.Required("Foundry:AgentVersion") ||
            (run.Provider == "workiq" && run.Metrics.CollectorAgentVersion != settings.Get("Foundry:CollectorAgentVersion")))
            throw new ApiException(409, "run_configuration_changed", "実行中に生成設定が変わりました。再生成してください。");
        if (run.Phase == "failed") throw FailedRun(run);
        if (run.Phase == "completed")
        {
            if (request.Draft is null) return Completed(run);
            var editPeriod = resolver.ResolveForGeneration(new(request.Message ?? "",
                request.Period ?? new(run.Period.Kind, run.Period.ReportDate, run.Period.StartDate, run.Period.EndDate), request.Members));
            if (editPeriod.Kind != "resolved") throw new ApiException(422, "period_confirmation_required", editPeriod.Message);
            var editMembers = NormalizeMembers(request.Members ?? run.Members, editPeriod.Period!.Kind);
            var clone = JsonSerializer.Deserialize<RunState>(JsonSerializer.Serialize(run, Json.Options), Json.Options)!;
            clone.Draft = request.Draft;
            clone.Message = request.Message ?? "";
            clone.Phase = "collected";
            clone.Text = null;
            clone.Metrics = NewMetrics(true);
            clone.Metrics.SourceCharacters = clone.Evidence.Sum(e => e.Text.Length);
            clone.PartialAccepted = run.PartialAccepted;
            bool recollect = editPeriod.Period != run.Period || !editMembers.Order().SequenceEqual(run.Members.Order());
            if (recollect)
            {
                clone.Period = editPeriod.Period!;
                clone.Members = editMembers;
                clone.Phase = "collect";
                clone.Evidence.Clear();
                clone.Coverage.Clear();
                clone.Queue.Clear();
                clone.Visited.Clear();
                clone.DailyFiles.Clear();
                clone.FolderFiles.Clear();
                clone.MetadataFiles.Clear();
                clone.SeenEvidence.Clear();
                clone.NextEvidenceNumber = 0;
                clone.CollectorResponseId = null;
                clone.CollectorPendingResponseId = null;
                clone.CollectorPendingSince = null;
                clone.CollectorLastCheckAt = null;
                clone.CollectorPollFailures = 0;
                clone.CollectorRateLimitRetries = 0;
                clone.CollectorResumeAt = null;
                clone.CollectorFormatOnly = false;
                clone.PendingApprovalId = null;
                clone.CollectorTurns = 0;
                clone.CollectorRepairAttempts = 0;
                clone.CollectorRepairReason = null;
                clone.CollectorAskCalls = 0;
                clone.CollectorAskErrors = 0;
                clone.CollectorToolOutputCharacters = 0;
                clone.CollectorToolResponses = 0;
                clone.CollectorMissingToolOutputs = 0;
                clone.CollectorQuestionCharacters = 0;
                clone.CollectorTimeZoneRequests = 0;
                clone.CollectorFallbackObserved = false;
                clone.WorkIqReplies.Clear();
                clone.WorkIqTasks.Clear();
                clone.WorkIqIdentityVerified = false;
                clone.WorkIqToolSubmissions = 0;
                clone.WorkIqTransportSubmissions = 0;
                clone.Metrics.SourceCharacters = 0;
                clone.PartialAccepted = false;
                if (clone.Provider == "graph" || clone.Period.Kind != "daily") collector.Initialize(clone);
            }
            await store.Create(clone, ct);
            return new { kind = "progress", continuationToken = clone.Id,
                message = recollect ? "明示された期間・対象者を優先し、根拠を再取得します。" : "既存の根拠で修正案を生成します。再取得は行いません。" };
        }
        if (run.Phase == "collect" && request.Draft is null && !string.IsNullOrEmpty(request.Message) && request.Message != run.Message)
            throw new ApiException(409, "continuation_input_changed", "取得中の指示は変更できません。新規生成してください。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(160));
        try
        {
            if (run.Phase == "collect")
            {
                if (run.Provider == "graph" || run.Period.Kind != "daily")
                {
                    if (request.Approval is not null) throw new ApiException(400, "approval_mismatch", "この実行に承認要求はありません。");
                    if (run.Queue.Count > 0)
                    {
                        var graphToken = await tokens.Graph(assertion, timeout.Token);
                        await collector.Collect(run, graphToken, timeout.Token);
                    }
                    if (run.Queue.Count > 0)
                    {
                        await lease.Save(ct);
                        return new { kind = "progress", continuationToken = run.Id,
                            retryAfterSeconds = run.NextAttemptAt is { } retryAt ? Math.Clamp((int)Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds), 1, 300) : 1,
                            message = $"対象期間の情報を取得中です（{run.Metrics.GraphCalls} API呼出し、残り{run.Queue.Count}ページ／対象）。" };
                    }
                    run.Phase = "collected";
                }
                else
                {
                    var continuation = await foundry.Collect(run, request, assertion, timeout.Token, lease.Save);
                    if (continuation is not null) { await lease.Save(ct); return continuation; }
                }
            }
            EvidenceNormalizer.ApplyBudget(run, spec);
            if (run.Evidence.Count == 0)
                throw FoundryService.NoEvidence(run);
            if (run.Coverage.Any(c => c.Status != "complete") && !run.PartialAccepted)
            {
                if (!request.AllowPartial)
                {
                    await lease.Save(ct);
                    return new { kind = "partial_confirmation", continuationToken = run.Id,
                        message = "一部の情報を取得できませんでした。取得状態を確認し、部分的な根拠で生成する場合は続行してください。",
                        coverage = run.Coverage, diagnosticRunId = run.Provider == "workiq" ? run.Id : null };
                }
                run.PartialAccepted = true;
            }
            await foundry.Generate(run, assertion, timeout.Token);
            run.Metrics.SourceStatus = run.Coverage.ToDictionary(c => c.SourceType, c => c.Status);
            run.Metrics.SourceCounts = run.Coverage.ToDictionary(c => c.SourceType, c => c.Count);
            await lease.Save(ct);
            logger.LogInformation("Report completed RunId={RunId} Provider={Provider} Metrics={Metrics}",
                run.Id, run.Provider, JsonSerializer.Serialize(run.Metrics, Json.Options));
            return Completed(run);
        }
        catch (ApiException ex) when ((ex.Status is 422 or 502 && ex.Code is not "delegated_token_failed") ||
            ex.Code == "collector_rate_limit_exhausted")
        {
            var terminal = MarkFailed(run, ex);
            await lease.Save(ct);
            throw terminal;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await lease.Save(ct);
            throw new ApiException(504, "upstream_timeout", "処理がタイムアウトしました。同じ継続ハンドルで再試行できます。");
        }
    }
    public static ApiException MarkFailed(RunState run, ApiException error)
    {
        run.Phase = "failed";
        run.FailureCode = error.Code;
        run.FailureMessage = error.Message;
        return new(error.Status, error.Code, error.Message, FoundryService.FailureDetails(run, restartRequired: true))
        { Diagnostic = error.Diagnostic };
    }
    public static ApiException FailedRun(RunState run) => new(422, run.FailureCode ?? "run_failed",
        (run.FailureMessage ?? "この実行は失敗しました。") + " 失敗済みのため継続はできません。同じ期間で新規取得してください。",
        FoundryService.FailureDetails(run, restartRequired: true));

    private Metrics NewMetrics(bool edited) => new()
    {
        PromptHash = spec.PromptHash, GenerationConfigHash = spec.ConfigHash, SchemaHash = spec.SchemaHash,
        EvidenceSchemaHash = spec.EvidenceSchemaHash, GenerationSpecVersion = spec.Config.Version,
        InputSelectionVersion = EvidenceNormalizer.SelectionVersion,
        ModelDeployment = spec.Config.ModelDeployment, ModelVersion = spec.Config.ExpectedModelVersion,
        AgentVersion = settings.Required("Foundry:AgentVersion"),
        CollectorAgentVersion = settings.Provider == "workiq" ? settings.Get("Foundry:CollectorAgentVersion") : null,
        Edited = edited
    };
    public static string[] NormalizeMembers(string[]? members, string kind)
    {
        if (kind != "managerWeekly") return [];
        if (members is null || members.Length is < 1 or > 30 || members.Any(m => !Guid.TryParse(m, out _)))
            throw new ApiException(400, "members_required", "上長週報は1〜30人の明示的なメンバーObject IDを指定してください。");
        return members.Select(m => Guid.Parse(m).ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static object Completed(RunState run) => new { kind = "completed", runId = run.Id,
        text = run.Text, period = run.Period, evidence = run.Evidence, coverage = run.Coverage, metrics = run.Metrics,
        diagnosticRunId = run.Provider == "workiq" ? run.Id : null };

    public static object FailedWorkIqReplies(RunState run, CollectorFailureDiagnostic? collectorFailure = null,
        IReadOnlyList<WorkIqToolReply>? latestResponseReplies = null)
    {
        if (run.Provider != "workiq" || run.Phase is not ("failed" or "completed" or "collected"))
            throw new ApiException(404, "diagnostics_unavailable", "取得が終了したWork IQ実行の応答だけを確認できます。");
        return new
        {
            kind = "workiq_tool_diagnostics", period = run.Period, collectorFailure,
            acquisitionProfile = run.AcquisitionProfile, coverage = run.Coverage,
            replies = run.WorkIqReplies.Select(WorkIqToolReplyReader.SanitizeSaved).ToArray(),
            latestResponseReplies,
            note = "Work IQの返答を取得Agentの根拠JSONとは別に表示します。最大8応答、本文6,000文字・受信形式65,536文字／応答。認証情報等は除去します。通常ログ・比較JSON・下書き保存には含めません。" +
                (run.AcquisitionProfile == WorkIqReadPlan.Profile && run.WorkIqToolSubmissions > run.WorkIqReplies.Count
                    ? $"\n承認済みread task {run.WorkIqToolSubmissions}件のうち、保存されている{run.WorkIqReplies.Count}応答を表示します（最大8件）。" : "") +
                (run.CollectorAskCalls > run.WorkIqReplies.Count
                    ? $"\n観測したask {run.CollectorAskCalls}件のうち、保存されている{run.WorkIqReplies.Count}件を表示します。" : "") +
                (collectorFailure is null ? "" :
                    $"\nFoundry終了理由: status={collectorFailure.Status}; code={collectorFailure.Code ?? "none"}; incompleteReason={collectorFailure.IncompleteReason ?? "none"}\n{collectorFailure.Message}") +
                (collectorFailure?.UpstreamErrorCode is null ? "" :
                    $"\nWork IQサービス診断: errorCode={collectorFailure.UpstreamErrorCode}; recoveryAction={collectorFailure.RecoveryAction ?? "未報告"}; requestId={collectorFailure.UpstreamRequestId ?? "未報告"}。" +
                    " このコードだけで権限不足・課金不足・データ不存在とは断定できません。")
        };
    }

    public async Task<object> Diagnostics(string runId, UserIdentity user, string assertion, CancellationToken ct)
    {
        await using var lease = await store.Acquire(runId, user, settings.Provider, ct);
        _ = FailedWorkIqReplies(lease.State);
        if (lease.State.Phase != "failed") return FailedWorkIqReplies(lease.State);
        try
        {
            var inspection = await foundry.ReadCollectorFailure(lease.State, assertion, ct);
            return FailedWorkIqReplies(lease.State, inspection?.Failure, inspection?.Replies);
        }
        catch (ApiException error)
        {
            logger.LogWarning("Collector status diagnostic unavailable Code={Code} Status={Status}", error.Code, error.Status);
            return FailedWorkIqReplies(lease.State, new("unavailable", error.Code,
                "Foundryの終了理由は取得できませんでした。以下は保存済みのWork IQ応答です。", null));
        }
    }

    public async Task<object> Save(SaveRequest request, UserIdentity user, string assertion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.Text))
            throw new ApiException(400, "save_invalid", "実行IDと本文が必要です。");
        await using var lease = await store.Acquire(request.RunId, user, settings.Provider, ct);
        var result = await reports.Save(lease.State, request, await tokens.Graph(assertion, ct), ct);
        await lease.Save(ct);
        return result;
    }
}

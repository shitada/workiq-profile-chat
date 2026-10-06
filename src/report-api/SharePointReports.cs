using System.Text.Json;

namespace GraphReportChat.Api;

public sealed class SharePointReports(GraphTransport graph, Settings settings)
{
    public async Task<object> Save(RunState state, SaveRequest request, string token, CancellationToken ct)
    {
        if (!settings.SharePointConfigured) throw new ApiException(503, "report_folder_not_configured", "SharePointテスト保存先が設定されていません。");
        if (state.Phase != "completed" || state.Text is null) throw new ApiException(409, "report_not_completed", "完成したレポートのみ保存できます。");
        if (request.Status is not ("confirmed" or "test-generated")) throw new ApiException(400, "invalid_status", "保存状態が不正です。");
        if (request.Text.Length is < 1 or > 60000) throw new ApiException(400, "invalid_draft", "保存する本文は1〜60,000文字です。");
        if (ReportValidator.Validate(request.Text, state.Period.Kind, state.Evidence).Count > 0)
            throw new ApiException(422, "edited_report_invalid", "編集後も必須見出しと有効な根拠引用を保持してください。");
        long revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string fileName = BuildFileName(state, revision);
        string folder = $"{GraphTransport.Root}/drives/{GraphCollector.Esc(settings.Required("Report:SharePointDriveId"))}/items/{GraphCollector.Esc(settings.Required("Report:SharePointFolderId"))}";
        var operation = new RunState();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var uploaded = await graph.Send($"{folder}:/{GraphCollector.Esc(fileName)}:/content?@microsoft.graph.conflictBehavior=fail",
                token, operation, 20, ct, HttpMethod.Put, request.Text, "text/markdown");
            using var item = JsonDocument.Parse(uploaded.Body);
            var itemId = GraphCollector.S(item.RootElement, "id");
            var webUrl = EvidenceNormalizer.SafeUrl(GraphCollector.S(item.RootElement, "webUrl"));
            if (itemId.Length == 0 || webUrl is null) throw new ApiException(502, "save_response_invalid", "保存応答を検証できません。");
            var metadata = Metadata(state, request.Status, revision);
            metadata["contentItemId"] = itemId;
            metadata["contentFileName"] = fileName;
            metadata["contentSha256"] = GenerationSpec.Hash(System.Text.Encoding.UTF8.GetBytes(request.Text));
            metadata["schemaVersion"] = "1";
            // File-scoped metadata keeps the approved Files.ReadWrite.All baseline; list columns
            // would require the additional Sites.ReadWrite.All permission.
            await graph.Send($"{folder}:/{GraphCollector.Esc(fileName + ".report.json")}:/content?@microsoft.graph.conflictBehavior=fail",
                token, operation, 20, ct, HttpMethod.Put, JsonSerializer.Serialize(metadata, Json.Options));
            state.SaveRevision++;
            return new { kind = "saved", webUrl, fileName };
        }
        catch (GraphFailure ex)
        {
            throw new ApiException(ex.Status is 401 or 403 ? ex.Status : 502, "sharepoint_save_failed",
                "SharePoint本文または管理メタデータ保存に失敗しました。フォルダーと委任権限を確認してください。未確定ファイルが残る可能性があります。");
        }
        catch (GraphDeferred)
        {
            throw new ApiException(429, "sharepoint_throttled", "SharePointが混雑しています。保存は未確定です。少し待って再試行してください。");
        }
        finally
        {
            state.Metrics.SaveMilliseconds += clock.ElapsedMilliseconds;
            state.Metrics.SaveGraphCalls += operation.Metrics.GraphCalls;
            state.Metrics.SaveRetries += operation.Metrics.Retries;
        }
    }
    public static string BuildFileName(RunState state, long revision) =>
        $"{state.Period.Kind}-{state.Period.ReportDate ?? state.Period.StartDate:yyyy-MM-dd}-{state.SubjectId}-{revision}-{Guid.NewGuid():N}.md";
    public static Dictionary<string, string> Metadata(RunState state, string status, long revision) => new()
    {
        ["subjectId"] = state.SubjectId,
        ["reportDate"] = (state.Period.ReportDate ?? state.Period.StartDate).ToString("yyyy-MM-dd"),
        ["activityDate"] = (state.Period.PreviousBusinessDate ?? state.Period.StartDate).ToString("yyyy-MM-dd"),
        ["periodStart"] = state.Period.StartDate.ToString("yyyy-MM-dd"),
        ["periodEnd"] = state.Period.EndDate.ToString("yyyy-MM-dd"),
        ["reportType"] = state.Period.Kind,
        ["status"] = status,
        ["revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["method"] = state.Provider,
        ["promptHash"] = state.Metrics.PromptHash,
        ["runId"] = state.Id
    };
}

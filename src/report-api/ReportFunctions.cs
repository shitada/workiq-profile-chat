using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GraphReportChat.Api;

public sealed class ReportFunctions(EasyAuth auth, Settings settings, GenerationSpec spec,
    PeriodResolver resolver, ReportOrchestrator reports, ILogger<ReportFunctions> logger)
{
    [Function("Health")]
    public IActionResult Health([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest request) =>
        new JsonResult(new { status = "ok" }, Json.Options);

    [Function("ReportConfig")]
    public Task<IActionResult> Config([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/config")] HttpRequest request) =>
        Execute(request, (_, _) => Task.FromResult<object>(new
        {
            provider = settings.Provider, testYear = settings.TestYear, timeZone = "Asia/Tokyo",
            acquisitionProfile = settings.Provider == "workiq" ? settings.AcquisitionProfile : null,
            promptHash = spec.PromptHash, modelDeployment = spec.Config.ModelDeployment,
            modelVersion = spec.Config.ExpectedModelVersion, sharePointConfigured = settings.SharePointConfigured
        }));

    [Function("ReportResolve")]
    public Task<IActionResult> Resolve([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/resolve")] HttpRequest request) =>
        Execute(request, async (_, _) => resolver.Resolve(await Read<ResolveRequest>(request)));

    [Function("ReportGenerate")]
    public Task<IActionResult> Generate([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/generate")] HttpRequest request) =>
        Execute(request, async (user, assertion) => await reports.Generate(await Read<GenerateRequest>(request), user, assertion, request.HttpContext.RequestAborted));

    [Function("ReportSave")]
    public Task<IActionResult> Save([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/save")] HttpRequest request) =>
        Execute(request, async (user, assertion) => await reports.Save(await Read<SaveRequest>(request), user, assertion, request.HttpContext.RequestAborted));

    [Function("ReportDiagnostics")]
    public Task<IActionResult> Diagnostics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/diagnostics/{runId}")] HttpRequest request, string runId) =>
        Execute(request, async (user, assertion) => await reports.Diagnostics(runId, user, assertion, request.HttpContext.RequestAborted));

    private async Task<IActionResult> Execute(HttpRequest request, Func<UserIdentity, string, Task<object>> action)
    {
        var correlation = Guid.NewGuid().ToString("N");
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        request.HttpContext.Response.Headers["X-Correlation-ID"] = correlation;
        try
        {
            var (user, assertion) = auth.Require(request);
            var output = await action(user, assertion);
            var document = JsonSerializer.SerializeToElement(output, Json.Options);
            var kind = document.TryGetProperty("kind", out var value) ? value.GetString() : "";
            if (kind == "progress" && document.TryGetProperty("retryAfterSeconds", out var retry))
                request.HttpContext.Response.Headers.RetryAfter = retry.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new JsonResult(document, Json.Options) { StatusCode = kind == "progress" ? 202 : 200 };
        }
        catch (ApiException ex) { return Error(ex.Status, ex.Code, ex.Message, correlation, ex.Details, ex.Diagnostic); }
        catch (JsonException) { return Error(400, "invalid_json", "JSONまたは日付の形式が不正です。", correlation); }
        catch (OperationCanceledException) { return Error(504, "request_timeout", "要求がタイムアウトしました。", correlation); }
        catch (Exception ex)
        {
            var failure = ServiceFailure.Wrap(ex, "report_request");
            return Error(failure.Status, failure.Code, failure.Message, correlation, diagnostic: failure.Diagnostic);
        }
    }
    private JsonResult Error(int status, string code, string message, string correlation, ReportFailureDetails? details = null,
        FailureDiagnostic? diagnostic = null)
    {
        logger.LogWarning("Report request rejected Code={Code} Status={Status} CorrelationId={CorrelationId}", code, status, correlation);
        if (diagnostic is not null)
            logger.LogWarning("Report service failure CorrelationId={CorrelationId} Diagnostic={Diagnostic}",
                correlation, ServiceFailure.Serialize(diagnostic));
        if (details is not null)
            logger.LogWarning("Report collection failure CorrelationId={CorrelationId} Counters={Counters}",
                correlation, JsonSerializer.Serialize(details.Coverage.Select(c => new
                {
                    c.SourceType, c.Status, c.Count, c.FetchedCount, c.RetrievedCount,
                    c.SentCount, c.Diagnostics
                }), Json.Options));
        return new JsonResult(new { code, message, correlationId = correlation, details, diagnostic }, Json.Options) { StatusCode = status };
    }
    private static async Task<T> Read<T>(HttpRequest request)
    {
        const int maximum = 256 * 1024;
        if (request.ContentLength > maximum) throw new ApiException(413, "body_too_large", "要求本文が大きすぎます。");
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(bytes, request.HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + count > maximum) throw new ApiException(413, "body_too_large", "要求本文が大きすぎます。");
            buffer.Write(bytes, 0, count);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json.Options)
            ?? throw new ApiException(400, "invalid_json", "JSON本文が必要です。");
    }
}

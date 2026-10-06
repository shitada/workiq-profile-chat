using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GraphReportChat.Api;

public sealed class GraphFailure(int status, string reason, string? graphCode = null) : Exception(reason)
{
    public int Status { get; } = status;
    public string? GraphCode { get; } = graphCode;
}
public sealed class GraphDeferred : Exception;
public sealed record GraphPayload(string Body, HttpStatusCode Status);

public sealed class GraphTransport(HttpClient http)
{
    public const string Root = "https://graph.microsoft.com/v1.0";
    public static Uri ValidateUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
            throw new GraphFailure(400, "unsafe_graph_url");
        return uri;
    }
    public static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var delay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
    public async Task<GraphPayload> Send(string url, string token, RunState state, int budget, CancellationToken ct,
        HttpMethod? method = null, string? body = null, string mediaType = "application/json", bool text = false)
    {
        var uri = ValidateUri(url);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (state.Metrics.GraphCalls >= budget) throw new GraphFailure(429, "api_budget_exhausted");
            state.Metrics.GraphCalls++;
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"Tokyo Standard Time\"");
            if (text) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/vtt"));
            if (method == HttpMethod.Put) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, mediaType);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 429 or 503 or 504)
            {
                if (attempt == 3) throw new GraphFailure((int)response.StatusCode, "retry_exhausted", await ErrorCode(response, ct));
                var delay = RetryDelay(response, attempt);
                state.Metrics.Retries++;
                if (delay > TimeSpan.FromSeconds(3))
                {
                    state.NextAttemptAt = DateTimeOffset.UtcNow.Add(delay);
                    throw new GraphDeferred();
                }
                await Task.Delay(delay, ct);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
            {
                // Only Graph's authenticated /content response may introduce a download URL.
                var target = response.Headers.Location;
                if (method is not null || !uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal) ||
                    target is null || target.Scheme != "https" || !target.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
                    target.UserInfo.Length > 0 || !target.IsDefaultPort)
                    throw new GraphFailure(502, "unsafe_download_redirect");
                using var download = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!download.IsSuccessStatusCode) throw new GraphFailure((int)download.StatusCode, "file_download_failed");
                return new(await ReadLimited(download.Content, ct), download.StatusCode);
            }
            if (!response.IsSuccessStatusCode)
                throw new GraphFailure((int)response.StatusCode, $"graph_http_{(int)response.StatusCode}", await ErrorCode(response, ct));
            return new(await ReadLimited(response.Content, ct), response.StatusCode);
        }
        throw new GraphFailure(502, "retry_exhausted");
    }
    private static async Task<string?> ErrorCode(HttpResponseMessage response, CancellationToken ct)
    {
        try { return ParseErrorCode(await ReadLimited(response.Content, ct)); }
        catch (Exception error) when (error is JsonException or GraphFailure) { return null; }
    }
    public static string? ParseErrorCode(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error)) return null;
        string code = GraphCollector.S(error, "code");
        // Only protocol error identifiers, never arbitrary error messages/inner payloads.
        return code switch
        {
            "Forbidden" or "AccessDenied" or "accessDenied" or "ErrorAccessDenied" or "Authorization_RequestDenied" or
            "InvalidAuthenticationToken" or "AuthenticationError" or "Unauthorized" or "BadRequest" or "BadArgument" or
            "Request_BadRequest" or "ResourceNotFound" or "Request_ResourceNotFound" or "NotFound" or "ItemNotFound" or
            "itemNotFound" or "ErrorItemNotFound" or "UnknownError" or "GeneralException" or "generalException" or
            "TooManyRequests" or "TooManyRequestsError" or "ServiceUnavailable" or "serviceNotAvailable" or
            "NotSupported" or "notSupported" or "InvalidRequest" or "invalidRequest" => code,
            "" => null,
            _ => "unrecognized_graph_error"
        };
    }
    private static async Task<string> ReadLimited(HttpContent content, CancellationToken ct)
    {
        const int max = 4 * 1024 * 1024;
        if (content.Headers.ContentLength > max) throw new GraphFailure(413, "source_response_too_large");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > max) throw new GraphFailure(413, "source_response_too_large");
            buffer.Write(bytes, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

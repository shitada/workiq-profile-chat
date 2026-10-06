using System.ClientModel;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Azure;

namespace GraphReportChat.Api;

public sealed record FailureCause(string Type, int? HttpStatus, string? ErrorCode);
public sealed record FailureDiagnostic(string Stage, string Category, IReadOnlyList<FailureCause> Causes);

public static class ServiceFailure
{
    public static FailureDiagnostic Describe(Exception exception, string stage)
    {
        var causes = new List<FailureCause>();
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        bool timedOut = false, transport = false;
        int examined = 0;
        while (pending.Count > 0 && examined++ < 24)
        {
            var current = pending.Dequeue();
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(8)) pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
            int? status = current switch
            {
                RequestFailedException azure => azure.Status,
                ClientResultException client => client.Status,
                HttpRequestException http => (int?)http.StatusCode,
                _ => null
            };
            string? code = current is RequestFailedException request ? SafeCode(request.ErrorCode) : null;
            causes.Add(new(current.GetType().Name, status, code));
            timedOut |= current is TimeoutException or OperationCanceledException;
            transport |= current is HttpRequestException or SocketException or IOException or AuthenticationException;
        }
        string category = causes.Any(c => c.HttpStatus is 401 or 403) ? "access_denied"
            : causes.Any(c => c.HttpStatus == 429) ? "throttled"
            : timedOut ? "timeout"
            : causes.Any(c => c.HttpStatus >= 400) ? "service_response"
            : transport || causes.Any(c => c.HttpStatus == 0) ? "transport"
            : "unexpected";
        return new(stage, category, causes.Distinct().Take(12).ToArray());
    }

    public static ApiException Wrap(Exception exception, string stage, ReportFailureDetails? details = null)
    {
        FailureDiagnostic diagnostic = Describe(exception, stage);
        string label = stage switch
        {
            "foundry_token" => "Foundry向けの認証",
            "collector_definition" => "取得Agentの設定読出し",
            "collector_request" => "Work IQ取得Agentへの要求",
            "collector_poll" => "実行中のWork IQ取得の進捗確認",
            "collector_response" => "Work IQ取得結果の処理",
            "generation_definition" => "生成Agentの設定読出し",
            "generation_request" => "レポート生成",
            "generation_response" => "生成結果の検証",
            "state_create" => "実行状態の保存",
            "state_read" => "実行状態の読出し",
            "state_save" => "実行状態の更新",
            _ => "レポート処理"
        };
        string message = diagnostic.Category switch
        {
            "access_denied" => $"{label}でサービスの認証・認可エラーを観測しました。HTTPステータスとサーバー診断を確認してください。",
            "throttled" => $"{label}がサービスの呼出し制限を受けました。時間をおいて新規取得してください。",
            "timeout" => $"{label}が時間内に完了しませんでした。データ0件や権限不足とは判定していません。",
            "transport" => $"{label}で通信が完了しませんでした。ネットワーク・サービス応答の診断を確認してください。権限不足とは断定できません。",
            "service_response" => $"{label}で外部サービスがエラーを返しました。処理段階とHTTPステータスを確認してください。",
            _ => $"{label}で予期しないエラーが発生しました。correlation IDでサーバー診断を確認してください。"
        };
        return new(diagnostic.Category == "timeout" ? 504 : 502, $"{stage}_{diagnostic.Category}", message, details)
        {
            Diagnostic = diagnostic
        };
    }

    public static string Serialize(FailureDiagnostic diagnostic) => JsonSerializer.Serialize(diagnostic, Json.Options);

    private static string? SafeCode(string? code) => code is { Length: > 0 and <= 80 } &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? code : null;
}

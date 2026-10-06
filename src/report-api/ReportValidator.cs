using System.Text;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public static class ReportValidator
{
    public static string[] RequiredSections(string kind) => kind switch
    {
        "daily" => ["前営業日に実施したこと", "得られた成果／進捗", "発生した課題", "ネクストアクション", "当日の予定", "相談事項", "情報共有事項", "称賛・ポジティブフィードバック", "根拠と情報不足"],
        "personalWeekly" => ["主な実施事項", "成果／進捗", "課題", "次週のアクション", "相談・共有事項", "根拠と不足日"],
        "managerWeekly" => ["メンバー別の活動・成果・課題", "チーム共通の進捗", "フォローが必要な事項", "次週のアクション", "根拠と不足日"],
        _ => throw new ApiException(400, "invalid_kind", "レポート種別が不正です。")
    };
    public static List<string> Validate(string text, string kind, IReadOnlyCollection<Evidence> evidence)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 60000) return ["empty_or_oversize"];
        foreach (var heading in RequiredSections(kind))
            if (!Regex.IsMatch(text, @"(?m)^#{1,6}\s+" + Regex.Escape(heading) + @"\s*$")) errors.Add("missing:" + heading);
        var ids = evidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var references = Regex.Matches(text, @"\[(E\d+)\]").Select(m => m.Groups[1].Value).ToArray();
        if (references.Any(id => !ids.Contains(id))) errors.Add("unknown_evidence");
        if (ids.Count > 0 && references.Length == 0) errors.Add("no_evidence_references");
        if (Regex.IsMatch(text, @"<\s*(script|iframe|object)\b", RegexOptions.IgnoreCase)) errors.Add("unsafe_html");
        return errors;
    }
    public static string AppendReferences(string text, IReadOnlyList<Evidence> evidence, IReadOnlyList<Coverage> coverage)
    {
        var builder = new StringBuilder(text.Trim());
        builder.AppendLine("\n\n---\n### 検証済み参照先");
        foreach (var item in evidence)
        {
            builder.Append($"- [{item.Id}] {item.SourceType}");
            if (item.Timestamp is { } timestamp) builder.Append($" {timestamp.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} JST");
            if (item.Url is { } url) builder.Append($" — [出典](<{url.Replace(">", "%3E").Replace("<", "%3C")}>)");
            else builder.Append(" — URL取得なし");
            builder.AppendLine();
        }
        builder.AppendLine("\n### 取得状態（サーバー記録）");
        foreach (var item in coverage) builder.AppendLine($"- {item.SourceType}: {item.Status} / 採用{item.Count}件 / {item.Reason ?? "取得完了"}");
        return builder.ToString();
    }
}

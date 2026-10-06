using System.Text;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public static class ReportValidator
{
    public static string NormalizeMarkdown(string text)
    {
        var match = Regex.Match(text.Trim(), @"\A(`{3,}|~{3,})(?:markdown|md)?[ \t]*\r?\n([\s\S]*?)\r?\n\1\z",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[2].Value.Trim() : text.Trim();
    }

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
        if (kind == "daily")
        {
            CheckSection("前営業日に実施したこと", "activity", requireAll: false);
            CheckSection("当日の予定", "schedule", requireAll: true);
            if (Regex.Matches(text, @"(?m)^#{1,6}[ \t]+根拠と情報不足[ \t]*\r?$").Count > 1)
                errors.Add("duplicate_section:根拠と情報不足");
        }
        return errors;

        void CheckSection(string heading, string category, bool requireAll)
        {
            if (Regex.Matches(text, @"(?m)^#{1,6}[ \t]+" + Regex.Escape(heading) + @"[ \t]*\r?$").Count > 1)
            {
                errors.Add("duplicate_section:" + heading);
                return;
            }
            string? section = Section(text, heading, out _, out _);
            if (section is null) return;
            var used = Regex.Matches(section, @"\[(E\d+)\]").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var item in evidence)
            {
                if (used.Contains(item.Id) && (item.Category is "activity" or "schedule") && item.Category != category)
                    errors.Add($"wrong_period_section:{heading}:{item.Id}");
                if (requireAll && item.Category == category && !used.Contains(item.Id))
                    errors.Add($"missing_schedule_reference:{item.Id}");
                if (category == "activity" && item.Category == category && item.SourceType == "calendar" && !used.Contains(item.Id))
                    errors.Add($"missing_activity_calendar_reference:{item.Id}");
            }
        }
    }

    private static string? Section(string text, string heading, out int start, out int end)
    {
        start = end = 0;
        var headings = Regex.Matches(text, @"(?m)^(#{1,6})[ \t]+([^\r\n]+?)[ \t]*\r?$");
        for (int i = 0; i < headings.Count; i++)
        {
            var current = headings[i];
            if (current.Groups[2].Value != heading) continue;
            start = current.Index + current.Length;
            end = text.Length;
            for (int j = i + 1; j < headings.Count; j++)
                if (headings[j].Groups[1].Length <= current.Groups[1].Length)
                {
                    end = headings[j].Index;
                    break;
                }
            return text[start..end];
        }
        return null;
    }
    public static string AppendReferences(string text, IReadOnlyList<Evidence> evidence, IReadOnlyList<Coverage> coverage,
        Period? period = null)
    {
        if (period?.Kind == "daily" && Section(text, "根拠と情報不足", out int start, out int end) is not null)
        {
            string References(string category)
            {
                var items = evidence.Where(e => e.Category == category).Select(e => $"[{e.Id}]").ToArray();
                return items.Length == 0 ? "採用された根拠なし（情報の不存在を意味しません）" : string.Join("", items);
            }
            var summary = new StringBuilder("\n");
            summary.AppendLine($"- 前営業日（{period.PreviousBusinessDate:yyyy-MM-dd}）の採用根拠：{References("activity")}");
            summary.AppendLine($"- 日報日（{period.ReportDate:yyyy-MM-dd}）の予定の採用根拠：{References("schedule")}");
            if (evidence.Any(e => e.SourceType == "calendar"))
                summary.AppendLine("- 予定表は予定の記録であり、参加・完了・成果の証拠ではありません。");
            summary.AppendLine("- 情報源別の取得状態・制限は下のサーバー記録を参照してください。未取得・採用0件から情報の不存在を判断しません。");
            text = text[..start] + summary + "\n" + text[end..];
        }
        var builder = new StringBuilder(text.Trim());
        builder.AppendLine("\n\n---\n### 検証済み参照先");
        foreach (var item in evidence)
        {
            builder.Append($"- [{item.Id}] {item.SourceType}");
            if (item.Category == "schedule") builder.Append("／当日の予定");
            else if (item.Category == "activity")
                builder.Append(item.SourceType == "calendar" ? "／前営業日の予定（参加未確認）" : "／前営業日の活動");
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

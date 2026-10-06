using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphReportChat.Api;

public sealed class BusinessCalendar
{
    // Cabinet Office published dates, including substitute/citizens' holidays.
    private const string Official2026 = "01-01,01-12,02-11,02-23,03-20,04-29,05-03,05-04,05-05,05-06,07-20,08-11,09-21,09-22,09-23,10-12,11-03,11-23";
    private const string Official2027 = "01-01,01-11,02-11,02-23,03-21,03-22,04-29,05-03,05-04,05-05,07-19,08-11,09-20,09-23,10-11,11-03,11-23";
    private readonly HashSet<DateOnly> holidays = [];
    private readonly HashSet<int> supportedYears = [2026, 2027];
    public BusinessCalendar(Settings settings)
    {
        foreach (var value in Official2026.Split(',')) holidays.Add(DateOnly.ParseExact($"2026-{value}", "yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var value in Official2027.Split(',')) holidays.Add(DateOnly.ParseExact($"2027-{value}", "yyyy-MM-dd", CultureInfo.InvariantCulture));
        // Dec 31 is not an official holiday, but is needed for Jan 1, 2026.
        foreach (var value in settings.Get("Report:CompanyHolidays").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new ApiException(503, "invalid_holidays", "会社休日は ISO 日付で指定してください。");
            holidays.Add(day);
        }
        var extra = settings.Get("Report:AdditionalOfficialHolidays");
        foreach (var entry in extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var year) || year is < 2 or > 9998)
                throw new ApiException(503, "invalid_holidays", "追加祝日設定は year:yyyy-MM-dd,... 形式です。");
            foreach (var date in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day.Year != year)
                    throw new ApiException(503, "invalid_holidays", "追加祝日の日付が不正です。");
                holidays.Add(day);
            }
            supportedYears.Add(year);
        }
    }
    public bool IsBusinessDay(DateOnly date)
    {
        if (!supportedYears.Contains(date.Year) && date != new DateOnly(2025, 12, 31))
            throw new ApiException(422, "holiday_year_unavailable", $"{date.Year}年の公式祝日設定が必要です。対象日は変更しません。");
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(date);
    }
    public DateOnly Previous(DateOnly date)
    {
        do { date = date.AddDays(-1); } while (!IsBusinessDay(date));
        return date;
    }
    public static DateTimeOffset StartUtc(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)).ToUniversalTime();
}

public sealed class PeriodResolver(Settings settings, BusinessCalendar calendar)
{
    private static readonly Regex Dates = new(@"(?<!\d)(?:(?<year>\d{4})[年/\-])?(?<month>\d{1,2})[月/\-](?<day>\d{1,2})日?(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Week = new(@"(?:(?<year>\d{4})年)?(?<month>\d{1,2})月(?:の)?第?(?<week>[1-5一二三四五])週目?", RegexOptions.Compiled);

    public Resolution ResolveForGeneration(ResolveRequest request)
    {
        var result = Resolve(request);
        if (result.Kind == "needs_confirmation" && result.Choices is { } choices && request.Period is { } selected)
        {
            var accepted = choices.FirstOrDefault(c => c.Period.Kind == selected.Kind &&
                c.Period.ReportDate == selected.ReportDate && c.Period.StartDate == selected.StartDate &&
                c.Period.EndDate == selected.EndDate);
            if (accepted is not null) return Resolved(accepted.Period);
        }
        return result;
    }

    public Resolution Resolve(ResolveRequest request)
    {
        var message = (request.Message ?? "").Normalize(NormalizationForm.FormKC);
        if (message.Length > 8000) throw new ApiException(400, "message_too_long", "指示は8,000文字以内です。");
        string kind = message.Contains("上長") || message.Contains("メンバー")
            ? "managerWeekly" : message.Contains("週報") ? "personalWeekly" :
            message.Contains("日報") ? "daily" : request.Period?.Kind ?? "daily";
        if (kind is not ("daily" or "personalWeekly" or "managerWeekly"))
            throw new ApiException(400, "invalid_kind", "レポート種別が不正です。");
        try
        {
            if (Regex.IsMatch(message, @"今年|去年|来年|令和|平成"))
                return Confirm("年は西暦の4桁で指定してください。");
            var matches = Dates.Matches(message);
            var week = Week.Match(message);
            if (matches.Count > 0)
            {
                int? explicitYear = matches.Cast<Match>().Where(m => m.Groups["year"].Success)
                    .Select(m => (int?)int.Parse(m.Groups["year"].Value)).FirstOrDefault();
                DateOnly Parse(Match match) => new(match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value) : explicitYear ?? settings.TestYear,
                    int.Parse(match.Groups["month"].Value), int.Parse(match.Groups["day"].Value));
                var dates = matches.Cast<Match>().Select(Parse).ToArray();
                if (kind == "daily")
                {
                    if (dates.Distinct().Count() != 1) return Confirm("日報日を1日だけ指定してください。");
                    return Resolved(Daily(dates[0]));
                }
                if (dates.Length == 1)
                {
                    var tail = message[(matches[0].Index + matches[0].Length)..];
                    var shortEnd = Regex.Match(tail, @"^\s*(?:[〜～~\-–—]|から)\s*(?<day>\d{1,2})日?(?![\d/月])");
                    if (shortEnd.Success) dates = [dates[0], new(dates[0].Year, dates[0].Month, int.Parse(shortEnd.Groups["day"].Value))];
                }
                if (dates.Length != 2) return Confirm("週報の開始日と終了日を指定してください（例: 8/3〜8/7）。");
                return Resolved(Weekly(kind, dates[0], dates[1]));
            }
            if (week.Success)
            {
                int year = week.Groups["year"].Success ? int.Parse(week.Groups["year"].Value) : settings.TestYear;
                int month = int.Parse(week.Groups["month"].Value);
                string weekText = week.Groups["week"].Value;
                int index = int.TryParse(weekText, out var number) ? number : "一二三四五".IndexOf(weekText, StringComparison.Ordinal) + 1;
                var first = new DateOnly(year, month, 1);
                int days = DateTime.DaysInMonth(year, month);
                int day = 1 + (index - 1) * 7;
                if (day > days) return Confirm("指定の週は存在しません。");
                var seven = Weekly(kind == "daily" ? "personalWeekly" : kind, first.AddDays(day - 1), new(year, month, Math.Min(day + 6, days)));
                var monday = first.AddDays(-(((int)first.DayOfWeek + 6) % 7) + (index - 1) * 7);
                var calendarWeek = Weekly(seven.Kind, monday, monday.AddDays(6));
                var firstMonday = first.AddDays((8 - (int)first.DayOfWeek) % 7 + (index - 1) * 7);
                return new("needs_confirmation", "週の定義を確認してください。取得はまだ開始しません。", Choices:
                [
                    new($"月内の{index}週目（{seven.StartDate:MM/dd}〜{seven.EndDate:MM/dd}）", seven),
                    new($"月初を含むカレンダー週（{monday:MM/dd}〜{monday.AddDays(6):MM/dd}）", calendarWeek),
                    new($"第{index}月曜から金曜（{firstMonday:MM/dd}〜{firstMonday.AddDays(4):MM/dd}）", Weekly(seven.Kind, firstMonday, firstMonday.AddDays(4)))
                ]);
            }
            // An unparsed date-like instruction must not silently reuse an old UI selection.
            if (Regex.IsMatch(message, @"\d+\s*[年月日/]|今日|昨日|今週|先週|来週|来月|今年|去年|来年|令和"))
                return Confirm("日付を yyyy-MM-dd または M/d、週報は開始日〜終了日で指定してください。");
            if (request.Period is { } selected)
            {
                if (kind == "daily" && selected.ReportDate is { } date) return Resolved(Daily(date));
                if (kind != "daily" && selected.StartDate is { } start && selected.EndDate is { } end)
                    return Resolved(Weekly(kind, start, end));
            }
            return Confirm("対象の日付または開始日・終了日を指定してください。");
        }
        catch (ArgumentOutOfRangeException) { return Confirm("存在しない日付です。対象日を確認してください。"); }
        catch (ApiException ex) when (ex.Status == 422) { return Confirm(ex.Message); }
    }
    private Period Daily(DateOnly date) => new("daily", date, date, date, calendar.Previous(date));
    private Period Weekly(string kind, DateOnly start, DateOnly end)
    {
        if (start > end || end.DayNumber - start.DayNumber > 30)
            throw new ApiException(422, "invalid_interval", "終了日は開始日以降、対象期間は31日以内にしてください。");
        calendar.IsBusinessDay(start);
        calendar.IsBusinessDay(end);
        return new(kind, null, start, end);
    }
    private static Resolution Confirm(string message) => new("needs_confirmation", message);
    private static Resolution Resolved(Period period) => new("resolved",
        period.Kind == "daily"
            ? $"{period.ReportDate:yyyy-MM-dd}の日報。実績: {period.PreviousBusinessDate:yyyy-MM-dd}、予定: {period.ReportDate:yyyy-MM-dd}（JST）。"
            : $"{period.StartDate:yyyy-MM-dd}〜{period.EndDate:yyyy-MM-dd}の日報日で集約します。", period);
}

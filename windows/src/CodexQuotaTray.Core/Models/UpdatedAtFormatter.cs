namespace CodexQuotaTray.Core.Models;

public static class UpdatedAtFormatter
{
    public static readonly TimeSpan ExpiredAfter = TimeSpan.FromDays(7);

    public static string Format(
        DateTimeOffset updatedAtUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        var updatedAtLocal = TimeZoneInfo.ConvertTime(updatedAtUtc, timeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var value = updatedAtLocal.Date == nowLocal.Date
            ? $"更新于 {updatedAtLocal:HH:mm}"
            : $"更新于 {updatedAtLocal:M月d日}";
        return IsExpired(updatedAtUtc, nowUtc) ? $"{value} · 已过期" : value;
    }

    public static bool IsExpired(DateTimeOffset updatedAtUtc, DateTimeOffset nowUtc) =>
        nowUtc - updatedAtUtc > ExpiredAfter;
}

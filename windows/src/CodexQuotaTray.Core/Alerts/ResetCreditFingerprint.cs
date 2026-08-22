using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Alerts;

public static class ResetCreditFingerprint
{
    public static string Create(ResetCreditView credit) => Create(
        credit.ResetType,
        credit.GrantedAtUtc,
        credit.ExpiresAtUtc,
        credit.Title);

    public static string Create(
        string? resetType,
        DateTimeOffset? grantedAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? title)
    {
        var canonical = string.Join(
            '\u001f',
            Normalize(resetType),
            FormatTimestamp(grantedAtUtc),
            FormatTimestamp(expiresAtUtc),
            Normalize(title));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

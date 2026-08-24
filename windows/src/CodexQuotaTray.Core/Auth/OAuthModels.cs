using System.Text.Json;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.Auth;

public sealed record OAuthCredentials(
    string AccessToken,
    string RefreshToken,
    string? IdToken = null,
    string? AccountId = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? PlanType = null,
    string? Email = null,
    DateTimeOffset? LastRefreshUtc = null)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshWithoutExpiryAfter = TimeSpan.FromDays(8);

    public bool NeedsRefresh(DateTimeOffset nowUtc) =>
        ExpiresAtUtc is { } expires
            ? expires <= nowUtc + RefreshSkew
            : LastRefreshUtc is { } refreshed && refreshed + RefreshWithoutExpiryAfter <= nowUtc;

    public OAuthCredentials WithTokens(
        string accessToken,
        string refreshToken,
        string? idToken,
        DateTimeOffset nowUtc) => this with
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            IdToken = idToken ?? IdToken,
            AccountId = JwtClaims.GetAccountId(idToken) ?? JwtClaims.GetAccountId(accessToken) ?? AccountId,
            PlanType = JwtClaims.GetPlanType(idToken) ?? JwtClaims.GetPlanType(accessToken) ?? PlanType,
            ExpiresAtUtc = JwtClaims.GetExpiry(idToken) ?? JwtClaims.GetExpiry(accessToken),
            LastRefreshUtc = nowUtc,
        };
}

public enum OAuthFailureKind
{
    DeviceAuthDisabled,
    LoginRequired,
    Unauthorized,
    RefreshExpired,
    RefreshRevoked,
    RefreshReused,
    InvalidResponse,
    Network,
    Server,
}

public sealed class OAuthException : Exception
{
    public OAuthException(OAuthFailureKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public OAuthFailureKind Kind { get; }

    public int? StatusCode { get; }
}

public sealed record OAuthDeviceCode(
    string DeviceAuthId,
    string UserCode,
    string VerificationUrl,
    TimeSpan PollInterval,
    DateTimeOffset ExpiresAtUtc);

public sealed record OAuthDeviceAuthorization(
    string AuthorizationCode,
    string CodeChallenge,
    string CodeVerifier);

public sealed record OAuthQuotaWindow(
    string Slot,
    string? LimitId,
    string? LimitName,
    long? UsedPercent,
    long? RemainingPercent,
    long? WindowDurationSeconds,
    long? ResetAtUtcSeconds,
    string? BucketId);

public sealed record OAuthResetCreditSummary(
    long? AvailableCount,
    IReadOnlyList<OAuthResetCredit>? Credits,
    bool FieldPresent);

public sealed record OAuthResetCredit(
    string? Id,
    string? ResetType,
    string? Status,
    DateTimeOffset? GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Title,
    string? Description);

public sealed record OAuthUsageResult(
    string? PlanType,
    IReadOnlyList<OAuthQuotaWindow> Windows,
    OAuthResetCreditSummary? ResetCredits);

public sealed record OAuthProfileResult(
    string? AccountId,
    string? Email,
    string? PlanType,
    AccountUsageSummary? UsageSummary,
    IReadOnlyList<AccountUsageBucket>? DailyUsageBuckets);

internal static class JwtClaims
{
    public static string? GetAccountId(string? token)
    {
        var payload = Parse(token);
        return GetString(payload?.GetPropertyOrNull("https://api.openai.com/auth"), "chatgpt_account_id")
            ?? GetString(payload, "chatgpt_account_id")
            ?? GetString(payload, "account_id");
    }

    public static string? GetPlanType(string? token)
    {
        var payload = Parse(token);
        return GetString(payload?.GetPropertyOrNull("https://api.openai.com/auth"), "chatgpt_plan_type")
            ?? GetString(payload, "chatgpt_plan_type");
    }

    public static DateTimeOffset? GetExpiry(string? token)
    {
        var payload = Parse(token);
        var value = payload?.GetPropertyOrNull("exp");
        var seconds = value is { } number && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var integer)
            ? integer
            : value is { } text && text.ValueKind == JsonValueKind.String
                && long.TryParse(text.GetString(), out integer)
                    ? integer
                    : (long?)null;
        try
        {
            return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static JsonElement? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var base64 = parts[1].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(base64));
            return document.RootElement.Clone();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object }
        && value.Value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() is { Length: > 0 } text ? text : null
            : null;
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property
            : null;
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.Auth;

public sealed class OAuthClient : IDisposable
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string DefaultAuthBaseUrl = "https://auth.openai.com";
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    public const string ProfilesUrl = "https://chatgpt.com/backend-api/wham/profiles/me";

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly string authBaseUrl;
    private readonly string clientId;
    private readonly Action<string> diagnostics;

    public OAuthClient(
        HttpClient? httpClient = null,
        string authBaseUrl = DefaultAuthBaseUrl,
        string clientId = ClientId,
        Action<string>? diagnostics = null)
    {
        this.httpClient = httpClient ?? CreateDefaultHttpClient();
        ownsHttpClient = httpClient is null;
        this.authBaseUrl = authBaseUrl.TrimEnd('/');
        this.clientId = clientId;
        this.diagnostics = diagnostics ?? (message => System.Diagnostics.Trace.WriteLine(message));
    }

    internal void LogRefresh(string message)
    {
        try { diagnostics("OAuth refresh " + message); }
        catch (Exception) { /* Logging must not interfere with rotation. */ }
    }

    public async Task<OAuthDeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/accounts/deviceauth/usercode"))
        {
            Content = JsonContent(new { client_id = clientId }),
        };
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == 404)
        {
            throw new OAuthException(OAuthFailureKind.DeviceAuthDisabled, "设备代码登录当前不可用。", response.StatusCodeInt);
        }

        EnsureSuccess(response, "device authorization");
        using var document = ParseObject(response.Body, "device authorization");
        var deviceId = StringValue(document.RootElement, "device_auth_id", "deviceAuthId")
            ?? throw InvalidResponse("device authorization id");
        var userCode = StringValue(document.RootElement, "user_code", "usercode", "userCode")
            ?? throw InvalidResponse("user code");
        var intervalSeconds = Math.Clamp(NumberValue(document.RootElement, "interval") ?? 5, 1, 30);
        var expiresSeconds = Math.Clamp(NumberValue(document.RootElement, "expires_in", "expiresIn") ?? 900, 60, 900);
        return new OAuthDeviceCode(
            deviceId,
            userCode,
            Url("/codex/device"),
            TimeSpan.FromSeconds(intervalSeconds),
            DateTimeOffset.UtcNow.AddSeconds(expiresSeconds));
    }

    public async Task<OAuthDeviceAuthorization> PollDeviceAuthorizationAsync(
        OAuthDeviceCode device,
        IProgress<OAuthDeviceCode>? progress,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < device.ExpiresAtUtc)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/accounts/deviceauth/token"))
            {
                Content = JsonContent(new
                {
                    device_auth_id = device.DeviceAuthId,
                    user_code = device.UserCode,
                }),
            };
            var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is 200 or 201)
            {
                using var document = ParseObject(response.Body, "device authorization polling");
                var authorizationCode = StringValue(document.RootElement, "authorization_code", "authorizationCode")
                    ?? throw InvalidResponse("authorization code");
                var challenge = StringValue(document.RootElement, "code_challenge", "codeChallenge")
                    ?? throw InvalidResponse("code challenge");
                var verifier = StringValue(document.RootElement, "code_verifier", "codeVerifier")
                    ?? throw InvalidResponse("code verifier");
                return new OAuthDeviceAuthorization(authorizationCode, challenge, verifier);
            }

            if (response.StatusCode is not (403 or 404))
            {
                EnsureSuccess(response, "device authorization polling");
            }

            progress?.Report(device);
            await Task.Delay(device.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new OAuthException(OAuthFailureKind.LoginRequired, "设备代码登录已超时。");
    }

    public async Task<OAuthCredentials> ExchangeDeviceAuthorizationAsync(
        OAuthDeviceAuthorization authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/oauth/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorization.AuthorizationCode,
                ["redirect_uri"] = Url("/deviceauth/callback"),
                ["client_id"] = clientId,
                ["code_verifier"] = authorization.CodeVerifier,
            }),
        };
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "OAuth token exchange");
        using var document = ParseObject(response.Body, "OAuth token exchange");
        return ParseCredentials(document.RootElement, null);
    }

    public async Task<OAuthCredentials> RefreshAsync(
        OAuthCredentials credentials,
        CancellationToken cancellationToken,
        OAuthRefreshReason reason = OAuthRefreshReason.Proactive)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new OAuthException(OAuthFailureKind.LoginRequired, "refresh token unavailable");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/oauth/token"))
        {
            Content = JsonContent(new
            {
                client_id = clientId,
                grant_type = "refresh_token",
                refresh_token = credentials.RefreshToken,
            }),
        };
        LogRefresh($"reason={reason} phase=start");
        HttpPayload response;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogRefresh($"reason={reason} status=unavailable error.code=none rotation=unknown persisted=not_attempted");
            throw;
        }
        LogRefresh($"reason={reason} status={response.StatusCode} error.code={OAuthRefreshDiagnostics.SafeCode(RefreshErrorCode(response))}");
        if (!response.IsSuccess)
        {
            throw RefreshFailure(response);
        }

        using var document = ParseObject(response.Body, "OAuth refresh");
        var accessToken = StringValue(document.RootElement, "access_token", "accessToken")
            ?? credentials.AccessToken;
        var refreshToken = StringValue(document.RootElement, "refresh_token", "refreshToken")
            ?? credentials.RefreshToken;
        var idToken = StringValue(document.RootElement, "id_token", "idToken");
        LogRefresh($"reason={reason} rotation={refreshToken != credentials.RefreshToken}");
        return credentials.WithTokens(accessToken, refreshToken, idToken, DateTimeOffset.UtcNow);
    }

    public async Task<OAuthUsageResult> ReadUsageAsync(
        OAuthCredentials credentials,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedAsync(UsageUrl, credentials, cancellationToken).ConfigureAwait(false);
        EnsureAuthenticatedSuccess(response, "usage");
        using var document = ParseObject(response.Body, "usage");
        var result = ParseUsage(document.RootElement);
        if (result.ResetCredits is { AvailableCount: > 0 })
        {
            try
            {
                var detailResponse = await SendAuthenticatedAsync(ResetCreditsUrl, credentials, cancellationToken).ConfigureAwait(false);
                if (detailResponse.IsSuccess)
                {
                    using var detailDocument = ParseDocument(detailResponse.Body, "reset-credit details");
                    result = result with
                    {
                        ResetCredits = result.ResetCredits with { Credits = ParseCreditArray(detailDocument.RootElement) },
                    };
                }
            }
            catch (OAuthException)
            {
                // The count in wham/usage is authoritative. Details are best effort.
            }
        }

        return result;
    }

    public async Task<OAuthProfileResult> ReadProfileAsync(
        OAuthCredentials credentials,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedAsync(ProfilesUrl, credentials, cancellationToken).ConfigureAwait(false);
        EnsureAuthenticatedSuccess(response, "usage profile");
        using var document = ParseObject(response.Body, "usage profile");
        return ParseProfile(document.RootElement, credentials);
    }

    public static OAuthUsageResult ParseUsage(JsonElement root)
    {
        var windows = new List<OAuthQuotaWindow>();
        if (GetObject(root, "rate_limit", "rateLimit") is { } rateLimit)
        {
            AppendRateWindow(
                windows,
                rateLimit,
                "primary",
                null,
                null,
                QuotaBucketPolicy.CanonicalBucketId);
            AppendRateWindow(
                windows,
                rateLimit,
                "secondary",
                null,
                null,
                QuotaBucketPolicy.CanonicalBucketId);
        }

        if (GetArray(root, "additional_rate_limits", "additionalRateLimits") is { } additional)
        {
            var index = 0;
            foreach (var entry in additional.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || GetObject(entry, "rate_limit", "rateLimit") is not { } rate)
                {
                    index++;
                    continue;
                }

                var feature = StringValue(entry, "metered_feature", "meteredFeature");
                var name = StringValue(entry, "limit_name", "limitName") ?? feature;
                var id = StringValue(entry, "limit_id", "limitId", "id") ?? feature ?? name ?? $"index:{index}";
                var bucket = id;
                AppendRateWindow(windows, rate, "primary", $"additional:{id}:primary", name, bucket);
                AppendRateWindow(windows, rate, "secondary", $"additional:{id}:secondary", name, bucket);
                index++;
            }
        }

        OAuthResetCreditSummary? resetCredits = null;
        if (GetProperty(root, "rate_limit_reset_credits", "rateLimitResetCredits") is { } reset)
        {
            if (reset.ValueKind == JsonValueKind.Object)
            {
                resetCredits = new OAuthResetCreditSummary(
                    NumberValue(reset, "available_count", "availableCount"),
                    GetArray(reset, "credits", "reset_credits") is { } credits ? ParseCreditArray(credits) : null,
                    true);
            }
            else
            {
                resetCredits = new OAuthResetCreditSummary(null, null, true);
            }
        }

        return new OAuthUsageResult(
            StringValue(root, "plan_type", "planType"),
            windows,
            resetCredits);
    }

    public static OAuthProfileResult ParseProfile(JsonElement root, OAuthCredentials credentials)
    {
        var stats = GetObject(root, "stats", "usage", "tokenUsage") ?? root;
        AccountUsageSummary? summary = null;
        if (stats.ValueKind == JsonValueKind.Object)
        {
            summary = new AccountUsageSummary(
                NumberValue(stats, "lifetime_tokens", "lifetimeTokens"),
                NumberValue(stats, "peak_daily_tokens", "peakDailyTokens"),
                NumberValue(stats, "current_streak_days", "currentStreakDays"),
                NumberValue(stats, "longest_streak_days", "longestStreakDays"),
                NumberValue(stats, "longest_running_turn_sec", "longestRunningTurnSec"));
        }

        IReadOnlyList<AccountUsageBucket>? buckets = null;
        if (GetArray(stats, "daily_usage_buckets", "dailyUsageBuckets") is { } array)
        {
            buckets = array.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new AccountUsageBucket(
                    ParseDate(value, "start_date", "startDate"),
                    NumberValue(value, "tokens")))
                .ToArray();
        }

        return new OAuthProfileResult(
            StringValue(root, "account_id", "accountId", "chatgpt_account_id") ?? credentials.AccountId,
            StringValue(root, "email") ?? credentials.Email,
            StringValue(root, "plan_type", "planType") ?? credentials.PlanType,
            summary,
            buckets);
    }

    public OAuthCredentials ParseCredentials(JsonElement root, OAuthCredentials? previous)
    {
        var access = StringValue(root, "access_token", "accessToken")
            ?? throw InvalidResponse("access token");
        var refresh = StringValue(root, "refresh_token", "refreshToken") ?? previous?.RefreshToken
            ?? throw InvalidResponse("refresh token");
        var idToken = StringValue(root, "id_token", "idToken") ?? previous?.IdToken;
        return new OAuthCredentials(
            access,
            refresh,
            idToken,
            JwtClaims.GetAccountId(idToken) ?? JwtClaims.GetAccountId(access) ?? previous?.AccountId,
            JwtClaims.GetExpiry(idToken) ?? JwtClaims.GetExpiry(access),
            JwtClaims.GetPlanType(idToken) ?? JwtClaims.GetPlanType(access) ?? previous?.PlanType,
            previous?.Email,
            DateTimeOffset.UtcNow);
    }

    private async Task<HttpPayload> SendAuthenticatedAsync(
        string url,
        OAuthCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexQuotaTray/0.9.0");
        request.Headers.TryAddWithoutValidation("originator", "CodexQuotaTray Windows");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpPayload> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpPayload((int)response.StatusCode, body);
        }
        catch (HttpRequestException error)
        {
            throw new OAuthException(OAuthFailureKind.Network, "无法连接 OpenAI。", inner: error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException(OAuthFailureKind.Network, "OpenAI 请求超时。", inner: error);
        }
    }

    private void EnsureAuthenticatedSuccess(HttpPayload response, string operation)
    {
        if (response.StatusCode is 401 or 403)
        {
            throw new OAuthException(OAuthFailureKind.Unauthorized, $"{operation} authentication required.", response.StatusCode);
        }

        EnsureSuccess(response, operation);
    }

    private static void EnsureSuccess(HttpPayload response, string operation)
    {
        if (response.IsSuccess)
        {
            return;
        }

        throw new OAuthException(OAuthFailureKind.Server, $"{operation} request failed.", response.StatusCode);
    }

    private static string? RefreshErrorCode(HttpPayload response)
    {
        string? errorCode = null;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (GetProperty(document.RootElement, "error") is { } error)
                {
                    errorCode = error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : StringValue(error, "code", "error");
                }
                errorCode ??= StringValue(document.RootElement, "code");
            }
        }
        catch (JsonException)
        {
        }

        return errorCode;
    }

    private static OAuthException RefreshFailure(HttpPayload response)
    {
        var kind = RefreshErrorCode(response)?.ToLowerInvariant() switch
        {
            "refresh_token_expired" => OAuthFailureKind.RefreshExpired,
            "refresh_token_reused" => OAuthFailureKind.RefreshReused,
            "refresh_token_invalidated" => OAuthFailureKind.RefreshRevoked,
            "invalid_grant" when response.StatusCode == 400 => OAuthFailureKind.LoginRequired,
            _ when response.StatusCode == 401 => OAuthFailureKind.LoginRequired,
            _ => OAuthFailureKind.Server,
        };
        return new OAuthException(kind, "OAuth token refresh failed.", response.StatusCode);
    }

    private static OAuthException InvalidResponse(string field) =>
        new(OAuthFailureKind.InvalidResponse, $"OAuth response omitted {field}.");

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private JsonDocument ParseObject(string body, string operation)
    {
        var document = ParseDocument(body, operation);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new OAuthException(OAuthFailureKind.InvalidResponse, $"{operation} response was not an object.");
        }

        return document;
    }

    private static JsonDocument ParseDocument(string body, string operation)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException error)
        {
            throw new OAuthException(OAuthFailureKind.InvalidResponse, $"{operation} response was not JSON.", inner: error);
        }
    }

    private string Url(string path) => authBaseUrl + "/" + path.TrimStart('/');

    private static void AppendRateWindow(
        List<OAuthQuotaWindow> output,
        JsonElement rateLimit,
        string slot,
        string? limitId,
        string? limitName,
        string bucketId)
    {
        if (GetObject(rateLimit, $"{slot}_window", $"{slot}Window") is not { } window)
        {
            return;
        }

        output.Add(new OAuthQuotaWindow(
            slot,
            limitId,
            limitName,
            NumberValue(window, "used_percent", "usedPercent"),
            NumberValue(window, "remaining_percent", "remainingPercent"),
            NumberValue(window, "limit_window_seconds", "limitWindowSeconds"),
            NumberValue(window, "reset_at", "resetAt", "resets_at", "resetsAt"),
            bucketId));
    }

    private static IReadOnlyList<OAuthResetCredit> ParseCreditArray(JsonElement value)
    {
        var array = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : GetArray(value, "credits", "reset_credits")?.EnumerateArray().ToArray() ?? [];
        return array
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => new OAuthResetCredit(
                StringValue(entry, "id"),
                StringValue(entry, "reset_type", "resetType"),
                StringValue(entry, "status"),
                TimestampValue(entry, "granted_at", "grantedAt"),
                TimestampValue(entry, "expires_at", "expiresAt"),
                StringValue(entry, "title"),
                StringValue(entry, "description")))
            .ToArray();
    }

    private static DateOnly? ParseDate(JsonElement value, params string[] names)
    {
        var text = StringValue(value, names);
        return text is not null
            && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
    }

    private static DateTimeOffset? TimestampValue(JsonElement value, params string[] names)
    {
        var number = NumberValue(value, names);
        if (number is not null)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(number.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        var text = StringValue(value, names);
        return text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date
                : null;
    }

    private static long? NumberValue(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(value, out var property, name))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String
                && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static string? StringValue(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(value, out var property, name)
                && property.ValueKind == JsonValueKind.String
                && property.GetString()?.Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static JsonElement? GetObject(JsonElement value, params string[] names) =>
        GetProperty(value, names) is { } property && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static JsonElement? GetArray(JsonElement value, params string[] names) =>
        GetProperty(value, names) is { } property && property.ValueKind == JsonValueKind.Array
            ? property
            : null;

    private static JsonElement? GetProperty(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(value, out var property, name))
            {
                return property;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement value, out JsonElement property, string name)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property))
        {
            return true;
        }

        property = default;
        return false;
    }

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private readonly record struct HttpPayload(int StatusCode, string Body)
    {
        public bool IsSuccess => StatusCode is >= 200 and <= 299;

        public int StatusCodeInt => StatusCode;
    }
}

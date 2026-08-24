using System.Text.Json;
using System.Net;
using CodexQuotaTray.Core.Auth;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class AccountAndDataSourceTests
{
    [TestMethod]
    public async Task SettingsMigrateSourcesAndRejectMalformedEnumValues()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        await File.WriteAllTextAsync(
            paths.Settings,
            "{\"quotaDataSource\":\"OAuth\",\"tokenUsageDataSource\":\"not-a-source\"}");

        var settings = await new SettingsService(new JsonFileStore(), paths).LoadAsync(CancellationToken.None);

        Assert.AreEqual(QuotaDataSource.OAuth, settings.QuotaDataSource);
        Assert.AreEqual(TokenUsageDataSource.Local, settings.TokenUsageDataSource);
    }

    [TestMethod]
    public async Task QuotaAndTokenCachesRemainIsolatedBySource()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var quota = new QuotaCacheDocument(
            1,
            DateTimeOffset.UtcNow,
            "Plus",
            [],
            Source: QuotaDataSource.OAuth);
        var date = new DateOnly(2026, 8, 23);
        var token = new TokenUsageSnapshot(
            1,
            DateTimeOffset.UtcNow,
            "Asia/Shanghai",
            new TokenUsageSummary(0, 0, 0, 100, 100, date, 1, 1, 1),
            [new TokenUsageDay(date, 100, null, null, null, null)],
            0,
            0,
            date,
            date,
            TokenUsageDataSource.OAuth);

        await persistence.SaveQuotaCacheAsync(quota, CancellationToken.None);
        await persistence.SaveTokenUsageCacheAsync(token, CancellationToken.None);

        Assert.IsNull(await persistence.LoadQuotaCacheAsync(CancellationToken.None));
        Assert.IsNotNull(await persistence.LoadQuotaCacheAsync(CancellationToken.None, QuotaDataSource.OAuth));
        Assert.IsNull(await persistence.LoadTokenUsageCacheAsync(CancellationToken.None));
        Assert.IsNotNull(await persistence.LoadTokenUsageCacheAsync(CancellationToken.None, TokenUsageDataSource.OAuth));
    }

    [TestMethod]
    public void AccountUsageNormalizerPreservesUnavailableMetrics()
    {
        var today = new DateOnly(2026, 8, 23);
        var normalized = AccountTokenUsageNormalizer.Normalize(
            new AccountUsageReadResult(
                new AccountUsageSummary(LifetimeTokens: null, PeakDailyTokens: null, CurrentStreakDays: 2),
                [new AccountUsageBucket(today, 128)]),
            TokenUsageDataSource.CodexCli,
            today,
            new DateTimeOffset(2026, 8, 23, 3, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.AreEqual(128, normalized.Summary.TodayTokens);
        Assert.AreEqual(128, normalized.Summary.Last7DaysTokens);
        Assert.AreEqual(128, normalized.Summary.Last30DaysTokens);
        Assert.AreEqual("不可用", Format(normalized, TokenUsageMetricAvailability.Lifetime));
        Assert.IsTrue(normalized.AvailableMetrics.HasFlag(TokenUsageMetricAvailability.Peak));
        Assert.IsTrue(normalized.AvailableMetrics.HasFlag(TokenUsageMetricAvailability.CurrentStreak));
        Assert.IsNull(normalized.Days[0].InputTokens);
        Assert.AreEqual(TokenUsageDataSource.CodexCli, normalized.Source);
    }

    [TestMethod]
    public void AccountUsageNormalizerLeavesMissingDailyBucketsUnavailable()
    {
        var normalized = AccountTokenUsageNormalizer.Normalize(
            new AccountUsageReadResult(null, null),
            TokenUsageDataSource.OAuth,
            new DateOnly(2026, 8, 23),
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Utc);

        Assert.AreEqual(TokenUsageMetricAvailability.None, normalized.AvailableMetrics);
        Assert.IsEmpty(normalized.Days);
        Assert.AreEqual(TokenUsageDataSource.OAuth, normalized.Source);
    }

    [TestMethod]
    public void OAuthProfileParserAcceptsSnakeCaseAndKeepsMissingSummaryFieldsNull()
    {
        using var document = JsonDocument.Parse("""
            {
              "email": "account@example.invalid",
              "plan_type": "plus",
              "stats": {
                "lifetime_tokens": null,
                "current_streak_days": 3,
                "daily_usage_buckets": [
                  { "start_date": "2026-08-23", "tokens": 256 }
                ]
              }
            }
            """);

        var parsed = OAuthClient.ParseProfile(
            document.RootElement,
            new OAuthCredentials("access", "refresh"));

        Assert.AreEqual("account@example.invalid", parsed.Email);
        Assert.AreEqual("plus", parsed.PlanType);
        Assert.IsNull(parsed.UsageSummary?.LifetimeTokens);
        Assert.AreEqual(3, parsed.UsageSummary?.CurrentStreakDays);
        Assert.AreEqual(256, parsed.DailyUsageBuckets?[0].Tokens);
    }

    [TestMethod]
    public void OAuthPrimaryQuotaWindowsUseCanonicalCodexBucket()
    {
        using var document = JsonDocument.Parse("""
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 12,
                  "limit_window_seconds": 18000
                },
                "secondary_window": {
                  "used_percent": 73,
                  "limit_window_seconds": 604800
                }
              }
            }
            """);

        var parsed = OAuthClient.ParseUsage(document.RootElement);

        Assert.HasCount(2, parsed.Windows);
        Assert.IsTrue(parsed.Windows.All(window =>
            QuotaBucketPolicy.IsCanonical(window.BucketId)));
    }

    [TestMethod]
    public async Task OAuthDeviceCodeFlowUsesBoundedOfficialEndpoints()
    {
        var handler = new QueueHttpHandler(
            Response("{\"device_auth_id\":\"device-1\",\"user_code\":\"ABCD-EFGH\",\"interval\":1}"),
            Response("{\"authorization_code\":\"authorization-1\",\"code_challenge\":\"challenge-1\",\"code_verifier\":\"verifier-1\"}"),
            Response("{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\"}"));
        using var httpClient = new HttpClient(handler);
        using var client = new OAuthClient(httpClient, "https://auth.test");

        var device = await client.RequestDeviceCodeAsync(CancellationToken.None);
        var authorization = await client.PollDeviceAuthorizationAsync(device, null, CancellationToken.None);
        var credentials = await client.ExchangeDeviceAuthorizationAsync(authorization, CancellationToken.None);

        Assert.AreEqual("ABCD-EFGH", device.UserCode);
        Assert.AreEqual("authorization-1", authorization.AuthorizationCode);
        Assert.AreEqual("access-1", credentials.AccessToken);
        Assert.AreEqual("refresh-1", credentials.RefreshToken);
        CollectionAssert.AreEqual(
            new[] { "/api/accounts/deviceauth/usercode", "/api/accounts/deviceauth/token", "/oauth/token" },
            handler.Requests.Select(request => request.RequestUri!.AbsolutePath).ToArray());
    }

    [TestMethod]
    public async Task OAuthUsageMapsUnauthorizedWithoutExposingResponseBody()
    {
        var handler = new QueueHttpHandler(Response(HttpStatusCode.Forbidden, "secret-response-body"));
        using var httpClient = new HttpClient(handler);
        using var client = new OAuthClient(httpClient);

        var error = await Assert.ThrowsAsync<OAuthException>(
            () => client.ReadUsageAsync(new OAuthCredentials("access-token", "refresh-token", AccountId: "account-1"), CancellationToken.None));

        Assert.AreEqual(OAuthFailureKind.Unauthorized, error.Kind);
        Assert.AreEqual("Bearer access-token", handler.Requests[0].Headers.Authorization?.ToString());
        Assert.AreEqual("account-1", handler.Requests[0].Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.IsFalse(error.Message.Contains("secret-response-body", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OAuthRefreshClassifiesReusedRefreshToken()
    {
        var handler = new QueueHttpHandler(Response(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"refresh_token_reused\"}}"));
        using var httpClient = new HttpClient(handler);
        using var client = new OAuthClient(httpClient);

        var error = await Assert.ThrowsAsync<OAuthException>(
            () => client.RefreshAsync(new OAuthCredentials("access-token", "refresh-token"), CancellationToken.None));

        Assert.AreEqual(OAuthFailureKind.RefreshReused, error.Kind);
        Assert.AreEqual(HttpStatusCode.BadRequest, (HttpStatusCode)error.StatusCode!.Value);
    }

    [TestMethod]
    public async Task OAuthLogoutClearsCredentialManagerState()
    {
        var store = new MemoryCredentialStore(new OAuthCredentials("access-token", "refresh-token"));
        using var httpClient = new HttpClient(new QueueHttpHandler());
        await using var manager = new OAuthCredentialManager(store, new OAuthClient(httpClient));

        Assert.IsNotNull(await manager.GetValidAsync(CancellationToken.None));
        await manager.ClearAsync(CancellationToken.None);

        Assert.IsNull(await manager.GetValidAsync(CancellationToken.None));
        Assert.IsTrue(store.Cleared);
    }

    private static string Format(TokenUsageSnapshot snapshot, TokenUsageMetricAvailability metric) =>
        snapshot.AvailableMetrics.HasFlag(metric) ? snapshot.Summary.LifetimeTokens.ToString() : "不可用";

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexQuotaTray.AccountTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static HttpResponseMessage Response(string body) => Response(HttpStatusCode.OK, body);

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body),
    };

    private sealed class QueueHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(queue.Count > 0
                ? queue.Dequeue()
                : throw new InvalidOperationException("No queued OAuth response."));
        }
    }

    private sealed class MemoryCredentialStore(OAuthCredentials? value) : IOAuthCredentialStore
    {
        private OAuthCredentials? current = value;

        internal bool Cleared { get; private set; }

        public Task<OAuthCredentials?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(current);

        public Task SaveAsync(OAuthCredentials credentials, CancellationToken cancellationToken)
        {
            current = credentials;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            current = null;
            Cleared = true;
            return Task.CompletedTask;
        }
    }
}

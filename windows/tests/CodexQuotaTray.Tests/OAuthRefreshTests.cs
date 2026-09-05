using System.Net;
using System.Text.Json;
using CodexQuotaTray.Core.Auth;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class OAuthRefreshTests
{
    [TestMethod]
    [DataRow(400, "refresh_token_expired", OAuthFailureKind.RefreshExpired)]
    [DataRow(403, "REFRESH_TOKEN_REUSED", OAuthFailureKind.RefreshReused)]
    [DataRow(400, "refresh_token_invalidated", OAuthFailureKind.RefreshRevoked)]
    [DataRow(400, "invalid_grant", OAuthFailureKind.LoginRequired)]
    [DataRow(401, "unknown", OAuthFailureKind.LoginRequired)]
    [DataRow(403, "unknown", OAuthFailureKind.Server)]
    [DataRow(403, "invalid_grant", OAuthFailureKind.Server)]
    [DataRow(500, "invalid_grant", OAuthFailureKind.Server)]
    public async Task ClassifiesOfficialErrorShapes(int status, string code, OAuthFailureKind expected)
    {
        foreach (var body in new[] { JsonSerializer.Serialize(new { error = new { code } }),
            JsonSerializer.Serialize(new { error = code }), JsonSerializer.Serialize(new { code }) })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(body, status))));
            using var client = new OAuthClient(http);
            var error = await Assert.ThrowsAsync<OAuthException>(() => client.RefreshAsync(Original(), CancellationToken.None));
            Assert.AreEqual(expected, error.Kind);
            Assert.AreEqual(status, error.StatusCode);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Unknown403PreservesCredentials(bool recovery)
    {
        var original = Original() with { LastRefreshUtc = DateTimeOffset.UtcNow.AddDays(-9) };
        var store = new Store(original);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("<html>blocked private body</html>", 403))));
        await using var manager = new OAuthCredentialManager(store, new OAuthClient(http));
        var error = await Assert.ThrowsAsync<OAuthException>(() => recovery
            ? manager.RefreshAfterUnauthorizedAsync(original, CancellationToken.None)
            : manager.GetValidAsync(CancellationToken.None));
        Assert.AreEqual(OAuthFailureKind.Server, error.Kind);
        Assert.AreEqual(original, store.Value);
        Assert.AreEqual(0, store.ClearCalls);
    }

    [TestMethod]
    [DataRow("refresh_token_expired")]
    [DataRow("refresh_token_reused")]
    [DataRow("refresh_token_invalidated")]
    [DataRow("invalid_grant")]
    public async Task TerminalFailureClearsBothRefreshPaths(string code)
    {
        foreach (var recovery in new[] { false, true })
        {
            var original = Original() with { LastRefreshUtc = DateTimeOffset.UtcNow.AddDays(-9) };
            var store = new Store(original);
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new { error = code }), 400))));
            await using var manager = new OAuthCredentialManager(store, new OAuthClient(http));
            await Assert.ThrowsAsync<OAuthException>(() => recovery
                ? manager.RefreshAfterUnauthorizedAsync(original, CancellationToken.None)
                : manager.GetValidAsync(CancellationToken.None));
            Assert.IsNull(store.Value);
            Assert.IsNull(await manager.GetValidAsync(CancellationToken.None));
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConcurrentRecoveryUsesLatestTokensEvenWithoutRotation(bool rotate)
    {
        var original = Original();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var logs = new List<string>();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            Interlocked.Increment(ref calls);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual(3, body.RootElement.EnumerateObject().Count());
            Assert.AreEqual(OAuthClient.ClientId, body.RootElement.GetProperty("client_id").GetString());
            Assert.AreEqual("refresh_token", body.RootElement.GetProperty("grant_type").GetString());
            Assert.AreEqual(original.RefreshToken, body.RootElement.GetProperty("refresh_token").GetString());
            entered.SetResult();
            await release.Task;
            return Response(JsonSerializer.Serialize(new { access_token = "new-access", refresh_token = rotate ? "new-refresh" : original.RefreshToken }));
        }));
        var store = new Store(original);
        await using var manager = new OAuthCredentialManager(store, new OAuthClient(http, diagnostics: logs.Add));
        var first = manager.RefreshAfterUnauthorizedAsync(original, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = manager.RefreshAfterUnauthorizedAsync(original, CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(results[0], results[1]);
        Assert.AreEqual(results[0], store.Value);
        Assert.IsTrue(logs.Any(line => line.Contains("reason=UnauthorizedRecovery status=200 error.code=none", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(line => line.Contains($"rotation={rotate}", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(line => line.Contains("persisted=true", StringComparison.Ordinal)));
        Assert.IsFalse(logs.Any(line => line.Contains("new-access", StringComparison.Ordinal) || line.Contains("new-refresh", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedPersistenceRetriesNewTokensBeforeConcurrentRecovery(bool clearFails)
    {
        var original = Original() with { LastRefreshUtc = DateTimeOffset.UtcNow.AddDays(-9) };
        var store = new Store(original) { FailSave = true, FailClear = clearFails };
        var calls = 0;
        var logs = new List<string>();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("""{"access_token":"new-access","refresh_token":"new-refresh"}"""));
        }));
        await using var manager = new OAuthCredentialManager(store, new OAuthClient(http, diagnostics: logs.Add));
        await Assert.ThrowsAsync<OAuthException>(() => manager.GetValidAsync(CancellationToken.None));
        if (!clearFails) Assert.IsNull(store.Value);
        await Assert.ThrowsAsync<OAuthException>(() => manager.RefreshAfterUnauthorizedAsync(original, CancellationToken.None));
        Assert.AreEqual(1, calls);
        store.FailSave = false;
        var results = await Task.WhenAll(manager.GetValidAsync(CancellationToken.None), manager.RefreshAfterUnauthorizedAsync(original, CancellationToken.None));
        Assert.AreEqual("new-refresh", results[0]!.RefreshToken);
        Assert.AreEqual(results[0], results[1]);
        Assert.AreEqual(results[0], store.Value);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(store.Attempts.All(value => value.RefreshToken == "new-refresh"));
        Assert.IsTrue(logs.Any(line => line.Contains("persisted=false", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(line => line.Contains("persisted=true", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancellationAfterRotationDoesNotSkipPersistence()
    {
        using var cancellation = new CancellationTokenSource();
        var original = Original();
        var store = new Store(original);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("""{"refresh_token":"new-refresh"}"""))));
        using var client = new OAuthClient(http, diagnostics: message =>
        {
            if (message.Contains("rotation=True", StringComparison.Ordinal)) cancellation.Cancel();
        });
        await using var manager = new OAuthCredentialManager(store, client);
        var result = await manager.RefreshAfterUnauthorizedAsync(original, cancellation.Token);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual("new-refresh", result!.RefreshToken);
        Assert.AreEqual(original.AccessToken, result.AccessToken);
        Assert.AreEqual(result, store.Value);
    }

    [TestMethod]
    public async Task UnknownErrorCodeIsFingerprintedWithoutLoggingPrivateBody()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("""{"error":{"code":"private@example.test-secret","message":"private response"}}""", 403))));
        using var client = new OAuthClient(http, diagnostics: logs.Add);
        await Assert.ThrowsAsync<OAuthException>(() => client.RefreshAsync(Original(), CancellationToken.None));
        Assert.IsTrue(logs.Any(line => line.Contains("reason=Proactive status=403 error.code=unknown_", StringComparison.Ordinal)));
        Assert.IsFalse(logs.Any(line => line.Contains("private", StringComparison.Ordinal)));
    }

    private static OAuthCredentials Original() => new("old-access", "old-refresh");
    private static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Store(OAuthCredentials value) : IOAuthCredentialStore
    {
        internal OAuthCredentials? Value = value;
        internal bool FailSave;
        internal bool FailClear;
        internal int ClearCalls;
        internal List<OAuthCredentials> Attempts = [];
        public Task<OAuthCredentials?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task SaveAsync(OAuthCredentials credentials, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(credentials);
            if (FailSave) throw new IOException("private storage failure");
            Value = credentials;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            if (FailClear) throw new IOException("private storage failure");
            Value = null;
            return Task.CompletedTask;
        }
    }
}

using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.Auth;

public sealed record WindowsAccountStatus(
    bool CodexCliAvailable,
    AccountReadResult? CodexCliAccount,
    bool OAuthAvailable,
    AccountReadResult? OAuthAccount,
    DateTimeOffset? CheckedAtUtc,
    string? ErrorText);

public sealed class WindowsAccountService(
    ICodexAppServerClientFactory cliFactory,
    OAuthCredentialManager oauthCredentials) : IAsyncDisposable
{
    private readonly SemaphoreSlim cliGate = new(1, 1);
    private bool disposed;

    public ICodexAppServerClientFactory OAuthFactory => new OAuthAppServerClientFactory(oauthCredentials);

    public async Task<WindowsAccountStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        AccountReadResult? cliAccount = null;
        AccountReadResult? oauthAccount = null;
        var cliAvailable = false;
        string? errorText = null;

        await cliGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cli = cliFactory.Create();
            await cli.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cliAccount = await cli.ReadAccountAsync(cancellationToken).ConfigureAwait(false);
            cliAvailable = true;
        }
        catch (CodexClientException error)
        {
            errorText = error.Kind == CodexClientErrorKind.MethodNotFound
                ? "当前 Codex CLI 不支持账户信息读取。"
                : "无法读取 Codex CLI 账户状态。";
        }
        finally
        {
            cliGate.Release();
        }

        var oauthAvailable = false;
        try
        {
            var credentials = await oauthCredentials.GetValidAsync(cancellationToken).ConfigureAwait(false);
            if (credentials is not null)
            {
                var profile = await oauthCredentials.Client.ReadProfileAsync(credentials, cancellationToken).ConfigureAwait(false);
                oauthAccount = new AccountReadResult(false, "oauth", profile.Email, profile.PlanType);
                oauthAvailable = true;
            }
        }
        catch (OAuthException error)
        {
            errorText ??= error.Kind is OAuthFailureKind.LoginRequired or OAuthFailureKind.Unauthorized
                ? "OAuth 账户需要重新登录。"
                : "无法读取 OAuth 账户状态。";
        }

        return new WindowsAccountStatus(
            cliAvailable,
            cliAccount,
            oauthAvailable,
            oauthAccount,
            DateTimeOffset.UtcNow,
            errorText);
    }

    public Task<OAuthDeviceCode> RequestOAuthDeviceCodeAsync(CancellationToken cancellationToken) =>
        oauthCredentials.Client.RequestDeviceCodeAsync(cancellationToken);

    public async Task<OAuthCredentials> CompleteOAuthLoginAsync(
        OAuthDeviceCode device,
        IProgress<OAuthDeviceCode>? progress,
        CancellationToken cancellationToken)
    {
        var authorization = await oauthCredentials.Client
            .PollDeviceAuthorizationAsync(device, progress, cancellationToken)
            .ConfigureAwait(false);
        var credentials = await oauthCredentials.Client
            .ExchangeDeviceAuthorizationAsync(authorization, cancellationToken)
            .ConfigureAwait(false);
        var profile = await oauthCredentials.Client
            .ReadProfileAsync(credentials, cancellationToken)
            .ConfigureAwait(false);
        credentials = credentials with
        {
            AccountId = profile.AccountId ?? credentials.AccountId,
            Email = profile.Email ?? credentials.Email,
            PlanType = profile.PlanType ?? credentials.PlanType,
        };
        await oauthCredentials.SetAsync(credentials, cancellationToken).ConfigureAwait(false);
        return credentials;
    }

    public Task LogoutOAuthAsync(CancellationToken cancellationToken) =>
        oauthCredentials.ClearAsync(cancellationToken);

    public async Task<bool> HasUsableOAuthCredentialsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return await oauthCredentials.GetValidAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> HasUsableCodexCliAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await cliGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cli = cliFactory.Create();
            await cli.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var account = await cli.ReadAccountAsync(cancellationToken).ConfigureAwait(false);
            return !account.RequiresOpenAiAuth;
        }
        catch (CodexClientException)
        {
            return false;
        }
        finally
        {
            cliGate.Release();
        }
    }

    public async Task<AccountUsageReadResult> ReadCodexCliUsageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await cliGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cli = cliFactory.Create();
            await cli.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await cli.ReadAccountUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cliGate.Release();
        }
    }

    public async Task<AccountUsageReadResult> ReadOAuthUsageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var credentials = await oauthCredentials.GetValidAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new OAuthException(OAuthFailureKind.LoginRequired, "请先登录 OAuth 账户。");
        var profile = await oauthCredentials.Client.ReadProfileAsync(credentials, cancellationToken).ConfigureAwait(false);
        return new AccountUsageReadResult(profile.UsageSummary, profile.DailyUsageBuckets);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cliGate.Dispose();
        await oauthCredentials.DisposeAsync().ConfigureAwait(false);
    }
}

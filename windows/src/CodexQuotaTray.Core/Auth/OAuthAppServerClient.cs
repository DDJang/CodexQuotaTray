using System.Text.Json;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.Auth;

public sealed class OAuthAppServerClientFactory(OAuthCredentialManager credentials) : ICodexAppServerClientFactory
{
    public ICodexAppServerClient Create() => new OAuthAppServerClient(credentials);
}

public sealed class OAuthAppServerClient(OAuthCredentialManager credentials) : ICodexAppServerClient
{
    private readonly object diagnosticsGate = new();
    private CodexDiagnosticSnapshot diagnostics = new();
    private bool disposed;
    private OAuthCredentials? connectedCredentials;

    public CodexDiagnosticSnapshot Diagnostics
    {
        get
        {
            lock (diagnosticsGate)
            {
                return diagnostics;
            }
        }
    }

    public async Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            connectedCredentials = await credentials.GetValidAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CodexClientException(CodexClientErrorKind.OAuthLoginRequired, "请先登录 OAuth 账户。");
            Update(value => value with
            {
                CliFound = false,
                CliVersion = "OAuth",
                AppServerStarted = true,
                InitializeSucceeded = true,
                LastError = null,
            });
            return new CodexSessionInfo("oauth", "oauth");
        }
        catch (OAuthException error)
        {
            var wrapped = Wrap(error);
            Update(value => value with { LastError = wrapped.Kind });
            throw wrapped;
        }
    }

    public Task<RateLimitsReadResult> ReadRateLimitsForRecoveryAsync(CancellationToken cancellationToken) =>
        ReadRateLimitsAsync(cancellationToken);

    public async Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var usage = await WithCredentialsAsync(
            (client, value, token) => client.ReadUsageAsync(value, token),
            cancellationToken).ConfigureAwait(false);
        var response = ToRateLimitsResponse(usage);
        Update(value => value with
        {
            RateLimitsReadSucceeded = true,
            ResetCreditsFieldPresent = usage.ResetCredits?.FieldPresent == true,
            AvailableCount = usage.ResetCredits?.AvailableCount,
            CreditDetailCount = usage.ResetCredits?.Credits?.Count,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            LastError = null,
        });
        return new RateLimitsReadResult(response, usage.ResetCredits?.FieldPresent == true);
    }

    public async Task<AccountReadResult> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var profile = await WithCredentialsAsync(
            (client, value, token) => client.ReadProfileAsync(value, token),
            cancellationToken).ConfigureAwait(false);
        return new AccountReadResult(
            RequiresOpenAiAuth: false,
            AccountType: "oauth",
            Email: profile.Email,
            PlanType: profile.PlanType);
    }

    public async Task<AccountUsageReadResult> ReadAccountUsageAsync(CancellationToken cancellationToken)
    {
        var profile = await WithCredentialsAsync(
            (client, value, token) => client.ReadProfileAsync(value, token),
            cancellationToken).ConfigureAwait(false);
        return new AccountUsageReadResult(profile.UsageSummary, profile.DailyUsageBuckets);
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        connectedCredentials = null;
        return ValueTask.CompletedTask;
    }

    private async Task<T> WithCredentialsAsync<T>(
        Func<OAuthClient, OAuthCredentials, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        OAuthCredentials? current;
        try
        {
            current = connectedCredentials
                ?? await credentials.GetValidAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthException error)
        {
            throw Wrap(error);
        }

        if (current is null)
        {
            throw new CodexClientException(CodexClientErrorKind.OAuthLoginRequired, "请先登录 OAuth 账户。");
        }
        try
        {
            return await operation(credentials.Client, current, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthException error) when (error.Kind == OAuthFailureKind.Unauthorized)
        {
            OAuthCredentials? refreshed;
            try
            {
                refreshed = await credentials.RefreshAfterUnauthorizedAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (OAuthException refreshError)
            {
                throw Wrap(refreshError);
            }

            if (refreshed is null)
            {
                throw Wrap(error);
            }

            connectedCredentials = refreshed;
            try
            {
                return await operation(credentials.Client, refreshed, cancellationToken).ConfigureAwait(false);
            }
            catch (OAuthException retryError)
            {
                throw Wrap(retryError);
            }
        }
        catch (OAuthException error)
        {
            throw Wrap(error);
        }
    }

    private void Update(Func<CodexDiagnosticSnapshot, CodexDiagnosticSnapshot> update)
    {
        lock (diagnosticsGate)
        {
            diagnostics = update(diagnostics);
        }
    }

    private static RateLimitsResponse ToRateLimitsResponse(OAuthUsageResult usage)
    {
        var groups = usage.Windows
            .GroupBy(window => window.BucketId ?? QuotaBucketPolicy.CanonicalBucketId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ToRateLimitSnapshot(group.Key, usage.PlanType, group),
                StringComparer.Ordinal);
        RateLimitResetCreditsSummary? reset = null;
        if (usage.ResetCredits is { } summary)
        {
            reset = new RateLimitResetCreditsSummary
            {
                AvailableCount = summary.AvailableCount,
                Credits = summary.Credits?.Select(ToResetCredit).ToList(),
            };
        }

        return new RateLimitsResponse
        {
            RateLimits = groups.Count == 0 ? null : groups.Values.First(),
            RateLimitsByLimitId = groups.Count == 0 ? null : groups,
            RateLimitResetCredits = reset,
        };
    }

    private static RateLimitSnapshot ToRateLimitSnapshot(
        string bucket,
        string? planType,
        IEnumerable<OAuthQuotaWindow> windows)
    {
        var array = windows.ToArray();
        return new RateLimitSnapshot
        {
            LimitId = bucket,
            LimitName = array.Select(window => window.LimitName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            PlanType = planType,
            Primary = ToWindow(array.FirstOrDefault(window => window.Slot == "primary")),
            Secondary = ToWindow(array.FirstOrDefault(window => window.Slot == "secondary")),
        };
    }

    private static RateLimitWindow? ToWindow(OAuthQuotaWindow? window)
    {
        if (window is null)
        {
            return null;
        }

        var used = window.UsedPercent ?? (window.RemainingPercent is { } remaining ? 100 - remaining : (long?)null);
        return new RateLimitWindow
        {
            UsedPercent = used,
            WindowDurationMinutes = window.WindowDurationSeconds is > 0
                ? Math.Max(1, (long)Math.Round(window.WindowDurationSeconds.Value / 60d))
                : null,
            ResetsAt = window.ResetAtUtcSeconds,
        };
    }

    private static RateLimitResetCredit ToResetCredit(OAuthResetCredit credit) => new()
    {
        Id = credit.Id,
        ResetType = credit.ResetType,
        Status = credit.Status,
        GrantedAt = credit.GrantedAtUtc is { } granted
            ? JsonSerializer.SerializeToElement(granted.ToUnixTimeSeconds())
            : null,
        ExpiresAt = credit.ExpiresAtUtc is { } expires
            ? JsonSerializer.SerializeToElement(expires.ToUnixTimeSeconds())
            : null,
        Title = credit.Title,
        Description = credit.Description,
    };

    private static CodexClientException Wrap(OAuthException error) =>
        new(
            error.Kind switch
            {
                OAuthFailureKind.LoginRequired => CodexClientErrorKind.OAuthLoginRequired,
                OAuthFailureKind.Unauthorized => CodexClientErrorKind.OAuthLoginRequired,
                OAuthFailureKind.RefreshExpired or OAuthFailureKind.RefreshRevoked or OAuthFailureKind.RefreshReused => CodexClientErrorKind.OAuthRefreshFailed,
                OAuthFailureKind.Network => CodexClientErrorKind.OAuthNetwork,
                OAuthFailureKind.InvalidResponse => CodexClientErrorKind.OAuthProtocol,
                _ => CodexClientErrorKind.RemoteError,
            },
            error.Message,
            error);

}

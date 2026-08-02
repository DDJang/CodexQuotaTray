using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTray.Core.Protocol;

public enum CodexClientErrorKind
{
    CliNotFound,
    CliVersionProbeFailed,
    ProcessStartFailed,
    InitializeTimeout,
    InitializeRejected,
    MethodNotFound,
    RequestTimeout,
    Cancelled,
    TransportClosed,
    Protocol,
    ShutdownFailed,
    RemoteError,
}

public sealed class CodexClientException : Exception
{
    public CodexClientException(CodexClientErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public CodexClientErrorKind Kind { get; }

}

public sealed record CodexSessionInfo(string? CliVersion, string? RuntimeVersion);

public sealed record CodexDiagnosticSnapshot(
    bool CliFound = false,
    string? CliVersion = null,
    bool AppServerStarted = false,
    bool InitializeSucceeded = false,
    bool RateLimitsReadSucceeded = false,
    int WindowCount = 0,
    bool ResetCreditsFieldPresent = false,
    long? AvailableCount = null,
    int? CreditDetailCount = null,
    DateTimeOffset? LastSuccessUtc = null,
    CodexClientErrorKind? LastError = null,
    long MalformedJsonCount = 0,
    bool StderrObserved = false);

public interface ICodexAppServerClient : IAsyncDisposable
{
    CodexDiagnosticSnapshot Diagnostics { get; }

    Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken);

    Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken);

    Task<RateLimitsReadResult> ReadRateLimitsForRecoveryAsync(CancellationToken cancellationToken) =>
        ReadRateLimitsAsync(cancellationToken);

    async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}

public interface ICodexAppServerClientFactory
{
    ICodexAppServerClient Create();
}

public sealed record RateLimitsReadResult(
    RateLimitsResponse Response,
    bool ResetCreditsFieldPresent,
    long IngressSequence = 0);

public sealed record RateLimitsUpdatedNotification(
    RateLimitsResponse Response,
    bool ResetCreditsFieldPresent,
    long IngressSequence = 0,
    bool IsOverflow = false,
    Action? IngressAcknowledgement = null)
{
    private int ingressAcknowledged;

    public void AcknowledgeIngress()
    {
        if (Interlocked.Exchange(ref ingressAcknowledged, 1) == 0)
        {
            IngressAcknowledgement?.Invoke();
        }
    }
}

public sealed class InitializeResponse
{
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("platformFamily")]
    public string? PlatformFamily { get; init; }

    [JsonPropertyName("platformOs")]
    public string? PlatformOs { get; init; }
}

public sealed class RateLimitsResponse
{
    [JsonPropertyName("rateLimits")]
    public RateLimitSnapshot? RateLimits { get; init; }

    [JsonPropertyName("rateLimitsByLimitId")]
    public Dictionary<string, RateLimitSnapshot>? RateLimitsByLimitId { get; init; }

    [JsonPropertyName("rateLimitResetCredits")]
    public RateLimitResetCreditsSummary? RateLimitResetCredits { get; init; }
}

public sealed class RateLimitSnapshot
{
    [JsonPropertyName("limitId")]
    public string? LimitId { get; init; }

    [JsonPropertyName("limitName")]
    public string? LimitName { get; init; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }

    [JsonPropertyName("primary")]
    public RateLimitWindow? Primary { get; init; }

    [JsonPropertyName("secondary")]
    public RateLimitWindow? Secondary { get; init; }
}

public sealed class RateLimitWindow
{
    [JsonPropertyName("usedPercent")]
    public long? UsedPercent { get; init; }

    [JsonPropertyName("windowDurationMins")]
    public long? WindowDurationMinutes { get; init; }

    [JsonPropertyName("resetsAt")]
    public long? ResetsAt { get; init; }
}

public sealed class RateLimitResetCreditsSummary
{
    [JsonPropertyName("availableCount")]
    public long? AvailableCount { get; init; }

    [JsonPropertyName("credits")]
    public List<RateLimitResetCredit>? Credits { get; init; }
}

public sealed class RateLimitResetCredit
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("expiresAt")]
    public JsonElement? ExpiresAt { get; init; }
}

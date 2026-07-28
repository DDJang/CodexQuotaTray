using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public static class TrayRegistrationPolicy
{
    public static IReadOnlyList<int> RetryDelaysMilliseconds { get; } = [0, 250, 500, 1000];

    public static TrayRegistrationState StateAfterAttempt(bool succeeded, int attemptNumber) =>
        succeeded
            ? TrayRegistrationState.Registered
            : attemptNumber >= RetryDelaysMilliseconds.Count
                ? TrayRegistrationState.Failed
                : TrayRegistrationState.RetryPending;
}

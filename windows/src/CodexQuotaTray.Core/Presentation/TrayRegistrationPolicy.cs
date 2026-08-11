using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public static class TrayRegistrationPolicy
{
    public static IReadOnlyList<int> RetryDelaysMilliseconds { get; } = [0, 250, 500, 1000];

    public static IReadOnlyList<int> VerificationDelaysMilliseconds { get; } = [250, 500, 1000, 2000];

    public static TrayRegistrationState StateAfterAttempt(bool confirmed, int attemptNumber) =>
        confirmed
            ? TrayRegistrationState.Registered
            : attemptNumber >= RetryDelaysMilliseconds.Count
                ? TrayRegistrationState.Failed
                : TrayRegistrationState.RetryPending;

    public static bool IsExplorerConfirmationSuccessful(
        int result,
        int left,
        int top,
        int right,
        int bottom) =>
        result >= 0 && right > left && bottom > top;
}

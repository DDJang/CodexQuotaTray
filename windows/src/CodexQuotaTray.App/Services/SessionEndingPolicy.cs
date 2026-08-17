using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.App.Services;

internal static class SessionEndingPolicy
{
    internal static bool IsConfirmed(uint message, ulong wParam) =>
        message == Interop.NativeMethods.WmEndSession && wParam != 0;

    internal static void ExitForWindowsUpdate(CrashSessionLog? crashSessionLog, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(exitApplication);
        crashSessionLog?.MarkExpectedTermination();
        exitApplication();
    }
}

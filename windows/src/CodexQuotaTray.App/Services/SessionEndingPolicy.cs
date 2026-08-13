namespace CodexQuotaTray.App.Services;

internal static class SessionEndingPolicy
{
    internal static bool IsConfirmed(uint message, ulong wParam) =>
        message == Interop.NativeMethods.WmEndSession && wParam != 0;
}

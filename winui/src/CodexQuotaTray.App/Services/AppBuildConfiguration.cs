namespace CodexQuotaTray.App.Services;

internal static class AppBuildConfiguration
{
#if CODEXQUOTATRAY_DEV
    internal const bool IsDevelopmentBuild = true;
#else
    internal const bool IsDevelopmentBuild = false;
#endif
}

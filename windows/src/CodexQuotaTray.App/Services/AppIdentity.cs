using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Services;

internal sealed record AppIdentity(
    string DisplayName,
    string DataDirectoryName,
    string StartupValueName,
    TrayIconIdentity TrayIcon,
    int TokenSyncPort,
    string TokenSyncDisplayNameSuffix,
    string TokenSyncDnsSdInstancePrefix)
{
    internal static AppIdentity Production { get; } = new(
        "CodexQuotaTray",
        "CodexQuotaTray",
        "CodexQuotaTray",
        TrayIconIdentity.Production,
        TokenUsageSyncServer.DefaultPort,
        string.Empty,
        "CodexQuotaTray");

    internal static AppIdentity Development { get; } = new(
        "CodexQuotaTray Dev",
        "CodexQuotaTray-Dev",
        "CodexQuotaTray Dev",
        TrayIconIdentity.Development,
        43822,
        " Dev",
        "CodexQuotaTray-Dev");

    internal static AppIdentity Preview { get; } = new(
        "CodexQuotaTray Preview",
        "CodexQuotaTray-WinUI-Preview",
        "CodexQuotaTray Preview",
        TrayIconIdentity.Preview,
        43823,
        " Preview",
        "CodexQuotaTray-Preview");

    internal static AppIdentity From(TrayIdentityMode mode) => mode switch
    {
        TrayIdentityMode.Production => Production,
        TrayIdentityMode.Development => Development,
        TrayIdentityMode.Preview => Preview,
        _ => throw new InvalidOperationException("Unknown application identity."),
    };
}

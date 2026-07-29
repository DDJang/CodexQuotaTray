using System.Diagnostics;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using Microsoft.Win32;

namespace CodexQuotaTray.App.Services;

internal sealed class SettingsPlatformActions(
    PreviewDataPaths paths,
    PreviewPersistence persistence,
    ProductionDataImporter importer) : ISettingsPlatformActions
{
    private const string StartupValueName = "CodexQuotaTray";

    public Task SetStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项注册表项。");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
            key.SetValue(StartupValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    public void OpenDataDirectory()
    {
        Directory.CreateDirectory(paths.Root);
        _ = Process.Start(new ProcessStartInfo(paths.Root) { UseShellExecute = true });
    }

    public Task<int> ImportProductionDataAsync(CancellationToken cancellationToken)
    {
        var preview = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray-WinUI-Preview");
        return importer.ImportAsync(preview, paths, cancellationToken);
    }

    public Task ClearQuotaCacheAsync() => persistence.ClearQuotaCacheAsync();
}

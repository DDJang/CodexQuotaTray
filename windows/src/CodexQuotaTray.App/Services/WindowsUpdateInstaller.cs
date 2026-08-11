using System.Diagnostics;
using System.ComponentModel;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowsUpdateInstaller
{
    internal bool TryStart(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

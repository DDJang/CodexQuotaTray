using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowsUpdateInstaller
{
    internal bool TryStart(string installerPath, string expectedSha256)
    {
        if (!File.Exists(installerPath)
            || expectedSha256.Length != 64
            || !expectedSha256.All(Uri.IsHexDigit))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

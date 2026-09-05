using System.Security.Cryptography;
using System.Text;

namespace CodexQuotaTray.Core.Auth;

public enum OAuthRefreshReason
{
    Proactive,
    UnauthorizedRecovery,
}

public static class OAuthRefreshDiagnostics
{
    // Unknown backend values are untrusted and may contain credentials or PII.
    public static string SafeCode(string? code) => code?.ToLowerInvariant() switch
    {
        null or "" => "none",
        "refresh_token_expired" => "refresh_token_expired",
        "refresh_token_reused" => "refresh_token_reused",
        "refresh_token_invalidated" => "refresh_token_invalidated",
        "invalid_grant" => "invalid_grant",
        _ => "unknown_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..12],
    };

    public static void Append(string path, string message)
    {
        lock (FileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
                {
                    File.WriteAllText(path, string.Empty);
                }

                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must not affect credential commits.
            }
        }
    }

    private static readonly object FileGate = new();
}

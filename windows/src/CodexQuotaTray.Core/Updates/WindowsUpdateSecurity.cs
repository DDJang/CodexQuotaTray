namespace CodexQuotaTray.Core.Updates;

public static class WindowsUpdateSecurity
{
    private static readonly string[] AllowedAssetHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public static bool IsAllowedAssetUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return AllowedAssetHosts.Any(allowed => string.Equals(host, allowed, StringComparison.Ordinal));
    }
}

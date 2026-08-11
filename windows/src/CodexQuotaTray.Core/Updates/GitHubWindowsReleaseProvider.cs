using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexQuotaTray.Core.Updates;

public sealed class GitHubWindowsReleaseProvider : IWindowsUpdateReleaseProvider, IDisposable
{
    public const string Repository = "DDJang/CodexQuotaTray";
    public const string ReleasesEndpoint = "https://api.github.com/repos/DDJang/CodexQuotaTray/releases?per_page=100";
    internal const int PageSize = 100;
    internal const int MaxPages = 3;
    private const string InstallerPrefix = "CodexQuotaTray-";
    private const string InstallerSuffix = "-setup.exe";
    private readonly HttpClient client;
    private readonly string endpoint;
    private readonly bool disposeClient;

    public GitHubWindowsReleaseProvider(HttpClient? client = null, string? endpoint = null)
    {
        this.client = client ?? CreateClient();
        this.endpoint = endpoint ?? ReleasesEndpoint;
        disposeClient = client is null;
    }

    public async Task<WindowsUpdateRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        WindowsUpdateRelease? latest = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildPageUri(page));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            latest = SelectLatest(latest, ParseLatestWindowsRelease(root));
            if (root.GetArrayLength() < PageSize)
            {
                break;
            }
        }

        return latest;
    }

    internal static WindowsUpdateRelease? ParseLatestWindowsRelease(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GitHub releases response must be an array.");
        }

        WindowsUpdateRelease? latest = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || GetBoolean(release, "draft")
                || GetBoolean(release, "prerelease"))
            {
                continue;
            }

            var tag = GetString(release, "tag_name");
            if (tag is null || !TryParseWindowsTag(tag, out var version))
            {
                continue;
            }

            var expectedInstaller = $"{InstallerPrefix}{version}{InstallerSuffix}";
            WindowsUpdateAsset? installer = null;
            WindowsUpdateAsset? checksums = null;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var urlText = GetString(asset, "browser_download_url");
                if (name is null || urlText is null || !Uri.TryCreate(urlText, UriKind.Absolute, out var url)
                    || !WindowsUpdateSecurity.IsAllowedAssetUri(url))
                {
                    continue;
                }

                long? size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : null;
                if (string.Equals(name, expectedInstaller, StringComparison.Ordinal))
                {
                    installer = new WindowsUpdateAsset(name, url, size);
                }
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.Ordinal))
                {
                    checksums = new WindowsUpdateAsset(name, url, size);
                }
            }

            if (installer is null || checksums is null)
            {
                continue;
            }

            var candidate = new WindowsUpdateRelease(
                tag,
                version,
                GetString(release, "name") ?? tag,
                GetString(release, "body") ?? string.Empty,
                ParsePublishedAt(release),
                installer,
                checksums);
            if (latest is null
                || candidate.Version.CompareTo(latest.Version) > 0
                || (candidate.Version == latest.Version
                    && candidate.PublishedAt.GetValueOrDefault() > latest.PublishedAt.GetValueOrDefault()))
            {
                latest = candidate;
            }
        }

        return latest;
    }

    internal static bool TryParseWindowsTag(string tag, out SemanticVersion version)
    {
        version = default;
        const string prefix = "windows-v";
        return tag.StartsWith(prefix, StringComparison.Ordinal)
            && SemanticVersion.TryParse(tag[prefix.Length..], out version);
    }

    public void Dispose()
    {
        if (disposeClient)
        {
            client.Dispose();
        }
    }

    private string BuildPageUri(int page) => page == 1
        ? endpoint
        : $"{endpoint}{(endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?')}page={page}";

    private static WindowsUpdateRelease? SelectLatest(
        WindowsUpdateRelease? current,
        WindowsUpdateRelease? candidate) => candidate is null
            ? current
            : current is null
                || candidate.Version.CompareTo(current.Version) > 0
                || (candidate.Version == current.Version
                    && candidate.PublishedAt.GetValueOrDefault() > current.PublishedAt.GetValueOrDefault())
                ? candidate
                : current;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexQuotaTray-Windows-Updater/1.0");
        return client;
    }

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ParsePublishedAt(JsonElement release)
    {
        var text = GetString(release, "published_at");
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }
}

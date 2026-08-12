using System.Text.Json;

namespace CodexQuotaTray.Core.Updates;

public sealed class StaticUpdateManifestProvider : IWindowsUpdateReleaseProvider, IDisposable
{
    public const string ManifestUrl =
        "https://raw.githubusercontent.com/DDJang/CodexQuotaTray/update-manifest/update-manifest.json";

    private readonly HttpClient client;
    private readonly string endpoint;
    private readonly bool disposeClient;

    public StaticUpdateManifestProvider(HttpClient? client = null, string? endpoint = null)
    {
        this.client = client ?? CreateClient();
        this.endpoint = endpoint ?? ManifestUrl;
        disposeClient = client is null;
    }

    public async Task<WindowsUpdateRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseWindowsRelease(document.RootElement);
    }

    internal static WindowsUpdateRelease ParseWindowsRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || !schemaVersion.TryGetInt32(out var parsedSchemaVersion)
            || parsedSchemaVersion != 1
            || !root.TryGetProperty("windows", out var platform)
            || platform.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Update manifest does not contain Windows information.");
        }

        var versionText = GetRequiredString(platform, "version");
        if (!SemanticVersion.TryParse(versionText, out var version))
        {
            throw new JsonException("Windows manifest version is invalid.");
        }

        var tag = GetRequiredString(platform, "tag");
        if (!string.Equals(tag, $"windows-v{version}", StringComparison.Ordinal))
        {
            throw new JsonException("Windows manifest tag does not match its version.");
        }

        if (!platform.TryGetProperty("installer", out var installerElement)
            || installerElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Windows manifest installer is missing.");
        }

        var expectedName = $"CodexQuotaTray-{version}-setup.exe";
        var installerName = GetRequiredString(installerElement, "name");
        if (!string.Equals(installerName, expectedName, StringComparison.Ordinal))
        {
            throw new JsonException("Windows manifest installer name is invalid.");
        }

        var installerUrl = GetAllowedUri(installerElement, "url");
        var installerSha256 = NormalizeSha256(GetRequiredString(installerElement, "sha256"));
        long? installerSize = installerElement.TryGetProperty("size", out var sizeElement)
            && sizeElement.TryGetInt64(out var parsedSize)
            && parsedSize >= 0
                ? parsedSize
                : null;
        return new WindowsUpdateRelease(
            tag,
            version,
            tag,
            GetOptionalString(platform, "releaseNotes") ?? string.Empty,
            DateTimeOffset.TryParse(GetOptionalString(platform, "publishedAt"), out var publishedAt)
                ? publishedAt
                : null,
            new WindowsUpdateAsset(installerName, installerUrl, installerSize),
            installerSha256);
    }

    public void Dispose()
    {
        if (disposeClient)
        {
            client.Dispose();
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        GetOptionalString(element, propertyName)
        ?? throw new JsonException($"Update manifest property '{propertyName}' is missing.");

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static Uri GetAllowedUri(JsonElement element, string propertyName)
    {
        var raw = GetRequiredString(element, propertyName);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !WindowsUpdateSecurity.IsAllowedAssetUri(uri))
        {
            throw new JsonException("Windows manifest installer URL is not trusted.");
        }

        return uri;
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new JsonException("Windows manifest SHA-256 is invalid.");
        }

        return normalized;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexQuotaTray-Windows-Updater/1.0");
        return client;
    }
}

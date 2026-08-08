using System.Security.Cryptography;

namespace CodexQuotaTray.Core.Persistence;

public sealed record TokenUsageSettings(int SchemaVersion, string PairingSecret);

public sealed class TokenUsageSettingsService(JsonFileStore store, PreviewDataPaths paths)
{
    public async Task<TokenUsageSettings> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await store.LoadAsync<TokenUsageSettings>(paths.TokenSyncSettings, cancellationToken).ConfigureAwait(false);
            if (existing is { SchemaVersion: 1 } && IsValid(existing.PairingSecret))
            {
                return existing;
            }
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        return await RegenerateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TokenUsageSettings> RegenerateAsync(CancellationToken cancellationToken)
    {
        var value = new TokenUsageSettings(1, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        await store.SaveAsync(paths.TokenSyncSettings, value, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static bool IsValid(string? value) => value is { Length: >= 64 };
}

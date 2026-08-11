using System.Security.Cryptography;

namespace CodexQuotaTray.Core.Persistence;

public sealed record TokenUsageSettings(int SchemaVersion, Guid DeviceId, string PairingSecret);

public sealed class TokenUsageSettingsService(JsonFileStore store, PreviewDataPaths paths)
{
    public async Task<TokenUsageSettings> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { SchemaVersion: 2 } && existing.DeviceId != Guid.Empty && IsValid(existing.PairingSecret))
        {
            return existing;
        }

        if (existing is { SchemaVersion: 1 } && IsValid(existing.PairingSecret))
        {
            var upgraded = new TokenUsageSettings(2, Guid.NewGuid(), existing.PairingSecret);
            await store.SaveAsync(paths.TokenSyncSettings, upgraded, cancellationToken).ConfigureAwait(false);
            return upgraded;
        }

        return await SaveAsync(new TokenUsageSettings(2, Guid.NewGuid(), NewSecret()), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TokenUsageSettings> RegenerateAsync(CancellationToken cancellationToken)
    {
        var existing = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        var deviceId = existing is not null && existing.DeviceId != Guid.Empty ? existing.DeviceId : Guid.NewGuid();
        var value = new TokenUsageSettings(2, deviceId, NewSecret());
        return await SaveAsync(value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenUsageSettings?> TryLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.LoadAsync<TokenUsageSettings>(paths.TokenSyncSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<TokenUsageSettings> SaveAsync(TokenUsageSettings value, CancellationToken cancellationToken)
    {
        await store.SaveAsync(paths.TokenSyncSettings, value, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool IsValid(string? value) => value is { Length: >= 64 };
}

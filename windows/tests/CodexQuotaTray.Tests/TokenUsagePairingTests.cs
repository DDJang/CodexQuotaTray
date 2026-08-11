using System.Text.Json;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenUsagePairingTests
{
    [TestMethod]
    public async Task DeviceIdentityIsStableAndSecretRotationPreservesIt()
    {
        using var directory = new TemporaryDirectory();
        var service = new TokenUsageSettingsService(new JsonFileStore(), new PreviewDataPaths(directory.Path));

        var first = await service.LoadOrCreateAsync(CancellationToken.None);
        var loaded = await service.LoadOrCreateAsync(CancellationToken.None);
        var rotated = await service.RegenerateAsync(CancellationToken.None);
        var afterRotation = await service.LoadOrCreateAsync(CancellationToken.None);

        Assert.AreNotEqual(Guid.Empty, first.DeviceId);
        Assert.AreEqual(first, loaded);
        Assert.AreEqual(first.DeviceId, rotated.DeviceId);
        Assert.AreNotEqual(first.PairingSecret, rotated.PairingSecret);
        Assert.AreEqual(rotated, afterRotation);
    }

    [TestMethod]
    public async Task LegacySecretMigratesToSchemaTwoWithNewStableIdentity()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            paths.TokenSyncSettings,
            JsonSerializer.Serialize(new { schemaVersion = 1, pairingSecret = new string('a', 64) }));

        var settings = await new TokenUsageSettingsService(new JsonFileStore(), paths).LoadOrCreateAsync(CancellationToken.None);

        Assert.AreEqual(2, settings.SchemaVersion);
        Assert.AreNotEqual(Guid.Empty, settings.DeviceId);
        Assert.AreEqual(new string('a', 64), settings.PairingSecret);
    }

    [TestMethod]
    public void PairingUriAndDiscoveryMetadataContainOnlyLanPairingData()
    {
        var deviceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");
        var pairing = new TokenUsagePairing(deviceId, "192.168.1.10", 43821, new string('b', 64), "Desk PC");

        var uri = pairing.ToUri();
        var metadata = new TokenUsageDiscoveryMetadata(deviceId, "Desk PC", 43821).TextAttributes;

        StringAssert.StartsWith(uri, "codexquota://pair?");
        StringAssert.Contains(uri, "deviceId=123e4567-e89b-12d3-a456-426614174000");
        StringAssert.Contains(uri, "host=192.168.1.10");
        StringAssert.Contains(uri, "port=43821");
        StringAssert.Contains(uri, "token=" + new string('b', 64));
        StringAssert.Contains(uri, "name=Desk%20PC");
        Assert.IsFalse(uri.Contains("openai", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(uri.Contains("email", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("123e4567-e89b-12d3-a456-426614174000", metadata["deviceId"]);
        Assert.AreEqual("Desk PC", metadata["name"]);
        Assert.AreEqual("43821", metadata["port"]);
        Assert.IsFalse(metadata.ContainsKey("secret"));
    }

    [TestMethod]
    public async Task PairingUriRejectsNonPrivateAddresses()
    {
        var pairing = new TokenUsagePairing(Guid.NewGuid(), "8.8.8.8", 43821, "secret", "Desk PC");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(pairing.ToUri));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexQuotaTray.PairingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

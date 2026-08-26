using CodexQuotaTray.App.Services;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class LanDiagnosticsTests
{
    [TestMethod]
    public async Task ListenerRequestStateSurvivesRestartAndTracksLastRemoteSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-LanDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var buffer = new LanDiagnosticBuffer(root))
            {
                buffer.Record("LAN listener start bind=192.168.1.58 port=43821 interfaceIndex=7");
                buffer.Record("LAN listener healthy=true bind=192.168.1.58 port=43821 interfaceIndex=7");
                buffer.Record("DNS-SD registration success interface=7");
                buffer.Record("LAN request remote=192.168.1.92 path=/v1/quota handler=entered=true result=SUCCESS status=200");
            }

            await using var restarted = new LanDiagnosticBuffer(root);
            var state = restarted.Snapshot;
            Assert.AreEqual("healthy", state.ListenerStatus);
            Assert.AreEqual("success", state.DnsSdStatus);
            Assert.AreEqual("192.168.1.92", state.LastRemoteAddress);
            Assert.AreEqual("SUCCESS", state.LastRequestResult);
            Assert.IsNotNull(state.LastSuccessUtc);
            StringAssert.Contains(restarted.CreateDiagnosticText(), "lastRemote=192.168.1.92");
            StringAssert.Contains(restarted.CreateDiagnosticText(), "result=SUCCESS");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void FormatterAndEventsNeverExposeAuthorizationPairingSecretOrToken()
    {
        using var buffer = new AsyncDisposableAdapter(new LanDiagnosticBuffer());
        buffer.Inner.Record("LAN request remote=192.168.1.92 Authorization: Bearer super-secret pairingSecret=super-secret token=access-token result=AUTH_FAILED status=401");

        var text = buffer.Inner.CreateDiagnosticText();
        Assert.IsFalse(text.Contains("super-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("access-token", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Authorization: Bearer", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(text, "AUTH_FAILED");
    }

    [TestMethod]
    public void WindowsFormatterIncludesListenerAndDnsSdFields()
    {
        var state = new LanDiagnosticState(
            Paired: true,
            DeviceId: "device-public-id",
            Endpoint: "192.168.1.58:43821",
            ListenerStatus: "healthy",
            BindAddress: "192.168.1.58",
            Port: 43821,
            InterfaceIndex: 7,
            DnsSdStatus: "success",
            DnsSdInterfaceIndex: 7,
            LastRemoteAddress: "192.168.1.92",
            LastRequestResult: "SUCCESS");

        var text = LanDiagnosticsFormatter.FormatWindows(
            "0.10.2",
            state,
            ["LAN listener healthy=true", "LAN request result=SUCCESS"],
            DateTimeOffset.UtcNow);

        StringAssert.Contains(text, "platform=Windows");
        StringAssert.Contains(text, "listener=healthy");
        StringAssert.Contains(text, "interfaceIndex=7");
        StringAssert.Contains(text, "dnsSd=success");
        StringAssert.Contains(text, "lastRemote=192.168.1.92");
        StringAssert.Contains(text, "Recent LAN events:");
    }

    [TestMethod]
    public async Task PersistentEventsRotateWithinThreeMegabytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-LanDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var buffer = new LanDiagnosticBuffer(root))
            {
                for (var index = 0; index < 7_000; index++)
                {
                    buffer.Record($"LAN event index={index} payload={new string('x', 420)}");
                    if (index % 64 == 0) await Task.Delay(1);
                }
            }

            var files = Directory.Exists(Path.Combine(root, "lan-diagnostics"))
                ? Directory.GetFiles(Path.Combine(root, "lan-diagnostics"), "events-*.log")
                : [];
            Assert.IsTrue(files.Length <= LanDiagnosticBuffer.SlotCount);
            Assert.IsTrue(files.All(file => new FileInfo(file).Length <= LanDiagnosticBuffer.MaximumSlotBytes));
            Assert.IsTrue(files.Sum(file => new FileInfo(file).Length) <=
                (long)LanDiagnosticBuffer.SlotCount * LanDiagnosticBuffer.MaximumSlotBytes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task PersistenceFailureIsFailOpenForTheLanOperation()
    {
        var file = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-LanDiagnostics-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(file, "not a directory");
            await using var buffer = new LanDiagnosticBuffer(file);
            buffer.Record("LAN request remote=192.168.1.92 result=HTTP_FAILED status=503");
            Assert.AreEqual("HTTP_FAILED", buffer.Snapshot.LastRequestResult);
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class AsyncDisposableAdapter(LanDiagnosticBuffer inner) : IDisposable
    {
        internal LanDiagnosticBuffer Inner { get; } = inner;

        public void Dispose() => Inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

using System.Diagnostics;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class LiveResourceSmokeTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async Task RealAppServer_SerialReadAndLifecycleMeasurement()
    {
        var binary = Environment.GetEnvironmentVariable("CODEXQUOTATRAY_LIVE_CODEX_BIN");
        if (string.IsNullOrWhiteSpace(binary))
        {
            Assert.Inconclusive("Set CODEXQUOTATRAY_LIVE_CODEX_BIN to explicitly enable the real-account smoke.");
        }

        var refreshes = int.TryParse(Environment.GetEnvironmentVariable("CODEXQUOTATRAY_LIVE_REFRESHES"), out var parsed)
            ? Math.Clamp(parsed, 1, 50)
            : 1;
        var process = Process.GetCurrentProcess();
        var beforeChildren = Process.GetProcessesByName("codex").Length;
        var beforeWorkingSet = process.WorkingSet64;
        var peakWorkingSet = beforeWorkingSet;
        await using (var client = new CodexAppServerClient(new CodexClientOptions(ExplicitCodexBinary: binary)))
        {
            await client.ConnectAsync(CancellationToken.None);
            for (var index = 0; index < refreshes; index++)
            {
                var value = await client.ReadRateLimitsAsync(CancellationToken.None);
                var normalized = QuotaNormalizer.Normalize(value);
                Assert.IsGreaterThan(0, normalized.Windows.Count, "The real account returned no displayable quota window.");
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                if (index + 1 < refreshes)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }
            }

            Console.WriteLine(
                $"live refreshes={refreshes}; cli={client.Diagnostics.CliVersion}; "
                + $"workingSetBeforeMiB={beforeWorkingSet / 1048576.0:F2}; "
                + $"workingSetAfterMiB={process.WorkingSet64 / 1048576.0:F2}; "
                + $"peakMiB={peakWorkingSet / 1048576.0:F2}; "
                + $"codexProcessesDuring={Process.GetProcessesByName("codex").Length}");
        }

        await Task.Delay(300);
        var afterChildren = Process.GetProcessesByName("codex").Length;
        Console.WriteLine($"codexProcessesBefore={beforeChildren}; codexProcessesAfter={afterChildren}");
        Assert.IsTrue(afterChildren <= beforeChildren, "The smoke left a Codex child process behind.");
    }
}

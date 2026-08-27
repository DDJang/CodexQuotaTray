using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Updates;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class WindowsUpdateTests
{
    [TestMethod]
    public void DownloadFormatting_UsesExpectedSizeAndSpeedPrecision()
    {
        var downloaded = (long)(35.8 * 1024 * 1024);
        var total = (long)(59.6 * 1024 * 1024);
        Assert.AreEqual("35.8 MB / 59.6 MB", WindowsUpdateDownloadFormatting.FormatSize(downloaded, total));
        Assert.AreEqual("35.8 MB", WindowsUpdateDownloadFormatting.FormatSize(downloaded, null));
        Assert.AreEqual("0.42 MB/s", WindowsUpdateDownloadFormatting.FormatSpeed(0.42 * 1024 * 1024));
        Assert.AreEqual("2.1 MB/s", WindowsUpdateDownloadFormatting.FormatSpeed(2.1 * 1024 * 1024));
    }

    [TestMethod]
    public void ManifestParser_UsesWindowsNodeRegardlessOfAndroidVersion()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "android": {"version":"9.9.9","tag":"android-v9.9.9"},
              "windows": {
                "version":"0.7.0",
                "tag":"windows-v0.7.0",
                "releaseNotes":"new",
                "publishedAt":"2026-08-01T00:00:00Z",
                "installer": {
                  "name":"CodexQuotaTray-0.7.0-setup.exe",
                  "url":"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.7.0/CodexQuotaTray-0.7.0-setup.exe",
                  "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size":10
                }
              }
            }
            """;

        using var document = JsonDocument.Parse(json);
        var release = StaticUpdateManifestProvider.ParseWindowsRelease(document.RootElement);

        Assert.AreEqual("windows-v0.7.0", release.TagName);
        Assert.AreEqual("CodexQuotaTray-0.7.0-setup.exe", release.Installer.Name);
        Assert.AreEqual(new string('A', 64), release.InstallerSha256);
    }

    [TestMethod]
    public void ManifestParser_RejectsMalformedPlatformData()
    {
        foreach (var json in new[]
        {
            "{}",
            WindowsManifestJson("0.7.0").Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            WindowsManifestJson("0.7.0").Replace(new string('a', 64), "bad-hash", StringComparison.Ordinal),
            WindowsManifestJson("0.7.0").Replace("windows-v0.7.0", "android-v0.7.0", StringComparison.Ordinal),
        })
        {
            using var document = JsonDocument.Parse(json);
            Assert.ThrowsExactly<JsonException>(() => StaticUpdateManifestProvider.ParseWindowsRelease(document.RootElement));
        }
    }

    [TestMethod]
    public async Task ManifestProvider_ReadsExactlyOneFixedDocument()
    {
        var requests = new List<Uri>();
        var handler = new StaticHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WindowsManifestJson("0.9.0")),
            };
        });
        using var client = new HttpClient(handler);
        using var provider = new StaticUpdateManifestProvider(client, "https://example.test/update-manifest.json");

        var release = await provider.GetLatestAsync(CancellationToken.None);

        Assert.IsNotNull(release);
        Assert.AreEqual("windows-v0.9.0", release.TagName);
        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual("/update-manifest.json", requests[0].AbsolutePath);
        Assert.IsFalse(StaticUpdateManifestProvider.ManifestUrl.Contains("api.github.com", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ManifestProvider_ReportsHttpAndNetworkFailures()
    {
        using var httpClient = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var httpFailure = new StaticUpdateManifestProvider(httpClient, "https://example.test/update-manifest.json");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => httpFailure.GetLatestAsync(CancellationToken.None));

        using var networkClient = new HttpClient(new ThrowingHttpHandler(new HttpRequestException("offline")));
        using var networkFailure = new StaticUpdateManifestProvider(networkClient, "https://example.test/update-manifest.json");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => networkFailure.GetLatestAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow("0.6.6", "0.6.5", true)]
    [DataRow("0.7.0", "0.6.99", true)]
    [DataRow("0.6.5", "0.6.5", false)]
    [DataRow("0.6.4", "0.6.5", false)]
    public void SemanticVersion_UsesStrictThreePartOrdering(string latestText, string currentText, bool newer)
    {
        Assert.IsTrue(SemanticVersion.TryParse(latestText, out var latest));
        Assert.IsTrue(SemanticVersion.TryParse(currentText, out var current));
        Assert.AreEqual(newer, latest.CompareTo(current) > 0);
    }

    [TestMethod]
    [DataRow("v0.6.6")]
    [DataRow("windows-v0.6")]
    [DataRow("windows-v01.2.3")]
    [DataRow("windows-v0.6.6-beta")]
    public void SemanticVersion_RejectsInvalidVersions(string value)
    {
        Assert.IsFalse(SemanticVersion.TryParse(value.StartsWith("windows-v", StringComparison.Ordinal) ? value[9..] : value, out _));
    }

    [TestMethod]
    public async Task Coordinator_AutomaticCheckIsSuppressedFor24HoursButManualBypasses()
    {
        var clock = new FakeUpdateClock(new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
        var provider = new FakeReleaseProvider(CreateRelease("0.7.0"));
        await using var coordinator = new WindowsUpdateCoordinator(provider, new MemoryStateStore(), Parse("0.6.6"), clock);

        var first = await coordinator.CheckAsync(WindowsUpdateCheckReason.Automatic, CancellationToken.None);
        var skipped = await coordinator.CheckAsync(WindowsUpdateCheckReason.Automatic, CancellationToken.None);
        var manual = await coordinator.CheckAsync(WindowsUpdateCheckReason.Manual, CancellationToken.None);

        Assert.AreEqual(WindowsUpdateCheckStatus.Available, first.Status);
        Assert.AreEqual(WindowsUpdateCheckStatus.Skipped, skipped.Status);
        Assert.AreEqual(WindowsUpdateCheckStatus.Available, manual.Status);
        Assert.AreEqual(2, provider.Calls);
    }

    [TestMethod]
    public async Task Coordinator_AutomaticCheckCanRunAfter24Hours()
    {
        var clock = new FakeUpdateClock(new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
        var provider = new FakeReleaseProvider(CreateRelease("0.7.0"));
        await using var coordinator = new WindowsUpdateCoordinator(provider, new MemoryStateStore(), Parse("0.6.6"), clock);

        await coordinator.CheckAsync(WindowsUpdateCheckReason.Automatic, CancellationToken.None);
        clock.UtcNowValue += TimeSpan.FromHours(24);
        await coordinator.CheckAsync(WindowsUpdateCheckReason.Automatic, CancellationToken.None);

        Assert.AreEqual(2, provider.Calls);
    }

    [TestMethod]
    public async Task Coordinator_ConcurrentChecksJoinOneProviderCall()
    {
        var provider = new FakeReleaseProvider(CreateRelease("0.7.0"));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Gate = firstStarted;
        await using var coordinator = new WindowsUpdateCoordinator(provider, new MemoryStateStore(), Parse("0.6.6"));

        var first = coordinator.CheckAsync(WindowsUpdateCheckReason.Manual, CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = coordinator.CheckAsync(WindowsUpdateCheckReason.Manual, CancellationToken.None);
        firstStarted.SetResult();

        await Task.WhenAll(first, second);
        Assert.AreEqual(1, provider.Calls);
    }

    [TestMethod]
    public async Task Coordinator_AutomaticFailureDoesNotNotify()
    {
        var provider = new FakeReleaseProvider(new HttpRequestException("offline"));
        await using var coordinator = new WindowsUpdateCoordinator(provider, new MemoryStateStore(), Parse("0.6.6"));
        var notifications = 0;
        coordinator.UpdateAvailable += (_, _) => notifications++;

        var result = await coordinator.CheckAsync(WindowsUpdateCheckReason.Automatic, CancellationToken.None);

        Assert.AreEqual(WindowsUpdateCheckStatus.Failed, result.Status);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public async Task Coordinator_EqualVersionIsUpToDate()
    {
        await using var coordinator = new WindowsUpdateCoordinator(
            new FakeReleaseProvider(CreateRelease("0.6.8")),
            new MemoryStateStore(),
            Parse("0.6.8"));

        var result = await coordinator.CheckAsync(WindowsUpdateCheckReason.Manual, CancellationToken.None);

        Assert.AreEqual(WindowsUpdateCheckStatus.UpToDate, result.Status);
    }

    [TestMethod]
    public async Task Coordinator_NotifiesEachAvailableVersionAtMostOnce()
    {
        var provider = new FakeReleaseProvider(CreateRelease("0.7.0"));
        var store = new MemoryStateStore();
        await using var coordinator = new WindowsUpdateCoordinator(provider, store, Parse("0.6.6"));
        var notifications = 0;
        coordinator.UpdateAvailable += (_, _) => notifications++;

        await coordinator.CheckAsync(WindowsUpdateCheckReason.Automatic, CancellationToken.None);
        await coordinator.CheckAsync(WindowsUpdateCheckReason.Manual, CancellationToken.None);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public async Task Downloader_ValidatesSha256AndUsesPartFile()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(handler);
            using var downloader = new WindowsUpdateDownloader(root, client);
            var result = await downloader.DownloadAsync(CreateRelease("0.7.0"), CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(File.Exists(result.InstallerPath));
            Assert.IsFalse(File.Exists(result.InstallerPath + ".part"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Downloader_RejectsBadHashAndUntrustedHost()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(handler);
            using var downloader = new WindowsUpdateDownloader(root, client);
            var badHash = await downloader.DownloadAsync(
                CreateRelease("0.7.0") with { InstallerSha256 = new string('0', 64) },
                CancellationToken.None);
            var untrusted = await downloader.DownloadAsync(
                CreateRelease("0.7.0") with
                {
                    Installer = new WindowsUpdateAsset(
                        "CodexQuotaTray-0.7.0-setup.exe",
                        new Uri("https://example.com/setup.exe")),
                },
                CancellationToken.None);

            Assert.IsFalse(badHash.Succeeded);
            Assert.IsFalse(untrusted.Succeeded);
            Assert.IsFalse(Directory.Exists(root) && Directory.EnumerateFiles(root, "*.part").Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Downloader_ReportsKnownTotalAndVerificationPhase()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var progress = new RecordingProgress();
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(handler);
            using var downloader = new WindowsUpdateDownloader(root, client);

            var result = await downloader.DownloadAsync(CreateRelease("0.7.0"), progress, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(progress.Values.Any(value =>
                value.Phase == WindowsUpdateDownloadPhase.Downloading
                && value.TotalBytes == bytes.Length
                && value.Percentage == 100));
            Assert.IsTrue(progress.Values.Any(value => value.Phase == WindowsUpdateDownloadPhase.Verifying));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Downloader_UsesIndeterminateProgressWhenTotalIsUnknown()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(bytes)),
        });
        var progress = new RecordingProgress();
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(handler);
            using var downloader = new WindowsUpdateDownloader(root, client);

            var result = await downloader.DownloadAsync(CreateRelease("0.7.0"), progress, CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(progress.Values.Any(value =>
                value.Phase == WindowsUpdateDownloadPhase.Downloading
                && value.TotalBytes is null
                && value.Percentage is null
                && value.BytesDownloaded > 0));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Downloader_CancellationCleansPartFileAndDoesNotPrepareInstaller()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("installer")),
            }));
            using var downloader = new WindowsUpdateDownloader(root, client);

            var result = await downloader.DownloadAsync(CreateRelease("0.7.0"), cancellation.Token);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.WasCancelled);
            Assert.IsFalse(Directory.Exists(root) && Directory.EnumerateFiles(root, "*.part").Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ServiceShutdownCancelsAndWaitsForActiveDownload()
    {
        var handler = new BlockingHttpHandler();
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-service-test-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(handler);
        var coordinator = new WindowsUpdateCoordinator(
            new FakeReleaseProvider(CreateRelease("0.7.0")), new MemoryStateStore(), Parse("0.6.6"));
        var service = new WindowsUpdateService(
            coordinator, new WindowsUpdateDownloader(root, client), new WindowsUpdateInstaller(), () => { });
        try
        {
            _ = await service.CheckAsync(manual: true, CancellationToken.None);
            var download = service.DownloadAsync(CancellationToken.None);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var shutdown = service.DisposeAsync().AsTask();

            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(shutdown.IsCompletedSuccessfully && !download.IsCompleted);
            var result = await download.WaitAsync(TimeSpan.FromSeconds(2));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(result.WasCancelled);
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ServiceHandlesCallerCancellationAndShutdownTogether()
    {
        var handler = new BlockingHttpHandler();
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-service-test-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var coordinator = new WindowsUpdateCoordinator(
            new FakeReleaseProvider(CreateRelease("0.7.0")), new MemoryStateStore(), Parse("0.6.6"));
        var service = new WindowsUpdateService(
            coordinator, new WindowsUpdateDownloader(root, client), new WindowsUpdateInstaller(), () => { });
        try
        {
            _ = await service.CheckAsync(manual: true, CancellationToken.None);
            var download = service.DownloadAsync(caller.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            caller.Cancel();
            var shutdown = service.DisposeAsync().AsTask();

            var result = await download.WaitAsync(TimeSpan.FromSeconds(2));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(result.WasCancelled);
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ServiceDisposeDrainsOperationPausedAfterAtomicEntry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-service-test-" + Guid.NewGuid().ToString("N"));
        var service = new WindowsUpdateService(
            new WindowsUpdateCoordinator(
                new FakeReleaseProvider((WindowsUpdateRelease?)null), new MemoryStateStore(), Parse("0.6.6")),
            new WindowsUpdateDownloader(root),
            new WindowsUpdateInstaller(),
            () => { },
            operationEntryHook: async () =>
            {
                entered.TrySetResult();
                await continueEntry.Task;
            });
        try
        {
            var operation = service.DownloadAsync(caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var shutdown = service.DisposeAsync().AsTask();
            var duplicateShutdown = service.DisposeAsync().AsTask();
            caller.Cancel();

            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsFalse(duplicateShutdown.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                service.CheckAsync(manual: true, CancellationToken.None));

            continueEntry.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await operation);
            await Task.WhenAll(shutdown, duplicateShutdown).WaitAsync(TimeSpan.FromSeconds(2));
            service.Dispose();
        }
        finally
        {
            continueEntry.TrySetResult();
            await service.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ServiceDoubleDisposeIsIdempotentAndSynchronousDisposeWaits()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-service-test-" + Guid.NewGuid().ToString("N"));
        var service = new WindowsUpdateService(
            new WindowsUpdateCoordinator(
                new FakeReleaseProvider((WindowsUpdateRelease?)null), new MemoryStateStore(), Parse("0.6.6")),
            new WindowsUpdateDownloader(root),
            new WindowsUpdateInstaller(),
            () => { });

        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());
        service.Dispose();
    }

    [TestMethod]
    public async Task ServiceRejectsOperationsAfterDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-service-test-" + Guid.NewGuid().ToString("N"));
        var service = new WindowsUpdateService(
            new WindowsUpdateCoordinator(
                new FakeReleaseProvider((WindowsUpdateRelease?)null), new MemoryStateStore(), Parse("0.6.6")),
            new WindowsUpdateDownloader(root),
            new WindowsUpdateInstaller(),
            () => { });
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CheckAsync(true, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.DownloadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.InstallPreparedAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.SetAutomaticChecksEnabledAsync(true, CancellationToken.None));
    }

    [TestMethod]
    public async Task Coordinator_AutoLaunchSettingDefaultsFalseAndRoundTrips()
    {
        var store = new MemoryStateStore();
        await using (var coordinator = new WindowsUpdateCoordinator(
            new FakeReleaseProvider((WindowsUpdateRelease?)null),
            store,
            Parse("0.6.6")))
        {
            Assert.IsFalse(coordinator.AutoLaunchInstallerAfterDownload);
            await coordinator.SetAutoLaunchInstallerAfterDownloadAsync(true, CancellationToken.None);
            Assert.IsTrue(coordinator.AutoLaunchInstallerAfterDownload);
        }

        await using (var reloaded = new WindowsUpdateCoordinator(
            new FakeReleaseProvider((WindowsUpdateRelease?)null),
            store,
            Parse("0.6.6")))
        {
            await reloaded.CheckAsync(WindowsUpdateCheckReason.Manual, CancellationToken.None);
            Assert.IsTrue(reloaded.AutoLaunchInstallerAfterDownload);
            await reloaded.SetAutoLaunchInstallerAfterDownloadAsync(false, CancellationToken.None);
        }

        var oldState = JsonSerializer.Deserialize<WindowsUpdateState>("{}");
        Assert.IsNotNull(oldState);
        Assert.IsFalse(oldState.AutoLaunchInstallerAfterDownload);
    }

    [TestMethod]
    public void ReleaseNotesMarkdown_RendersSupportedBlocksAndInlineStyles()
    {
        var blocks = ReleaseNotesMarkdown.Parse("""
            # Heading

            - **bold** *italic* `code` [link](https://github.com/DDJang/CodexQuotaTray)
            1. ordered
            > quote

            ```
            var value = 1;
            ```
            """);

        CollectionAssert.AreEqual(
            new[]
            {
                ReleaseNotesBlockKind.Heading,
                ReleaseNotesBlockKind.UnorderedListItem,
                ReleaseNotesBlockKind.OrderedListItem,
                ReleaseNotesBlockKind.Quote,
                ReleaseNotesBlockKind.CodeBlock,
            },
            blocks.Select(block => block.Kind).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ReleaseNotesInlineKind.Bold,
                ReleaseNotesInlineKind.Text,
                ReleaseNotesInlineKind.Italic,
                ReleaseNotesInlineKind.Text,
                ReleaseNotesInlineKind.InlineCode,
                ReleaseNotesInlineKind.Text,
                ReleaseNotesInlineKind.Link,
            },
            blocks[1].Inlines.Select(inline => inline.Kind).ToArray());
        Assert.AreEqual(1, blocks[2].ListIndex);
        Assert.AreEqual("https://github.com/DDJang/CodexQuotaTray", blocks[1].Inlines.Last().Url);
        StringAssert.Contains(blocks[4].Inlines[0].Text, "var value = 1;");
    }

    [TestMethod]
    public void ReleaseNotesMarkdown_UnsupportedMarkupFallsBackWithoutThrowing()
    {
        var blocks = ReleaseNotesMarkdown.Parse("<table>raw</table>\n\n| a | b |\n\n``` unfinished");

        Assert.IsTrue(blocks.Count >= 2);
        Assert.IsTrue(blocks.Any(block => block.Kind == ReleaseNotesBlockKind.Paragraph));
        Assert.AreEqual(ReleaseNotesBlockKind.CodeBlock, blocks[^1].Kind);
    }

    [TestMethod]
    public void Security_AllowsOnlyHttpsGitHubAssetHosts()
    {
        Assert.IsTrue(WindowsUpdateSecurity.IsAllowedAssetUri(new Uri("https://github.com/a")));
        Assert.IsTrue(WindowsUpdateSecurity.IsAllowedAssetUri(new Uri("https://objects.githubusercontent.com/a")));
        Assert.IsFalse(WindowsUpdateSecurity.IsAllowedAssetUri(new Uri("http://github.com/a")));
        Assert.IsFalse(WindowsUpdateSecurity.IsAllowedAssetUri(new Uri("https://example.com/a")));
        Assert.IsFalse(WindowsUpdateSecurity.IsAllowedAssetUri(new Uri("https://evil.github.com/a")));
    }

    private static SemanticVersion Parse(string value)
    {
        Assert.IsTrue(SemanticVersion.TryParse(value, out var version));
        return version;
    }

    private static string WindowsManifestJson(string version) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        android = new
        {
            version = "9.9.9",
            tag = "android-v9.9.9",
        },
        windows = new
        {
            version,
            tag = $"windows-v{version}",
            releaseNotes = "notes",
            publishedAt = "2026-08-11T00:00:00Z",
            installer = new
            {
                name = $"CodexQuotaTray-{version}-setup.exe",
                url = $"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v{version}/CodexQuotaTray-{version}-setup.exe",
                sha256 = new string('a', 64),
                size = 10,
            },
        },
    });

    private static WindowsUpdateRelease CreateRelease(string versionText)
    {
        var version = Parse(versionText);
        return new WindowsUpdateRelease(
            $"windows-v{version}",
            version,
            $"CodexQuotaTray {version}",
            "notes",
            DateTimeOffset.UtcNow,
            new WindowsUpdateAsset(
                $"CodexQuotaTray-{version}-setup.exe",
                new Uri($"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v{version}/CodexQuotaTray-{version}-setup.exe")),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("installer"))));
    }

    private sealed class FakeReleaseProvider : IWindowsUpdateReleaseProvider
    {
        private readonly WindowsUpdateRelease? release;
        private readonly Exception? error;

        internal FakeReleaseProvider(WindowsUpdateRelease? release) => this.release = release;

        internal FakeReleaseProvider(Exception error) => this.error = error;

        internal int Calls { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Gate { get; set; }

        public async Task<WindowsUpdateRelease?> GetLatestAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (error is not null)
            {
                throw error;
            }

            return release;
        }
    }

    private sealed class MemoryStateStore : IWindowsUpdateStateStore
    {
        internal WindowsUpdateState? State { get; private set; }

        public Task<WindowsUpdateState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task SaveAsync(WindowsUpdateState state, CancellationToken cancellationToken)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpdateClock(DateTimeOffset initial) : IUpdateClock
    {
        internal DateTimeOffset UtcNowValue { get; set; } = initial;
        public DateTimeOffset UtcNow => UtcNowValue;
    }

    private sealed class RecordingProgress : IProgress<WindowsUpdateDownloadProgress>
    {
        internal List<WindowsUpdateDownloadProgress> Values { get; } = [];

        public void Report(WindowsUpdateDownloadProgress value) => Values.Add(value);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class StaticHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = ResponseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHttpHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ThrowingHttpHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }
}

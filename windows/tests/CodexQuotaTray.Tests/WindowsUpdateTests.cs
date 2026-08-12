using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.Updates;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class WindowsUpdateTests
{
    [TestMethod]
    public void ReleaseParser_SelectsHighestWindowsReleaseRegardlessOfOrderAndIgnoresAndroid()
    {
        const string json = """
            [
              {"tag_name":"android-v9.9.9","draft":false,"prerelease":false,"assets":[]},
              {"tag_name":"windows-v0.7.0","name":"0.7.0","body":"new","published_at":"2026-08-01T00:00:00Z","assets":[
                {"name":"CodexQuotaTray-0.7.0-setup.exe","browser_download_url":"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.7.0/CodexQuotaTray-0.7.0-setup.exe","size":10},
                {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.7.0/SHA256SUMS.txt","size":100}]},
              {"tag_name":"windows-v0.6.6","draft":false,"prerelease":false,"assets":[
                {"name":"CodexQuotaTray-0.6.6-setup.exe","browser_download_url":"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.6.6/CodexQuotaTray-0.6.6-setup.exe"},
                {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.6.6/SHA256SUMS.txt"}]}
            ]
            """;

        using var document = JsonDocument.Parse(json);
        var release = GitHubWindowsReleaseProvider.ParseLatestWindowsRelease(document.RootElement);

        Assert.IsNotNull(release);
        Assert.AreEqual("windows-v0.7.0", release.TagName);
        Assert.AreEqual("CodexQuotaTray-0.7.0-setup.exe", release.Installer.Name);
    }

    [TestMethod]
    public void ReleaseParser_RequiresStableTagAndBothExactAssets()
    {
        const string json = """
            [
              {"tag_name":"windows-v0.8.0","draft":true,"prerelease":false,"assets":[]},
              {"tag_name":"windows-v0.8.1-rc.1","draft":false,"prerelease":false,"assets":[]},
              {"tag_name":"windows-v0.8.0","draft":false,"prerelease":true,"assets":[]},
              {"tag_name":"windows-v0.9.0","draft":false,"prerelease":false,"assets":[{"name":"other.exe","browser_download_url":"https://github.com/DDJang/CodexQuotaTray/other.exe"}]}
            ]
            """;

        using var document = JsonDocument.Parse(json);
        Assert.IsNull(GitHubWindowsReleaseProvider.ParseLatestWindowsRelease(document.RootElement));
    }

    [TestMethod]
    public async Task ReleaseProvider_FindsWindowsReleaseOnSecondPageAfterAndroidPage()
    {
        var requests = new List<Uri>();
        var handler = new StaticHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                    ? ReleasePage(1, _ => WindowsReleaseJson("windows-v0.9.0", "0.9.0"))
                    : ReleasePage(100, _ => AndroidReleaseJson("android-v0.1.0"))),
            };
        });
        using var client = new HttpClient(handler);
        using var provider = new GitHubWindowsReleaseProvider(client, "https://example.test/releases?per_page=100");

        var release = await provider.GetLatestAsync(CancellationToken.None);

        Assert.IsNotNull(release);
        Assert.AreEqual("windows-v0.9.0", release.TagName);
        Assert.AreEqual(2, requests.Count);
        Assert.IsTrue(requests[1].Query.Contains("page=2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReleaseProvider_ChoosesHigherVersionFromLaterPage()
    {
        var requests = new List<Uri>();
        var handler = new StaticHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var body = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                ? ReleasePage(1, _ => WindowsReleaseJson("windows-v0.9.0", "0.9.0"))
                : ReleasePage(100, _ => WindowsReleaseJson("windows-v0.7.0", "0.7.0"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var client = new HttpClient(handler);
        using var provider = new GitHubWindowsReleaseProvider(client, "https://example.test/releases?per_page=100");

        var release = await provider.GetLatestAsync(CancellationToken.None);

        Assert.IsNotNull(release);
        Assert.AreEqual("windows-v0.9.0", release.TagName);
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task ReleaseProvider_StopsWhenSecondPageIsEmpty()
    {
        var requests = new List<Uri>();
        var handler = new StaticHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var body = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                ? "[]"
                : ReleasePage(100, _ => AndroidReleaseJson("android-v0.1.0"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var client = new HttpClient(handler);
        using var provider = new GitHubWindowsReleaseProvider(client, "https://example.test/releases?per_page=100");

        Assert.IsNull(await provider.GetLatestAsync(CancellationToken.None));
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task ReleaseProvider_StopsAfterShortPage()
    {
        var requests = new List<Uri>();
        var handler = new StaticHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleasePage(1, _ => AndroidReleaseJson("android-v0.1.0"))),
            };
        });
        using var client = new HttpClient(handler);
        using var provider = new GitHubWindowsReleaseProvider(client, "https://example.test/releases?per_page=100");

        Assert.IsNull(await provider.GetLatestAsync(CancellationToken.None));
        Assert.AreEqual(1, requests.Count);
    }

    [TestMethod]
    public async Task ReleaseProvider_StopsAtBoundedMaximumPageCount()
    {
        var requests = new List<Uri>();
        var handler = new StaticHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleasePage(100, _ => AndroidReleaseJson("android-v0.1.0"))),
            };
        });
        using var client = new HttpClient(handler);
        using var provider = new GitHubWindowsReleaseProvider(client, "https://example.test/releases?per_page=100");

        Assert.IsNull(await provider.GetLatestAsync(CancellationToken.None));
        Assert.AreEqual(3, requests.Count);
        Assert.IsFalse(requests.Any(request => request.Query.Contains("page=4", StringComparison.Ordinal)));
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
        Assert.IsFalse(SemanticVersion.TryParse(value.StartsWith("windows-v", StringComparison.Ordinal) ? value[9..] : value, out _)
            && GitHubWindowsReleaseProvider.TryParseWindowsTag(value, out _));
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
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(handler);
            using var downloader = new WindowsUpdateDownloader(root, client);
            handler.ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? new StringContent($"{hash}  CodexQuotaTray-0.7.0-setup.exe\n")
                    : new ByteArrayContent(bytes),
            };

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
            handler.ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? new StringContent($"{new string('0', 64)}  CodexQuotaTray-0.7.0-setup.exe\n")
                    : new ByteArrayContent(bytes),
            };

            var badHash = await downloader.DownloadAsync(CreateRelease("0.7.0"), CancellationToken.None);
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
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new StaticHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? new StringContent($"{hash}  CodexQuotaTray-0.7.0-setup.exe\n")
                : new ByteArrayContent(bytes),
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
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new StaticHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? new StringContent($"{hash}  CodexQuotaTray-0.7.0-setup.exe\n")
                : new StreamContent(new NonSeekableStream(bytes)),
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

    private static string ReleasePage(int count, Func<int, string> releaseFactory) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(releaseFactory)) + "]";

    private static string AndroidReleaseJson(string tag) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft = false,
        prerelease = false,
        assets = Array.Empty<object>(),
    });

    private static string WindowsReleaseJson(string tag, string version) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        name = $"Release {version}",
        body = "notes",
        published_at = "2026-08-11T00:00:00Z",
        draft = false,
        prerelease = false,
        assets = new object[]
        {
            new
            {
                name = $"CodexQuotaTray-{version}-setup.exe",
                browser_download_url = $"https://github.com/DDJang/CodexQuotaTray/releases/download/{tag}/CodexQuotaTray-{version}-setup.exe",
            },
            new
            {
                name = "SHA256SUMS.txt",
                browser_download_url = $"https://github.com/DDJang/CodexQuotaTray/releases/download/{tag}/SHA256SUMS.txt",
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
            new WindowsUpdateAsset(
                "SHA256SUMS.txt",
                new Uri($"https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v{version}/SHA256SUMS.txt")));
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
}

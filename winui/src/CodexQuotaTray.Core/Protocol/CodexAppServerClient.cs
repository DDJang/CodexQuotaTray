using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaTray.Core.Protocol;

public sealed record CodexClientOptions(
    string? ExplicitCodexBinary = null,
    IReadOnlyList<string>? ServerArguments = null,
    TimeSpan? VersionTimeout = null,
    TimeSpan? InitializeTimeout = null,
    TimeSpan? RequestTimeout = null,
    TimeSpan? ShutdownTimeout = null)
{
    public IReadOnlyList<string> EffectiveServerArguments => ServerArguments ?? ["app-server", "--stdio"];
    public TimeSpan EffectiveVersionTimeout => VersionTimeout ?? TimeSpan.FromSeconds(3);
    public TimeSpan EffectiveInitializeTimeout => InitializeTimeout ?? TimeSpan.FromSeconds(10);
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(10);
    public TimeSpan EffectiveShutdownTimeout => ShutdownTimeout ?? TimeSpan.FromSeconds(3);
}

public sealed class CodexAppServerClientFactory(CodexClientOptions options) : ICodexAppServerClientFactory
{
    public ICodexAppServerClient Create() => new CodexAppServerClient(options);
}

public sealed class CodexAppServerClient(CodexClientOptions options) : ICodexAppServerClient
{
    private readonly object diagnosticGate = new();
    private CodexDiagnosticSnapshot diagnostics = new();
    private CodexAppServerProcess? process;
    private JsonLineRpcConnection? connection;
    private bool disposed;

    public CodexDiagnosticSnapshot Diagnostics
    {
        get
        {
            lock (diagnosticGate)
            {
                return diagnostics with
                {
                    MalformedJsonCount = connection?.MalformedJsonCount ?? diagnostics.MalformedJsonCount,
                    StderrObserved = process?.StderrObserved ?? diagnostics.StderrObserved,
                };
            }
        }
    }

    public async Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (connection is not null)
        {
            return new CodexSessionInfo(Diagnostics.CliVersion, null);
        }

        LocatedCodex located;
        using (var locateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            locateTimeout.CancelAfter(options.EffectiveVersionTimeout * 3 + TimeSpan.FromSeconds(1));
            try
            {
                located = await CodexCliLocator.LocateAsync(options, locateTimeout.Token)
                    .WaitAsync(locateTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CodexClientException(
                    CodexClientErrorKind.CliVersionProbeFailed,
                    "Codex CLI version probing exceeded its bounded deadline.",
                    error);
            }
        }
        Update(value => value with { CliFound = true, CliVersion = located.Version, LastError = null });
        try
        {
            process = await CodexAppServerProcess.StartAsync(
                located.Path,
                options.EffectiveServerArguments,
                options.EffectiveShutdownTimeout,
                cancellationToken).ConfigureAwait(false);
            Update(value => value with { AppServerStarted = true });
            connection = new JsonLineRpcConnection(process.StandardOutput, process.StandardInput);
            JsonElement result;
            try
            {
                result = await connection.RequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new
                        {
                            name = "codex_quota_tray_winui",
                            title = "CodexQuotaTray WinUI",
                            version = "0.3.4",
                        },
                    },
                    options.EffectiveInitializeTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CodexClientException error) when (error.Kind == CodexClientErrorKind.RequestTimeout)
            {
                throw new CodexClientException(CodexClientErrorKind.InitializeTimeout, "App Server initialization timed out.", error);
            }
            catch (CodexClientException error) when (error.Kind is CodexClientErrorKind.RemoteError or CodexClientErrorKind.MethodNotFound)
            {
                throw new CodexClientException(CodexClientErrorKind.InitializeRejected, "App Server rejected initialization.", error);
            }

            var initialized = result.Deserialize<InitializeResponse>()
                ?? throw new CodexClientException(CodexClientErrorKind.Protocol, "Initialize result was empty.");
            if (string.IsNullOrWhiteSpace(initialized.PlatformFamily) || string.IsNullOrWhiteSpace(initialized.PlatformOs))
            {
                throw new CodexClientException(CodexClientErrorKind.Protocol, "Initialize result omitted required platform fields.");
            }

            await connection.NotifyAsync("initialized", cancellationToken).ConfigureAwait(false);
            var runtimeVersion = ExtractVersion(initialized.UserAgent);
            Update(value => value with { InitializeSucceeded = true, LastError = null });
            return new CodexSessionInfo(located.Version, runtimeVersion);
        }
        catch (CodexClientException error)
        {
            Update(value => value with { LastError = error.Kind });
            await ResetSessionSafelyAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            var wrapped = new CodexClientException(CodexClientErrorKind.ProcessStartFailed, "Could not start Codex App Server.", error);
            Update(value => value with { LastError = wrapped.Kind });
            await ResetSessionSafelyAsync().ConfigureAwait(false);
            throw wrapped;
        }
    }

    public async Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var rpc = connection ?? throw new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server is not connected.");
        try
        {
            var result = await rpc.RequestAsync(
                "account/rateLimits/read",
                null,
                options.EffectiveRequestTimeout,
                cancellationToken).ConfigureAwait(false);
            var fieldPresent = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimitResetCredits", out _);
            var response = result.Deserialize<RateLimitsResponse>()
                ?? throw new CodexClientException(CodexClientErrorKind.Protocol, "Rate-limit result was empty.");
            var detailCount = response.RateLimitResetCredits?.Credits?.Count;
            Update(value => value with
            {
                RateLimitsReadSucceeded = true,
                ResetCreditsFieldPresent = fieldPresent,
                AvailableCount = response.RateLimitResetCredits?.AvailableCount,
                CreditDetailCount = detailCount,
                LastSuccessUtc = DateTimeOffset.UtcNow,
                LastError = null,
            });
            return new RateLimitsReadResult(response, fieldPresent);
        }
        catch (CodexClientException error)
        {
            Update(value => value with { RateLimitsReadSucceeded = false, LastError = error.Kind });
            throw;
        }
        catch (JsonException error)
        {
            var wrapped = new CodexClientException(CodexClientErrorKind.Protocol, "Rate-limit result could not be parsed.", error);
            Update(value => value with { RateLimitsReadSucceeded = false, LastError = wrapped.Kind });
            throw wrapped;
        }
    }

    public void RecordWindowCount(int count) => Update(value => value with { WindowCount = count });

    public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rpc = connection ?? throw new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server is not connected.");
        await foreach (var notification in rpc.ReadNotificationsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(notification.Method, "account/rateLimits/updated", StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = TryParseNotification(notification.Parameters);
            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private static RateLimitsUpdatedNotification? TryParseNotification(JsonElement parameters)
    {
        try
        {
            var fieldPresent = parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("rateLimitResetCredits", out _);
            var response = parameters.Deserialize<RateLimitsResponse>();
            return response is null ? null : new RateLimitsUpdatedNotification(response, fieldPresent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Update(Func<CodexDiagnosticSnapshot, CodexDiagnosticSnapshot> update)
    {
        lock (diagnosticGate)
        {
            diagnostics = update(diagnostics);
        }
    }

    private async Task ResetSessionAsync()
    {
        // Closing stdin and reaping the child makes stdout reach a deterministic EOF.
        // StreamReader cancellation alone is not guaranteed to interrupt a Windows pipe read.
        if (process is not null)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            process = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            connection = null;
        }
    }

    private async Task ResetSessionSafelyAsync()
    {
        try
        {
            await ResetSessionAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            Update(value => value with { LastError = CodexClientErrorKind.ShutdownFailed });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await ResetSessionSafelyAsync().ConfigureAwait(false);
    }

    private static string? ExtractVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Regex.Match(text, @"(?<!\d)\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?", RegexOptions.CultureInvariant) is { Success: true } match
            ? match.Value
            : null;
    }
}

internal sealed record LocatedCodex(string Path, string? Version);

internal static class CodexCliLocator
{
    internal static async Task<LocatedCodex> LocateAsync(CodexClientOptions options, CancellationToken cancellationToken)
    {
        var candidates = options.ExplicitCodexBinary is { Length: > 0 } explicitPath
            ? new[] { explicitPath }
            : DefaultCandidates();
        var observed = false;
        foreach (var candidate in candidates)
        {
            Process? process = null;
            var started = false;
            try
            {
                var info = CodexAppServerProcess.CreateStartInfo(candidate, ["--version"]);
                process = new Process { StartInfo = info };
                if (!process.Start())
                {
                    continue;
                }

                started = true;
                observed = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.EffectiveVersionTimeout);
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                // A launcher can exit while a descendant still owns the redirected handles.
                // Bound the waits independently instead of trusting pipe cancellation support.
                var output = await outputTask.WaitAsync(timeout.Token).ConfigureAwait(false);
                _ = await errorTask.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode == 0)
                {
                    var version = Regex.Match(output, @"\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?").Value;
                    return new LocatedCodex(candidate, string.IsNullOrEmpty(version) ? null : version);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                observed = true;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
            }
            finally
            {
                if (process is not null)
                {
                    try
                    {
                        if (started && !process.HasExited)
                        {
                            // Version probes should not enumerate an inaccessible packaged process tree.
                            // The actual App Server uses a Job Object; this short-lived probe does not.
                            process.Kill(entireProcessTree: false);
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                    {
                    }

                    process.Dispose();
                }
            }
        }

        throw new CodexClientException(
            observed ? CodexClientErrorKind.CliVersionProbeFailed : CodexClientErrorKind.CliNotFound,
            observed ? "Codex CLI version probe failed." : "Codex CLI was not found.");
    }

    internal static string[] DefaultCandidates()
    {
        var npmShim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");
        return new[] { npmShim, "codex.cmd", "codex.exe", "codex" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class CodexAppServerProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly TimeSpan shutdownTimeout;
    private readonly Task stderrTask;
    private readonly WindowsProcessJob? job;
    private long stderrBytes;
    private bool disposed;

    private CodexAppServerProcess(Process process, TimeSpan shutdownTimeout, WindowsProcessJob? job)
    {
        this.process = process;
        this.shutdownTimeout = shutdownTimeout;
        this.job = job;
        StandardInput = process.StandardInput;
        StandardOutput = process.StandardOutput;
        stderrTask = DrainStderrAsync(process.StandardError);
    }

    internal TextWriter StandardInput { get; }

    internal TextReader StandardOutput { get; }

    internal bool StderrObserved => Interlocked.Read(ref stderrBytes) > 0;

    internal static Task<CodexAppServerProcess> StartAsync(
        string binary,
        IReadOnlyList<string> arguments,
        TimeSpan shutdownTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = CreateStartInfo(binary, arguments), EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new CodexClientException(CodexClientErrorKind.ProcessStartFailed, "Could not start Codex App Server.");
        }

        WindowsProcessJob? job = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                job = WindowsProcessJob.CreateAndAssign(process);
            }

            return Task.FromResult(new CodexAppServerProcess(process, shutdownTimeout, job));
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            job?.Dispose();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string binary, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo info;
        if (binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            // cmd.exe applies a second quoting pass after ProcessStartInfo. Supplying the
            // complete /c payload through ArgumentList escapes its outer quotes and makes
            // paths with spaces fail. The documented /s /c form requires one quoted payload.
            info.Arguments = $"/d /s /c \"{BuildCommandLine(binary, arguments)}\"";
        }
        else
        {
            info = new ProcessStartInfo(binary);
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        return info;
    }

    private static string BuildCommandLine(string binary, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Quote(binary) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private async Task DrainStderrAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
            {
                Interlocked.Add(ref stderrBytes, count);
            }
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StandardInput.Close();
        using var timeout = new CancellationTokenSource(shutdownTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            job?.Dispose();
            await stderrTask.ConfigureAwait(false);
            process.Dispose();
        }
    }
}

internal sealed class WindowsProcessJob : IDisposable
{
    private IntPtr handle;

    private WindowsProcessJob(IntPtr handle) => this.handle = handle;

    internal static WindowsProcessJob CreateAndAssign(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the App Server process job.");
        }

        var job = new WindowsProcessJob(handle);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 },
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)length) || !AssignProcessToJobObject(handle, process.Handle))
            {
                throw new InvalidOperationException("Could not contain the App Server process tree.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return job;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

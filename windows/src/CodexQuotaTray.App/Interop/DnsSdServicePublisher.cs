using System.ComponentModel;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Interop;

internal sealed class DnsSdServicePublisher : IAsyncDisposable
{
    internal const uint DnsRequestPending = 9506;
    private const uint DnsQueryRequestVersion1 = 1;
    private static readonly DnsServiceRegisterComplete RegisterCompleteCallback = OnNativeCompletion;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, CallbackRegistration> ActivePublishers = new();
    private static long nextQueryContext;
    private readonly Guid deviceId;
    private readonly string displayName;
    private readonly string serviceInstancePrefix;
    private readonly IDnsSdNative native;
    private readonly TimeSpan callbackTimeout;
    private readonly Action<string> diagnostic;
    private readonly List<IntPtr> allocations = [];
    private readonly object callbackStateLock = new();
    private TaskCompletionSource<uint>? registrationCompletion;
    private TaskCompletionSource<uint>? deregistrationCompletion;
    private DnsSdRegisterRequest request;
    private DnsSdRegisterRequest deregistrationRequest;
    private DnsSdCancel registrationCancel;
    private IntPtr serviceInstance;
    private IntPtr queryContext;
    private IntPtr deregistrationQueryContext;
    private RegistrationPhase phase;
    private Stopwatch? registrationStopwatch;

    internal DnsSdServicePublisher(
        Guid deviceId,
        string displayName,
        string serviceInstancePrefix = "CodexQuotaTray",
        IDnsSdNative? native = null,
        TimeSpan? callbackTimeout = null,
        Action<string>? diagnostic = null)
    {
        this.deviceId = deviceId;
        this.displayName = displayName;
        this.serviceInstancePrefix = serviceInstancePrefix;
        this.native = native ?? WindowsDnsSdNative.Instance;
        this.callbackTimeout = callbackTimeout ?? TimeSpan.FromSeconds(2);
        this.diagnostic = diagnostic ?? (message => System.Diagnostics.Debug.WriteLine(message));
    }

    internal bool IsStarted => phase == RegistrationPhase.Registered && serviceInstance != IntPtr.Zero;
    internal static int ActivePublisherCount => ActivePublishers.Values.Select(value => value.Publisher).Distinct().Count();

    internal async Task StartAsync(IPAddress address, int port, uint interfaceIndex = 0, CancellationToken cancellationToken = default)
    {
        if (phase != RegistrationPhase.Stopped || serviceInstance != IntPtr.Zero)
        {
            throw new InvalidOperationException("DNS-SD service is already registering or registered.");
        }
        if (!TokenUsageSyncServer.IsPrivateLanAddress(address))
        {
            throw new ArgumentException("DNS-SD requires a private IPv4 address.", nameof(address));
        }

        try
        {
            var ip4 = Allocate(sizeof(uint));
            Marshal.WriteInt32(ip4, unchecked((int)ToHostOrder(address)));
            var metadata = new TokenUsageDiscoveryMetadata(deviceId, displayName, port);
            var keyValues = metadata.TextAttributes;
            var keys = AllocateStringArray(keyValues.Keys);
            var values = AllocateStringArray(keyValues.Values);
            serviceInstance = native.ConstructInstance(
                $"{SanitizeLabel(serviceInstancePrefix)}-{deviceId:N}._codexquota._tcp.local",
                $"{SanitizeLabel(Environment.MachineName)}.local",
                ip4,
                checked((ushort)port),
                checked((uint)keyValues.Count),
                keys,
                values);
            if (serviceInstance == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DNS-SD instance creation failed.");
            }

            queryContext = new IntPtr(Interlocked.Increment(ref nextQueryContext));
            if (!ActivePublishers.TryAdd(queryContext, new CallbackRegistration(this, CallbackKind.Registration)))
            {
                throw new InvalidOperationException("Could not allocate a DNS-SD callback context.");
            }
            var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            request = new DnsSdRegisterRequest
            {
                Version = DnsQueryRequestVersion1,
                InterfaceIndex = interfaceIndex,
                ServiceInstance = serviceInstance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(RegisterCompleteCallback),
                QueryContext = queryContext,
                Credentials = IntPtr.Zero,
                UnicastEnabled = false,
            };
            lock (callbackStateLock)
            {
                phase = RegistrationPhase.Registering;
                registrationCompletion = completion;
            }
            registrationCancel = default;
            registrationStopwatch = Stopwatch.StartNew();
            var result = native.Register(ref request, ref registrationCancel);
            diagnostic($"DNS-SD register immediate status={result} pending={result == DnsRequestPending} interface={interfaceIndex}");
            var status = result == DnsRequestPending
                ? await WaitForRegistrationAsync(completion, cancellationToken).ConfigureAwait(false)
                : result;
            if (status != 0)
            {
                diagnostic($"DNS-SD registration failure status={status}");
                throw new Win32Exception((int)status, "Windows DNS-SD registration failed.");
            }
            lock (callbackStateLock)
            {
                phase = RegistrationPhase.Registered;
            }
            diagnostic("DNS-SD registration success");
        }
        catch
        {
            lock (callbackStateLock)
            {
                phase = RegistrationPhase.Stopped;
            }
            ReleaseCallbackContext();
            FreeNative();
            throw;
        }
        finally
        {
            lock (callbackStateLock)
            {
                registrationCompletion = null;
            }
            registrationStopwatch = null;
        }
    }

    internal Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken) =>
        StartAsync(address, port, 0, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (serviceInstance == IntPtr.Zero)
        {
            return;
        }
        try
        {
            if (phase == RegistrationPhase.Registered)
            {
                var status = await DeregisterAsync().ConfigureAwait(false);
                diagnostic($"DNS-SD deregistration status={status}");
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (Win32Exception) { }
        catch (TimeoutException) { }
        finally
        {
            lock (callbackStateLock)
            {
                phase = RegistrationPhase.Stopped;
                deregistrationCompletion = null;
            }
            ReleaseCallbackContext();
            FreeNative();
        }
    }

    private static void OnNativeCompletion(uint status, IntPtr queryContext, IntPtr instance)
    {
        if (ActivePublishers.TryGetValue(queryContext, out var callbackRegistration))
        {
            callbackRegistration.Publisher.HandleNativeCompletion(status, instance, callbackRegistration.Kind);
        }
        else if (instance != IntPtr.Zero)
        {
            try { WindowsDnsSdNative.Instance.FreeInstance(instance); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine($"DNS-SD orphan callback instance cleanup failed: {error.GetType().Name}");
            }
        }
    }

    private void HandleNativeCompletion(uint status, IntPtr instance, CallbackKind callbackKind)
    {
        try
        {
            if (instance != IntPtr.Zero)
            {
                native.FreeInstance(instance);
            }
        }
        catch (Exception error)
        {
            diagnostic($"DNS-SD callback instance cleanup failed: {error.GetType().Name}");
        }
        finally
        {
            var elapsed = registrationStopwatch?.ElapsedMilliseconds;
            lock (callbackStateLock)
            {
                diagnostic($"DNS-SD callback phase={phase} status={status} elapsedMs={elapsed?.ToString() ?? "n/a"}");
                var completion = callbackKind == CallbackKind.Registration
                    ? registrationCompletion
                    : deregistrationCompletion;
                completion?.TrySetResult(status);
            }
        }
    }

    private async Task<uint> WaitForRegistrationAsync(
        TaskCompletionSource<uint> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await completion.Task.WaitAsync(callbackTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            var shouldCancel = false;
            lock (callbackStateLock)
            {
                if (!completion.Task.IsCompleted && phase == RegistrationPhase.Registering)
                {
                    phase = RegistrationPhase.CancellingRegistration;
                    shouldCancel = true;
                }
            }

            if (!shouldCancel)
            {
                diagnostic($"DNS-SD timeout-success race winner=callback elapsedMs={registrationStopwatch?.ElapsedMilliseconds ?? 0}");
                return await completion.Task.ConfigureAwait(false);
            }

            diagnostic($"DNS-SD registration {(error is TimeoutException ? "callback timeout" : "cancelled")} elapsedMs={registrationStopwatch?.ElapsedMilliseconds ?? 0}");
            var cancelStatus = native.CancelRegistration(ref registrationCancel);
            diagnostic($"DNS-SD registration cancel status={cancelStatus}");
            await DeregisterAfterCancelledStartAsync(completion).ConfigureAwait(false);

            ExceptionDispatchInfo.Capture(error).Throw();
            throw new InvalidOperationException("Unreachable registration cancellation path.");
        }
    }

    private async Task DeregisterAfterCancelledStartAsync(TaskCompletionSource<uint> registration)
    {
        try
        {
            var status = await DeregisterAsync().ConfigureAwait(false);
            if (registration.Task is { IsCompletedSuccessfully: true } && registration.Task.Result == 0)
            {
                diagnostic("DNS-SD cancel race returned success; compensating deregistration");
            }
            else
            {
                diagnostic($"DNS-SD cancellation deregistration status={status}");
            }
        }
        finally
        {
            lock (callbackStateLock)
            {
                deregistrationCompletion = null;
            }
        }
    }

    private async Task<uint> DeregisterAsync()
    {
        var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new IntPtr(Interlocked.Increment(ref nextQueryContext));
        lock (callbackStateLock)
        {
            phase = RegistrationPhase.Deregistering;
            deregistrationCompletion = completion;
            deregistrationQueryContext = context;
            deregistrationRequest = request;
            deregistrationRequest.RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(RegisterCompleteCallback);
            deregistrationRequest.QueryContext = context;
        }
        if (!ActivePublishers.TryAdd(context, new CallbackRegistration(this, CallbackKind.Deregistration)))
        {
            throw new InvalidOperationException("Could not allocate a DNS-SD deregistration callback context.");
        }

        var result = native.Deregister(ref deregistrationRequest);
        return result == DnsRequestPending
            ? await completion.Task.ConfigureAwait(false)
            : result;
    }

    private void ReleaseCallbackContext()
    {
        IntPtr registrationContext;
        IntPtr deregistrationContext;
        lock (callbackStateLock)
        {
            registrationContext = queryContext;
            queryContext = IntPtr.Zero;
            deregistrationContext = deregistrationQueryContext;
            deregistrationQueryContext = IntPtr.Zero;
        }
        if (registrationContext != IntPtr.Zero)
        {
            _ = ActivePublishers.TryRemove(registrationContext, out _);
        }
        if (deregistrationContext != IntPtr.Zero)
        {
            _ = ActivePublishers.TryRemove(deregistrationContext, out _);
        }
    }

    private IntPtr Allocate(int bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes);
        allocations.Add(pointer);
        return pointer;
    }

    private IntPtr AllocateStringArray(IEnumerable<string> values)
    {
        var strings = values.Select(Marshal.StringToHGlobalUni).ToArray();
        allocations.AddRange(strings);
        var array = Allocate(IntPtr.Size * strings.Length);
        for (var index = 0; index < strings.Length; index++)
        {
            Marshal.WriteIntPtr(array, index * IntPtr.Size, strings[index]);
        }
        return array;
    }

    private void FreeNative()
    {
        if (serviceInstance != IntPtr.Zero)
        {
            try { native.FreeInstance(serviceInstance); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            serviceInstance = IntPtr.Zero;
        }
        foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation);
        allocations.Clear();
    }

    private static uint ToHostOrder(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string SanitizeLabel(string value)
    {
        var filtered = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "CodexQuotaTray" : filtered[..Math.Min(63, filtered.Length)];
    }

    private enum RegistrationPhase { Stopped, Registering, CancellingRegistration, Registered, Deregistering }
    private enum CallbackKind { Registration, Deregistration }
    private readonly record struct CallbackRegistration(DnsSdServicePublisher Publisher, CallbackKind Kind);
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void DnsServiceRegisterComplete(uint status, IntPtr queryContext, IntPtr instance);

[StructLayout(LayoutKind.Sequential)]
internal struct DnsSdRegisterRequest
{
    public uint Version;
    public uint InterfaceIndex;
    public IntPtr ServiceInstance;
    public IntPtr RegisterCompletionCallback;
    public IntPtr QueryContext;
    public IntPtr Credentials;
    [MarshalAs(UnmanagedType.Bool)] public bool UnicastEnabled;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DnsSdCancel
{
    public IntPtr Reserved;
}

internal interface IDnsSdNative
{
    IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values);
    uint Register(ref DnsSdRegisterRequest request, ref DnsSdCancel cancel);
    uint CancelRegistration(ref DnsSdCancel cancel);
    uint Deregister(ref DnsSdRegisterRequest request);
    void FreeInstance(IntPtr instance);
}

internal sealed class WindowsDnsSdNative : IDnsSdNative
{
    internal static readonly WindowsDnsSdNative Instance = new();
    public IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values) =>
        DnsServiceConstructInstance(serviceName, hostName, ip4Address, IntPtr.Zero, port, 0, 0, propertyCount, keys, values);
    public uint Register(ref DnsSdRegisterRequest request, ref DnsSdCancel cancel) => DnsServiceRegister(ref request, ref cancel);
    public uint CancelRegistration(ref DnsSdCancel cancel) => DnsServiceRegisterCancel(ref cancel);
    public uint Deregister(ref DnsSdRegisterRequest request) => DnsServiceDeRegister(ref request, IntPtr.Zero);
    public void FreeInstance(IntPtr instance) => DnsServiceFreeInstance(instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4Address, IntPtr ip6Address, ushort port, ushort priority, ushort weight, uint propertyCount, IntPtr keys, IntPtr values);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern uint DnsServiceRegister(ref DnsSdRegisterRequest request, ref DnsSdCancel cancel);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern uint DnsServiceRegisterCancel(ref DnsSdCancel cancel);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern uint DnsServiceDeRegister(ref DnsSdRegisterRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern void DnsServiceFreeInstance(IntPtr instance);
}

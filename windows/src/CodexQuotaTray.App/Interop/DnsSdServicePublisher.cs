using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Interop;

internal sealed class DnsSdServicePublisher : IAsyncDisposable
{
    internal const uint DnsRequestPending = 9500;
    private const uint DnsQueryRequestVersion1 = 1;
    private static readonly DnsServiceRegisterComplete RegisterCompleteCallback = OnNativeCompletion;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DnsSdServicePublisher> ActivePublishers = new();
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
    private IntPtr serviceInstance;
    private IntPtr queryContext;
    private RegistrationPhase phase;

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

    internal async Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
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
            if (!ActivePublishers.TryAdd(queryContext, this))
            {
                throw new InvalidOperationException("Could not allocate a DNS-SD callback context.");
            }
            registrationCompletion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            request = new DnsSdRegisterRequest
            {
                Version = DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = serviceInstance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(RegisterCompleteCallback),
                QueryContext = queryContext,
                Credentials = IntPtr.Zero,
                UnicastEnabled = false,
            };
            phase = RegistrationPhase.Registering;
            var result = native.Register(ref request);
            var status = result == DnsRequestPending
                ? await registrationCompletion.Task.WaitAsync(callbackTimeout, cancellationToken).ConfigureAwait(false)
                : result;
            if (status != 0)
            {
                diagnostic($"DNS-SD registration failure status={status}");
                throw new Win32Exception((int)status, "Windows DNS-SD registration failed.");
            }
            phase = RegistrationPhase.Registered;
            diagnostic("DNS-SD registration success");
        }
        catch
        {
            phase = RegistrationPhase.Stopped;
            ReleaseCallbackContext();
            FreeNative();
            throw;
        }
        finally
        {
            registrationCompletion = null;
        }
    }

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
                var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (callbackStateLock)
                {
                    phase = RegistrationPhase.Deregistering;
                    deregistrationCompletion = completion;
                }
                var result = native.Deregister(ref request);
                var status = result == DnsRequestPending
                    ? await completion.Task.WaitAsync(callbackTimeout).ConfigureAwait(false)
                    : result;
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
        if (ActivePublishers.TryGetValue(queryContext, out var publisher))
        {
            publisher.CompleteCurrentOperation(status);
        }
    }

    private void CompleteCurrentOperation(uint status)
    {
        TaskCompletionSource<uint>? completion;
        lock (callbackStateLock)
        {
            completion = phase switch
            {
                RegistrationPhase.Registering => registrationCompletion,
                RegistrationPhase.Deregistering => deregistrationCompletion,
                _ => null,
            };
        }
        completion?.TrySetResult(status);
    }

    private void ReleaseCallbackContext()
    {
        var context = Interlocked.Exchange(ref queryContext, IntPtr.Zero);
        if (context != IntPtr.Zero)
        {
            _ = ActivePublishers.TryRemove(context, out _);
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

    private enum RegistrationPhase { Stopped, Registering, Registered, Deregistering }
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

internal interface IDnsSdNative
{
    IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values);
    uint Register(ref DnsSdRegisterRequest request);
    uint Deregister(ref DnsSdRegisterRequest request);
    void FreeInstance(IntPtr instance);
}

internal sealed class WindowsDnsSdNative : IDnsSdNative
{
    internal static readonly WindowsDnsSdNative Instance = new();
    public IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values) =>
        DnsServiceConstructInstance(serviceName, hostName, ip4Address, IntPtr.Zero, port, 0, 0, propertyCount, keys, values);
    public uint Register(ref DnsSdRegisterRequest request) => DnsServiceRegister(ref request, IntPtr.Zero);
    public uint Deregister(ref DnsSdRegisterRequest request) => DnsServiceDeRegister(ref request, IntPtr.Zero);
    public void FreeInstance(IntPtr instance) => DnsServiceFreeInstance(instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4Address, IntPtr ip6Address, ushort port, ushort priority, ushort weight, uint propertyCount, IntPtr keys, IntPtr values);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern uint DnsServiceRegister(ref DnsSdRegisterRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern uint DnsServiceDeRegister(ref DnsSdRegisterRequest request, IntPtr cancel);
    [DllImport("dnsapi.dll", SetLastError = true)] private static extern void DnsServiceFreeInstance(IntPtr instance);
}

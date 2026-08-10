using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Interop;

internal sealed class DnsSdServicePublisher : IAsyncDisposable
{
    private const uint DnsQueryRequestVersion1 = 1;
    private const uint DnsRequestPending = 9500;
    private readonly Guid deviceId;
    private readonly string displayName;
    private readonly string serviceInstancePrefix;
    private readonly List<IntPtr> allocations = [];
    private TaskCompletionSource<bool>? deregistrationCompletion;
    private DnsServiceRegisterComplete? callback;
    private RegisterRequest request;
    private IntPtr serviceInstance;
    private bool registered;

    internal DnsSdServicePublisher(
        Guid deviceId,
        string displayName,
        string serviceInstancePrefix = "CodexQuotaTray")
    {
        this.deviceId = deviceId;
        this.displayName = displayName;
        this.serviceInstancePrefix = serviceInstancePrefix;
    }

    internal bool IsStarted => serviceInstance != IntPtr.Zero;

    internal void Start(IPAddress address, int port)
    {
        if (IsStarted)
        {
            throw new InvalidOperationException("DNS-SD service is already registered.");
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
            serviceInstance = DnsServiceConstructInstance(
                $"{SanitizeLabel(serviceInstancePrefix)}-{deviceId:N}._codexquota._tcp.local",
                $"{SanitizeLabel(Environment.MachineName)}.local",
                ip4,
                IntPtr.Zero,
                checked((ushort)port),
                0,
                0,
                checked((uint)keyValues.Count),
                keys,
                values);
            if (serviceInstance == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DNS-SD instance creation failed.");
            }

            callback = OnRegistrationComplete;
            request = new RegisterRequest
            {
                Version = DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = serviceInstance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(callback),
                QueryContext = IntPtr.Zero,
                Credentials = IntPtr.Zero,
                UnicastEnabled = false,
            };
            var result = DnsServiceRegister(ref request, IntPtr.Zero);
            if (result != 0 && result != DnsRequestPending)
            {
                throw new Win32Exception((int)result, "Windows DNS-SD registration failed.");
            }

            registered = true;
        }
        catch
        {
            FreeNative();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsStarted)
        {
            return;
        }

        if (registered)
        {
            registered = false;
            deregistrationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var result = DnsServiceDeRegister(ref request, IntPtr.Zero);
                if (result == 0 || result == DnsRequestPending)
                {
                    await deregistrationCompletion.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (Win32Exception)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        FreeNative();
        deregistrationCompletion = null;
    }

    private void OnRegistrationComplete(uint status, IntPtr queryContext, IntPtr instance)
    {
        deregistrationCompletion?.TrySetResult(status == 0);
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
        foreach (var value in strings)
        {
            allocations.Add(value);
        }

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
            try
            {
                DnsServiceFreeInstance(serviceInstance);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            serviceInstance = IntPtr.Zero;
        }

        foreach (var allocation in allocations)
        {
            Marshal.FreeHGlobal(allocation);
        }

        allocations.Clear();
        callback = null;
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

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName,
        string hostName,
        IntPtr ip4Address,
        IntPtr ip6Address,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertyCount,
        IntPtr keys,
        IntPtr values);

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern uint DnsServiceRegister(ref RegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern uint DnsServiceDeRegister(ref RegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DnsServiceRegisterComplete(uint status, IntPtr queryContext, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        [MarshalAs(UnmanagedType.Bool)]
        public bool UnicastEnabled;
    }
}

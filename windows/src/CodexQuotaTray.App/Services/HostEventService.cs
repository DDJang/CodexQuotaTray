using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CodexQuotaTray.App.Services;

internal sealed class HostEventService : IDisposable
{
    private readonly Action networkRestored;
    private readonly Action<string>? networkChanged;
    private readonly ConnectivityHintCallback callback;
    private bool wasAvailable = NetworkInterface.GetIsNetworkAvailable();
    private IntPtr notificationHandle;

    internal HostEventService(Action networkRestored, Action<string>? networkChanged = null)
    {
        this.networkRestored = networkRestored;
        this.networkChanged = networkChanged;
        callback = OnConnectivityHint;
    }

    internal void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        var result = NotifyNetworkConnectivityHintChange(callback, IntPtr.Zero, false, out notificationHandle);
        if (result != 0)
        {
            notificationHandle = IntPtr.Zero;
            System.Diagnostics.Debug.WriteLine($"Network connectivity notification registration failed: {result}");
        }
    }

    private void OnConnectivityHint(IntPtr context, int hint)
    {
        try { networkChanged?.Invoke("CONNECTIVITY_HINT"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Network change observer failed: {error.GetType().Name}");
        }
        var available = NetworkInterface.GetIsNetworkAvailable();
        var restored = !wasAvailable && available;
        wasAvailable = available;
        if (restored)
        {
            networkRestored();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args)
    {
        try { networkChanged?.Invoke("NETWORK_ADDRESS_CHANGED"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Network address observer failed: {error.GetType().Name}");
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        try { networkChanged?.Invoke("NETWORK_AVAILABILITY_CHANGED"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Network availability observer failed: {error.GetType().Name}");
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (notificationHandle != IntPtr.Zero)
        {
            _ = CancelMibChangeNotify2(notificationHandle);
            notificationHandle = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ConnectivityHintCallback(IntPtr context, int connectivityHint);

    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyNetworkConnectivityHintChange(
        ConnectivityHintCallback callback,
        IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out IntPtr notificationHandle);

    [DllImport("iphlpapi.dll")]
    private static extern uint CancelMibChangeNotify2(IntPtr notificationHandle);
}

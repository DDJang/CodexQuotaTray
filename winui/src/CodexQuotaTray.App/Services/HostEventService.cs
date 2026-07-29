using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CodexQuotaTray.App.Services;

internal sealed class HostEventService : IDisposable
{
    private readonly Action networkRestored;
    private readonly ConnectivityHintCallback callback;
    private bool wasAvailable = NetworkInterface.GetIsNetworkAvailable();
    private IntPtr notificationHandle;

    internal HostEventService(Action networkRestored)
    {
        this.networkRestored = networkRestored;
        callback = OnConnectivityHint;
    }

    internal void Start()
    {
        var result = NotifyNetworkConnectivityHintChange(callback, IntPtr.Zero, false, out notificationHandle);
        if (result != 0)
        {
            notificationHandle = IntPtr.Zero;
            System.Diagnostics.Debug.WriteLine($"Network connectivity notification registration failed: {result}");
        }
    }

    private void OnConnectivityHint(IntPtr context, int hint)
    {
        var available = NetworkInterface.GetIsNetworkAvailable();
        var restored = !wasAvailable && available;
        wasAvailable = available;
        if (restored)
        {
            networkRestored();
        }
    }

    public void Dispose()
    {
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

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexQuotaTray.App.Services;

internal sealed record LanNeighborSnapshot(
    string State,
    string? MacAddress,
    uint InterfaceIndex,
    string? CollectionError = null)
{
    internal static LanNeighborSnapshot Unknown(uint interfaceIndex, string error) =>
        new("Unknown", null, interfaceIndex, error);
}

internal static class LanNeighborDiagnosticsFormatter
{
    internal static string StateName(int value, bool isUnreachable = false) => value switch
    {
        0 => "Unreachable/Failed",
        1 => "Incomplete",
        2 => "Probe",
        3 => "Delay",
        4 => "Stale",
        5 => isUnreachable ? "Unreachable/Failed" : "Reachable",
        6 => "Permanent",
        _ => "Unknown",
    };

    internal static string FormatMac(byte[] physicalAddress, uint length)
    {
        var safeLength = Math.Min(Math.Min((int)length, physicalAddress.Length), 32);
        return safeLength == 0
            ? "unavailable"
            : string.Join("-", physicalAddress.Take(safeLength).Select(value => value.ToString("X2")));
    }
}

/// <summary>
/// Read-only wrapper around GetIpNetEntry2. It never creates, deletes, or
/// flushes a neighbor entry and therefore cannot alter system network state.
/// </summary>
internal static class WindowsLanNeighborReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorNotFound = 1168;

    internal static LanNeighborSnapshot Read(IPAddress remote, uint interfaceIndex)
    {
        if (!OperatingSystem.IsWindows() || remote.AddressFamily != AddressFamily.InterNetwork)
        {
            return LanNeighborSnapshot.Unknown(interfaceIndex, "UNSUPPORTED");
        }

        try
        {
            var row = new MibIpNetRow2
            {
                Address = SockaddrInet.FromIpv4(remote),
                InterfaceIndex = interfaceIndex,
                PhysicalAddress = new byte[32],
            };
            var result = GetIpNetEntry2(ref row);
            if (result != ErrorSuccess)
            {
                return LanNeighborSnapshot.Unknown(
                    interfaceIndex,
                    result == ErrorNotFound ? "NOT_FOUND" : $"WIN32_{result}");
            }

            var mac = LanNeighborDiagnosticsFormatter.FormatMac(row.PhysicalAddress, row.PhysicalAddressLength);
            return new LanNeighborSnapshot(
                LanNeighborDiagnosticsFormatter.StateName(row.State, (row.Flags & 0x02) != 0),
                mac == "unavailable" ? null : mac,
                row.InterfaceIndex);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
        {
            return LanNeighborSnapshot.Unknown(interfaceIndex, error.GetType().Name);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetEntry2(ref MibIpNetRow2 row);

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(2)] internal ushort Port;
        [FieldOffset(4)] internal uint Ipv4Address;

        internal static SockaddrInet FromIpv4(IPAddress address) => new()
        {
            Family = (ushort)AddressFamily.InterNetwork,
            Ipv4Address = BitConverter.ToUInt32(address.GetAddressBytes(), 0),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow2
    {
        internal SockaddrInet Address;
        internal uint InterfaceIndex;
        internal ulong InterfaceLuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] PhysicalAddress;
        internal uint PhysicalAddressLength;
        internal int State;
        internal byte Flags;
        internal uint ReachabilityTime;
    }
}

/// <summary>One bounded ICMP echo with an explicit local IPv4 source.</summary>
internal static class WindowsBoundIcmpProbe
{
    private const uint IpSuccess = 0;
    private const int IpDestinationNetworkUnreachable = 11002;
    private const int IpDestinationHostUnreachable = 11003;
    private const int IpDestinationProtocolUnreachable = 11004;
    private const int IpDestinationPortUnreachable = 11005;
    private const int IpRequestTimedOut = 11010;
    private const int IpBadRoute = 11012;

    internal static async Task<LanRepairProbeResult> SendAsync(
        IPAddress source,
        IPAddress remote,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()
            || source.AddressFamily != AddressFamily.InterNetwork
            || remote.AddressFamily != AddressFamily.InterNetwork)
        {
            return LanRepairProbeResult.IO;
        }

        return await Task.Run(
            () => Send(source, remote, timeout),
            CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static LanRepairProbeResult Send(IPAddress source, IPAddress remote, TimeSpan timeout)
    {
        var handle = IcmpCreateFile();
        if (handle == new IntPtr(-1)) return LanRepairProbeResult.IO;
        var replyBuffer = Marshal.AllocHGlobal(64);
        try
        {
            var replies = IcmpSendEcho2Ex(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                BitConverter.ToUInt32(source.GetAddressBytes(), 0),
                BitConverter.ToUInt32(remote.GetAddressBytes(), 0),
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                replyBuffer,
                64,
                checked((uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue)));
            var status = replies > 0 ? Marshal.ReadInt32(replyBuffer, sizeof(uint)) : Marshal.GetLastWin32Error();
            return status switch
            {
                (int)IpSuccess => LanRepairProbeResult.REPLY,
                IpRequestTimedOut => LanRepairProbeResult.TIMEOUT,
                IpDestinationNetworkUnreachable or IpDestinationHostUnreachable
                    or IpDestinationProtocolUnreachable or IpDestinationPortUnreachable
                    or IpBadRoute => LanRepairProbeResult.UNREACHABLE,
                _ => LanRepairProbeResult.IO,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(replyBuffer);
            _ = IcmpCloseHandle(handle);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        IntPtr icmpHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        uint sourceAddress,
        uint destinationAddress,
        IntPtr requestData,
        ushort requestSize,
        IntPtr requestOptions,
        IntPtr replyBuffer,
        uint replySize,
        uint timeout);

    [DllImport("iphlpapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(IntPtr icmpHandle);
}

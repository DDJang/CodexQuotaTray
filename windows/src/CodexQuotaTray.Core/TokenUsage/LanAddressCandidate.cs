using System.Net;
using System.Net.NetworkInformation;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed record LanAddressCandidate(
    IPAddress Address,
    IPAddress? SubnetMask,
    NetworkInterfaceType InterfaceType,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> Gateways,
    string SafeInterfaceId,
    string Description);

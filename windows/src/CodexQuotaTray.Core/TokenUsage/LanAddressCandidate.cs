using System.Net;
using System.Net.NetworkInformation;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed record LanAddressCandidate(
    IPAddress Address,
    IPAddress? SubnetMask,
    NetworkInterfaceType InterfaceType,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> Gateways,
    uint InterfaceIndex,
    string SafeInterfaceId,
    string Description);

public sealed record LanEndpointSelection(IPAddress Address, uint InterfaceIndex);

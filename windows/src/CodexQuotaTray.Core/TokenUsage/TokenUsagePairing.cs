using System.Globalization;
using System.Text;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed record TokenUsagePairing(
    Guid DeviceId,
    string Host,
    int Port,
    string PairingSecret,
    string? DisplayName)
{
    public const string Scheme = "codexquota";
    public const string PairHost = "pair";
    public const string ServiceType = "_codexquota._tcp";

    public string ToUri()
    {
        if (DeviceId == Guid.Empty)
        {
            throw new InvalidOperationException("A device identity is required for QR pairing.");
        }

        if (!System.Net.IPAddress.TryParse(Host, out var address) || !TokenUsageSyncServer.IsPrivateLanAddress(address))
        {
            throw new InvalidOperationException("QR pairing requires a private IPv4 address.");
        }

        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(PairingSecret))
        {
            throw new InvalidOperationException("QR pairing data is incomplete.");
        }

        var builder = new StringBuilder($"{Scheme}://{PairHost}?deviceId={Uri.EscapeDataString(DeviceId.ToString("D"))}");
        builder.Append($"&host={Uri.EscapeDataString(Host)}");
        builder.Append($"&port={Port.ToString(CultureInfo.InvariantCulture)}");
        builder.Append($"&token={Uri.EscapeDataString(PairingSecret)}");
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            builder.Append($"&name={Uri.EscapeDataString(DisplayName)}");
        }

        return builder.ToString();
    }
}

public sealed record TokenUsageDiscoveryMetadata(Guid DeviceId, string DisplayName, int Port)
{
    public IReadOnlyDictionary<string, string> TextAttributes => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["deviceId"] = DeviceId.ToString("D"),
        ["name"] = DisplayName,
        ["port"] = Port.ToString(CultureInfo.InvariantCulture),
    };
}

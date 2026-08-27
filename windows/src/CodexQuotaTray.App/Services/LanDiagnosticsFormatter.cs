using System.Globalization;

namespace CodexQuotaTray.App.Services;

internal static class LanDiagnosticsFormatter
{
    private const int MaximumEvents = 50;
    private const int MaximumCharacters = 16_000;

    internal static string FormatWindows(
        string version,
        LanDiagnosticState state,
        IReadOnlyList<string> recentEvents,
        DateTimeOffset now)
    {
        var lines = new List<string>
        {
            "CodexQuotaTray LAN Diagnostics",
            "",
            "App:",
            $"version={Safe(version) ?? "unavailable"}",
            "platform=Windows",
            $"timestamp={now:O}",
            "",
            "Pairing:",
            $"paired={state.Paired.ToString().ToLowerInvariant()}",
            $"device={Safe(state.DeviceId) ?? "unavailable"}",
            $"endpoint={Safe(state.Endpoint) ?? "unavailable"}",
            "",
            "Last connection:",
            $"lastSuccess={Format(state.LastSuccessUtc)}",
            $"lastFailure={Format(state.LastFailureUtc)}",
            $"failurePhase={Safe(state.LastFailurePhase) ?? "unavailable"}",
            $"attempt={state.LastAttemptId?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}",
            $"channel={Safe(state.LastAttemptChannel) ?? "unavailable"}",
            "",
            "Network:",
            "networkHandle=unavailable",
            $"interface={Safe(state.BindAddress) ?? "unavailable"}",
            "local=unavailable",
            "prefixLength=unavailable",
            "gateway=unavailable",
            $"target={Safe(state.Endpoint) ?? "unavailable"}",
            "route=unavailable",
            "transports=unavailable",
            "capabilities=unavailable",
            "SSID=unavailable",
            "BSSID=unavailable",
            "frequency=unavailable",
            "",
            "Windows service:",
            $"listener={Safe(state.ListenerStatus) ?? "unavailable"}",
            $"bind={Safe(state.BindAddress) ?? "unavailable"}",
            $"interfaceIndex={state.InterfaceIndex?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}",
            $"dnsSd={Safe(state.DnsSdStatus) ?? "unavailable"}",
            $"dnsSdInterface={state.DnsSdInterfaceIndex?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}",
            $"lastRemote={Safe(state.LastRemoteAddress) ?? "unavailable"}",
            $"lastRequest={Format(state.LastRequestUtc)} result={Safe(state.LastRequestResult) ?? "unavailable"}",
            $"lastNetworkChange={Format(state.LastNetworkChangeUtc)} reason={Safe(state.LastNetworkChangeReason) ?? "unavailable"}",
            $"lastReconcile={Format(state.LastReconcileUtc)} result={Safe(state.LastReconcileResult) ?? "unavailable"}",
            $"lastReconcileReason={Safe(state.LastReconcileReason) ?? "unavailable"}",
            $"lastRepairProbe={Format(state.LastRepairProbeUtc)} result={Safe(state.LastRepairProbeResult) ?? "unavailable"} action={Safe(state.LastRepairActionResult) ?? "unavailable"}",
            $"lastRepairRemote={Safe(state.LastRepairRemote) ?? "unavailable"}",
            $"lastRepairProbeKind={Safe(state.LastRepairProbeKind) ?? "unavailable"}",
            $"lastRepairLocalAddress={Safe(state.LastRepairLocalAddress) ?? "unavailable"}",
            $"lastRepairInterfaceIndex={state.LastRepairInterfaceIndex?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}",
            $"lastRepairSourceBound={state.LastRepairSourceBound?.ToString().ToLowerInvariant() ?? "unavailable"}",
            $"lastRepairFailureKind={Safe(state.LastRepairFailureKind) ?? "unavailable"}",
            $"neighborBeforeState={Safe(state.LastRepairNeighborBeforeState) ?? "unavailable"}",
            $"neighborBeforeMac={Safe(state.LastRepairNeighborBeforeMac) ?? "unavailable"}",
            $"neighborBeforeError={Safe(state.LastRepairNeighborBeforeError) ?? "unavailable"}",
            $"neighborAfterState={Safe(state.LastRepairNeighborAfterState) ?? "unavailable"}",
            $"neighborAfterMac={Safe(state.LastRepairNeighborAfterMac) ?? "unavailable"}",
            $"neighborAfterError={Safe(state.LastRepairNeighborAfterError) ?? "unavailable"}",
            "",
            "Recent LAN events:",
        };

        lines.AddRange(recentEvents
            .TakeLast(MaximumEvents)
            .Select(LanDiagnosticRedactor.Sanitize));
        var result = string.Join(Environment.NewLine, lines);
        return result.Length <= MaximumCharacters ? result : result[..MaximumCharacters];
    }

    private static string? Safe(string? value) => string.IsNullOrWhiteSpace(value) ? null : LanDiagnosticRedactor.Sanitize(value);

    private static string Format(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable";
}

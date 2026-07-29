namespace CodexQuotaTray.Core.Presentation;

public static class PrototypeDiagnostics
{
    public static string Create(string applicationVersion, string runtimeVersion) =>
        string.Join(
            Environment.NewLine,
            $"CodexQuotaTray demo: {applicationVersion}",
            $".NET runtime: {runtimeVersion}",
            "Data source: static demo",
            "Production user data accessed: no",
            "App Server connected: no");
}

namespace CodexQuotaTray.Core.Runtime;

public enum TrayIdentityMode
{
    Production,
    Development,
    Preview,
}

public sealed record AppLaunchProfile(bool ShowDemo, bool IsolatedPreview, bool IsDevelopmentBuild = false)
{
    public const string ProductionInstanceKey = "CodexQuotaTray";
    public const string DevelopmentInstanceKey = "CodexQuotaTray.Dev";
    public const string PreviewInstanceKey = "CodexQuotaTray.Preview";

    public bool UsePreviewIdentity => ShowDemo || IsolatedPreview;

    public string InstanceKey => TrayIdentity switch
    {
        TrayIdentityMode.Production => ProductionInstanceKey,
        TrayIdentityMode.Development => DevelopmentInstanceKey,
        TrayIdentityMode.Preview => PreviewInstanceKey,
        _ => throw new InvalidOperationException("Unknown application identity."),
    };

    public TrayIdentityMode TrayIdentity => UsePreviewIdentity
        ? TrayIdentityMode.Preview
        : IsDevelopmentBuild
            ? TrayIdentityMode.Development
            : TrayIdentityMode.Production;

    public bool CanConfigureStartup => !UsePreviewIdentity;

    public static AppLaunchProfile FromArguments(
        IEnumerable<string> processArguments,
        string? activationArguments = null,
        bool isDevelopmentBuild = false)
    {
        var activation = (activationArguments ?? string.Empty).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arguments = processArguments.Concat(activation);
        var showDemo = arguments.Any(value =>
            string.Equals(value, "--demo", StringComparison.OrdinalIgnoreCase));
        var isolatedPreview = arguments.Any(value =>
            string.Equals(value, "--isolated-preview-data", StringComparison.OrdinalIgnoreCase));

        return new AppLaunchProfile(showDemo, isolatedPreview, isDevelopmentBuild);
    }
}

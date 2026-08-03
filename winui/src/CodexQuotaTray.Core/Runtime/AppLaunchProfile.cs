namespace CodexQuotaTray.Core.Runtime;

public enum TrayIdentityMode
{
    Production,
    Preview,
}

public sealed record AppLaunchProfile(bool ShowDemo, bool IsolatedPreview)
{
    public const string ProductionInstanceKey = "CodexQuotaTray";
    public const string PreviewInstanceKey = "CodexQuotaTray.Preview";

    public bool UsePreviewIdentity => ShowDemo || IsolatedPreview;

    public string InstanceKey => UsePreviewIdentity ? PreviewInstanceKey : ProductionInstanceKey;

    public TrayIdentityMode TrayIdentity => UsePreviewIdentity
        ? TrayIdentityMode.Preview
        : TrayIdentityMode.Production;

    public bool CanConfigureStartup => !UsePreviewIdentity;

    public static AppLaunchProfile FromArguments(
        IEnumerable<string> processArguments,
        string? activationArguments = null)
    {
        var activation = (activationArguments ?? string.Empty).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arguments = processArguments.Concat(activation);
        var showDemo = arguments.Any(value =>
            string.Equals(value, "--demo", StringComparison.OrdinalIgnoreCase));
        var isolatedPreview = arguments.Any(value =>
            string.Equals(value, "--isolated-preview-data", StringComparison.OrdinalIgnoreCase));

        return new AppLaunchProfile(showDemo, isolatedPreview);
    }
}

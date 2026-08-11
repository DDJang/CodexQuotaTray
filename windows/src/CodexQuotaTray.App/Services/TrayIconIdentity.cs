namespace CodexQuotaTray.App.Services;

internal readonly record struct TrayIconIdentity(string Name, Guid Guid, string Tooltip)
{
    internal static TrayIconIdentity Production { get; } = new(
        "Production",
        new Guid("8F4F2C19-0C4C-4E1B-8F5C-50D0F1A4A77D"),
        "CodexQuotaTray");

    internal static TrayIconIdentity Development { get; } = new(
        "Development",
        new Guid("DAE4A244-1B58-46BA-9D08-E66C5EBB5A5B"),
        "CodexQuotaTray Dev");

    internal static TrayIconIdentity Preview { get; } = new(
        "Preview",
        new Guid("4B3F9C1D-6C21-4B9B-AFC7-31D8BAFE19E2"),
        "CodexQuotaTray Preview");
}

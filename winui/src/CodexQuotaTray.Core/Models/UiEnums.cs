namespace CodexQuotaTray.Core.Models;

public enum QuotaTone
{
    Accent,
    Warning,
    Critical,
    Unavailable,
}

public enum StatusTone
{
    Success,
    Refreshing,
    Warning,
    Error,
    Neutral,
}

public enum ResetCreditKind
{
    Unavailable,
    Empty,
    CountOnly,
    PartialDetails,
    CompleteDetails,
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum BackdropKind
{
    DesktopAcrylic,
    Mica,
    Opaque,
}

public enum TrayEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

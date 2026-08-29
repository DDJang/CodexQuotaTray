using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Updates;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexQuotaTray.App.Services;

internal enum AppNotificationRegistrationState
{
    NotAttempted,
    Unsupported,
    Registered,
    Failed,
}

internal sealed class WindowsAppNotificationService(Action activationRequested) : IDisposable
{
    private AppNotificationManager? manager;
    private AppNotificationSetting? lastSetting;
    private bool disposed;

    internal bool IsRegistered => manager is not null;

    internal bool? AppNotificationSupported { get; private set; }

    internal bool RegisterAttempted { get; private set; }

    internal AppNotificationRegistrationState RegistrationState { get; private set; } =
        AppNotificationRegistrationState.NotAttempted;

    internal string? DisplayName { get; private set; }

    internal string? IconPath { get; private set; }

    internal bool? IconExists { get; private set; }

    internal Exception? LastRegistrationError { get; private set; }

    internal bool TryRegister(string displayName, Uri iconUri)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (manager is not null)
        {
            return true;
        }

        DisplayName = displayName;
        IconPath = GetIconPath(iconUri);
        IconExists = iconUri.IsFile && File.Exists(iconUri.LocalPath);
        RegisterAttempted = false;
        LastRegistrationError = null;
        lastSetting = null;

        try
        {
            AppNotificationSupported = AppNotificationManager.IsSupported();
            if (AppNotificationSupported != true)
            {
                RegistrationState = AppNotificationRegistrationState.Unsupported;
                LogRegistration("unsupported");
                return false;
            }

            if (IconExists != true)
            {
                throw new FileNotFoundException(
                    "The app notification icon was not found.",
                    IconPath ?? string.Empty);
            }

            var candidate = AppNotificationManager.Default;
            lastSetting = TryReadSetting(candidate);
            RegisterAttempted = true;
            candidate.NotificationInvoked += OnNotificationInvoked;
            try
            {
                candidate.Register(displayName, iconUri);
            }
            catch
            {
                candidate.NotificationInvoked -= OnNotificationInvoked;
                throw;
            }

            manager = candidate;
            lastSetting = TryReadSetting(candidate) ?? lastSetting;
            RegistrationState = AppNotificationRegistrationState.Registered;
            LastRegistrationError = null;
            LogRegistration("registered");
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            RegistrationState = AppNotificationRegistrationState.Failed;
            LastRegistrationError = error;
            LogRegistration("failed");
            return false;
        }
    }

    internal void ShowQuotaAlert(QuotaAlert alert)
    {
        var content = QuotaNotificationFormatter.Format(alert);
        Show(content.Title, content.Body, "quota");
    }

    internal void ShowWindowsUpdateAvailable(WindowsUpdateRelease release) =>
        Show("CodexQuotaTray 更新", $"发现 Windows 新版本 {release.Version}，打开设置即可查看。", "windows-update");

    internal string CreateDiagnosticText()
    {
        var setting = FormatSetting();
        return string.Join(
            Environment.NewLine,
            "Windows notifications:",
            $"mode: {(IsRegistered ? "AppNotification" : "ShellFallback")}",
            $"appNotificationSupported: {FormatBoolean(AppNotificationSupported)}",
            $"appNotificationRegistration: {RegistrationState}",
            $"registerAttempted: {RegisterAttempted}",
            $"displayName: {DisplayName ?? "none"}",
            $"iconPath: {IconPath ?? "none"}",
            $"iconExists: {FormatBoolean(IconExists)}",
            $"setting: {setting}",
            $"registrationError: {FormatRegistrationError(LastRegistrationError)}",
            $"registrationHResult: {FormatHResult(LastRegistrationError)}");
    }

    private void Show(string title, string body, string kind)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = manager
            ?? throw new InvalidOperationException("Windows app notifications are unavailable.");
        var setting = current.Setting;
        lastSetting = setting;
        if (setting != AppNotificationSetting.Enabled)
        {
            throw new InvalidOperationException(
                $"Windows app notifications are disabled: {setting}.");
        }

        var notification = new AppNotificationBuilder()
            .AddArgument("action", "show")
            .AddText(title)
            .AddText(body)
            .BuildNotification();
        System.Diagnostics.Debug.WriteLine(
            $"Windows app notification delivery: channel=AppNotification kind={kind} setting={setting}");
        current.Show(notification);
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args) => activationRequested();

    private AppNotificationSetting? GetSetting()
    {
        if (manager is { } current)
        {
            lastSetting = TryReadSetting(current) ?? lastSetting;
        }

        return lastSetting;
    }

    private string FormatSetting() => GetSetting() switch
    {
        null => "Unavailable",
        AppNotificationSetting.Enabled => "Enabled",
        var value => $"Disabled ({value})",
    };

    private void LogRegistration(string phase) =>
        System.Diagnostics.Debug.WriteLine(
            $"Windows app notification registration: phase={phase} "
            + $"supported={FormatBoolean(AppNotificationSupported)} "
            + $"attempted={RegisterAttempted} state={RegistrationState} "
            + $"displayName={DisplayName ?? "none"} iconPath={IconPath ?? "none"} "
            + $"iconExists={FormatBoolean(IconExists)} setting={FormatSetting()} "
            + $"error={FormatRegistrationError(LastRegistrationError)} "
            + $"hresult={FormatHResult(LastRegistrationError)}");

    private static AppNotificationSetting? TryReadSetting(AppNotificationManager candidate)
    {
        try
        {
            return candidate.Setting;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Windows app notification setting unavailable: {error.GetType().Name} "
                + $"hresult=0x{unchecked((uint)error.HResult):X8}");
            return null;
        }
    }

    private static string GetIconPath(Uri iconUri) =>
        iconUri.IsFile ? iconUri.LocalPath : iconUri.ToString();

    private static string FormatBoolean(bool? value) => value switch
    {
        true => "true",
        false => "false",
        _ => "unknown",
    };

    private static string FormatRegistrationError(Exception? error)
    {
        if (error is null)
        {
            return "none";
        }

        var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 200)
        {
            message = message[..200];
        }

        return string.IsNullOrWhiteSpace(message)
            ? error.GetType().Name
            : $"{error.GetType().Name}({message})";
    }

    private static string FormatHResult(Exception? error) => error is null
        ? "none"
        : $"0x{unchecked((uint)error.HResult):X8}";

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (manager is not { } current)
        {
            return;
        }

        manager = null;
        current.NotificationInvoked -= OnNotificationInvoked;
        try
        {
            current.Unregister();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Windows app notification unregistration failed: {error.GetType().Name}");
        }
    }
}

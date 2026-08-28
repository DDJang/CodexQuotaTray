using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Updates;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowsAppNotificationService(Action activationRequested) : IDisposable
{
    private AppNotificationManager? manager;
    private bool disposed;

    internal bool IsRegistered => manager is not null;

    internal Exception? LastRegistrationError { get; private set; }

    internal bool TryRegister(string displayName, Uri iconUri)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (manager is not null)
        {
            return true;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return false;
            }

            var candidate = AppNotificationManager.Default;
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
            LastRegistrationError = null;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            LastRegistrationError = error;
            System.Diagnostics.Debug.WriteLine(
                $"Windows app notification registration failed: {error.GetType().Name}");
            return false;
        }
    }

    internal void ShowQuotaAlert(QuotaAlert alert)
    {
        var content = QuotaNotificationFormatter.Format(alert);
        Show(content.Title, content.Body);
    }

    internal void ShowWindowsUpdateAvailable(WindowsUpdateRelease release) =>
        Show("CodexQuotaTray 更新", $"发现 Windows 新版本 {release.Version}，打开设置即可查看。");

    private void Show(string title, string body)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = manager
            ?? throw new InvalidOperationException("Windows app notifications are unavailable.");
        if (current.Setting != AppNotificationSetting.Enabled)
        {
            throw new InvalidOperationException(
                $"Windows app notifications are disabled: {current.Setting}.");
        }

        var notification = new AppNotificationBuilder()
            .AddArgument("action", "show")
            .AddText(title)
            .AddText(body)
            .BuildNotification();
        current.Show(notification);
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args) => activationRequested();

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

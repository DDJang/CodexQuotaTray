using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CodexQuotaTray.App.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        SettingsRoot.DataContext = viewModel;
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        appWindow.Resize(new Windows.Graphics.SizeInt32(560, 720));
        appWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            appWindow.Hide();
        };
    }
}

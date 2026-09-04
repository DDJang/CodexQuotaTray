using Microsoft.UI.Xaml.Controls;

namespace CodexQuotaTray.App.Views;

public sealed partial class QuotaView : UserControl
{
    public QuotaView()
    {
        InitializeComponent();
    }

    internal Grid ContentBottomBoundary => FooterRow;
}

using CodexQuotaTray.Core.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace CodexQuotaTray.App.Services;

internal sealed class DiagnosticsClipboardService(IDiagnosticTextProvider provider)
{
    internal void Copy()
    {
        var package = new DataPackage();
        package.SetText(LanDiagnosticRedactor.SanitizeText(provider.CreateDiagnosticText()));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}

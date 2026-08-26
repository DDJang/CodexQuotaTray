using CodexQuotaTray.Core.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace CodexQuotaTray.App.Services;

internal sealed class DiagnosticsClipboardService(IDiagnosticTextProvider provider)
{
    internal bool TryCopy()
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(LanDiagnosticRedactor.SanitizeText(provider.CreateDiagnosticText()));

            // Flush synchronously opens the OS clipboard and can fail with
            // CLIPBRD_E_CANT_OPEN or stall when another process owns it.
            // The tray process remains alive, so SetContent is sufficient for
            // the user to paste the copied text without blocking the UI thread.
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Diagnostics clipboard copy failed exceptionClass={error.GetType().Name} hresult=0x{error.HResult:X8}");
            return false;
        }
    }
}

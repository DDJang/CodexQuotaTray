using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace CodexQuotaTray.App.Services;

internal static class TokenUsageQrCodeGenerator
{
    internal static WriteableBitmap Create(string value, int size = 220)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 2,
                PureBarcode = true,
            },
        };
        var pixels = writer.Write(value);
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels.Pixels, 0, pixels.Pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }
}

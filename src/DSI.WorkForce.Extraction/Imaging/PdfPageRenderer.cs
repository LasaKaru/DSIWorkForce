using OpenCvSharp;
using PDFtoImage;
using SkiaSharp;

namespace DSI.WorkForce.Extraction.Imaging;

/// <summary>Renders PDF pages one at a time (never the whole file in memory) to grayscale OpenCV images.</summary>
// PDFtoImage (PDFium) supports Windows, Linux and macOS - all platforms this service runs on.
#pragma warning disable CA1416
public static class PdfPageRenderer
{
    public static int GetPageCount(byte[] pdf) => Conversion.GetPageCount(pdf);

    /// <summary>Caller owns (and must dispose) the returned Mat.</summary>
    public static Mat RenderPage(byte[] pdf, int pageIndex, int dpi)
    {
        using var bitmap = Conversion.ToImage(pdf, pageIndex, options: new RenderOptions(Dpi: dpi, WithAnnotations: false));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return Cv2.ImDecode(data.ToArray(), ImreadModes.Grayscale);
    }

    /// <summary>Loads PNG/JPG/TIFF scans too, so scanners that output images work without code changes.</summary>
    public static Mat LoadImage(byte[] bytes) => Cv2.ImDecode(bytes, ImreadModes.Grayscale);
}

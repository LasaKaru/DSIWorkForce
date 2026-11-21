using OpenCvSharp;
using PDFtoImage;
using SkiaSharp;

namespace DSI.WorkForce.Extraction.Imaging;

/// <summary>Renders PDF pages one at a time (never the whole file in memory) to grayscale OpenCV images.</summary>
// PDFtoImage (PDFium) supports Windows, Linux and macOS - all platforms this service runs on.
#pragma warning disable CA1416
public static class PdfPageRenderer
{
    // PDFium (the native PDF engine) is not thread-safe: pages are processed in parallel, rendering is serialised.
    // Rendering takes ~0.1 s per page versus seconds of OCR, so this lock is never the bottleneck.
    private static readonly object PdfiumLock = new();

    public static int GetPageCount(byte[] pdf) { lock (PdfiumLock) return Conversion.GetPageCount(pdf); }

    /// <summary>Caller owns (and must dispose) the returned Mat.</summary>
    public static Mat RenderPage(byte[] pdf, int pageIndex, int dpi)
    {
        SKData data;
        lock (PdfiumLock)
        {
            using var bitmap = Conversion.ToImage(pdf, pageIndex, options: new RenderOptions(Dpi: dpi, WithAnnotations: false));
            data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        }
        using var _ = data;
        return Cv2.ImDecode(data.ToArray(), ImreadModes.Grayscale);
    }

    /// <summary>Loads PNG/JPG/TIFF scans too, so scanners that output images work without code changes.</summary>
    public static Mat LoadImage(byte[] bytes) => Cv2.ImDecode(bytes, ImreadModes.Grayscale);
}

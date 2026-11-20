using DSI.WorkForce.Core.Templates;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Imaging;

/// <summary>Binarizes the aligned page once and answers "how much ink is inside this zone?".</summary>
public sealed class InkMeter : IDisposable
{
    private readonly Mat _binary = new();
    private Mat? _withoutLines;

    public int Width => _binary.Width;
    public int Height => _binary.Height;

    public InkMeter(Mat alignedGray, OmrSettings s)
    {
        var block = s.BinarizeBlockSize % 2 == 1 ? s.BinarizeBlockSize : s.BinarizeBlockSize + 1;
        Cv2.AdaptiveThreshold(alignedGray, _binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, block, s.BinarizeC);
    }

    /// <summary>Fraction (0..1) of dark pixels in the zone after trimming <paramref name="inset"/> of each side.</summary>
    public double InkRatio(Zone z, double inset)
    {
        var rect = ToRect(z, inset);
        if (rect.Width <= 0 || rect.Height <= 0) return 0;
        using var roi = new Mat(_binary, rect);
        return Cv2.CountNonZero(roi) / (double)(rect.Width * rect.Height);
    }

    /// <summary>
    /// Same as <see cref="InkRatio"/> but ignores printed table lines (long horizontal / vertical runs),
    /// so an empty cell whose zone overlaps a grid line still reads as empty. Used for "is this box blank?".
    /// </summary>
    public double InkRatioIgnoringLines(Zone z, double inset)
    {
        EnsureLinesRemoved();
        var rect = ToRect(z, inset);
        if (rect.Width <= 0 || rect.Height <= 0) return 0;
        using var roi = new Mat(_withoutLines!, rect);
        return Cv2.CountNonZero(roi) / (double)(rect.Width * rect.Height);
    }

    private void EnsureLinesRemoved()
    {
        if (_withoutLines is not null) return;
        using var horizontal = new Mat();
        using var vertical = new Mat();
        using var lines = new Mat();
        using var hk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(20, Width / 25), 1));
        using var vk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(20, Height / 35)));
        using var grow = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var speck = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2));
        Cv2.MorphologyEx(_binary, horizontal, MorphTypes.Open, hk);
        Cv2.MorphologyEx(_binary, vertical, MorphTypes.Open, vk);
        Cv2.BitwiseOr(horizontal, vertical, lines);
        Cv2.Dilate(lines, lines, grow);                 // also remove the anti-aliased halo around lines
        _withoutLines = new Mat();
        Cv2.Subtract(_binary, lines, _withoutLines);
        Cv2.MorphologyEx(_withoutLines, _withoutLines, MorphTypes.Open, speck);   // scanner dust
    }

    public Rect ToRect(Zone z, double inset = 0)
    {
        double x = z.X * Width, y = z.Y * Height, w = z.W * Width, h = z.H * Height;
        double ix = w * inset, iy = h * inset;
        var r = new Rect((int)Math.Round(x + ix), (int)Math.Round(y + iy), (int)Math.Round(w - 2 * ix), (int)Math.Round(h - 2 * iy));
        return r.Intersect(new Rect(0, 0, Width, Height));
    }

    public void Dispose()
    {
        _binary.Dispose();
        _withoutLines?.Dispose();
    }
}

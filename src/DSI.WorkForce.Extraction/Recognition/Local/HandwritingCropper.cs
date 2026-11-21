using DSI.WorkForce.Core.Templates;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Recognition.Local;

/// <summary>
/// Turns a form box into a clean, single-line image for the text recognizer. This matters more than the model:
///  1. take the box plus a margin, so digits touching the printed border are not cut off;
///  2. erase printed table lines (long horizontal / vertical runs);
///  3. keep only ink whose centre lies inside the box (drops neighbours' strokes and dust);
///  4. crop tight to the ink, pad, and scale to the recognizer's input height.
/// </summary>
public static class HandwritingCropper
{
    /// <param name="variant">0 = standard, 1 = thicker strokes (faint pens), 2 = no line removal (slashes / dashes kept).</param>
    public static Mat Prepare(Mat page, Zone z, int variant)
    {
        int pw = page.Width, ph = page.Height;
        var boxH = z.H * ph;
        double margin = boxH * 0.25;
        var outer = new Rect((int)(z.X * pw - margin), (int)(z.Y * ph - margin), (int)(z.W * pw + 2 * margin), (int)(boxH + 2 * margin))
            .Intersect(new Rect(0, 0, pw, ph));
        var inner = new Rect((int)(z.X * pw) - outer.X, (int)(z.Y * ph) - outer.Y, (int)(z.W * pw), (int)boxH);

        using var crop = new Mat(page, outer);
        using var binary = new Mat();
        Cv2.AdaptiveThreshold(crop, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 31, 15);

        var clean = crop.Clone();
        using var ink = binary.Clone();
        if (variant != 2)
        {
            using var h = new Mat();
            using var v = new Mat();
            using var lines = new Mat();
            using var hk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(25, (int)(boxH * 1.2)), 1));
            using var vk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(25, (int)(boxH * 0.9))));
            using var grow = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.MorphologyEx(binary, h, MorphTypes.Open, hk);
            Cv2.MorphologyEx(binary, v, MorphTypes.Open, vk);
            Cv2.BitwiseOr(h, v, lines);
            Cv2.Dilate(lines, lines, grow);
            Cv2.Subtract(binary, lines, ink);
            clean.SetTo(Scalar.White, lines);
        }

        // Keep ink components whose centre is inside the box; whiten everything else.
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centroids);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
        using var drop = new Mat(ink.Size(), MatType.CV_8UC1, Scalar.All(0));
        for (var i = 1; i < n; i++)
        {
            var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            var centre = new Point((int)centroids.At<double>(i, 0), (int)centroids.At<double>(i, 1));
            if (area < 12 || !inner.Contains(centre))
            {
                using var m = new Mat();
                Cv2.Compare(labels, i, m, CmpType.EQ);
                drop.SetTo(Scalar.All(255), m);
                continue;
            }
            x0 = Math.Min(x0, stats.At<int>(i, (int)ConnectedComponentsTypes.Left));
            y0 = Math.Min(y0, stats.At<int>(i, (int)ConnectedComponentsTypes.Top));
            x1 = Math.Max(x1, stats.At<int>(i, (int)ConnectedComponentsTypes.Left) + stats.At<int>(i, (int)ConnectedComponentsTypes.Width));
            y1 = Math.Max(y1, stats.At<int>(i, (int)ConnectedComponentsTypes.Top) + stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
        }
        clean.SetTo(Scalar.White, drop);

        Mat tight = x0 == int.MaxValue ? clean : new Mat(clean, new Rect(x0, y0, x1 - x0, y1 - y0)).Clone();
        if (!ReferenceEquals(tight, clean)) clean.Dispose();

        if (variant == 1)   // thicker / darker strokes help faint ball-point writing
        {
            using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.Erode(tight, tight, k);
        }

        using var padded = new Mat();
        Cv2.CopyMakeBorder(tight, padded, 10, 10, 14, 14, BorderTypes.Constant, Scalar.White);
        tight.Dispose();
        var targetH = variant == 1 ? 48 : 64;
        var scale = targetH / (double)padded.Height;
        using var resized = new Mat();
        Cv2.Resize(padded, resized, new Size(Math.Max(16, (int)(padded.Width * scale)), targetH), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
        var bgr = new Mat();
        Cv2.CvtColor(resized, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }
}

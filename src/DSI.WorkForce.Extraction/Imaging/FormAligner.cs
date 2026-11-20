using DSI.WorkForce.Core.Templates;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Imaging;

public sealed record AlignmentResult(Mat Aligned, bool Success, string? Message) : IDisposable
{
    public void Dispose() => Aligned.Dispose();
}

/// <summary>
/// Straightens a scanned page onto the template's canonical canvas so every box sits at fixed coordinates.
/// "OuterBorder" finds the thick printed rectangle around PC-011-A and applies a perspective warp,
/// which also removes skew, scale and small rotations from the scanner / phone camera.
/// </summary>
public static class FormAligner
{
    public static AlignmentResult Align(Mat gray, AlignmentSettings s)
    {
        var size = new Size(s.CanonicalWidth, s.CanonicalHeight);
        var corners = FindBorder(gray, s.MinBorderAreaRatio, out var message);
        if (corners is null)
        {
            // Still return a resized page so a human can review it.
            var fallback = new Mat();
            Cv2.Resize(gray, fallback, size);
            return new AlignmentResult(fallback, false, message);
        }

        var dst = new[] { new Point2f(0, 0), new Point2f(size.Width - 1, 0), new Point2f(size.Width - 1, size.Height - 1), new Point2f(0, size.Height - 1) };
        using var m = Cv2.GetPerspectiveTransform(corners, dst);
        var aligned = new Mat();
        Cv2.WarpPerspective(gray, aligned, m, size, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);
        return new AlignmentResult(aligned, true, null);
    }

    /// <returns>Corners ordered top-left, top-right, bottom-right, bottom-left, or null.</returns>
    public static Point2f[]? FindBorder(Mat gray, double minAreaRatio, out string? message)
    {
        message = null;
        using var blur = new Mat();
        using var bin = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);
        Cv2.AdaptiveThreshold(blur, bin, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 31, 10);
        // Close small gaps in the printed border (faded toner, staples, punched holes).
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel);

        Cv2.FindContours(bin, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) { message = "No contours found (blank page?)."; return null; }

        var largest = contours.MaxBy(c => Cv2.ContourArea(c))!;
        var areaRatio = Cv2.ContourArea(largest) / (gray.Width * (double)gray.Height);
        if (areaRatio < minAreaRatio)
        {
            message = $"Form border covers only {areaRatio:P0} of the page (min {minAreaRatio:P0}).";
            return null;
        }

        var approx = Cv2.ApproxPolyDP(largest, 0.02 * Cv2.ArcLength(largest, true), true);
        Point2f[] pts = approx.Length == 4
            ? approx.Select(p => new Point2f(p.X, p.Y)).ToArray()
            : Cv2.MinAreaRect(largest).Points();   // border partly broken: use the best-fit rectangle
        return OrderCorners(pts);
    }

    public static Point2f[] OrderCorners(Point2f[] p)
    {
        // TL = min(x+y), BR = max(x+y), TR = min(y-x), BL = max(y-x)
        var tl = p.MinBy(q => q.X + q.Y);
        var br = p.MaxBy(q => q.X + q.Y);
        var tr = p.MinBy(q => q.Y - q.X);
        var bl = p.MaxBy(q => q.Y - q.X);
        return [tl, tr, br, bl];
    }
}

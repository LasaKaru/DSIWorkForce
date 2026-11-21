using DSI.WorkForce.Core.Templates;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Imaging;

public sealed record AlignmentResult(Mat Aligned, bool Success, string? Message, string Method = "", double Quality = 0) : IDisposable
{
    public void Dispose() => Aligned.Dispose();
}

/// <summary>
/// Straightens a scanned page onto the template's canonical canvas so every box sits at fixed coordinates.
///
/// Method 1 - printed frame lines (robust): finds the long, dark printed lines of the form and takes the outermost
///   ones as the frame. Vertical lines that run past the form (scanner streaks, dirty glass) are ignored, and light
///   features (paper edge, coloured streaks) never count as "dark". The corners are the intersections of the fitted
///   border lines, so skew and small rotations are removed too.
/// Method 2 - largest outer contour (fallback for faint prints).
///
/// Every candidate must have the template's width/height ratio, and after warping the frame must really lie on the
/// four edges (quality check). Otherwise the sheet is flagged ALIGNMENT_FAILED for a human.
/// </summary>
public static class FormAligner
{
    private const double AspectTolerance = 0.12;   // +/- 12 % of the template's width/height ratio
    private const double MinQuality = 0.55;        // share of the 4 edges that must be covered by the frame line

    public static AlignmentResult Align(Mat gray, AlignmentSettings s)
    {
        var size = new Size(s.CanonicalWidth, s.CanonicalHeight);
        var expectedAspect = s.CanonicalWidth / (double)s.CanonicalHeight;
        var reasons = new List<string>();
        AlignmentResult? best = null;

        foreach (var (method, find) in new (string, Func<Point2f[]?>)[]
                 {
                     ("frame-lines", () => FindFrameByLines(gray, s.MinBorderAreaRatio, out var why) ?? Fail(reasons, "frame lines: " + why)),
                     ("outer-contour", () => FindBorder(gray, s.MinBorderAreaRatio, out var why) ?? Fail(reasons, "outer contour: " + why)),
                 })
        {
            var corners = find();
            if (corners is null) continue;

            var aspect = Distance(corners[0], corners[1]) / Math.Max(1, Distance(corners[0], corners[3]));
            if (Math.Abs(aspect / expectedAspect - 1) > AspectTolerance)
            {
                reasons.Add($"{method}: shape {aspect:0.00} does not match the form ({expectedAspect:0.00})");
                continue;
            }

            var aligned = Warp(gray, corners, size);
            var quality = EdgeQuality(aligned);
            var candidate = new AlignmentResult(aligned, quality >= MinQuality, quality >= MinQuality ? null : $"{method}: frame not found on all edges ({quality:P0})", method, quality);
            if (candidate.Success) { best?.Dispose(); return candidate; }
            reasons.Add(candidate.Message!);
            if (best is null || candidate.Quality > best.Quality) { best?.Dispose(); best = candidate; }
            else candidate.Dispose();
        }

        if (best is not null) return best with { Message = string.Join("; ", reasons) };

        // Nothing usable: still return a resized page so a human can review it.
        var fallback = new Mat();
        Cv2.Resize(gray, fallback, size);
        return new AlignmentResult(fallback, false, reasons.Count > 0 ? string.Join("; ", reasons) : "Form frame not found.", "none");
    }

    /// <summary>Outer frame from the printed lines. Corners ordered TL, TR, BR, BL; null if not found.</summary>
    public static Point2f[]? FindFrameByLines(Mat gray, double minAreaRatio, out string? message)
    {
        message = null;
        int w = gray.Width, h = gray.Height;

        // "Dark" relative to the paper: printed lines are black; paper edges and coloured streaks are much lighter.
        var paper = Median(gray);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, paper * 0.62, 255, ThresholdTypes.BinaryInv);

        using var hMask = new Mat();
        using var vMask = new Mat();
        using (var hk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(20, w / 10), 1)))
            Cv2.MorphologyEx(dark, hMask, MorphTypes.Open, hk);
        using (var vk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(20, h / 14))))
            Cv2.MorphologyEx(dark, vMask, MorphTypes.Open, vk);

        var horizontal = Group(Segments(hMask, horizontal: true, minLength: (int)(w * 0.15)), horizontal: true)
            .Where(l => l.Length >= 0.55 * w).ToList();
        if (horizontal.Count < 2) { message = "fewer than 2 long horizontal lines"; return null; }
        var top = horizontal.MinBy(l => l.Position)!;
        var bottom = horizontal.MaxBy(l => l.Position)!;
        var formHeight = bottom.Position - top.Position;
        if (formHeight < h * 0.3) { message = "form too small on the page"; return null; }

        // Real form borders stay between the top and bottom border; scanner streaks run past them.
        var tolerance = 0.02 * h;
        var vertical = Group(Segments(vMask, horizontal: false, minLength: (int)(h * 0.10)), horizontal: false)
            .Where(l => l.Length >= 0.45 * formHeight && l.Start >= top.Position - tolerance && l.End <= bottom.Position + tolerance)
            .ToList();
        if (vertical.Count < 2) { message = "fewer than 2 frame-height vertical lines"; return null; }
        var left = vertical.MinBy(l => l.Position)!;
        var right = vertical.MaxBy(l => l.Position)!;
        if (right.Position - left.Position < w * 0.3) { message = "form too narrow on the page"; return null; }

        var corners = new[]
        {
            Intersect(top.Fit, left.Fit), Intersect(top.Fit, right.Fit),
            Intersect(bottom.Fit, right.Fit), Intersect(bottom.Fit, left.Fit),
        };
        if (corners.Any(c => c is null)) { message = "border lines do not intersect"; return null; }
        var pts = corners.Select(c => c!.Value).ToArray();
        var area = Math.Abs(Cv2.ContourArea(pts));
        if (area / (w * (double)h) < minAreaRatio) { message = $"frame covers only {area / (w * (double)h):P0} of the page"; return null; }
        return pts;
    }

    /// <summary>Fallback: largest outer contour of the dark pixels.</summary>
    public static Point2f[]? FindBorder(Mat gray, double minAreaRatio, out string? message)
    {
        message = null;
        var paper = Median(gray);
        using var bin = new Mat();
        Cv2.Threshold(gray, bin, paper * 0.62, 255, ThresholdTypes.BinaryInv);
        // Close small gaps in the printed border (faded toner, staples, punched holes).
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel);

        Cv2.FindContours(bin, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) { message = "no contours (blank page?)"; return null; }

        var largest = contours.MaxBy(c => Cv2.ContourArea(c))!;
        var areaRatio = Cv2.ContourArea(largest) / (gray.Width * (double)gray.Height);
        if (areaRatio < minAreaRatio)
        {
            message = $"largest shape covers only {areaRatio:P0} of the page (min {minAreaRatio:P0})";
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

    /// <summary>Share (0..1) of the four canvas edges covered by dark frame pixels after warping.</summary>
    public static double EdgeQuality(Mat aligned)
    {
        var paper = Median(aligned);
        using var dark = new Mat();
        Cv2.Threshold(aligned, dark, paper * 0.62, 255, ThresholdTypes.BinaryInv);
        int w = dark.Width, h = dark.Height, band = Math.Max(4, Math.Min(w, h) / 120);
        double Coverage(Rect r, bool alongX)
        {
            using var roi = new Mat(dark, r);
            using var reduced = new Mat();
            // A column / row of the band counts as "frame" when any pixel across the band is dark.
            Cv2.Reduce(roi, reduced, alongX ? ReduceDimension.Row : ReduceDimension.Column, ReduceTypes.Max, MatType.CV_8U);
            return Cv2.CountNonZero(reduced) / (double)(alongX ? r.Width : r.Height);
        }
        var scores = new[]
        {
            Coverage(new Rect(0, 0, w, band), alongX: true), Coverage(new Rect(0, h - band, w, band), alongX: true),
            Coverage(new Rect(0, 0, band, h), alongX: false), Coverage(new Rect(w - band, 0, band, h), alongX: false),
        };
        return scores.Min();
    }

    private static Mat Warp(Mat gray, Point2f[] corners, Size size)
    {
        var dst = new[] { new Point2f(0, 0), new Point2f(size.Width - 1, 0), new Point2f(size.Width - 1, size.Height - 1), new Point2f(0, size.Height - 1) };
        using var m = Cv2.GetPerspectiveTransform(corners, dst);
        var aligned = new Mat();
        Cv2.WarpPerspective(gray, aligned, m, size, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);
        return aligned;
    }

    private sealed record LineGroup(double Position, double Start, double End, Line2D Fit)
    {
        public double Length => End - Start;
    }

    private static List<LineSegmentPoint> Segments(Mat mask, bool horizontal, int minLength) =>
        Cv2.HoughLinesP(mask, 1, Math.PI / 720, 80, minLength, 25)
            .Where(s => horizontal
                ? Math.Abs(s.P2.Y - s.P1.Y) < 0.08 * Math.Abs(s.P2.X - s.P1.X)
                : Math.Abs(s.P2.X - s.P1.X) < 0.08 * Math.Abs(s.P2.Y - s.P1.Y))
            .ToList();

    /// <summary>Merges collinear segments (same row / column within 8 px) into one line with a least-squares fit.</summary>
    private static List<LineGroup> Group(List<LineSegmentPoint> segments, bool horizontal)
    {
        double Pos(LineSegmentPoint s) => horizontal ? (s.P1.Y + s.P2.Y) / 2.0 : (s.P1.X + s.P2.X) / 2.0;
        var groups = new List<List<LineSegmentPoint>>();
        foreach (var s in segments.OrderBy(Pos))
        {
            if (groups.Count > 0 && Math.Abs(Pos(s) - Pos(groups[^1][^1])) <= 8) groups[^1].Add(s);
            else groups.Add([s]);
        }
        return groups.Select(g =>
        {
            var pts = g.SelectMany(s => new[] { new Point2f(s.P1.X, s.P1.Y), new Point2f(s.P2.X, s.P2.Y) }).ToArray();
            var fit = Cv2.FitLine(pts, DistanceTypes.L2, 0, 0.01, 0.01);
            var along = horizontal ? pts.Select(p => (double)p.X) : pts.Select(p => (double)p.Y);
            return new LineGroup(g.Average(Pos), along.Min(), along.Max(), fit);
        }).ToList();
    }

    private static Point2f? Intersect(Line2D a, Line2D b)
    {
        var det = a.Vx * -b.Vy - a.Vy * -b.Vx;
        if (Math.Abs(det) < 1e-9) return null;
        var t = ((b.X1 - a.X1) * -b.Vy - (b.Y1 - a.Y1) * -b.Vx) / det;
        return new Point2f((float)(a.X1 + t * a.Vx), (float)(a.Y1 + t * a.Vy));
    }

    private static double Distance(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Median(Mat gray)
    {
        using var hist = new Mat();
        Cv2.CalcHist([gray], [0], null, hist, 1, [256], [new Rangef(0, 256)]);
        var half = gray.Total() / 2.0;
        double sum = 0;
        for (var i = 0; i < 256; i++)
        {
            sum += hist.At<float>(i);
            if (sum >= half) return i;
        }
        return 200;
    }

    private static Point2f[]? Fail(List<string> reasons, string reason)
    {
        reasons.Add(reason);
        return null;
    }
}

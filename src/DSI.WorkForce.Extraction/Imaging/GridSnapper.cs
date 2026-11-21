using DSI.WorkForce.Core.Templates;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Imaging;

/// <summary>
/// Finds the real printed table lines inside a template zone and returns the actual row / column boundaries.
/// The template splits a table evenly, but printed forms are not perfectly even (the hourly grid's 12 columns differ
/// in width, print runs vary). Each expected boundary snaps to the nearest detected printed line; where no line is
/// found the template position is kept. This makes every cell sit exactly between its own lines.
/// </summary>
public sealed class GridLayout(double[] xEdges, double[] yEdges)
{
    /// <summary>Column boundaries as page fractions (columns + 1 values).</summary>
    public double[] XEdges { get; } = xEdges;
    /// <summary>Row boundaries as page fractions (rows + 1 values).</summary>
    public double[] YEdges { get; } = yEdges;

    public int SnappedLines { get; init; }

    /// <summary>Zone of cell (row, column), both 1-based.</summary>
    public Zone Cell(int row, int column) =>
        new(XEdges[column - 1], YEdges[row - 1], XEdges[column] - XEdges[column - 1], YEdges[row] - YEdges[row - 1]);

    public static GridLayout Even(Zone z, int rows, int columns) => new(
        Enumerable.Range(0, columns + 1).Select(i => z.X + z.W * i / columns).ToArray(),
        Enumerable.Range(0, rows + 1).Select(i => z.Y + z.H * i / rows).ToArray());
}

public static class GridSnapper
{
    public static GridLayout Snap(Mat alignedGray, Zone zone, int rows, int columns)
    {
        var even = GridLayout.Even(zone, rows, columns);
        if (rows <= 1 && columns <= 1) return even;

        int pw = alignedGray.Width, ph = alignedGray.Height;
        // Search a little outside the zone so the outer table lines are found too.
        var marginX = zone.W * pw / Math.Max(1, columns) * 0.5;
        var marginY = zone.H * ph / Math.Max(1, rows) * 0.5;
        var roiRect = new Rect((int)(zone.X * pw - marginX), (int)(zone.Y * ph - marginY), (int)(zone.W * pw + 2 * marginX), (int)(zone.H * ph + 2 * marginY))
            .Intersect(new Rect(0, 0, pw, ph));
        if (roiRect.Width < 10 || roiRect.Height < 10) return even;

        using var roi = new Mat(alignedGray, roiRect);
        var paper = StrokeFilter.PaperLevel(roi);
        using var dark = new Mat();
        Cv2.Threshold(roi, dark, paper * 0.62, 255, ThresholdTypes.BinaryInv);

        var snapped = 0;
        var xs = even.XEdges;
        if (columns > 1)
        {
            // Printed column lines run (almost) the full table height; handwriting and short streak pieces do not.
            var lines = LinePositions(dark, vertical: true, minRun: (int)(zone.H * ph * 0.6));
            xs = Snap(even.XEdges, lines.Select(x => (x + roiRect.X) / (double)pw).ToList(), zone.W / columns * 0.4, ref snapped);
        }
        var ys = even.YEdges;
        if (rows > 1)
        {
            var lines = LinePositions(dark, vertical: false, minRun: (int)(zone.W * pw * 0.6));
            ys = Snap(even.YEdges, lines.Select(y => (y + roiRect.Y) / (double)ph).ToList(), zone.H / rows * 0.4, ref snapped);
        }
        return new GridLayout(xs, ys) { SnappedLines = snapped };
    }

    /// <summary>Centres of printed lines: rows/columns of the mask with a long dark run (after a morphological open).</summary>
    private static List<double> LinePositions(Mat dark, bool vertical, int minRun)
    {
        using var mask = new Mat();
        var length = Math.Max(10, minRun);
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, vertical ? new Size(1, length) : new Size(length, 1)))
            Cv2.MorphologyEx(dark, mask, MorphTypes.Open, k);
        using var profile = new Mat();
        Cv2.Reduce(mask, profile, vertical ? ReduceDimension.Row : ReduceDimension.Column, ReduceTypes.Max, MatType.CV_8U);

        var count = vertical ? profile.Cols : profile.Rows;
        var centres = new List<double>();
        int start = -1;
        for (var i = 0; i <= count; i++)
        {
            var on = i < count && (vertical ? profile.At<byte>(0, i) : profile.At<byte>(i, 0)) > 0;
            if (on && start < 0) start = i;
            if (!on && start >= 0) { centres.Add((start + i - 1) / 2.0); start = -1; }
        }
        return centres;
    }

    /// <summary>Moves each expected boundary to the nearest detected line within <paramref name="tolerance"/>.</summary>
    private static double[] Snap(double[] expected, List<double> found, double tolerance, ref int snapped)
    {
        var result = (double[])expected.Clone();
        var used = new HashSet<int>();
        for (var i = 0; i < expected.Length; i++)
        {
            var best = -1;
            var bestDist = tolerance;
            for (var j = 0; j < found.Count; j++)
            {
                var d = Math.Abs(found[j] - expected[i]);
                if (d < bestDist && !used.Contains(j)) { best = j; bestDist = d; }
            }
            if (best < 0) continue;
            used.Add(best);
            result[i] = found[best];
            snapped++;
        }
        // Keep boundaries strictly increasing (a mis-snap must never produce an empty or negative cell).
        for (var i = 1; i < result.Length; i++)
            if (result[i] <= result[i - 1] + 1e-4) result[i] = Math.Max(expected[i], result[i - 1] + 1e-4);
        return result;
    }
}

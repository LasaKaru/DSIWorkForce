using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Imaging;

/// <summary>
/// Decides which ink blobs inside a form box are real handwriting. Scanner streaks (dirty glass / ADF rollers),
/// faint coloured lines and stray table-line pieces are removed:
///  * light blobs: much lighter than pen ink (coloured streaks are pale once converted to grey);
///  * box-spanning blobs: thin and running from the top to the bottom of the box (a handwritten "1" never touches both).
/// </summary>
public static class StrokeFilter
{
    /// <summary>Paper brightness estimate (median grey) of a page.</summary>
    public static double PaperLevel(Mat gray)
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

    /// <summary>
    /// Clears non-handwriting blobs from <paramref name="binaryRoi"/> (white = ink) in place, using the matching grey image.
    /// </summary>
    /// <param name="spanRect">Area a blob must cross completely (top to bottom) to count as a streak; usually the box itself.</param>
    public static void RemoveNonHandwriting(Mat binaryRoi, Mat grayRoi, double paper, Rect? spanRect = null, int minArea = 12)
    {
        var span = spanRect ?? new Rect(0, 0, binaryRoi.Width, binaryRoi.Height);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(binaryRoi, labels, stats, centroids);
        if (n <= 1) return;

        // Mean grey per blob in one pass.
        var sums = new double[n];
        var counts = new int[n];
        var lab = labels.GetGenericIndexer<int>();
        var g = grayRoi.GetGenericIndexer<byte>();
        for (var y = 0; y < labels.Rows; y++)
            for (var x = 0; x < labels.Cols; x++)
            {
                var l = lab[y, x];
                if (l == 0) continue;
                sums[l] += g[y, x];
                counts[l]++;
            }

        var lightLimit = paper * 0.78;   // pen ink is clearly darker than this; pale streaks are not
        using var drop = new Mat(binaryRoi.Size(), MatType.CV_8UC1, Scalar.All(0));
        for (var i = 1; i < n; i++)
        {
            int left = stats.At<int>(i, (int)ConnectedComponentsTypes.Left), top = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width), h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            var meanGrey = counts[i] > 0 ? sums[i] / counts[i] : 255;

            var tooSmall = area < minArea;
            var light = meanGrey > lightLimit;
            var spansBox = top <= span.Top + 2 && top + h >= span.Bottom - 2 && w <= Math.Max(6, span.Width * 0.2);
            if (!tooSmall && !light && !spansBox) continue;

            using var m = new Mat();
            Cv2.Compare(labels, i, m, CmpType.EQ);
            drop.SetTo(Scalar.All(255), m);
        }
        binaryRoi.SetTo(Scalar.All(0), drop);
    }
}

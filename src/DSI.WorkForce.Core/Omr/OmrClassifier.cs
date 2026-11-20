namespace DSI.WorkForce.Core.Omr;

public enum MarkState { Empty, Marked, Uncertain }

/// <summary>Turns "fraction of dark pixels in a box" into a decision + confidence. Pure maths, unit tested.</summary>
public static class OmrClassifier
{
    public static (MarkState State, decimal Confidence) Classify(double inkRatio, double threshold, double uncertainBand)
    {
        if (inkRatio >= threshold + uncertainBand) return (MarkState.Marked, 0.99m);
        if (inkRatio <= Math.Max(0, threshold - uncertainBand)) return (MarkState.Empty, 0.99m);
        // Inside the grey zone: confidence drops the closer we are to the threshold.
        var distance = Math.Abs(inkRatio - threshold) / Math.Max(uncertainBand, 1e-9);
        return (MarkState.Uncertain, (decimal)Math.Round(0.5 + 0.35 * Math.Min(distance, 1), 2));
    }

    /// <summary>Counts ticks in one row of the tally grid. Confidence = weakest cell.</summary>
    public static (int Ticks, decimal Confidence, int UncertainCells) CountTally(IReadOnlyList<double> cellInkRatios, double threshold, double uncertainBand)
    {
        int ticks = 0, uncertain = 0;
        decimal conf = 1m;
        foreach (var r in cellInkRatios)
        {
            var (state, c) = Classify(r, threshold, uncertainBand);
            if (state == MarkState.Marked) ticks++;
            else if (state == MarkState.Uncertain)
            {
                uncertain++;
                if (r >= threshold) ticks++;
            }
            conf = Math.Min(conf, c);
        }
        return (ticks, conf, uncertain);
    }
}

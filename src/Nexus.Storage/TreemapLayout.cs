namespace Nexus.Storage;

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Area => W * H;
    public bool Contains(double x, double y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>Squarified treemap (Bruls, Huizing, van Wijk): rectangles stay as close to squares as possible.</summary>
public static class TreemapLayout
{
    /// <summary>
    /// Lays out items inside <paramref name="bounds"/>; areas are proportional to <paramref name="sizes"/>.
    /// Sizes must be sorted in decreasing order (zero/negative sizes get an empty rectangle). Result i is for sizes[i].
    /// </summary>
    public static RectD[] Squarify(IReadOnlyList<double> sizes, RectD bounds)
    {
        var result = new RectD[sizes.Count];
        double total = 0;
        foreach (var s in sizes) if (s > 0) total += s;
        if (total <= 0 || bounds.W <= 0 || bounds.H <= 0) return result;

        double scale = bounds.Area / total;
        RectD free = bounds;
        int i = 0, count = sizes.Count;
        while (i < count && sizes[i] > 0)
        {
            double side = Math.Min(free.W, free.H);
            int start = i;
            double rowArea = 0, worst = double.MaxValue;
            // grow the row while the worst aspect ratio keeps improving
            while (i < count && sizes[i] > 0)
            {
                double a = sizes[i] * scale;
                double newWorst = Worst(sizes, start, i, rowArea + a, scale, side);
                if (i > start && newWorst > worst) break;
                rowArea += a; worst = newWorst; i++;
            }
            free = PlaceRow(sizes, start, i, rowArea, scale, free, result);
        }
        return result;
    }

    private static double Worst(IReadOnlyList<double> sizes, int start, int end, double rowArea, double scale, double side)
    {
        double max = sizes[start] * scale, min = sizes[end] * scale;
        // end is the candidate being added; sizes are decreasing so min is the candidate, max the first of the row
        double s2 = side * side, r2 = rowArea * rowArea;
        return Math.Max(s2 * max / r2, r2 / (s2 * min));
    }

    private static RectD PlaceRow(IReadOnlyList<double> sizes, int start, int end, double rowArea, double scale, RectD free, RectD[] result)
    {
        bool horizontal = free.W >= free.H; // row runs along the short side
        if (horizontal)
        {
            double w = rowArea / free.H, y = free.Y;
            for (int k = start; k < end; k++)
            {
                double h = sizes[k] * scale / w;
                result[k] = new RectD(free.X, y, w, h);
                y += h;
            }
            return new RectD(free.X + w, free.Y, Math.Max(0, free.W - w), free.H);
        }
        else
        {
            double h = rowArea / free.W, x = free.X;
            for (int k = start; k < end; k++)
            {
                double w = sizes[k] * scale / h;
                result[k] = new RectD(x, free.Y, w, h);
                x += w;
            }
            return new RectD(free.X, free.Y + h, free.W, Math.Max(0, free.H - h));
        }
    }
}

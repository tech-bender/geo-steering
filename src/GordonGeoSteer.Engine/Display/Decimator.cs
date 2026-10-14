namespace GordonGeoSteer.Engine.Display;

/// <summary>
/// Screen-space min/max decimation for drawing long curves. Points are already in pixel coordinates:
/// <c>along</c> is the coordinate on the plot's depth/position axis, <c>across</c> the value
/// coordinate. Consecutive points that fall into the same integer pixel along the axis are replaced
/// by at most four points (first, min, max, last, in original order), so every extreme is kept and the
/// drawn shape is identical at pixel resolution. Consecutive grouping (rather than global bucketing)
/// keeps the sample order, so non-monotone data (a lateral plotted on TVT that goes up, down or
/// stacks on itself) is still drawn correctly. NaN in either coordinate breaks the line; segments are
/// returned separately.
/// </summary>
public static class Decimator
{
    public readonly record struct P(double Along, double Across);

    public static List<List<P>> Decimate(IReadOnlyList<double> along, IReadOnlyList<double> across)
    {
        var result = new List<List<P>>();
        List<P>? seg = null;
        int n = Math.Min(along.Count, across.Count);
        var idx = new int[4];
        int i = 0;
        while (i < n)
        {
            double a = along[i], v = across[i];
            if (double.IsNaN(a) || double.IsNaN(v) || double.IsInfinity(a) || double.IsInfinity(v))
            {
                seg = null; i++;
                continue;
            }
            if (seg == null) { seg = new List<P>(); result.Add(seg); }
            long px = (long)Math.Floor(a);
            // gather the run of consecutive valid samples in this pixel
            int start = i, iMin = i, iMax = i;
            int j = i + 1;
            while (j < n)
            {
                double aj = along[j], vj = across[j];
                if (double.IsNaN(aj) || double.IsNaN(vj) || double.IsInfinity(aj) || double.IsInfinity(vj)) break;
                if ((long)Math.Floor(aj) != px) break;
                if (vj < across[iMin]) iMin = j;
                if (vj > across[iMax]) iMax = j;
                j++;
            }
            int end = j - 1;
            if (end - start < 3)
            {
                for (int k = start; k <= end; k++) seg.Add(new P(along[k], across[k]));
            }
            else
            {
                idx[0] = start; idx[1] = iMin; idx[2] = iMax; idx[3] = end;
                Array.Sort(idx);
                int prev = -1;
                foreach (var k in idx)
                {
                    if (k == prev) continue;
                    seg.Add(new P(along[k], across[k]));
                    prev = k;
                }
            }
            i = j;
        }
        return result;
    }
}

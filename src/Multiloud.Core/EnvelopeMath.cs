namespace Multiloud.Core;

public static class EnvelopeMath
{
    /// <summary>Canonical audio shape; adding a point on an existing line changes no processing.</summary>
    public static List<EnvelopePoint> Canonicalize(IReadOnlyList<EnvelopePoint> points)
    {
        var result = points.Where(p => double.IsFinite(p.Time) && double.IsFinite(p.GainDb))
            .GroupBy(p => p.Time).Select(g => g.Last()).OrderBy(p => p.Time).ToList();
        if (result.All(p => p.GainDb == 0)) return [];
        for (int i = 1; i + 1 < result.Count;)
        {
            var left = result[i - 1]; var point = result[i]; var right = result[i + 1];
            double line = left.GainDb + (right.GainDb - left.GainDb) * ((point.Time - left.Time) / (right.Time - left.Time));
            if (Math.Abs(point.GainDb - line) <= 1e-10) { result.RemoveAt(i); if (i > 1) i--; }
            else i++;
        }
        return result;
    }
}

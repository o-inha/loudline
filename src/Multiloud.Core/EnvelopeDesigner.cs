namespace Multiloud.Core;

/// <summary>Conservative, section-scale gain suggestions; never a syllable/transient compressor.</summary>
public static partial class EnvelopeDesigner
{
    public static List<EnvelopePoint> Design(AnalysisResult analysis, double duration, ProcessingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(settings);
        LevelingDetector.ValidateSensitivity(settings.BassSensitivity);
        if (!double.IsFinite(duration) || duration <= 0) return [new(0, 0)];
        var detector = LevelingDetector.GetProfile(analysis, settings);
        var source = detector.ShortTerm.OrderBy(p => p.Time).ToArray();
        var fullBand = analysis.ShortTerm.OrderBy(p => p.Time).ToArray();
        if (source.Length < 5 || analysis.IntegratedLufs <= -69)
            return [new(0, 0), new(duration, 0)];
        double bound = Math.Clamp(settings.MaxLevelingDb, 0, 18);
        double transition = Math.Clamp(settings.TransitionSeconds, 2, 30);
        double fullBandGate = Math.Max(-55, analysis.IntegratedLufs - 25);
        double gate = fullBandGate + (detector.ReferenceLoudness - analysis.IntegratedLufs);
        double Median(IEnumerable<double> values)
        {
            var sorted = values.Where(double.IsFinite).Order().ToArray();
            return sorted.Length == 0 ? -90 : LoudnessAnalyzer.Percentile(sorted, .5);
        }
        double[] levels = new double[source.Length];
        for (int i = 0; i < source.Length; i++)
            levels[i] = Median(source.Skip(Math.Max(0, i - 2)).Take(Math.Min(source.Length - 1, i + 2) - Math.Max(0, i - 2) + 1).Select(p => p.Loudness));

        // A change is a section boundary only if several seconds on both sides
        // support it. Isolated snare hits and short breaks cannot create one.
        var candidates = new List<(int Index, double Contrast, double Slope)>();
        for (int i = 5; i + 5 < source.Length; i++)
        {
            double before = Median(levels.Skip(i - 5).Take(4));
            double after = Median(levels.Skip(i + 2).Take(4));
            if (before < gate || after < gate) continue;
            if (detector.UsesReducedBass &&
                (Median(fullBand.Skip(i - 5).Take(4).Select(p => p.Loudness)) < fullBandGate ||
                 Median(fullBand.Skip(i + 2).Take(4).Select(p => p.Loudness)) < fullBandGate)) continue;
            double contrast = Math.Abs(after - before);
            double slope = Math.Abs(levels[Math.Min(levels.Length - 1, i + 1)] - levels[Math.Max(0, i - 1)]);
            if (contrast >= 3.5 && slope >= 1.0) candidates.Add((i, contrast, slope));
        }
        var boundaries = new List<double> { 0 };
        for (int i = 0; i < candidates.Count;)
        {
            int end = i + 1;
            while (end < candidates.Count && candidates[end].Index - candidates[end - 1].Index <= 4) end++;
            var best = candidates.Skip(i).Take(end - i).OrderByDescending(c => c.Slope).ThenByDescending(c => c.Contrast).First();
            double t = source[best.Index].Time;
            if (t - boundaries[^1] >= 6 && duration - t >= 6) boundaries.Add(t);
            i = end;
        }
        boundaries.Add(duration);
        double anchor = detector.ReferenceLoudness;
        var raw = new double[source.Length];
        int neighborhoodStart = 0, neighborhoodEnd = 0;
        for (int i = 0; i < source.Length; i++)
        {
            int segment = 0;
            while (segment + 1 < boundaries.Count - 1 && source[i].Time >= boundaries[segment + 1]) segment++;
            double start = Math.Max(boundaries[segment], source[i].Time - 5);
            double end = Math.Min(boundaries[segment + 1], source[i].Time + 5);
            while (neighborhoodStart < source.Length && source[neighborhoodStart].Time < start) neighborhoodStart++;
            if (neighborhoodEnd < neighborhoodStart) neighborhoodEnd = neighborhoodStart;
            while (neighborhoodEnd < source.Length && source[neighborhoodEnd].Time <= end) neighborhoodEnd++;
            var neighborhood = source.Skip(neighborhoodStart).Take(neighborhoodEnd - neighborhoodStart).Where(p => p.Loudness >= gate).Select(p => p.Loudness);
            double level = Median(neighborhood);
            if (source[i].Loudness < gate || level < gate || fullBand[i].Loudness < fullBandGate) { raw[i] = 0; continue; }
            double difference = anchor - level;
            // Leave a one-LU expression band untouched.
            raw[i] = Math.Clamp(Math.Sign(difference) * Math.Max(0, Math.Abs(difference) - 1), -bound, bound);
        }

        // A symmetric, offline ramp anticipates sustained changes instead of
        // waiting for them to become uncomfortably loud/quiet during playback.
        var smooth = new double[source.Length];
        double radius = transition / 2;
        for (int i = 0; i < source.Length; i++)
        {
            double weighted = 0, total = 0;
            for (int j = Math.Max(0, i - (int)Math.Ceiling(radius)); j <= Math.Min(source.Length - 1, i + (int)Math.Ceiling(radius)); j++)
            {
                double distance = Math.Abs(source[i].Time - source[j].Time);
                double weight = Math.Max(0, 1 - distance / Math.Max(1, radius + 1));
                weighted += raw[j] * weight;
                total += weight;
            }
            smooth[i] = total > 0 ? weighted / total : 0;
        }

        // Deliberate fades and near-silence must not be turned into crescendos
        // of room noise. Only a sustained plateau qualifies for positive gain.
        for (int i = 0; i < source.Length; i++)
        {
            if (!AllowsPositiveGain(source, fullBand, i, gate, fullBandGate)) smooth[i] = Math.Min(0, smooth[i]);
            if (smooth[i] <= 0) continue;
            if (detector.UsesReducedBass)
            {
                double confidence = i < detector.BassConfidence.Count ? detector.BassConfidence[i] : 1;
                double cap = Math.Min(bound, 1.5 + Math.Max(0, bound - 1.5) * confidence);
                smooth[i] = Math.Min(smooth[i], cap);
            }
        }
        // Bound gain slew even after fade/noise protection alters suggestions.
        double maxSlope = Math.Max(.2, 2 * Math.Max(1, bound) / transition);
        for (int i = 1; i < smooth.Length; i++)
        {
            double delta = maxSlope * (source[i].Time - source[i - 1].Time);
            // Reduce neighboring gains to meet the slew bound; never raise a
            // protected silent/fading point merely to make a ramp smoother.
            smooth[i] = Math.Min(smooth[i], smooth[i - 1] + delta);
        }
        for (int i = smooth.Length - 2; i >= 0; i--)
        {
            double delta = maxSlope * (source[i + 1].Time - source[i].Time);
            smooth[i] = Math.Min(smooth[i], smooth[i + 1] + delta);
        }
        var dense = new List<EnvelopePoint> { new(0, smooth[0]) };
        for (int i = 0; i < source.Length; i++)
            if (source[i].Time > 0 && source[i].Time < duration)
                dense.Add(new(source[i].Time, Math.Clamp(smooth[i], -bound, bound)));
        dense.Add(new(duration, smooth[^1]));
        // Douglas–Peucker in dB makes a small, genuinely editable envelope.
        var keep = new SortedSet<int> { 0, dense.Count - 1 };
        Simplify(dense, 0, dense.Count - 1, .22, keep);
        return RefineAbruptSections(analysis, duration, settings, keep.Select(i => dense[i]).ToList());
    }

    private static void Simplify(List<EnvelopePoint> points, int first, int last, double tolerance, SortedSet<int> keep)
    {
        if (last <= first + 1) return;
        double biggest = tolerance;
        int selected = -1;
        double dt = points[last].Time - points[first].Time;
        for (int i = first + 1; i < last; i++)
        {
            double fraction = dt > 0 ? (points[i].Time - points[first].Time) / dt : 0;
            double line = points[first].GainDb + fraction * (points[last].GainDb - points[first].GainDb);
            double error = Math.Abs(points[i].GainDb - line);
            if (error > biggest) { biggest = error; selected = i; }
        }
        if (selected < 0) return;
        keep.Add(selected);
        Simplify(points, first, selected, tolerance, keep);
        Simplify(points, selected, last, tolerance, keep);
    }
}

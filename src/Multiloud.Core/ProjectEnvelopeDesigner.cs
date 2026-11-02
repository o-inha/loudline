namespace Multiloud.Core;

/// <summary>Project-time leveling, built from immutable detector summaries rather than concatenated audio.</summary>
public static class ProjectEnvelopeDesigner
{
    public static List<EnvelopePoint> Design(IReadOnlyList<AudioTrack> tracks, ProcessingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(settings);
        double duration = tracks.Sum(track => track.Duration);
        if (duration <= 0) return [];
        // One common detector clock lets its three-second windows and sustained
        // section detector see across file boundaries, including mixed formats.
        int bins = checked((int)Math.Ceiling(duration * 10));
        var full = new double[bins]; var high = new double[bins];
        double offset = 0;
        foreach (var track in tracks)
        {
            if (track.Analysis is { } analysis)
            {
                var energy = analysis.LevelingEnergy;
                if (energy != null)
                {
                    for (int i = 0; i < energy.FramesPerBin.Count; i++)
                    {
                        double start = offset + i * energy.HopFrames / (double)energy.SampleRate;
                        double end = Math.Min(offset + track.Duration, start + energy.FramesPerBin[i] / (double)energy.SampleRate);
                        AddInterval(full, high, start, end, energy.FullBandEnergy[i], energy.HighPassedEnergy[i]);
                    }
                }
                else
                {
                    // Old analyses / fixtures without detail retain the full-band
                    // detector rather than fabricating unavailable spectral data.
                    var profile = analysis.ShortTerm;
                    int index = 0;
                    for (int bin = Math.Max(0, (int)Math.Floor(offset * 10)); bin < Math.Min(bins, (int)Math.Ceiling((offset + track.Duration) * 10)); bin++)
                    {
                        double time = Math.Clamp((bin + .5) / 10 - offset, 0, track.Duration);
                        while (index + 1 < profile.Count && profile[index + 1].Time <= time) index++;
                        double level = profile.Count == 0 ? analysis.IntegratedLufs : profile[index].Loudness;
                        double value = level > -70 ? Math.Pow(10, (level + .691) / 10) : 0;
                        AddInterval(full, high, Math.Max(offset, bin / 10.0), Math.Min(offset + track.Duration, (bin + 1) / 10.0), value, value);
                    }
                }
            }
            offset += track.Duration;
        }
        var counts = Enumerable.Repeat(1, bins).ToArray();
        var composite = new AnalysisResult
        {
            IntegratedLufs = Integrated(full),
            ShortTerm = ShortTerm(full, duration),
            LevelingEnergy = new LevelingEnergyProfile(10, 1, bins, full, high, counts)
        };
        var result = EnvelopeDesigner.Design(composite, duration, settings);
        return result.Select(point => point with { Time = Math.Clamp(point.Time, 0, duration), GainDb = Math.Clamp(point.GainDb, -24, 24) })
            .GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
    }

    private static void AddInterval(double[] full, double[] high, double start, double end, double f, double h)
    {
        for (int bin = Math.Max(0, (int)Math.Floor(start * 10)); bin < Math.Min(full.Length, (int)Math.Ceiling(end * 10)); bin++)
        {
            double fraction = Math.Max(0, Math.Min(end, (bin + 1) / 10.0) - Math.Max(start, bin / 10.0)) * 10;
            full[bin] += Math.Max(0, f) * fraction;
            high[bin] += Math.Max(0, h) * fraction;
        }
    }

    private static double Integrated(double[] energy)
    {
        var blocks = new List<double>();
        double sum = 0;
        for (int i = 0; i < energy.Length; i++)
        {
            sum += energy[i]; if (i >= 4) sum -= energy[i - 4];
            if (i >= 3 && LoudnessAnalyzer.EnergyToLufs(sum / 4) >= -70) blocks.Add(sum / 4);
        }
        if (blocks.Count == 0) return -70;
        double gate = LoudnessAnalyzer.EnergyToLufs(blocks.Average()) - 10;
        var active = blocks.Where(value => LoudnessAnalyzer.EnergyToLufs(value) >= gate).ToArray();
        return active.Length == 0 ? -70 : LoudnessAnalyzer.EnergyToLufs(active.Average());
    }

    private static List<LoudnessFrame> ShortTerm(double[] energy, double duration)
    {
        var sum = new double[energy.Length + 1];
        for (int i = 0; i < energy.Length; i++) sum[i + 1] = sum[i] + energy[i];
        var profile = new List<LoudnessFrame>();
        for (int center = 5; center < energy.Length + 5; center += 10)
        {
            int start = Math.Max(0, center - 15), end = Math.Min(energy.Length, center + 15);
            if (end > start) profile.Add(new(Math.Min(duration, center / 10.0),
                Math.Max(-90, LoudnessAnalyzer.EnergyToLufs((sum[end] - sum[start]) / (end - start)))));
        }
        return profile;
    }

    /// <summary>Exact local render view of one shared project curve. Boundary knots are derived, never UI points.</summary>
    public static List<EnvelopePoint> Crop(IReadOnlyList<EnvelopePoint> points, double start, double duration, bool canonicalize = true)
    {
        if (!double.IsFinite(start) || !double.IsFinite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        // On-line UI knots must not even change floating-point interpolation
        // order at a file boundary and invalidate otherwise-identical DSP keys.
        if (canonicalize) points = EnvelopeMath.Canonicalize(points);
        double end = start + duration;
        var result = new List<EnvelopePoint> { new(0, AudioProcessor.EnvelopeGainAt(points, start)) };
        int lo = 0, hi = points.Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (points[mid].Time <= start) lo = mid + 1; else hi = mid; }
        for (int index = lo; index < points.Count && points[index].Time < end; index++)
            result.Add(points[index] with { Time = points[index].Time - start });
        if (duration > 0) result.Add(new(duration, AudioProcessor.EnvelopeGainAt(points, end)));
        return result;
    }

    /// <summary>Migrates file-local authored+derived curves without losing their interior knots.</summary>
    public static List<EnvelopePoint> FromTracks(IReadOnlyList<AudioTrack> tracks)
    {
        var points = new List<EnvelopePoint>(); double offset = 0;
        foreach (var track in tracks)
        {
            var local = TransitionDesigner.CombinedEnvelope(track);
            if (local.Count == 0) local = [new(0, 0), new(track.Duration, 0)];
            // At an old discontinuous join, retain both values one sample apart.
            // New projects have a genuinely shared curve with no forced edge knot.
            foreach (var point in local)
            {
                double time = offset + Math.Clamp(point.Time, 0, track.Duration);
                if (points.Count > 0 && time <= points[^1].Time)
                {
                    if (Math.Abs(points[^1].GainDb - point.GainDb) < 1e-10) continue;
                    double gap = Math.Min(1 / (double)Math.Max(1, track.SampleRate), track.Duration / 1000);
                    if (gap <= 0) continue;
                    points[^1] = points[^1] with { Time = time - gap };
                }
                points.Add(new(time, point.GainDb));
            }
            offset += track.Duration;
        }
        return points;
    }
}

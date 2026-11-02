namespace Multiloud.Core;

/// <summary>
/// Small, derived join corrections calculated only from cached loudness profiles.
/// Authored envelopes remain intact; repeated reordering cannot accumulate gains.
/// </summary>
public static class TransitionDesigner
{
    public static IReadOnlyCollection<AudioTrack> Update(IReadOnlyList<AudioTrack> tracks, ProcessingSettings settings,
        IReadOnlyList<AudioTrack>? previousOrder = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(settings);
        LevelingDetector.ValidateSensitivity(settings.BassSensitivity);
        double seconds = double.IsFinite(settings.TransitionSeconds) ? Math.Clamp(settings.TransitionSeconds, 2, 30) : 8;
        // Design the full-strength authored+edge envelope once. The main
        // Strength control blends both layers together during rendering.
        const double strength = 1;
        double bound = double.IsFinite(settings.MaxLevelingDb) ? Math.Clamp(settings.MaxLevelingDb, 0, 2) : 2;
        var starts = new double[tracks.Count];
        var ends = new double[tracks.Count];
        var oldNext = new Dictionary<AudioTrack, AudioTrack>();
        if (previousOrder != null)
        {
            for (int i = 0; i + 1 < previousOrder.Count; i++) oldNext[previousOrder[i]] = previousOrder[i + 1];
            for (int i = 0; i < tracks.Count; i++)
            {
                starts[i] = GainAt(tracks[i], 0);
                ends[i] = GainAt(tracks[i], tracks[i].Duration);
            }
            if (tracks.Count > 0) { starts[0] = 0; ends[^1] = 0; }
        }
        for (int i = 0; i + 1 < tracks.Count; i++)
        {
            // Structural edits update only a genuinely new join. An unrelated
            // manual edit must not cause an unchanged join to be regenerated.
            if (previousOrder != null && oldNext.TryGetValue(tracks[i], out var oldRight) && ReferenceEquals(oldRight, tracks[i + 1])) continue;
            ends[i] = 0;
            starts[i + 1] = 0;
            if (!TryEdgeDeviation(tracks[i], false, seconds, strength, settings, out double left, out double leftConfidence) ||
                !TryEdgeDeviation(tracks[i + 1], true, seconds, strength, settings, out double right, out double rightConfidence)) continue;
            double difference = right - left;
            // One LU of natural variation is left alone. Each side can receive
            // at most two dB at full Strength, tapering to zero inside its edge.
            double correction = Math.Sign(difference) * Math.Max(0, Math.Abs(difference) - 1) * .5;
            correction = Math.Clamp(correction, -bound, bound);
            double confidence = correction >= 0 ? leftConfidence : rightConfidence;
            double positiveLimit = Math.Min(bound, 1.5 + Math.Max(0, bound - 1.5) * confidence);
            correction = Math.Clamp(correction, -positiveLimit, positiveLimit);
            ends[i] = correction;
            starts[i + 1] = -correction;
        }
        var changed = new List<AudioTrack>();
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var curve = EdgeCurve(track.Duration, seconds, starts[i], ends[i]);
            if (track.TransitionEnvelope.SequenceEqual(curve)) continue;
            track.TransitionEnvelope = curve;
            changed.Add(track);
        }
        return changed;
    }

    /// <summary>Automatic edge correction only, before applying Leveling Strength.</summary>
    public static double GainAt(AudioTrack track, double time)
        => AudioProcessor.EnvelopeGainAt(track.TransitionEnvelope, time);

    /// <summary>Authored plus derived edge curve, at full Strength, for display/edit conversion.</summary>
    public static List<EnvelopePoint> CombinedEnvelope(AudioTrack track)
        => Combine(track.Envelope, track.TransitionEnvelope);

    /// <summary>Exact piecewise-linear dB sum; zero layers introduce no redundant knots.</summary>
    public static List<EnvelopePoint> Combine(IReadOnlyList<EnvelopePoint> authored, IReadOnlyList<EnvelopePoint> edges)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(edges);
        static List<EnvelopePoint> Normalize(IReadOnlyList<EnvelopePoint> source)
        {
            var points = EnvelopeMath.Canonicalize(source);
            return points.All(p => p.GainDb == 0) ? [] : points;
        }
        var a = Normalize(authored);
        var b = Normalize(edges);
        if (b.Count == 0) return a;
        if (a.Count == 0) return b;
        return a.Select(p => p.Time).Concat(b.Select(p => p.Time)).Distinct().Order()
            .Select(time => new EnvelopePoint(time, AudioProcessor.EnvelopeGainAt(a, time) + AudioProcessor.EnvelopeGainAt(b, time))).ToList();
    }

    private static List<EnvelopePoint> EdgeCurve(double duration, double seconds, double startGain, double endGain)
    {
        if (duration <= 0 || startGain == 0 && endGain == 0) return [];
        double edge = Math.Min(seconds, duration * .5);
        if (edge <= 0) return [];
        return new List<EnvelopePoint>
        {
            new(0, startGain), new(edge, 0), new(duration - edge, 0), new(duration, endGain)
        }.GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
    }

    private static bool TryEdgeDeviation(AudioTrack track, bool start, double seconds, double strength, ProcessingSettings settings,
        out double deviation, out double confidence)
    {
        deviation = 0;
        confidence = 1;
        var analysis = track.Analysis;
        if (analysis == null || analysis.IntegratedLufs <= -65 || track.Duration < 3 || analysis.ShortTerm.Count < 3) return false;
        var detector = LevelingDetector.GetProfile(analysis, settings);
        var source = detector.ShortTerm;
        double width = Math.Min(seconds, track.Duration * .5);
        double lo = start ? 0 : track.Duration - width;
        double hi = start ? width : track.Duration;
        double offset = detector.ReferenceLoudness - analysis.IntegratedLufs;
        double fullBandGate = Math.Max(-55, analysis.IntegratedLufs - 20);
        double gate = fullBandGate + offset;
        var edge = source.Where(p => p.Time >= lo && p.Time <= hi && double.IsFinite(p.Loudness)).OrderBy(p => p.Time).ToArray();
        var fullBandEdge = analysis.ShortTerm.Where(p => p.Time >= lo && p.Time <= hi && double.IsFinite(p.Loudness)).OrderBy(p => p.Time).ToArray();
        if (edge.Length < 2 || edge.Any(p => p.Loudness < gate)) return false;
        if (fullBandEdge.Length < 2 || fullBandEdge.Any(p => p.Loudness < fullBandGate)) return false;
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length == 0 ? -90 : LoudnessAnalyzer.Percentile(sorted, .5);
        }
        int third = Math.Max(1, edge.Length / 3);
        double outer = Median((start ? fullBandEdge.Take(third) : fullBandEdge.TakeLast(third)).Select(p => p.Loudness));
        double inner = Median((start ? fullBandEdge.TakeLast(third) : fullBandEdge.Take(third)).Select(p => p.Loudness));
        // Preserve intentional fades instead of lifting their tails/noise floors.
        if (outer < inner - 3) return false;
        double originalProfile = ProfileIntegrated(source, _ => 0, -70 + offset);
        double leveledProfile = ProfileIntegrated(source,
            time => AudioProcessor.EnvelopeGainAt(track.Envelope, time) * strength, -70 + offset);
        if (!double.IsFinite(originalProfile) || !double.IsFinite(leveledProfile)) return false;
        double baseline = detector.ReferenceLoudness + leveledProfile - originalProfile;
        double edgeLevel = Median(edge.Select(p => p.Loudness + AudioProcessor.EnvelopeGainAt(track.Envelope, p.Time) * strength));
        // Compare deviations from each track's own intended baseline. Constant
        // input gain, matching blend, user trim and deliberate target differences
        // are preserved, rather than being undone at the join.
        deviation = edgeLevel - baseline;
        if (detector.UsesReducedBass)
            confidence = Median(source.Select((frame, index) => (frame, index)).Where(item => item.frame.Time >= lo && item.frame.Time <= hi)
                .Select(item => item.index < detector.BassConfidence.Count ? detector.BassConfidence[item.index] : 1));
        return double.IsFinite(deviation);
    }

    private static double ProfileIntegrated(IReadOnlyList<LoudnessFrame> profile, Func<double, double> gain, double absoluteGate)
    {
        var energies = profile.Where(p => double.IsFinite(p.Loudness)).Select(p => p.Loudness + gain(p.Time))
            .Where(level => level >= absoluteGate).Select(level => Math.Pow(10, (level + .691) / 10)).ToArray();
        if (energies.Length == 0) return double.NaN;
        double relative = LoudnessAnalyzer.EnergyToLufs(energies.Average()) - 10;
        var gated = energies.Where(energy => LoudnessAnalyzer.EnergyToLufs(energy) >= relative).ToArray();
        return gated.Length == 0 ? double.NaN : LoudnessAnalyzer.EnergyToLufs(gated.Average());
    }
}

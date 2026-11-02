namespace Multiloud.Core;

/// <summary>A sharp level change supported by sustained, active musical sections.</summary>
public sealed record LevelingSectionBoundary(double Time, double BeforeLoudness, double AfterLoudness)
{
    public double BeforeConfidence { get; init; } = 1;
    public double AfterConfidence { get; init; } = 1;
}

public static partial class EnvelopeDesigner
{
    /// <summary>
    /// Locates section changes from cached 100 ms energies. Hits, short breaks,
    /// slow fades and bass-only spectral changes do not qualify for faster ramps.
    /// Legacy analyses without detail retain their existing slow suggestions.
    /// </summary>
    public static IReadOnlyList<LevelingSectionBoundary> DetectAbruptSections(AnalysisResult analysis,
        double duration, ProcessingSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        var profile = LevelingDetector.GetProfile(analysis, settings, ct);
        var detail = profile.Detail;
        if (!double.IsFinite(duration) || duration <= 0 || detail.Count < 90 || analysis.IntegratedLufs <= -69)
            return Array.Empty<LevelingSectionBoundary>();
        double hop = detail[0].Duration;
        if (!double.IsFinite(hop) || hop <= 0) return Array.Empty<LevelingSectionBoundary>();
        int context = Math.Max(8, (int)Math.Ceiling(4 / hop));
        int gap = Math.Max(1, (int)Math.Ceiling(.4 / hop));
        int near = Math.Max(1, (int)Math.Round(.3 / hop));
        if (detail.Count < 2 * (context + gap) + 1) return Array.Empty<LevelingSectionBoundary>();
        double fullGate = Math.Max(-55, analysis.IntegratedLufs - 25);
        double detectorGate = fullGate + profile.ReferenceLoudness - analysis.IntegratedLufs;
        var levels = new double[detail.Count];
        static double Median3(double a, double b, double c) => a + b + c - Math.Min(a, Math.Min(b, c)) - Math.Max(a, Math.Max(b, c));
        for (int i = 0; i < detail.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            levels[i] = Median3(detail[Math.Max(0, i - 1)].Loudness, detail[i].Loudness, detail[Math.Min(detail.Count - 1, i + 1)].Loudness);
        }
        var before = new double[context];
        var after = new double[context];
        var highBefore = new double[context];
        var highAfter = new double[context];
        var candidates = new List<(LevelingSectionBoundary Boundary, double Score)>();
        double Median(double[] values) => LoudnessAnalyzer.Percentile(values, .5);
        for (int i = context + gap; i + context + gap <= detail.Count; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            // Cheap bounded prefilter: only a sizeable local change reaches the
            // more expensive sustained-context statistics below.
            double nearBefore = Median3(levels[i - near], levels[i - near + near / 2], levels[i - 1]);
            double nearAfter = Median3(levels[i], levels[i + Math.Max(1, near / 2)], levels[i + near - 1]);
            double local = nearAfter - nearBefore;
            if (Math.Abs(local) < 2.5) continue;
            int activeBefore = 0, activeAfter = 0;
            double confidenceBefore = 0, confidenceAfter = 0;
            for (int j = 0; j < context; j++)
            {
                int left = i - gap - context + j, right = i + gap + j;
                before[j] = levels[left]; after[j] = levels[right];
                highBefore[j] = detail[left].HighPassedLoudness; highAfter[j] = detail[right].HighPassedLoudness;
                if (detail[left].FullBandLoudness >= fullGate && levels[left] >= detectorGate) activeBefore++;
                if (detail[right].FullBandLoudness >= fullGate && levels[right] >= detectorGate) activeAfter++;
                confidenceBefore += detail[left].BassConfidence; confidenceAfter += detail[right].BassConfidence;
            }
            if (activeBefore < context * .9 || activeAfter < context * .9) continue;
            confidenceBefore /= context; confidenceAfter /= context;
            // A bass-only gain/spectral event remains eligible for the normal
            // slow leveling curve, but never acquires a rapid gain correction.
            if (confidenceBefore < .45 || confidenceAfter < .45) continue;
            Array.Sort(before); Array.Sort(after);
            double leftLevel = Median(before), rightLevel = Median(after);
            double contrast = rightLevel - leftLevel, magnitude = Math.Abs(contrast);
            if (magnitude < 3.5 || Math.Sign(local) != Math.Sign(contrast) || Math.Abs(local) < magnitude * .7) continue;
            double spread = Math.Max(2.5, magnitude * .4);
            if (LoudnessAnalyzer.Percentile(before, .9) - LoudnessAnalyzer.Percentile(before, .1) > spread ||
                LoudnessAnalyzer.Percentile(after, .9) - LoudnessAnalyzer.Percentile(after, .1) > spread ||
                before.Count(value => Math.Abs(value - leftLevel) <= 2.5) < context * .8 ||
                after.Count(value => Math.Abs(value - rightLevel) <= 2.5) < context * .8) continue;
            Array.Sort(highBefore); Array.Sort(highAfter);
            double highContrast = Median(highAfter) - Median(highBefore);
            if (Math.Sign(highContrast) != Math.Sign(contrast) || Math.Abs(highContrast) < Math.Min(2.5, magnitude * .6)) continue;
            double time = detail[i].Time;
            if (time <= 0 || time >= duration) continue;
            var boundary = new LevelingSectionBoundary(time, leftLevel, rightLevel)
            {
                BeforeConfidence = confidenceBefore, AfterConfidence = confidenceAfter
            };
            // The largest adjacent-bin change localizes a true step to its
            // containing 100 ms boundary instead of a smeared three-second edge.
            double score = Math.Abs(detail[i].Loudness - detail[i - 1].Loudness) + Math.Abs(local) * .01;
            candidates.Add((boundary, score));
        }
        var result = new List<LevelingSectionBoundary>();
        for (int i = 0; i < candidates.Count;)
        {
            int end = i + 1;
            while (end < candidates.Count && candidates[end].Boundary.Time - candidates[end - 1].Boundary.Time <= .5) end++;
            var best = candidates.Skip(i).Take(end - i).OrderByDescending(candidate => candidate.Score).First().Boundary;
            if (result.Count == 0 || best.Time - result[^1].Time >= 6) result.Add(best);
            i = end;
        }
        return result.AsReadOnly();
    }

    private static List<EnvelopePoint> RefineAbruptSections(AnalysisResult analysis, double duration,
        ProcessingSettings settings, List<EnvelopePoint> slow)
    {
        var sections = DetectAbruptSections(analysis, duration, settings);
        if (sections.Count == 0) return slow;
        var profile = LevelingDetector.GetProfile(analysis, settings);
        double bound = Math.Clamp(settings.MaxLevelingDb, 0, 18);
        double normalSeconds = Math.Clamp(settings.TransitionSeconds, 2, 30);
        double normalSlope = Math.Max(.2, 2 * Math.Max(1, bound) / normalSeconds);
        double fastSlope = Math.Max(6, normalSlope);
        double reductionAmount = (1 - settings.BassSensitivity) / .75;
        double Gain(double level, double confidence)
        {
            double difference = profile.ReferenceLoudness - level;
            double gain = Math.Clamp(Math.Sign(difference) * Math.Max(0, Math.Abs(difference) - 1), -bound, bound);
            if (gain > 0 && profile.UsesReducedBass)
            {
                double blended = 1 - reductionAmount * (1 - confidence);
                gain = Math.Min(gain, Math.Min(bound, 1.5 + Math.Max(0, bound - 1.5) * blended));
            }
            return gain;
        }
        var result = slow;
        var fastIntervals = new List<(double Start, double End)>();
        for (int index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            double beforeGain = Gain(section.BeforeLoudness, section.BeforeConfidence);
            double afterGain = Gain(section.AfterLoudness, section.AfterConfidence);
            double change = afterGain - beforeGain;
            if (Math.Abs(change) < 1) continue;
            double seconds = Math.Min(normalSeconds, Math.Max(.6, Math.Abs(change) / fastSlope));
            // Attenuate in anticipation of a loud section. For a quiet section,
            // put most recovery after the boundary to avoid lifting its loud lead-in.
            double lead = change < 0 ? .8 : .15;
            double rampStart = section.Time - seconds * lead;
            double rampEnd = section.Time + seconds * (1 - lead);
            double extent = normalSeconds * .5 + 4;
            double left = Math.Max(index == 0 ? 0 : (sections[index - 1].Time + section.Time) * .5, section.Time - extent);
            double right = Math.Min(index + 1 == sections.Count ? duration : (sections[index + 1].Time + section.Time) * .5, section.Time + extent);
            if (rampStart <= left + .05 || rampEnd >= right - .05) continue;
            double leftGain = AudioProcessor.EnvelopeGainAt(slow, left);
            double rightGain = AudioProcessor.EnvelopeGainAt(slow, right);
            // Any bridge to the untouched curve retains the normal slow slew.
            beforeGain = Math.Clamp(beforeGain, leftGain - normalSlope * (rampStart - left), leftGain + normalSlope * (rampStart - left));
            afterGain = Math.Clamp(afterGain, rightGain - normalSlope * (right - rampEnd), rightGain + normalSlope * (right - rampEnd));
            // Bridge constraints can move the targets toward one another; if
            // they ever increase the ramp's required slew, retain the slow curve.
            if (Math.Abs(afterGain - beforeGain) > fastSlope * (rampEnd - rampStart) + 1e-9) continue;
            var replacement = result.Where(point => point.Time < left || point.Time > right).ToList();
            replacement.Add(new(left, leftGain)); replacement.Add(new(rampStart, beforeGain));
            replacement.Add(new(rampEnd, afterGain)); replacement.Add(new(right, rightGain));
            result = replacement.GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
            fastIntervals.Add((rampStart, rampEnd));
        }
        return fastIntervals.Count == 0 ? slow : ProtectRefinedCurve(analysis, profile, duration, bound,
            normalSlope, fastSlope, fastIntervals, result);
    }

    private static bool AllowsPositiveGain(IReadOnlyList<LoudnessFrame> source,
        IReadOnlyList<LoudnessFrame> fullBand, int index, double detectorGate, double fullBandGate)
    {
        if (source[index].Loudness < detectorGate || fullBand[index].Loudness < fullBandGate) return false;
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Where(double.IsFinite).Order().ToArray();
            return sorted.Length == 0 ? -90 : LoudnessAnalyzer.Percentile(sorted, .5);
        }
        if (index < 8)
        {
            double later = Median(fullBand.Skip(Math.Min(source.Count - 1, index + 2)).Take(4).Select(frame => frame.Loudness));
            if (later - fullBand[index].Loudness > 4) return false;
        }
        if (index >= source.Count - 8)
        {
            double earlier = Median(fullBand.Skip(Math.Max(0, index - 5)).Take(Math.Min(4, index + 1)).Select(frame => frame.Loudness));
            if (earlier - fullBand[index].Loudness > 4) return false;
        }
        return true;
    }

    private static List<EnvelopePoint> ProtectRefinedCurve(AnalysisResult analysis, LevelingProfile profile,
        double duration, double bound, double normalSlope, double fastSlope,
        IReadOnlyList<(double Start, double End)> fastIntervals, List<EnvelopePoint> refined)
    {
        var source = profile.ShortTerm.OrderBy(frame => frame.Time).ToArray();
        var fullBand = analysis.ShortTerm.OrderBy(frame => frame.Time).ToArray();
        double fullGate = Math.Max(-55, analysis.IntegratedLufs - 25);
        double gate = fullGate + profile.ReferenceLoudness - analysis.IntegratedLufs;
        var points = refined.ToList();
        for (int i = 0; i < source.Length; i++)
        {
            double time = source[i].Time;
            if (time < 0 || time > duration) continue;
            double cap = AllowsPositiveGain(source, fullBand, i, gate, fullGate) ? bound : 0;
            if (cap > 0 && profile.UsesReducedBass)
            {
                double confidence = i < profile.BassConfidence.Count ? profile.BassConfidence[i] : 1;
                cap = Math.Min(cap, 1.5 + Math.Max(0, bound - 1.5) * confidence);
            }
            double gain = AudioProcessor.EnvelopeGainAt(refined, time);
            if (gain > cap) points.Add(new EnvelopePoint(time, cap));
        }
        points = points.GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
        double AllowedChange(double start, double end)
        {
            double allowed = normalSlope * (end - start);
            foreach (var interval in fastIntervals)
                allowed += (fastSlope - normalSlope) * Math.Max(0, Math.Min(end, interval.End) - Math.Max(start, interval.Start));
            return allowed;
        }
        // Reapplying protection can introduce a new corner close to a fast
        // correction. Reduce neighboring gains to meet the appropriate slew;
        // protected silent/fading points are never raised to smooth the curve.
        for (int i = 1; i < points.Count; i++)
            points[i] = points[i] with { GainDb = Math.Min(points[i].GainDb,
                points[i - 1].GainDb + AllowedChange(points[i - 1].Time, points[i].Time)) };
        for (int i = points.Count - 2; i >= 0; i--)
            points[i] = points[i] with { GainDb = Math.Min(points[i].GainDb,
                points[i + 1].GainDb + AllowedChange(points[i].Time, points[i + 1].Time)) };
        return points;
    }
}

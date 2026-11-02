namespace Multiloud.Core;

public static partial class AudioProcessor
{
    /// <summary>
    /// Reuses immutable pre-match stages. Envelope edits update only changed
    /// segments and their filter/FIR dependencies; globally coupled gain and
    /// limiting are always regenerated, and final PCM is measured again.
    /// </summary>
    public static RenderResult RenderIncremental(float[] samples, int sampleRate, int channels,
        IReadOnlyList<EnvelopePoint> envelope, double trimDb, ProcessingSettings settings, RenderResult? previous,
        CancellationToken ct = default, double inputGainDb = 0, IReadOnlyList<EnvelopePoint>? transitionEnvelope = null)
    {
        ct.ThrowIfCancellationRequested(); LoudnessAnalyzer.Validate(samples, sampleRate, channels);
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(settings);
        LevelingDetector.ValidateSensitivity(settings.BassSensitivity);
        if (!double.IsFinite(trimDb) || !double.IsFinite(inputGainDb) || !double.IsFinite(settings.TargetLufs)
            || !double.IsFinite(settings.Strength) || !double.IsFinite(settings.MatchingStrength) || !double.IsFinite(settings.CeilingDb)
            || !double.IsFinite(settings.MaximizerCharacter) || !double.IsFinite(settings.TransientPreservation)
            || !double.IsFinite(settings.BassProtection) || !double.IsFinite(settings.MaxSustainedReductionDb))
            throw new ArgumentException("Processing controls must be finite numbers.");
        if (!Enum.IsDefined(settings.MaximizerStyle)) throw new ArgumentException("Unsupported maximizer style.", nameof(settings));
        double character = Math.Clamp(settings.MaximizerCharacter, 0, 1);
        double transientPreservation = Math.Clamp(settings.TransientPreservation, 0, 1);
        double bassProtection = Math.Clamp(settings.BassProtection, 0, 1);
        double sustainedLimit = Math.Clamp(settings.MaxSustainedReductionDb, 0, 12);
        double ceilingDb = Math.Clamp(settings.CeilingDb, -12, 0);
        int frames = samples.Length / channels;
        if (frames == 0) return new RenderResult
        {
            Samples = [], OutputLufs = -70, TruePeakDb = -120, Warning = "This file contains no audio frames.",
            RenderedLimiterEnabled = settings.LimiterEnabled, RenderedCeilingDb = ceilingDb,
            RenderedMaximizerStyle = settings.MaximizerStyle, RenderedMaximizerCharacter = character,
            RenderedTransientPreservation = transientPreservation,
            RenderedBassProtection = bassProtection, RenderedDynamicsGuardEnabled = settings.DynamicsGuardEnabled,
            RenderedMaxSustainedReductionDb = sustainedLimit
        };
        double strength = settings.LevelingEnabled ? Math.Clamp(settings.Strength, 0, 1) : 0;
        double matchingStrength = settings.MatchingEnabled ? Math.Clamp(settings.MatchingStrength, 0, 1) : 0;
        bool matching = matchingStrength > 0;
        inputGainDb = Math.Clamp(inputGainDb, -30, 30); trimDb = Math.Clamp(trimDb, -36, 36);
        var points = EnvelopeMath.Canonicalize(TransitionDesigner.Combine(envelope, transitionEnvelope ?? [])).ToArray();
        var old = RenderStageRetention.Get(previous);
        bool reuse = previous?.HasAudibleBackground != true && old != null && ReferenceEquals(old.Source, samples) && old.SampleRate == sampleRate && old.Channels == channels
            && previous!.Samples.Length == samples.Length;
        int first = 0, end = frames;
        if (reuse)
            (first, end) = ChangedEnvelopeFrames(previous!.RenderedEnvelope, points, previous.RenderedLevelingStrength,
                strength, previous.RenderedInputGainDb, inputGainDb, sampleRate, frames);
        if (reuse && first >= end && previous!.RenderedMatchingStrength == matchingStrength
            && (!matching || previous.RenderedTargetLufs == settings.TargetLufs) && previous.RenderedTrimDb == trimDb
            && old!.LimiterEnabled == settings.LimiterEnabled && (!settings.LimiterEnabled || old.CeilingDb == ceilingDb)
            && (!settings.LimiterEnabled || previous.RenderedMaximizerStyle == settings.MaximizerStyle &&
                previous.RenderedMaximizerCharacter == character && previous.RenderedTransientPreservation == transientPreservation &&
                previous.RenderedBassProtection == bassProtection && previous.RenderedDynamicsGuardEnabled == settings.DynamicsGuardEnabled &&
                (!settings.DynamicsGuardEnabled || previous.RenderedMaxSustainedReductionDb == sustainedLimit)))
        {
            // Adding/removing a point on the same line, or changing a bypassed
            // control, does not rerun the limiter's target-learning trial passes.
            var unchanged = new RenderResult
            {
                Samples = previous.Samples, MatchGainDb = previous.MatchGainDb, OutputLufs = previous.OutputLufs,
                TruePeakDb = previous.TruePeakDb, MaxReductionDb = previous.MaxReductionDb, Warning = previous.Warning,
                AverageReductionDb = previous.AverageReductionDb, LimitedPercent = previous.LimitedPercent,
                HeavyLimitingPercent = previous.HeavyLimitingPercent, SafetyAttenuationDb = previous.SafetyAttenuationDb,
                ReductionTrace = previous.ReductionTrace, DynamicsGuardReductionDb = previous.DynamicsGuardReductionDb,
                SustainedReductionDb = previous.SustainedReductionDb,
                PreMatchLufs = previous.PreMatchLufs, RenderedInputGainDb = inputGainDb, RenderedLevelingStrength = strength,
                RenderedMatchingStrength = matchingStrength, RenderedTargetLufs = settings.TargetLufs, RenderedTrimDb = trimDb,
                RenderedLimiterEnabled = settings.LimiterEnabled, RenderedCeilingDb = ceilingDb,
                RenderedMaximizerStyle = settings.MaximizerStyle, RenderedMaximizerCharacter = character,
                RenderedTransientPreservation = transientPreservation,
                RenderedBassProtection = bassProtection, RenderedDynamicsGuardEnabled = settings.DynamicsGuardEnabled,
                RenderedMaxSustainedReductionDb = sustainedLimit,
                RenderedEnvelope = Array.AsReadOnly(points), Work = new(true, 0, 0, 0, 0, 0, 0)
            };
            ct.ThrowIfCancellationRequested(); RenderStageRetention.Attach(unchanged, old); return unchanged;
        }
        float[] leveled = reuse ? first < end ? (float[])old!.Leveled.Clone() : old!.Leveled : new float[samples.Length];
        if (!reuse || first < end) ApplyLeveling(samples, leveled, sampleRate, channels, points, strength, inputGainDb, first, end, ct);
        var meter = reuse ? first < end ? LoudnessCache.Update(old!.PreMatchMeter, leveled, first, end, ct) : old!.PreMatchMeter
            : LoudnessCache.Create(leveled, sampleRate, channels, ct);
        int meterWork = reuse && first >= end ? 0 : meter.RecomputedFrames;
        // The peak detector is only a limiter dependency. A bypassed module
        // neither scans/allocates a detector nor silently protects its ceiling.
        // Re-enabling protection needs a fresh detector if the old stage was off.
        bool reuseDetector = reuse && old!.LimiterEnabled && old.Detector.Length == frames;
        double[] detector = !settings.LimiterEnabled ? [] : reuseDetector
            ? first < end ? (double[])old!.Detector.Clone() : old!.Detector : new double[frames];
        int detectorFirst = !settings.LimiterEnabled ? 0 : reuseDetector ? Math.Max(0, first - 12) : 0;
        int detectorEnd = !settings.LimiterEnabled ? 0 : reuseDetector ? Math.Min(frames, end + 12) : frames;
        if (reuseDetector && first >= end) detectorFirst = detectorEnd = 0;
        for (int f = detectorFirst; f < detectorEnd; f++)
        {
            if ((f & 8191) == 0) ct.ThrowIfCancellationRequested();
            detector[f] = TruePeakMeter.FramePeak(leveled, channels, f);
        }
        double leveledLufs = meter.IntegratedLufs;
        bool measurable = leveledLufs > -69;
        double matchGain = matching && measurable ? Math.Clamp(settings.TargetLufs - leveledLufs, -36, 36) * matchingStrength : 0;
        double desiredLufs = matching ? leveledLufs + (settings.TargetLufs - leveledLufs) * matchingStrength + trimDb : leveledLufs + trimDb;
        double ceiling = Math.Pow(10, ceilingDb / 20);
        var output = new float[samples.Length];
        // One linked gain array is reused by all target-learning passes; there
        // are no full-file oversampled PCM or per-channel limiter copies.
        var limiterGains = settings.LimiterEnabled ? new float[frames] : null;
        LoudnessCache? outputMeter = null;
        double outputLufs = -70;
        int dirtyFirst = 0, dirtyEnd = frames;
        int passes = matching && settings.LimiterEnabled && measurable ? 3 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            double constantGain = Math.Pow(10, (matchGain + trimDb) / 20);
            for (int i = 0; i < output.Length; i++)
            {
                if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
                output[i] = (float)(leveled[i] * constantGain);
            }
            if (limiterGains != null)
                AdaptiveMaximizer.Apply(output, detector, limiterGains, constantGain, sampleRate, channels, ceiling,
                    settings.MaximizerStyle, character, transientPreservation, ct, bassProtection);
            (dirtyFirst, dirtyEnd) = reuse ? ChangedPcmFrames(previous!.Samples, output, channels, ct) : (0, frames);
            outputMeter = reuse && dirtyFirst < dirtyEnd
                ? LoudnessCache.Update(old!.OutputMeter, output, dirtyFirst, dirtyEnd, ct)
                : reuse ? old!.OutputMeter : LoudnessCache.Create(output, sampleRate, channels, ct);
            meterWork += reuse && dirtyFirst >= dirtyEnd ? 0 : outputMeter.RecomputedFrames;
            outputLufs = outputMeter.IntegratedLufs;
            double error = desiredLufs - outputLufs;
            if (pass + 1 >= passes || Math.Abs(error) <= .12 || error > 6 || !measurable) break;
            matchGain = Math.Clamp(matchGain + Math.Clamp(error, -2, 2), -36 * matchingStrength, 36 * matchingStrength);
        }
        var outputPeaks = reuse ? PeakBlockCache.Update(old!.OutputPeaks, output, sampleRate, channels, dirtyFirst, dirtyEnd, ct)
            : PeakBlockCache.Create(output, sampleRate, channels, ct);
        int peakWork = outputPeaks.RecomputedFrames;
        double peak = outputPeaks.Peak, safetyDb = 0;
        double guardBackoff = 0, sustainedReduction = 0;
        for (int guardPass = 0; ; guardPass++)
        {
            if (limiterGains != null)
            {
                // Gain modulation can create a reconstructed peak not present in
                // the pre-gain detector. Fix bounded problem regions first, measure
                // the resulting PCM again, and retain static trim as a last resort.
                for (int attempt = 0; attempt < 2 && peak > ceiling; attempt++)
                {
                    if (!AdaptiveMaximizer.CorrectReconstructedPeaks(output, limiterGains, outputPeaks, sampleRate,
                        channels, ceiling, ct, out int correctionFirst, out int correctionEnd)) break;
                    outputMeter = LoudnessCache.Update(outputMeter!, output, correctionFirst, correctionEnd, ct);
                    meterWork += outputMeter.RecomputedFrames; outputLufs = outputMeter.IntegratedLufs;
                    outputPeaks = PeakBlockCache.Update(outputPeaks, output, sampleRate, channels, correctionFirst, correctionEnd, ct);
                    peakWork += outputPeaks.RecomputedFrames; peak = outputPeaks.Peak;
                }
            }
            if (settings.LimiterEnabled && peak > ceiling)
            {
                safetyDb = 20 * Math.Log10(ceiling / peak) - .005;
                double safety = Math.Pow(10, safetyDb / 20);
                for (int i = 0; i < output.Length; i++)
                {
                    if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
                    output[i] = (float)(output[i] * safety);
                }
                // Recompute exact post-trim states, not a scaled stale meter cache.
                outputMeter = LoudnessCache.Create(output, sampleRate, channels, ct); meterWork += outputMeter.RecomputedFrames;
                outputLufs = outputMeter.IntegratedLufs;
                outputPeaks = PeakBlockCache.Create(output, sampleRate, channels, ct); peakWork += outputPeaks.RecomputedFrames;
                peak = outputPeaks.Peak;
            }
            sustainedReduction = limiterGains == null ? 0 : PeakDynamicsGuard.MeasureSustainedReductionDb(
                leveled, output, Math.Pow(10, (matchGain + trimDb) / 20), sampleRate, channels, ct);
            if (limiterGains == null || !settings.DynamicsGuardEnabled || sustainedReduction <= sustainedLimit + .025)
                break;

            double nextGain = matchGain - Math.Max(.15, sustainedReduction - sustainedLimit + .15);
            if (guardPass >= 6)
            {
                // The final fallback places the unmodified music below every
                // reconstructed-peak constraint, so no sustained limiting is
                // needed even for an extreme target or a zero-dB limit.
                double maximum = detector.Length == 0 ? 0 : detector.Max();
                if (maximum > 0) nextGain = Math.Min(nextGain, 20 * Math.Log10(ceiling * .98 / maximum) - trimDb);
            }
            guardBackoff += matchGain - nextGain;
            matchGain = nextGain;
            double scalar = Math.Pow(10, (matchGain + trimDb) / 20);
            for (int i = 0; i < output.Length; i++)
            {
                if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
                output[i] = (float)(leveled[i] * scalar);
            }
            AdaptiveMaximizer.Apply(output, detector, limiterGains, scalar, sampleRate, channels, ceiling,
                settings.MaximizerStyle, character, transientPreservation, ct, bassProtection);
            outputMeter = LoudnessCache.Create(output, sampleRate, channels, ct);
            meterWork += outputMeter.RecomputedFrames; outputLufs = outputMeter.IntegratedLufs;
            outputPeaks = PeakBlockCache.Create(output, sampleRate, channels, ct);
            peakWork += outputPeaks.RecomputedFrames; peak = outputPeaks.Peak; safetyDb = 0;
        }
        (dirtyFirst, dirtyEnd) = reuse ? ChangedPcmFrames(previous!.Samples, output, channels, ct) : (0, frames);
        var reduction = AdaptiveMaximizer.MeasureReduction(limiterGains, frames, sampleRate, -safetyDb, ct);
        var warnings = new List<string>();
        if (guardBackoff > .01) warnings.Add($"Dynamics safeguard reduced drive by {guardBackoff:F1} dB to preserve sustained dynamics, preferring a quieter result over heavy limiting.");
        if (!measurable) warnings.Add("No measurable programme loudness; silence was not amplified.");
        if (matching && measurable && Math.Abs(outputLufs - desiredLufs) > .5)
            warnings.Add(settings.LimiterEnabled
                ? $"Output is {outputLufs:F1} LUFS; the requested {desiredLufs:F1} LUFS is constrained by peak headroom or the gain limit."
                : $"Output is {outputLufs:F1} LUFS; the requested {desiredLufs:F1} LUFS is constrained by the gain limit.");
        if (settings.LimiterEnabled && reduction.Maximum > 6) warnings.Add($"Peak reduction {reduction.Maximum:F1} dB; average {reduction.Average:F1} dB, {reduction.HeavyPercent:F1}% above 3 dB.");
        var result = new RenderResult
        {
            Samples = output, MatchGainDb = matchGain, OutputLufs = outputLufs, TruePeakDb = LoudnessAnalyzer.ToDb(peak),
            MaxReductionDb = reduction.Maximum, AverageReductionDb = reduction.Average,
            LimitedPercent = reduction.LimitedPercent, HeavyLimitingPercent = reduction.HeavyPercent,
            SafetyAttenuationDb = -safetyDb, ReductionTrace = reduction.Trace,
            DynamicsGuardReductionDb = guardBackoff, SustainedReductionDb = sustainedReduction,
            Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null,
            PreMatchLufs = leveledLufs, RenderedInputGainDb = inputGainDb, RenderedLevelingStrength = strength,
            RenderedMatchingStrength = matchingStrength, RenderedTargetLufs = settings.TargetLufs, RenderedTrimDb = trimDb,
            RenderedLimiterEnabled = settings.LimiterEnabled, RenderedCeilingDb = ceilingDb,
            RenderedMaximizerStyle = settings.MaximizerStyle, RenderedMaximizerCharacter = character,
            RenderedTransientPreservation = transientPreservation,
            RenderedBassProtection = bassProtection, RenderedDynamicsGuardEnabled = settings.DynamicsGuardEnabled,
            RenderedMaxSustainedReductionDb = sustainedLimit,
            RenderedEnvelope = Array.AsReadOnly(points),
            Work = new(reuse, reuse ? end - first : frames, detectorEnd - detectorFirst, meterWork, peakWork, dirtyFirst, dirtyEnd)
        };
        ct.ThrowIfCancellationRequested();
        RenderStageRetention.Attach(result, new RenderStageState
        {
            Source = samples, SampleRate = sampleRate, Channels = channels, Leveled = leveled, Detector = detector,
            PreMatchMeter = meter, OutputMeter = outputMeter!, OutputPeaks = outputPeaks,
            LimiterEnabled = settings.LimiterEnabled, CeilingDb = ceilingDb
        });
        return result;
    }

    private static (int First, int End) ChangedEnvelopeFrames(IReadOnlyList<EnvelopePoint> before, IReadOnlyList<EnvelopePoint> after,
        double oldStrength, double newStrength, double oldInput, double newInput, int rate, int frames)
    {
        // Input gain is outside the bounded envelope stage. An input/curve
        // cancellation before that clamp need not cancel after it.
        if (oldInput != newInput) return (0, frames);
        double duration = frames / (double)rate;
        var times = before.Select(p => p.Time).Concat(after.Select(p => p.Time)).Append(0).Append(duration)
            .Where(t => t >= 0 && t <= duration).Distinct().Order().ToArray();
        double first = duration, last = 0;
        for (int i = 0; i + 1 < times.Length; i++)
        {
            double a = times[i], b = times[i + 1];
            bool differs = EnvelopeGainAt(before, a) * oldStrength + oldInput != EnvelopeGainAt(after, a) * newStrength + newInput
                || EnvelopeGainAt(before, b) * oldStrength + oldInput != EnvelopeGainAt(after, b) * newStrength + newInput;
            if (differs) { first = Math.Min(first, a); last = Math.Max(last, b); }
        }
        if (first >= last) return (0, 0);
        return (Math.Clamp((int)Math.Floor(first * rate), 0, frames), Math.Clamp((int)Math.Ceiling(last * rate) + 1, 0, frames));
    }

    private static void ApplyLeveling(float[] samples, float[] destination, int rate, int channels,
        EnvelopePoint[] points, double strength, double input, int first, int end, CancellationToken ct)
    {
        int segment = 0;
        for (int f = first; f < end; f++)
        {
            if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
            double time = f / (double)rate;
            while (segment + 1 < points.Length && points[segment + 1].Time <= time) segment++;
            double db = 0;
            if (strength > 0 && points.Length > 0)
            {
                db = points[segment].GainDb;
                if (segment + 1 < points.Length)
                {
                    double dt = points[segment + 1].Time - points[segment].Time;
                    double fraction = dt > 0 ? Math.Clamp((time - points[segment].Time) / dt, 0, 1) : 1;
                    db += fraction * (points[segment + 1].GainDb - db);
                }
                db = Math.Clamp(db * strength, -36, 36);
            }
            double gain = Math.Pow(10, (input + db) / 20);
            for (int c = 0; c < channels; c++)
            {
                float source = samples[f * channels + c];
                if (!float.IsFinite(source)) throw new ArgumentException("Audio contains non-finite samples.", nameof(samples));
                destination[f * channels + c] = (float)(source * gain);
            }
        }
    }

    private static (int First, int End) ChangedPcmFrames(float[] before, float[] after, int channels, CancellationToken ct)
    {
        int first = 0, end = after.Length;
        while (first < end && before[first] == after[first]) { if ((first & 32767) == 0) ct.ThrowIfCancellationRequested(); first++; }
        if (first == end) return (0, 0);
        while (end > first && before[end - 1] == after[end - 1]) { if ((end & 32767) == 0) ct.ThrowIfCancellationRequested(); end--; }
        return (first / channels, (end + channels - 1) / channels);
    }

}

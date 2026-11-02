namespace Multiloud.Core;

public static partial class AudioProcessor
{
    /// <summary>
    /// Levels the music, matches the measured final mix by changing music gain,
    /// and reserves true-peak headroom for an immutable background segment.
    /// Background samples are never limited, trimmed or faded by this method.
    /// </summary>
    public static RenderResult RenderWithBackground(float[] samples, int sampleRate, int channels,
        IReadOnlyList<EnvelopePoint> envelope, double trimDb, ProcessingSettings settings, float[] background,
        CancellationToken ct = default, double inputGainDb = 0,
        IReadOnlyList<EnvelopePoint>? transitionEnvelope = null, RenderResult? previous = null)
    {
        ct.ThrowIfCancellationRequested(); LoudnessAnalyzer.Validate(samples, sampleRate, channels);
        ArgumentNullException.ThrowIfNull(background);
        if (background.Length != samples.Length) throw new ArgumentException("Background and music must have the same sample count and format.", nameof(background));
        bool audibleBed = false;
        for (int i = 0; i < background.Length; i++)
        {
            if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
            if (!float.IsFinite(background[i])) throw new ArgumentException("Background audio contains non-finite samples.", nameof(background));
            audibleBed |= background[i] != 0;
        }
        if (!audibleBed)
        {
            var legacy = RenderIncremental(samples, sampleRate, channels, envelope, trimDb, settings,
                previous?.HasAudibleBackground == true ? null : previous, ct, inputGainDb, transitionEnvelope);
            // An overlapping silent clip is still a complete, valid bed render.
            // Keep the legacy PCM byte-for-byte while publishing readiness and
            // loudness metadata for original-plus-bed playback.
            double originalLufs = LoudnessCache.Create(samples, sampleRate, channels, ct).IntegratedLufs;
            var silentBed = WithSilentBackground(legacy, background, originalLufs);
            var legacyStage = RenderStageRetention.Get(legacy);
            if (legacyStage != null) RenderStageRetention.Attach(silentBed, legacyStage);
            return silentBed;
        }
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(settings);
        LevelingDetector.ValidateSensitivity(settings.BassSensitivity);
        if (!double.IsFinite(trimDb) || !double.IsFinite(inputGainDb) || !double.IsFinite(settings.TargetLufs)
            || !double.IsFinite(settings.Strength) || !double.IsFinite(settings.MatchingStrength) || !double.IsFinite(settings.CeilingDb)
            || !double.IsFinite(settings.MaximizerCharacter) || !double.IsFinite(settings.TransientPreservation)
            || !double.IsFinite(settings.BassProtection) || !double.IsFinite(settings.MaxSustainedReductionDb))
            throw new ArgumentException("Processing controls must be finite numbers.");
        if (!Enum.IsDefined(settings.MaximizerStyle)) throw new ArgumentException("Unsupported maximizer style.", nameof(settings));
        double ceilingDb = Math.Clamp(settings.CeilingDb, -12, 0), ceiling = Math.Pow(10, ceilingDb / 20);
        // The ceiling belongs to Peak protection. Bypassing that module must
        // permit an unmodified mix, even if its music or fixed bed exceeds it.
        var headroom = settings.LimiterEnabled
            ? BackgroundPeakHeadroom.Create(background, sampleRate, channels, ceiling, ct) : null;
        double bedPeak = headroom?.BackgroundPeak ?? 0;
        if (settings.LimiterEnabled && bedPeak > ceiling)
            throw new InvalidOperationException($"The background alone reaches {LoudnessAnalyzer.ToDb(bedPeak):F2} dBTP, above the {ceilingDb:F2} dBTP ceiling. Lower the background gain before previewing or exporting; its samples cannot be limited without changing the fixed bed.");
        int frames = samples.Length / channels;
        double strength = settings.LevelingEnabled ? Math.Clamp(settings.Strength, 0, 1) : 0;
        double matching = settings.MatchingEnabled ? Math.Clamp(settings.MatchingStrength, 0, 1) : 0;
        double character = Math.Clamp(settings.MaximizerCharacter, 0, 1), transient = Math.Clamp(settings.TransientPreservation, 0, 1);
        double bassProtection = Math.Clamp(settings.BassProtection, 0, 1);
        double maximumSustained = Math.Clamp(settings.MaxSustainedReductionDb, 0, 12);
        inputGainDb = Math.Clamp(inputGainDb, -30, 30); trimDb = Math.Clamp(trimDb, -36, 36);
        var points = EnvelopeMath.Canonicalize(TransitionDesigner.Combine(envelope, transitionEnvelope ?? [])).ToArray();
        var old = RenderStageRetention.Get(previous);
        bool reuse = old != null && ReferenceEquals(old.Source, samples) && old.SampleRate == sampleRate && old.Channels == channels
            && previous!.Samples.Length == samples.Length;
        int first = 0, end = frames;
        if (reuse) (first, end) = ChangedEnvelopeFrames(previous!.RenderedEnvelope, points, previous.RenderedLevelingStrength,
            strength, previous.RenderedInputGainDb, inputGainDb, sampleRate, frames);
        float[] leveled = reuse ? first < end ? (float[])old!.Leveled.Clone() : old!.Leveled : new float[samples.Length];
        if (!reuse || first < end) ApplyLeveling(samples, leveled, sampleRate, channels, points, strength, inputGainDb, first, end, ct);
        var preMeter = reuse ? first < end ? LoudnessCache.Update(old!.PreMatchMeter, leveled, first, end, ct) : old!.PreMatchMeter
            : LoudnessCache.Create(leveled, sampleRate, channels, ct);
        long meterWork = reuse && first >= end ? 0 : preMeter.RecomputedFrames;
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
        double musicPeak = settings.LimiterEnabled ? detector.Max() : 0;
        bool measurable = preMeter.IntegratedLufs > -69;
        var output = new float[samples.Length];
        var gains = settings.LimiterEnabled ? new float[frames] : null;
        var warnings = new List<string>();
        // Each compact budget covers the local background reconstruction and
        // its filter support. A rare background peak now constrains only its
        // neighborhood, rather than the complete music track.
        double staticReduction = 0;

        double MeasureMix(float[] source, double gain)
        {
            MixBackground(source, background, output, gain, ct);
            var meter = LoudnessCache.Create(output, sampleRate, channels, ct);
            meterWork += meter.RecomputedFrames;
            return meter.IntegratedLufs;
        }
        bool sameBed = previous != null && ReferenceEquals(previous.BackgroundSamples, background);
        double originalMix = reuse && sameBed && previous!.OriginalMixLufs.HasValue
            ? previous.OriginalMixLufs.Value : MeasureMix(samples, 1);
        double bedLufs;
        if (reuse && sameBed && previous!.BackgroundLufs.HasValue) bedLufs = previous.BackgroundLufs.Value;
        else { bedLufs = LoudnessCache.Create(background, sampleRate, channels, ct).IntegratedLufs; meterWork += frames; }
        double preMix = reuse && first >= end && sameBed && previous!.PreMatchMixLufs.HasValue
            ? previous.PreMatchMixLufs.Value : MeasureMix(leveled, 1);
        double baseline = trimDb == 0 ? preMix : MeasureMix(leveled, Math.Pow(10, trimDb / 20));
        // Strength blends final-mix loudness, rather than accidentally applying
        // the track correction to the fixed bed. Manual trim remains music-only.
        double desired = baseline + (settings.TargetLufs + trimDb - baseline) * matching;

        void BuildMusic(double match)
        {
            double scalar = Math.Pow(10, (match + trimDb) / 20);
            staticReduction = 0;
            for (int i = 0; i < output.Length; i++)
            {
                if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
                output[i] = (float)(leveled[i] * scalar);
                if (!float.IsFinite(output[i])) throw new ArgumentException("Music contains non-finite or excessively large samples.", nameof(samples));
            }
            if (gains != null && musicPeak > 0)
                AdaptiveMaximizer.Apply(output, detector, gains, scalar, sampleRate, channels, ceiling,
                    settings.MaximizerStyle, character, transient, ct, bassProtection,
                    headroom!.Ceilings, headroom.HopFrames);
            else if (gains != null) Array.Fill(gains, 1);
        }
        double Evaluate(double match)
        {
            BuildMusic(match);
            MixBackground(output, background, output, 1, ct);
            var meter = LoudnessCache.Create(output, sampleRate, channels, ct); meterWork += meter.RecomputedFrames;
            return meter.IntegratedLufs;
        }

        double gainBound = 36 * matching, bestGain = 0, bestError = double.PositiveInfinity;
        var candidates = new SortedDictionary<double, double>();
        double TryCandidate(double gain)
        {
            if (candidates.TryGetValue(gain, out double cached)) return cached;
            double value = Evaluate(gain), error = Math.Abs(value - desired);
            if (error < bestError) { bestGain = gain; bestError = error; }
            candidates.Add(gain, value);
            return value;
        }
        if (matching > 0 && measurable)
        {
            double initial = Math.Clamp(desired - baseline, -gainBound, gainBound);
            TryCandidate(initial);
            if (bestError > .08)
            {
                // Fixed-phase cancellation can make mix loudness non-monotonic.
                // Include the unchanged music gain, so opposite-phase material
                // can bracket a feasible target on either side of cancellation.
                TryCandidate(0); TryCandidate(-gainBound); TryCandidate(gainBound);
                // Opposite-phase copies need not have equal levels. Their
                // cancellation minimum can lie between all existing seeds.
                // One unfiltered correlation pass finds the exact positive
                // scalar minimum for proportional sources without a costly
                // grid of full-file LUFS measurements. Other programme material
                // still uses measured LUFS to accept and refine this candidate.
                double energy = 0, cross = 0;
                for (int i = 0; i < leveled.Length; i++)
                {
                    if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
                    double music = leveled[i], bed = background[i];
                    energy += music * music; cross += music * bed;
                }
                if (energy > 0 && cross < 0)
                {
                    double cancellationScalar = -cross / energy;
                    double cancellationDb = 20 * Math.Log10(cancellationScalar) - trimDb;
                    if (double.IsFinite(cancellationDb)) TryCandidate(Math.Clamp(cancellationDb, -gainBound, gainBound));
                }
                var measured = candidates.ToArray();
                int bracket = -1; double nearest = double.PositiveInfinity;
                for (int i = 0; i + 1 < measured.Length; i++)
                {
                    double a = measured[i].Value - desired, b = measured[i + 1].Value - desired;
                    if (a * b > 0) continue;
                    double distance = Math.Abs((measured[i].Key + measured[i + 1].Key) * .5 - initial);
                    if (distance < nearest) { nearest = distance; bracket = i; }
                }
                if (bracket >= 0)
                {
                    double lo = measured[bracket].Key, hi = measured[bracket + 1].Key;
                    double loError = measured[bracket].Value - desired;
                    for (int iteration = 0; iteration < 12 && bestError > .08; iteration++)
                    {
                        double middle = (lo + hi) * .5, error = TryCandidate(middle) - desired;
                        if (Math.Sign(error) == Math.Sign(loError)) { lo = middle; loError = error; }
                        else hi = middle;
                    }
                }
            }
        }
        // No measurable music is ever amplified merely to raise the bed's LUFS.
        // The dynamics guard lowers music drive, not the fixed bed. Measure it
        // after the local safety repair as well as the main limiter response.
        double requestedGain = bestGain, sustainedReduction = 0;
        int peakWork = settings.LimiterEnabled ? frames : 0;
        void BuildProtectedMusic()
        {
            BuildMusic(bestGain);
            if (headroom != null && gains != null)
            {
                var repair = headroom.ProtectMusic(output, gains, sampleRate, channels, ct);
                peakWork = SaturatedAdd(peakWork, repair.PeakFrames);
                staticReduction = repair.SafetyAttenuationDb;
                sustainedReduction = PeakDynamicsGuard.MeasureSustainedReductionDb(leveled, output,
                    Math.Pow(10, (bestGain + trimDb) / 20), sampleRate, channels, ct);
            }
        }
        for (int guardPass = 0; ; guardPass++)
        {
            BuildProtectedMusic();
            if (!settings.LimiterEnabled || !settings.DynamicsGuardEnabled ||
                sustainedReduction <= maximumSustained + .05)
                break;
            if (guardPass >= 6 || bestGain <= -72)
            {
                // A finite positive minimum budget admits a provably idle
                // limiter. Prefer this conservative lower drive over leaving
                // the chosen sustained-reduction limit unmet after convergence.
                double minimum = headroom!.Ceilings.Length == 0 ? 0 : headroom.Ceilings.Min();
                if (minimum > 0 && musicPeak > 0)
                {
                    double idleDrive = 20 * Math.Log10(minimum * .99 / musicPeak) - trimDb;
                    if (idleDrive < bestGain)
                    {
                        bestGain = idleDrive;
                        BuildProtectedMusic();
                    }
                }
                break;
            }
            bestGain = Math.Max(-72, bestGain - Math.Max(.1, sustainedReduction - maximumSustained + .1));
        }
        double dynamicsBackoff = Math.Max(0, requestedGain - bestGain);
        if (dynamicsBackoff > 0)
            warnings.Add($"Dynamics protection reduced music drive by {dynamicsBackoff:F1} dB, preferring a quieter result over sustained limiting.");
        if (settings.LimiterEnabled && settings.DynamicsGuardEnabled && sustainedReduction > maximumSustained + .1)
            warnings.Add("The fixed background leaves too little headroom to meet the sustained-reduction limit. Lower the background gain.");
        MixBackground(output, background, output, 1, ct);
        var outputPeaks = PeakBlockCache.Create(output, sampleRate, channels, ct); peakWork = SaturatedAdd(peakWork, frames);
        if (settings.LimiterEnabled && outputPeaks.Peak > ceiling)
        {
            peakWork = SaturatedAdd(peakWork, headroom!.RepairMixedRoundoff(output,
                background, gains!, sampleRate, channels, ceiling, ct));
            outputPeaks = PeakBlockCache.Create(output, sampleRate, channels, ct); peakWork = SaturatedAdd(peakWork, frames);
            if (outputPeaks.Peak > ceiling)
                throw new InvalidOperationException("The final mix could not be protected locally without changing the fixed background. Lower the background gain.");
            warnings.Add("Music was locally attenuated at a background peak to preserve the output ceiling.");
        }
        var finalMeter = LoudnessCache.Create(output, sampleRate, channels, ct); meterWork += finalMeter.RecomputedFrames;
        // Limiter gains already include post-limiter music-only safety factors.
        // With protection off, no automatic attenuation has been applied.
        var reduction = AdaptiveMaximizer.MeasureReduction(gains, frames, sampleRate, gains == null ? staticReduction : 0, ct);
        if (!measurable) warnings.Add("No measurable music loudness; silent music was not amplified. The background remains fixed.");
        if (matching > 0 && Math.Abs(finalMeter.IntegratedLufs - desired) > .5)
            warnings.Add(settings.LimiterEnabled
                ? $"Final mix is {finalMeter.IntegratedLufs:F1} LUFS; the requested {desired:F1} LUFS was constrained by music peak headroom, dynamics protection or the allowed gain."
                : $"Final mix is {finalMeter.IntegratedLufs:F1} LUFS; the bounded music-only solve could not reach the requested {desired:F1} LUFS with the fixed background and allowed music gain. Lower the background or choose a different target.");
        if (settings.LimiterEnabled && reduction.Maximum > 6)
            warnings.Add($"Music peak reduction {reduction.Maximum:F1} dB; average {reduction.Average:F1} dB, {reduction.HeavyPercent:F1}% above 3 dB. Lowering the background may free peak headroom.");
        var changed = previous != null && previous.Samples.Length == output.Length
            ? ChangedPcmFrames(previous.Samples, output, channels, ct) : (0, frames);
        var result = new RenderResult
        {
            Samples = output, BackgroundSamples = background, HasAudibleBackground = true,
            OriginalMixLufs = originalMix, BackgroundLufs = bedLufs, PreMatchMixLufs = preMix,
            MatchGainDb = bestGain, OutputLufs = finalMeter.IntegratedLufs, TruePeakDb = LoudnessAnalyzer.ToDb(outputPeaks.Peak),
            MaxReductionDb = reduction.Maximum, AverageReductionDb = reduction.Average, LimitedPercent = reduction.LimitedPercent,
            HeavyLimitingPercent = reduction.HeavyPercent, SafetyAttenuationDb = staticReduction, ReductionTrace = reduction.Trace,
            DynamicsGuardReductionDb = dynamicsBackoff, SustainedReductionDb = sustainedReduction,
            Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null,
            PreMatchLufs = preMeter.IntegratedLufs, RenderedInputGainDb = inputGainDb, RenderedLevelingStrength = strength,
            RenderedMatchingStrength = matching, RenderedTargetLufs = settings.TargetLufs, RenderedTrimDb = trimDb,
            RenderedLimiterEnabled = settings.LimiterEnabled, RenderedCeilingDb = ceilingDb,
            RenderedMaximizerStyle = settings.MaximizerStyle, RenderedMaximizerCharacter = character, RenderedTransientPreservation = transient,
            RenderedBassProtection = bassProtection, RenderedDynamicsGuardEnabled = settings.DynamicsGuardEnabled,
            RenderedMaxSustainedReductionDb = maximumSustained,
            RenderedEnvelope = Array.AsReadOnly(points),
            Work = new(reuse, reuse ? end - first : frames, detectorEnd - detectorFirst, (int)Math.Min(int.MaxValue, meterWork), peakWork, changed.Item1, changed.Item2)
        };
        ct.ThrowIfCancellationRequested();
        RenderStageRetention.Attach(result, new RenderStageState
        {
            Source = samples, SampleRate = sampleRate, Channels = channels, Leveled = leveled, Detector = detector,
            PreMatchMeter = preMeter, OutputMeter = finalMeter, OutputPeaks = outputPeaks,
            LimiterEnabled = settings.LimiterEnabled, CeilingDb = ceilingDb
        });
        return result;
    }

    private static void MixBackground(float[] music, float[] background, float[] destination, double gain, CancellationToken ct)
    {
        for (int i = 0; i < music.Length; i++)
        {
            if ((i & 32767) == 0) ct.ThrowIfCancellationRequested();
            float value = (float)(music[i] * gain + background[i]);
            if (!float.IsFinite(value)) throw new ArgumentException("Mixed audio contains non-finite or excessively large samples.");
            destination[i] = value;
        }
    }

    private static int SaturatedAdd(int a, int b) => (int)Math.Min(int.MaxValue, (long)a + b);

    private static RenderResult WithSilentBackground(RenderResult music, float[] background, double originalLufs) => new()
    {
        Samples = music.Samples, BackgroundSamples = background, HasAudibleBackground = false, OriginalMixLufs = originalLufs,
        BackgroundLufs = -70, PreMatchMixLufs = music.PreMatchLufs,
        MatchGainDb = music.MatchGainDb, OutputLufs = music.OutputLufs, TruePeakDb = music.TruePeakDb,
        MaxReductionDb = music.MaxReductionDb, AverageReductionDb = music.AverageReductionDb,
        LimitedPercent = music.LimitedPercent, HeavyLimitingPercent = music.HeavyLimitingPercent,
        SafetyAttenuationDb = music.SafetyAttenuationDb, ReductionTrace = music.ReductionTrace, Warning = music.Warning,
        DynamicsGuardReductionDb = music.DynamicsGuardReductionDb, SustainedReductionDb = music.SustainedReductionDb,
        PreMatchLufs = music.PreMatchLufs, RenderedInputGainDb = music.RenderedInputGainDb,
        RenderedLevelingStrength = music.RenderedLevelingStrength, RenderedMatchingStrength = music.RenderedMatchingStrength,
        RenderedTargetLufs = music.RenderedTargetLufs, RenderedTrimDb = music.RenderedTrimDb,
        RenderedLimiterEnabled = music.RenderedLimiterEnabled, RenderedCeilingDb = music.RenderedCeilingDb,
        RenderedMaximizerStyle = music.RenderedMaximizerStyle, RenderedMaximizerCharacter = music.RenderedMaximizerCharacter,
        RenderedTransientPreservation = music.RenderedTransientPreservation, RenderedEnvelope = music.RenderedEnvelope,
        RenderedBassProtection = music.RenderedBassProtection, RenderedDynamicsGuardEnabled = music.RenderedDynamicsGuardEnabled,
        RenderedMaxSustainedReductionDb = music.RenderedMaxSustainedReductionDb,
        Work = music.Work, Waveform = music.Waveform
    };
}

using Multiloud.Core;

internal static class AdaptiveMaximizerChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Adaptive maximizer is an exact bypass below its ceiling", BelowCeiling),
        ("Maximizer styles and character change bass/drum response while protecting peaks", StylesAndCharacter),
        ("Maximizer applies linked gain to proportional stereo channels", StereoLink),
        ("Gain-reduction statistics match the attenuation applied to PCM", ReductionStatistics),
        ("Reduction history is bounded, ordered and immutable", ReductionHistory),
        ("Reconstructed peak repair changes only the isolated problem region", LocalPeakRepair),
        ("Changing maximizer controls reuses pre-match stages and matches a fresh render", IncrementalControls),
        ("Peak-protection bypass preserves above-ceiling PCM and active gain controls", BypassedSafety),
        ("Maximizer processing honors cancellation and rejects non-finite controls", CancellationAndValidation),
    ];

    private const int Rate = 12000;
    private const int Seconds = 6;
    private static ProcessingSettings Settings => new()
    {
        LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = true,
        CeilingDb = -1, MaximizerStyle = MaximizerStyle.Transparent,
        MaximizerCharacter = .5, TransientPreservation = .5, BassProtection = 0
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:R} ± {tolerance:R}, got {actual:R}");

    /// <summary>Deterministic bass plus short kick/tick attacks; no normalization or EQ.</summary>
    private static float[] Fixture(double scale = 1)
    {
        var samples = new float[Rate * Seconds * 2];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            double time = frame / (double)Rate;
            double sample = .65 * Math.Sin(2 * Math.PI * 55 * time) + .17 * Math.Sin(2 * Math.PI * 110 * time);
            double age = time - .2;
            if (age >= 0)
            {
                age %= .5;
                if (age < .13)
                    sample += 1.25 * Math.Exp(-age * 36) * Math.Sin(2 * Math.PI * 75 * age)
                        + .5 * Math.Exp(-age * 4000)
                        + .3 * Math.Exp(-age * 1500) * Math.Sin(2 * Math.PI * 3300 * age);
            }
            samples[frame * 2] = (float)(sample * scale);
            samples[frame * 2 + 1] = samples[frame * 2] * .37f;
        }
        return samples;
    }

    private static RenderResult Render(float[] source, ProcessingSettings settings)
        => AudioProcessor.Render(source, Rate, 2, [], 0, settings);

    private static void Safe(RenderResult result, double ceilingDb)
    {
        Require(result.Samples.All(float.IsFinite), "Maximizer produced non-finite PCM");
        double ceiling = Math.Pow(10, ceilingDb / 20);
        Require(result.Samples.All(sample => Math.Abs(sample) <= ceiling + 1e-6), "Sample amplitude exceeded the chosen ceiling");
        Require(result.TruePeakDb <= ceilingDb + .02, $"Reported reconstructed peak exceeded ceiling: {result.TruePeakDb:R}");
        var measured = LoudnessAnalyzer.Analyze(result.Samples, Rate, 2);
        Require(measured.TruePeakDb <= ceilingDb + .02, $"Independent reconstructed peak exceeded ceiling: {measured.TruePeakDb:R}");
    }

    private static bool Different(float[] first, float[] second)
    {
        Require(first.Length == second.Length, "Processing changed the source duration");
        for (int index = 0; index < first.Length; index++)
            if (Math.Abs(first[index] - second[index]) > 1e-5) return true;
        return false;
    }

    private static void BelowCeiling()
    {
        var samples = Fixture(.04);
        foreach (var style in new[] { MaximizerStyle.Transparent, MaximizerStyle.Punchy })
        {
            var settings = Settings with { MaximizerStyle = style };
            var active = Render(samples, settings);
            var bypassed = Render(samples, settings with { LimiterEnabled = false });
            Require(active.Samples.AsSpan().SequenceEqual(samples) && active.Samples.AsSpan().SequenceEqual(bypassed.Samples),
                "An idle maximizer altered below-ceiling audio");
            Near(active.MaxReductionDb, 0, 1e-10, "Idle maximum reduction");
            Near(active.AverageReductionDb, 0, 1e-10, "Idle average reduction");
            Near(active.LimitedPercent, 0, 1e-10, "Idle limited percentage");
            Near(active.HeavyLimitingPercent, 0, 1e-10, "Idle heavy-limiting percentage");
            Near(active.SafetyAttenuationDb, 0, 1e-10, "Idle safety attenuation");
            Require(active.ReductionTrace.All(point => point.ReductionDb == 0), "Idle maximizer reported nonzero reduction history");
        }
    }

    private static void StylesAndCharacter()
    {
        var samples = Fixture();
        var transparent = Render(samples, Settings);
        var punchy = Render(samples, Settings with { MaximizerStyle = MaximizerStyle.Punchy });
        Require(Different(transparent.Samples, punchy.Samples), "The two maximizer styles produced the same active response");
        Safe(transparent, -1); Safe(punchy, -1);
        var fast = Render(samples, Settings with { MaximizerCharacter = 0, TransientPreservation = 0 });
        var slow = Render(samples, Settings with { MaximizerCharacter = 1, TransientPreservation = 0 });
        Require(Different(fast.Samples, slow.Samples), "Character did not change an actively limiting response");
        var transients = Render(samples, Settings with { MaximizerCharacter = 0, TransientPreservation = 1 });
        Require(Different(fast.Samples, transients.Samples), "Transient preservation did not change short drum attacks");
        Safe(fast, -1); Safe(slow, -1); Safe(transients, -1);
        Require(transparent.RenderedLimiterEnabled && transparent.RenderedCeilingDb == -1
            && transparent.RenderedMaximizerStyle == MaximizerStyle.Transparent
            && transparent.RenderedMaximizerCharacter == .5 && transparent.RenderedTransientPreservation == .5,
            "Rendered maximizer metadata does not describe the audible preview");
    }

    private static void StereoLink()
    {
        var samples = Fixture();
        foreach (var style in new[] { MaximizerStyle.Transparent, MaximizerStyle.Punchy })
        {
            var result = Render(samples, Settings with { MaximizerStyle = style });
            Require(result.MaxReductionDb > .5, "Stereo fixture did not exercise gain reduction");
            for (int index = 0; index < result.Samples.Length; index += 2)
                Near(result.Samples[index + 1], result.Samples[index] * .37, 2e-7, "Stereo-linked channel ratio");
        }
    }

    private static void ReductionStatistics()
    {
        var samples = Fixture();
        // Applied gain remains meaningful through a signal's zero crossings,
        // but it cannot be recovered from a 0/0 PCM ratio. Give this statistics
        // fixture a tiny DC offset so every frame exposes its actual gain.
        for (int index = 0; index < samples.Length; index += 2)
        {
            samples[index] += .0037f;
            if (Math.Abs(samples[index]) < 1e-8f) samples[index] = 1e-8f;
            samples[index + 1] = samples[index] * .37f;
        }
        var result = Render(samples, Settings);
        double maximum = 0, total = 0;
        int limited = 0, heavy = 0, frames = samples.Length / 2;
        for (int frame = 0; frame < frames; frame++)
        {
            double input = Math.Abs(samples[frame * 2]);
            double output = Math.Abs(result.Samples[frame * 2]);
            double reduction = input > 1e-9 && output > 0 ? Math.Max(0, 20 * Math.Log10(input / output)) : 0;
            maximum = Math.Max(maximum, reduction); total += reduction;
            if (reduction > .1) limited++;
            if (reduction > 3) heavy++;
        }
        Near(result.MaxReductionDb, maximum, .002, "Maximum actual reduction");
        Near(result.AverageReductionDb, total / frames, .002, "Average actual reduction");
        Near(result.LimitedPercent, limited * 100.0 / frames, .03, "Percentage with more than 0.1 dB reduction");
        Near(result.HeavyLimitingPercent, heavy * 100.0 / frames, .03, "Percentage with more than 3 dB reduction");
        Require(result.AverageReductionDb <= result.MaxReductionDb + 1e-9
            && result.HeavyLimitingPercent <= result.LimitedPercent && result.LimitedPercent <= 100,
            "Gain-reduction statistics have inconsistent bounds");
    }

    private static void ReductionHistory()
    {
        var samples = Fixture();
        var result = Render(samples, Settings);
        var snapshot = result.ReductionTrace.ToArray();
        Require(snapshot.Length >= Seconds * 9 && snapshot.Length <= Seconds * 11 + 1, "Reduction history is not approximately 100 ms per point");
        double previousTime = -1;
        foreach (var point in snapshot)
        {
            Require(double.IsFinite(point.Time) && point.Time > previousTime && point.Time >= 0 && point.Time <= Seconds,
                "Reduction history timestamps are not ordered within the source duration");
            Require(double.IsFinite(point.ReductionDb) && point.ReductionDb >= 0 && point.ReductionDb <= result.MaxReductionDb + .002,
                "Reduction history contains invalid or out-of-range values");
            previousTime = point.Time;
        }
        Require(snapshot.Any(point => point.ReductionDb > .5), "Active limiting did not appear in reduction history");
        if (result.ReductionTrace is IList<LimiterReductionPoint> mutable && snapshot.Length > 0)
        {
            bool blocked = false;
            try { mutable[0] = snapshot[0] with { ReductionDb = 999 }; }
            catch (NotSupportedException) { blocked = true; }
            Require(blocked, "Published reduction history allows external mutation");
        }
        AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0, Settings with { MaximizerStyle = MaximizerStyle.Punchy }, result);
        Require(result.ReductionTrace.SequenceEqual(snapshot), "Processing a new preview mutated the previous history");
    }

    private static void Equal(RenderResult actual, RenderResult expected)
    {
        Require(actual.Samples.AsSpan().SequenceEqual(expected.Samples), "Incremental maximizer PCM differs from a fresh render");
        Require(actual.MatchGainDb == expected.MatchGainDb && actual.OutputLufs == expected.OutputLufs
            && actual.TruePeakDb == expected.TruePeakDb && actual.MaxReductionDb == expected.MaxReductionDb
            && actual.AverageReductionDb == expected.AverageReductionDb && actual.LimitedPercent == expected.LimitedPercent
            && actual.HeavyLimitingPercent == expected.HeavyLimitingPercent && actual.SafetyAttenuationDb == expected.SafetyAttenuationDb,
            "Incremental maximizer measurements differ from a fresh render");
        Require(actual.ReductionTrace.SequenceEqual(expected.ReductionTrace), "Incremental maximizer history differs from a fresh render");
    }

    private static void IncrementalControls()
    {
        var samples = Fixture();
        var previous = Render(samples, Settings);
        var original = (float[])previous.Samples.Clone();
        var identical = AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0, Settings, previous);
        NoWork(identical, "Identical active controls");
        Require(ReferenceEquals(identical.Samples, previous.Samples) && ReferenceEquals(identical.ReductionTrace, previous.ReductionTrace),
            "Identical controls did not retain immutable PCM and history references");
        ProcessingSettings[] variants =
        [
            Settings with { MaximizerStyle = MaximizerStyle.Punchy },
            Settings with { MaximizerCharacter = 0 },
            Settings with { TransientPreservation = 1 },
        ];
        foreach (var settings in variants)
        {
            var incremental = AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0, settings, previous);
            Equal(incremental, Render(samples, settings));
            Require(incremental.Work.UsedIncrementalStage && incremental.Work.LevelingFramesProcessed == 0
                && incremental.Work.DetectorFramesProcessed == 0, "Maximizer-only edit rebuilt unchanged pre-match stages");
            Require(incremental.Work.OutputChangedEndFrame > incremental.Work.OutputChangedStartFrame
                && Different(incremental.Samples, previous.Samples), "Maximizer edit incorrectly retained stale final PCM");
            Require(incremental.RenderedLimiterEnabled == settings.LimiterEnabled
                && incremental.RenderedCeilingDb == settings.CeilingDb
                && incremental.RenderedMaximizerStyle == settings.MaximizerStyle
                && incremental.RenderedMaximizerCharacter == settings.MaximizerCharacter
                && incremental.RenderedTransientPreservation == settings.TransientPreservation,
                "Incremental maximizer metadata retained old settings");
        }
        Require(previous.Samples.AsSpan().SequenceEqual(original), "Changing maximizer settings mutated a playing/cached preview");
        var bypassSettings = Settings with { LimiterEnabled = false };
        var bypassed = Render(samples, bypassSettings);
        var editedBypass = AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0,
            bypassSettings with { MaximizerStyle = MaximizerStyle.Punchy, MaximizerCharacter = 1, TransientPreservation = 0 }, bypassed);
        NoWork(editedBypass, "Bypassed style controls");
        Require(ReferenceEquals(editedBypass.Samples, bypassed.Samples) && ReferenceEquals(editedBypass.ReductionTrace, bypassed.ReductionTrace),
            "Editing bypassed maximizer controls replaced immutable audio/history");
        Require(!editedBypass.RenderedLimiterEnabled && editedBypass.RenderedMaximizerStyle == MaximizerStyle.Punchy
            && editedBypass.RenderedMaximizerCharacter == 1 && editedBypass.RenderedTransientPreservation == 0,
            "Bypassed no-op did not retain the new maximizer settings as metadata");
    }

    private static void NoWork(RenderResult result, string context)
        => Require(result.Work.UsedIncrementalStage && result.Work.LevelingFramesProcessed == 0
            && result.Work.DetectorFramesProcessed == 0 && result.Work.LoudnessFramesProcessed == 0
            && result.Work.FinalPeakFramesProcessed == 0 && result.Work.OutputChangedStartFrame == 0
            && result.Work.OutputChangedEndFrame == 0, $"{context} caused unnecessary processing");

    private static void LocalPeakRepair()
    {
        const int seconds = 4;
        var samples = new float[seconds * Rate * 2];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            samples[frame * 2] = (float)(.05 * Math.Sin(2 * Math.PI * 220 * frame / Rate));
            samples[frame * 2 + 1] = samples[frame * 2] * .37f;
        }
        int spike = 2 * Rate + Rate / 20;
        samples[spike * 2] = 1.4f; samples[spike * 2 + 1] = samples[spike * 2] * .37f;
        var before = (float[])samples.Clone();
        var gains = Enumerable.Repeat(1f, samples.Length / 2).ToArray();
        var peaks = PeakBlockCache.Create(samples, Rate, 2, default);
        double ceiling = Math.Pow(10, -1 / 20.0);
        Require(peaks.Peak > ceiling, "Isolated-spike fixture did not exercise reconstructed peak repair");
        int changedFirst = gains.Length, changedEnd = 0, passes = 0;
        for (; passes < 2 && peaks.Peak > ceiling; passes++)
        {
            Require(AdaptiveMaximizer.CorrectReconstructedPeaks(samples, gains, peaks, Rate, 2, ceiling, default,
                out int first, out int end), "Overshooting local peak did not produce a repair");
            Require(first >= (int)(1.9 * Rate) && end <= (int)(2.3 * Rate) && first < end && end - first <= Rate * .4,
                $"Local peak repair touched an unbounded region: {first}..{end}");
            changedFirst = Math.Min(changedFirst, first); changedEnd = Math.Max(changedEnd, end);
            peaks = PeakBlockCache.Update(peaks, samples, Rate, 2, first, end, default);
        }
        Require(passes > 0 && peaks.Peak <= ceiling + 1e-6, "Two local correction passes did not restore reconstructed headroom");
        Require(TruePeakMeter.Measure(samples, 2, default) <= ceiling + 1e-6, "Independent reconstruction still exceeded the ceiling after local repair");
        Require(samples.AsSpan(0, changedFirst * 2).SequenceEqual(before.AsSpan(0, changedFirst * 2))
            && samples.AsSpan(changedEnd * 2).SequenceEqual(before.AsSpan(changedEnd * 2)), "Local correction changed distant PCM");
        Require(gains.AsSpan(0, changedFirst).ToArray().All(gain => gain == 1)
            && gains.AsSpan(changedEnd).ToArray().All(gain => gain == 1), "Local correction attenuated distant gain history");
        Require(gains.Min() < 1 && gains.All(gain => gain > 0 && gain <= 1), "Local repair gain bounds are invalid");
        for (int index = 0; index < samples.Length; index += 2)
            Near(samples[index + 1], samples[index] * .37, 2e-7, "Local repair must preserve stereo linkage");
        var repaired = (float[])samples.Clone(); var repairedGains = (float[])gains.Clone();
        Require(!AdaptiveMaximizer.CorrectReconstructedPeaks(samples, gains, peaks, Rate, 2, ceiling, default, out _, out _),
            "Safe reconstructed peaks requested an additional correction");
        Require(samples.AsSpan().SequenceEqual(repaired) && gains.AsSpan().SequenceEqual(repairedGains), "A no-op local repair changed audio or gain history");
    }

    private static void BypassedSafety()
    {
        var samples = Fixture();
        var settings = Settings with { LimiterEnabled = false };
        var result = Render(samples, settings);
        Require(!result.RenderedLimiterEnabled && result.Samples.AsSpan().SequenceEqual(samples),
            "Bypassed peak protection changed above-ceiling source PCM");
        Require(result.TruePeakDb > settings.CeilingDb + .1,
            "Above-ceiling bypass fixture failed to exercise the inactive ceiling");
        Near(result.SafetyAttenuationDb, 0, 0, "Bypassed safety attenuation");
        Near(result.MaxReductionDb, 0, 0, "Bypassed maximum reduction");
        Near(result.AverageReductionDb, 0, 0, "Bypassed average reduction");
        Near(result.LimitedPercent, 0, 0, "Bypassed limited percentage");
        Require(result.ReductionTrace.All(point => point.ReductionDb == 0), "Bypassed protection published a nonzero reduction history");
        Require(result.Work.DetectorFramesProcessed == 0, "Bypassed peak protection still ran the limiter detector");

        // Other active modules/gains remain valid reasons to render, but none
        // may implicitly reactivate the bypassed peak-protection ceiling.
        EnvelopePoint[] envelope = [new(0, 6), new(Seconds, 6)];
        var leveling = settings with { LevelingEnabled = true, Strength = .5 };
        var raised = AudioProcessor.Render(samples, Rate, 2, envelope, 3, leveling, inputGainDb: 4);
        double gain = Math.Pow(10, 10 / 20.0);
        for (int index = 0; index < samples.Length; index++)
            Near(raised.Samples[index], samples[index] * gain, 2e-6,
                "Active leveling/input/manual gain was altered by bypassed protection");
        Near(raised.SafetyAttenuationDb, 0, 0, "Active leveling with bypassed protection");
        Require(raised.Work.DetectorFramesProcessed == 0, "Leveling-only render ran the limiter detector");

        var ceilingOnly = AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0,
            settings with { CeilingDb = -12 }, result);
        Require(ReferenceEquals(ceilingOnly.Samples, result.Samples) && ceilingOnly.Work.LevelingFramesProcessed == 0
            && ceilingOnly.Work.DetectorFramesProcessed == 0 && ceilingOnly.Work.FinalPeakFramesProcessed == 0,
            "Changing an inactive ceiling reprocessed the audio");
        var protectedAgain = AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0, Settings, result);
        var freshProtected = Render(samples, Settings);
        Require(protectedAgain.Samples.AsSpan().SequenceEqual(freshProtected.Samples),
            "Re-enabling protection reused a stale or absent bypass detector");
        Require(protectedAgain.Work.DetectorFramesProcessed == samples.Length / 2,
            "Re-enabling protection did not rebuild its detector");
        Safe(protectedAgain, -1);
    }

    private static void CancellationAndValidation()
    {
        var samples = Fixture();
        var previous = Render(samples, Settings);
        using var source = new CancellationTokenSource(); source.Cancel();
        int cancelled = 0;
        try { AudioProcessor.Render(samples, Rate, 2, [], 0, Settings, source.Token); }
        catch (OperationCanceledException) { cancelled++; }
        try { AudioProcessor.RenderIncremental(samples, Rate, 2, [], 0, Settings with { MaximizerStyle = MaximizerStyle.Punchy }, previous, source.Token); }
        catch (OperationCanceledException) { cancelled++; }
        Require(cancelled == 2, "Adaptive maximizer ignored cancellation");
        int invalid = 0;
        foreach (var settings in new[] { Settings with { MaximizerCharacter = double.NaN }, Settings with { TransientPreservation = double.PositiveInfinity } })
        {
            try { Render(samples, settings); }
            catch (ArgumentException) { invalid++; }
        }
        Require(invalid == 2, "Non-finite maximizer controls were accepted");
    }
}

using Multiloud.Core;

internal static class BackgroundMixChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Background placements crop negative starts and never loop", PlacementsAndNoLoop),
        ("Background segments retain alignment, continuity and deterministic PCM", SegmentContinuity),
        ("Background resampling preserves audible tones and channel mapping", ResamplingAndChannels),
        ("Background fades multiply in amplitude before the layer gain", FadesAndLayerGain),
        ("Unavailable background sources block only intersecting active regions", MissingSources),
        ("A silent background is exactly the existing music render", SilentBedBypass),
        ("Track trim changes music without trimming the fixed background", MusicOnlyTrim),
        ("Background-aware matching reaches feasible final-mix loudness", FeasibleMatching),
        ("Music-only peak control preserves the bed through musical silence", MusicOnlyPeakControl),
        ("Background-only and infeasible targets preserve fixed-bed level", InfeasibleTargetsAndSilence),
        ("A background above the chosen ceiling is rejected only with peak protection enabled", UnsafeBed),
        ("Background mixes keep leveling and matching gains unprotected when peak protection is off", BypassedPeakControl),
        ("Background-aware incremental rendering matches a fresh immutable result", IncrementalAndHistory),
        ("Background rendering validates bounds and honors cancellation", ValidationAndCancellation),
    ];

    private const int Rate = 12000;
    private const int Channels = 2;
    private static ProcessingSettings Settings => new()
    {
        LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false, CeilingDb = -1,
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:R} ± {tolerance:R}, got {actual:R}");

    private static void Rejects(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, message);
    }

    private static float[] Tone(double seconds, double amplitude, double frequency = 500, int channels = Channels, int rate = Rate)
    {
        var samples = new float[(int)Math.Round(seconds * rate) * channels];
        for (int frame = 0; frame < samples.Length / channels; frame++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / rate));
            for (int channel = 0; channel < channels; channel++) samples[frame * channels + channel] = value;
        }
        return samples;
    }

    private static AudioTrack Source(float[] samples, int channels = Channels, int rate = Rate) => new()
    {
        FilePath = "background-fixture.wav", Original = samples, Channels = channels, SampleRate = rate,
    };

    private static BackgroundLayer Layer(params BackgroundClip[] clips) => new() { Clips = clips };

    private static void EqualSamples(float[] actual, float[] expected, double tolerance, string message)
    {
        Require(actual.Length == expected.Length, $"{message}: sample count differs");
        for (int index = 0; index < actual.Length; index++)
            Near(actual[index], expected[index], tolerance, $"{message} at sample {index}");
    }

    private static void PlacementsAndNoLoop()
    {
        var ramp = Enumerable.Range(0, Rate).Select(frame => frame / (float)Rate).ToArray();
        var source = Source(ramp, 1);
        var saved = (float[])ramp.Clone();
        var placed = BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = source, StartSeconds = .25 }), 0, 2, Rate, Channels);
        Require(placed.Length == 2 * Rate * Channels, "Background segment length is wrong");
        foreach (int frame in new[] { 0, Rate / 4 - 1, Rate * 5 / 4, 2 * Rate - 1 })
            Near(placed[frame * 2], 0, 0, "Outside a placed clip must be silent without looping");
        foreach (int frame in new[] { Rate / 4, Rate / 2, Rate, Rate * 5 / 4 - 1 })
        {
            double expected = ramp[frame - Rate / 4];
            Near(placed[frame * 2], expected, 2e-6, "Integer clip alignment");
            Near(placed[frame * 2 + 1], expected, 2e-6, "Mono duplication");
        }
        var cropped = BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = source, StartSeconds = -.25 }), 0, 1, Rate, 1);
        Near(cropped[0], ramp[Rate / 4], 2e-6, "Negative placement must crop the source beginning");
        Near(cropped[Rate / 2], ramp[Rate * 3 / 4], 2e-6, "Negative source offset");
        Near(cropped[Rate * 3 / 4], 0, 0, "Cropped source must stop at its real end");
        Require(ramp.AsSpan().SequenceEqual(saved), "Background rendering mutated source PCM");
    }

    private static void SegmentContinuity()
    {
        const int sourceRate = 11025;
        var source = Source(Tone(3, .08, 739, 1, sourceRate), 1, sourceRate);
        var layer = Layer(new BackgroundClip { Source = source, StartSeconds = .1373, FadeInSeconds = .15, FadeOutSeconds = .2 });
        var complete = BackgroundRenderer.RenderSegment(layer, 0, 3, Rate, Channels);
        var repeated = BackgroundRenderer.RenderSegment(layer, 0, 3, Rate, Channels);
        Require(complete.AsSpan().SequenceEqual(repeated), "Repeated background rendering is not deterministic");
        var first = BackgroundRenderer.RenderSegment(layer, 0, 1.25, Rate, Channels);
        var second = BackgroundRenderer.RenderSegment(layer, 1.25, 1.75, Rate, Channels);
        EqualSamples(first.Concat(second).ToArray(), complete, 2e-7, "Adjacent background segments lost absolute source phase");
        // The noninteger source placement must not be silently snapped to an output frame.
        var snapped = BackgroundRenderer.RenderSegment(Layer(new BackgroundClip
        {
            Source = source, StartSeconds = Math.Round(.1373 * Rate) / Rate, FadeInSeconds = .15, FadeOutSeconds = .2
        }), 0, 3, Rate, Channels);
        Require(complete.Zip(snapped).Any(pair => Math.Abs(pair.First - pair.Second) > 1e-5), "Fractional placement was rounded away");
    }

    private static void ResamplingAndChannels()
    {
        const int sourceRate = 11025;
        var mono = Source(Tone(1, .1, 500, 1, sourceRate), 1, sourceRate);
        var converted = BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = mono }), 0, 1, Rate, Channels);
        double error = 0; int count = 0;
        for (int frame = 100; frame < Rate - 100; frame++)
        {
            double reference = .1 * Math.Sin(2 * Math.PI * 500 * frame / Rate);
            error += Math.Pow(converted[frame * 2] - reference, 2); count++;
            Near(converted[frame * 2 + 1], converted[frame * 2], 0, "Resampled mono must duplicate to stereo at unity");
        }
        Require(Math.Sqrt(error / count) < .001, "Resampling materially changed a well-within-band audio tone");
        var stereo = new float[Rate * 2];
        for (int frame = 0; frame < Rate; frame++) { stereo[frame * 2] = .08f; stereo[frame * 2 + 1] = .02f; }
        var averaged = BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = Source(stereo) }), 0, 1, Rate, 1);
        Near(averaged[Rate / 2], .05, 1e-6, "Stereo background converted to mono must average channels");
    }

    private static void FadesAndLayerGain()
    {
        var constant = Source(Enumerable.Repeat(.1f, Rate).ToArray(), 1);
        var clip = new BackgroundClip { Source = constant, FadeInSeconds = .8, FadeOutSeconds = .8 };
        var faded = BackgroundRenderer.RenderSegment(new BackgroundLayer { GainDb = -6, Clips = new[] { clip } }, 0, 1, Rate, 1);
        double gain = Math.Pow(10, -6 / 20.0);
        foreach (double time in new[] { 0d, .25, .5, .75 })
        {
            double expected = .1 * Math.Min(1, time / .8) * Math.Min(1, (1 - time) / .8) * gain;
            Near(faded[(int)(time * Rate)], expected, 1e-6, "Overlapping edge fades must multiply in amplitude");
        }
        var overlap = BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = constant }, new BackgroundClip { Source = constant }), 0, 1, Rate, 1);
        Near(overlap[Rate / 2], .2, 1e-6, "Overlapping background clips must sum");
    }

    private static void MissingSources()
    {
        var missing = new AudioTrack { FilePath = "missing-background.wav", SavedDuration = 1, WorkState = TrackWorkState.Missing };
        var layer = Layer(new BackgroundClip { Source = missing, StartSeconds = 2 });
        Require(BackgroundRenderer.RenderSegment(layer, 0, 1, Rate, Channels).All(sample => sample == 0), "A missing nonintersecting clip blocked an unrelated segment");
        Rejects(() => BackgroundRenderer.RenderSegment(layer, 2, 1, Rate, Channels), "An intersecting missing background source was silently omitted");
        var disabled = new BackgroundLayer { Enabled = false, Clips = layer.Clips };
        Require(BackgroundRenderer.RenderSegment(disabled, 2, 1, Rate, Channels).All(sample => sample == 0), "A disabled background layer requested missing audio");
    }

    private static void SilentBedBypass()
    {
        var music = Tone(3, .06);
        EnvelopePoint[] envelope = [new(0, 0), new(1, 0), new(1.5, 2), new(2, 0), new(3, 0)];
        var settings = Settings with { LevelingEnabled = true, MatchingEnabled = true, LimiterEnabled = true, TargetLufs = -18 };
        var legacy = AudioProcessor.Render(music, Rate, Channels, envelope, -1, settings);
        var background = new float[music.Length];
        var mixed = AudioProcessor.RenderWithBackground(music, Rate, Channels, envelope, -1, settings, background);
        Require(mixed.Samples.AsSpan().SequenceEqual(legacy.Samples), "A digital-silent bed changed legacy music PCM");
        Require(mixed.MatchGainDb == legacy.MatchGainDb && mixed.OutputLufs == legacy.OutputLufs && mixed.TruePeakDb == legacy.TruePeakDb
            && mixed.MaxReductionDb == legacy.MaxReductionDb, "A silent bed changed legacy measurements");
        Require(mixed.IncludesBackground && !mixed.HasAudibleBackground && mixed.BackgroundSamples is { Length: > 0 },
            "A rendered silent background segment must retain playback-readiness metadata");
        var edited = envelope.ToArray(); edited[2] = new EnvelopePoint(1.5, 3);
        var incremental = AudioProcessor.RenderWithBackground(music, Rate, Channels, edited, -1, settings, background, previous: mixed);
        var complete = AudioProcessor.RenderWithBackground(music, Rate, Channels, edited, -1, settings, background);
        Require(incremental.Work.UsedIncrementalStage && incremental.Work.LevelingFramesProcessed < music.Length / Channels,
            "A silent-bed readiness wrapper prevented localized pre-match stage reuse");
        Require(incremental.Samples.AsSpan().SequenceEqual(complete.Samples), "Localized silent-bed PCM differs from a fresh render");
    }

    private static void MusicOnlyTrim()
    {
        var music = Tone(3, .025);
        var bed = Tone(3, .008, 233);
        var savedMusic = (float[])music.Clone(); var savedBed = (float[])bed.Clone();
        var result = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], -6, Settings, bed);
        double factor = Math.Pow(10, -6 / 20.0);
        for (int index = 0; index < music.Length; index++)
            Near(result.Samples[index], music[index] * factor + bed[index], 2e-7, "Track trim attenuated the fixed background");
        Require(result.IncludesBackground && result.BackgroundSamples != null && result.BackgroundSamples.AsSpan().SequenceEqual(bed), "Mixed preview did not retain the exact fixed background");
        Require(music.AsSpan().SequenceEqual(savedMusic) && bed.AsSpan().SequenceEqual(savedBed), "Mix rendering mutated its input arrays");
        var raw = music.Zip(bed, (m, b) => m + b).ToArray();
        Near(result.OriginalMixLufs ?? double.NaN, LoudnessAnalyzer.Analyze(raw, Rate, Channels).IntegratedLufs, .01, "Original mixed loudness must describe raw music plus fixed bed");
    }

    private static void FeasibleMatching()
    {
        var music = Tone(3, .04);
        var bed = Tone(3, .004, 233);
        var settings = Settings with { MatchingEnabled = true, LimiterEnabled = true, TargetLufs = -18 };
        var full = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, settings, bed);
        Near(full.OutputLufs, -18, .4, "Background-aware full matching target");
        Near(LoudnessAnalyzer.Analyze(full.Samples, Rate, Channels).IntegratedLufs, full.OutputLufs, .01, "Mixed loudness must be measured from final PCM");
        Require(LoudnessAnalyzer.Analyze(full.Samples, Rate, Channels).TruePeakDb <= settings.CeilingDb + .02, "Matched final mix exceeded the reconstructed peak ceiling");
        var baseline = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, settings with { MatchingStrength = 0 }, bed);
        var half = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, settings with { MatchingStrength = .5 }, bed);
        Near(half.OutputLufs, (baseline.OutputLufs + settings.TargetLufs) * .5, .4, "Fractional background-aware matching must interpolate mixed LUFS");
        // A coherent inverse bed has a loudness minimum at unity music gain.
        // Both extreme gain endpoints are louder than this attainable target,
        // so a solver assuming only an increasing endpoint bracket will miss it.
        var coherentMusic = Tone(3, .05, 1000);
        var inverseBed = coherentMusic.Select(value => -value).ToArray();
        var cancellation = AudioProcessor.RenderWithBackground(coherentMusic, Rate, Channels, [], 0,
            settings with { TargetLufs = -36 }, inverseBed);
        Near(cancellation.OriginalMixLufs ?? double.NaN, -70, .01, "Coherent inverse fixture must cancel before matching");
        Near(cancellation.OutputLufs, -36, .4, "A measured decreasing-branch target was incorrectly treated as infeasible");
        Near(LoudnessAnalyzer.Analyze(cancellation.Samples, Rate, Channels).IntegratedLufs, cancellation.OutputLufs, .01,
            "Coherent background target must be measured from actual final PCM");
        // A non-unity inverse bed moves the loudness minimum away from every
        // zero-gain seed. Trim shifts the matching-coordinate minimum again.
        var strongerInverseBed = coherentMusic.Select(value => -2 * value).ToArray();
        foreach (double trim in new[] { 0d, 3d, 6d })
        {
            var nonUnity = AudioProcessor.RenderWithBackground(coherentMusic, Rate, Channels, [], trim,
                settings with { TargetLufs = -36 }, strongerInverseBed);
            Near(nonUnity.OutputLufs, -36 + trim, .4,
                $"Non-unity inverse bed missed its feasible target with {trim:0} dB music trim");
            Require(LoudnessAnalyzer.Analyze(nonUnity.Samples, Rate, Channels).TruePeakDb <= settings.CeilingDb + .02,
                "Non-unity coherent-background matching exceeded the final reconstructed peak ceiling");
        }
    }

    private static void MusicOnlyPeakControl()
    {
        var music = Tone(4, .65);
        for (int frame = 0; frame < music.Length / 2; frame++)
        {
            if (frame >= Rate / 2 && frame < Rate || frame >= 2 * Rate && frame < Rate * 5 / 2)
                music[frame * 2] = music[frame * 2 + 1] = 0;
        }
        for (int frame = Rate / 4; frame < music.Length / 2; frame += Rate)
            music[frame * 2] = music[frame * 2 + 1] = 1.8f;
        var bed = Tone(4, .045, 233);
        var result = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings with { LimiterEnabled = true }, bed);
        Require(result.MaxReductionDb > .1, "Peak-control fixture did not exercise music limiting");
        for (int frame = Rate / 2; frame < Rate; frame++)
            for (int channel = 0; channel < Channels; channel++)
                Near(result.Samples[frame * 2 + channel], bed[frame * 2 + channel], 0, "Music limiting or final safety attenuation altered the bed during a music gap");
        Require(LoudnessAnalyzer.Analyze(result.Samples, Rate, Channels).TruePeakDb <= -0.98, "Music-only protection did not safely bound the complete mix");
    }

    private static void InfeasibleTargetsAndSilence()
    {
        var bed = Tone(3, .1, 1000);
        var silence = new float[bed.Length];
        var settings = Settings with { MatchingEnabled = true, LimiterEnabled = true, TargetLufs = -36 };
        var onlyBed = AudioProcessor.RenderWithBackground(silence, Rate, Channels, [], 12, settings, bed, inputGainDb: 20);
        Require(onlyBed.Samples.AsSpan().SequenceEqual(bed), "Silent music caused the fixed bed to be changed or amplified");
        Near(onlyBed.MatchGainDb, 0, 1e-9, "Digital music silence must not receive matching gain");
        Require(!string.IsNullOrWhiteSpace(onlyBed.Warning), "An unattainable background-only target was not reported");
        var quietMusic = Tone(3, .01);
        var constrained = AudioProcessor.RenderWithBackground(quietMusic, Rate, Channels, [], 0, settings, bed);
        Require(!string.IsNullOrWhiteSpace(constrained.Warning), "An impossible below-bed loudness target was not reported");
        Require(Math.Abs(constrained.MatchGainDb) <= 36.000001, "Infeasible matching exceeded the allowed music gain");
        Require(constrained.OutputLufs > settings.TargetLufs + 5, "Matching silently attenuated the fixed bed to meet an impossible target");
    }

    private static void UnsafeBed()
    {
        var music = Tone(2, .8, 500);
        var bed = Tone(2, .99, 1000);
        var bypass = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, bed);
        for (int index = 0; index < music.Length; index++)
            Near(bypass.Samples[index], music[index] + bed[index], 0,
                "Bypassed protection changed a mix with an above-ceiling fixed bed");
        Require(bypass.TruePeakDb > Settings.CeilingDb + .1 && bypass.SafetyAttenuationDb == 0
            && bypass.MaxReductionDb == 0 && bypass.Work.DetectorFramesProcessed == 0,
            "Unprotected background mix silently applied or prepared peak control");
        Rejects(() => AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0,
            Settings with { LimiterEnabled = true }, bed), "An independently unsafe fixed bed was accepted and silently trimmed");
    }

    private static void BypassedPeakControl()
    {
        var music = Tone(3, .4, 500);
        var bed = Tone(3, .02, 233);
        EnvelopePoint[] envelope = [new(0, 6), new(3, 6)];
        var settings = Settings with { LevelingEnabled = true, Strength = 1, CeilingDb = -12 };
        var leveled = AudioProcessor.RenderWithBackground(music, Rate, Channels, envelope, -1,
            settings, bed, inputGainDb: 2);
        for (int index = 0; index < music.Length; index++)
        {
            // The processor applies input/envelope gain before the manual trim.
            float first = (float)(music[index] * Math.Pow(10, 8 / 20.0));
            float expected = (float)(first * Math.Pow(10, -1 / 20.0) + bed[index]);
            Near(leveled.Samples[index], expected, 2e-7,
                "Unprotected leveling mix changed requested music gain or the fixed bed");
        }
        Require(leveled.TruePeakDb > settings.CeilingDb + 1 && leveled.SafetyAttenuationDb == 0
            && leveled.MaxReductionDb == 0 && leveled.Work.DetectorFramesProcessed == 0,
            "Leveling-only background mix implicitly enabled peak protection");

        var matchedSettings = Settings with { MatchingEnabled = true, TargetLufs = -6, CeilingDb = -12 };
        var matched = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, matchedSettings, bed);
        Near(matched.OutputLufs, -6, .4, "Matching without protection should not be constrained by its inactive ceiling");
        Require(matched.TruePeakDb > matchedSettings.CeilingDb + 1 && matched.SafetyAttenuationDb == 0
            && matched.MaxReductionDb == 0, "Matching implicitly attenuated an above-ceiling music/background mix");

        // Switching back on must rebuild the detector, while preserving the
        // exact same protected result as a fresh render.
        var protectedSettings = Settings with { LimiterEnabled = true };
        var protectedAgain = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0,
            protectedSettings, bed, previous: matched);
        var freshProtected = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0,
            protectedSettings, bed);
        Require(protectedAgain.Samples.AsSpan().SequenceEqual(freshProtected.Samples),
            "Re-enabling background peak protection reused a stale or absent detector");
        Require(protectedAgain.Work.DetectorFramesProcessed == music.Length / Channels,
            "Re-enabling background protection did not rebuild its detector");
        Require(protectedAgain.TruePeakDb <= protectedSettings.CeilingDb + .02,
            "Re-enabled background peak protection did not restore the ceiling");
    }

    private static void IncrementalAndHistory()
    {
        var music = Tone(3, .05);
        var bed = Tone(3, .008, 233);
        EnvelopePoint[] before = [new(0, 0), new(1, 0), new(1.5, 1), new(2, 0), new(3, 0)];
        EnvelopePoint[] after = [new(0, 0), new(1, 0), new(1.5, 2), new(2, 0), new(3, 0)];
        var settings = Settings with { LevelingEnabled = true, Strength = .7, MatchingEnabled = true, LimiterEnabled = true, TargetLufs = -18 };
        var previous = AudioProcessor.RenderWithBackground(music, Rate, Channels, before, 0, settings, bed);
        Require(previous.HasAudibleBackground, "The audible-bed cache regression fixture was not marked as mixed audio");
        var saved = (float[])previous.Samples.Clone(); var savedBed = (float[])bed.Clone();
        var incremental = AudioProcessor.RenderWithBackground(music, Rate, Channels, after, 0, settings, bed, previous: previous);
        var complete = AudioProcessor.RenderWithBackground(music, Rate, Channels, after, 0, settings, bed);
        Require(incremental.Samples.AsSpan().SequenceEqual(complete.Samples), "Incremental mixed PCM differs from a fresh mix render");
        Require(incremental.OutputLufs == complete.OutputLufs && incremental.TruePeakDb == complete.TruePeakDb
            && incremental.MatchGainDb == complete.MatchGainDb && incremental.MaxReductionDb == complete.MaxReductionDb,
            "Incremental mixed measurements differ from a fresh mix render");
        Require(previous.Samples.AsSpan().SequenceEqual(saved) && bed.AsSpan().SequenceEqual(savedBed), "Incremental mixing mutated a previous preview or its fixed bed");
        var removed = AudioProcessor.RenderIncremental(music, Rate, Channels, after, 0, settings, incremental);
        var ordinary = AudioProcessor.Render(music, Rate, Channels, after, 0, settings);
        Require(!removed.Work.UsedIncrementalStage, "Ordinary music reused final-stage caches containing an audible background");
        Require(removed.Samples.AsSpan().SequenceEqual(ordinary.Samples)
            && removed.OutputLufs == ordinary.OutputLufs && removed.TruePeakDb == ordinary.TruePeakDb
            && removed.MatchGainDb == ordinary.MatchGainDb && removed.MaxReductionDb == ordinary.MaxReductionDb,
            "Removing the background reused mixed final-stage caches as ordinary music");
    }

    private static void ValidationAndCancellation()
    {
        var source = Source(Tone(1, .01));
        var clip = new BackgroundClip { Source = source };
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        int cancelled = 0;
        try { BackgroundRenderer.RenderSegment(Layer(clip), 0, 1, Rate, Channels, cancel.Token); }
        catch (OperationCanceledException) { cancelled++; }
        var music = Tone(1, .03); var bed = Tone(1, .004, 233);
        try { AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, bed, cancel.Token); }
        catch (OperationCanceledException) { cancelled++; }
        Require(cancelled == 2, "Background generation or mix rendering ignored cancellation");
        Rejects(() => BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = source, StartSeconds = double.NaN }), 0, 1, Rate, Channels), "A non-finite clip placement was accepted");
        Rejects(() => BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = source, StartSeconds = 86401 }), 0, 1, Rate, Channels), "An out-of-bounds clip placement was accepted");
        Rejects(() => BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = source, FadeInSeconds = -.1 }), 0, 1, Rate, Channels), "A negative fade was accepted");
        Rejects(() => BackgroundRenderer.RenderSegment(Layer(new BackgroundClip { Source = source, FadeOutSeconds = 2 }), 0, 1, Rate, Channels), "A fade longer than its source was accepted");
        Rejects(() => BackgroundRenderer.RenderSegment(new BackgroundLayer { GainDb = double.NaN, Clips = new[] { clip } }, 0, 1, Rate, Channels), "A non-finite background gain was accepted");
        Rejects(() => BackgroundRenderer.RenderSegment(Layer(clip), 0, 1, 0, Channels), "An invalid output sample rate was accepted");
        Rejects(() => AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, new float[1]), "A background of the wrong sample length was accepted");
        var invalid = (float[])bed.Clone(); invalid[3] = float.NaN;
        Rejects(() => AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, invalid), "Non-finite background PCM was accepted");
    }
}

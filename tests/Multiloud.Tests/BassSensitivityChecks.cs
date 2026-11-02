using Multiloud.Core;

internal static class BassSensitivityChecks
{
    private const int Rate = 12000;
    private const int Duration = 60;
    private sealed record Fixture(float[] Samples, AnalysisResult Analysis);
    private static readonly Lazy<Fixture> Kick45 = new(() => Analyze(Music(45)));
    private static readonly Lazy<Fixture> Kick60 = new(() => Analyze(Music(60)));
    private static ProcessingSettings Settings(double sensitivity) => new() { BassSensitivity = sensitivity, MaxLevelingDb = 6, TransitionSeconds = 8 };

    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Reduced bass sensitivity ignores more of sustained 45/60 Hz kicks over unchanged melody", BassChanges),
        ("Reduced sensitivity still levels genuine broadband six-dB sections", BroadbandChanges),
        ("Reduced profiles and track joins use each detector's own reference", DetectorReferenceAndJoins),
        ("Bass-only quiet passages retain a conservative positive-gain cap", BassOnly),
        ("Reduced-bass suggestions preserve fades and silent passages", FadesAndSilence),
        ("Detector profiles preserve standard meters and use immutable cached energies", MeterIsolation),
        ("Normal sensitivity preserves standard profiles and legacy analysis suggestions", LegacyNormal),
        ("Bass detection never filters bypassed or zero-strength audio", BypassAudio),
        ("Bass sensitivity validates bounds and honors cancellation including cache hits", BoundsAndCancellation),
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Near(double actual, double expected, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:G6} ± {tolerance:G3}, got {actual:G6}.");
    private static Fixture Analyze(float[] samples) => new(samples, LoudnessAnalyzer.Analyze(samples, Rate, 1));
    private static double Melody(double time) => .035 * Math.Sin(2 * Math.PI * 880 * time)
        + .012 * Math.Sin(2 * Math.PI * 1760 * time) + .006 * Math.Sin(2 * Math.PI * 2640 * time);
    private static float[] Music(double bassFrequency, bool broadband = false)
    {
        var samples = new float[Duration * Rate];
        for (int frame = 0; frame < samples.Length; frame++)
        {
            double time = frame / (double)Rate;
            bool middle = time is >= 20 and < 40;
            if (broadband)
            {
                double bed = Melody(time) + .025 * Math.Sin(2 * Math.PI * 190 * time) + .02 * Math.Sin(2 * Math.PI * 3780 * time);
                samples[frame] = (float)(bed * (middle ? 1 : .5));
            }
            else
            {
                double beat = time % .5;
                double kick = middle ? .35 * Math.Min(1, beat / .012) * Math.Exp(-beat / .16) * Math.Sin(2 * Math.PI * bassFrequency * time) : 0;
                samples[frame] = (float)(Melody(time) + kick);
            }
        }
        return samples;
    }
    private static double Contrast(IReadOnlyList<EnvelopePoint> envelope)
        => (AudioProcessor.EnvelopeGainAt(envelope, 10) + AudioProcessor.EnvelopeGainAt(envelope, 50)) / 2
            - AudioProcessor.EnvelopeGainAt(envelope, 30);
    private static void Bounds(IReadOnlyList<EnvelopePoint> points, double duration, params double[] abruptTimes)
    {
        Require(points.Count >= 2 && points[0].Time == 0 && points[^1].Time == duration &&
            points.All(point => double.IsFinite(point.Time + point.GainDb) && point.Time >= 0 && point.Time <= duration && Math.Abs(point.GainDb) <= 6.000001),
            "Suggested envelope lost its finite time/gain bounds.");
        for (int i = 1; i < points.Count; i++)
        {
            Require(points[i].Time > points[i - 1].Time, "Bass-aware envelope timestamps did not increase.");
            double slope = Math.Abs(points[i].GainDb - points[i - 1].GainDb) / (points[i].Time - points[i - 1].Time);
            bool confirmedWindow = abruptTimes.Any(time => points[i - 1].Time >= time - 2 && points[i].Time <= time + 2);
            Require(slope <= 1.500001 || confirmedWindow && slope <= 6.000001,
                "Bass-aware leveling exceeded its slow slew outside a genuine abrupt section or its fast safe bound inside one.");
        }
    }

    private static void BassChanges()
    {
        foreach (var fixture in new[] { Kick45.Value, Kick60.Value })
        {
            var normal = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(1));
            var reduced = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(.25));
            double full = Contrast(normal), lessBass = Contrast(reduced);
            Require(full > 1, $"The fixture did not exercise bass-induced leveling: contrast={full:F3} dB.");
            Require(lessBass >= -.1 && lessBass < full * .85 && full - lessBass > .5,
                $"Unchanged melody was still ducked too strongly by bass: normal={full:F3}, reduced={lessBass:F3} dB.");
            Bounds(normal, Duration); Bounds(reduced, Duration);
        }
    }

    private static void BroadbandChanges()
    {
        var fixture = Analyze(Music(0, broadband: true));
        var normal = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(1));
        var reduced = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(.25));
        Require(Contrast(normal) > 2.5 && Contrast(reduced) > 2.5,
            $"A real six-dB musical level change was ignored: normal={Contrast(normal):F3}, reduced={Contrast(reduced):F3}.");
        Near(Contrast(reduced), Contrast(normal), .3, "Broadband leveling should remain effective");
        Bounds(reduced, Duration, 20, 40);
    }

    private static void BassOnly()
    {
        var samples = new float[Duration * Rate];
        for (int frame = 0; frame < samples.Length; frame++)
        {
            double time = frame / (double)Rate;
            samples[frame] = (float)((time is >= 20 and < 40 ? .18 : .045) * Math.Sin(2 * Math.PI * 45 * time));
        }
        var fixture = Analyze(samples);
        var normal = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(1));
        var reduced = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(.25));
        Require(normal.Max(point => point.GainDb) > 3, "The bass-only safety fixture did not need positive gain.");
        Require(reduced.All(point => point.GainDb <= 1.500001), "A bass-only passage was boosted beyond the conservative reduced-bass cap.");
        var profile = LevelingDetector.GetProfile(fixture.Analysis, .25);
        Require(profile.BassConfidence.Count == profile.ShortTerm.Count && profile.BassConfidence.All(value => value is >= 0 and <= 1),
            "Bass confidence lost its per-frame bounds.");
        Bounds(reduced, Duration);
    }

    private static void DetectorReferenceAndJoins()
    {
        const int seconds = 20;
        var bass = Analyze(Enumerable.Range(0, seconds * Rate)
            .Select(frame => (float)(.2 * Math.Sin(2 * Math.PI * 45 * frame / Rate))).ToArray());
        var flute = Analyze(Enumerable.Range(0, seconds * Rate)
            .Select(frame => (float)Melody(frame / (double)Rate)).ToArray());
        var settings = Settings(.25);
        foreach (var fixture in new[] { bass, flute })
        {
            var profile = LevelingDetector.GetProfile(fixture.Analysis, settings);
            double center = profile.ShortTerm.OrderBy(frame => Math.Abs(frame.Time - seconds / 2)).First().Loudness;
            Near(center, profile.ReferenceLoudness, .03, "Steady source should equal its own reduced detector reference");
            Require(EnvelopeDesigner.Design(fixture.Analysis, seconds, settings).All(point => Math.Abs(point.GainDb) <= .05),
                "A flat source received a constant boost from mixing a reduced level with the old standard reference.");
        }
        var bassProfile = LevelingDetector.GetProfile(bass.Analysis, settings);
        Require(bass.Analysis.IntegratedLufs - bassProfile.ReferenceLoudness > 3,
            "The reference fixture did not exercise a substantial bass-dependent detector offset.");
        AudioTrack Track(Fixture fixture) => new()
        {
            SampleRate = Rate, Channels = 1, Original = fixture.Samples, Analysis = fixture.Analysis,
            Envelope = [new(0, 0), new(seconds, 0)]
        };
        var a = Track(bass); var b = Track(flute);
        TransitionDesigner.Update([a, b], settings);
        Require(a.TransitionEnvelope.Concat(b.TransitionEnvelope).All(point => Math.Abs(point.GainDb) <= .05) &&
            Math.Abs(TransitionDesigner.GainAt(a, seconds)) <= .05 && Math.Abs(TransitionDesigner.GainAt(b, 0)) <= .05,
            "A join between individually flat bass and flute sources used inconsistent detector baselines.");
    }

    private static void FadesAndSilence()
    {
        var fade = new float[30 * Rate];
        for (int frame = 0; frame < fade.Length; frame++)
        {
            double time = frame / (double)Rate;
            fade[frame] = (float)((Melody(time) + .2 * Math.Sin(2 * Math.PI * 45 * time)) * Math.Min(1, (30 - time) / 10));
        }
        var envelope = EnvelopeDesigner.Design(Analyze(fade).Analysis, 30, Settings(.25));
        Require(AudioProcessor.EnvelopeGainAt(envelope, 28.5) <= .1 && envelope[^1].GainDb <= .1,
            "A bass-rich terminal fade was lifted into a crescendo.");
        var silence = Analyze(new float[30 * Rate]);
        Require(EnvelopeDesigner.Design(silence.Analysis, 30, Settings(.25)).All(point => point.GainDb == 0),
            "Silence received bass-aware gain.");
        var gap = Music(45);
        Array.Clear(gap, 20 * Rate, 20 * Rate);
        var gaps = EnvelopeDesigner.Design(Analyze(gap).Analysis, Duration, Settings(.25));
        Require(AudioProcessor.EnvelopeGainAt(gaps, 30) <= .000001, "A long silent passage received positive gain.");
        Bounds(gaps, Duration);
    }

    private static void MeterIsolation()
    {
        var fixture = Kick45.Value; var analysis = fixture.Analysis;
        double integrated = analysis.IntegratedLufs, range = analysis.LoudnessRange, peak = analysis.TruePeakDb;
        var standard = analysis.ShortTerm.ToArray(); var originalProfile = analysis.ShortTerm;
        var energy = analysis.LevelingEnergy;
        Require(energy != null && energy.FullBandEnergy.Count == Duration * 10 &&
            energy.HighPassedEnergy.Count == energy.FullBandEnergy.Count && energy.FramesPerBin.Sum() == fixture.Samples.Length &&
            energy.RetainedBytes >= energy.FullBandEnergy.Count * 20L, "Analysis omitted its bounded 100 ms detector energies.");
        var cached = LevelingDetector.GetProfile(analysis, .25);
        Require(ReferenceEquals(cached, LevelingDetector.GetProfile(analysis, .25)), "Repeated unchanged detector settings rebuilt their cached profile.");
        foreach (double sensitivity in new[] { .25, .5, .75, 1 })
        {
            var profile = LevelingDetector.GetProfile(analysis, sensitivity);
            Require(profile.ShortTerm.Count == standard.Length && profile.ShortTerm.Select(point => point.Time).SequenceEqual(standard.Select(point => point.Time)),
                "Bass detection shifted the standard measurement timeline.");
            Require(analysis.IntegratedLufs == integrated && analysis.LoudnessRange == range && analysis.TruePeakDb == peak &&
                ReferenceEquals(analysis.ShortTerm, originalProfile) && analysis.ShortTerm.SequenceEqual(standard) && ReferenceEquals(analysis.LevelingEnergy, energy),
                "Changing detector sensitivity changed standard LUFS/LRA/true peak or cached source analysis.");
        }
        bool immutable = false;
        try { ((IList<double>)energy!.FullBandEnergy)[0] = -1; }
        catch (NotSupportedException) { immutable = true; }
        Require(immutable && energy!.FullBandEnergy[0] >= 0, "Public detector energy summaries were mutable.");
    }

    private static void LegacyNormal()
    {
        var analysis = Kick60.Value.Analysis;
        var legacy = new AnalysisResult { IntegratedLufs = analysis.IntegratedLufs, TruePeakDb = analysis.TruePeakDb,
            LoudnessRange = analysis.LoudnessRange, ShortTerm = analysis.ShortTerm.ToList() };
        var normal = LevelingDetector.GetProfile(analysis, 1);
        Require(!normal.UsesReducedBass && normal.ReferenceLoudness == analysis.IntegratedLufs &&
            ReferenceEquals(normal.ShortTerm, analysis.ShortTerm) && normal.ShortTerm.SequenceEqual(analysis.ShortTerm) &&
            EnvelopeDesigner.Design(legacy, Duration, Settings(.25)).SequenceEqual(EnvelopeDesigner.Design(legacy, Duration, Settings(1))),
            "Normal changed standard measurements, or legacy analysis without detector detail changed its historical suggestions.");
        // Cached 100 ms evidence now improves sustained step handling even in
        // Normal mode; its analyzed envelope intentionally need not equal the
        // no-energy legacy fallback. The legacy fallback itself remains exact.
        // Fixed legacy output captured from the pre-bass section designer. This
        // independent profile catches changes beyond merely selecting its input.
        var profile = new AnalysisResult { IntegratedLufs = -20,
            ShortTerm = Enumerable.Range(0, 60).Select(i => new LoudnessFrame(i + .5, i < 20 || i >= 40 ? -24 : -12)).ToList() };
        EnvelopePoint[] expected = [new(0, 3), new(14.5, 3), new(15.5, 2.64), new(16.5, 1.92), new(20.5, -3.84),
            new(21.5, -4.92), new(23.5, -6), new(34.5, -6), new(35.5, -5.64), new(37.5, -3.84), new(41.5, 1.92), new(43.5, 3), new(60, 3)];
        var actual = EnvelopeDesigner.Design(profile, Duration, Settings(1));
        Require(actual.Count == expected.Length, "Normal sensitivity changed the legacy envelope node count.");
        for (int i = 0; i < expected.Length; i++)
        { Near(actual[i].Time, expected[i].Time, 1e-9, "Legacy envelope timestamp"); Near(actual[i].GainDb, expected[i].GainDb, 1e-9, "Legacy envelope gain"); }
    }

    private static void BypassAudio()
    {
        var fixture = Kick45.Value;
        var normal = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(1));
        var reduced = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings(.25));
        foreach (bool enabled in new[] { false, true })
        {
            var bypass = Settings(.25) with { LevelingEnabled = enabled, Strength = 0, MatchingEnabled = false, LimiterEnabled = false };
            var a = AudioProcessor.Render(fixture.Samples, Rate, 1, reduced, 0, bypass);
            var b = AudioProcessor.Render(fixture.Samples, Rate, 1, normal, 0, bypass with { BassSensitivity = 1 });
            Require(a.Samples.SequenceEqual(fixture.Samples) && a.Samples.SequenceEqual(b.Samples) &&
                a.OutputLufs == b.OutputLufs && a.TruePeakDb == b.TruePeakDb && a.MatchGainDb == b.MatchGainDb,
                "A detector-only preference filtered or otherwise changed bypassed PCM or meters.");
        }
    }

    private static void BoundsAndCancellation()
    {
        Require(new ProcessingSettings().BassSensitivity == .25, "New processing settings lost reduced-bass default.");
        var analysis = Kick45.Value.Analysis;
        foreach (double invalid in new[] { .249, 1.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Throws<ArgumentOutOfRangeException>(() => LevelingDetector.GetProfile(analysis, invalid));
            Throws<ArgumentOutOfRangeException>(() => AudioProcessor.Render(new float[1200], Rate, 1, [], 0, Settings(invalid)));
        }
        foreach (double endpoint in new[] { .25, 1.0 })
        {
            LevelingDetector.GetProfile(analysis, endpoint);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Throws<OperationCanceledException>(() => LevelingDetector.GetProfile(analysis, endpoint, cts.Token));
            Throws<OperationCanceledException>(() => LevelingDetector.GetProfile(analysis, Settings(endpoint), cts.Token));
        }
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

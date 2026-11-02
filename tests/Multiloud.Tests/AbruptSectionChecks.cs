using Multiloud.Core;

internal static class AbruptSectionChecks
{
    private const int Rate = 12000;
    private const double Duration = 60, Change = 20.37;
    private const double Quiet = .035, Loud = .14;
    private sealed record Fixture(float[] Samples, AnalysisResult Analysis);
    private static readonly Lazy<Fixture> Rising = new(() => Analyze(ProgramLevel(true, false)));
    private static readonly Lazy<Fixture> Falling = new(() => Analyze(ProgramLevel(false, false)));
    private static ProcessingSettings Settings(double bass = .25) => new()
    {
        BassSensitivity = bass, MaxLevelingDb = 6, TransitionSeconds = 16,
        MatchingEnabled = false, LimiterEnabled = false, Strength = 1,
    };
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Sustained abrupt changes are localized from cached 100 ms evidence in both directions", Localization),
        ("Confirmed steps receive fast continuous directional ramps while ordinary drift stays slow", FastVersusGradual),
        ("Isolated hits and short breaks do not receive fast section correction", TransientRejection),
        ("Fades, silence and bass-only spectral additions do not create fast false steps", MusicalGuards),
        ("A valid late step cannot overwrite protected fade or silence inside its bridge", StepThenProtectedTail),
        ("Detailed section detection preserves source meters and honors cancellation", MeterAndCancellation),
        ("Retargeting suggestions preserves manual dB offsets and source nodes", RetargetManualOffsets),
    ];
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double value, double expected, double tolerance, string message)
        => Require(double.IsFinite(value) && Math.Abs(value - expected) <= tolerance,
            $"{message}: expected {expected:F5} ± {tolerance:F5}, got {value:F5}.");
    private static Fixture Analyze(float[] samples) => new(samples, LoudnessAnalyzer.Analyze(samples, Rate, 1));
    private static double Tone(double time) => .7 * Math.Sin(2 * Math.PI * 500 * time)
        + .2 * Math.Sin(2 * Math.PI * 1500 * time) + .1 * Math.Sin(2 * Math.PI * 2500 * time);
    private static float[] Signal(Func<double, double> level)
        => Enumerable.Range(0, (int)(Duration * Rate)).Select(frame =>
        {
            double time = frame / (double)Rate;
            return (float)(level(time) * Tone(time));
        }).ToArray();
    private static float[] ProgramLevel(bool rising, bool gradual)
        => Signal(time =>
        {
            double progress = gradual ? Math.Clamp((time - Change + 6) / 12, 0, 1) : time < Change ? 0 : 1;
            if (!rising) progress = 1 - progress;
            return Quiet * Math.Pow(Loud / Quiet, progress);
        });
    private static AnalysisResult Legacy(AnalysisResult source) => new()
    {
        IntegratedLufs = source.IntegratedLufs, LoudnessRange = source.LoudnessRange,
        TruePeakDb = source.TruePeakDb, ShortTerm = source.ShortTerm.ToList(),
    };
    private static double Gain(IReadOnlyList<EnvelopePoint> curve, double time) => AudioProcessor.EnvelopeGainAt(curve, time);
    private static double ResponseFraction(IReadOnlyList<EnvelopePoint> curve)
        => Math.Abs(Gain(curve, Change + 1.5) - Gain(curve, Change - 1.5)) /
            Math.Max(.001, Math.Abs(Gain(curve, Change + 10) - Gain(curve, Change - 10)));

    private static void Localization()
    {
        foreach (var (fixture, rising) in new[] { (Rising.Value, true), (Falling.Value, false) })
        foreach (double bass in new[] { .25, 1.0 })
        {
            var settings = Settings(bass);
            var detail = LevelingDetector.GetProfile(fixture.Analysis, settings).Detail;
            Require(detail.Count == (int)(Duration * 10), "The section detector did not expose its cached 100 ms evidence.");
            for (int i = 0; i < detail.Count; i++)
            {
                Near(detail[i].Time, i * .1, 1e-8, "Detail-bin start time");
                Near(detail[i].Duration, .1, 1e-8, "Detail-bin duration");
                Require(double.IsFinite(detail[i].Loudness + detail[i].FullBandLoudness + detail[i].HighPassedLoudness) &&
                    detail[i].BassConfidence is >= 0 and <= 1, "Detail evidence contains invalid levels or confidence.");
            }
            var steps = EnvelopeDesigner.DetectAbruptSections(fixture.Analysis, Duration, settings);
            Require(steps.Count == 1, $"A single sustained step produced {steps.Count} section boundaries.");
            Near(steps[0].Time, Change, .2, "Abrupt section location");
            Near(steps[0].AfterLoudness - steps[0].BeforeLoudness,
                (rising ? 1 : -1) * 20 * Math.Log10(Loud / Quiet), .3, "Persistent before/after contrast");
        }
    }

    private static void FastVersusGradual()
    {
        foreach (var (fixture, rising) in new[] { (Rising.Value, true), (Falling.Value, false) })
        {
            var gradual = Analyze(ProgramLevel(rising, true));
            foreach (double bass in new[] { .25, 1.0 })
            {
                var settings = Settings(bass);
                var fast = EnvelopeDesigner.Design(fixture.Analysis, Duration, settings);
                var old = EnvelopeDesigner.Design(Legacy(fixture.Analysis), Duration, settings);
                double response = ResponseFraction(fast), legacyResponse = ResponseFraction(old);
                Require(response > .75 && response > legacyResponse + .35,
                    $"A confirmed step retained the slow N-second ramp: detailed={response:F3}, legacy={legacyResponse:F3}.");
                double before = Gain(fast, Change - 10), after = Gain(fast, Change + 10);
                Require(rising ? before > after + 3 : after > before + 3, "Step correction has the wrong direction or no meaningful gain change.");
                double atStep = (Gain(fast, Change) - before) / (after - before);
                Require(rising ? atStep > .55 : atStep < .45,
                    $"A loud step was attenuated late or a quiet step was pre-boosted: fraction={atStep:F3}.");
                CheckCurve(fast, allowStep: true);
                Require(EnvelopeDesigner.DetectAbruptSections(gradual.Analysis, Duration, settings).Count == 0,
                    "A twelve-second musical level transition was classified as an abrupt step.");
                var slow = EnvelopeDesigner.Design(gradual.Analysis, Duration, settings);
                Require(ResponseFraction(slow) < .6, "A gradual section change was forced into a fast ramp.");
                CheckCurve(slow, allowStep: false);
                Console.WriteLine($"METRIC {(rising ? "rising" : "falling")} bass={bass:F2} step response={response:F3}, legacy={legacyResponse:F3}, gradual={ResponseFraction(slow):F3}, atStep={atStep:F3}");
            }
        }
    }
    private static void CheckCurve(IReadOnlyList<EnvelopePoint> curve, bool allowStep)
    {
        Require(curve.Count >= 2 && curve[0].Time == 0 && curve[^1].Time == Duration &&
            curve.All(point => double.IsFinite(point.Time + point.GainDb) && point.Time >= 0 && point.Time <= Duration && Math.Abs(point.GainDb) <= 6.000001),
            "Step handling lost finite gain/time bounds or file endpoints.");
        for (int i = 1; i < curve.Count; i++)
        {
            double dt = curve[i].Time - curve[i - 1].Time;
            Require(dt > 0, "Step handling introduced duplicate or reversed envelope times.");
            double slope = Math.Abs(curve[i].GainDb - curve[i - 1].GainDb) / dt;
            bool local = allowStep && curve[i - 1].Time >= Change - 2 && curve[i].Time <= Change + 2;
            Require(slope <= .750001 || local && slope <= 6.000001,
                $"A gain ramp accelerated outside the step or exceeded its safe slew: {slope:F4} dB/s.");
            double left = Gain(curve, curve[i].Time - 1.0 / Rate), right = Gain(curve, curve[i].Time + 1.0 / Rate);
            Require(Math.Abs(right - left) <= .0011, "The envelope has a gain discontinuity at an editable step node.");
        }
    }

    private static void TransientRejection()
    {
        foreach (double length in new[] { .08, .7, 2.0 })
        {
            var hit = Analyze(Signal(time => time >= Change && time < Change + length ? Loud : Quiet));
            var gap = Analyze(Signal(time => time >= Change && time < Change + length ? 0 : Loud));
            foreach (var fixture in new[] { hit, gap })
            {
                Require(EnvelopeDesigner.DetectAbruptSections(fixture.Analysis, Duration, Settings()).Count == 0,
                    $"A {length:F2}s hit or short break was mistaken for a sustained section.");
                var curve = EnvelopeDesigner.Design(fixture.Analysis, Duration, Settings());
                CheckCurve(curve, allowStep: false);
                if (ReferenceEquals(fixture, gap))
                    Require(Gain(curve, Change + length / 2) <= .000001, "A short silent break received positive gain.");
            }
        }
    }

    private static void MusicalGuards()
    {
        var fade = Analyze(Signal(time => Loud * Math.Clamp((Duration - time) / 15, 0, 1)));
        var silence = Analyze(new float[(int)(Duration * Rate)]);
        var spectral = Analyze(Enumerable.Range(0, (int)(Duration * Rate)).Select(frame =>
        {
            double time = frame / (double)Rate;
            return (float)(Quiet * Tone(time) + (time >= Change && time < Change + 20 ? .35 * Math.Sin(2 * Math.PI * 45 * time) : 0));
        }).ToArray());
        foreach (double bass in new[] { .25, 1.0 })
        foreach (var fixture in new[] { fade, silence, spectral })
        {
            var settings = Settings(bass);
            Require(EnvelopeDesigner.DetectAbruptSections(fixture.Analysis, Duration, settings).Count == 0,
                "A fade, silence, or bass-only spectral addition caused fast step correction.");
            var curve = EnvelopeDesigner.Design(fixture.Analysis, Duration, settings);
            CheckCurve(curve, allowStep: false);
            if (ReferenceEquals(fixture, fade))
                Require(Gain(curve, 58.5) <= .1 && Gain(curve, Duration) <= .1, "A terminal fade was boosted by step handling.");
            if (ReferenceEquals(fixture, silence))
                Require(curve.All(point => point.GainDb == 0), "Silent audio acquired a suggested gain.");
        }
    }

    private static void MeterAndCancellation()
    {
        var analysis = Rising.Value.Analysis;
        var standard = analysis.ShortTerm; var saved = standard.ToArray(); var energy = analysis.LevelingEnergy;
        double integrated = analysis.IntegratedLufs, lra = analysis.LoudnessRange, peak = analysis.TruePeakDb;
        foreach (double bass in new[] { .25, 1.0 })
        {
            EnvelopeDesigner.DetectAbruptSections(analysis, Duration, Settings(bass));
            EnvelopeDesigner.Design(analysis, Duration, Settings(bass));
            Require(analysis.IntegratedLufs == integrated && analysis.LoudnessRange == lra && analysis.TruePeakDb == peak &&
                ReferenceEquals(analysis.ShortTerm, standard) && standard.SequenceEqual(saved) && ReferenceEquals(analysis.LevelingEnergy, energy),
                "Detailed section detection changed standard LUFS/LRA/true peak or retained source analysis.");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            bool cancelled = false;
            try { EnvelopeDesigner.DetectAbruptSections(analysis, Duration, Settings(bass), cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Cached section detection ignored cancellation.");
        }
        Require(EnvelopeDesigner.DetectAbruptSections(Legacy(analysis), Duration, Settings()).Count == 0,
            "Legacy analysis invented detailed evidence that it does not retain.");
    }

    private static void StepThenProtectedTail()
    {
        const double lateStep = 50.03;
        foreach (bool fade in new[] { true, false })
        {
            var fixture = Analyze(Signal(time => time < lateStep ? Loud : time < 55 ? Quiet :
                fade ? Quiet * Math.Clamp((Duration - time) / 5, 0, 1) : 0));
            var settings = Settings();
            var steps = EnvelopeDesigner.DetectAbruptSections(fixture.Analysis, Duration, settings);
            Require(steps.Any(step => Math.Abs(step.Time - lateStep) <= .2),
                "The late-step safety fixture did not contain a qualified sustained change.");
            var curve = EnvelopeDesigner.Design(fixture.Analysis, Duration, settings);
            foreach (double time in new[] { 57.5, 58.5, 59.5, Duration })
                Require(Gain(curve, time) <= .000001,
                    $"A fast step bridge replaced a protected {(fade ? "fade" : "silent")} tail with positive gain at {time:F1}s.");
            Require(curve[0].Time == 0 && curve[^1].Time == Duration &&
                curve.All(point => double.IsFinite(point.Time + point.GainDb) && point.Time >= 0 && point.Time <= Duration && Math.Abs(point.GainDb) <= 6.000001),
                "Late-step protection lost finite time/gain bounds.");
            for (int i = 1; i < curve.Count; i++)
            {
                double dt = curve[i].Time - curve[i - 1].Time;
                Require(dt > 0 && Math.Abs(curve[i].GainDb - curve[i - 1].GainDb) / dt <= 6.000001 &&
                    Math.Abs(Gain(curve, curve[i].Time + 1.0 / Rate) - Gain(curve, curve[i].Time - 1.0 / Rate)) <= .0011,
                    "Restoring a protected tail introduced an instantaneous or unsafe gain discontinuity.");
            }
        }
    }

    private static void RetargetManualOffsets()
    {
        const double duration = 40;
        EnvelopePoint[] oldSuggestion = [new(0, 0), new(20, 4), new(40, 0)];
        EnvelopePoint[] nextSuggestion = [new(0, 1), new(10, -2), new(30, 3), new(40, -1)];
        EnvelopePoint[] manual = [new(0, 1), new(5, 3), new(17, -2), new(40, .5)];
        var authored = TransitionDesigner.Combine(oldSuggestion, manual);
        var saved = authored.ToArray(); var oldSaved = oldSuggestion.ToArray(); var nextSaved = nextSuggestion.ToArray();
        var updated = EnvelopeDesigner.Retarget(authored, oldSuggestion, nextSuggestion, duration);
        var returned = EnvelopeDesigner.Retarget(updated, nextSuggestion, oldSuggestion, duration);
        for (double time = 0; time <= duration; time += .125)
        {
            Near(Gain(updated, time) - Gain(nextSuggestion, time), Gain(authored, time) - Gain(oldSuggestion, time), 1e-9,
                "Retargeting erased or doubled a manual dB offset");
            Near(Gain(returned, time), Gain(authored, time), 1e-9, "Changing suggestions and returning was not reversible");
        }
        Require(authored.SequenceEqual(saved) && oldSuggestion.SequenceEqual(oldSaved) && nextSuggestion.SequenceEqual(nextSaved),
            "Retargeting mutated authored nodes or cached old/new suggestions.");
        EnvelopePoint[] inserted = [new(0, 1), new(10, 3), new(20, 5), new(40, 1)];
        Require(EnvelopeDesigner.Retarget(inserted, oldSuggestion, oldSuggestion, duration).SequenceEqual(inserted),
            "An unchanged suggestion discarded an intentionally inserted authored node.");
        var initial = EnvelopeDesigner.Retarget([], [], nextSuggestion, duration);
        for (double time = 0; time <= duration; time += .25)
            Near(Gain(initial, time), Gain(nextSuggestion, time), 1e-9, "Initial suggestion retargeting changed its effective curve");
    }
}

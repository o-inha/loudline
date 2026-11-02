using Multiloud.Core;

internal static class PeakDynamicsGuardChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Dynamics safeguard measures sustained music energy and rejects isolated attacks", Measurement),
        ("Dynamics safeguard backs off sustained music limiting while retaining true-peak safety", SustainedCap),
        ("Dynamics safeguard does not lower a whole track for one short hit over quiet music", IsolatedHit),
        ("Zero sustained-reduction limit safely backs off rather than disabling peak protection", ZeroCap),
        ("Dynamics safeguard control edits match fresh rendering and retain immutable previews", Incremental),
        ("Inactive peak controls preserve audio while reporting finite clamped settings", BypassedMetadata),
    ];

    private const int Rate = 8000;
    private static ProcessingSettings Settings => new()
    {
        LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = true,
        BassProtection = 0, CeilingDb = -1, MaximizerCharacter = .5,
        TransientPreservation = .5, DynamicsGuardEnabled = false, MaxSustainedReductionDb = 2
    };

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:R}, got {actual:R}.");

    private static float[] Fixture()
    {
        var samples = new float[Rate * 3 * 2];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            double time = frame / (double)Rate;
            double section = frame < Rate ? 1 : .28;
            float value = (float)(section * (.52 * Math.Sin(2 * Math.PI * 80 * time) + .23 * Math.Sin(2 * Math.PI * 997 * time)));
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = value * .7f;
        }
        return samples;
    }

    private static RenderResult Render(float[] source, ProcessingSettings settings, double trim = 12)
        => AudioProcessor.Render(source, Rate, 2, [], trim, settings);

    private static void Measurement()
    {
        var source = Enumerable.Repeat(.25f, Rate * 2).ToArray();
        var attenuated = source.Select(value => value * .5f).ToArray();
        Near(PeakDynamicsGuard.MeasureSustainedReductionDb(source, attenuated, 1, Rate, 1, CancellationToken.None),
            20 * Math.Log10(2), 1e-9, "Sustained energy attenuation");
        Near(PeakDynamicsGuard.MeasureSustainedReductionDb(source, source, 1, Rate, 1, CancellationToken.None),
            0, 0, "Unchanged energy");

        var shortAttack = Enumerable.Repeat(.0001f, Rate * 2).ToArray();
        var shortResult = (float[])shortAttack.Clone();
        for (int frame = Rate; frame < Rate + Rate / 50; frame++)
        {
            shortAttack[frame] = 2;
            shortResult[frame] = .5f;
        }
        Near(PeakDynamicsGuard.MeasureSustainedReductionDb(shortAttack, shortResult, 1, Rate, 1, CancellationToken.None),
            0, 0, "Twenty-millisecond attack was classified as sustained limiting");
    }

    private static void SustainedCap()
    {
        var source = Fixture();
        var uncheckedResult = Render(source, Settings);
        var settings = Settings with { DynamicsGuardEnabled = true, MaxSustainedReductionDb = 2 };
        var guarded = Render(source, settings);
        Require(uncheckedResult.SustainedReductionDb > 4, "Fixture did not cause sustained heavy limiting.");
        Require(guarded.DynamicsGuardReductionDb > 2, "Safeguard did not reduce drive for sustained heavy limiting.");
        Require(guarded.SustainedReductionDb <= settings.MaxSustainedReductionDb + .03,
            "Safeguard exceeded the configured sustained-reduction limit.");
        Require(guarded.OutputLufs < uncheckedResult.OutputLufs - .5,
            "Safeguard did not prefer a quieter result over sustained heavy limiting.");
        double measured = PeakDynamicsGuard.MeasureSustainedReductionDb(source, guarded.Samples,
            Math.Pow(10, (guarded.MatchGainDb + 12) / 20), Rate, 2, CancellationToken.None);
        Near(guarded.SustainedReductionDb, measured, 1e-9, "Published sustained-reduction measurement");
        Require(guarded.TruePeakDb <= settings.CeilingDb + .02, "Safeguard abandoned peak protection.");
        Require(guarded.Warning?.Contains("safeguard", StringComparison.OrdinalIgnoreCase) == true,
            "Safeguard backoff was not explained in track feedback.");
    }

    private static void IsolatedHit()
    {
        var source = new float[Rate * 3 * 2];
        for (int frame = 0; frame < source.Length / 2; frame++)
        {
            double time = frame / (double)Rate;
            double value = .0001 * Math.Sin(2 * Math.PI * 330 * time);
            if (frame >= Rate && frame < Rate + Rate / 40)
            {
                double age = (frame - Rate) / (double)Rate;
                value += 2 * Math.Sin(2 * Math.PI * 880 * age) * Math.Pow(Math.Sin(Math.PI * age / .025), 2);
            }
            source[frame * 2] = (float)value;
            source[frame * 2 + 1] = (float)(value * .7);
        }
        var ordinary = Render(source, Settings, 0);
        var guarded = Render(source, Settings with { DynamicsGuardEnabled = true, MaxSustainedReductionDb = 0 }, 0);
        Require(ordinary.MaxReductionDb > 3, "Short-hit fixture did not exercise peak protection.");
        Near(guarded.DynamicsGuardReductionDb, 0, 0, "Short hit caused global drive reduction");
        Require(guarded.Samples.AsSpan().SequenceEqual(ordinary.Samples),
            "Safeguard altered the whole track for an isolated short hit.");
    }

    private static void ZeroCap()
    {
        var source = Fixture();
        var settings = Settings with { DynamicsGuardEnabled = true, MaxSustainedReductionDb = 0 };
        var guarded = Render(source, settings, 18);
        Require(guarded.DynamicsGuardReductionDb > 6, "Zero cap did not back off an overloaded signal.");
        Require(guarded.SustainedReductionDb <= .03, "Zero cap retained meaningful sustained limiting.");
        Require(guarded.TruePeakDb <= settings.CeilingDb + .02, "Zero cap allowed a true-peak overshoot.");
        Require(guarded.Samples.Length == source.Length && guarded.Samples.All(float.IsFinite),
            "Zero cap changed duration or produced invalid audio.");
    }

    private static void Equal(RenderResult actual, RenderResult expected)
    {
        Require(actual.Samples.AsSpan().SequenceEqual(expected.Samples), "Incremental safeguard PCM differs from a fresh render.");
        Require(actual.MatchGainDb == expected.MatchGainDb && actual.OutputLufs == expected.OutputLufs &&
            actual.TruePeakDb == expected.TruePeakDb && actual.DynamicsGuardReductionDb == expected.DynamicsGuardReductionDb &&
            actual.SustainedReductionDb == expected.SustainedReductionDb &&
            actual.RenderedDynamicsGuardEnabled == expected.RenderedDynamicsGuardEnabled &&
            actual.RenderedMaxSustainedReductionDb == expected.RenderedMaxSustainedReductionDb,
            "Incremental safeguard metadata differs from a fresh render.");
    }

    private static void Incremental()
    {
        var source = Fixture(); var previous = Render(source, Settings);
        var snapshot = (float[])previous.Samples.Clone();
        foreach (double cap in new[] { 2d, 0d, 5d })
        {
            var settings = Settings with { DynamicsGuardEnabled = true, MaxSustainedReductionDb = cap };
            var incremental = AudioProcessor.RenderIncremental(source, Rate, 2, [], 12, settings, previous);
            Equal(incremental, Render(source, settings));
            Require(incremental.Work.UsedIncrementalStage && incremental.Work.LevelingFramesProcessed == 0 &&
                incremental.Work.DetectorFramesProcessed == 0, "Safeguard controls rebuilt unchanged pre-match stages.");
            var repeated = AudioProcessor.RenderIncremental(source, Rate, 2, [], 12, settings, incremental);
            Require(ReferenceEquals(repeated.Samples, incremental.Samples) && repeated.Work.LoudnessFramesProcessed == 0,
                "Identical safeguard settings rebuilt an already-current result.");
        }
        Require(previous.Samples.AsSpan().SequenceEqual(snapshot), "Safeguard edits mutated a retained preview.");
        var unusedCap = AudioProcessor.RenderIncremental(source, Rate, 2, [], 12,
            Settings with { MaxSustainedReductionDb = 0 }, previous);
        Require(ReferenceEquals(unusedCap.Samples, previous.Samples) && unusedCap.RenderedMaxSustainedReductionDb == 0,
            "Changing the disabled safeguard did DSP work or left stale settings metadata.");
    }

    private static void BypassedMetadata()
    {
        var source = Fixture();
        var bypass = Settings with { LimiterEnabled = false, DynamicsGuardEnabled = true };
        var first = Render(source, bypass with { BassProtection = 7, MaxSustainedReductionDb = 99 }, 0);
        Require(first.Samples.AsSpan().SequenceEqual(source), "Bypassed peak controls modified audio.");
        Near(first.RenderedBassProtection, 1, 0, "Clamped bypassed bass setting");
        Near(first.RenderedMaxSustainedReductionDb, 12, 0, "Clamped bypassed safeguard setting");
        Near(first.DynamicsGuardReductionDb, 0, 0, "Bypassed safeguard drive reduction");
        Near(first.SustainedReductionDb, 0, 0, "Bypassed sustained reduction");
        var second = AudioProcessor.RenderIncremental(source, Rate, 2, [], 0,
            bypass with { BassProtection = -1, MaxSustainedReductionDb = -1 }, first);
        Require(ReferenceEquals(first.Samples, second.Samples), "Inactive peak settings rebuilt unchanged audio.");
        Near(second.RenderedBassProtection, 0, 0, "Lower clamped bass setting");
        Near(second.RenderedMaxSustainedReductionDb, 0, 0, "Lower clamped safeguard setting");
        foreach (var invalid in new[]
        {
            bypass with { BassProtection = double.NaN },
            bypass with { MaxSustainedReductionDb = double.PositiveInfinity }
        })
        {
            bool rejected = false;
            try { Render(source, invalid, 0); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "A non-finite inactive peak setting was accepted.");
        }
    }
}

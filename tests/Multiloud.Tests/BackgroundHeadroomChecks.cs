using Multiloud.Core;

internal static class BackgroundHeadroomChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("An isolated background peak does not attenuate unrelated music sections", IsolatedBackgroundPeak),
        ("Local background headroom preserves fixed PCM and independently checked true peaks", FixedBedAndTruePeaks),
        ("Background dynamics protection prefers lower drive to sustained limiting", DynamicsProtection),
        ("A near-ceiling background peak never mutes unrelated music", NearCeilingBackground),
        ("Post-addition peak repair ducks only local music and retains the exact bed", LocalMixedRepair),
        ("Background peak budgets remain compact and cancellable", CompactAndCancellation),
    ];

    private const int Rate = 12000;
    private const int Channels = 2;
    private static ProcessingSettings Settings => new()
    {
        LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = true,
        BassProtection = 0, CeilingDb = -1,
    };

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static float[] Tone(double seconds, double amplitude, double frequency)
    {
        var samples = new float[(int)Math.Round(seconds * Rate) * Channels];
        for (int frame = 0; frame < samples.Length / Channels; frame++)
        {
            float sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / Rate));
            samples[frame * Channels] = sample;
            samples[frame * Channels + 1] = sample * .73f;
        }
        return samples;
    }

    private static void Safe(RenderResult result, double ceiling)
    {
        double measured = LoudnessAnalyzer.Analyze(result.Samples, Rate, Channels).TruePeakDb;
        Require(measured <= ceiling + .02, $"Final reconstructed mix peak exceeded the ceiling: {measured:R} dBTP");
        Require(Math.Abs(measured - result.TruePeakDb) < .001, "Peak metadata does not describe final PCM");
    }

    private static void IsolatedBackgroundPeak()
    {
        var music = Tone(4, .55, 1000);
        var bed = Tone(4, .002, 233);
        int center = 2 * Rate;
        for (int frame = center - Rate / 80; frame <= center + Rate / 80; frame++)
        {
            double time = (frame - center) / (double)Rate;
            float burst = (float)(.80 * Math.Exp(-Math.Pow(time / .004, 2)) * Math.Cos(2 * Math.PI * 500 * time));
            bed[frame * Channels] += burst;
            bed[frame * Channels + 1] += burst;
        }
        var originalBed = (float[])bed.Clone();
        var result = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, bed);
        Require(result.MaxReductionDb > 1, "The isolated background fixture did not require music peak protection");
        double worst = 0;
        for (int frame = Rate / 2; frame < Rate; frame++)
            for (int channel = 0; channel < Channels; channel++)
            {
                int index = frame * Channels + channel;
                worst = Math.Max(worst, Math.Abs(result.Samples[index] - (float)(music[index] + bed[index])));
            }
        Require(worst < 1e-6, $"A later background peak attenuated an unrelated earlier section: {worst:R}");
        Require(bed.AsSpan().SequenceEqual(originalBed), "Peak protection mutated fixed background PCM");
        Require(result.SafetyAttenuationDb < .01, "An isolated background peak triggered a whole-track safety trim");
        Safe(result, Settings.CeilingDb);
    }

    private static void FixedBedAndTruePeaks()
    {
        var music = Tone(3, .95, 55);
        var bed = Tone(3, .22, 233);
        for (int frame = 0; frame < music.Length / Channels; frame++)
        {
            double time = frame / (double)Rate;
            float hit = (float)(.7 * Math.Exp(-Math.Pow((time - 1.3) / .001, 2)));
            music[frame * Channels] += hit;
            music[frame * Channels + 1] -= hit * .5f;
            if (time is > 2 and < 2.5)
                music[frame * Channels] = music[frame * Channels + 1] = 0;
        }
        var savedMusic = (float[])music.Clone();
        var savedBed = (float[])bed.Clone();
        foreach (double bass in new[] { 0d, .75 })
        {
            var result = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0,
                Settings with { BassProtection = bass }, bed);
            for (int frame = (int)(2.1 * Rate); frame < (int)(2.4 * Rate); frame++)
                for (int channel = 0; channel < Channels; channel++)
                {
                    int index = frame * Channels + channel;
                    Require(result.Samples[index] == bed[index], "Music processing changed the bed in a music gap");
                }
            Require(result.BackgroundSamples != null && result.BackgroundSamples.AsSpan().SequenceEqual(savedBed),
                "The rendered background reference was altered");
            Require(music.AsSpan().SequenceEqual(savedMusic) && bed.AsSpan().SequenceEqual(savedBed),
                "Rendering mutated immutable source arrays");
            Safe(result, Settings.CeilingDb);
        }
    }

    private static void DynamicsProtection()
    {
        var music = Tone(4, 2, 500);
        var bed = Tone(4, .02, 233);
        var unrestricted = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, bed);
        var guarded = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0,
            Settings with { DynamicsGuardEnabled = true, MaxSustainedReductionDb = 1 }, bed);
        Require(guarded.DynamicsGuardReductionDb > 3, "The dynamics guard did not reduce an excessive music drive");
        Require(guarded.SustainedReductionDb <= 1.1,
            $"The guarded music retained excessive sustained reduction: {guarded.SustainedReductionDb:R} dB");
        Require(guarded.OutputLufs <= unrestricted.OutputLufs + .05,
            $"The dynamics guard made the output louder while reducing excessive limiting: {unrestricted.OutputLufs:R} to {guarded.OutputLufs:R} LUFS");
        Require(guarded.BackgroundSamples!.AsSpan().SequenceEqual(bed), "Dynamics protection attenuated the fixed background");
        Safe(guarded, Settings.CeilingDb);
    }

    private static (float[] Music, float[] Background) NearCeilingFixture()
    {
        double ceiling = Math.Pow(10, Settings.CeilingDb / 20);
        float plateau = (float)ceiling;
        if (plateau > ceiling) plateau = MathF.BitDecrement(plateau);
        var music = Tone(4, .2, 500);
        var bed = new float[music.Length];
        for (int frame = 0; frame < music.Length / Channels; frame++)
        {
            double time = frame / (double)Rate;
            double shape = time is >= 1.95 and <= 2.05 ? 1 :
                time is > 1.45 and < 1.95 ? .5 - .5 * Math.Cos(Math.PI * (time - 1.45) / .5) :
                time is > 2.05 and < 2.55 ? .5 + .5 * Math.Cos(Math.PI * (time - 2.05) / .5) : 0;
            for (int channel = 0; channel < Channels; channel++)
            {
                int index = frame * Channels + channel;
                bed[index] = (float)(plateau * shape);
                music[index] = (float)(music[index] * (1 - shape) + 3.3551290422e-8 * shape);
            }
        }
        // Quantizing the otherwise monotonic fade may add a reconstructed peak
        // slightly above the flat portion. Keep the bed independently safe.
        double peak = TruePeakMeter.Measure(bed, Channels, default);
        if (peak > ceiling)
        {
            double scale = ceiling / peak * (1 - 2e-7);
            for (int index = 0; index < bed.Length; index++) bed[index] = (float)(bed[index] * scale);
        }
        Require(TruePeakMeter.Measure(bed, Channels, default) <= ceiling, "Near-ceiling fixture has an unsafe background.");
        return (music, bed);
    }

    private static void NearCeilingBackground()
    {
        var (music, bed) = NearCeilingFixture();
        var snapshot = (float[])bed.Clone();
        var result = AudioProcessor.RenderWithBackground(music, Rate, Channels, [], 0, Settings, bed);
        for (int frame = Rate / 2; frame < Rate; frame++)
            for (int channel = 0; channel < Channels; channel++)
            {
                int index = frame * Channels + channel;
                Require(result.Samples[index] == music[index], "A near-ceiling bed peak altered earlier music.");
            }
        Require(result.Samples.Skip(3 * Rate * Channels).Any(value => Math.Abs(value) > .1),
            "A near-ceiling bed peak muted later music.");
        Require(result.SafetyAttenuationDb < .01, "A local near-ceiling event caused uniform attenuation.");
        Require(bed.AsSpan().SequenceEqual(snapshot), "Near-ceiling protection modified the fixed background.");
        Safe(result, Settings.CeilingDb);
    }

    private static void LocalMixedRepair()
    {
        var (music, bed) = NearCeilingFixture();
        var snapshot = (float[])bed.Clone();
        double ceiling = Math.Pow(10, Settings.CeilingDb / 20);
        // Force the post-addition safety path independently of the conservative
        // input limiter. This contribution remains tiny and localized.
        for (int frame = (int)(1.95 * Rate); frame <= (int)(2.05 * Rate); frame++)
            for (int channel = 0; channel < Channels; channel++)
                music[frame * Channels + channel] = 1e-6f;
        var mix = music.Select((value, index) => (float)(value + bed[index])).ToArray();
        var before = (float[])mix.Clone();
        Require(TruePeakMeter.Measure(mix, Channels, default) > ceiling,
            "The post-addition roundoff fixture did not exceed the ceiling.");
        var budget = BackgroundPeakHeadroom.Create(bed, Rate, Channels, ceiling, default);
        var gains = Enumerable.Repeat(1f, music.Length / Channels).ToArray();
        budget.RepairMixedRoundoff(mix, bed, gains, Rate, Channels, ceiling, default);
        Require(TruePeakMeter.Measure(mix, Channels, default) <= ceiling,
            "Local post-addition repair did not retain true-peak safety.");
        for (int frame = Rate / 2; frame < Rate; frame++)
            for (int channel = 0; channel < Channels; channel++)
            {
                int index = frame * Channels + channel;
                Require(mix[index] == before[index], "Post-addition repair altered an unrelated earlier section.");
            }
        for (int frame = (int)(1.99 * Rate); frame < (int)(2.01 * Rate); frame++)
            for (int channel = 0; channel < Channels; channel++)
            {
                int index = frame * Channels + channel;
                Require(mix[index] == bed[index], "The fixed bed changed while the local music was fully ducked.");
            }
        Require(bed.AsSpan().SequenceEqual(snapshot), "Post-addition repair modified the background source.");
    }

    private static void CompactAndCancellation()
    {
        var bed = Tone(2, .1, 233);
        var budget = BackgroundPeakHeadroom.Create(bed, Rate, Channels, Math.Pow(10, -.05), default);
        Require(budget.Ceilings.Length <= 2002 && budget.HopFrames == 12,
            "Local headroom allocated a native-frame array rather than compact control bins");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { BackgroundPeakHeadroom.Create(bed, Rate, Channels, .9, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Preparing background headroom ignored cancellation");
    }
}

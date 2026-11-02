using Multiloud.Core;

internal static class SpectralProtectionChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Bass peak control is bit-exact when idle and when disabled", Idle),
        ("Bass peak control preserves more treble while limiting a deep sustained tone", TreblePreservation),
        ("Bass peak control retains stereo linking, duration and reconstructed peak safety", StereoAndSafety),
        ("Bass peak control accepts compact time-varying ceilings and cancellation", LocalBudgets),
        ("Bass peak control is continuous across bounded filter blocks", BlockContinuity),
    ];

    private const int Rate = 12000;
    private static ProcessingSettings Settings => new()
    {
        LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = true,
        BassProtection = 0, CeilingDb = -1, MaximizerCharacter = .5, TransientPreservation = .5
    };

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static float[] Fixture(double seconds = 5, double scale = 1)
    {
        int frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            double time = frame / (double)Rate;
            float value = (float)(scale * (1.28 * Math.Sin(2 * Math.PI * 55 * time) + .065 * Math.Sin(2 * Math.PI * 2100 * time)));
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = value * .37f;
        }
        return samples;
    }

    private static RenderResult Render(float[] samples, double amount)
        => AudioProcessor.Render(samples, Rate, 2, [], 0, Settings with { BassProtection = amount });

    private static void Idle()
    {
        var quiet = Fixture(scale: .02);
        var protectedResult = Render(quiet, 1);
        Require(protectedResult.Samples.AsSpan().SequenceEqual(quiet), "An idle bass stage altered the source.");
        var loud = Fixture();
        var baseline = AudioProcessor.Render(loud, Rate, 2, [], 0, Settings);
        var zero = Render(loud, 0);
        Require(zero.Samples.AsSpan().SequenceEqual(baseline.Samples), "Zero bass amount did not preserve legacy protection.");
    }

    private static double Magnitude(float[] samples, double frequency)
    {
        double real = 0, imaginary = 0;
        int first = Rate, last = Math.Min(samples.Length / 2 - Rate / 2, Rate * 4);
        for (int frame = first; frame < last; frame++)
        {
            double phase = 2 * Math.PI * frequency * frame / Rate;
            real += samples[frame * 2] * Math.Cos(phase);
            imaginary += samples[frame * 2] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(1, last - first);
    }

    private static void TreblePreservation()
    {
        var source = Fixture();
        var broadband = Render(source, 0);
        var bassAware = Render(source, 1);
        double legacyTreble = Magnitude(broadband.Samples, 2100), newTreble = Magnitude(bassAware.Samples, 2100);
        Require(newTreble > legacyTreble * 1.15,
            $"Selective bass control did not preserve more upper-frequency signal ({newTreble:R} versus {legacyTreble:R}).");
        Require(Magnitude(bassAware.Samples, 55) < Magnitude(source, 55), "Bass control did not reduce the overloaded low band.");
    }

    private static void StereoAndSafety()
    {
        var source = Fixture(); var snapshot = (float[])source.Clone();
        foreach (var style in new[] { MaximizerStyle.Transparent, MaximizerStyle.Punchy })
        {
            var result = AudioProcessor.Render(source, Rate, 2, [], 0, Settings with { BassProtection = 1, MaximizerStyle = style });
            Require(result.Samples.Length == source.Length, "Bass control changed duration.");
            Require(source.AsSpan().SequenceEqual(snapshot), "Bass control modified the immutable source.");
            Require(result.Samples.All(float.IsFinite), "Bass control produced a non-finite sample.");
            Require(result.TruePeakDb <= Settings.CeilingDb + .02, "Bass control exceeded the reconstructed peak ceiling.");
            for (int index = 0; index < source.Length; index += 2)
                Require(Math.Abs(result.Samples[index + 1] - result.Samples[index] * .37) < 3e-7,
                    "Bass control changed the stereo relationship.");
        }
    }

    private static void LocalBudgets()
    {
        var source = Fixture(1); int frames = source.Length / 2;
        var detector = new double[frames];
        for (int frame = 0; frame < frames; frame++) detector[frame] = TruePeakMeter.FramePeak(source, 2, frame);
        int hop = 12;
        var budgets = Enumerable.Repeat(.85, (frames + hop - 1) / hop).ToArray();
        for (int bin = budgets.Length / 3; bin < budgets.Length * 2 / 3; bin++) budgets[bin] = .55;
        var output = (float[])source.Clone(); var gains = new float[frames];
        AdaptiveMaximizer.Apply(output, detector, gains, 1, Rate, 2, .9, MaximizerStyle.Transparent, .5, .5,
            CancellationToken.None, 1, budgets, hop);
        for (int frame = 0; frame < frames; frame++)
            Require(Math.Abs(output[frame * 2]) <= budgets[frame / hop] + 1e-5,
                "Bass control ignored a local music peak budget.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool canceled = false;
        try { AdaptiveMaximizer.Apply((float[])source.Clone(), detector, gains, 1, Rate, 2, .9,
            MaximizerStyle.Transparent, .5, .5, cancellation.Token, 1); }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "Bass control ignored cancellation.");
    }

    private static void BlockContinuity()
    {
        // The first bounded filter-block join falls within this sustained tone.
        // Its derivative should be no larger than ordinary neighboring periods.
        var source = Fixture(6); var result = Render(source, 1);
        int boundary = 32768;
        double atJoin = Math.Abs(result.Samples[boundary * 2] - result.Samples[(boundary - 1) * 2]);
        // Both fixture tones repeat after 2400 frames. Compare corresponding
        // derivatives outside the boundary rather than including the join in
        // the reference maximum, which would hide precisely this failure.
        double reference = 0;
        foreach (int frame in new[] { boundary - 4800, boundary - 2400, boundary + 2400, boundary + 4800 })
            reference = Math.Max(reference, Math.Abs(result.Samples[frame * 2] - result.Samples[(frame - 1) * 2]));
        Require(atJoin <= reference * 1.05 + .0001, "Bounded bass filtering introduced a discontinuity.");
    }
}

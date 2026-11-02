using Multiloud.Core;

internal static class LoudnessCacheChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Incremental K-weighting matches a complete scan exactly", LocalUpdate),
        ("K-weighted checkpoints preserve partial blocks and silence", PartialAndSilent),
        ("K-weighted incremental scan honors cancellation", Cancellation),
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Exact(double actual, double expected, string message)
        => Require(BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected),
            $"{message}: expected {expected:R}, got {actual:R}");

    private static float[] Signal(int frames, int rate, int channels)
    {
        var random = new Random(417);
        var samples = new float[frames * channels];
        for (int frame = 0; frame < frames; frame++)
            for (int channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] = (float)(.12 * Math.Sin(2 * Math.PI * (233 + channel * 71) * frame / rate)
                    + .01 * (random.NextDouble() * 2 - 1));
        return samples;
    }

    private static void LocalUpdate()
    {
        const int rate = 48000, channels = 2;
        var source = Signal(120 * rate, rate, channels);
        var original = LoudnessCache.Create(source, rate, channels, default);
        double before = original.IntegratedLufs;
        Exact(before, LoudnessAnalyzer.Measure(source, rate, channels, default).IntegratedLufs, "Cached initial scan");
        var edited = (float[])source.Clone();
        int first = 20 * rate + 37, last = 22 * rate + 81;
        for (int frame = first; frame < last; frame++)
            for (int channel = 0; channel < channels; channel++)
                edited[frame * channels + channel] *= .5f;
        var incremental = LoudnessCache.Update(original, edited, first, last, default);
        var complete = LoudnessCache.Create(edited, rate, channels, default);
        Exact(incremental.IntegratedLufs, complete.IntegratedLufs, "Incremental cache vs complete cache");
        Exact(incremental.IntegratedLufs, LoudnessAnalyzer.Measure(edited, rate, channels, default).IntegratedLufs, "Incremental cache vs reference analyzer");
        Exact(original.IntegratedLufs, before, "Incremental update changed old cache");
        Require(incremental.RecomputedFrames >= last - first && incremental.RecomputedFrames < source.Length / channels / 4,
            $"Local edit reprocessed {incremental.RecomputedFrames} frames of {source.Length / channels}");
        Require(original.RetainedBytes > 0 && incremental.RetainedBytes == original.RetainedBytes, "Cache memory accounting changed unexpectedly");
        var noChange = LoudnessCache.Update(incremental, edited, first, first, default);
        Exact(noChange.IntegratedLufs, incremental.IntegratedLufs, "Empty update changed loudness");
        Require(noChange.RecomputedFrames == 0, "Empty edit performed PCM filtering");
    }

    private static void PartialAndSilent()
    {
        const int rate = 44100;
        foreach (int frames in new[] { 0, 37, rate / 3, rate * 3 + 173 })
        {
            var source = Signal(frames, rate, 1);
            var initial = LoudnessCache.Create(source, rate, 1, default);
            Exact(initial.IntegratedLufs, LoudnessAnalyzer.Measure(source, rate, 1, default).IntegratedLufs, "Partial-block initial scan");
            var changed = (float[])source.Clone();
            int first = frames / 3, last = Math.Min(frames, first + 71);
            for (int frame = first; frame < last; frame++) changed[frame] = 0;
            var updated = LoudnessCache.Update(initial, changed, first, last, default);
            Exact(updated.IntegratedLufs, LoudnessCache.Create(changed, rate, 1, default).IntegratedLufs, "Partial-block incremental scan");
        }
        var silent = new float[rate * 3];
        var silentCache = LoudnessCache.Create(silent, rate, 1, default);
        Exact(silentCache.IntegratedLufs, -70, "Silent cached loudness");
        var silentUpdate = LoudnessCache.Update(silentCache, silent, rate, rate + 1, default);
        Exact(silentUpdate.IntegratedLufs, -70, "Silent updated loudness");
        Require(silentUpdate.RecomputedFrames == rate / 10, "Identical silence did not settle at its first checkpoint");
    }

    private static void Cancellation()
    {
        var samples = Signal(8000, 8000, 1);
        var cache = LoudnessCache.Create(samples, 8000, 1, default);
        using var source = new CancellationTokenSource();
        source.Cancel();
        int cancelled = 0;
        try { LoudnessCache.Create(samples, 8000, 1, source.Token); }
        catch (OperationCanceledException) { cancelled++; }
        try { LoudnessCache.Update(cache, samples, 1, 2, source.Token); }
        catch (OperationCanceledException) { cancelled++; }
        Require(cancelled == 2, "Cached scan did not honor cancellation");
    }
}

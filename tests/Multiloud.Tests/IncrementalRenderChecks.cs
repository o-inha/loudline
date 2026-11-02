using Multiloud.Core;

internal static class IncrementalRenderChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Point moves, additions and removal match a fresh complete render", LocalEdits),
        ("Localized leveling retains immutable history and only updates dependent frames", LocalityAndHistory),
        ("Global matching changes reuse leveling but regenerate globally coupled output", GlobalMatching),
        ("Collinear points cause no incremental stage work", CollinearNoOp),
        ("Incremental rendering falls back safely and honors cancellation", FallbackAndCancellation)
    ];
    private const int Rate = 12000, Seconds = 45;
    private static float[] Fixture()
    {
        var samples = new float[Rate * Seconds * 2];
        for (int f = 0; f < samples.Length / 2; f++)
        {
            double t = f / (double)Rate;
            samples[f * 2] = (float)(.07 * Math.Sin(2 * Math.PI * 330 * t) + .03 * Math.Sin(2 * Math.PI * 907 * t));
            samples[f * 2 + 1] = samples[f * 2] * .8f;
        }
        for (int f = Rate * 3; f < samples.Length / 2; f += Rate * 4) { samples[f * 2] = 1.1f; samples[f * 2 + 1] = .8f; }
        return samples;
    }
    private static EnvelopePoint[] Original => [new(0, 0), new(19, 0), new(20, 1), new(21, 0), new(Seconds, 0)];
    private static ProcessingSettings Settings => new() { Strength = .7, TargetLufs = -14 };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal(RenderResult actual, RenderResult full)
    {
        Require(actual.Samples.AsSpan().SequenceEqual(full.Samples), "Incremental PCM differs from a fresh render");
        Require(actual.MatchGainDb == full.MatchGainDb && actual.OutputLufs == full.OutputLufs
            && actual.TruePeakDb == full.TruePeakDb && actual.MaxReductionDb == full.MaxReductionDb,
            "Incremental loudness, gain or peak metadata differs from fresh processing");
    }
    private static void LocalEdits()
    {
        var samples = Fixture();
        EnvelopePoint[][] edits =
        [
            [new(0, 0), new(19, 0), new(20, 2), new(21, 0), new(Seconds, 0)],
            [new(0, 0), new(19, 0), new(20.25, 1), new(21, 0), new(Seconds, 0)],
            [new(0, 0), new(19, 0), new(20, 1), new(20.5, 1.1), new(21, 0), new(Seconds, 0)],
            [new(0, 0), new(19, 0), new(21, 0), new(Seconds, 0)]
        ];
        foreach (double matching in new[] { 0d, 1d })
        foreach (bool limiter in new[] { false, true })
        {
            var settings = Settings with { MatchingStrength = matching, LimiterEnabled = limiter };
            var previous = AudioProcessor.Render(samples, Rate, 2, Original, 0, settings);
            foreach (var points in edits)
            {
                var incremental = AudioProcessor.RenderIncremental(samples, Rate, 2, points, 0, settings, previous);
                Equal(incremental, AudioProcessor.Render(samples, Rate, 2, points, 0, settings));
                Require(incremental.Work.UsedIncrementalStage && incremental.Work.LevelingFramesProcessed <= 2 * Rate + 2,
                    "Moving one point recomputed leveling outside its previous/next neighbors");
                Require(incremental.Work.DetectorFramesProcessed <= 2 * Rate + 26, "Oversampling did not use the localized FIR dependency range");
            }
        }
    }
    private static void LocalityAndHistory()
    {
        var samples = Fixture();
        // Below the ceiling and without normalization, the final PCM is local too.
        for (int i = 0; i < samples.Length; i++) samples[i] *= .1f;
        var settings = Settings with { MatchingEnabled = false, LimiterEnabled = false };
        var previous = AudioProcessor.Render(samples, Rate, 2, Original, 0, settings);
        var before = (float[])previous.Samples.Clone(); var stage = previous.StageState!;
        var stagedBefore = (float[])stage.Leveled.Clone();
        var points = Original; points[2] = new(20, 2);
        var result = AudioProcessor.RenderIncremental(samples, Rate, 2, points, 0, settings, previous);
        Require(previous.Samples.AsSpan().SequenceEqual(before) && stage.Leveled.AsSpan().SequenceEqual(stagedBefore), "Processing mutated a cached/playing previous buffer");
        Require(result.Samples.AsSpan(0, 19 * Rate * 2).SequenceEqual(before.AsSpan(0, 19 * Rate * 2)), "Unchanged prefix was modified");
        Require(result.Samples.AsSpan(21 * Rate * 2).SequenceEqual(before.AsSpan(21 * Rate * 2)), "Unchanged suffix was modified");
        Require(result.Work.LoudnessFramesProcessed < 12 * Rate && result.Work.FinalPeakFramesProcessed < 3 * Rate,
            "Local final PCM was remeasured across the whole track");
        Require(result.RetainedIntermediateBytes > samples.LongLength * 4, "Intermediate cache memory was not exposed for budgeting");
    }
    private static void GlobalMatching()
    {
        var samples = Fixture(); var previous = AudioProcessor.Render(samples, Rate, 2, Original, 0, Settings);
        var settings = Settings with { TargetLufs = -18 };
        var result = AudioProcessor.RenderIncremental(samples, Rate, 2, Original, 0, settings, previous);
        Equal(result, AudioProcessor.Render(samples, Rate, 2, Original, 0, settings));
        Require(result.Work.LevelingFramesProcessed == 0 && result.Work.DetectorFramesProcessed == 0, "Changing only target rebuilt pre-match stages");
        Require(!result.Samples.AsSpan(0, Rate * 2).SequenceEqual(previous.Samples.AsSpan(0, Rate * 2)), "Global matching incorrectly retained stale PCM outside an edit region");
    }
    private static void CollinearNoOp()
    {
        var samples = Fixture(); var previous = AudioProcessor.Render(samples, Rate, 2, Original, 0, Settings);
        EnvelopePoint[] points = [new(0, 0), new(19, 0), new(19.5, .5), new(20, 1), new(21, 0), new(Seconds, 0)];
        var result = AudioProcessor.RenderIncremental(samples, Rate, 2, points, 0, Settings, previous);
        Equal(result, previous);
        Require(result.Work.LevelingFramesProcessed == 0 && result.Work.DetectorFramesProcessed == 0
            && result.Work.LoudnessFramesProcessed == 0 && result.Work.FinalPeakFramesProcessed == 0,
            "Adding a point on the existing curve caused audio-stage work");
    }
    private static void FallbackAndCancellation()
    {
        var samples = Fixture(); var previous = AudioProcessor.Render(samples, Rate, 2, Original, 0, Settings);
        var replacement = (float[])samples.Clone();
        var result = AudioProcessor.RenderIncremental(replacement, Rate, 2, Original, 0, Settings, previous);
        Require(!result.Work.UsedIncrementalStage, "A different source reused stale intermediate PCM");
        Equal(result, AudioProcessor.Render(replacement, Rate, 2, Original, 0, Settings));
        // Input gain sits outside envelope clamping: equal raw dB sums can
        // still produce different PCM when an extreme authored point is capped.
        var quiet = samples.Select(value => value * .0001f).ToArray();
        EnvelopePoint[] capped = [new(0, 50), new(Seconds, 50)];
        EnvelopePoint[] changed = [new(0, 40), new(Seconds, 40)];
        var bypass = Settings with { MatchingEnabled = false, LimiterEnabled = false, Strength = 1 };
        var cappedPrevious = AudioProcessor.Render(quiet, Rate, 2, capped, 0, bypass, inputGainDb: -10);
        var cappedIncremental = AudioProcessor.RenderIncremental(quiet, Rate, 2, changed, 0, bypass, cappedPrevious, inputGainDb: 0);
        Equal(cappedIncremental, AudioProcessor.Render(quiet, Rate, 2, changed, 0, bypass));
        using var source = new CancellationTokenSource(); source.Cancel(); bool cancelled = false;
        try { AudioProcessor.RenderIncremental(samples, Rate, 2, Original, 0, Settings, previous, source.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Incremental processing ignored cancellation");
    }
}

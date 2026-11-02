using Multiloud.Core;

internal static class GeneratedSilenceChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Generated silence has exact decimal duration and shares immutable zero-valued sources", DurationAndSharing),
        ("Generated silence remains silent through active music processing", Processing),
        ("Slicing generated silence retains its project-owned source and exact regions", Slicing),
        ("Fixed background remains unchanged throughout generated music silence", Background),
        ("Generated silence rejects malformed durations and honors cancellation", BoundsAndCancellation),
    ];

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void DurationAndSharing()
    {
        var first = GeneratedSilenceSource.Create(1.05, 8000, 1);
        var second = GeneratedSilenceSource.Create(1.05, 8000, 1);
        Require(first.Duration == 1.05 && first.OriginalFrameCount == 8400 && first.GeneratedSilenceSeconds == 1.05,
            "Decimal silence duration was rounded to an integer second.");
        Require(first.Original.All(sample => sample == 0) && first.Analysis is { IntegratedLufs: -70, TruePeakDb: -120 },
            "Generated silence has nonzero audio or incorrect known measurements.");
        Require(ReferenceEquals(first.Original, second.Original) && ReferenceEquals(first.OriginalWaveform, second.OriginalWaveform) &&
            first.SourceId != second.SourceId && first.Id != second.Id,
            "Equal silences copied PCM or incorrectly shared mutable slot identity.");
        Require(first.Analysis!.LevelingEnergy?.FrameCount == first.OriginalFrameCount,
            "Known silent energy does not cover its entire region.");
        var minimum = GeneratedSilenceSource.Create(.001, 11025, 1);
        Require(minimum.OriginalFrameCount == 12 && minimum.GeneratedSilenceSeconds >= .001 &&
            minimum.GeneratedSilenceSeconds == minimum.Duration,
            "Minimum silence rounded below the project's persistable duration at an uncommon sample rate.");
    }

    private static void Processing()
    {
        var silence = GeneratedSilenceSource.Create(.3, 8000, 2);
        var original = silence.Original.ToArray();
        var result = AudioProcessor.Render(silence.Original, silence.SampleRate, silence.Channels,
            [new(0, 12), new(.3, -12)], 24,
            new ProcessingSettings { TargetLufs = -6, Strength = 1, MatchingStrength = 1, BassProtection = 1,
                DynamicsGuardEnabled = true, MaxSustainedReductionDb = 0 }, inputGainDb: 30);
        Require(result.Samples.Length == silence.Original.Length && result.Samples.All(sample => sample == 0) &&
            result.OutputLufs == -70 && result.TruePeakDb == -120 && silence.Original.AsSpan().SequenceEqual(original),
            "Loudness processing fabricated sound, changed duration or mutated generated silence.");
    }

    private static void Slicing()
    {
        var silence = GeneratedSilenceSource.Create(1.05, 8000, 2);
        var (left, right) = AudioTrackSlicer.SplitShared(silence, .4);
        var (_, final) = AudioTrackSlicer.SplitShared(right, .25);
        Require(left.Duration == .4 && right.Duration == .65 && Math.Abs(final.Duration - .4) < 1e-12 &&
            left.GeneratedSilenceSeconds == 1.05 && right.GeneratedSilenceSeconds == 1.05 && final.GeneratedSilenceSeconds == 1.05 &&
            Math.Abs(final.SourceStartSeconds - .65) < 1e-12 && ReferenceEquals(final.Original, silence.Original),
            "Silence cuts lost their original generated source duration or shared region.");
        Require(left.Analysis?.IntegratedLufs == -70 && right.Analysis?.LevelingEnergy?.FrameCount == right.OriginalFrameCount &&
            final.Analysis?.TruePeakDb == -120, "Known silence was turned into an unmeasured slice.");
    }

    private static void Background()
    {
        var silence = GeneratedSilenceSource.Create(.5, 8000, 2);
        var bed = new float[silence.Original.Length];
        for (int frame = 0; frame < bed.Length / 2; frame++)
        {
            bed[frame * 2] = (float)(.02 * Math.Sin(2 * Math.PI * 233 * frame / 8000));
            bed[frame * 2 + 1] = bed[frame * 2] * .7f;
        }
        var result = AudioProcessor.RenderWithBackground(silence.Original, 8000, 2, [], 12,
            new ProcessingSettings { Strength = 1, TargetLufs = -6 }, bed, inputGainDb: 30);
        Require(result.Samples.AsSpan().SequenceEqual(bed) && ReferenceEquals(result.BackgroundSamples, bed),
            "Music silence changed or amplified the fixed background.");
    }

    private static void BoundsAndCancellation()
    {
        foreach (double seconds in new[] { 0, -.1, double.NaN, double.PositiveInfinity, 600.001, .00001 })
        {
            bool rejected = false;
            try { GeneratedSilenceSource.Create(seconds); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "Invalid generated silence duration was accepted.");
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { GeneratedSilenceSource.Create(2, ct: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Generating silence ignored cancellation.");
    }
}

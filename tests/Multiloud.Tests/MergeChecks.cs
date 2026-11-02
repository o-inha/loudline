using Multiloud.Core;

internal static class MergeChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Merging adjacent shared cuts restores a zero-copy source range", Shared),
        ("Merging different sources preserves PCM order and immutable inputs", DifferentSources),
        ("Merge retains differing manual balances and resolves one target", ManualBalance),
        ("Merged recipes survive slicing and flatten subsequent joins", Recipes),
        ("Mixed-rate mono/stereo merges preserve duration on one sample clock", MixedFormats),
        ("Merge source materialization honors cancellation and size limits", Cancellation),
    ];
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static AudioTrack Source(string name, int frames = 200, int rate = 8000, int channels = 2)
    {
        var samples = Enumerable.Range(0, frames * channels).Select(index => (float)(.1 * Math.Sin(index * .071))).ToArray();
        return new AudioTrack { FilePath = name, SampleRate = rate, Channels = channels,
            Original = samples, OriginalWaveform = WaveformPyramid.Build(samples, channels),
            OriginalPeaks = AudioProcessor.BuildPeaks(samples, channels) };
    }
    private static void Shared()
    {
        var root = Source("Full album.wav", 16391); var (left, right) = AudioTrackSlicer.SplitShared(root, 7341d / 8000);
        var merged = AudioTrackMerger.CreateSlot(left, right);
        Require(ReferenceEquals(merged.Original, root.Original) && merged.OriginalFrameCount == root.OriginalFrameCount &&
            merged.OriginalFrameOffset == 0 && merged.SourceStartSeconds == 0 && merged.SourceDurationSeconds == root.Duration &&
            merged.MergedSources == null && merged.FilePath == root.FilePath, "Merging matching cuts copied PCM or lost the original source range.");
        Require(ReferenceEquals(merged.OriginalWaveform?.SharedSummaryRoot, root.OriginalWaveform),
            "Merging adjacent cuts rebuilt their waveform summaries.");
    }
    private static void DifferentSources()
    {
        var a = Source("A.wav"); var b = Source("B.wav", 317);
        var expected = a.Original.Concat(b.Original).ToArray();
        var merged = AudioTrackMerger.CreateSlot(a, b);
        Require(merged.Original.Length == 0 && merged.Duration == a.Duration + b.Duration,
            "A merge could not publish its metadata before the worker preparation.");
        AudioTrackMerger.Materialize(merged);
        Require(merged.Original.AsSpan().SequenceEqual(expected) && merged.MergedSources is { Count: 2 },
            "A source join reordered or altered native-format samples.");
        Require(a.Original.AsSpan().SequenceEqual(expected.AsSpan(0, a.Original.Length)) &&
            b.Original.AsSpan().SequenceEqual(expected.AsSpan(a.Original.Length)), "Merging modified an immutable input.");
    }
    private static void ManualBalance()
    {
        var root = Source("Album.wav"); var (a, b) = AudioTrackSlicer.SplitShared(root, root.Duration * .5);
        a.InputGainDb = -3; a.TrimDb = -3; a.TargetLufs = -12;
        b.InputGainDb = 2; b.TrimDb = 1; b.TargetLufs = -18;
        var merged = AudioTrackMerger.CreateSlot(a, b); AudioTrackMerger.Materialize(merged);
        Require(merged.InputGainDb == 0 && merged.TrimDb == 0 && merged.TargetLufs == null,
            "A merged item retained conflicting per-track controls instead of one target.");
        for (int index = 0; index < merged.Original.Length; index++)
        {
            double factor = Math.Pow(10, (index < a.OriginalSampleCount ? -6 : 3) / 20.0);
            Require(Math.Abs(merged.Original[index] - root.Original[index] * factor) < 1e-7,
                "Merging discarded a section's authored manual balance.");
        }
    }
    private static void Recipes()
    {
        var a = Source("A.wav"); var b = Source("B.wav"); var c = Source("C.wav");
        var first = AudioTrackMerger.CreateSlot(a, b); AudioTrackMerger.Materialize(first);
        var halves = AudioTrackSlicer.SplitShared(first, a.Duration);
        Require(halves.Left.MergedSourceId == first.MergedSourceId && ReferenceEquals(halves.Right.MergedSources, first.MergedSources),
            "A slice lost its full merged reconstruction recipe.");
        var rejoined = AudioTrackMerger.CreateSlot(halves.Left, halves.Right);
        Require(ReferenceEquals(rejoined.Original, first.Original) && rejoined.MergedSourceId == first.MergedSourceId,
            "Rejoining composite cuts copied their root or changed recipe identity.");
        var extended = AudioTrackMerger.CreateSlot(halves.Right, c); AudioTrackMerger.Materialize(extended);
        Require(extended.MergedSources is { Count: 2 } && extended.MergedSources[0].Source.FilePath == "B.wav" &&
            extended.Original.AsSpan().SequenceEqual(b.Original.Concat(c.Original).ToArray()),
            "A repeated merge retained out-of-range components or a recursive source chain.");
    }
    private static void MixedFormats()
    {
        var a = Source("Mono.wav", 137, 8000, 1); var b = Source("Stereo.wav", 211, 12000, 2);
        var merged = AudioTrackMerger.CreateSlot(a, b); AudioTrackMerger.Materialize(merged);
        Require(merged.SampleRate == 48000 && merged.Channels == 2 && merged.Original.All(float.IsFinite) &&
            Math.Abs(merged.Duration - (a.Duration + b.Duration)) <= 1d / 48000,
            "Mixed formats changed duration, channel mapping or output validity.");
    }
    private static void Cancellation()
    {
        var merged = AudioTrackMerger.CreateSlot(Source("A.wav"), Source("B.wav"));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { AudioTrackMerger.Materialize(merged, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && merged.Original.Length == 0, "Cancelled preparation still published PCM.");
        var tooLarge = new AudioTrack { MergedSources = [new(new AudioTrack { SavedDuration = 10000 }), new(new AudioTrack { SavedDuration = 10000 })] };
        bool rejected = false;
        try { AudioTrackMerger.Materialize(tooLarge); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "An oversized merge allocated an unsupported source.");
    }
}

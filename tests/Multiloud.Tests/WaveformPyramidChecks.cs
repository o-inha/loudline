using System.Runtime.InteropServices;
using Multiloud.Core;

internal static class WaveformPyramidChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Waveform pyramid extrema match every raw sample across mono/stereo ranges", RawAgreement),
        ("Waveform summaries retain impulses at bin and range boundaries", ImpulseBoundaries),
        ("Fractional waveform zoom covers the correct sample interval", FractionalZoom),
        ("Waveform query work stays bounded at every zoom without allocations", BoundedQueries),
        ("Waveform pyramid queries are immutable and thread safe", ConcurrentQueries),
        ("Incremental waveform summaries update only changed bins and ancestors", IncrementalUpdate),
        ("Waveform summaries use packed contiguous storage and exclude shared PCM", PackedStorage),
        ("Sliced waveform views reuse immutable summaries with exact bounded queries", SlicedViews),
        ("Waveform construction validates data and supports cancellation", ValidationAndCancellation)
    ];
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static float[] Signal(int frames, int channels, int seed = 724)
    {
        var random = new Random(seed); var result = new float[frames * channels];
        for (int i = 0; i < result.Length; i++) result[i] = (float)(random.NextDouble() * 2 - 1);
        return result;
    }
    private static WaveformExtrema Raw(float[] samples, int channels, int first, int end)
    {
        first = Math.Clamp(first, 0, samples.Length / channels); end = Math.Clamp(end, 0, samples.Length / channels);
        if (first >= end) return default;
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (int i = first * channels; i < end * channels; i++) { min = Math.Min(min, samples[i]); max = Math.Max(max, samples[i]); }
        return new(min, max);
    }
    private static void RawAgreement()
    {
        foreach (int channels in new[] { 1, 2 })
        foreach (int frames in new[] { 1, 63, 64, 65, 257, 8193 })
        foreach (int block in new[] { 1, 3, 64, 128 })
        {
            var samples = Signal(frames, channels); var pyramid = WaveformPyramid.Build(samples, channels, baseFrames: block);
            var random = new Random(frames * channels);
            for (int i = 0; i < 100; i++)
            {
                int a = random.Next(-10, frames + 10), b = random.Next(-10, frames + 10);
                Require(pyramid.Query(a, b) == Raw(samples, channels, a, b), $"Incorrect extrema for {channels}ch {frames}frames [{a},{b}) block{block}");
            }
            Require(pyramid.Query(0, frames) == Raw(samples, channels, 0, frames), "Whole-source extrema missed a partial EOF block");
        }
        var positive = Enumerable.Repeat(.25f, 130).ToArray();
        Require(WaveformPyramid.Build(positive, 1).Query(1, 129) == new WaveformExtrema(.25f, .25f), "Actual signed extrema were forced to zero");
    }
    private static void ImpulseBoundaries()
    {
        const int frames = 1027;
        foreach (int impulse in new[] { 0, 1, 62, 63, 64, 65, 127, 128, 255, 256, 1023, 1024, 1026 })
        {
            var samples = new float[frames * 2]; samples[impulse * 2] = 1.75f; samples[impulse * 2 + 1] = -1.5f;
            var pyramid = WaveformPyramid.Build(samples, 2);
            Require(pyramid.Query(0, frames) == new WaveformExtrema(-1.5f, 1.75f), "A transient disappeared from the overview");
            Require(pyramid.Query(impulse, impulse + 1) == new WaveformExtrema(-1.5f, 1.75f), "Close zoom missed a channel's impulse");
            Require(pyramid.Query(0, impulse) == default && pyramid.Query(impulse + 1, frames) == default, "A summary included a transient outside the requested interval");
        }
    }
    private static void FractionalZoom()
    {
        const int rate = 44100;
        var samples = Signal(1001, 2); var pyramid = WaveformPyramid.Build(samples, 2);
        foreach (var bounds in new[] { (-.1, .002), (63.25 / rate, 64.25 / rate), (999.9 / rate, 1000.1 / rate), (0d, .0013333) })
        {
            int a = (int)Math.Floor(bounds.Item1 * rate), b = (int)Math.Ceiling(bounds.Item2 * rate);
            Require(pyramid.QuerySeconds(bounds.Item1, bounds.Item2, rate) == Raw(samples, 2, a, b), "Fractional pixel bounds lost sample coverage");
        }
        Require(pyramid.QuerySeconds(1, 1, rate) == default && pyramid.QuerySeconds(2, 1, rate) == default, "Empty time ranges should be empty");
    }
    private static void BoundedQueries()
    {
        var pyramid = WaveformPyramid.Build(Signal((1 << 20) + 13, 2), 2);
        for (int level = 0; level < 12; level++)
        {
            int size = 64 << level;
            var value = pyramid.Query(0, size, out var work);
            Require(work.RawFrames == 0 && work.SummaryNodes == 1, "An aligned LOD range did not use a single summary");
        }
        const int columns = 4096;
        for (int pass = 0; pass < 20; pass++)
        for (int x = 0; x < columns; x++)
        {
            int first = (int)((long)x * pyramid.FrameCount / columns), end = (int)((long)(x + 1) * pyramid.FrameCount / columns);
            pyramid.Query(first, end, out var work);
            Require(work.RawFrames <= 126 && work.SummaryNodes <= pyramid.LevelCount * 2,
                "Coarse display-column work grew with represented audio duration");
        }
        // Warm the method before counting steady-state allocations.
        for (int i = 0; i < 100; i++) pyramid.Query(i + 1, i + 3000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) pyramid.Query(i + 1, i + 3000);
        Require(GC.GetAllocatedBytesForCurrentThread() == before, "Waveform queries allocate per-column heap objects");
        pyramid.Query(17, 21, out var close);
        Require(close.RawFrames == 4 && close.SummaryNodes == 0, "Genuinely close zoom should read its exact four raw frames");
    }
    private static void ConcurrentQueries()
    {
        var samples = Signal(16391, 2); var pyramid = WaveformPyramid.Build(samples, 2);
        Parallel.For(0, 16, worker =>
        {
            var random = new Random(worker);
            for (int i = 0; i < 100; i++)
            {
                int first = random.Next(16000), end = Math.Min(16391, first + random.Next(1, 300));
                Require(pyramid.Query(first, end) == Raw(samples, 2, first, end), "Concurrent query changed immutable summaries");
            }
        });
    }
    private static void IncrementalUpdate()
    {
        const int frames = 65539;
        var samples = Signal(frames, 2); var before = (float[])samples.Clone();
        var previous = WaveformPyramid.Build(samples, 2); var oldExtrema = previous.Query(0, frames);
        var changed = (float[])samples.Clone();
        for (int frame = 117; frame < 384; frame++) { changed[frame * 2] = 2; changed[frame * 2 + 1] = -3; }
        var updated = previous.Update(changed, 117, 384); var fresh = WaveformPyramid.Build(changed, 2);
        Require(updated.Work.UsedIncremental && updated.Work.RawFrames <= 384 - 117 + 126, "Incremental waveform construction scanned unrelated PCM");
        Require(updated.Work.SummaryNodes < frames / 64, "Incremental waveform update rebuilt unrelated summaries");
        var random = new Random(874);
        for (int i = 0; i < 1000; i++)
        {
            int first = random.Next(frames), end = random.Next(first, frames + 1);
            Require(updated.Query(first, end) == fresh.Query(first, end), "Updated waveform differs from a complete rebuild");
            Require(previous.Query(first, end) == Raw(before, 2, first, end), "Updating mutated previous cached summaries");
        }
        Require(previous.Query(0, frames) == oldExtrema && samples.AsSpan().SequenceEqual(before), "An update mutated previous source PCM");
        Require(ReferenceEquals(previous.Update(samples, 0, 0), previous), "Unchanged identical PCM should retain pyramid identity");
        var equivalent = previous.Update((float[])samples.Clone(), 0, 0);
        Require(equivalent.Work.RawFrames == 0 && equivalent.Work.SummaryNodes == 0 && equivalent.Query(0, frames) == oldExtrema,
            "Identical replacement PCM rebuilt summaries");
        var tail = (float[])changed.Clone(); tail[^1] = -8;
        var tailUpdated = updated.Update(tail, frames - 1, frames);
        Require(tailUpdated.Query(frames - 1, frames) == new WaveformExtrema(-8, tail[^2]) && tailUpdated.Query(0, frames).Min == -8,
            "Updating the final partial block missed its ancestor extrema");
        var resized = previous.Update(changed[..1000], 0, 500);
        Require(!resized.Work.UsedIncremental && resized.FrameCount == 500, "Length changes did not fall back to a complete build");
    }
    private static void PackedStorage()
    {
        Require(Marshal.SizeOf<WaveformExtrema>() == 8, "Min/max summaries are not packed two-float values");
        var samples = Signal(1 << 20, 2); var pyramid = WaveformPyramid.Build(samples, 2);
        Require(pyramid.RetainedBytes < samples.LongLength * sizeof(float) / 8, "Summary accounting includes a PCM copy or excessive per-bin object overhead");
        Require(pyramid.LevelCount == 15, "Expected pairwise levels from 16,384 base bins to one root");
    }
    private static void SlicedViews()
    {
        foreach (int channels in new[] { 1, 2 })
        {
            var samples = Signal(16391, channels);
            var source = WaveformPyramid.Build(samples, channels);
            var slice = source.Slice(17, 16003);
            var nested = slice.Slice(63, 10011);
            Require(slice.FrameCount == 15986 && nested.FrameCount == 9948 &&
                slice.Work.RawFrames == 0 && slice.Work.SummaryNodes == 0 &&
                nested.Work.RawFrames == 0 && nested.LevelCount == source.LevelCount,
                "Creating a slice rebuilt data or changed the source format");
            long expectedRetention = 128 + source.RetainedBytes + samples.LongLength * sizeof(float);
            Require(slice.RetainedBytes == expectedRetention && nested.RetainedBytes == expectedRetention,
                "A range view hid its retained parent PCM or counted it repeatedly through nested views");
            var random = new Random(71);
            for (int i = 0; i < 200; i++)
            {
                int a = random.Next(9948), b = random.Next(a + 1, 9949);
                Require(nested.Query(a, b, out var work) == Raw(samples, channels, a + 80, b + 80),
                    "A repeated slice queried the wrong source frames");
                Require(work.RawFrames <= 126 && work.SummaryNodes <= source.LevelCount * 2,
                    "A sliced query scanned work proportional to its represented duration");
            }
            Require(ReferenceEquals(source.Slice(0, source.FrameCount), source), "A full slice discarded its original pyramid");
            var changed = samples.Skip(80 * channels).Take(nested.FrameCount * channels).ToArray();
            changed[0] = 4;
            var updated = nested.Update(changed, 0, 1);
            Require(updated.Query(0, 1).Max == 4 && nested.Query(0, 1) == Raw(samples, channels, 80, 81),
                "Updating a sliced source mutated its shared summaries");
        }
    }
    private static void ValidationAndCancellation()
    {
        using var source = new CancellationTokenSource(); source.Cancel(); int errors = 0;
        try { WaveformPyramid.Build(new float[2], 2, source.Token); } catch (OperationCanceledException) { errors++; }
        var pyramid = WaveformPyramid.Build(new float[20], 2);
        try { pyramid.Update(new float[20], 0, 1, source.Token); } catch (OperationCanceledException) { errors++; }
        try { WaveformPyramid.Build(new float[3], 2); } catch (ArgumentException) { errors++; }
        try { WaveformPyramid.Build([float.NaN], 1); } catch (ArgumentException) { errors++; }
        Require(errors == 4, "Waveform construction failed validation or ignored cancellation");
        Require(WaveformPyramid.Build([], 1).Query(0, 5) == default, "Empty source query must be safe");
    }
}

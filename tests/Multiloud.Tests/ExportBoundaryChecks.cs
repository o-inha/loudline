using Multiloud.Core;

internal static class ExportBoundaryChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Custom exports reconstruct identical finished PCM across arbitrary source boundaries", Exact),
        ("Custom export boundaries canonicalize onto one sample clock without empty files", Canonical),
        ("Export partitions and one-file mode leave rendered source audio unchanged", NonDestructive),
        ("Mixed-format export chunks and custom partitions share exact resampling phase", Mixed),
        ("Boundary fades affect only internal file edges and remain bounded on short ranges", Fades),
        ("Uniform finished albums preserve native channel format and sample rate", UniformFormat),
        ("Export timeline reads use bounded reusable memory and honor cancellation", Bounded),
        ("Export timeline validation rejects malformed sources, ranges and markers", Validation)
    ];

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ExportAudioSource Source(int frames, int rate = 12000, int channels = 2, int seed = 1)
    {
        var random = new Random(seed);
        return new(Enumerable.Range(0, frames * channels).Select(_ => (float)(random.NextDouble() * .8 - .4)).ToArray(), rate, channels, $"source{seed}.wav");
    }
    private static float[] Read(ExportAudioTimeline timeline, ExportAudioRange range, int block = 137)
    {
        var result = new float[checked((int)range.FrameCount * timeline.Channels)];
        long frame = range.StartFrame;
        while (frame < range.EndFrame)
        {
            int count = (int)Math.Min(block, range.EndFrame - frame);
            Require(timeline.Read(frame, result, (int)(frame - range.StartFrame) * timeline.Channels, count) == count, "Short album read");
            frame += count;
        }
        return result;
    }
    private static void Exact()
    {
        var a = Source(421, seed: 1); var b = Source(751, seed: 2); var c = Source(210, seed: 3);
        var timeline = new ExportAudioTimeline([a, b, c]);
        var ranges = ExportBoundaryPlan.Custom(timeline, new[] { 37d / 12000, 500d / 12000, 1000d / 12000 });
        var joined = ranges.SelectMany(range => Read(timeline, range, 53)).ToArray();
        Require(joined.SequenceEqual(a.Samples.Concat(b.Samples).Concat(c.Samples)), "Export boundaries changed, repeated or dropped source samples");
        Require(Read(timeline, ExportBoundaryPlan.Single(timeline)[0]).SequenceEqual(joined), "One-file and custom exports differ");
        Require(ranges[1].StartFrame < 421 && ranges[1].EndFrame > 421, "Fixture did not cross an original source edge");
    }
    private static void Canonical()
    {
        var timeline = new ExportAudioTimeline([Source(1000)]);
        var ranges = ExportBoundaryPlan.Custom(timeline, new[] { -1d, 0, .010000001, .01, .010000002, .04, timeline.Duration, 99 });
        Require(ranges.Count == 3 && ranges[0].StartFrame == 0 && ranges[0].EndFrame == 120 &&
            ranges[1].EndFrame == 480 && ranges[^1].EndFrame == 1000 && ranges.All(range => range.FrameCount > 0), "Duplicate or outside markers created gaps/empty exports");
        Require(!ranges[0].FadeIn && ranges[0].FadeOut && ranges[1].FadeIn && ranges[1].FadeOut && ranges[^1].FadeIn && !ranges[^1].FadeOut,
            "Custom fade edges include the release's natural ends");
    }
    private static void NonDestructive()
    {
        var a = Source(1000); var b = Source(2000, seed: 2); var copies = new[] { a.Samples.ToArray(), b.Samples.ToArray() };
        var timeline = new ExportAudioTimeline([a, b]);
        Require(ExportBoundaryPlan.Original(timeline).Count == 2 && ExportBoundaryPlan.Single(timeline).Count == 1, "Export mode retained custom partitions");
        _ = Read(timeline, ExportBoundaryPlan.Custom(timeline, new[] { .03, .06, .1 })[1]);
        Require(a.Samples.SequenceEqual(copies[0]) && b.Samples.SequenceEqual(copies[1]), "Export partitioning mutated rendered PCM");
    }
    private static void Mixed()
    {
        var sources = new[] { Source(7057, 11025, 1), Source(9031, 24000, 2, 2), Source(17777, 44100, 1, 3) };
        var timeline = new ExportAudioTimeline(sources);
        Require(timeline.SampleRate == 48000 && timeline.Channels == 2, "Mixed formats did not choose the shared stereo clock");
        var one = Read(timeline, ExportBoundaryPlan.Single(timeline)[0], 16384);
        var custom = ExportBoundaryPlan.Custom(timeline, new[] { .12123, .50137, .70001, 1.00331 });
        var joined = custom.SelectMany(range => Read(timeline, range, 101)).ToArray();
        Require(one.SequenceEqual(joined), "Chunk or exported-file boundaries restarted resampling phase");
        Require(one.All(float.IsFinite) && timeline.FrameCount == Math.Round(sources.Sum(source => source.Samples.Length / (double)(source.SampleRate * source.Channels)) * 48000), "The common sample clock changed total duration");
    }
    private static void Fades()
    {
        var timeline = new ExportAudioTimeline([Source(3000)]);
        var ranges = ExportBoundaryPlan.Custom(timeline, new[] { .1 });
        Require(ExportBoundaryPlan.FadeGain(ranges[0], 0, 12000, 3) == 1 &&
            ExportBoundaryPlan.FadeGain(ranges[^1], ranges[^1].FrameCount - 1, 12000, 3) == 1, "Boundary fades changed the album's natural ends");
        Require(ExportBoundaryPlan.FadeGain(ranges[0], ranges[0].FrameCount - 1, 12000, 3) == 0 &&
            ExportBoundaryPlan.FadeGain(ranges[1], 0, 12000, 3) == 0, "Internal boundary fade did not reach zero");
        var tiny = new ExportAudioRange(5, 10, true, true);
        for (int i = 0; i < tiny.FrameCount; i++) Require(ExportBoundaryPlan.FadeGain(tiny, i, 12000, 1000) is >= 0 and <= 1, "Long fade invalidated a short export");
        foreach (var range in ranges)
            for (int i = 0; i < range.FrameCount; i++) Require(ExportBoundaryPlan.FadeGain(range, i, 12000, 0) == 1, "Default zero fade changed PCM gain");
    }
    private static void UniformFormat()
    {
        foreach (int rate in new[] { 8000, 44100, 96000 })
            foreach (int channels in new[] { 1, 2 })
            {
                var timeline = new ExportAudioTimeline([Source(37, rate, channels), Source(59, rate, channels, 2)]);
                Require(timeline.SampleRate == rate && timeline.Channels == channels && timeline.FrameCount == 96, "Uniform native format changed");
            }
        var tiny = Source(3, 44100, 1);
        var many = new ExportAudioTimeline(Enumerable.Repeat(tiny, 10007));
        Require(many.FrameCount == 30021 && Read(many, new ExportAudioRange(30018, 30021)).SequenceEqual(tiny.Samples),
            "A long sequence of tiny native clips lost a frame through duration rounding");
    }
    private static void Bounded()
    {
        var source = Source(10000); var timeline = new ExportAudioTimeline([source, source, source]);
        var buffer = new float[256]; timeline.Read(1900, buffer, 0, 128);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) timeline.Read(i * 17, buffer, 0, 128);
        Require(GC.GetAllocatedBytesForCurrentThread() - before < 4096, "Reading allocated a buffer per block or album-length audio");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { timeline.Read(0, buffer, 0, 128, cts.Token); throw new InvalidOperationException("Cancelled read succeeded"); }
        catch (OperationCanceledException) { }
    }
    private static void Validation()
    {
        static void Reject(Action action)
        { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Malformed export input accepted"); }
        Reject(() => new ExportAudioTimeline([]));
        Reject(() => new ExportAudioTimeline([new ExportAudioSource([.1f], 48000, 2)]));
        var timeline = new ExportAudioTimeline([Source(100)]);
        Reject(() => ExportBoundaryPlan.Custom(timeline, new[] { double.NaN }));
        Reject(() => timeline.Read(-1, new float[200], 0, 100));
        Reject(() => ExportBoundaryPlan.FadeGain(new ExportAudioRange(0, 100), 0, 12000, -1));
        var invalid = new ExportAudioTimeline([new ExportAudioSource([float.NaN, 0], 48000, 2)]);
        try { invalid.Read(0, new float[2], 0, 1); throw new InvalidOperationException("Nonfinite audio accepted"); }
        catch (InvalidDataException) { }
    }
}

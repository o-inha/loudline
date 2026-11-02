using Multiloud.Core;

internal static class LiveMixChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Module-off live mixing preserves fixed music/background gains without peak attenuation", ExactMix),
        ("Shared music cuts preserve resampling phase and background fades bit for bit", SharedCuts),
        ("Live export retains native mono source ranges without copying or resampling", NativeRange),
        ("Live custom exports and arbitrary chunk sizes share one continuous mix clock", ExportPartitions),
        ("Live mixing retains bounded buffers and defers sample validation to requested audio", Bounded),
        ("Single live-track exports preserve their absolute background placement", SingleTrackBed)
    ];
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static float[] Pcm(int frames, int channels, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, frames * channels).Select(_ => (float)(random.NextDouble() * .4 - .2)).ToArray();
    }
    private static BackgroundLayer Bed(int frames = 48000, int rate = 48000) => new()
    {
        GainDb = -3,
        Clips = [new BackgroundClip { Source = new AudioTrack { Original = Pcm(frames, 2, 3), SampleRate = rate, Channels = 2 },
            StartSeconds = -.015, FadeInSeconds = .07, FadeOutSeconds = .11 }]
    };
    private static float[] Read(LiveAudioReader reader, int block = 113)
    {
        var result = new float[checked((int)reader.FrameCount * 2)];
        for (long frame = 0; frame < reader.FrameCount;)
        {
            int frames = (int)Math.Min(block, reader.FrameCount - frame);
            Require(reader.Read(frame, result, (int)frame * 2, frames) == frames, "Short live mix read");
            frame += frames;
        }
        return result;
    }
    private static float[] Read(ExportAudioTimeline timeline, ExportAudioRange range, int block = 117)
    {
        var result = new float[checked((int)range.FrameCount * timeline.Channels)];
        for (long frame = range.StartFrame; frame < range.EndFrame;)
        {
            int frames = (int)Math.Min(block, range.EndFrame - frame);
            Require(timeline.Read(frame, result, (int)(frame - range.StartFrame) * timeline.Channels, frames) == frames, "Short live export read");
            frame += frames;
        }
        return result;
    }
    private static void ExactMix()
    {
        var music = Enumerable.Repeat(.8f, 4000).ToArray();
        var bg = Enumerable.Repeat(.7f, 4000).ToArray();
        var bed = new BackgroundLayer { Clips = [new BackgroundClip { Source = new AudioTrack { Original = bg } }] };
        var live = new LiveAudioSource(music, 48000, 2, 0, 2000, Background: bed);
        var output = Read(live.CreateReader(48000, 2));
        Require(output.All(value => value == (float)((double).8f + .7f)), "Live bypass silently limited, clipped or attenuated the mix");
        Require(music.All(value => value == .8f) && bg.All(value => value == .7f), "Live mixing modified source PCM");
        var gain = Read((live with { GainDb = -6 }).CreateReader(48000, 2));
        float expected = (float)(.8f * Math.Pow(10, -6d / 20) + .7f);
        Require(gain.All(value => value == expected), "Music fixed gain also changed the unprocessed background");
    }
    private static void SharedCuts()
    {
        var pcm = Pcm(20001, 2, 4); var bed = Bed();
        var whole = new LiveAudioSource(pcm, 44100, 2, 0, 20001, Background: bed);
        int[] edges = [0, 371, 9991, 15002, 20001];
        var pieces = edges.Zip(edges.Skip(1), (first, end) => whole with
        { FrameOffset = first, FrameCount = end - first, AlbumStartSeconds = first / 44100d }).ToArray();
        var expected = Read(whole.CreateReader(48000, 2), 4096);
        var actual = pieces.SelectMany(piece => Read(piece.CreateReader(48000, 2), 73)).ToArray();
        Require(actual.SequenceEqual(expected), "A shared cut reset music resampling context or the background sample/fade clock");
        Require(pieces.All(piece => ReferenceEquals(piece.Samples, pcm)), "Cuts copied source samples");
    }
    private static void NativeRange()
    {
        var root = Pcm(1031, 1, 7);
        var live = new LiveAudioSource(root, 44100, 1, 93, 617, AlbumStartSeconds: .25);
        var timeline = new ExportAudioTimeline([new ExportAudioSource(root, 44100, 1, "range.wav", live)]);
        Require(timeline.SampleRate == 44100 && timeline.Channels == 1 && timeline.FrameCount == 617, "Native live range export changed format or duration");
        Require(Read(timeline, ExportBoundaryPlan.Single(timeline)[0]).SequenceEqual(root.Skip(93).Take(617)), "Native source range changed sample values");
        var halfClock = live with { AlbumStartSeconds = .5 / 44100 };
        var halfTimeline = new ExportAudioTimeline([new ExportAudioSource(root, 44100, 1, "half.wav", halfClock)]);
        Require(halfTimeline.FrameCount == 617 && Read(halfTimeline, ExportBoundaryPlan.Single(halfTimeline)[0]).SequenceEqual(root.Skip(93).Take(617)),
            "An unrelated album-clock half frame changed native export PCM or its odd frame count");
    }
    private static void ExportPartitions()
    {
        var pcm = Pcm(11025, 2, 10);
        var source = new LiveAudioSource(pcm, 22050, 2, 0, 11025, GainDb: 2, Background: Bed());
        var timeline = new ExportAudioTimeline([new ExportAudioSource(pcm, 48000, 2, "live.wav", source)]);
        var whole = Read(timeline, ExportBoundaryPlan.Single(timeline)[0], 16384);
        var custom = ExportBoundaryPlan.Custom(timeline, [.01237, .18271, .30113]);
        var pieces = custom.SelectMany(range => Read(timeline, range, 41)).ToArray();
        Require(pieces.SequenceEqual(whole), "Live custom export chunks changed the finished mix");
        Require(whole.SequenceEqual(Read(source.CreateReader(48000, 2), 201)), "Live playback and export recipes differ");
    }
    private static void Bounded()
    {
        var root = Pcm(6000, 2, 2); root[^1] = float.NaN;
        var source = new LiveAudioSource(root, 48000, 2, 0, 3000, Background: Bed(10000));
        var reader = source.CreateReader(48000, 2); var buffer = new float[260];
        reader.Read(0, buffer, 2, 128);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) reader.Read(i * 13, buffer, 2, 128);
        Require(GC.GetAllocatedBytesForCurrentThread() - before < 4096, "Live playback allocates a new audio buffer on each callback");
        Require(buffer[0] == 0 && buffer[1] == 0 && buffer[^1] == 0, "Live mix wrote outside requested bounds");
        var converted = source with { SampleRate = 44100, Background = Bed(10000, 44100) };
        converted.CreateReader(48000, 2).Read(0, buffer, 0, 128); // Warm the immutable rate-pair kernels.
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) converted.CreateReader(48000, 2).Read(i, buffer, 0, 128);
        Require(GC.GetAllocatedBytesForCurrentThread() - before < 512 * 1024,
            "Repeated scrub readers rebuilt large sinc phase kernels instead of sharing bounded immutable tables");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { reader.Read(0, buffer, 0, 128, cancelled.Token); throw new InvalidOperationException("Cancelled live read succeeded"); }
        catch (OperationCanceledException) { }
    }
    private static void SingleTrackBed()
    {
        var pcm = Pcm(24000, 2, 9); var bed = Bed();
        var full = new LiveAudioSource(pcm, 48000, 2, 0, 24000, Background: bed);
        var right = full with { FrameOffset = 9001, FrameCount = 14999, AlbumStartSeconds = 9001d / 48000 };
        var timeline = new ExportAudioTimeline([new ExportAudioSource(pcm, 48000, 2, "right.wav", right)]);
        Require(Read(timeline, ExportBoundaryPlan.Single(timeline)[0]).SequenceEqual(Read(full.CreateReader(48000, 2)).Skip(9001 * 2)),
            "Single-track live export restarted its background or fades at time zero");
    }
}

using Multiloud.App.Services;
using Multiloud.Core;

internal static class BackgroundCacheTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Disabled backgrounds reuse legacy music while active zero beds retain the common format", InactiveSegments),
        ("Background segments follow album offsets and exact overlap boundaries", SegmentPositions),
        ("Audible background gain, position, fades and output format isolate previews", EffectiveChanges),
        ("Background keys ignore music processing and clip display identity", IndependentBedIdentity),
        ("Background source replacements preserve separate undo cache variants", SourceReplacement),
        ("Background source ranges isolate segments without replacing their decoded root", SharedSourceRanges),
        ("Unrelated background clips do not invalidate a music segment", UnrelatedClips),
        ("Restoring a background configuration reuses the exact completed mix", RestoreVariant),
        ("Preview budgets account for retained fixed background PCM", MemoryBudget),
        ("Live preview budgets account for shared music and distinct background source roots", LiveMemoryBudget),
    ];

    private static readonly ProcessingSettings Settings = new();
    private static AudioTrack Source(double seconds = 4, int rate = 8000, int channels = 1, float value = .01f) => new()
    {
        FilePath = "background.wav", SampleRate = rate, Channels = channels,
        Original = Enumerable.Repeat(value, (int)(seconds * rate) * channels).ToArray(),
    };
    private static AudioTrack Music() => Source(seconds: 2, value: .1f);
    private static BackgroundLayer Layer(AudioTrack? source = null, double start = 0) => new()
    {
        GainDb = -6,
        Clips = [new BackgroundClip { Source = source ?? Source(), StartSeconds = start, FadeInSeconds = .1, FadeOutSeconds = .2 }]
    };
    private static string? Bed(BackgroundLayer layer, double start = 0, double duration = 2, int rate = 8000, int channels = 1)
        => PreviewCache.CreateBackgroundKey(layer, start, duration, rate, channels);
    private static string Key(AudioTrack music, BackgroundLayer layer, double start = 0, ProcessingSettings? settings = null)
        => PreviewCache.CreateKey(music, settings ?? Settings, layer, start);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void InactiveSegments()
    {
        var music = Music(); var layer = Layer();
        string bare = PreviewCache.CreateKey(music, Settings);
        foreach (var inactive in new[] { new BackgroundLayer(), layer with { Enabled = false } })
            Require(Bed(inactive) == null && Key(music, inactive) == bare,
                "Disabled or empty background invalidated exact legacy music-only audio.");
        foreach (var silent in new[] { Layer(start: 2), Layer(start: -4) })
            Require(Bed(silent) == null && Key(music, silent) != bare,
                "An active zero-bed segment reused source-format audio instead of the common stereo mix.");
        Require(Bed(layer, duration: 0) == null, "A zero-length segment retained a background variant.");
        Require(Bed(layer) != null && Key(music, layer) != bare,
            "An active overlapping bed reused the music-only result.");
    }

    private static void SegmentPositions()
    {
        var music = Music(); var layer = Layer(start: 1);
        var first = Bed(layer, start: 0);
        Require(first != null && first != Bed(layer, start: 2) && first != Bed(layer, start: .000125),
            "Different portions of one positioned source shared a background key.");
        Require(Bed(layer, start: 5) == null && Bed(layer, start: -1) == null,
            "Exact clip-end/segment-end boundaries included a neighboring background clip.");
        Require(Bed(layer, start: 4.999875) != null && Bed(layer, start: -.999875) != null,
            "A one-frame overlap was mistaken for an empty segment.");
        Require(Key(music, layer, 0) != Key(music, layer, 2),
            "Reordering a music slot to a different bed position reused its old final mix.");
    }

    private static void EffectiveChanges()
    {
        var music = Music(); var layer = Layer(); var clip = layer.Clips[0];
        string original = Key(music, layer);
        var variants = new[]
        {
            layer with { GainDb = layer.GainDb + .000001 },
            layer with { Clips = [clip with { StartSeconds = .000125 }] },
            layer with { Clips = [clip with { FadeInSeconds = .100001 }] },
            layer with { Clips = [clip with { FadeOutSeconds = .200001 }] },
        };
        foreach (var changed in variants)
            Require(Key(music, changed) != original, "An audible fine background edit aliased the previous mix.");
        var key = Bed(layer);
        Require(key != Bed(layer, duration: 1.999875) && key != Bed(layer, rate: 12000) && key != Bed(layer, channels: 2),
            "A different bed length or resampling/channel format reused the old PCM.");
        music.TrimDb = .1;
        Require(Key(music, layer) != original, "Adding the bed caused music processing changes to be ignored.");
    }

    private static void IndependentBedIdentity()
    {
        var music = Music(); var layer = Layer(); string? original = Bed(layer);
        var clip = layer.Clips[0];
        var copied = layer with { Clips = [clip with { Id = Guid.NewGuid() }] };
        Require(Bed(copied) == original && Key(music, copied) == Key(music, layer),
            "Reconstructed clip display identity invalidated otherwise identical audio.");
        // The background segment is reusable while music-only knobs change.
        string mix = Key(music, layer);
        var settings = Settings with { TargetLufs = -18, Strength = .2, CeilingDb = -2 };
        Require(Key(music, layer, settings: settings) != mix && Bed(layer) == original,
            "A music edit either missed its final mix variant or invalidated fixed bed PCM.");
        clip.Source.TrimDb = 8; clip.Source.InputGainDb = -7; clip.Source.TargetLufs = -9;
        clip.Source.Envelope = [new(0, 12), new(4, -12)];
        Require(Bed(layer) == original,
            "Music-processing metadata on a background source changed its fixed unprocessed bed identity.");
    }

    private static void SourceReplacement()
    {
        var music = Music(); var oldSource = Source(); var layer = Layer(oldSource);
        string oldKey = Key(music, layer);
        var replacement = Source(value: .02f); replacement.Id = oldSource.Id;
        var changed = layer with { Clips = [layer.Clips[0] with { Source = replacement }] };
        string newKey = Key(music, changed);
        Require(newKey != oldKey && Bed(changed) != Bed(layer),
            "Same-slot, same-format and same-length background source replacement reused old PCM.");
        var cache = new PreviewCache();
        var oldRender = new RenderResult { Samples = music.Original, BackgroundSamples = oldSource.Original.Take(music.Original.Length).ToArray() };
        var newRender = new RenderResult { Samples = music.Original.ToArray(), BackgroundSamples = replacement.Original.Take(music.Original.Length).ToArray() };
        cache.Store(oldKey, oldRender, []); cache.Store(newKey, newRender, []);
        Require(cache.TryGet(oldKey, out var a) && ReferenceEquals(a.Render, oldRender) &&
            cache.TryGet(newKey, out var b) && ReferenceEquals(b.Render, newRender),
            "Background replacement overwrote an undoable source variant.");
        cache.RemoveTrack(music.Id);
        Require(cache.Count == 0 && cache.CachedBytes == 0, "Removing a music slot leaked its background mix variants.");
    }

    private static void SharedSourceRanges()
    {
        var source = Source(); source.OriginalFrameLength = 16000;
        var layer = Layer(source); string? first = Bed(layer);
        source.OriginalFrameOffset = 1;
        Require(first != Bed(layer), "A different shared background region reused its previous bed segment.");
        source.OriginalFrameOffset = 0; source.OriginalFrameLength = 16001;
        Require(first != Bed(layer), "A different shared background duration reused its previous segment.");
        source.OriginalFrameLength = 16000;
        Require(first == Bed(layer), "Restoring the same background range failed to restore its bed identity.");
    }

    private static void UnrelatedClips()
    {
        var music = Music(); var layer = Layer(); string before = Key(music, layer);
        var remote = new BackgroundClip { Source = Source(), StartSeconds = 10 };
        var added = layer with { Clips = [remote, layer.Clips[0]] };
        Require(Key(music, added) == before && Bed(added) == Bed(layer),
            "Adding an off-segment clip invalidated unrelated preview audio.");
        added = added with { Clips = [remote with { StartSeconds = 12, FadeInSeconds = 2 }, layer.Clips[0]] };
        Require(Key(music, added) == before, "Editing an off-segment background clip invalidated unrelated audio.");
        added = added with { Clips = [remote with { StartSeconds = 1 }, layer.Clips[0]] };
        Require(Key(music, added) != before, "Moving a background clip into the segment failed to invalidate its mix.");
    }

    private static void RestoreVariant()
    {
        var music = Music(); var layer = Layer(); var cache = new PreviewCache();
        string key = Key(music, layer); var samples = new float[music.Original.Length];
        var bed = BackgroundRenderer.RenderSegment(layer, 0, music.Duration, music.SampleRate, music.Channels);
        var render = new RenderResult { Samples = samples, BackgroundSamples = bed }; var peaks = new List<PeakBin> { new(-.1f, .1f) };
        cache.Store(key, render, peaks);
        var changed = layer with { GainDb = -8 };
        Require(!cache.TryGet(Key(music, changed), out _), "Changed background gain returned a stale cached mix.");
        var restored = changed with { GainDb = layer.GainDb, Clips = layer.Clips.Select(clip => clip with { }).ToArray() };
        Require(cache.TryGet(Key(music, restored), out var hit) && ReferenceEquals(hit.Render, render) &&
            ReferenceEquals(hit.Render.Samples, samples) && ReferenceEquals(hit.Render.BackgroundSamples, bed) && ReferenceEquals(hit.Peaks, peaks),
            "Restoring an equivalent bed failed to reuse exact final and background PCM references.");
    }

    private static void MemoryBudget()
    {
        var music = Music(); var layer = Layer(); string key = Key(music, layer);
        var samples = new float[music.Original.Length]; var background = new float[samples.Length];
        var bare = new RenderResult { Samples = samples };
        var mixed = new RenderResult { Samples = samples, BackgroundSamples = background };
        long bareBytes = PreviewCache.EstimateBytes(key, bare, []), mixedBytes = PreviewCache.EstimateBytes(key, mixed, []);
        Require(mixedBytes - bareBytes == background.LongLength * sizeof(float), "Preview cache did not count fixed background PCM exactly once.");
        var prepared = new float[samples.Length];
        var common = new RenderResult { Samples = samples, BackgroundSamples = background, BackgroundOriginalSamples = prepared,
            RenderedSampleRate = 48000, RenderedChannels = 2 };
        long commonBytes = PreviewCache.EstimateBytes(key, common, []);
        Require(commonBytes - mixedBytes == prepared.LongLength * sizeof(float),
            "The extra common-format Original PCM escaped background cache accounting.");
        var commonBudget = new PreviewCache(commonBytes - 1); commonBudget.Store(key, common, []);
        Require(commonBudget.Count == 0 && commonBudget.CachedBytes == 0,
            "A prepared common-format Original buffer exceeded the configured cache memory budget.");
        var tooSmall = new PreviewCache(mixedBytes - 1); tooSmall.Store(key, mixed, []);
        Require(tooSmall.Count == 0 && tooSmall.CachedBytes == 0, "A background mix exceeded the configured memory budget.");
        var exact = new PreviewCache(mixedBytes); exact.Store(key, mixed, []); exact.Store(key, mixed, []);
        Require(exact.Count == 1 && exact.CachedBytes == mixedBytes, "Replacing a mixed variant double-counted retained bed PCM.");
    }

    private static void LiveMemoryBudget()
    {
        var music = Music(); var bed = Source(); var layer = Layer(bed);
        var bare = new RenderResult { Samples = music.Original };
        var live = new LiveAudioSource(music.Original, music.SampleRate, music.Channels,
            0, music.OriginalFrameCount, Background: layer);
        var streamed = new RenderResult { Samples = music.Original, LiveSource = live };
        long bareCost = PreviewCache.EstimateBytes("live", bare, []);
        long liveCost = PreviewCache.EstimateBytes("live", streamed, []);
        Require(liveCost >= bareCost + bed.Original.LongLength * sizeof(float),
            "A live preview omitted the background root it retains for playback/export.");
        var repeated = layer with { Clips = [layer.Clips[0], layer.Clips[0] with { StartSeconds = 1 }] };
        var repeatedRender = new RenderResult { Samples = music.Original, LiveSource = live with { Background = repeated } };
        long repeatedCost = PreviewCache.EstimateBytes("live", repeatedRender, []);
        Require(repeatedCost >= liveCost && repeatedCost - liveCost < bed.Original.LongLength * sizeof(float),
            "Reusing one background source counted its entire PCM a second time within one preview.");
        var second = Source();
        var distinct = layer with { Clips = [layer.Clips[0], new BackgroundClip { Source = second }] };
        var distinctRender = new RenderResult { Samples = music.Original, LiveSource = live with { Background = distinct } };
        Require(PreviewCache.EstimateBytes("live", distinctRender, []) >= liveCost + second.Original.LongLength * sizeof(float),
            "A second independent live background source escaped the cache byte budget.");
        var small = new PreviewCache(liveCost - 1); small.Store("live", streamed, []);
        Require(small.Count == 0 && small.CachedBytes == 0, "An oversized live source variant exceeded its cache limit.");
    }
}

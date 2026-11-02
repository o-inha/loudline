using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

internal static class LivePlaybackTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Live Original/Result mix ready sources without prepared bed or measured PCM", ImmediateBothModes),
        ("Live shared slices preserve the continuous resampled album during playback and seeks", SlicedPlayback),
        ("Live monitor attenuation and byte-backed callback offsets remain bounded", AliasedMonitor),
        ("Live mono music-only playback preserves equal-power center routing", MonoRouting)
    ];
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static PlaybackTrackState State(LiveAudioSource live) => new(Guid.NewGuid(), 48000, 2,
        live.CreateReader(48000, 2).FrameCount / 48000d, null, null, BackgroundRequired: live.HasBackground,
        LiveOriginal: live, LiveResult: live);
    private static float[] Read(PlaybackSequence sequence)
    {
        var result = new List<float>(); var buffer = new float[258];
        while (true)
        {
            int got = sequence.Read(buffer, 1, 253); if (got == 0) break;
            result.AddRange(buffer.Skip(1).Take(got));
        }
        return result.ToArray();
    }
    private static BackgroundLayer Bed(float[] samples) => new()
    { Clips = [new BackgroundClip { Source = new AudioTrack { Original = samples } }] };
    private static void ImmediateBothModes()
    {
        var music = new[] { .9f, -.9f, .8f, -.8f, .7f, -.7f };
        var bed = new[] { .3f, .3f, .2f, .2f, .1f, .1f };
        var live = new LiveAudioSource(music, 48000, 2, 0, 3, Background: Bed(bed)); var state = State(live);
        Require(state.IsReady(true) && state.IsReady(false), "Live audio unnecessarily waited for analysis or a prepared background mix");
        foreach (bool original in new[] { true, false })
        {
            var sequence = new PlaybackSequence(() => new[] { state }, original, 0);
            Require(Read(sequence).SequenceEqual(music.Zip(bed, (a, b) => (float)((double)a + b))), "Live playback changed the bypass sum or added background twice");
        }
        var pending = state with { LiveResult = null };
        Require(pending.IsReady(true) && !pending.IsReady(false), "Pending processed Result blocked source Original or became prematurely ready");
    }
    private static void SlicedPlayback()
    {
        var random = new Random(2);
        var music = Enumerable.Range(0, 22053 * 2).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
        var bed = Bed(Enumerable.Range(0, 48000 * 2).Select(_ => (float)(random.NextDouble() * .1)).ToArray());
        var live = new LiveAudioSource(music, 44100, 2, 0, 22053, Background: bed);
        int cut = 5771;
        var first = State(live with { FrameCount = cut });
        var second = State(live with { FrameOffset = cut, FrameCount = 22053 - cut, AlbumStartSeconds = cut / 44100d });
        PlaybackTrackState[] parts = [first, second];
        var all = Read(new PlaybackSequence(() => new[] { State(live) }, true, 0));
        var split = Read(new PlaybackSequence(() => parts, false, 0));
        Require(all.SequenceEqual(split), "Slicing changed live playback's resampling or background clock");
        double seek = first.Duration + 97.25d / 48000;
        var tail = Read(new PlaybackSequence(() => parts, false, seek));
        Require(tail.SequenceEqual(all.Skip((int)Math.Floor(seek * 48000) * 2)), "Live source range seek restarted the source or bed clock");
        live = live with { Background = null };
        first = State(live with { FrameCount = cut }) with { Duration = cut / 44100d };
        second = State(live with { FrameOffset = cut, FrameCount = 22053 - cut, AlbumStartSeconds = cut / 44100d })
            with { Duration = (22053 - cut) / 44100d };
        parts = [first, second];
        all = Read(new PlaybackSequence(() => new[] { State(live) }, true, 0));
        split = Read(new PlaybackSequence(() => parts, false, 0));
        Require(all.SequenceEqual(split), "Native fractional state durations changed sliced music-only playback");
        seek = first.Duration + 97.25d / 48000;
        tail = Read(new PlaybackSequence(() => parts, false, seek));
        Require(tail.SequenceEqual(all.Skip((int)Math.Floor(seek * 48000) * 2)), "Native fractional state duration made live seek miss one output frame");
    }
    private static void AliasedMonitor()
    {
        var source = new LiveAudioSource([.2f, -.3f, .4f, -.5f], 48000, 2, 0, 2, Background: Bed([.1f, .1f, .1f, .1f]));
        var state = State(source) with { OriginalAuditionGainDb = 20 * Math.Log10(.5) };
        var sequence = new PlaybackSequence(() => new[] { state }, true, 0);
        var bytes = Enumerable.Repeat((byte)0x5A, 40).ToArray(); var alias = new WaveBuffer(bytes);
        int got = sequence.Read(alias.FloatBuffer, 3, 7);
        Require(got == 4, "Live callback returned incomplete frames");
        var decoded = new float[10]; Buffer.BlockCopy(bytes, 0, decoded, 0, bytes.Length);
        Require(Math.Abs(decoded[3] - .15) < 1e-7 && Math.Abs(decoded[4] + .1) < 1e-7,
            "Monitor matching did not attenuate the complete live mix");
        Require(bytes.Take(12).All(value => value == 0x5A) && bytes.Skip(28).All(value => value == 0x5A), "Live callback overwrote adjacent byte-backed samples");
    }
    private static void MonoRouting()
    {
        var source = new LiveAudioSource([.1f, .2f, -.3f], 48000, 1, 0, 3);
        var state = State(source); var actual = Read(new PlaybackSequence(() => new[] { state }, false, 0));
        var expected = source.Samples.SelectMany(value => new[] { value * Math.Sqrt(.5), value * Math.Sqrt(.5) }).ToArray();
        Require(actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-7), "Live bypass changed mono monitoring level");
    }
}

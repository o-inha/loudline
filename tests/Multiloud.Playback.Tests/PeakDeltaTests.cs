using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

internal static class PeakDeltaTests
{
    private const int Rate = 48000;
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Peak Delta excludes leveling/matching changes and is silent without limiting", EarlierStagesCancel),
        ("Peak Delta reconstructs only protection changes in both listening views", ProtectionDifference),
        ("Peak Delta cancels fixed background while keeping source and Result immutable", BackgroundCancels),
        ("Peak Delta respects shared root ranges and mixed-rate mono monitoring", RootRangeAndResampling),
        ("Peak Delta preserves byte-backed offsets and stops at pending Results", PendingAndByteBuffer),
        ("Peak Delta scrubbing uses the same bounded difference provider", ScrubDifference),
        ("Peak Delta includes music drive backed off by the dynamics safeguard", DynamicsGuardDifference),
    ];

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void Near(double actual, double expected, double tolerance = 1e-6)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected:G10} ± {tolerance:G3}, got {actual:G10}.");

    private static float[] Tone(double amplitude, int frames = Rate, int channels = 2, int rate = Rate)
    {
        var samples = new float[frames * channels];
        for (int frame = 0; frame < frames; frame++)
            for (int channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * frame / rate) * (channel == 0 ? 1 : .6));
        return samples;
    }

    private static AudioTrack Track(float[] samples, RenderResult result, int rate = Rate, int channels = 2)
        => new() { FilePath = "delta.wav", Original = samples, SampleRate = rate, Channels = channels, Render = result };

    private static PlaybackTrackState State(AudioTrack track)
        => new(track.Id, track.SampleRate, track.Channels, track.Duration, track.Original, track.Render!.Samples,
            OriginalAuditionGainDb: -20, PreviewAuditionGainDb: -30,
            PeakDelta: PeakDeltaSource.ForTrack(track), DeltaRequested: true);

    private static float[] ReadAll(PlaybackSequence sequence)
    {
        var result = new List<float>();
        var buffer = new float[1026];
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            Array.Fill(buffer, 99);
            int got = sequence.Read(buffer, 3, 1019);
            Require(got >= 0 && got <= 1018 && got % 2 == 0, "Delta returned a partial output frame.");
            Require(buffer.Take(3).All(x => x == 99) && buffer.Skip(3 + got).All(x => x == 99), "Delta wrote outside callback bounds.");
            if (got == 0) return result.ToArray();
            result.AddRange(buffer.Skip(3).Take(got));
        }
        throw new InvalidOperationException("Delta did not reach EOF.");
    }

    private static float[] Read(PlaybackTrackState state, bool original = false, double seek = 0)
        => ReadAll(PlaybackService.CreateSampleProvider(() => new[] { state }, original, seek));

    private static float Before(float sample, RenderResult render, double time)
    {
        double envelope = render.RenderedLevelingStrength > 0 && render.RenderedEnvelope.Count > 0
            ? Math.Clamp(AudioProcessor.EnvelopeGainAt(render.RenderedEnvelope, time) * render.RenderedLevelingStrength, -36, 36) : 0;
        float leveled = (float)(sample * Math.Pow(10, (render.RenderedInputGainDb + envelope) / 20));
        return (float)(leveled * Math.Pow(10, (render.MatchGainDb + render.DynamicsGuardReductionDb + render.RenderedTrimDb) / 20));
    }

    private static void EarlierStagesCancel()
    {
        var input = Tone(.04);
        EnvelopePoint[] envelope = [new(0, -3), new(.4, 4), new(1, -2)];
        foreach (bool limiter in new[] { false, true })
        {
            var render = AudioProcessor.Render(input, Rate, 2, envelope, -.7, new ProcessingSettings
            {
                LevelingEnabled = true, MatchingEnabled = true, LimiterEnabled = limiter,
                Strength = .63, TargetLufs = -20, CeilingDb = -1,
            }, inputGainDb: 1.2);
            Require(!input.AsSpan().SequenceEqual(render.Samples), "The fixture did not exercise earlier processing stages.");
            var state = State(Track(input, render));
            Require(state.PeakDelta != null && state.IsReady(true) && state.IsReady(false), "A ready Delta could not be published.");
            Require(Read(state).All(x => x == 0) && Read(state, true).All(x => x == 0), "Delta leaked leveling, matching or monitor gain.");
        }
    }

    private static void ProtectionDifference()
    {
        var input = Tone(1.5);
        var render = AudioProcessor.Render(input, Rate, 2, [new(0, 1), new(1, -1)], 1.5,
            new ProcessingSettings { MatchingEnabled = false, Strength = .7, CeilingDb = -2 }, inputGainDb: 2);
        Require(render.MaxReductionDb > .5, "The Delta fixture did not limit.");
        var state = State(Track(input, render));
        var heard = Read(state);
        Require(heard.Length == input.Length && heard.Any(x => Math.Abs(x) > .01), "Delta lost its removed signal or duration.");
        Require(heard.AsSpan().SequenceEqual(Read(state, true)), "Original and Result selected different Delta signals.");
        for (int index = 0; index < heard.Length; index++)
            Near(heard[index], Before(input[index], render, (index / 2) / (double)Rate) - render.Samples[index], 1e-7);
    }

    private static void BackgroundCancels()
    {
        var input = Tone(.4);
        var background = Tone(.02);
        EnvelopePoint[] points = [new(0, -2), new(1, 2)];
        var render = AudioProcessor.RenderWithBackground(input, Rate, 2, points, -.2,
            new ProcessingSettings { MatchingEnabled = false, Strength = .6, CeilingDb = -.5 }, background, inputGainDb: -1);
        render.BackgroundOriginalSamples = input; render.RenderedSampleRate = Rate; render.RenderedChannels = 2;
        Require(render.MaxReductionDb == 0, "The cancellation fixture should be below the peak constraint.");
        var inputCopy = input.ToArray(); var backgroundCopy = background.ToArray(); var outputCopy = render.Samples.ToArray();
        var state = State(Track(input, render));
        Require(Read(state).All(x => x == 0), "Delta leaked a fixed bed or its floating-point mix rounding.");
        Require(input.AsSpan().SequenceEqual(inputCopy) && background.AsSpan().SequenceEqual(backgroundCopy) && render.Samples.AsSpan().SequenceEqual(outputCopy),
            "Delta modified immutable source, background or export PCM.");
    }

    private static void RootRangeAndResampling()
    {
        const int rate = 44100, frames = rate / 4, prefix = 123;
        var root = Enumerable.Repeat(.7f, prefix).Concat(Enumerable.Repeat(.4f, frames)).Concat(Enumerable.Repeat(-.7f, 321)).ToArray();
        var render = new RenderResult { Samples = Enumerable.Repeat(.1f, frames).ToArray(), RenderedLimiterEnabled = true };
        var track = Track(root, render, rate, 1);
        track.OriginalFrameOffset = prefix; track.OriginalFrameLength = frames;
        var descriptor = PeakDeltaSource.ForTrack(track);
        Require(descriptor != null && ReferenceEquals(descriptor.Source, root) && descriptor.FrameOffset == prefix && descriptor.FrameCount == frames,
            "Delta copied or lost its shared source range.");
        var heard = Read(State(track), seek: .05);
        Require(Math.Abs(heard.Length / 2 - (frames / (double)rate - .05) * Rate) < 5, "Mixed-rate Delta changed the range duration.");
        for (int index = 100; index + 100 < heard.Length; index++) Near(heard[index], .3 * Math.Sqrt(.5), 2e-4);

        var live = new RenderResult { Samples = root, LiveSource = new(root, rate, 1, prefix, frames), RenderedLimiterEnabled = false };
        track.Render = live;
        Require(Read(State(track)).All(x => x == 0), "An all-off live slice exposed the full root or leaked original music.");
    }

    private static void PendingAndByteBuffer()
    {
        float[] source = [.3f, -.2f, .5f, -.4f, .6f, -.5f];
        var render = new RenderResult { Samples = [.1f, -.1f, .2f, -.2f, .2f, -.1f], RenderedLimiterEnabled = true };
        var first = State(Track(source, render));
        var pending = first with { Id = Guid.NewGuid(), PeakDelta = null };
        Require(!pending.IsReady(true) && !pending.IsReady(false), "Pending Delta fell back to ready Original or Result audio.");
        var sequence = PlaybackService.CreateSampleProvider(() => new[] { first, pending }, true, 0);
        var bytes = Enumerable.Repeat((byte)0x6B, 64).ToArray();
        var alias = new WaveBuffer(bytes);
        int got = sequence.Read(alias.FloatBuffer, 3, 9);
        Require(got == source.Length && sequence.PendingTrackId == pending.Id, "Delta skipped a pending track or lost aligned EOF.");
        var heard = new float[bytes.Length / sizeof(float)]; Buffer.BlockCopy(bytes, 0, heard, 0, bytes.Length);
        for (int index = 0; index < got; index++) Near(heard[index + 3], source[index] - render.Samples[index]);
        Require(bytes.Take(12).All(x => x == 0x6B) && bytes.Skip((3 + got) * sizeof(float)).All(x => x == 0x6B),
            "Delta damaged a byte-backed callback outside its returned region.");
    }

    private static void ScrubDifference()
    {
        var source = Enumerable.Repeat(.4f, Rate * 2).ToArray();
        var render = new RenderResult { Samples = Enumerable.Repeat(.1f, source.Length).ToArray(), RenderedLimiterEnabled = true };
        var state = State(Track(source, render));
        var grain = PlaybackService.CreateScrubGrain([state], true, .2);
        Require(grain.Length > 3000 && grain.Length <= 3840 * 2, "Delta scrub did not use a bounded grain.");
        Near(grain[2000], .3);
        Require(PlaybackService.CreateScrubGrain([state with { PeakDelta = null }], true, .2).Length == 0,
            "Pending Delta scrub played the original source instead.");
    }

    private static void DynamicsGuardDifference()
    {
        var input = Enumerable.Repeat(.4f, 2048).ToArray();
        const double backoff = 6;
        double scalar = Math.Pow(10, -backoff / 20);
        var render = new RenderResult
        {
            Samples = input.Select(x => (float)(x * scalar)).ToArray(),
            RenderedLimiterEnabled = true, MatchGainDb = -backoff, DynamicsGuardReductionDb = backoff,
        };
        var heard = Read(State(Track(input, render)));
        Require(heard.Length == input.Length, "The dynamics safeguard changed Delta duration.");
        for (int index = 0; index < heard.Length; index++) Near(heard[index], input[index] - render.Samples[index], 1e-7);
    }
}

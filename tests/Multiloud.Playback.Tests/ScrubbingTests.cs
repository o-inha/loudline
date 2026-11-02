using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

internal static class ScrubbingTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("Scrub grains use Original or Result and include their background exactly once", GrainModes),
        ("Scrub grains are bounded and have silent raised-cosine edges", GrainEdges),
        ("Scrub grains are silent for an unready result and honor cancellation", PendingAndCancellation),
        ("Scrub transport stays open between grains and resumes at the released clock", TransportResume),
        ("Scrub transport crossfades replacement grains and supports byte-backed callbacks", ReplaceAndAlias),
        ("Scrub generation cannot revive a transport after its endpoint consumed EOF", ClosedTransport),
        ("Scrubbing shared source ranges does not copy or read outside the selected clip", SharedRange),
    ];

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, double epsilon = 1e-6)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= epsilon, $"Expected {expected}, got {actual}");
    private static float[] Constant(float value, int frames = 48000) => Enumerable.Repeat(value, frames * 2).ToArray();
    private static PlaybackTrackState State(float[]? original, float[]? result, float[]? background = null)
        => new(Guid.NewGuid(), 48000, 2, (original ?? result)!.Length / 96000.0, original, result,
            BackgroundSamples: background, BackgroundRequired: background != null);

    private static void GrainModes()
    {
        var state = State(Constant(.2f), Constant(.7f), Constant(.1f));
        var original = PlaybackService.CreateScrubGrain([state], true, .2);
        var result = PlaybackService.CreateScrubGrain([state], false, .2);
        Near(original[2000], .3); Near(result[2000], .7);
        Require(original.Length == 7680 && result.Length == 7680, "A grain exceeded its 80 ms budget");
    }

    private static void GrainEdges()
    {
        var samples = Constant(.8f);
        var state = State(samples, samples);
        var grain = PlaybackService.CreateScrubGrain([state], true, .2);
        Near(grain[0], 0); Near(grain[1], 0); Near(grain[^1], 0); Near(grain[^2], 0);
        Near(grain[2000], .8); Require(grain[200] > 0 && grain[200] < .8, "Leading edge was not faded");
        var tail = PlaybackService.CreateScrubGrain([state], true, .999);
        Require(tail.Skip(96).All(value => value == 0), "An end grain exposed uninitialized or following data");
        Near(tail[94], 0);
        Require(samples.All(value => value == .8f), "Scrubbing changed the immutable source");
    }

    private static void PendingAndCancellation()
    {
        var state = State(Constant(.2f), null);
        Require(PlaybackService.CreateScrubGrain([state], false, .1).Length == 0, "An unready Result played Original silently instead");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        bool cancelled = false;
        try { PlaybackService.CreateScrubGrain([state], true, .1, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Cancelled scrub work still produced a grain");
    }

    private static void TransportResume()
    {
        var state = State(Constant(.2f), Constant(.2f));
        var transport = new PlaybackTransport(new PlaybackSequence(() => new[] { state }, true, 0));
        Require(transport.TryBeginScrub(.2), "Live transport refused scrub mode");
        transport.SetScrubGrain(Constant(.4f, 4), .2);
        var buffer = new float[20];
        Require(transport.Read(buffer, 0, buffer.Length) == 20, "Short grain closed the endpoint");
        Require(buffer.Take(8).All(value => value == .4f) && buffer.Skip(8).All(value => value == 0), "Grain tail did not become silence");
        Require(transport.Read(buffer, 0, buffer.Length) == 20 && buffer.All(value => value == 0), "Idle scrub should keep the endpoint alive with silence");
        Near(transport.PositionAt(20), .2);
        Require(transport.FinalReadSequence == null, "Scrub silence was mistaken for EOF");
        Require(transport.EndScrub(new PlaybackSequence(() => new[] { state }, true, .6)), "Transport did not leave scrub mode");
        Near(transport.PositionAt(0), .6);
        Require(transport.Read(buffer, 0, buffer.Length) == 20 && buffer.All(value => value == .2f), "Release did not restore normal audio");
        Require(!transport.SetScrubGrain(Constant(.9f, 2), .7), "A delayed grain reactivated scrubbing after release");
    }

    private static void ReplaceAndAlias()
    {
        var state = State(Constant(.2f), Constant(.2f));
        var transport = new PlaybackTransport(new PlaybackSequence(() => new[] { state }, true, 0));
        transport.TryBeginScrub(.1); transport.SetScrubGrain(Constant(.4f, 1000), .1);
        var before = new float[8]; transport.Read(before, 0, before.Length);
        transport.SetScrubGrain(Constant(0, 1000), .2);
        var bytes = Enumerable.Repeat((byte)0x6B, 5000).ToArray();
        var alias = new WaveBuffer(bytes).FloatBuffer;
        Require(transport.Read(alias, 4, 600) == 600, "Byte-backed scrub read failed");
        var decoded = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, decoded, 0, bytes.Length);
        Near(decoded[4], .4); Require(decoded[4 + 200] > 0 && decoded[4 + 200] < .4, "Old grain did not fade during replacement");
        Near(decoded[4 + 500], 0);
        Require(bytes.Take(16).All(value => value == 0x6B) && bytes.Skip(604 * 4).All(value => value == 0x6B), "Scrub callback overwrote adjacent host memory");
    }

    private static void ClosedTransport()
    {
        var state = State(Constant(.2f, 2), Constant(.2f, 2));
        var transport = new PlaybackTransport(new PlaybackSequence(() => new[] { state }, true, 0));
        var buffer = new float[16]; transport.Read(buffer, 0, 16); transport.Read(buffer, 0, 16);
        Require(!transport.TryBeginScrub(.1), "Scrub revived an endpoint that had already returned EOF");
    }

    private static void SharedRange()
    {
        var samples = Constant(.1f, 48000);
        for (int i = 24000 * 2; i < samples.Length; i++) samples[i] = .6f;
        var live = new LiveAudioSource(samples, 48000, 2, 24000, 24000);
        var state = new PlaybackTrackState(Guid.NewGuid(), 48000, 2, .5, samples, samples, LiveOriginal: live, LiveResult: live);
        Near(PlaybackService.CreateScrubGrain([state], true, .1)[2000], .6);
        Near(PlaybackService.CreateScrubGrain([state], false, .1)[2000], .6);
        Require(ReferenceEquals(live.Samples, samples), "Scrub descriptor did not retain shared PCM");
    }
}

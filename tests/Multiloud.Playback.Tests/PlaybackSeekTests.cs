using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// The same long-lived provider is handed to WASAPI. These checks simulate its
// queued output clock independently from the PCM callbacks that run ahead of it.
internal static class PlaybackSeekTests
{
    private const int Rate = 48000;

    public static readonly (string Name, Action Run)[] All =
    [
        ("Persistent playback transport accepts repeated seeks without replacing its provider", RepeatedSeek),
        ("Seek clock mapping accounts for audio already queued before the new epoch", QueuedAudioClock),
        ("Pending seek is rejected before active PCM or position is changed", PendingSeek),
        ("Persistent seek keeps byte-backed callback offsets and adjacent data intact", ByteBackedSeek),
        ("Final positive tail stays seekable until an actual EOF is returned", EofHandoff),
        ("Zero-length callback does not close the seekable transport", ZeroLengthRead),
        ("Current PCM stays pinned until seek resolves the latest source", PinnedSource),
        ("Seek clock handles invalid clocks and clamps to terminal track end", ClockBounds),
        ("Concurrent seeks switch whole byte-backed callbacks without mixing PCM epochs", ConcurrentSeek),
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance = 1e-8)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected:G12} ± {tolerance:G3}, got {actual:G12}");

    private static float[] Pattern(int frames, float offset = 0)
    {
        var samples = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = offset + frame / 65536f;
            samples[frame * 2 + 1] = -offset - frame / 32768f;
        }
        return samples;
    }

    private static PlaybackTrackState State(float[]? samples, int frames, Guid? id = null)
        => new(id ?? Guid.NewGuid(), Rate, 2, frames / (double)Rate, samples, samples);

    private static PlaybackSequence Sequence(PlaybackTrackState state, double seconds = 0)
        => new(() => new[] { state }, original: true, seconds);

    private static void RepeatedSeek()
    {
        var samples = Pattern(2000);
        var state = State(samples, 2000);
        var initial = Sequence(state);
        var transport = new PlaybackTransport(initial);
        var buffer = new float[16];
        Require(transport.Read(buffer, 0, 16) == 16, "Initial audio did not read");
        var firstSeek = Sequence(state, 300.25 / Rate);
        var latestSeek = Sequence(state, 500.25 / Rate);
        Require(transport.TrySeek(firstSeek) && transport.TrySeek(latestSeek), "A live seek was refused");
        Require(ReferenceEquals(transport.Sequence, latestSeek), "Transport did not keep the latest seek");
        Require(transport.Read(buffer, 0, 16) == 16, "Seeking lost the callback provider");
        for (int i = 0; i < 16; i++) Near(buffer[i], samples[500 * 2 + i]);
        Require(ReferenceEquals(transport.CurrentTrack, state), "Seek resolved an unrelated track");
    }

    private static void QueuedAudioClock()
    {
        var state = State(Pattern(4 * Rate), 4 * Rate);
        var transport = new PlaybackTransport(Sequence(state, 1));
        var buffer = new float[1440];
        Require(transport.Read(buffer, 0, 1200) == 1200, "Failed to simulate 600 queued frames");
        Near(transport.PositionAt(240.0 / Rate), 1 + 240.0 / Rate);
        Require(transport.TrySeek(Sequence(state, 2)), "Seek while old audio was queued failed");
        // Old frames are already in the endpoint buffer. Hold the requested cursor
        // at the new start until the hardware reaches the new rendered epoch.
        Near(transport.PositionAt(240.0 / Rate), 2);
        Near(transport.PositionAt(600.0 / Rate), 2);
        Require(transport.Read(buffer, 0, 1440) == 1440, "Failed to render after seek");
        Near(transport.PositionAt(720.0 / Rate), 2 + 120.0 / Rate);
        Require(transport.TrySeek(Sequence(state, 3)), "Repeated seek with queued audio failed");
        Near(transport.PositionAt(900.0 / Rate), 3);
        Near(transport.PositionAt(1320.0 / Rate), 3);
        Near(transport.PositionAt(1368.0 / Rate), 3.001);
    }

    private static void PendingSeek()
    {
        var samples = Pattern(40);
        var state = State(samples, 40);
        var initial = Sequence(state);
        var transport = new PlaybackTransport(initial);
        var buffer = new float[8];
        Require(transport.Read(buffer, 0, 8) == 8, "Initial PCM missing");
        var pending = Sequence(State(null, 40));
        bool rejected = false;
        try { transport.TrySeek(pending); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Pending track seek should explain that PCM is not ready");
        Require(ReferenceEquals(transport.Sequence, initial), "Rejected seek replaced the current sequence");
        Require(ReferenceEquals(transport.CurrentTrack, state), "Rejected seek unpinned current PCM");
        Near(transport.PositionAt(2.0 / Rate), 2.0 / Rate);
        Require(transport.Read(buffer, 0, 8) == 8, "Rejected seek interrupted current audio");
        for (int i = 0; i < buffer.Length; i++) Near(buffer[i], samples[8 + i]);
    }

    private static void ByteBackedSeek()
    {
        var samples = Pattern(100);
        var state = State(samples, 100);
        var transport = new PlaybackTransport(Sequence(state));
        var wave = new SampleToWaveProvider(transport);
        byte[] bytes = Enumerable.Repeat((byte)0x6B, 128).ToArray();
        Require(wave.Read(bytes, 16, 64) == 64, "Byte-backed initial callback failed");
        Require(transport.TrySeek(Sequence(state, 20.25 / Rate)), "Byte-backed stream could not seek");
        Array.Fill(bytes, (byte)0x6B);
        Require(wave.Read(bytes, 16, 64) == 64, "Byte-backed callback after seek failed");
        var decoded = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, decoded, 0, bytes.Length);
        for (int i = 0; i < 16; i++) Near(decoded[4 + i], samples[40 + i]);
        Require(bytes.Take(16).All(value => value == 0x6B), "Seek wrote before the callback offset");
        Require(bytes.Skip(80).All(value => value == 0x6B), "Seek wrote past the callback count");
    }

    private static void EofHandoff()
    {
        var state = State(Pattern(2), 2);
        var transport = new PlaybackTransport(Sequence(state));
        var buffer = new float[8];
        Require(transport.Read(buffer, 0, 8) == 4, "Short final callback lost its tail");
        Require(transport.Sequence.IsTerminal, "Fixture should reach logical end during its positive read");
        Require(transport.FinalReadSequence == null, "Positive tail was mistaken for WASAPI EOF");
        var restarted = Sequence(state);
        Require(transport.TrySeek(restarted), "Seek should be possible before actual EOF is handed off");
        Require(transport.Read(buffer, 0, 8) == 4, "Restarted short track lost data");
        Require(transport.Read(buffer, 0, 8) == 0, "Expected actual EOF after the positive tail");
        Require(ReferenceEquals(transport.FinalReadSequence, restarted), "Completion reported an old seek epoch");
        Require(!transport.TrySeek(Sequence(state)), "Cannot revive an endpoint after it has consumed EOF");
        Require(ReferenceEquals(transport.Sequence, restarted), "Rejected late seek changed the terminal epoch");
    }

    private static void ZeroLengthRead()
    {
        var state = State(Pattern(10), 10);
        var transport = new PlaybackTransport(Sequence(state));
        var buffer = new float[8];
        Require(transport.Read(buffer, 0, 0) == 0 && transport.Read(buffer, 0, 1) == 0,
            "Sub-frame callback should return no complete audio frames");
        Require(transport.FinalReadSequence == null, "A zero-length request marked the endpoint complete");
        Require(transport.TrySeek(Sequence(state, 2.25 / Rate)), "Zero-length request closed the transport");
        Require(transport.Read(buffer, 0, 4) == 4, "Audio missing after zero-length request");
    }

    private static void PinnedSource()
    {
        var firstSamples = Pattern(50, .1f);
        var newerSamples = Pattern(50, .4f);
        var original = State(firstSamples, 50);
        PlaybackTrackState[] published = [original];
        var transport = new PlaybackTransport(new PlaybackSequence(() => Volatile.Read(ref published), true, 0));
        var buffer = new float[8];
        Require(transport.Read(buffer, 0, 8) == 8, "Current track did not open");
        var replacement = State(newerSamples, 50, original.Id);
        Volatile.Write(ref published, [replacement]);
        Require(ReferenceEquals(transport.CurrentTrack, original), "A publication replaced current PCM inside a callback");
        Require(transport.Read(buffer, 0, 8) == 8, "Publication interrupted current audio");
        for (int i = 0; i < 8; i++) Near(buffer[i], firstSamples[8 + i]);
        Require(transport.TrySeek(new PlaybackSequence(() => Volatile.Read(ref published), true, 0)), "Seek did not resolve new publication");
        Require(ReferenceEquals(transport.CurrentTrack, replacement), "Seek kept outdated PCM pinned");
        Require(transport.Read(buffer, 0, 8) == 8, "New publication failed to play after seek");
        for (int i = 0; i < 8; i++) Near(buffer[i], newerSamples[i]);
    }

    private static void ClockBounds()
    {
        var state = State(Pattern(100), 100);
        var transport = new PlaybackTransport(Sequence(state, 20.25 / Rate));
        Near(transport.PositionAt(double.NaN), 20.25 / Rate);
        Near(transport.PositionAt(double.PositiveInfinity), 20.25 / Rate);
        Near(transport.PositionAt(-1), 20.25 / Rate);
        var buffer = new float[1000];
        Require(transport.Read(buffer, 0, buffer.Length) == 160, "Terminal clock fixture lost remaining frames");
        Near(transport.PositionAt(1000), 100.0 / Rate);
        // An explicit end seek is a valid target. It can hand EOF to the endpoint
        // normally, unlike a pending source which must be rejected before swap.
        Require(transport.TrySeek(Sequence(state, state.Duration)), "Seek to album end should be a valid transport operation");
        Near(transport.PositionAt(0), state.Duration);
        Require(transport.Read(buffer, 0, 2) == 0, "End seek failed to hand off EOF");
    }

    private static void ConcurrentSeek()
    {
        const int frames = 500_000;
        var first = State(Enumerable.Repeat(.125f, frames * 2).ToArray(), frames);
        var second = State(Enumerable.Repeat(.625f, frames * 2).ToArray(), frames);
        var transport = new PlaybackTransport(Sequence(first));
        using var start = new ManualResetEventSlim(false);
        var reads = Task.Run(() =>
        {
            start.Wait();
            // Match NAudio's actual host-buffer alias rather than only testing
            // ordinary managed float arrays. One Read must observe one epoch.
            var bytes = new byte[280 * sizeof(float)];
            var alias = new WaveBuffer(bytes);
            var decoded = new float[280];
            for (int iteration = 0; iteration < 1500; iteration++)
            {
                Array.Fill(bytes, (byte)0x5A);
                Require(transport.Read(alias.FloatBuffer, 3, 256) == 256, "Concurrent seek unexpectedly ended active PCM");
                Buffer.BlockCopy(bytes, 0, decoded, 0, bytes.Length);
                float epoch = decoded[3];
                Require(epoch is .125f or .625f, "Callback returned unknown epoch samples");
                for (int i = 1; i < 256; i++)
                    Require(decoded[3 + i] == epoch, "One callback mixed samples from different seek epochs");
                Require(bytes.Take(3 * sizeof(float)).All(value => value == 0x5A), "Concurrent callback changed its leading sentinel");
                Require(bytes.Skip(259 * sizeof(float)).All(value => value == 0x5A), "Concurrent callback changed its trailing sentinel");
            }
        });
        var seeks = Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < 1500; iteration++)
                Require(transport.TrySeek(Sequence(iteration % 2 == 0 ? second : first, (iteration % 64) / (double)Rate)),
                    "A live concurrent seek was unexpectedly refused");
        });
        start.Set();
        Task.WaitAll(reads, seeks);
        Require(double.IsFinite(transport.PositionAt(.5)), "Concurrent callback clock became invalid");
    }
}

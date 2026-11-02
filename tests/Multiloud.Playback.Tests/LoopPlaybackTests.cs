using Multiloud.App.Services;
using Multiloud.Core;

internal static class LoopPlaybackTests
{
    private const int Rate = 48000;
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Dual audition loops alternate A then B in one uninterrupted callback", Alternate),
        ("Loop labels retain A/B order when ranges overlap or run backwards on the timeline", LabelOrder),
        ("Loop toggles and handle updates follow the latest immutable state", Updates),
        ("Loop clock maps queued audio across repeats, state changes and manual seeks", ClockAndSeek),
        ("Loop arrivals wait for unavailable Results and resolve newly published PCM", Readiness),
        ("Pending overlapping loops retain B and explicit continuation points before Play", Continuation),
        ("Loop edge ramps remove discontinuities without adding or dropping time", EdgeRamps),
        ("Loop monitoring retains live background, Solo, Result and Delta routing", Monitoring),
        ("Short loops reuse bounded readers instead of allocating on every wrap", BoundedRepeats),
        ("Mixed-rate loop repeats retain resampling phase and channel routing", MixedRate),
        ("Scrub grains ignore loops and a released seek retains its chosen position", Scrubbing),
        ("Concurrent loop publications never tear the output clock history", ConcurrentClock),
    ];
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, double tolerance = 1e-6)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"Expected {expected:R}; got {actual:R}");
    private static LoopRange Range(int first, int end) => new(first / (double)Rate, end / (double)Rate);
    private static float[] Pattern(int frames) => Enumerable.Range(0, frames).SelectMany(i => new[] { i / 10000f, -i / 10000f }).ToArray();
    private static PlaybackTrackState State(float[] samples, float[]? result = null)
        => new(Guid.NewGuid(), Rate, 2, samples.Length / (double)(Rate * 2), samples, result ?? samples);
    private static PlaybackSequence Sequence(PlaybackTrackState state, PlaybackLoopState loops, int start = 0, bool original = true, bool enter = true)
    {
        PlaybackTrackState[] published = [state];
        return new(() => published, original, start / (double)Rate, () => loops, enter);
    }
    private static void Frames(float[] heard, int[] frames)
    {
        Require(heard.Length == frames.Length * 2, "Wrong callback frame count");
        for (int i = 0; i < frames.Length; i++) { Near(heard[i * 2], frames[i] / 10000f); Near(heard[i * 2 + 1], -frames[i] / 10000f); }
    }
    private static void Alternate()
    {
        var state = State(Pattern(100)); var loops = new PlaybackLoopState(true, Range(10, 14), true, Range(60, 66));
        var sequence = Sequence(state, loops);
        var heard = new float[40]; Require(sequence.Read(heard, 0, heard.Length) == heard.Length && !sequence.IsTerminal, "Loops stopped the endpoint or inserted a gap");
        Frames(heard, Enumerable.Range(10, 4).Concat(Enumerable.Range(60, 6)).Concat(Enumerable.Range(10, 4)).Concat(Enumerable.Range(60, 6)).ToArray());
        Near(sequence.StartSeconds, 10d / Rate); Near(sequence.StopPositionSeconds, 10d / Rate);
    }
    private static void LabelOrder()
    {
        foreach (var loops in new[] { new PlaybackLoopState(true, Range(50, 54), true, Range(4, 8)), new(true, Range(4, 8), true, Range(6, 10)) })
        {
            var sequence = Sequence(State(Pattern(100)), loops); var output = new float[24];
            Require(sequence.Read(output, 0, output.Length) == output.Length, "A/B label order ended playback");
            Frames(output, Enumerable.Range((int)Math.Round(loops.A!.StartSeconds * Rate), 4)
                .Concat(Enumerable.Range((int)Math.Round(loops.B!.StartSeconds * Rate), 4))
                .Concat(Enumerable.Range((int)Math.Round(loops.A.StartSeconds * Rate), 4)).ToArray());
        }
    }
    private static void Updates()
    {
        var state = State(Pattern(100)); PlaybackLoopState loops = new(true, Range(10, 14), false, Range(40, 44));
        var sequence = new PlaybackSequence(() => new[] { state }, true, 10d / Rate, () => Volatile.Read(ref loops));
        var head = new float[4]; sequence.Read(head, 0, 4); Frames(head, [10, 11]);
        Volatile.Write(ref loops, loops with { BEnabled = true });
        var next = new float[8]; sequence.Read(next, 0, 8); Frames(next, [12, 13, 40, 41]);
        Volatile.Write(ref loops, loops with { AEnabled = false });
        sequence.Read(next, 0, 8); Frames(next, [42, 43, 40, 41]);
        Volatile.Write(ref loops, loops with { BEnabled = false });
        sequence.Read(next, 0, 8); Frames(next, [42, 43, 44, 45]);
        using var service = new PlaybackService(); service.UpdateLoops(loops);
        Require(service.Loops == loops && service.Loops.A != null && service.Loops.B != null, "Disabled ranges were discarded");
    }
    private static void ClockAndSeek()
    {
        var state = State(Pattern(100)); PlaybackLoopState loops = new(true, Range(10, 14), true, Range(60, 66));
        var sequence = new PlaybackSequence(() => new[] { state }, true, 0, () => Volatile.Read(ref loops));
        var transport = new PlaybackTransport(sequence); var buffer = new float[80]; transport.Read(buffer, 0, buffer.Length);
        foreach (var (device, album) in new[] { (0, 10), (2, 12), (4, 60), (9, 65), (10, 10), (39, 65) }) Near(transport.PositionAt(device / (double)Rate), album / (double)Rate);
        Volatile.Write(ref loops, loops with { BEnabled = false }); transport.Read(buffer, 0, 8);
        Near(transport.PositionAt(39d / Rate), 65d / Rate); Near(transport.PositionAt(41d / Rate), 11d / Rate);
        var sought = new PlaybackSequence(() => new[] { state }, true, 30d / Rate, () => loops, enterLoops: false);
        Require(transport.TrySeek(sought), "Loop transport refused an explicit seek outside its range");
        var seekOutput = new float[4]; transport.Read(seekOutput, 0, 4); Frames(seekOutput, [30, 31]);
        Near(transport.PositionAt(0), 30d / Rate);
        var tail = new PlaybackSequence(() => new[] { state }, true, 98d / Rate, () => loops, enterLoops: false);
        var end = new float[8]; tail.Read(end, 0, 8); Frames(end, [98, 99, 10, 11]);
    }
    private static void Readiness()
    {
        var first = State(Enumerable.Repeat(.1f, 16).ToArray()); var next = State(Enumerable.Repeat(.3f, 16).ToArray());
        PlaybackTrackState[] states = [first, next with { PreviewSamples = null }];
        var loops = new PlaybackLoopState(true, Range(0, 4), true, Range(10, 14));
        var sequence = new PlaybackSequence(() => Volatile.Read(ref states), false, 0, () => loops);
        var output = new float[32]; Require(sequence.Read(output, 0, output.Length) == 8, "Loop skipped an unready target");
        Require(sequence.PendingTrackId == next.Id, "Loop did not expose the pending target track"); Near(sequence.StopPositionSeconds, 10d / Rate);
        sequence = new(() => Volatile.Read(ref states), false, 0, () => loops); sequence.Read(output, 0, 4);
        Volatile.Write(ref states, [first with { PreviewSamples = Enumerable.Repeat(.9f, 16).ToArray() }, next]);
        Require(sequence.Read(output, 0, 16) == 16, "Published loop target was not picked up");
        Require(output.Take(4).All(x => x == .1f) && output.Skip(4).Take(8).All(x => x == .3f) && output.Skip(12).Take(4).All(x => x == .9f), "Loop arrivals changed pinned PCM early or failed to refresh it on repeat");
    }
    private static void Continuation()
    {
        var state = State(Pattern(32)); PlaybackTrackState[] published = [state];
        var loops = new PlaybackLoopState(true, Range(0, 12), true, Range(4, 16));
        var sequence = new PlaybackSequence(() => Volatile.Read(ref published), false, 0, () => loops);
        var first = new float[24]; Require(sequence.Read(first, 0, first.Length) == first.Length, "Overlap fixture did not finish A");
        Volatile.Write(ref published, [state with { PreviewSamples = null }]);
        Require(sequence.Read(first, 0, 2) == 0 && sequence.PendingTrackId == state.Id && sequence.PendingLoopSlot == 1,
            "An unavailable overlapping B lost its loop itinerary");
        Near(sequence.StopPositionSeconds, 4d / Rate);
        Volatile.Write(ref published, [state]);
        var resumed = new PlaybackSequence(() => Volatile.Read(ref published), false, sequence.StopPositionSeconds,
            () => loops, enterLoops: false, preferredLoopSlot: sequence.PendingLoopSlot);
        var output = new float[28]; resumed.Read(output, 0, output.Length);
        Frames(output, Enumerable.Range(4, 12).Concat(new[] { 0, 1 }).ToArray());
        using var service = new PlaybackService(); service.UpdateTracks([state]); service.UpdateLoops(loops);
        Near(service.ResolvePlayPosition(30d / Rate), 0);
        service.RememberLoopContinuation(24d / Rate);
        Near(service.ResolvePlayPosition(24d / Rate), 24d / Rate);
        service.RememberLoopContinuation(15d / Rate, preferredSlot: 1);
        Near(service.ResolvePlayPosition(15d / Rate), 15d / Rate);
        service.UpdateLoops(new PlaybackLoopState(true, Range(20, 28)));
        Near(service.ResolvePlayPosition(15d / Rate), 20d / Rate);
    }
    private static void EdgeRamps()
    {
        var samples = Enumerable.Repeat(1f, 6000).ToArray(); var original = samples.ToArray();
        var loops = new PlaybackLoopState(true, Range(100, 900), true, Range(2000, 2800));
        var sequence = Sequence(State(samples), loops); var output = new float[3200];
        Require(sequence.Read(output, 0, output.Length) == output.Length, "Loop ramps changed the number of audio frames");
        Near(output[0], 1); Near(output[799 * 2], 0); Near(output[800 * 2], 0); Near(output[1000 * 2], 1); Near(output[1599 * 2], 0);
        var repeat = new float[2]; Require(sequence.Read(repeat, 0, 2) == 2, "Ramped repeat handed EOF to the endpoint"); Near(repeat[0], 0);
        Require(samples.AsSpan().SequenceEqual(original), "Loop ramps mutated source/export PCM");
        Near(sequence.PositionAtElapsed(800d / Rate), 2000d / Rate);
    }
    private static void Monitoring()
    {
        var source = Enumerable.Repeat(.4f, 64).ToArray(); var result = Enumerable.Repeat(.1f, 64).ToArray();
        var loops = new PlaybackLoopState(true, Range(3, 7));
        foreach (bool original in new[] { true, false })
        {
            var sequence = Sequence(State(source, result), loops, original: original); var output = new float[16]; sequence.Read(output, 0, output.Length);
            Require(output.All(x => Math.Abs(x - (original ? .4f : .1f)) < 1e-7), "Loop changed Original/Result routing");
        }
        var backgroundSource = new AudioTrack { Original = Enumerable.Repeat(.05f, 64).ToArray(), SampleRate = Rate, Channels = 2 };
        var layer = new BackgroundLayer { Clips = [new BackgroundClip { Source = backgroundSource }] };
        var live = new LiveAudioSource(source, Rate, 2, 0, 32, Background: layer);
        var liveState = State(source, result) with { LiveOriginal = live };
        var data = new float[16]; Sequence(liveState, loops).Read(data, 0, data.Length); Require(data.All(x => Math.Abs(x - .45f) < 1e-6), "Live loop lost or doubled its background");
        Sequence(liveState with { SoloBackground = layer }, loops).Read(data, 0, data.Length); Require(data.All(x => Math.Abs(x - .05f) < 1e-6), "Solo loops leaked music or used the wrong album position");
        var render = new RenderResult { Samples = result, RenderedLimiterEnabled = true };
        var delta = new PeakDeltaSource(source, Rate, 2, 0, 32, render);
        var deltaState = State(source, result) with { PeakDelta = delta, DeltaRequested = true };
        Sequence(deltaState, loops).Read(data, 0, data.Length); Require(data.All(x => Math.Abs(x - .3f) < 1e-6), "Loop Delta leaked a different monitoring route");
    }
    private static void BoundedRepeats()
    {
        var loops = new PlaybackLoopState(true, Range(2, 3), true, Range(8, 9)); var state = State(Pattern(20));
        var sequence = Sequence(state, loops); var buffer = new float[20000]; sequence.Read(buffer, 0, 100);
        long before = GC.GetAllocatedBytesForCurrentThread(); Require(sequence.Read(buffer, 0, buffer.Length) == buffer.Length, "One-frame loops did not continue");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated < 4096, $"Ten thousand tiny loop repeats allocated {allocated} bytes");
    }
    private static void MixedRate()
    {
        const int rate = 44100, prefix = 123, count = 5000;
        var root = Enumerable.Range(0, prefix + count + 100).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * 700 * i / rate))).ToArray();
        var live = new LiveAudioSource(root, rate, 1, prefix, count);
        var state = new PlaybackTrackState(Guid.NewGuid(), rate, 1, live.Duration, root, root, LiveOriginal: live, LiveResult: live);
        var loops = new PlaybackLoopState(true, Range(100, 900)); var sequence = Sequence(state, loops);
        var output = new float[4800]; Require(sequence.Read(output, 0, output.Length) == output.Length, "Mixed-rate loops changed frame counts");
        for (int frame = 100; frame < 600; frame++) { Near(output[frame * 2], output[(frame + 800) * 2], 1e-7); Near(output[frame * 2], output[frame * 2 + 1], 1e-7); }
    }
    private static void Scrubbing()
    {
        var state = State(Pattern(6000)); var loops = new PlaybackLoopState(true, Range(10, 14));
        using var service = new PlaybackService(); service.UpdateLoops(loops);
        var grain = PlaybackService.CreateScrubGrain(new[] { state }, true, 1000d / Rate);
        Near(grain[1000 * 2], state.OriginalSamples![2000 * 2]);
        var transport = new PlaybackTransport(Sequence(state, loops)); Require(transport.TryBeginScrub(1000d / Rate), "Loop transport refused scrubbing outside its range");
        Near(transport.PositionAt(1), 1000d / Rate);
        var released = new PlaybackSequence(() => new[] { state }, true, 1000d / Rate, () => loops, enterLoops: false);
        Require(transport.EndScrub(released), "Loop transport did not resume after scrubbing");
        var output = new float[4]; transport.Read(output, 0, output.Length); Frames(output, [1000, 1001]);
        Require(service.Loops == loops, "Scrubbing altered authored loops");
    }
    private static void ConcurrentClock()
    {
        var state = State(Pattern(1000)); PlaybackLoopState loops = new(true, Range(10, 100), true, Range(500, 700));
        var sequence = new PlaybackSequence(() => new[] { state }, true, 0, () => Volatile.Read(ref loops));
        using var start = new ManualResetEventSlim(false);
        var render = Task.Run(() =>
        {
            start.Wait(); var output = new float[256];
            for (int i = 0; i < 2000; i++)
            {
                Volatile.Write(ref loops, new PlaybackLoopState(true, Range(10 + i % 5, 100 + i % 5), i % 3 != 0, Range(500, 700)));
                Require(sequence.Read(output, 0, output.Length) == output.Length, "Concurrent loop updates ended playback");
            }
        });
        var clock = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 10000; i++)
            {
                double position = sequence.PositionAtElapsed((i % 2000) / 48000.0);
                Require(double.IsFinite(position) && position >= 0 && position <= state.Duration, "A torn loop epoch produced an invalid clock");
            }
        });
        start.Set(); Task.WaitAll(render, clock);
    }
}

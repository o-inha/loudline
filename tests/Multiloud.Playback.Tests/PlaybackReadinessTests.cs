using Multiloud.App.Services;

internal static class PlaybackReadinessTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("A ready first preview plays while the next track is pending", ReadyFirstTrack),
        ("A next preview published before its boundary continues seamlessly", NextBecomesReady),
        ("Pending boundaries stop exactly without skipping or using source audio", PendingBoundary),
        ("Seeking into a pending preview cannot play stale or original audio", SeekPending),
        ("Service rejects a pending seek before opening audio hardware", ServiceRejectsPendingSeek),
        ("Undecoded zero-duration tracks remain pending boundaries", UndecodedBoundary),
        ("Published edits cannot replace the current playback buffer", CurrentBufferIsImmutable),
        ("Original playback remains available while previews are pending", OriginalWhilePreviewPending),
    ];

    private const int Rate = 48000;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static float[] Samples(int frames, float offset)
    {
        var result = new float[frames * 2];
        for (int i = 0; i < result.Length; i++) result[i] = offset + (i + 1) * .001f;
        return result;
    }
    private static PlaybackTrackState State(float[] preview) => new(
        Guid.NewGuid(), Rate, 2, preview.Length / (double)(Rate * 2),
        Samples(preview.Length / 2, .7f), preview);
    private static void Match(float[] actual, int offset, IEnumerable<float> expected)
    {
        foreach (float value in expected)
        {
            Require(Math.Abs(actual[offset] - value) <= 1e-6, $"Unexpected PCM at {offset}: expected {value}, got {actual[offset]}");
            offset++;
        }
    }

    private static void ReadyFirstTrack()
    {
        var first = State(Samples(8, .1f)); var pending = State(Samples(8, .2f)) with { PreviewSamples = null };
        PlaybackTrackState[] states = [first, pending];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), false, 0);
        var buffer = new float[4];
        Require(provider.Read(buffer, 0, buffer.Length) == buffer.Length, "A pending later track blocked the ready first preview");
        Match(buffer, 0, first.PreviewSamples!.Take(4));
        Require(provider.PendingTrackId == null && !provider.IsComplete, "Playback stopped before reaching the pending boundary");
    }

    private static void NextBecomesReady()
    {
        var first = State(Samples(6, .1f)); var second = State(Samples(5, .3f));
        PlaybackTrackState[] states = [first, second with { PreviewSamples = null }];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), false, 0);
        var head = new float[4]; Require(provider.Read(head, 0, head.Length) == head.Length, "First preview did not start");
        Volatile.Write(ref states, [first, second]);
        var rest = new float[64];
        var expected = first.PreviewSamples!.Skip(head.Length).Concat(second.PreviewSamples!).ToArray();
        Require(provider.Read(rest, 2, 60) == expected.Length, "Ready next preview was not picked up at the track boundary");
        Match(rest, 2, expected);
        Require(provider.IsComplete && provider.PendingTrackId == null, "A fully ready sequence did not finish normally");
        Require(Math.Abs(provider.StopPositionSeconds - first.Duration - second.Duration) < 1e-10, "Natural completion reported the wrong album position");
    }

    private static void PendingBoundary()
    {
        var first = State(Samples(5, .1f)); var second = State(Samples(6, .3f)); var third = State(Samples(7, .5f));
        PlaybackTrackState[] states = [first, second with { PreviewSamples = null }, third];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), false, 0);
        var buffer = Enumerable.Repeat(99f, 64).ToArray();
        int read = provider.Read(buffer, 2, 60);
        Require(read == first.PreviewSamples!.Length, "Pending boundary must return only the ready first track's samples");
        Match(buffer, 2, first.PreviewSamples!);
        Require(buffer.Take(2).All(value => value == 99) && buffer.Skip(2 + read).All(value => value == 99), "Pending boundary read wrote beyond its returned sample count");
        Require(provider.PendingTrackId == second.Id && !provider.IsComplete, "Pending boundary was confused with natural album completion");
        Require(Math.Abs(provider.StopPositionSeconds - first.Duration) < 1e-10, "Pending boundary must retain the exact start of the unavailable track");
        Require(provider.Read(buffer, 0, buffer.Length) == 0, "Pending preview used original or later-track audio");
        Volatile.Write(ref states, [first, second, third]);
        Require(provider.Read(buffer, 0, buffer.Length) == 0, "A provider that reached pending EOF must remain stopped until playback is explicitly restarted");
    }

    private static void SeekPending()
    {
        var first = State(Samples(5, .1f)); var pending = State(Samples(8, .3f)) with { PreviewSamples = null };
        PlaybackTrackState[] states = [first, pending];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), false, first.Duration + 2.25 / Rate);
        var buffer = new float[8];
        Require(provider.PendingTrackId == pending.Id && provider.Read(buffer, 0, buffer.Length) == 0,
            "Seeking a pending preview must be rejected before any source/stale PCM can be played");
        Require(!provider.IsComplete, "Pending seek must not report natural album completion");
    }

    private static void CurrentBufferIsImmutable()
    {
        var first = State(Samples(8, .1f)); var second = State(Samples(6, .3f));
        PlaybackTrackState[] states = [first, second with { PreviewSamples = null }];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), false, 0);
        // The current buffer is pinned at sequence creation; future tracks resolve
        // from newly published snapshots only when their boundary is reached.
        Volatile.Write(ref states, [first with { PreviewSamples = Samples(8, .9f) }, second]);
        var head = new float[4]; Require(provider.Read(head, 0, head.Length) == head.Length, "Current preview did not start");
        Match(head, 0, first.PreviewSamples!.Take(4));
        Volatile.Write(ref states, [first with { PreviewSamples = null }, second]);
        var buffer = new float[64];
        var expected = first.PreviewSamples!.Skip(4).Concat(second.PreviewSamples!).ToArray();
        Require(provider.Read(buffer, 0, buffer.Length) == expected.Length, "Publishing pending edits interrupted an already pinned current buffer");
        Match(buffer, 0, expected);
    }

    private static void ServiceRejectsPendingSeek()
    {
        var first = State(Samples(5, .1f)); var pending = State(Samples(8, .3f)) with { PreviewSamples = null };
        using var service = new PlaybackService();
        service.UpdateTracks([first, pending]);
        bool rejected = false;
        try { service.Play(false, first.Duration + 2.25 / Rate); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("preview", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
        Require(rejected, "Service must reject pending processed audio before creating a WASAPI device");
        Require(!service.IsPlaying && Math.Abs(service.PositionSeconds - first.Duration) < 1e-10,
            "Rejected playback must preserve the pending track's boundary without starting an audio stream");
    }

    private static void UndecodedBoundary()
    {
        var first = State(Samples(5, .1f));
        var undecoded = new PlaybackTrackState(Guid.NewGuid(), Rate, 2, 0, null, null);
        var third = State(Samples(6, .3f));
        PlaybackTrackState[] states = [first, undecoded, third];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), false, 0);
        var buffer = new float[64];
        Require(provider.Read(buffer, 0, buffer.Length) == first.PreviewSamples!.Length,
            "An undecoded track with unknown duration was skipped");
        Require(provider.PendingTrackId == undecoded.Id && Math.Abs(provider.StopPositionSeconds - first.Duration) < 1e-10,
            "Unknown-duration track must block playback at its start until decoded");
    }

    private static void OriginalWhilePreviewPending()
    {
        var first = State(Samples(5, .1f)) with { PreviewSamples = null };
        var second = State(Samples(6, .3f)) with { PreviewSamples = null };
        PlaybackTrackState[] states = [first, second];
        var provider = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), true, 0);
        var buffer = new float[64]; var expected = first.OriginalSamples!.Concat(second.OriginalSamples!).ToArray();
        Require(provider.Read(buffer, 0, buffer.Length) == expected.Length, "Pending processed audio incorrectly blocked original playback");
        Match(buffer, 0, expected);
        Require(provider.IsComplete && provider.PendingTrackId == null, "Original playback did not complete normally");
    }
}

using NAudio.Wave;

namespace Multiloud.App.Services;

/// <summary>
/// Keeps one endpoint provider alive while switching immutable album streams.
/// Endpoint audio already queued before a seek drains normally; the new stream
/// begins on the following read, without reopening the device or its audio client.
/// </summary>
internal sealed class PlaybackTransport : ISampleProvider
{
    private readonly object gate = new();
    private PlaybackSequence sequence;
    private PlaybackSequence? finalReadSequence;
    private long emittedFrames, epochFirstFrame;
    private bool scrubbing;
    private double scrubPosition;
    private float[]? scrubGrain, fadingGrain;
    private int scrubIndex, fadingIndex, fadingFrames;
    private const int ScrubCrossfadeFrames = 240; // 5 ms, independent of pointer event rate.

    public PlaybackTransport(PlaybackSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        this.sequence = sequence;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public PlaybackSequence Sequence { get { lock (gate) return sequence; } }
    public PlaybackTrackState? CurrentTrack { get { lock (gate) return sequence.CurrentTrack; } }
    public PlaybackSequence? FinalReadSequence { get { lock (gate) return finalReadSequence; } }

    /// <summary>
    /// Replaces the source at a callback boundary. Once EOF has been returned to
    /// WASAPI its render thread may already be exiting, so the caller must Play
    /// again instead. A positive final tail is still replaceable until that EOF.
    /// </summary>
    public bool TrySeek(PlaybackSequence next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.PendingTrackId is not null)
            throw new InvalidOperationException("The audio at the requested position is not ready yet.");
        lock (gate)
        {
            if (finalReadSequence is not null) return false;
            sequence = next;
            epochFirstFrame = emittedFrames;
            return true;
        }
    }

    internal bool TryBeginScrub(double position)
    {
        lock (gate)
        {
            if (finalReadSequence is not null) return false;
            scrubbing = true; scrubPosition = position;
            scrubGrain = fadingGrain = null; scrubIndex = fadingIndex = fadingFrames = 0;
            return true;
        }
    }

    internal bool SetScrubGrain(float[] samples, double position)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % 2 != 0) throw new ArgumentException("A scrub grain must contain complete stereo frames.", nameof(samples));
        lock (gate)
        {
            if (!scrubbing || finalReadSequence is not null) return false;
            fadingGrain = scrubGrain; fadingIndex = scrubIndex;
            fadingFrames = Math.Min(ScrubCrossfadeFrames, Math.Max(0, (fadingGrain?.Length ?? 0) - fadingIndex) / 2);
            scrubGrain = samples; scrubIndex = 0; scrubPosition = position;
            return true;
        }
    }

    internal bool EndScrub(PlaybackSequence next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (gate)
        {
            if (!scrubbing || finalReadSequence is not null || next.PendingTrackId is not null) return false;
            sequence = next; epochFirstFrame = emittedFrames;
            scrubbing = false; scrubGrain = fadingGrain = null;
            return true;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // A zero-sized request is not EOF and must not close the seek window.
        if (count < WaveFormat.Channels) return 0;
        lock (gate)
        {
            if (finalReadSequence is not null) return 0;
            if (scrubbing)
            {
                count -= count % 2;
                for (int i = 0; i < count; i += 2)
                {
                    double tail = fadingFrames > 0 ? fadingFrames / (double)ScrubCrossfadeFrames : 0;
                    for (int channel = 0; channel < 2; channel++)
                    {
                        float value = scrubGrain != null && scrubIndex + channel < scrubGrain.Length ? scrubGrain[scrubIndex + channel] : 0;
                        if (fadingGrain != null && fadingFrames > 0 && fadingIndex + channel < fadingGrain.Length)
                            value += (float)(fadingGrain[fadingIndex + channel] * tail);
                        buffer[offset + i + channel] = value;
                    }
                    scrubIndex += 2; fadingIndex += 2;
                    if (fadingFrames > 0) --fadingFrames;
                }
                emittedFrames += count / 2;
                return count; // Silence between grains keeps the same endpoint alive.
            }
            int read = sequence.Read(buffer, offset, count);
            emittedFrames += read / WaveFormat.Channels;
            if (read == 0) finalReadSequence = sequence;
            return read;
        }
    }

    /// <summary>
    /// Maps the endpoint's elapsed playback clock into the latest seek's album
    /// time. A seek starts after previously emitted, buffered frames, so hold its
    /// visual cursor at the target until the endpoint reaches that frame. Device
    /// pause time is removed by PlaybackService before calling this method.
    /// </summary>
    internal int LoopSlotAt(double deviceSeconds)
    {
        lock (gate)
        {
            if (scrubbing) return -1;
            if (!double.IsFinite(deviceSeconds)) deviceSeconds = 0;
            double elapsed = Math.Max(0, deviceSeconds - epochFirstFrame / 48000.0);
            return sequence.LoopSlotAtElapsed(elapsed);
        }
    }

    public double PositionAt(double deviceSeconds)
    {
        lock (gate)
        {
            if (scrubbing) return scrubPosition;
            if (!double.IsFinite(deviceSeconds)) deviceSeconds = 0;
            double elapsed = Math.Max(0, deviceSeconds - epochFirstFrame / 48000.0);
            return sequence.PositionAtElapsed(elapsed);
        }
    }
}

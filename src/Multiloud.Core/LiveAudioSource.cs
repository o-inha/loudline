namespace Multiloud.Core;

/// <summary>
/// Immutable, non-destructive music range plus positioned background. No PCM is
/// copied, measured or mixed while this descriptor is created. Source arrays must
/// remain immutable. AlbumStartSeconds locates both resampling and the fixed bed.
/// </summary>
public sealed record LiveAudioSource(float[] Samples, int SampleRate, int Channels,
    int FrameOffset, int FrameCount, double AlbumStartSeconds = 0, double GainDb = 0,
    BackgroundLayer? Background = null, bool DualMono = false)
{
    public double Duration => FrameCount / (double)SampleRate;
    public bool HasBackground => Background is { Enabled: true, Clips.Count: > 0 };
    public LiveAudioReader CreateReader(int sampleRate, int channels) => new(this, sampleRate, channels);

    internal void Validate()
    {
        LoudnessAnalyzer.Validate(Samples, SampleRate, Channels);
        if (FrameOffset < 0 || FrameCount <= 0 || (long)FrameOffset + FrameCount > Samples.Length / Channels ||
            !double.IsFinite(AlbumStartSeconds) || !double.IsFinite(GainDb) || GainDb is < -120 or > 66)
            throw new ArgumentException("Invalid live audio range, timeline position or fixed gain.");
    }
}

/// <summary>
/// Callback-sized random access to a live mix. The source clock is derived from
/// the original PCM origin, so cutting a range never resets resampling phase or
/// discards the sinc context on either side of the new, purely structural edge.
/// Each reader belongs to one consumer and retains only bounded kernel storage.
/// </summary>
public sealed class LiveAudioReader
{
    private readonly LiveAudioSource source;
    private readonly int sampleRate, channels, rootFrames;
    private readonly double sourceOriginFrames, ratio, gain, monoGain;
    private readonly bool nativeClock;
    private readonly BackgroundRenderer.SincResampler? resampler;
    private readonly BackgroundRenderer.SegmentReader? background;
    public long FirstOutputFrame { get; }
    public long FrameCount { get; }

    public LiveAudioReader(LiveAudioSource source, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(source); source.Validate();
        if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 2)
            throw new ArgumentException("Live output must be 8–384 kHz mono or stereo.");
        this.source = source; this.sampleRate = sampleRate; this.channels = channels;
        rootFrames = source.Samples.Length / source.Channels;
        ratio = source.SampleRate / (double)sampleRate;
        gain = source.GainDb == 0 ? 1 : Math.Pow(10, source.GainDb / 20);
        // Preserve the music-only monitor's equal-power center routing. The
        // established background renderer uses dual-mono for its common mix.
        monoGain = source.Channels == 1 && channels == 2 && !source.HasBackground && !source.DualMono ? Math.Sqrt(.5) : 1;
        FirstOutputFrame = checked((long)Math.Round(source.AlbumStartSeconds * sampleRate));
        nativeClock = !source.HasBackground && !source.DualMono && source.SampleRate == sampleRate;
        FrameCount = nativeClock ? source.FrameCount
            : checked((long)Math.Round((source.AlbumStartSeconds + source.Duration) * sampleRate)) - FirstOutputFrame;
        // Keep the same source origin across adjacent range views of one file.
        sourceOriginFrames = (source.AlbumStartSeconds - source.FrameOffset / (double)source.SampleRate) * sampleRate;
        double nearestOrigin = Math.Round(sourceOriginFrames);
        if (Math.Abs(sourceOriginFrames - nearestOrigin) < 1e-8) sourceOriginFrames = nearestOrigin;
        if (source.SampleRate != sampleRate)
            resampler = BackgroundRenderer.SincResampler.Get(source.SampleRate, sampleRate, default);
        if (source.HasBackground) background = new BackgroundRenderer.SegmentReader(source.Background!, sampleRate, channels);
    }

    public int Read(long firstFrame, float[] buffer, int offset, int frames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (firstFrame < 0 || firstFrame > FrameCount || offset < 0 || frames < 0 ||
            (long)offset + (long)frames * channels > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(frames));
        ct.ThrowIfCancellationRequested();
        int count = (int)Math.Min(frames, FrameCount - firstFrame);
        if (count == 0) return 0;
        long absolute = FirstOutputFrame + firstFrame;
        if (background != null) background.ReadFrames(absolute, buffer, offset, count, ct);
        for (int frame = 0; frame < count; frame++)
        {
            if ((frame & 4095) == 0) ct.ThrowIfCancellationRequested();
            double position = nativeClock ? source.FrameOffset + firstFrame + frame
                : (absolute + frame - sourceOriginFrames) * ratio;
            double nearest = Math.Round(position);
            if (Math.Abs(position - nearest) < 1e-8) position = nearest;
            double left = BackgroundRenderer.Read(source.Samples, source.Channels, rootFrames, position, 0, resampler);
            double right = source.Channels == 2
                ? BackgroundRenderer.Read(source.Samples, 2, rootFrames, position, 1, resampler) : left;
            for (int channel = 0; channel < channels; channel++)
            {
                double music = channels == 1 ? (left + right) * .5 : channel == 0 ? left : right;
                int index = offset + frame * channels + channel;
                float value = (float)(music * gain * monoGain + (background == null ? 0 : buffer[index]));
                if (!float.IsFinite(value)) throw new InvalidDataException("The live mix contains an invalid audio sample.");
                buffer[index] = value;
            }
        }
        return count;
    }
}

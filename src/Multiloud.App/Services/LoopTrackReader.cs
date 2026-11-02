using Multiloud.Core;

namespace Multiloud.App.Services;

/// <summary>
/// Random-access monitoring of one pinned track. Readers and small scratch
/// buffers survive loop repeats, so short A/B regions never allocate a new
/// provider graph on each wrap and never render full difference/mix buffers.
/// </summary>
internal sealed class LoopTrackReader
{
    internal PlaybackTrackState State { get; }
    internal double AlbumOffset { get; }
    private readonly LiveAudioReader? music, bed;
    private readonly BackgroundRenderer.SegmentReader? solo;
    private readonly DeltaReader? delta;
    private readonly float gain;
    private readonly float[]? bedBuffer;

    internal LoopTrackReader(PlaybackTrackState state, bool original, double offset)
    {
        State = state; AlbumOffset = offset;
        double db = state.SoloBackground != null || state.DeltaRequested ? 0 : original ? state.OriginalAuditionGainDb : state.PreviewAuditionGainDb;
        gain = (float)Math.Pow(10, double.IsFinite(db) ? Math.Clamp(db, -120, 0) / 20 : 0);
        if (state.SoloBackground is { } background)
            solo = new BackgroundRenderer.SegmentReader(background with { Enabled = true }, 48000, 2);
        else if (state.DeltaRequested)
            delta = new(state.PeakDelta!, offset);
        else if ((original ? state.LiveOriginal : state.LiveResult) is { } source)
            music = source.CreateReader(48000, 2);
        else
        {
            var samples = (original ? state.OriginalSamples : state.PreviewSamples)!;
            music = new LiveAudioSource(samples, state.SampleRate, state.Channels, 0,
                samples.Length / state.Channels, offset).CreateReader(48000, 2);
            if (original && state.BackgroundSamples is { } bedSamples)
            {
                bed = new LiveAudioSource(bedSamples, state.SampleRate, state.Channels, 0,
                    bedSamples.Length / state.Channels, offset).CreateReader(48000, 2);
                bedBuffer = new float[4096];
            }
        }
    }

    internal int Read(long absoluteFrame, float[] buffer, int offset, int frames)
    {
        if (solo != null)
        {
            solo.Read(absoluteFrame / 48000.0, buffer, offset, frames);
            return frames;
        }
        if (delta != null) return delta.Read(absoluteFrame, buffer, offset, frames);
        long first = absoluteFrame - music!.FirstOutputFrame;
        if (first < 0 || first >= music.FrameCount) return 0;
        int read = music.Read(first, buffer, offset, frames);
        if (bed != null)
        {
            long bedFirst = absoluteFrame - bed.FirstOutputFrame;
            int got = bedFirst >= 0 && bedFirst <= bed.FrameCount ? bed.Read(bedFirst, bedBuffer!, 0, read) : 0;
            for (int i = 0; i < read * 2; i++) buffer[offset + i] = (buffer[offset + i] + (i < got * 2 ? bedBuffer![i] : 0)) * gain;
        }
        else if (gain != 1)
            for (int i = 0; i < read * 2; i++) buffer[offset + i] *= gain;
        return read;
    }

    private sealed class DeltaReader
    {
        private const int CacheFrames = 2048;
        private readonly PeakDeltaSource source;
        private readonly PeakDeltaSource.NativeReader native;
        private readonly RandomAccessResampler resampler;
        private readonly Func<int, int, double> sampleAt;
        private readonly int[] tags = new int[CacheFrames];
        private readonly float[] samples = new float[CacheFrames * 2];
        private readonly double offset, ratio, monoGain;
        internal DeltaReader(PeakDeltaSource source, double offset)
        {
            this.source = source; this.offset = offset;
            native = source.CreateNativeReader();
            resampler = new(source.SampleRate, 48000);
            ratio = source.SampleRate / 48000.0;
            monoGain = source.Channels == 1 ? Math.Sqrt(.5) : 1;
            Array.Fill(tags, -1);
            sampleAt = NativeSample;
        }
        private double NativeSample(int frame, int channel)
        {
            int slot = frame % CacheFrames;
            if (tags[slot] != frame)
            {
                native.ReadFrame(frame, out float left, out float right);
                samples[slot * 2] = left; samples[slot * 2 + 1] = right;
                tags[slot] = frame;
            }
            return samples[slot * 2 + channel];
        }
        internal int Read(long absoluteFrame, float[] buffer, int start, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                double position = (absoluteFrame + frame - offset * 48000) * ratio;
                double nearest = Math.Round(position);
                if (Math.Abs(position - nearest) < 1e-8) position = nearest;
                buffer[start + frame * 2] = (float)(resampler.Read(sampleAt, source.FrameCount, position, 0) * monoGain);
                buffer[start + frame * 2 + 1] = (float)(resampler.Read(sampleAt, source.FrameCount, position, source.Channels == 1 ? 0 : 1) * monoGain);
            }
            return frames;
        }
    }
}

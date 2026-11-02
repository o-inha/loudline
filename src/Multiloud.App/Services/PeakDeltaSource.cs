using System.IO;
using Multiloud.Core;
using NAudio.Wave;

namespace Multiloud.App.Services;

/// <summary>
/// Immutable recipe for the signal removed by peak protection. It retains the
/// existing source and Result, never a second album-sized difference buffer.
/// Input samples use the exact native/common format supplied to the processor.
/// </summary>
public sealed record PeakDeltaSource(float[] Source, int SampleRate, int Channels,
    int FrameOffset, int FrameCount, RenderResult Result)
{
    public double Duration => FrameCount / (double)SampleRate;

    /// <summary>Creates monitoring metadata without copying or scanning PCM.</summary>
    public static PeakDeltaSource? ForTrack(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Render is not { } render) return null;
        PeakDeltaSource source;
        if (render.BackgroundOriginalSamples is { Length: > 0 } prepared)
        {
            int rate = render.RenderedSampleRate > 0 ? render.RenderedSampleRate : 48000;
            int channels = render.RenderedChannels > 0 ? render.RenderedChannels : 2;
            if (channels is < 1 or > 2) return null;
            source = new(prepared, rate, channels, 0, prepared.Length / channels, render);
        }
        else source = new(track.Original, track.SampleRate, track.Channels,
            track.OriginalFrameOffset, track.OriginalFrameCount, render);
        return source.IsValid ? source : null;
    }

    internal bool IsValid => Source != null && Result != null &&
        SampleRate is >= 8000 and <= 384000 && Channels is >= 1 and <= 2 &&
        Source.Length % Channels == 0 && FrameOffset >= 0 && FrameCount > 0 &&
        (long)FrameOffset + FrameCount <= Source.Length / Channels &&
        double.IsFinite(Result.RenderedInputGainDb) && double.IsFinite(Result.RenderedLevelingStrength) &&
        double.IsFinite(Result.MatchGainDb) && double.IsFinite(Result.RenderedTrimDb) && double.IsFinite(Result.DynamicsGuardReductionDb) &&
        (!Result.RenderedLimiterEnabled ||
            Result.Samples.Length == (long)FrameCount * Channels &&
            (Result.BackgroundSamples == null || Result.BackgroundSamples.Length == Result.Samples.Length));

    internal ISampleProvider CreateSampleProvider(double seconds) => new Reader(this, seconds);

    internal NativeReader CreateNativeReader() => new(this);

    internal sealed class NativeReader
    {
        private readonly PeakDeltaSource source;
        private readonly double constantGain, inputGainDb, strength;
        internal NativeReader(PeakDeltaSource source)
        {
            if (!source.IsValid) throw new ArgumentException("Invalid peak difference monitoring source.", nameof(source));
            this.source = source;
            inputGainDb = Math.Clamp(source.Result.RenderedInputGainDb, -30, 30);
            strength = Math.Clamp(source.Result.RenderedLevelingStrength, 0, 1);
            constantGain = Math.Pow(10, (source.Result.MatchGainDb + source.Result.DynamicsGuardReductionDb +
                Math.Clamp(source.Result.RenderedTrimDb, -36, 36)) / 20);
        }
        internal void ReadFrame(int native, out float left, out float right)
        {
            left = right = 0;
            if (!source.Result.RenderedLimiterEnabled || native < 0 || native >= source.FrameCount) return;
            var render = source.Result;
            double db = strength > 0 && render.RenderedEnvelope.Count > 0
                ? Math.Clamp(AudioProcessor.EnvelopeGainAt(render.RenderedEnvelope, native / (double)source.SampleRate) * strength, -36, 36) : 0;
            double levelingGain = Math.Pow(10, (inputGainDb + db) / 20);
            for (int channel = 0; channel < source.Channels; channel++)
            {
                int index = native * source.Channels + channel;
                // Match both float roundings before the limiter. Including the
                // immutable bed before subtraction cancels its addition rounding.
                float leveled = (float)(source.Source[(source.FrameOffset + native) * source.Channels + channel] * levelingGain);
                float before = (float)(leveled * constantGain);
                if (render.BackgroundSamples != null) before = (float)(before + render.BackgroundSamples[index]);
                float difference = before - render.Samples[index];
                if (!float.IsFinite(difference)) throw new InvalidDataException("The peak difference contains an invalid audio sample.");
                if (channel == 0) left = difference; else right = difference;
            }
            if (source.Channels == 1) right = left;
        }
    }

    private sealed class Reader : ISampleProvider
    {
        private readonly PeakDeltaSource source;
        private readonly NativeReader native;
        private int position;
        public WaveFormat WaveFormat { get; }
        internal Reader(PeakDeltaSource source, double seconds)
        {
            if (!source.IsValid || !double.IsFinite(seconds)) throw new ArgumentException("Invalid peak difference monitoring source.", nameof(source));
            this.source = source; native = new(source);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, source.Channels);
            position = (int)Math.Clamp(Math.Floor(Math.Max(0, seconds) * source.SampleRate), 0, source.FrameCount);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int frames = Math.Min(count / source.Channels, source.FrameCount - position);
            for (int frame = 0; frame < frames; frame++)
            {
                native.ReadFrame(position + frame, out float left, out float right);
                buffer[offset + frame * source.Channels] = left;
                if (source.Channels == 2) buffer[offset + frame * 2 + 1] = right;
            }
            position += frames;
            return frames * source.Channels;
        }
    }
}

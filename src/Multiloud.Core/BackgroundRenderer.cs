namespace Multiloud.Core;

/// <summary>Deterministic timeline placement, resampling and fades, without looping.</summary>
public static class BackgroundRenderer
{
    public static float[] RenderSegment(BackgroundLayer layer, double albumStart, double duration,
        int sampleRate, int channels, CancellationToken ct = default)
    {
        if (!double.IsFinite(duration) || duration < 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be finite and nonnegative.");
        double frameLength = Math.Round(duration * sampleRate);
        if (!double.IsFinite(frameLength) || frameLength > Array.MaxLength / Math.Max(1, channels))
            throw new ArgumentOutOfRangeException(nameof(duration), "The requested background segment is too long.");
        var reader = new SegmentReader(layer, sampleRate, channels);
        var output = new float[(int)frameLength * channels];
        reader.ReadFresh(albumStart, output, (int)frameLength, ct);
        return output;
    }

    /// <summary>
    /// Bounded streaming access to the same absolute album-time background as
    /// RenderSegment. A reader belongs to one consumer; immutable source PCM is
    /// shared, and resampling kernels are reused across callback-sized reads.
    /// </summary>
    public sealed class SegmentReader
    {
        private readonly BackgroundLayer layer;
        private readonly int sampleRate, channels;
        private readonly Dictionary<int, SincResampler> resamplers = new();

        public SegmentReader(BackgroundLayer layer, int sampleRate, int channels)
        {
            ArgumentNullException.ThrowIfNull(layer);
            if (sampleRate is < 8000 or > 384000 || channels is < 1 or > 2)
                throw new ArgumentException("Background output must be 8–384 kHz mono or stereo.");
            if (!double.IsFinite(layer.GainDb) || layer.GainDb is < -60 or > 12)
                throw new ArgumentOutOfRangeException(nameof(layer), "Background gain must be between -60 and +12 dB.");
            ArgumentNullException.ThrowIfNull(layer.Clips);
            this.layer = layer; this.sampleRate = sampleRate; this.channels = channels;
        }

        public void Read(double albumStart, float[] output, int offset, int frames, CancellationToken ct = default)
            => ReadCore(albumStart, output, offset, frames, true, ct);

        /// <summary>Exact integer output clock for partition-invariant streaming exports.</summary>
        public void ReadFrames(long firstFrame, float[] output, int offset, int frames, CancellationToken ct = default)
            => ReadCore(firstFrame / (double)sampleRate, output, offset, frames, true, ct, firstFrame);

        internal void ReadFresh(double albumStart, float[] output, int frames, CancellationToken ct)
            => ReadCore(albumStart, output, 0, frames, false, ct);

        private void ReadCore(double albumStart, float[] output, int offset, int frames, bool clear, CancellationToken ct, double? exactOrigin = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            ct.ThrowIfCancellationRequested();
            if (!double.IsFinite(albumStart) || !double.IsFinite(albumStart * sampleRate))
                throw new ArgumentOutOfRangeException(nameof(albumStart));
            if (offset < 0 || frames < 0 || (long)offset + (long)frames * channels > output.Length)
                throw new ArgumentOutOfRangeException(nameof(frames));
            // Indexed clearing supports the byte-backed float buffer used by
            // audio hosts, whose runtime array type is not necessarily float[].
            if (clear) for (int i = 0; i < frames * channels; i++) output[offset + i] = 0;
            if (!layer.Enabled || frames == 0) return;
            double gain = Math.Pow(10, layer.GainDb / 20);
            double origin = exactOrigin ?? albumStart * sampleRate;
            foreach (var clip in layer.Clips)
            {
                ct.ThrowIfCancellationRequested();
                ValidateClip(clip);
                var source = clip.Source;
                double startFrame = clip.StartSeconds * sampleRate;
                double endFrame = clip.EndSeconds * sampleRate;
                int first = (int)Math.Clamp(Math.Ceiling(startFrame - origin), 0, frames);
                int end = (int)Math.Clamp(Math.Ceiling(endFrame - origin), 0, frames);
                if (first >= end) continue;
                if (source.Original.Length == 0)
                    throw new InvalidOperationException($"Background source '{source.Name}' is missing or has not been decoded. Relink it before previewing or exporting.");
                LoudnessAnalyzer.Validate(source.Original, source.SampleRate, source.Channels);
                SincResampler? resampler = null;
                if (source.SampleRate != sampleRate && !resamplers.TryGetValue(source.SampleRate, out resampler))
                {
                    resampler = SincResampler.Get(source.SampleRate, sampleRate, ct);
                    resamplers.Add(source.SampleRate, resampler);
                }
                int sourceFrames = source.Original.Length / source.Channels;
                for (int f = first; f < end; f++)
                {
                    if ((f & 4095) == 0) ct.ThrowIfCancellationRequested();
                    // Use one common sample lattice for continuous reads and
                    // identical PCM on either side of a music-file boundary.
                    double time = (origin + f - startFrame) / sampleRate;
                    if (time < 0 || time >= source.Duration) continue;
                    double fade = 1;
                    if (clip.FadeInSeconds > 0) fade *= Math.Clamp(time / clip.FadeInSeconds, 0, 1);
                    if (clip.FadeOutSeconds > 0) fade *= Math.Clamp((source.Duration - time) / clip.FadeOutSeconds, 0, 1);
                    double position = source.OriginalFrameOffset + time * source.SampleRate;
                    double nearest = Math.Round(position);
                    if (Math.Abs(position - nearest) < 1e-8) position = nearest;
                    double left = BackgroundRenderer.Read(source.Original, source.Channels, sourceFrames, position, 0, resampler);
                    double right = source.Channels == 2 ? BackgroundRenderer.Read(source.Original, 2, sourceFrames, position, 1, resampler) : left;
                    for (int c = 0; c < channels; c++)
                    {
                        double value = channels == 1 ? (left + right) * .5 : c == 0 ? left : right;
                        int index = offset + f * channels + c;
                        float mixed = (float)(output[index] + value * fade * gain);
                        if (!float.IsFinite(mixed)) throw new ArgumentException("Background audio contains non-finite or excessively large samples.");
                        output[index] = mixed;
                    }
                }
            }
        }
    }

    private static void ValidateClip(BackgroundClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip); ArgumentNullException.ThrowIfNull(clip.Source);
        double duration = clip.Source.Duration;
        if (!double.IsFinite(clip.StartSeconds) || clip.StartSeconds is < -86400 or > 86400
            || !double.IsFinite(duration) || duration < 0 || !double.IsFinite(clip.EndSeconds))
            throw new ArgumentException("Background clip positions must be finite and within one day of the album start.");
        if (!double.IsFinite(clip.FadeInSeconds) || !double.IsFinite(clip.FadeOutSeconds)
            || clip.FadeInSeconds < 0 || clip.FadeOutSeconds < 0
            || clip.FadeInSeconds > duration || clip.FadeOutSeconds > duration)
            throw new ArgumentException("Each background fade must be between zero and its source duration.");
    }

    internal static double Read(float[] pcm, int channels, int frames, double position, int channel, SincResampler? resampler)
    {
        // Preserve exact PCM for aligned, common-rate clips.
        int integer = (int)Math.Floor(position);
        if (resampler == null && position == integer)
        {
            float value = integer >= 0 && integer < frames ? pcm[integer * channels + channel] : 0;
            if (!float.IsFinite(value)) throw new ArgumentException("Background audio contains non-finite samples.");
            return value;
        }
        return (resampler ?? SincResampler.Unity).Read(pcm, channels, frames, position, channel);
    }

    // Blackman-windowed sinc, with anti-alias cutoff for downsampling. Linear
    // interpolation between phase kernels avoids audible phase quantization.
    internal sealed class SincResampler
    {
        private const int Phases = 512;
        internal static readonly SincResampler Unity = new(48000, 48000, default);
        private const long CacheBudget = 16 * 1024 * 1024;
        private static readonly object CacheGate = new();
        private sealed record CacheEntry(SincResampler Value, LinkedListNode<(int Source, int Target)> Node);
        private static readonly Dictionary<(int Source, int Target), CacheEntry> Cache = new();
        private static readonly LinkedList<(int Source, int Target)> CacheOrder = new();
        private static long cacheBytes;
        private readonly double[][] kernels;
        private readonly int radius;
        private long RetainedBytes => 128L + kernels.Sum(row => row.LongLength * sizeof(double) + 24) + kernels.LongLength * 8;

        // Kernels depend only on the two rates and are immutable. Sharing them
        // avoids rebuilding hundreds of phase tables for every scrub grain.
        // Readers still keep their timeline and mutable cursor state separate.
        internal static SincResampler Get(int sourceRate, int targetRate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var key = (sourceRate, targetRate);
            lock (CacheGate)
            {
                if (Cache.TryGetValue(key, out var found))
                {
                    CacheOrder.Remove(found.Node); CacheOrder.AddFirst(found.Node);
                    return found.Value;
                }
            }
            // Construct outside the lock so an uncommon new conversion cannot
            // block an existing playback consumer. Cancellation never publishes
            // a partial kernel or a faulted lazy value in the shared cache.
            var created = sourceRate == targetRate ? Unity : new SincResampler(sourceRate, targetRate, ct);
            ct.ThrowIfCancellationRequested();
            lock (CacheGate)
            {
                if (Cache.TryGetValue(key, out var found)) return found.Value;
                long bytes = created.RetainedBytes;
                if (bytes > CacheBudget) return created;
                while ((Cache.Count >= 32 || cacheBytes + bytes > CacheBudget) && CacheOrder.Last is { } last)
                {
                    var removed = Cache[last.Value]; cacheBytes -= removed.Value.RetainedBytes;
                    Cache.Remove(last.Value); CacheOrder.RemoveLast();
                }
                var node = CacheOrder.AddFirst(key);
                Cache.Add(key, new CacheEntry(created, node)); cacheBytes += bytes;
                return created;
            }
        }

        internal SincResampler(int sourceRate, int targetRate, CancellationToken ct)
        {
            double ratio = Math.Min(1, targetRate / (double)sourceRate);
            double cutoff = .94 * ratio;
            radius = (int)Math.Ceiling(32 / ratio);
            kernels = new double[Phases + 1][];
            for (int phase = 0; phase <= Phases; phase++)
            {
                if ((phase & 15) == 0) ct.ThrowIfCancellationRequested();
                var row = kernels[phase] = new double[radius * 2];
                double total = 0, fraction = phase / (double)Phases;
                for (int j = 0; j < row.Length; j++)
                {
                    double x = j - radius + 1 - fraction;
                    double sinc = Math.Abs(x) < 1e-12 ? cutoff : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);
                    double window = Math.Abs(x) >= radius ? 0 : .42 + .5 * Math.Cos(Math.PI * x / radius) + .08 * Math.Cos(2 * Math.PI * x / radius);
                    row[j] = sinc * window; total += row[j];
                }
                for (int j = 0; j < row.Length; j++) row[j] /= total;
            }
        }

        internal double Read(float[] pcm, int channels, int frames, double position, int channel)
        {
            int integer = (int)Math.Floor(position);
            double phase = (position - integer) * Phases;
            int index = Math.Clamp((int)phase, 0, Phases - 1);
            double fraction = phase - index;
            var a = kernels[index]; var b = kernels[index + 1];
            double value = 0;
            for (int j = 0, source = integer - radius + 1; j < a.Length; j++, source++)
            {
                if (source < 0 || source >= frames) continue;
                float sample = pcm[source * channels + channel];
                if (!float.IsFinite(sample)) throw new ArgumentException("Background audio contains non-finite samples.");
                value += sample * (a[j] + (b[j] - a[j]) * fraction);
            }
            return value;
        }

        internal double Read(Func<int, int, double> sampleAt, int frames, double position, int channel)
        {
            int integer = (int)Math.Floor(position);
            double phase = (position - integer) * Phases;
            int index = Math.Clamp((int)phase, 0, Phases - 1);
            double fraction = phase - index;
            var a = kernels[index]; var b = kernels[index + 1];
            double value = 0;
            for (int j = 0, source = integer - radius + 1; j < a.Length; j++, source++)
            {
                if (source < 0 || source >= frames) continue;
                double sample = sampleAt(source, channel);
                if (!double.IsFinite(sample)) throw new ArgumentException("Generated audio contains non-finite samples.");
                value += sample * (a[j] + (b[j] - a[j]) * fraction);
            }
            return value;
        }
    }
}

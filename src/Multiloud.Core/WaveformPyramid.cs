namespace Multiloud.Core;

/// <summary>Actual signed extrema across every channel in a frame interval.</summary>
public readonly record struct WaveformExtrema(float Min, float Max);

public readonly record struct WaveformQueryWork(int SummaryNodes, int RawFrames);
public readonly record struct WaveformBuildWork(int RawFrames, int SummaryNodes, bool UsedIncremental);

/// <summary>
/// Immutable multi-resolution extrema. Source PCM is referenced, not copied;
/// callers must keep that PCM immutable after building the pyramid. Queries
/// combine complete summary nodes and scan only incomplete boundary blocks.
/// </summary>
public sealed class WaveformPyramid
{
    private readonly float[] samples;
    private readonly WaveformExtrema[][] levels;
    private readonly WaveformPyramid? sourceView;
    private readonly int sourceFrameOffset;
    public int FrameCount { get; }
    public int Channels { get; }
    public int BaseFrames { get; }
    public int LevelCount => sourceView?.LevelCount ?? levels.Length;
    public WaveformBuildWork Work { get; }
    /// <summary>The immutable summary owner shared by all frame-range views.</summary>
    public WaveformPyramid SharedSummaryRoot => sourceView ?? this;
    /// <summary>
    /// Summary arrays and conservative overhead; ordinary pyramids exclude their
    /// caller-owned PCM. Range views also account for the additional parent PCM
    /// they retain independently of a sliced track's own copied samples.
    /// </summary>
    public long RetainedBytes { get; }

    private WaveformPyramid(float[] samples, int channels, int baseFrames,
        WaveformExtrema[][] levels, WaveformBuildWork work = default)
    {
        this.samples = samples; this.levels = levels;
        Channels = channels; BaseFrames = baseFrames; FrameCount = samples.Length / channels;
        Work = work;
        RetainedBytes = 128L + 24 + levels.LongLength * IntPtr.Size
            + levels.Sum(level => 24L + level.LongLength * 8);
    }

    private WaveformPyramid(WaveformPyramid source, int firstFrame, int frameCount)
    {
        // Flatten repeated slices: a query always makes at most one delegation
        // to the immutable original summaries, regardless of the number of cuts.
        sourceView = source.sourceView ?? source;
        sourceFrameOffset = source.sourceFrameOffset + firstFrame;
        samples = sourceView.samples; levels = [];
        Channels = source.Channels; BaseFrames = source.BaseFrames; FrameCount = frameCount;
        Work = new(0, 0, false);
        RetainedBytes = 128 + sourceView.RetainedBytes + sourceView.samples.LongLength * sizeof(float);
    }

    /// <summary>
    /// An exact frame-aligned view that shares existing PCM and summaries.
    /// Creating it does not scan samples or rebuild any resolution level.
    /// </summary>
    public WaveformPyramid Slice(int firstFrame, int endFrameExclusive)
    {
        if (firstFrame < 0 || endFrameExclusive > FrameCount || endFrameExclusive <= firstFrame)
            throw new ArgumentOutOfRangeException(nameof(firstFrame));
        return firstFrame == 0 && endFrameExclusive == FrameCount ? this :
            new(this, firstFrame, endFrameExclusive - firstFrame);
    }

    public static WaveformPyramid Build(float[] samples, int channels,
        CancellationToken ct = default, int baseFrames = 64)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ct.ThrowIfCancellationRequested();
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (samples.Length % channels != 0) throw new ArgumentException("Incomplete audio frame.", nameof(samples));
        if (baseFrames is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(baseFrames));
        int frames = samples.Length / channels;
        if (frames == 0) return new(samples, channels, baseFrames, []);
        int bins = (int)((frames + (long)baseFrames - 1) / baseFrames);
        var summaries = new WaveformExtrema[bins];
        for (int bin = 0; bin < bins; bin++)
        {
            if ((bin & 255) == 0) ct.ThrowIfCancellationRequested();
            int first = bin * baseFrames;
            int end = (int)Math.Min(frames, first + (long)baseFrames);
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = first * channels; i < end * channels; i++)
            {
                float value = samples[i];
                if (!float.IsFinite(value)) throw new ArgumentException("Audio contains non-finite samples.", nameof(samples));
                min = Math.Min(min, value); max = Math.Max(max, value);
            }
            summaries[bin] = new(min, max);
        }
        var built = new List<WaveformExtrema[]> { summaries };
        while (summaries.Length > 1)
        {
            ct.ThrowIfCancellationRequested();
            var parents = new WaveformExtrema[(summaries.Length + 1) / 2];
            for (int i = 0; i < parents.Length; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var a = summaries[i * 2];
                var b = i * 2 + 1 < summaries.Length ? summaries[i * 2 + 1] : a;
                parents[i] = new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
            }
            built.Add(parents); summaries = parents;
        }
        ct.ThrowIfCancellationRequested();
        return new(samples, channels, baseFrames, built.ToArray(), new(frames, built.Sum(level => level.Length), false));
    }

    /// <summary>
    /// Rebuilds only summaries intersecting changed frames, then their ancestors.
    /// PCM outside [first,end) must match the previous source. Old pyramids and
    /// PCM remain untouched; a different source length safely triggers Build.
    /// </summary>
    public WaveformPyramid Update(float[] newSamples, int firstChangedFrame, int endChangedFrameExclusive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newSamples); ct.ThrowIfCancellationRequested();
        if (newSamples.Length % Channels != 0) throw new ArgumentException("Incomplete audio frame.", nameof(newSamples));
        if (sourceView != null) return Build(newSamples, Channels, ct, BaseFrames);
        if (newSamples.Length != samples.Length) return Build(newSamples, Channels, ct, BaseFrames);
        int first = Math.Clamp(firstChangedFrame, 0, FrameCount);
        int end = Math.Clamp(endChangedFrameExclusive, 0, FrameCount);
        if (first >= end)
            return ReferenceEquals(newSamples, samples) ? this : new(newSamples, Channels, BaseFrames, levels, new(0, 0, true));
        if (first == 0 && end == FrameCount) return Build(newSamples, Channels, ct, BaseFrames);
        var updated = new WaveformExtrema[levels.Length][];
        int from = first / BaseFrames;
        int to = (int)((end + (long)BaseFrames - 1) / BaseFrames);
        int raw = 0, nodes = 0;
        updated[0] = (WaveformExtrema[])levels[0].Clone();
        for (int bin = from; bin < to; bin++)
        {
            if ((bin & 255) == 0) ct.ThrowIfCancellationRequested();
            int begin = bin * BaseFrames, finish = (int)Math.Min(FrameCount, begin + (long)BaseFrames);
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = begin * Channels; i < finish * Channels; i++)
            {
                float value = newSamples[i];
                if (!float.IsFinite(value)) throw new ArgumentException("Audio contains non-finite samples.", nameof(newSamples));
                min = Math.Min(min, value); max = Math.Max(max, value);
            }
            updated[0][bin] = new(min, max); raw += finish - begin; nodes++;
        }
        for (int level = 1; level < updated.Length; level++)
        {
            ct.ThrowIfCancellationRequested();
            from /= 2; to = (to + 1) / 2;
            updated[level] = (WaveformExtrema[])levels[level].Clone();
            for (int i = from; i < to; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var a = updated[level - 1][i * 2];
                var b = i * 2 + 1 < updated[level - 1].Length ? updated[level - 1][i * 2 + 1] : a;
                updated[level][i] = new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max)); nodes++;
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(newSamples, Channels, BaseFrames, updated, new(raw, nodes, true));
    }

    public WaveformExtrema Query(int firstFrame, int endFrameExclusive)
        => Query(firstFrame, endFrameExclusive, out _);

    /// <summary>
    /// Exact [first,end) extrema after clipping to the source. Default queries
    /// read at most 126 raw frames and twice LevelCount summary nodes, regardless
    /// of the duration represented by a display column. Empty/reversed ranges
    /// return (0,0). No allocation or shared mutable state is used.
    /// </summary>
    public WaveformExtrema Query(int firstFrame, int endFrameExclusive, out WaveformQueryWork work)
    {
        int first = Math.Clamp(firstFrame, 0, FrameCount);
        int end = Math.Clamp(endFrameExclusive, 0, FrameCount);
        if (first >= end) { work = default; return default; }
        if (sourceView != null)
            return sourceView.Query(sourceFrameOffset + first, sourceFrameOffset + end, out work);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        int raw = 0, nodes = 0;
        void ReadRaw(int from, int to)
        {
            for (int i = from * Channels; i < to * Channels; i++)
            {
                float value = samples[i]; min = Math.Min(min, value); max = Math.Max(max, value);
            }
            raw += to - from;
        }
        if (first % BaseFrames != 0)
        {
            int boundary = (int)Math.Min(end, ((long)first / BaseFrames + 1) * BaseFrames);
            ReadRaw(first, boundary); first = boundary;
        }
        if (first < end)
        {
            // The final partial base block is complete relative to the source
            // when the query ends at EOF, so it can use its summary as well.
            int tail = end == FrameCount ? end : Math.Max(first, end / BaseFrames * BaseFrames);
            int left = first / BaseFrames;
            int right = end == FrameCount ? levels[0].Length : tail / BaseFrames;
            int level = 0;
            while (left < right)
            {
                if ((left & 1) != 0)
                {
                    var value = levels[level][left++]; min = Math.Min(min, value.Min); max = Math.Max(max, value.Max); nodes++;
                }
                if ((right & 1) != 0)
                {
                    var value = levels[level][--right]; min = Math.Min(min, value.Min); max = Math.Max(max, value.Max); nodes++;
                }
                left >>= 1; right >>= 1; level++;
            }
            if (tail < end) ReadRaw(tail, end);
        }
        work = new(nodes, raw);
        return new(min, max);
    }

    /// <summary>Conservative sample coverage for fractional pixel/time boundaries.</summary>
    public WaveformExtrema QuerySeconds(double startSeconds, double endSeconds, int sampleRate)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds)) throw new ArgumentException("Waveform time bounds must be finite.");
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (endSeconds <= startSeconds) return default;
        int first = (int)Math.Clamp(Math.Floor(startSeconds * sampleRate), 0, FrameCount);
        int end = (int)Math.Clamp(Math.Ceiling(endSeconds * sampleRate), 0, FrameCount);
        return Query(first, end);
    }
}

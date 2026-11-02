namespace Multiloud.Core;

/// <summary>An immutable snapshot of a finished Result. Monitoring gains are deliberately absent.</summary>
public sealed record ExportAudioSource(float[] Samples, int SampleRate, int Channels, string Name = "",
    LiveAudioSource? LiveSource = null);

/// <summary>A half-open range on the common export sample clock. Fades only mark internal export joins.</summary>
public sealed record ExportAudioRange(long StartFrame, long EndFrame, bool FadeIn = false, bool FadeOut = false)
{
    public long FrameCount => EndFrame - StartFrame;
}

/// <summary>
/// Continuous, bounded streaming access to the finished album. Uniform formats copy their exact PCM;
/// mixed formats use one 48 kHz stereo clock. Export partitions never restart a source or resampler.
/// A timeline belongs to one export worker. Referenced source arrays must remain immutable.
/// </summary>
public sealed class ExportAudioTimeline
{
    private sealed record Part(ExportAudioSource Source, long First, long End,
        BackgroundRenderer.SegmentReader? Reader, LiveAudioReader? LiveReader = null);
    private readonly Part[] parts;
    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount { get; }
    public double Duration => FrameCount / (double)SampleRate;
    public IReadOnlyList<long> SourceBoundaries { get; }
    public IReadOnlyList<string> SourceNames { get; }

    public ExportAudioTimeline(IEnumerable<ExportAudioSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var input = sources.ToArray();
        if (input.Length == 0) throw new ArgumentException("The release has no finished audio to export.", nameof(sources));
        foreach (var source in input)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.LiveSource is { } live) live.Validate();
            else
            {
                LoudnessAnalyzer.Validate(source.Samples, source.SampleRate, source.Channels);
                if (source.Samples.Length == 0) throw new ArgumentException("Every export source must contain complete audio frames.", nameof(sources));
            }
        }
        static (int Rate, int Channels) Format(ExportAudioSource source) => source.LiveSource is { } live
            ? live.HasBackground ? (48000, 2) : (live.SampleRate, live.Channels)
            : (source.SampleRate, source.Channels);
        var format = Format(input[0]);
        bool uniform = input.All(source => Format(source) == format);
        SampleRate = uniform ? format.Rate : 48000;
        Channels = uniform ? format.Channels : 2;
        parts = new Part[input.Length];
        double position = 0;
        long first = 0;
        for (int index = 0; index < input.Length; index++)
        {
            var source = input[index];
            if (source.LiveSource is { } live)
            {
                var liveReader = live.CreateReader(SampleRate, Channels);
                long liveEnd = checked(first + liveReader.FrameCount);
                parts[index] = new Part(source, first, liveEnd, null, liveReader);
                position += live.Duration;
                first = liveEnd;
                continue;
            }
            position += source.Samples.Length / (double)(source.SampleRate * source.Channels);
            if (!double.IsFinite(position * SampleRate) || position * SampleRate > long.MaxValue)
                throw new ArgumentException("The release is too long to export.", nameof(sources));
            // Uniform formats already have an integer clock: never let summed
            // floating durations add/drop a sample across many tiny clips.
            long end = uniform ? checked(first + source.Samples.LongLength / Channels)
                : checked((long)Math.Round(position * SampleRate));
            BackgroundRenderer.SegmentReader? reader = null;
            if (source.SampleRate != SampleRate || source.Channels != Channels)
            {
                var audio = new AudioTrack { Original = source.Samples, SampleRate = source.SampleRate, Channels = source.Channels };
                reader = new BackgroundRenderer.SegmentReader(new BackgroundLayer
                {
                    Clips = [new BackgroundClip { Source = audio, StartSeconds = 0 }]
                }, SampleRate, Channels);
            }
            parts[index] = new Part(source, first, end, reader);
            first = end;
        }
        FrameCount = first;
        SourceBoundaries = Array.AsReadOnly(parts.Take(parts.Length - 1).Select(part => part.End).ToArray());
        SourceNames = Array.AsReadOnly(input.Select(source => source.Name).ToArray());
    }

    /// <summary>Fills up to frameCount frames. No allocation depends on the album or exported range length.</summary>
    public int Read(long firstFrame, float[] buffer, int offset, int frameCount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (firstFrame < 0 || firstFrame > FrameCount || offset < 0 || frameCount < 0 ||
            (long)offset + (long)frameCount * Channels > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        ct.ThrowIfCancellationRequested();
        int count = (int)Math.Min(frameCount, FrameCount - firstFrame);
        if (count == 0) return 0;
        long end = firstFrame + count;
        int index = PartAt(firstFrame);
        while (index < parts.Length && parts[index].First < end)
        {
            ct.ThrowIfCancellationRequested();
            var part = parts[index++];
            long start = Math.Max(firstFrame, part.First), last = Math.Min(end, part.End);
            int frames = checked((int)(last - start));
            if (frames <= 0) continue;
            int destination = checked(offset + (int)(start - firstFrame) * Channels);
            long localFrame = start - part.First;
            if (part.LiveReader is { } liveReader)
            {
                if (liveReader.Read(localFrame, buffer, destination, frames, ct) != frames)
                    throw new InvalidDataException("The live Result ended before its export boundary.");
            }
            else if (part.Reader == null)
            {
                int sourceOffset = checked((int)localFrame * Channels);
                Array.Copy(part.Source.Samples, sourceOffset, buffer, destination, frames * Channels);
                for (int sample = destination; sample < destination + frames * Channels; sample++)
                    if (!float.IsFinite(buffer[sample])) throw new InvalidDataException("The Result contains an invalid audio sample.");
            }
            else
            {
                // Source-local clock is stable across every output file and chunk.
                part.Reader.ReadFrames(localFrame, buffer, destination, frames, ct);
            }
        }
        return count;
    }

    private int PartAt(long frame)
    {
        int low = 0, high = parts.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (parts[mid].End <= frame) low = mid + 1; else high = mid;
        }
        return low;
    }
}

/// <summary>Canonical file partitions. Marker edits change these ranges and nothing in audio processing.</summary>
public static class ExportBoundaryPlan
{
    public static IReadOnlyList<ExportAudioRange> Single(ExportAudioTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return [new(0, timeline.FrameCount)];
    }

    public static IReadOnlyList<ExportAudioRange> Original(ExportAudioTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return Partition(timeline.FrameCount, timeline.SourceBoundaries, fades: false);
    }

    public static IReadOnlyList<ExportAudioRange> Custom(ExportAudioTimeline timeline, IEnumerable<double> markersSeconds)
    {
        ArgumentNullException.ThrowIfNull(timeline); ArgumentNullException.ThrowIfNull(markersSeconds);
        var markers = new List<long>();
        foreach (double seconds in markersSeconds)
        {
            if (!double.IsFinite(seconds)) throw new ArgumentException("Boundary times must be finite.", nameof(markersSeconds));
            if (seconds <= 0 || seconds >= timeline.Duration) continue;
            markers.Add(checked((long)Math.Round(seconds * timeline.SampleRate)));
        }
        return Partition(timeline.FrameCount, markers, fades: true);
    }

    private static IReadOnlyList<ExportAudioRange> Partition(long duration, IEnumerable<long> markers, bool fades)
    {
        var edges = markers.Where(frame => frame > 0 && frame < duration).Distinct().Order().Prepend(0).Append(duration).ToArray();
        var result = new ExportAudioRange[edges.Length - 1];
        for (int index = 0; index < result.Length; index++)
            result[index] = new(edges[index], edges[index + 1], fades && index > 0, fades && index < result.Length - 1);
        return result;
    }

    /// <summary>Linear edge-fade gain on a range. The release's natural beginning and ending remain untouched.</summary>
    public static double FadeGain(ExportAudioRange range, long localFrame, int sampleRate, double milliseconds)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range.StartFrame < 0 || range.FrameCount <= 0 || localFrame < 0 || localFrame >= range.FrameCount || sampleRate <= 0 ||
            !double.IsFinite(milliseconds) || milliseconds is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        long frames = Math.Min((long)Math.Round(milliseconds * sampleRate / 1000), range.FrameCount / 2);
        if (frames == 0) return 1;
        double gain = 1;
        if (range.FadeIn && localFrame < frames)
            gain *= frames == 1 ? 0 : localFrame / (double)(frames - 1);
        long remaining = range.FrameCount - 1 - localFrame;
        if (range.FadeOut && remaining < frames)
            gain *= frames == 1 ? 0 : remaining / (double)(frames - 1);
        return gain;
    }
}

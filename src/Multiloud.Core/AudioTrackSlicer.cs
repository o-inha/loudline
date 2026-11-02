namespace Multiloud.Core;

/// <summary>Frame-aligned, nondestructive source ranges for independently processed slices.</summary>
public static class AudioTrackSlicer
{
    public static bool HasRange(AudioTrack track) => track.SourceStartSeconds != 0 || track.SourceDurationSeconds.HasValue;

    public static float[] ExtractSource(float[] source, int sampleRate, int channels,
        double startSeconds, double? durationSeconds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sampleRate <= 0 || channels <= 0 || source.Length % channels != 0 ||
            !double.IsFinite(startSeconds) || startSeconds < 0 ||
            durationSeconds is { } duration && (!double.IsFinite(duration) || duration <= 0))
            throw new ArgumentException("Invalid audio source range.");
        ct.ThrowIfCancellationRequested();
        int frames = source.Length / channels;
        double start = Math.Round(startSeconds * sampleRate);
        double end = durationSeconds is { } length ? Math.Round((startSeconds + length) * sampleRate) : frames;
        if (start >= frames || end <= start) throw new InvalidOperationException("The saved slice is outside the replacement audio file.");
        int first = (int)Math.Min(start, frames), last = (int)Math.Min(end, frames);
        if (first == 0 && last == frames) return source;
        var result = new float[checked((last - first) * channels)];
        Copy(source, first * channels, result, result.Length, ct);
        return result;
    }

    public static (AudioTrack Left, AudioTrack Right) Split(AudioTrack track, double localSeconds,
        CancellationToken ct = default)
        => SplitCore(track, localSeconds, RegionStorage.Copy, ct);

    /// <summary>
    /// Splits an immutable decoded range without copying PCM. Repeated cuts
    /// keep referring directly to the original array rather than a view chain.
    /// </summary>
    public static (AudioTrack Left, AudioTrack Right) SplitShared(AudioTrack track, double localSeconds,
        CancellationToken ct = default)
        => SplitCore(track, localSeconds, RegionStorage.Shared, ct);

    /// <summary>
    /// Creates visible source regions without copying PCM or analyzing audio.
    /// Existing waveform summaries are shared through exact frame-range views.
    /// </summary>
    public static (AudioTrack Left, AudioTrack Right) CreateSplitSlots(AudioTrack track, double localSeconds)
        => SplitCore(track, localSeconds, RegionStorage.Slot, default);

    private enum RegionStorage { Copy, Shared, Slot }

    private static (AudioTrack Left, AudioTrack Right) SplitCore(AudioTrack track, double localSeconds,
        RegionStorage storage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.SampleRate <= 0 || track.Channels <= 0 || track.Original.Length % track.Channels != 0 ||
            !double.IsFinite(localSeconds)) throw new ArgumentException("Invalid slice position.");
        int totalFrames = track.OriginalFrameCount;
        if (track.OriginalFrameOffset < 0 || totalFrames < 0 ||
            (long)track.OriginalFrameOffset + totalFrames > track.Original.Length / track.Channels)
            throw new ArgumentException("The slice source range is outside its decoded audio.", nameof(track));
        double rounded = Math.Round(localSeconds * track.SampleRate);
        if (rounded <= 0 || rounded >= totalFrames) throw new ArgumentOutOfRangeException(nameof(localSeconds), "Slice inside the audio file.");
        int frame = (int)rounded;
        double cut = frame / (double)track.SampleRate;
        ct.ThrowIfCancellationRequested();
        var left = Region(track, 0, frame, storage, ct);
        var right = Region(track, frame, totalFrames, storage, ct);
        left.Envelope = CropEnvelope(track.Envelope, 0, cut);
        right.Envelope = CropEnvelope(track.Envelope, cut, track.Duration);
        // Project leveling owns continuity. Legacy derived join corrections are
        // cropped too, so splitting an older saved curve is lossless.
        left.TransitionEnvelope = CropEnvelope(track.TransitionEnvelope, 0, cut);
        right.TransitionEnvelope = CropEnvelope(track.TransitionEnvelope, cut, track.Duration);
        return (left, right);
    }

    public static List<EnvelopePoint> CropEnvelope(IReadOnlyList<EnvelopePoint> points, double start, double end)
    {
        if (points.Count == 0) return [];
        var result = new List<EnvelopePoint> { new(0, AudioProcessor.EnvelopeGainAt(points, start)) };
        result.AddRange(points.Where(point => point.Time > start && point.Time < end)
            .Select(point => point with { Time = point.Time - start }));
        result.Add(new(end - start, AudioProcessor.EnvelopeGainAt(points, end)));
        return result;
    }

    private static AudioTrack Region(AudioTrack source, int first, int end, RegionStorage storage, CancellationToken ct)
    {
        float[] samples = storage == RegionStorage.Copy ? new float[checked((end - first) * source.Channels)] :
            storage == RegionStorage.Shared ? source.Original : [];
        if (storage == RegionStorage.Copy)
            Copy(source.Original, checked((source.OriginalFrameOffset + first) * source.Channels), samples, samples.Length, ct);
        double duration = (end - first) / (double)source.SampleRate;
        return new AudioTrack
        {
            FilePath = source.FilePath, MissingAbsolutePath = source.MissingAbsolutePath,
            GeneratedSilenceSeconds = source.GeneratedSilenceSeconds,
            MergedSourceId = source.MergedSourceId, MergedSources = source.MergedSources,
            SourceStartSeconds = source.SourceStartSeconds + first / (double)source.SampleRate,
            SourceDurationSeconds = duration, SavedDuration = duration,
            SampleRate = source.SampleRate, Channels = source.Channels, Original = samples,
            OriginalFrameOffset = storage == RegionStorage.Shared ? source.OriginalFrameOffset + first : 0,
            OriginalFrameLength = storage == RegionStorage.Shared ? end - first : null,
            OriginalWaveform = source.OriginalWaveform?.Slice(first, end),
            OriginalPeaks = CropPublishedPeaks(source, first, end),
            InputGainDb = source.InputGainDb, TargetLufs = source.TargetLufs, TrimDb = source.TrimDb,
            Analysis = source.GeneratedSilenceSeconds.HasValue ? GeneratedSilenceSource.CreateAnalysis(end - first, source.SampleRate) : null,
            WorkState = TrackWorkState.Queued, Status = "Queued for analysis"
        };
    }

    private static List<PeakBin> CropPublishedPeaks(AudioTrack source, int first, int end)
    {
        if (source.OriginalPeaks.Count == 0) return [];
        int frames = source.OriginalFrameCount, count = source.OriginalPeaks.Count;
        int from = Math.Clamp((int)((long)first * count / frames), 0, count - 1);
        int to = Math.Clamp((int)(((long)end * count + frames - 1) / frames), from + 1, count);
        // A temporary bounded-resolution fallback is enough for the first UI
        // frame. Exact parent-pyramid tiles replace it asynchronously, and PCM
        // preparation publishes precise per-slice display peaks afterward.
        return source.OriginalPeaks.GetRange(from, to - from);
    }

    private static void Copy(float[] source, int offset, float[] destination, int count, CancellationToken ct)
    {
        const int block = 262144;
        for (int done = 0; done < count; done += block)
        {
            ct.ThrowIfCancellationRequested();
            Array.Copy(source, offset + done, destination, done, Math.Min(block, count - done));
        }
        ct.ThrowIfCancellationRequested();
    }
}

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Multiloud.Core;

namespace Multiloud.App.Services;

/// <summary>A measured preview and its display peaks. Treat the referenced buffers as immutable.</summary>
public sealed record CachedPreview(RenderResult Render, List<PeakBin> Peaks);

/// <summary>
/// A bounded in-memory LRU of completed preview variants. AudioTrack retains the
/// active preview independently, so eviction never invalidates the displayed audio.
/// </summary>
public sealed class PreviewCache
{
    private sealed record Entry(string Key, CachedPreview Preview, long Bytes);
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recent = new();
    private long cachedBytes;

    public PreviewCache(long maxBytes = 1024L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
    public int Count { get { lock (gate) return entries.Count; } }
    /// <summary>Conservative estimated retained bytes, including waveform peaks and entry overhead.</summary>
    public long CachedBytes { get { lock (gate) return cachedBytes; } }

    /// <summary>
    /// Canonicalizes the inputs actually used by AudioProcessor.Render. A track ID
    /// identifies an album slot; SourceId identifies that slot's immutable decoded
    /// PCM, including source variants retained by undo after a replacement.
    /// </summary>
    public static string CreateKey(AudioTrack track, ProcessingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);
        double targetLufs = track.TargetLufs ?? settings.TargetLufs;
        if (!double.IsFinite(track.TrimDb) || !double.IsFinite(track.InputGainDb) ||
            !double.IsFinite(settings.Strength) || !double.IsFinite(settings.MatchingStrength) ||
            !double.IsFinite(targetLufs) || !double.IsFinite(settings.CeilingDb) ||
            !Enum.IsDefined(settings.MaximizerStyle) || !double.IsFinite(settings.MaximizerCharacter) || !double.IsFinite(settings.TransientPreservation) ||
            !double.IsFinite(settings.BassProtection) || !double.IsFinite(settings.MaxSustainedReductionDb))
            throw new ArgumentException("Processing controls must be finite numbers.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> sourceId = stackalloc byte[16];
        track.SourceId.TryWriteBytes(sourceId);
        hash.AppendData(sourceId);
        AddInt64(hash, track.SampleRate);
        AddInt64(hash, track.Channels);
        AddInt64(hash, track.Original.LongLength);
        AddInt64(hash, track.OriginalFrameOffset);
        AddInt64(hash, track.OriginalFrameCount);
        AddDouble(hash, Math.Clamp(track.InputGainDb, -30, 30));
        AddDouble(hash, Math.Clamp(track.TrimDb, -36, 36));
        double matchingStrength = settings.MatchingEnabled ? Math.Clamp(settings.MatchingStrength, 0, 1) : 0;
        AddDouble(hash, matchingStrength);
        if (matchingStrength > 0) AddDouble(hash, targetLufs);
        AddInt64(hash, settings.LimiterEnabled ? 1 : 0);
        if (settings.LimiterEnabled)
        {
            // A bypassed peak-protection module never applies a hidden static
            // trim, so its ceiling cannot change the resulting audio either.
            AddDouble(hash, Math.Clamp(settings.CeilingDb, -12, 0));
            AddInt64(hash, (int)settings.MaximizerStyle);
            AddDouble(hash, Math.Clamp(settings.MaximizerCharacter, 0, 1));
            AddDouble(hash, Math.Clamp(settings.TransientPreservation, 0, 1));
            AddDouble(hash, Math.Clamp(settings.BassProtection, 0, 1));
            AddInt64(hash, settings.DynamicsGuardEnabled ? 1 : 0);
            if (settings.DynamicsGuardEnabled) AddDouble(hash, Math.Clamp(settings.MaxSustainedReductionDb, 0, 12));
        }

        double strength = settings.LevelingEnabled ? Math.Clamp(settings.Strength, 0, 1) : 0;
        List<EnvelopePoint> points = [];
        if (strength > 0)
        {
            // Reuse the renderer's filtered, sorted layer combination directly.
            points = EnvelopeMath.Canonicalize(TransitionDesigner.CombinedEnvelope(track));
            // An empty or zero-gain envelope applies unity at every frame, so
            // neither enabling it nor changing Strength changes the preview.
            if (points.Count == 0 || points.All(point => point.GainDb == 0)) strength = 0;
        }
        AddDouble(hash, strength);
        if (strength > 0)
        {
            // Hash the same normalized curve used by the renderer, without rounding:
            // fine manual edits must not alias another audible variant.
            AddInt64(hash, points.Count);
            foreach (var point in points)
            {
                AddDouble(hash, point.Time);
                AddDouble(hash, point.GainDb);
            }
        }
        return SourcePrefix(track) + Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Checks source ownership before retaining an active preview under a saved key.</summary>
    public static bool IsForSource(AudioTrack track, string? key)
    {
        ArgumentNullException.ThrowIfNull(track);
        return key != null && key.StartsWith(SourcePrefix(track), StringComparison.Ordinal);
    }

    public static string CreateKey(AudioTrack track, ProcessingSettings settings, BackgroundLayer background, double albumStart)
    {
        var music = CreateKey(track, settings);
        if (!background.Enabled || background.Clips.Count == 0) return music;
        long first = (long)Math.Round(albumStart * 48000), end = (long)Math.Round((albumStart + track.Duration) * 48000);
        int frames = checked((int)Math.Max(0, end - first));
        var bed = CreateBackgroundKey(background, first / 48000.0, frames / 48000.0, 48000, 2) ?? "silence:" + frames;
        return music + ":48k-stereo-background:" + bed;
    }

    /// <summary>Identity of the exact background segment, independent of music controls.</summary>
    public static string? CreateBackgroundKey(BackgroundLayer background, double albumStart, double duration, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(background);
        if (!background.Enabled) return null;
        if (!double.IsFinite(albumStart) || !double.IsFinite(duration) || duration < 0 ||
            !double.IsFinite(background.GainDb) || background.GainDb is < -60 or > 12)
            throw new ArgumentException("Background positions and gain must be finite and within range.");
        var clips = background.Clips.Where(clip => clip.Source.Duration > 0 &&
            clip.StartSeconds < albumStart + duration && clip.EndSeconds > albumStart).ToArray();
        if (clips.Length == 0 || duration == 0) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddInt64(hash, 1); AddDouble(hash, albumStart); AddDouble(hash, duration);
        AddInt64(hash, sampleRate); AddInt64(hash, channels); AddDouble(hash, background.GainDb);
        AddInt64(hash, clips.Length);
        Span<byte> sourceId = stackalloc byte[16];
        foreach (var clip in clips)
        {
            clip.Source.SourceId.TryWriteBytes(sourceId); hash.AppendData(sourceId);
            AddInt64(hash, clip.Source.SampleRate); AddInt64(hash, clip.Source.Channels);
            AddInt64(hash, clip.Source.Original.LongLength); AddDouble(hash, clip.Source.Duration);
            AddInt64(hash, clip.Source.OriginalFrameOffset); AddInt64(hash, clip.Source.OriginalFrameCount);
            AddDouble(hash, clip.StartSeconds); AddDouble(hash, clip.FadeInSeconds); AddDouble(hash, clip.FadeOutSeconds);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public bool TryGet(string key, [NotNullWhen(true)] out CachedPreview? preview)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node)) { preview = null; return false; }
            recent.Remove(node);
            recent.AddFirst(node);
            preview = node.Value.Preview;
            return true;
        }
    }

    /// <summary>
    /// Stores references without copying audio. Oversized variants are left with
    /// AudioTrack alone; they do not evict smaller useful entries from this cache.
    /// </summary>
    public void Store(string key, RenderResult render, List<PeakBin> peaks)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(peaks);
        long bytes = EstimateBytes(key, render, peaks);
        lock (gate)
        {
            if (entries.Remove(key, out var previous)) RemoveNode(previous);
            if (bytes > MaxBytes) return;
            while (cachedBytes > MaxBytes - bytes && recent.Last is { } oldest)
            {
                entries.Remove(oldest.Value.Key);
                RemoveNode(oldest);
            }
            var node = recent.AddFirst(new Entry(key, new CachedPreview(render, peaks), bytes));
            entries.Add(key, node);
            cachedBytes += bytes;
        }
    }

    public void RemoveTrack(Guid trackId)
    {
        string prefix = TrackPrefix(trackId);
        lock (gate)
        {
            foreach (var key in entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                var node = entries[key];
                entries.Remove(key);
                RemoveNode(node);
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            recent.Clear();
            cachedBytes = 0;
        }
    }

    /// <summary>
    /// Conservatively accounts for PCM, retained incremental state, rendered curve,
    /// peak pyramids, peaks, list capacity, strings and entry overhead. Shared buffers count
    /// separately per variant. Later intermediate-state eviction can only reduce
    /// actual usage beneath the cost recorded when this entry was stored.
    /// </summary>
    public static long EstimateBytes(string key, RenderResult render, List<PeakBin> peaks)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(peaks);
        return 1024L + key.Length * 2L + (render.Warning?.Length ?? 0) * 2L
            + render.Samples.LongLength * sizeof(float)
            + (render.BackgroundSamples?.LongLength ?? 0) * sizeof(float)
            + (render.BackgroundOriginalSamples?.LongLength ?? 0) * sizeof(float)
            + EstimateLiveRetainedBytes(render)
            + render.RetainedIntermediateBytes
            // The pyramid retains its source PCM by reference; RetainedBytes
            // covers only its additional levels, so PCM is counted once above.
            + (render.Waveform?.RetainedBytes ?? 0)
            + (long)render.RenderedEnvelope.Count * (IntPtr.Size + 24)
            + 64 + (long)render.ReductionTrace.Count * (IntPtr.Size + 32)
            + (long)peaks.Capacity * IntPtr.Size + (long)peaks.Count * 24;
    }

    private static long EstimateLiveRetainedBytes(RenderResult render)
    {
        if (render.LiveSource is not { } live) return 0;
        long bytes = 192;
        // Live playback retains source arrays instead of a rendered album-sized
        // mix. Count each distinct root once within this cache entry, including
        // backgrounds outside this clip that the immutable layer still retains.
        var arrays = new HashSet<float[]>(ReferenceEqualityComparer.Instance) { render.Samples };
        if (render.BackgroundSamples is { } backgroundSamples) arrays.Add(backgroundSamples);
        if (render.BackgroundOriginalSamples is { } originalSamples) arrays.Add(originalSamples);
        void AddArray(float[] samples)
        {
            if (arrays.Add(samples)) bytes += 24L + samples.LongLength * sizeof(float);
        }
        AddArray(live.Samples);
        if (live.Background is not { } layer) return bytes;
        bytes += 128L + (long)layer.Clips.Count * (IntPtr.Size + 96);
        var sources = new HashSet<AudioTrack>(ReferenceEqualityComparer.Instance);
        foreach (var clip in layer.Clips)
        {
            var source = clip.Source;
            AddArray(source.Original);
            if (!sources.Add(source)) continue;
            bytes += 256L + source.FilePath.Length * 2L + (source.OriginalWaveform?.RetainedBytes ?? 0)
                + (long)source.OriginalPeaks.Capacity * IntPtr.Size + (long)source.OriginalPeaks.Count * 24;
        }
        return bytes;
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        recent.Remove(node);
        cachedBytes -= node.Value.Bytes;
    }

    private static string TrackPrefix(Guid trackId) => trackId.ToString("N") + ":";
    private static string SourcePrefix(AudioTrack track) => TrackPrefix(track.Id) + track.SourceId.ToString("N") + ":";

    private static void AddDouble(IncrementalHash hash, double value)
        => AddInt64(hash, BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value));

    private static void AddInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

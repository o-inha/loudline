namespace Multiloud.Core;

public sealed record EnvelopePoint(double Time, double GainDb);
public sealed record LoudnessFrame(double Time, double Loudness);
public sealed record PeakBin(float Min, float Max);
public sealed record LimiterReductionPoint(double Time, double ReductionDb);

public enum TrackWorkState { Idle, Analyzing, Queued, Processing, Failed, Missing, Unprocessed }
public enum MaximizerStyle { Transparent, Punchy }

public sealed class AnalysisResult
{
    public double IntegratedLufs { get; init; } = -70;
    public double TruePeakDb { get; init; } = -120;
    public double LoudnessRange { get; init; }
    public List<LoudnessFrame> ShortTerm { get; init; } = [];
    public List<EnvelopePoint> SuggestedEnvelope { get; init; } = [];
    // Detector-only energy branches; standard LUFS and ShortTerm remain unmodified.
    public LevelingEnergyProfile? LevelingEnergy { get; init; }
}

public sealed record ProcessingSettings
{
    public double BassSensitivity { get; init; } = .25;
    public bool LevelingEnabled { get; init; } = true;
    public bool MatchingEnabled { get; init; } = true;
    public double MatchingStrength { get; init; } = 1;
    public bool LimiterEnabled { get; init; } = true;
    public MaximizerStyle MaximizerStyle { get; init; } = MaximizerStyle.Transparent;
    public double MaximizerCharacter { get; init; } = .5;
    public double TransientPreservation { get; init; } = .5;
    public double BassProtection { get; init; } = .5;
    public bool DynamicsGuardEnabled { get; init; }
    public double MaxSustainedReductionDb { get; init; } = 3;
    public double Strength { get; init; } = 0.65;
    public double TargetLufs { get; init; } = -14;
    public double CeilingDb { get; init; } = -1;
    public double MaxLevelingDb { get; init; } = 6;
    public double TransitionSeconds { get; init; } = 8;
}

public sealed class RenderResult
{
    internal RenderStageState? StageState;
    public long RetainedIntermediateBytes => Volatile.Read(ref StageState)?.RetainedBytes ?? 0;
    public double PreMatchLufs { get; init; } = -70;
    public double RenderedMatchingStrength { get; init; }
    public double RenderedInputGainDb { get; init; }
    public double RenderedLevelingStrength { get; init; }
    public double RenderedTargetLufs { get; init; } = -14;
    public double RenderedTrimDb { get; init; }
    public bool RenderedLimiterEnabled { get; init; }
    public double RenderedCeilingDb { get; init; } = -1;
    public MaximizerStyle RenderedMaximizerStyle { get; init; } = MaximizerStyle.Transparent;
    public double RenderedMaximizerCharacter { get; init; } = .5;
    public double RenderedTransientPreservation { get; init; } = .5;
    public double RenderedBassProtection { get; init; } = .5;
    public bool RenderedDynamicsGuardEnabled { get; init; }
    public double RenderedMaxSustainedReductionDb { get; init; } = 3;
    public double DynamicsGuardReductionDb { get; init; }
    public double SustainedReductionDb { get; init; }
    public IReadOnlyList<EnvelopePoint> RenderedEnvelope { get; init; } = Array.Empty<EnvelopePoint>();
    public RenderWorkInfo Work { get; init; } = new(false, 0, 0, 0, 0, 0, 0);
    public required float[] Samples { get; init; }
    // A module-off Result can be an immutable source-range recipe instead of
    // an album-length mix buffer. It is mixed only as playback/export reads it.
    public LiveAudioSource? LiveSource { get; init; }
    // The positioned bed stays fixed through music-only gain and peak control.
    // As with Samples, callers must keep this published PCM immutable.
    public float[]? BackgroundSamples { get; init; }
    public bool IncludesBackground => BackgroundSamples is { Length: > 0 };
    // A zero-filled bed still carries readiness metadata, but its retained
    // output meter and peak cache are ordinary music-only processing stages.
    public bool HasAudibleBackground { get; init; }
    public double? OriginalMixLufs { get; init; }
    public double? BackgroundLufs { get; init; }
    public double? PreMatchMixLufs { get; init; }
    // Assigned before publication when album background playback uses a common
    // output format. Zero retains the source-format convention for legacy renders.
    public int RenderedSampleRate { get; set; }
    public int RenderedChannels { get; set; }
    public float[]? BackgroundOriginalSamples { get; set; }
    // Built off the UI thread before this render is published; the pyramid
    // itself and its referenced Samples stay immutable during display/playback.
    public WaveformPyramid? Waveform { get; set; }
    public double MatchGainDb { get; init; }
    public double OutputLufs { get; init; }
    public double TruePeakDb { get; init; }
    public double MaxReductionDb { get; init; }
    public double AverageReductionDb { get; init; }
    public double LimitedPercent { get; init; }
    public double HeavyLimitingPercent { get; init; }
    public double SafetyAttenuationDb { get; init; }
    // Maximum attenuation in each 100 ms block. With selective bass control,
    // this describes the most attenuated band, rather than one full-band gain.
    public IReadOnlyList<LimiterReductionPoint> ReductionTrace { get; init; } = Array.Empty<LimiterReductionPoint>();
    public string? Warning { get; init; }
}

public sealed record RenderWorkInfo(bool UsedIncrementalStage, int LevelingFramesProcessed,
    int DetectorFramesProcessed, int LoudnessFramesProcessed, int FinalPeakFramesProcessed,
    int OutputChangedStartFrame, int OutputChangedEndFrame);

public sealed class AudioTrack
{
    private sealed record OriginalRegionCache(float[] Root, int FrameOffset, int FrameCount, int Channels, float[] Samples);
    private OriginalRegionCache? _originalRegionCache;
    private float[] _original = [];
    private int _originalFrameOffset;
    private int? _originalFrameLength;
    public Guid Id { get; set; } = Guid.NewGuid();
    // A relink keeps the slot ID but publishes a fresh immutable audio source.
    public Guid SourceId { get; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";
    // Synthetic silence has no external file. Slices retain this full source
    // duration while SourceStartSeconds/SourceDurationSeconds select a range.
    public double? GeneratedSilenceSeconds { get; set; }
    // A merged source keeps a flat reconstruction recipe. Its decoded PCM is
    // immutable; slices share the recipe identity and select their own range.
    public Guid? MergedSourceId { get; set; }
    public IReadOnlyList<MergedSourcePart>? MergedSources { get; set; }
    // FilePath points at the untouched source. Seconds preserve the region if a
    // relink changes the source sample rate; native frame boundaries are used
    // when decoding. An in-memory slice can share Original with other slices.
    public double SourceStartSeconds { get; set; }
    public double? SourceDurationSeconds { get; set; }
    public string? MissingAbsolutePath { get; set; }
    public double SavedDuration { get; set; }
    public string Name => Path.GetFileName(FilePath);
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public float[] Original
    {
        get => _original;
        set { if (ReferenceEquals(_original, value)) return; _original = value; Volatile.Write(ref _originalRegionCache, null); }
    }
    // The immutable decoded array can contain more than this clip's region.
    // Offsets are into that array, independently of the saved file-time range.
    // Null length means all complete frames remaining after the offset.
    public int OriginalFrameOffset
    {
        get => _originalFrameOffset;
        set { if (_originalFrameOffset == value) return; _originalFrameOffset = value; Volatile.Write(ref _originalRegionCache, null); }
    }
    public int? OriginalFrameLength
    {
        get => _originalFrameLength;
        set { if (_originalFrameLength == value) return; _originalFrameLength = value; Volatile.Write(ref _originalRegionCache, null); }
    }
    public int OriginalFrameCount => OriginalFrameLength ??
        (Channels > 0 ? Original.Length / Channels - OriginalFrameOffset : 0);
    public int OriginalSampleOffset => checked(OriginalFrameOffset * Channels);
    public int OriginalSampleCount => checked(OriginalFrameCount * Channels);
    public ReadOnlyMemory<float> OriginalRegion => Original.AsMemory(OriginalSampleOffset, OriginalSampleCount);
    // A worker-only processor may need contiguous region PCM. Keep that derived
    // array separate from Original so toggling back to live monitoring retains
    // the original root, sinc context and source phase across every slice.
    public long RetainedRegionBytes => Volatile.Read(ref _originalRegionCache)?.Samples.LongLength * sizeof(float) ?? 0;
    public WaveformPyramid? OriginalWaveform { get; set; }
    public double Duration => Original.Length > 0 ? OriginalFrameCount / (double)SampleRate : double.IsFinite(SavedDuration) ? Math.Max(0, SavedDuration) : 0;

    /// <summary>
    /// Materializes only this region for processors requiring a contiguous
    /// array. Whole-array sources are returned directly; playback and slicing
    /// should consume the range instead. Call on a worker for large regions.
    /// </summary>
    public float[] GetOriginalRegion(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        float[] root = Original;
        int frameOffset = OriginalFrameOffset, frames = OriginalFrameCount, channels = Channels;
        int offset = checked(frameOffset * channels), count = checked(frames * channels);
        if (offset < 0 || count < 0 || (long)offset + count > root.Length)
            throw new InvalidOperationException("The audio region is outside its decoded source.");
        if (offset == 0 && count == root.Length) return root;
        bool Matches(OriginalRegionCache? cached) => cached != null && ReferenceEquals(cached.Root, root) &&
            cached.FrameOffset == frameOffset && cached.FrameCount == frames && cached.Channels == channels;
        var previous = Volatile.Read(ref _originalRegionCache);
        if (Matches(previous)) return previous!.Samples;
        var result = new float[count];
        const int block = 262144;
        for (int copied = 0; copied < count;)
        {
            ct.ThrowIfCancellationRequested();
            int length = Math.Min(block, count - copied);
            Array.Copy(root, offset + copied, result, copied, length);
            copied += length;
        }
        ct.ThrowIfCancellationRequested();
        var entry = new OriginalRegionCache(root, frameOffset, frames, channels, result);
        while (true)
        {
            if (!ReferenceEquals(Original, root) || OriginalFrameOffset != frameOffset ||
                OriginalFrameCount != frames || Channels != channels) return result;
            var winner = Interlocked.CompareExchange(ref _originalRegionCache, entry, previous);
            if (ReferenceEquals(winner, previous)) return result;
            if (Matches(winner)) return winner!.Samples;
            // Source slots are immutable while workers consume them. If a
            // caller replaces one concurrently, return its captured region
            // without retaining a stale cache on the replacement slot.
            if (!ReferenceEquals(Original, root) || OriginalFrameOffset != frameOffset ||
                OriginalFrameCount != frames || Channels != channels) return result;
            previous = winner;
        }
    }
    public AnalysisResult? Analysis { get; set; }
    public RenderResult? Render { get; set; }
    public List<EnvelopePoint> Envelope { get; set; } = [];
    // Derived join corrections are separate so reordering never overwrites
    // authored/manual envelope points or accumulates previous edge adjustments.
    public List<EnvelopePoint> TransitionEnvelope { get; set; } = [];
    public double InputGainDb { get; set; }
    public double? TargetLufs { get; set; }
    public double TrimDb { get; set; }
    public string Status { get; set; } = "Waiting";
    public TrackWorkState WorkState { get; set; }
    public List<PeakBin> OriginalPeaks { get; set; } = [];
    public List<PeakBin> PreviewPeaks { get; set; } = [];
}

public sealed record MergedSourcePart(AudioTrack Source, double GainDb = 0);

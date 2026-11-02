using System.Runtime.CompilerServices;

namespace Multiloud.Core;

/// <summary>Project-owned silence, reconstructed from metadata without an external audio file.</summary>
public static class GeneratedSilenceSource
{
    public const double MinimumSeconds = .001;
    public const double MaximumSeconds = 600;
    private sealed record Root(float[] Samples, WaveformPyramid Waveform, List<PeakBin> Peaks,
        int SampleRate, int Channels, AnalysisResult Analysis);
    private static readonly object Gate = new();
    private static readonly List<WeakReference<Root>> Recent = [];
    private static readonly ConditionalWeakTable<float[], Root> Owners = new();

    public static AudioTrack Create(double seconds, int sampleRate = 48000, int channels = 2,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(seconds) || seconds is < MinimumSeconds or > MaximumSeconds ||
            sampleRate is < 8000 or > 384000 || channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Silence duration must be between 0.001 and 600 seconds.");
        int frames = Math.Max(checked((int)Math.Ceiling(MinimumSeconds * sampleRate)),
            checked((int)Math.Round(seconds * sampleRate)));
        int samples = checked(frames * channels);
        Root? root = null;
        lock (Gate)
        {
            Recent.RemoveAll(item => !item.TryGetTarget(out _));
            foreach (var item in Recent)
                if (item.TryGetTarget(out var candidate) && candidate.Samples.Length == samples &&
                    candidate.SampleRate == sampleRate && candidate.Channels == channels)
                { root = candidate; break; }
        }
        if (root == null)
        {
            var pcm = new float[samples];
            var waveform = WaveformPyramid.Build(pcm, channels, ct);
            var peaks = Enumerable.Repeat(new PeakBin(0, 0), Math.Min(frames, 16000)).ToList();
            root = new(pcm, waveform, peaks, sampleRate, channels, CreateAnalysis(frames, sampleRate));
            lock (Gate)
            {
                Owners.Add(pcm, root);
                Recent.Insert(0, new(root));
                if (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
            }
        }
        ct.ThrowIfCancellationRequested();
        double duration = frames / (double)sampleRate;
        return new AudioTrack
        {
            FilePath = "Silence.wav", GeneratedSilenceSeconds = duration,
            SampleRate = sampleRate, Channels = channels, Original = root.Samples,
            OriginalWaveform = root.Waveform, OriginalPeaks = root.Peaks,
            SourceDurationSeconds = duration, SavedDuration = duration, Analysis = root.Analysis,
            WorkState = TrackWorkState.Queued, Status = "Queued for preview"
        };
    }

    /// <summary>The exact known analysis of a generated zero-valued region, without scanning its PCM.</summary>
    public static AnalysisResult CreateAnalysis(int frames, int sampleRate)
    {
        if (frames <= 0 || sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(frames));
        int hop = Math.Max(1, (int)Math.Round(sampleRate * .1));
        int bins = (frames + hop - 1) / hop;
        var counts = new int[bins];
        for (int bin = 0; bin < bins; bin++) counts[bin] = Math.Min(hop, frames - bin * hop);
        double duration = frames / (double)sampleRate;
        return new AnalysisResult
        {
            IntegratedLufs = -70, TruePeakDb = -120, LoudnessRange = 0,
            ShortTerm = [new(0, -90), new(duration, -90)],
            SuggestedEnvelope = [new(0, 0), new(duration, 0)],
            LevelingEnergy = new LevelingEnergyProfile(sampleRate, hop, frames, new double[bins], new double[bins], counts)
        };
    }
}

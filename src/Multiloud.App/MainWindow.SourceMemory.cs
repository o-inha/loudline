using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private long EstimateWorkingAudioBytes() => EstimateWorkingAudioBytes(_tracks,
        _backgroundLayer.Clips.Select(clip => clip.Source),
        _settings.LevelingEnabled || _settings.MatchingEnabled || _settings.LimiterEnabled);

    private static long EstimateWorkingAudioBytes(IEnumerable<AudioTrack> music,
        IEnumerable<AudioTrack> background, bool processing)
    {
        var roots = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        var summaries = new HashSet<WaveformPyramid>(ReferenceEqualityComparer.Instance);
        var peaks = new HashSet<List<PeakBin>>(ReferenceEqualityComparer.Instance);
        var sources = new HashSet<AudioTrack>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        void AddSource(AudioTrack source)
        {
            if (!sources.Add(source)) return;
            if (source.MergedSources != null)
                foreach (var part in source.MergedSources) AddSource(part.Source);
            if (roots.Add(source.Original)) bytes += 24L + source.Original.LongLength * sizeof(float);
            bytes += source.RetainedRegionBytes;
            if (source.OriginalWaveform is { } waveform)
            {
                var root = waveform.SharedSummaryRoot;
                if (summaries.Add(root)) bytes += root.RetainedBytes;
                if (!ReferenceEquals(root, waveform)) bytes += 128;
            }
            if (peaks.Add(source.OriginalPeaks))
                bytes += 64L + (long)source.OriginalPeaks.Capacity * IntPtr.Size + (long)source.OriginalPeaks.Count * 24;
        }
        foreach (var track in music)
        {
            AddSource(track);
            // Reserve processor buffers for each actual clip, rather than each
            // reference to an hour-long shared root. Live bypass needs no such
            // reservation, including when static gains/background are enabled.
            if (processing) bytes += (long)Math.Max(0, track.OriginalSampleCount) * (9 + 8 / Math.Max(1, track.Channels));
        }
        foreach (var source in background) AddSource(source);
        return bytes;
    }
}

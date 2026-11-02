using System.IO;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private readonly Dictionary<Guid, AudioTrack> _mergedProjectRoots = new();

    private AudioTrack DecodeMergedProjectSource(ProjectTrack saved, CancellationToken ct)
    {
        if (saved.MergedSourceId is not { } id || saved.MergedSources is not { Count: > 0 } parts)
            throw new InvalidDataException("The merged source has no reconstruction recipe.");
        if (_cacheProjectSourceDecodes && _mergedProjectRoots.TryGetValue(id, out var cached)) return MergedRootView(cached);
        var decoded = new List<MergedSourcePart>();
        var roots = new Dictionary<string, AudioTrack>(StringComparer.OrdinalIgnoreCase);
        string projectPath = _projectPath ?? Path.Combine(Environment.CurrentDirectory, "Release.loudline");
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            var source = part.Source;
            AudioTrack root;
            if (source.GeneratedSilenceSeconds is { } silence)
                root = GeneratedSilenceSource.Create(silence, source.SampleRate, source.Channels, ct);
            else
            {
                var link = ProjectFileService.Resolve(source, projectPath);
                string path = link.ResolvedPath ?? throw new FileNotFoundException("A merged source component is missing.", link.ExpectedPath);
                if (!roots.TryGetValue(path, out var original)) roots[path] = original = DecodeProjectSource(path, ct);
                root = new AudioTrack { FilePath = path, Original = original.Original, SampleRate = original.SampleRate,
                    Channels = original.Channels, OriginalWaveform = original.OriginalWaveform, OriginalPeaks = original.OriginalPeaks };
            }
            if (source.SourceDurationSeconds is { } requested &&
                Math.Round((source.SourceStartSeconds + requested) * root.SampleRate) > root.OriginalFrameCount)
                throw new InvalidOperationException("A merged component does not cover its saved source range.");
            SelectDecodedProjectRange(root, source.SourceStartSeconds, source.SourceDurationSeconds, ct);
            root.SourceStartSeconds = source.SourceStartSeconds; root.SourceDurationSeconds = root.Duration;
            root.SavedDuration = root.Duration;
            decoded.Add(new(root, part.GainDb));
        }
        var merged = new AudioTrack
        {
            FilePath = Path.GetFileName(saved.Path), MergedSourceId = id, MergedSources = decoded,
            SampleRate = saved.SampleRate, Channels = saved.Channels,
            SavedDuration = decoded.Sum(part => part.Source.Duration)
        };
        long samples = checked((long)Math.Round(merged.SavedDuration * merged.SampleRate) * merged.Channels);
        if (samples > 536000000 || EstimateWorkingAudioBytes(
            _tracks.Concat(_decodedProjectRoots.Values).Concat(_mergedProjectRoots.Values),
            _backgroundLayer.Clips.Select(clip => clip.Source).Concat(decoded.Select(part => part.Source)), false) + samples * 5 >
            12L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Reconstructing this merged source would exceed the supported working audio size.");
        AudioTrackMerger.Materialize(merged, ct);
        if (_cacheProjectSourceDecodes) _mergedProjectRoots[id] = merged;
        return MergedRootView(merged);
    }

    private static AudioTrack MergedRootView(AudioTrack root) => new()
    {
        FilePath = root.FilePath, MergedSourceId = root.MergedSourceId, MergedSources = root.MergedSources,
        SampleRate = root.SampleRate, Channels = root.Channels, Original = root.Original,
        OriginalWaveform = root.OriginalWaveform, OriginalPeaks = root.OriginalPeaks, SavedDuration = root.Duration
    };

    private async Task RelinkMergedComponentsAsync(AudioTrack track)
    {
        if (_importing || _exporting || track.MergedSources == null) return;
        string projectPath = _projectPath ?? Path.Combine(Environment.CurrentDirectory, "Release.loudline");
        var saved = SavedTrack(track, projectPath);
        var link = ProjectFileService.Resolve(saved, projectPath);
        var leaves = ProjectFileService.LeafSources(link).ToArray();
        new Controls.MissingFilesDialog(leaves) { Owner = this }.ShowDialog();
        if (link.IsResolved) await ReplaceSourceFamilyAsync(track, track.FilePath, mergedRecipe: saved);
    }

    private IEnumerable<(AudioTrack Old, AudioTrack Replacement)> StageDependentMergedReplacements(
        string originalPath, string replacementPath, AudioTrack decoded, CancellationToken ct)
    {
        originalPath = Path.GetFullPath(originalPath);
        var rebuilt = new Dictionary<Guid, AudioTrack>();
        foreach (var parent in _tracks.Where(track => track.MergedSourceId.HasValue && track.MergedSources != null))
        {
            var parts = parent.MergedSources!;
            if (!parts.Any(part => !part.Source.GeneratedSilenceSeconds.HasValue &&
                string.Equals(Path.GetFullPath(part.Source.FilePath), originalPath, StringComparison.OrdinalIgnoreCase))) continue;
            var id = parent.MergedSourceId!.Value;
            if (!rebuilt.TryGetValue(id, out var root))
            {
                var nextParts = parts.Select(part => !part.Source.GeneratedSilenceSeconds.HasValue &&
                    string.Equals(Path.GetFullPath(part.Source.FilePath), originalPath, StringComparison.OrdinalIgnoreCase)
                    ? part with { Source = ReplacementRange(part.Source, decoded, replacementPath, ct) } : part).ToArray();
                root = new AudioTrack { FilePath = parent.FilePath, MergedSourceId = id, MergedSources = nextParts,
                    SampleRate = parent.SampleRate, Channels = parent.Channels,
                    SavedDuration = nextParts.Sum(part => part.Source.Duration) };
                AudioTrackMerger.Materialize(root, ct); rebuilt[id] = root;
            }
            yield return (parent, ReplacementRange(parent, root, parent.FilePath, ct));
        }
    }
}

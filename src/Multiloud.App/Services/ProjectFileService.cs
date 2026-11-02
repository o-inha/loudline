using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using Multiloud.Core;

namespace Multiloud.App.Services;

public sealed class ProjectData
{
    public int Version { get; set; } = 9;
    public ProcessingSettings Settings { get; set; } = new();
    public bool FloatExport { get; set; }
    public List<ProjectTrack> Tracks { get; set; } = [];
    public ProjectBackgroundLayer Background { get; set; } = new();
    public List<EnvelopePoint>? ProjectEnvelope { get; set; }
    public List<EnvelopePoint>? ProjectEnvelopeSuggestion { get; set; }
    public List<double> CustomTrackBoundaries { get; set; } = [];
    public ProjectExportOptions ExportOptions { get; set; } = new();
    public PlaybackLoopState Loops { get; set; } = PlaybackLoopState.Empty;
}

/// <summary>Export-only preferences. These never participate in audio processing or preview keys.</summary>
public sealed record ProjectExportOptions
{
    public bool AsOneFile { get; set; }
    // Preserve the preference even when no marker is currently inside the album.
    // The effective UI/output choice falls back to original boundaries then.
    public bool UseCustomBoundaries { get; set; } = true;
    public double BoundaryFadeMs { get; set; }
}

public sealed class ProjectBackgroundLayer
{
    public bool Enabled { get; set; } = true;
    public double GainDb { get; set; }
    public List<ProjectBackgroundClip> Clips { get; set; } = [];
}

public sealed class ProjectBackgroundClip
{
    public ProjectTrack Source { get; set; } = new();
    public double StartSeconds { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
}

public sealed class ProjectTrack
{
    public Guid? MergedSourceId { get; set; }
    public List<ProjectMergedPart>? MergedSources { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ResolvedSourcePath { get; set; }
    public double? GeneratedSilenceSeconds { get; set; }
    public string Path { get; set; } = "";
    public string? AbsolutePath { get; set; }
    public double Duration { get; set; }
    public double SourceStartSeconds { get; set; }
    public double? SourceDurationSeconds { get; set; }
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public double TrimDb { get; set; }
    public double InputGainDb { get; set; }
    public double? TargetLufs { get; set; }
    public List<EnvelopePoint>? TransitionEnvelope { get; set; }
    public List<EnvelopePoint> Envelope { get; set; } = [];
}

public sealed class ProjectMergedPart
{
    public ProjectTrack Source { get; set; } = new();
    public double GainDb { get; set; }
}

/// <summary>Source resolution is separate from the paths authored in the project.</summary>
public sealed class ProjectSourceLink : INotifyPropertyChanged
{
    private string? resolvedPath;

    internal ProjectSourceLink(ProjectTrack saved, string expectedPath, string? resolvedPath,
        IReadOnlyList<ProjectSourceLink>? children = null)
    {
        Saved = saved;
        ExpectedPath = expectedPath;
        this.resolvedPath = resolvedPath;
        Children = children ?? [];
        if (Children.Count == 0) saved.ResolvedSourcePath = resolvedPath;
        foreach (var child in Children) child.PropertyChanged += (_, _) =>
        {
            Changed(nameof(ResolvedPath)); Changed(nameof(IsResolved)); Changed(nameof(IsMissing)); Changed(nameof(StatusText));
        };
    }

    public ProjectTrack Saved { get; }
    public string ExpectedPath { get; }
    public string FileName => System.IO.Path.GetFileName(ExpectedPath);
    public IReadOnlyList<ProjectSourceLink> Children { get; }
    public string? ResolvedPath => Children.Count > 0 && Children.Any(child => !child.IsResolved) ? null : resolvedPath;
    public bool IsResolved => ResolvedPath is not null;
    public bool IsMissing => !IsResolved;
    public string StatusText => IsResolved ? "Ready" : "Missing";
    public string DisplayPath => resolvedPath ?? ExpectedPath;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>An explicit file choice may resolve even an ambiguous folder match.</summary>
    public void Relink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonical = System.IO.Path.GetFullPath(path);
        if (!File.Exists(canonical)) throw new FileNotFoundException("The selected audio file could not be found.", canonical);
        if (string.Equals(resolvedPath, canonical, StringComparison.Ordinal)) return;
        resolvedPath = canonical;
        Saved.ResolvedSourcePath = canonical;
        Changed(nameof(ResolvedPath));
        Changed(nameof(IsResolved));
        Changed(nameof(IsMissing));
        Changed(nameof(StatusText));
        Changed(nameof(DisplayPath));
    }

    private void Changed([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <param name="Unmatched">Rows without an eligible match, excluding ambiguous rows.</param>
public sealed record ProjectFolderSearchResult(int Matched, int Ambiguous, int Unmatched);

public static class ProjectFileService
{
    /// <summary>Reads a project into the current format without rewriting authored envelopes.</summary>
    public static ProjectData DeserializeProject(string json)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.Deserialize<ProjectData>() ?? throw new InvalidDataException("The project is empty.");
        if (data.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9) || data.Settings == null || data.Tracks == null)
            throw new InvalidDataException("Unsupported project format.");
        // Earlier files never authored this detector preference. Restore their
        // full-spectrum behavior rather than inheriting the new-project default.
        bool storedBass = document.RootElement.TryGetProperty(nameof(ProjectData.Settings), out var settings) &&
            settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(nameof(ProcessingSettings.BassSensitivity), out _);
        if (data.Version < 5 && !storedBass) data.Settings = data.Settings with { BassSensitivity = 1 };
        // Older projects retain their authored single-band sound until the user
        // explicitly enables the newer selective bass control.
        bool storedProtection = settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty(nameof(ProcessingSettings.BassProtection), out _);
        if (!storedProtection) data.Settings = data.Settings with { BassProtection = 0 };
        if (data.Version < 6) data.Background = new();
        if (data.Version < 7) { data.ProjectEnvelope = null; data.ProjectEnvelopeSuggestion = null; }
        if (data.Version < 8) { data.CustomTrackBoundaries = []; data.ExportOptions = new(); }
        if (data.Version < 9) data.Loops = PlaybackLoopState.Empty;
        ValidateLoops(data.Loops);
        data.CustomTrackBoundaries = NormalizeCustomTrackBoundaries(data.CustomTrackBoundaries);
        ValidateExportOptions(data.ExportOptions);
        ValidateBackground(data.Background);
        foreach (var track in data.Tracks) ValidateSourceRange(track);
        ValidateProjectEnvelope(data.ProjectEnvelope);
        ValidateProjectEnvelope(data.ProjectEnvelopeSuggestion);
        data.Version = 9;
        return data;
    }

    public static void ValidateLoops(PlaybackLoopState loops)
    {
        if (loops == null || new[] { loops.A, loops.B }.Any(range => range != null &&
            (!range.IsValid || range.EndSeconds > int.MaxValue)))
            throw new InvalidDataException("Invalid audition loop ranges.");
    }

    /// <summary>Retain absolute marker times, including positions beyond a temporarily shortened project.</summary>
    public static List<double> NormalizeCustomTrackBoundaries(IReadOnlyList<double> markers)
    {
        if (markers == null || markers.Count > 100000 || markers.Any(time =>
            !double.IsFinite(time) || time <= 0 || time > int.MaxValue))
            throw new InvalidDataException("Invalid custom track boundaries.");
        var ordered = markers.OrderBy(time => time);
        var result = new List<double>(markers.Count);
        foreach (var time in ordered)
            if (result.Count == 0 || time - result[^1] > 1e-9) result.Add(time);
        return result;
    }

    public static void ValidateExportOptions(ProjectExportOptions options)
    {
        if (options == null || !double.IsFinite(options.BoundaryFadeMs) || options.BoundaryFadeMs is < 0 or > 1000)
            throw new InvalidDataException("Invalid export settings.");
    }

    public static void ValidateSourceRange(ProjectTrack track)
    {
        if (track == null || !double.IsFinite(track.SourceStartSeconds) || track.SourceStartSeconds is < 0 or > 86400 ||
            track.SourceDurationSeconds is { } duration && (!double.IsFinite(duration) || duration is <= 0 or > 86400) ||
            track.SourceDurationSeconds is { } length && track.SourceStartSeconds + length > 86400)
            throw new InvalidDataException("Invalid audio slice source range.");
        if (track.MergedSources is { } parts)
        {
            if (track.MergedSourceId == null || track.SampleRate is < 8000 or > 384000 || track.Channels is < 1 or > 2 ||
                parts.Count is < 2 or > 10000 ||
                parts.Any(part => part == null || part.Source == null || part.Source.MergedSources != null ||
                    !double.IsFinite(part.GainDb) || part.GainDb is < -132 or > 132 ||
                    !double.IsFinite(part.Source.SourceDurationSeconds ?? part.Source.Duration) ||
                    (part.Source.SourceDurationSeconds ?? part.Source.Duration) <= 0 ||
                    part.Source.SampleRate is < 8000 or > 384000 || part.Source.Channels is < 1 or > 2 ||
                    !part.Source.GeneratedSilenceSeconds.HasValue && string.IsNullOrWhiteSpace(part.Source.Path) &&
                        string.IsNullOrWhiteSpace(part.Source.AbsolutePath)))
                throw new InvalidDataException("Invalid merged source recipe.");
            foreach (var part in parts) ValidateSourceRange(part.Source);
            double rootDuration = parts.Sum(part => part.Source.SourceDurationSeconds ?? part.Source.Duration);
            double rootFrames = Math.Round(rootDuration * track.SampleRate);
            // Mixed native rates meet on this common frame clock. A final
            // output frame may extend fractionally beyond the native sum.
            if (!double.IsFinite(rootDuration) || rootFrames <= 0 ||
                Math.Round(track.SourceStartSeconds * track.SampleRate) >= rootFrames ||
                track.SourceDurationSeconds is { } mergedLength &&
                    Math.Round((track.SourceStartSeconds + mergedLength) * track.SampleRate) > rootFrames)
                throw new InvalidDataException("The merged slice is outside its reconstructed source.");
        }
        if (track.GeneratedSilenceSeconds is { } silence && (!double.IsFinite(silence) ||
            silence is < GeneratedSilenceSource.MinimumSeconds or > GeneratedSilenceSource.MaximumSeconds ||
            track.SourceStartSeconds >= silence || track.SourceDurationSeconds is { } silentLength &&
                track.SourceStartSeconds + silentLength > silence + 1e-7))
            throw new InvalidDataException("Invalid generated silence duration or region.");
    }

    private static void ValidateProjectEnvelope(List<EnvelopePoint>? envelope)
    {
        if (envelope != null && (envelope.Count > 1000000 || envelope.Any(point => point == null ||
            !double.IsFinite(point.Time) || point.Time < 0 || !double.IsFinite(point.GainDb) || point.GainDb is < -24 or > 24)))
            throw new InvalidDataException("Invalid project volume envelope.");
    }

    public static void ValidateBackground(ProjectBackgroundLayer background)
    {
        if (background == null || background.Clips == null || !double.IsFinite(background.GainDb) || background.GainDb is < -60 or > 12)
            throw new InvalidDataException("Invalid background layer data.");
        foreach (var clip in background.Clips)
        {
            if (clip == null || clip.Source == null || !double.IsFinite(clip.Source.Duration) || clip.Source.Duration is < 0 or > 86400 ||
                !double.IsFinite(clip.StartSeconds) || clip.StartSeconds is < -86400 or > 86400 ||
                !double.IsFinite(clip.FadeInSeconds) || clip.FadeInSeconds < 0 || clip.FadeInSeconds > clip.Source.Duration ||
                !double.IsFinite(clip.FadeOutSeconds) || clip.FadeOutSeconds < 0 || clip.FadeOutSeconds > clip.Source.Duration)
                throw new InvalidDataException("Invalid background clip placement or fades.");
            ValidateSourceRange(clip.Source);
        }
    }

    /// <summary>
    /// Prefer the saved project-relative path so a moved release remains portable.
    /// Rooted legacy paths are accepted; the absolute hint is only a fallback.
    /// Neither successful resolution nor a missing source changes saved paths.
    /// </summary>
    public static ProjectSourceLink Resolve(ProjectTrack saved, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        if (saved.MergedSources is { Count: > 0 } parts)
        {
            ValidateSourceRange(saved);
            string virtualPath = Path.Combine(directory, System.IO.Path.GetFileName(!string.IsNullOrWhiteSpace(saved.Path) ? saved.Path : "Merged.wav"));
            return new(saved, virtualPath, virtualPath, parts.Select(part => Resolve(part.Source, projectPath)).ToArray());
        }
        if (saved.GeneratedSilenceSeconds.HasValue)
        {
            ValidateSourceRange(saved);
            var virtualPath = Path.Combine(directory, "Silence.wav");
            return new ProjectSourceLink(saved, virtualPath, virtualPath);
        }
        string? expected = !string.IsNullOrWhiteSpace(saved.Path) ? Path.GetFullPath(saved.Path, directory) : null;
        if (saved.ResolvedSourcePath is { } chosen && File.Exists(chosen)) return new(saved, expected ?? chosen, chosen);
        if (expected is not null && File.Exists(expected)) return new(saved, expected, expected);
        string? fallback = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(saved.AbsolutePath) && Path.IsPathRooted(saved.AbsolutePath))
                fallback = Path.GetFullPath(saved.AbsolutePath, directory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An optional stale absolute hint must not hide the primary path.
        }
        expected ??= fallback;
        if (expected is null || string.IsNullOrEmpty(Path.GetFileName(expected)))
            throw new InvalidDataException("A project track must identify an audio source file.");
        string? resolved = File.Exists(expected) ? expected : fallback is not null && File.Exists(fallback) ? fallback : null;
        return new ProjectSourceLink(saved, expected, resolved);
    }

    public static IEnumerable<ProjectSourceLink> LeafSources(ProjectSourceLink source)
        => source.Children.Count == 0 ? [source] : source.Children.SelectMany(LeafSources);

    /// <summary>
    /// Searches only unresolved rows off the UI thread, then publishes matches on
    /// the caller's captured context. A root-level exact file wins; elsewhere a
    /// basename must identify one file. Reparse points and inaccessible branches
    /// are excluded, and cancellation before publication leaves all rows intact.
    /// </summary>
    public static async Task<ProjectFolderSearchResult> SearchFolder(IEnumerable<ProjectSourceLink> links,
        string folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ct.ThrowIfCancellationRequested();
        var pending = links.Where(link => !link.IsResolved).Distinct().ToArray();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected source folder could not be found.");
        var names = pending.Select(link => link.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = await Task.Run(() => FindCandidates(root, names, ct), ct);
        ct.ThrowIfCancellationRequested();
        int matched = 0, ambiguous = 0;
        foreach (var link in pending)
        {
            // A manual relink while the worker searched takes precedence.
            if (link.IsResolved || !candidates.TryGetValue(link.FileName, out var matches)) continue;
            string exactRoot = Path.Combine(root, link.FileName);
            string? chosen = matches.FirstOrDefault(path => string.Equals(path, exactRoot, StringComparison.Ordinal));
            if (chosen is null)
            {
                var atRoot = matches.Where(path => string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (atRoot.Length == 1) chosen = atRoot[0];
                else if (atRoot.Length == 0 && matches.Count == 1) chosen = matches[0];
            }
            if (chosen is null) { ambiguous++; continue; }
            try { link.Relink(chosen); matched++; }
            catch (FileNotFoundException) { /* A file can disappear after enumeration. */ }
        }
        return new(matched, ambiguous, pending.Count(link => !link.IsResolved) - ambiguous);
    }

    private static Dictionary<string, List<string>> FindCandidates(string root, HashSet<string> names, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo child) { directories.Push(child); continue; }
                    if (!names.Contains(entry.Name)) continue;
                    if (!result.TryGetValue(entry.Name, out var matches)) result.Add(entry.Name, matches = []);
                    matches.Add(entry.FullName);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Continue with sibling folders if access or contents change.
            }
        }
        return result;
    }
}

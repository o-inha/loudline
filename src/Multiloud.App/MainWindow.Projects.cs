using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _openingProject;
    private bool _backgroundProjectNeedsCopy;
    private bool _cacheProjectSourceDecodes;
    private (string Path, float[] Samples, int SampleRate, int Channels, WaveformPyramid Waveform, List<PeakBin> Peaks)? _cachedProjectSource;
    private readonly Dictionary<string, AudioTrack> _decodedProjectRoots = new(StringComparer.OrdinalIgnoreCase);

    private void BeginProjectSourceDecodeCache() { _cacheProjectSourceDecodes = true; _cachedProjectSource = null; _decodedProjectRoots.Clear(); _mergedProjectRoots.Clear(); }
    private void EndProjectSourceDecodeCache() { _cacheProjectSourceDecodes = false; _cachedProjectSource = null; _decodedProjectRoots.Clear(); _mergedProjectRoots.Clear(); }

    private AudioTrack DecodeProjectSource(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The operation cache also covers nonadjacent recipe parts. Every leaf
        // is decoded once, even when several merged sources reference it.
        if (_cacheProjectSourceDecodes && _decodedProjectRoots.TryGetValue(Path.GetFullPath(path), out var cached))
            return DecodedProjectRootView(cached, path);
        var loaded = AudioFileService.Load(path, ct);
        loaded.OriginalWaveform = WaveformPyramid.Build(loaded.Original, loaded.Channels, ct);
        loaded.OriginalPeaks = BuildDisplayPeaks(loaded.OriginalWaveform, ct);
        if (_cacheProjectSourceDecodes) _cachedProjectSource = (path, loaded.Original, loaded.SampleRate,
            loaded.Channels, loaded.OriginalWaveform, loaded.OriginalPeaks);
        if (_cacheProjectSourceDecodes)
        {
            if (_decodedProjectRoots.Values.Sum(root => root.Original.LongLength * sizeof(float) + (root.OriginalWaveform?.RetainedBytes ?? 0)) +
                loaded.Original.LongLength * sizeof(float) + loaded.OriginalWaveform.RetainedBytes > 12L * 1024 * 1024 * 1024)
                throw new InvalidOperationException("The decoded project sources exceed the 12 GB working audio budget.");
            _decodedProjectRoots[Path.GetFullPath(path)] = loaded;
            // The caller immediately selects its saved range, including on the
            // first decode. Keep the cached root descriptor private so that
            // selecting that first range cannot crop subsequent siblings.
            return DecodedProjectRootView(loaded, path);
        }
        return loaded;
    }

    private static AudioTrack DecodedProjectRootView(AudioTrack root, string path) => new()
    {
        FilePath = path, Original = root.Original, SampleRate = root.SampleRate,
        Channels = root.Channels, OriginalWaveform = root.OriginalWaveform, OriginalPeaks = root.OriginalPeaks
    };

    private static void SelectDecodedProjectRange(AudioTrack loaded, double startSeconds, double? durationSeconds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            durationSeconds is { } duration && (!double.IsFinite(duration) || duration <= 0))
            throw new ArgumentException("Invalid audio source range.");
        int frames = loaded.OriginalFrameCount;
        double start = Math.Round(startSeconds * loaded.SampleRate);
        double end = durationSeconds is { } length ? Math.Round((startSeconds + length) * loaded.SampleRate) : frames;
        if (start >= frames || end <= start)
            throw new InvalidOperationException("The saved slice is outside the replacement audio file.");
        int first = (int)Math.Min(start, frames), last = (int)Math.Min(end, frames);
        if (first == 0 && last == frames) return;
        loaded.OriginalFrameOffset += first; loaded.OriginalFrameLength = last - first;
        loaded.OriginalWaveform = loaded.OriginalWaveform!.Slice(first, last);
        loaded.OriginalPeaks = BuildDisplayPeaks(loaded.OriginalWaveform, ct);
    }

    private static ProjectTrack SavedTrack(AudioTrack track, string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        bool virtualSource = track.GeneratedSilenceSeconds.HasValue || track.MergedSources is { Count: > 0 };
        string? absolute = virtualSource ? null : Path.GetFullPath(track.FilePath);
        if (!track.GeneratedSilenceSeconds.HasValue && track.WorkState == TrackWorkState.Missing && track.MissingAbsolutePath is { } hint && Path.IsPathFullyQualified(hint))
            absolute = Path.GetFullPath(hint);
        return new ProjectTrack
        {
            Path = virtualSource ? track.Name : Path.GetRelativePath(directory, Path.GetFullPath(track.FilePath)),
            GeneratedSilenceSeconds = track.GeneratedSilenceSeconds,
            MergedSourceId = track.MergedSourceId,
            MergedSources = track.MergedSources?.Select(part => new ProjectMergedPart
            { Source = SavedTrack(part.Source, projectPath), GainDb = part.GainDb }).ToList(),
            AbsolutePath = absolute,
            Duration = track.Duration, SampleRate = track.SampleRate, Channels = track.Channels,
            SourceStartSeconds = track.SourceStartSeconds, SourceDurationSeconds = track.SourceDurationSeconds,
            TrimDb = track.TrimDb, InputGainDb = track.InputGainDb, TargetLufs = track.TargetLufs,
            Envelope = track.Envelope.ToList(), TransitionEnvelope = track.TransitionEnvelope.ToList()
        };
    }

    private ProjectData CreateProjectData(string path) => new()
    {
        Settings = _settings, FloatExport = _floatExport,
        Tracks = _tracks.Select(track => SavedTrack(track, path)).ToList(),
        Background = SavedBackground(path),
        ProjectEnvelope = _projectEnvelope.ToList(), ProjectEnvelopeSuggestion = _projectSuggestion.ToList(),
        CustomTrackBoundaries = CaptureCustomBoundaries(), ExportOptions = CaptureExportOptions(), Loops = CaptureLoops()
    };

    private static void ValidateProjectTrack(ProjectTrack track)
    {
        ProjectFileService.ValidateSourceRange(track);
        if (track is null || !track.GeneratedSilenceSeconds.HasValue && track.MergedSources == null && string.IsNullOrWhiteSpace(track.Path) && string.IsNullOrWhiteSpace(track.AbsolutePath) ||
            !double.IsFinite(track.TrimDb) || track.TrimDb is < -30 or > 30 ||
            !double.IsFinite(track.InputGainDb) || track.InputGainDb is < -30 or > 30 ||
            track.TargetLufs is { } target && (!double.IsFinite(target) || target is < -36 or > -6) ||
            !double.IsFinite(track.Duration) || track.Duration is < 0 or > 86400 || track.SampleRate is < 8000 or > 384000 ||
            track.Channels is < 1 or > 2 || track.Envelope == null ||
            track.Envelope.Any(point => point is null || !double.IsFinite(point.Time) || !double.IsFinite(point.GainDb)) ||
            track.TransitionEnvelope?.Any(point => point is null || !double.IsFinite(point.Time) || !double.IsFinite(point.GainDb)) == true)
            throw new InvalidDataException("Invalid track data.");
    }

    private static AudioTrack ProjectSlot(ProjectSourceLink source) => new()
    {
        FilePath = source.ResolvedPath ?? source.ExpectedPath,
        GeneratedSilenceSeconds = source.Saved.GeneratedSilenceSeconds,
        MergedSourceId = source.Saved.MergedSourceId,
        MergedSources = source.Saved.MergedSources?.Select((part, index) => new MergedSourcePart(ProjectSlot(source.Children[index]), part.GainDb)).ToArray(),
        MissingAbsolutePath = source.Saved.AbsolutePath,
        SavedDuration = source.Saved.Duration > 0 ? source.Saved.Duration : source.Saved.Envelope.Where(point => double.IsFinite(point.Time) && point.Time is >= 0 and <= 86400).Select(point => point.Time).DefaultIfEmpty(0).Max(),
        SampleRate = source.Saved.SampleRate, Channels = source.Saved.Channels,
        SourceStartSeconds = source.Saved.SourceStartSeconds, SourceDurationSeconds = source.Saved.SourceDurationSeconds,
        TrimDb = source.Saved.TrimDb, InputGainDb = source.Saved.InputGainDb, TargetLufs = source.Saved.TargetLufs,
        Envelope = source.Saved.Envelope.ToList(), TransitionEnvelope = source.Saved.TransitionEnvelope?.ToList() ?? [],
        WorkState = source.IsResolved ? TrackWorkState.Analyzing : TrackWorkState.Missing,
        Status = source.IsResolved ? "Loading audio…" : "File not found"
    };

    private void CancelSourceWork()
    {
        CloseSettingsFlyout();
        CancelInterruptedPlayback();
        _renderCts?.Cancel(); ++_revision; _renderedRevision = -1; _renderStartedRevision = -1;
        _player.Stop(); _resumeAfterDeviceLoss = false; PlayButton.Content = "▶";
    }

    private async Task LoadProjectSourceAsync(AudioTrack track, ProjectTrack saved, string path, CancellationToken ct, ProcessingSettings? authoredSettings = null)
    {
        var baseline = authoredSettings ?? _settings;
        if (EstimateWorkingAudioBytes() > 12L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("This release exceeds the 12 GB working audio budget.");
        track.WorkState = TrackWorkState.Analyzing; track.Status = "Loading audio…"; RefreshList();
        SetBusy($"Loading {Path.GetFileName(path)}…", true);
        var decoded = await Task.Run(() =>
        {
            var loaded = saved.MergedSources is { Count: > 0 } ? DecodeMergedProjectSource(saved, ct) : track.GeneratedSilenceSeconds is { } silence
                ? GeneratedSilenceSource.Create(silence, track.SampleRate, track.Channels, ct) : DecodeProjectSource(path, ct);
            SelectDecodedProjectRange(loaded, track.SourceStartSeconds, track.SourceDurationSeconds, ct);
            return loaded;
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (_closed || !_tracks.Contains(track)) throw new OperationCanceledException(ct);
        track.Original = decoded.Original; track.OriginalWaveform = decoded.OriginalWaveform; track.OriginalPeaks = decoded.OriginalPeaks;
        track.OriginalFrameOffset = decoded.OriginalFrameOffset; track.OriginalFrameLength = decoded.OriginalFrameLength;
        track.SampleRate = decoded.SampleRate; track.Channels = decoded.Channels; track.SavedDuration = track.Duration;
        track.MergedSourceId = decoded.MergedSourceId; track.MergedSources = decoded.MergedSources;
        if (track.GeneratedSilenceSeconds.HasValue)
            track.Analysis = GeneratedSilenceSource.CreateAnalysis(track.OriginalFrameCount, track.SampleRate);
        if (TryPublishIdentityPreview(track, _settings))
        {
            track.MissingAbsolutePath = null;
            RefreshList(); QueueRender(addition: true);
            return;
        }
        if (track.GeneratedSilenceSeconds.HasValue)
        {
            track.WorkState = TrackWorkState.Queued; track.Status = "Queued for preview";
            RefreshList(); QueueRender(addition: true); return;
        }
        track.Status = "Analyzing loudness…"; RefreshList();
        var samples = track.Original; int sampleRate = track.SampleRate, channels = track.Channels;
        var analysis = await Task.Run(() => LoudnessAnalyzer.Analyze(track.GetOriginalRegion(ct), sampleRate, channels, ct), ct);
        ct.ThrowIfCancellationRequested();
        if (_closed || !_tracks.Contains(track) || !ReferenceEquals(track.Original, samples)) throw new OperationCanceledException(ct);
        track.Analysis = analysis;
        track.Envelope = RetargetLevelingEnvelope(ValidateEnvelope(track.Envelope, track.Duration), analysis, track.Duration, baseline, _settings);
        track.TransitionEnvelope = saved.TransitionEnvelope is { Count: > 0 }
            ? ValidateEnvelope(saved.TransitionEnvelope, track.Duration).Select(point => point with { GainDb = Math.Clamp(point.GainDb, -2, 2) }).ToList() : [];
        track.MissingAbsolutePath = null; track.WorkState = TrackWorkState.Queued; track.Status = "Queued for preview";
        if (!SameLevelingDesign(baseline, _settings)) UpdateTransitions();
        RefreshList(); QueueRender(addition: true);
    }

    private static void MarkUnavailable(AudioTrack track, string? reason = null)
    {
        track.SavedDuration = track.Duration;
        track.Original = []; track.OriginalFrameOffset = 0; track.OriginalFrameLength = null;
        track.OriginalWaveform = null; track.OriginalPeaks = [];
        track.Analysis = null; track.Render = null; track.PreviewPeaks = [];
        track.WorkState = TrackWorkState.Missing; track.Status = reason ?? "File not found";
    }

    private async Task OpenProjectAsync(string path, bool showMissingDialog = true)
    {
        if (_openingProject || _importing || _exporting) return;
        _openingProject = _importing = true;
        BeginProjectSourceDecodeCache();
        var cts = new CancellationTokenSource(); _importCts = cts; var ct = cts.Token;
        bool replaced = false;
        try
        {
            path = Path.GetFullPath(path);
            var projectJson = await File.ReadAllTextAsync(path, ct);
            using var projectHeader = System.Text.Json.JsonDocument.Parse(projectJson);
            bool legacyProject = projectHeader.RootElement.TryGetProperty("Version", out var format) && format.TryGetInt32(out int version) && version < 8;
            var data = ProjectFileService.DeserializeProject(projectJson);
            ValidateSettings(data.Settings);
            foreach (var saved in data.Tracks) ValidateProjectTrack(saved);
            foreach (var saved in data.Background.Clips) ValidateProjectTrack(saved.Source);
            var sources = data.Tracks.Select(saved => ProjectFileService.Resolve(saved, path)).ToList();
            var backgroundSources = data.Background.Clips.Select(saved => (Saved: saved, Link: ProjectFileService.Resolve(saved.Source, path))).ToArray();
            var missing = sources.Concat(backgroundSources.Select(source => source.Link)).SelectMany(ProjectFileService.LeafSources).Where(source => !source.IsResolved).ToList();
            if (showMissingDialog && missing.Count > 0)
                new MissingFilesDialog(missing) { Owner = this }.ShowDialog();
            ct.ThrowIfCancellationRequested();
            var slots = sources.Select(ProjectSlot).ToArray();
            var backgroundSlots = backgroundSources.Select(source => new BackgroundClip
            {
                Source = ProjectSlot(source.Link), StartSeconds = source.Saved.StartSeconds,
                FadeInSeconds = source.Saved.FadeInSeconds, FadeOutSeconds = source.Saved.FadeOutSeconds
            }).ToArray();
            CancelSourceWork(); _tracks.Clear(); _previewCache.Clear(); _renderKeys.Clear(); _undo.Clear(); _redo.Clear();
            _settings = data.Settings; _floatExport = data.FloatExport; ResetTargetReadout(); SyncControls();
            _tracks.AddRange(slots);
            BeginSourceProcessing();
            LoadProjectEnvelope(data.ProjectEnvelope, data.ProjectEnvelopeSuggestion);
            LoadCustomBoundaries(data.CustomTrackBoundaries);
            LoadExportOptions(data.ExportOptions);
            LoadLoops(data.Loops);
            _backgroundLayer = new BackgroundLayer
            {
                Enabled = data.Background.Enabled, GainDb = data.Background.GainDb,
                Clips = backgroundSlots
            };
            RefreshBackground();
            _projectPath = path; _backgroundProjectNeedsCopy = legacyProject; _dirty = missing.Any(source => source.IsResolved);
            Timeline.Playhead = 0; Timeline.SelectedTrack = null; UpdateTime(); RefreshList(); Timeline.FitAll();
            Title = $"{Path.GetFileNameWithoutExtension(path)} — Loudline {AppVersion.Display}"; replaced = true;
            // Background sources are ready before music preview work can start.
            for (int index = 0; index < backgroundSources.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (backgroundSources[index].Link.ResolvedPath is not { } resolved) continue;
                var clip = backgroundSlots[index];
                if (!_backgroundLayer.Clips.Any(current => current.Id == clip.Id && ReferenceEquals(current.Source, clip.Source))) continue;
                var source = clip.Source;
                try { await LoadBackgroundSourceAsync(source, resolved, ct); }
                catch (OperationCanceledException) { MarkNotProcessed(source); throw; }
                catch (Exception ex) { MarkUnavailable(source, $"File unavailable: {ex.Message}"); }
                RefreshBackground();
            }
            for (int index = 0; index < sources.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (sources[index].ResolvedPath is not { } resolved) continue;
                try { await LoadProjectSourceAsync(_tracks[index], sources[index].Saved, resolved, ct, data.Settings); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { MarkUnavailable(_tracks[index], $"File unavailable: {ex.Message}"); }
            }
            if (data.Tracks.Any(saved => saved.TransitionEnvelope == null)) UpdateTransitions();
            // History was reset before loading; retain edits applied while the
            // asynchronous sources were decoding/analyzing.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _openingProject = _importing = false;
            EndProjectSourceDecodeCache();
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            cts.Dispose();
            if (replaced && !_closed)
            {
                foreach (var track in _tracks.Where(track => track.WorkState == TrackWorkState.Analyzing)) MarkNotProcessed(track);
                foreach (var clip in _backgroundLayer.Clips.Where(clip => clip.Source.WorkState == TrackWorkState.Analyzing)) MarkNotProcessed(clip.Source);
                RefreshBackground();
                RefreshList();
                if (ct.IsCancellationRequested) FinishSourceCancellation(); else QueueRender(addition: true);
                if (_tracks.Count > 0) SelectTrack(_tracks[0]);
            }
            else if (!_closed) CompletePreview(_revision);
        }
    }

    private void RelinkTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackListEntry row }) RequestTrackRelink(row.Track);
        e.Handled = true;
    }

    private void RequestTrackRelink(AudioTrack track)
    {
        if (_importing || _exporting || !_tracks.Contains(track) || track.GeneratedSilenceSeconds.HasValue) return;
        if (track.WorkState != TrackWorkState.Missing)
        {
            _ = ChooseTrackRelinkFileAsync(track);
            return;
        }
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (track.MergedSources is { Count: > 0 })
        {
            var components = new MenuItem { Header = "Relink component files…" };
            components.Click += async (_, _) =>
            { try { await RelinkMergedComponentsAsync(track); } catch (Exception ex) { ShowError("Could not relink audio", ex); } };
            menu.Items.Add(components);
        }
        var file = new MenuItem { Header = "Choose file…" };
        file.Click += async (_, _) => await ChooseTrackRelinkFileAsync(track);
        var folder = new MenuItem { Header = "Locate folder…" };
        folder.Click += async (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Locate missing audio" };
            if (picker.ShowDialog(this) != true) return;
            var projectPath = _projectPath ?? Path.Combine(Environment.CurrentDirectory, "Release.loudline");
            var pairs = _tracks.Where(item => item.WorkState == TrackWorkState.Missing).Select(item =>
                (Track: item, Source: ProjectFileService.Resolve(SavedTrack(item, projectPath), projectPath))).ToArray();
            // A missing file may have returned since project opening; it is safe to load it too.
            var dialog = new MissingFilesDialog(pairs.SelectMany(pair => ProjectFileService.LeafSources(pair.Source)).ToArray()) { Owner = this };
            dialog.Loaded += async (_, _) => await dialog.LocateFolderAsync(picker.FolderName);
            dialog.ShowDialog();
            try { await RelinkLocatedSourcesAsync(pairs.Where(pair => pair.Source.IsResolved).ToArray()); }
            catch (Exception ex) { ShowError("Could not relink audio", ex); }
        };
        menu.Items.Add(file);
        if (track.WorkState == TrackWorkState.Missing) menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private async Task ChooseTrackRelinkFileAsync(AudioTrack track)
    {
        var picker = new OpenFileDialog { Filter = AudioFileService.FileFilter, Title = $"Relink source audio: {track.Name}" };
        if (picker.ShowDialog(this) == true)
            try { await RelinkTrackAsync(track, picker.FileName); }
            catch (Exception ex) { ShowError("Could not relink audio", ex); }
    }

    private async Task RelinkLocatedSourcesAsync(IReadOnlyList<(AudioTrack Track, ProjectSourceLink Source)> sources)
    {
        var handled = new HashSet<Guid>();
        foreach (var (track, source) in sources)
        {
            if (handled.Contains(track.Id) || !_tracks.Contains(track)) continue;
            foreach (var sibling in SourceFamily(track)) handled.Add(sibling.Id);
            if (track.MergedSources is { Count: > 0 })
                await ReplaceSourceFamilyAsync(track, track.FilePath, mergedRecipe: source.Saved);
            else await RelinkTrackAsync(track, source.ResolvedPath!);
        }
    }

    private async Task RelinkTrackAsync(AudioTrack track, string path)
    {
        var family = SourceFamily(track);
        if (family.Length > 1 || family.Any(item => item.Original.Length > 0 || item.SourceDurationSeconds.HasValue || item.SourceStartSeconds > 0))
            await ReplaceSourceFamilyAsync(track, path);
        else
            await RelinkSourcesAsync(family.Select(item => (item, path)).ToArray());
    }

    private async Task RelinkSourcesAsync(IReadOnlyList<(AudioTrack Track, string Path)> sources)
    {
        if (_importing || _exporting || sources.Count == 0) return;
        foreach (var source in sources)
            if (!File.Exists(source.Path)) throw new FileNotFoundException("The selected audio file could not be found.", source.Path);
        FinishStrengthGesture(false); PushUndo();
        var previousOrder = _tracks.ToArray();
        var position = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        var anchor = PlaybackTrackAt(CapturePlaybackTracks(), position);
        var local = anchor == null ? 0 : position - _tracks.TakeWhile(track => track.Id != anchor.Id).Sum(track => track.Duration);
        CancelSourceWork(); _importing = true; ExportButton.IsEnabled = false;
        BeginProjectSourceDecodeCache();
        var cts = new CancellationTokenSource(); _importCts = cts; var ct = cts.Token;
        var projectPath = _projectPath ?? Path.Combine(Environment.CurrentDirectory, "Release.loudline");
        var authoredSettings = _settings;
        try
        {
            foreach (var (old, path) in sources)
            {
                ct.ThrowIfCancellationRequested();
                int index = _tracks.IndexOf(old); if (index < 0) continue;
                RememberActivePreview(old);
                var state = SavedTrack(old, projectPath);
                // An analyzed future source may already have been retargeted by
                // Apply while an earlier replacement was loading.
                var stateSettings = old.Analysis != null ? _settings : authoredSettings;
                var replacement = ProjectSlot(ProjectFileService.Resolve(state, projectPath));
                replacement.Id = old.Id; replacement.FilePath = Path.GetFullPath(path); replacement.MissingAbsolutePath = replacement.FilePath;
                replacement.WorkState = TrackWorkState.Analyzing;
                _tracks[index] = replacement; _renderKeys.Remove(old.Id); RefreshList(); SelectTrack(replacement);
                try { await LoadProjectSourceAsync(replacement, state, replacement.FilePath, ct, stateSettings); }
                catch (OperationCanceledException) { MarkNotProcessed(replacement); throw; }
                catch (Exception ex) { MarkUnavailable(replacement, $"File unavailable: {ex.Message}"); }
                UpdateTransitions(previousOrder);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _importing = false; _dirty = true;
            EndProjectSourceDecodeCache();
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            cts.Dispose();
            if (!_closed)
            {
                if (anchor != null && _tracks.FirstOrDefault(track => track.Id == anchor.Id) is { } current)
                    Timeline.Playhead = _tracks.TakeWhile(track => track.Id != anchor.Id).Sum(track => track.Duration) + Math.Min(local, current.Duration);
                UpdateTime(); RefreshList();
                if (ct.IsCancellationRequested) FinishSourceCancellation(); else QueueRender();
            }
        }
    }
}

using System.IO;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private ProjectBackgroundLayer SavedBackground(string projectPath) => new()
    {
        Enabled = _backgroundLayer.Enabled, GainDb = _backgroundLayer.GainDb,
        Clips = _backgroundLayer.Clips.Select(clip => new ProjectBackgroundClip
        {
            Source = SavedTrack(clip.Source, projectPath), StartSeconds = clip.StartSeconds,
            FadeInSeconds = clip.FadeInSeconds, FadeOutSeconds = clip.FadeOutSeconds
        }).ToList()
    };

    private static BackgroundClip BoundBackgroundSource(BackgroundClip clip, AudioTrack source) => clip with
    {
        Source = source,
        FadeInSeconds = Math.Clamp(clip.FadeInSeconds, 0, source.Duration),
        FadeOutSeconds = Math.Clamp(clip.FadeOutSeconds, 0, source.Duration)
    };

    // Imports use the same decoder before adding their clip, so this deliberately
    // does not require membership in the current background layer.
    private async Task LoadBackgroundSourceAsync(AudioTrack source, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_closed) throw new OperationCanceledException(ct);
        source.FilePath = Path.GetFullPath(path);
        source.WorkState = TrackWorkState.Analyzing; source.Status = "Loading background audio…";
        RefreshBackground();
        var decoded = await Task.Run(() =>
        {
            var loaded = source.GeneratedSilenceSeconds is { } silence
                ? GeneratedSilenceSource.Create(silence, source.SampleRate, source.Channels, ct) : DecodeProjectSource(path, ct);
            SelectDecodedProjectRange(loaded, source.SourceStartSeconds, source.SourceDurationSeconds, ct);
            return loaded;
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (_closed) throw new OperationCanceledException(ct);
        source.Original = decoded.Original; source.SampleRate = decoded.SampleRate; source.Channels = decoded.Channels;
        source.OriginalFrameOffset = decoded.OriginalFrameOffset; source.OriginalFrameLength = decoded.OriginalFrameLength;
        source.OriginalWaveform = decoded.OriginalWaveform; source.OriginalPeaks = decoded.OriginalPeaks;
        source.SavedDuration = source.Duration; source.MissingAbsolutePath = null;
        source.Analysis = null; source.Render = null; source.PreviewPeaks = [];
        source.WorkState = TrackWorkState.Idle; source.Status = "Ready";
        // A replacement file can be shorter than its saved duration. Publish
        // valid fades together with the newly decoded source.
        if (_backgroundLayer.Clips.Any(clip => ReferenceEquals(clip.Source, source)))
            _backgroundLayer = _backgroundLayer with
            {
                Clips = _backgroundLayer.Clips.Select(clip => ReferenceEquals(clip.Source, source) ? BoundBackgroundSource(clip, source) : clip).ToArray()
            };
        RefreshBackground();
    }

    private async Task RelinkBackgroundAsync(BackgroundClip clip)
    {
        if (_closed || _importing || _exporting || !_backgroundLayer.Clips.Any(current => current.Id == clip.Id && ReferenceEquals(current.Source, clip.Source))) return;
        var projectPath = _projectPath ?? Path.Combine(Environment.CurrentDirectory, "Release.loudline");
        var source = ProjectFileService.Resolve(SavedTrack(clip.Source, projectPath), projectPath);
        new MissingFilesDialog([source]) { Owner = this }.ShowDialog();
        if (source.ResolvedPath is not { } path || clip.Source.WorkState != TrackWorkState.Missing &&
            string.Equals(path, clip.Source.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        try { await RelinkBackgroundSourceAsync(clip, path); }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Background relink cancelled."; }
        catch (Exception ex) { if (!_closed) ShowError("Could not relink background audio", ex); }
    }

    private async Task RelinkBackgroundSourceAsync(BackgroundClip clip, string path, CancellationToken ct = default)
    {
        if (_closed || _importing || _exporting || !_backgroundLayer.Clips.Any(current => current.Id == clip.Id && ReferenceEquals(current.Source, clip.Source))) return;
        if (!File.Exists(path)) throw new FileNotFoundException("The selected background file could not be found.", path);
        FinishStrengthGesture(false);
        var source = new AudioTrack { FilePath = Path.GetFullPath(path), WorkState = TrackWorkState.Analyzing };
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _importCts = cts; _importing = true; ExportButton.IsEnabled = false;
        try
        {
            SetBusy($"Loading background: {Path.GetFileName(path)}…", true);
            await LoadBackgroundSourceAsync(source, path, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            var latest = _backgroundLayer.Clips.FirstOrDefault(current => current.Id == clip.Id && ReferenceEquals(current.Source, clip.Source));
            if (latest == null) return;
            var replacement = BoundBackgroundSource(latest, source);
            // The old source stays untouched for Undo; the root helper captures
            // history before replacing this immutable layer.
            SetBackgroundLayer(_backgroundLayer with
            {
                Clips = _backgroundLayer.Clips.Select(current => current.Id == latest.Id ? replacement : current).ToArray()
            });
        }
        finally
        {
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            cts.Dispose(); _importing = false;
            if (!_closed) { RefreshBackground(); RefreshList(); QueueRender(addition: true); }
        }
    }
}

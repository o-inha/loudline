using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private ContextMenu? _trackContextMenu;

    private bool CanExportTrack(AudioTrack track) => !_closed && !_importing && !_exporting &&
        _tracks.Contains(track) && track.WorkState == TrackWorkState.Idle && SourceAnalysisSatisfied(track, _settings) &&
        track.Render is { Samples.Length: > 0 } && PreviewCurrent(track, _settings) && BackgroundExportReady(track);

    private AudioTrack? TrackForFileListContext(DependencyObject? source)
    {
        while (source != null && !ReferenceEquals(source, FileList))
        {
            if (source is ListBoxItem item)
            {
                if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), FileList)) return null;
                // Recycled containers update their bound Tag with the actual row.
                // Never fall back to the current selection on an empty list area.
                var track = item.Tag as AudioTrack ?? (item.DataContext as TrackListEntry)?.Track;
                return track != null && _tracks.Contains(track) ? track : null;
            }
            source = TrackContextParent(source);
        }
        return null;
    }

    private static DependencyObject? TrackContextParent(DependencyObject source)
    {
        if (source is Visual or Visual3D)
        {
            var parent = VisualTreeHelper.GetParent(source);
            if (parent != null) return parent;
        }
        if (source is ContentElement content)
            return ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent;
        return (source as FrameworkElement)?.Parent ?? LogicalTreeHelper.GetParent(source);
    }

    private AudioTrack? SelectFileListContextTarget(DependencyObject? source)
    {
        var track = TrackForFileListContext(source);
        if (track is null) return null;
        FileList.Focus(); // Commit a focused numeric editor before evaluating readiness.
        if (!_tracks.Contains(track)) return null;
        SelectTrack(track);
        if (FileList.ItemContainerGenerator.ContainerFromItem(FileList.SelectedItem) is ListBoxItem item) item.Focus();
        return track;
    }

    private void FileListTrackContext(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        var track = SelectFileListContextTarget(e.OriginalSource as DependencyObject);
        if (track is null) return;
        ShowTrackContextMenu(track, FileList, e.GetPosition(FileList));
        e.Handled = true;
    }

    private ContextMenu CreateTrackContextMenu(AudioTrack track)
    {
        var menu = new ContextMenu();
        var export = new MenuItem { Header = "Export this file…", Tag = track, IsEnabled = CanExportTrack(track) };
        export.Click += async (_, _) => await ExportTrackClickAsync(track);
        menu.Items.Add(export);
        if (track.GeneratedSilenceSeconds.HasValue) return menu;
        menu.Items.Add(new Separator());
        var relink = new MenuItem { Header = "Relink…", IsEnabled = !_importing && !_exporting };
        // Let this menu close before opening the file/folder choices.
        relink.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => RequestTrackRelink(track)), DispatcherPriority.Input);
        menu.Items.Add(relink);
        return menu;
    }

    private void ShowTrackContextMenu(AudioTrack track, FrameworkElement placementTarget, Point point)
    {
        if (!_tracks.Contains(track)) return;
        placementTarget.Focus();
        SelectTrack(track);
        _trackContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = CreateTrackContextMenu(track);
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = point.X;
        menu.VerticalOffset = point.Y;
        _trackContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_trackContextMenu, menu)) _trackContextMenu = null; };
        menu.IsOpen = true;
    }

    private async Task ExportTrackClickAsync(AudioTrack track)
    {
        FinishStrengthGesture(false);
        CommitTarget();
        CommitCeiling();
        if (!CanExportTrack(track))
        {
            StatusText.Text = "This file's Result is not ready to export.";
            return;
        }
        bool float32 = _floatExport;
        var dialog = new SaveFileDialog
        {
            Title = "Export processed audio file",
            Filter = float32 ? "32-bit float WAV|*.wav" : "24-bit PCM WAV|*.wav",
            DefaultExt = ".wav", AddExtension = true, CheckPathExists = true,
            // The atomic writer always preserves an existing output or source.
            OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(track.Name) + " - Loudline.wav",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await ExportSingleTrackAsync(track, dialog.FileName, float32);
            StatusText.Text = $"Exported {(float32 ? "32-bit float" : "24-bit PCM")} WAV: {Path.GetFileName(dialog.FileName)}";
        }
        catch (OperationCanceledException) { StatusText.Text = "Export cancelled. Partial file removed."; }
        catch (Exception error) { ShowError("Could not export audio", error); }
    }

    /// <summary>Exports exactly one current Result, independently of other tracks' readiness.</summary>
    private async Task ExportSingleTrackAsync(AudioTrack track, string path, bool float32, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanExportTrack(track)) throw new InvalidOperationException("This file's Result is not ready to export.");
        string destination = Path.GetFullPath(path);
        if (ExportProtectedSourcePaths().Contains(destination))
            throw new IOException("Export must use a different path from every source file.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"A file already exists at {destination}. Choose a different export name.");
        // Only immutable rendered PCM goes to the writer. Monitoring gains and
        // Original/Comparison display modes never participate in this snapshot.
        var render = track.Render!;
        var snapshot = new AudioTrack
        {
            FilePath = track.FilePath, GeneratedSilenceSeconds = track.GeneratedSilenceSeconds,
            SampleRate = render.RenderedSampleRate > 0 ? render.RenderedSampleRate : track.SampleRate,
            Channels = render.RenderedChannels > 0 ? render.RenderedChannels : track.Channels, Render = render,
        };
        bool timelineEnabled = Timeline.IsEnabled, processingEnabled = ProcessingPanel.IsEnabled;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancelInterruptedPlayback();
        _exporting = true; _exportCts = cts;
        try
        {
            _player.Stop(); PlayButton.Content = "▶";
            ExportButton.IsEnabled = false; Timeline.IsEnabled = false; ProcessingPanel.IsEnabled = false;
            RefreshBackground();
            SetBusy($"Exporting {track.Name}…", true);
            BeginExportProgress(1);
            var progress = ExportFileProgress(1, 1, 0, 1, 1);
            await Task.Run(() => AudioFileService.ExportWav(snapshot, destination, float32, cts.Token, progress), cts.Token);
            FlushExportProgress();
        }
        finally
        {
            EndExportProgress();
            if (ReferenceEquals(_exportCts, cts)) _exportCts = null;
            cts.Dispose(); _exporting = false;
            if (!_closed)
            {
                Timeline.IsEnabled = timelineEnabled; ProcessingPanel.IsEnabled = processingEnabled;
                ExportButton.IsEnabled = !_importing && _tracks.Count > 0 && _renderedRevision == _revision &&
                    _tracks.All(item => item.WorkState == TrackWorkState.Idle && SourceAnalysisSatisfied(item, _settings) && PreviewCurrent(item, _settings) && BackgroundExportReady(item));
                RestoreTrackExportStatus();
                RefreshList();
            }
        }
    }

    private void RestoreTrackExportStatus()
    {
        // Other previews can finish, start their next track, or be cancelled
        // during export. Derive progress from live work rather than old UI state.
        var pending = _tracks.FirstOrDefault(item => item.WorkState is TrackWorkState.Processing or TrackWorkState.Analyzing)
            ?? _tracks.FirstOrDefault(item => item.WorkState == TrackWorkState.Queued);
        if (_importing) SetBusy("Loading audio…", true);
        else if (_renderCts is { IsCancellationRequested: false } && pending != null)
            SetBusy(pending.WorkState == TrackWorkState.Queued ? "Preview queued…" : $"Building preview: {pending.Name}…", true);
        else
        {
            int missing = _tracks.Count(item => item.WorkState == TrackWorkState.Missing);
            bool current = _tracks.All(item => SourceAnalysisSatisfied(item, _settings) && PreviewCurrent(item, _settings) && BackgroundExportReady(item));
            SetIdle(missing > 0 ? $"{missing} missing file{(missing == 1 ? "" : "s")}" : current ? "Ready" : "Preview needs updating");
        }
    }
}

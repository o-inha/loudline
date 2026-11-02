using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _closingProject;

    private async void CloseProjectClick(object sender, RoutedEventArgs e)
    {
        try { await CloseProjectAsync(); }
        catch (Exception error) { ShowError("Could not close project", error); }
    }

    private async Task<bool> CloseProjectAsync(Func<bool>? confirm = null)
    {
        if (_closed || _closingProject || _exporting) return false;
        if (!(confirm ?? ConfirmSave)()) return false;
        _closingProject = true;
        var content = Content as UIElement;
        bool enabled = content?.IsEnabled ?? true;
        try
        {
            CloseSettingsFlyout();
            FinishStrengthGesture(false);
            FinishBackgroundGainDrag(false); CloseBackgroundGainTextEdit();
            CancelPlayheadScrubbing();
            Timeline.ResetProjectInteractions();
            BackgroundLane.CompletePointerInteraction(false);
            CancelSourceWork();
            _backgroundOriginalResume = null; _originalImportResume = null;
            _importCts?.Cancel();
            content?.SetCurrentValue(IsEnabledProperty, false);
            SetBusy("Closing project…");

            // Source operations publish metadata and own their final cleanup.
            // Let cancellation finish that cleanup before clearing the session,
            // rather than allowing a late import continuation into an empty one.
            while (_importing || _openingProject) await Task.Delay(10);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            _renderCts?.Cancel();
            await _renderGate.WaitAsync();
            _renderGate.Release();
            if (_closed) return false;
            CancelSourceWork(); // Invalidate any preview queued by source cleanup.
            ResetEmptyProject();
            return true;
        }
        finally
        {
            _closingProject = false;
            if (!_closed) { content?.SetCurrentValue(IsEnabledProperty, enabled); RefreshAutoProcessState(); }
        }
    }

    private void ResetEmptyProject()
    {
        SetFileListVisible(true);
        ResetInsertSilenceOptions();
        _trackContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _trackContextMenu = null;
        _fileListDragTrack = null; ClearFileListDropMarker();
        _tracks.Clear(); _renderKeys.Clear(); _previewCache.Clear();
        _trackRows.Clear(); _rowsById.Clear();
        _undo.Clear(); _redo.Clear();
        _backgroundSegments.Clear(); _backgroundOriginals.Clear();
        _backgroundLayer = new(); _backgroundUiLayer = null; _backgroundUiSources = 0;
        _soloSourceLayer = _soloPlaybackLayer = null; _soloSourceSignature = 0;
        _plannedRenderKeys = null; _renderCts = null;
        _renderedRevision = _renderStartedRevision = -1;
        _pendingProcessingEdit = _processingRollback = _completedProcessing = null;
        _pendingProcessingHistory = _processingRollbackHistory = _completedProcessingHistory = [];
        _pendingProcessingDirty = _processingRollbackDirty = _completedProcessingDirty = false;
        _processingPaused = _restoringProcessing = _sourceProcessingPending = _atomicSourceOperation = false;
        EndProjectSourceDecodeCache();
        _projectPath = null; _backgroundProjectNeedsCopy = false;
        _settings = new ProcessingSettings(); _floatExport = false;
        _targetEditing = _targetTextDirty = false; ResetTargetReadout();
        RestoreProjectEnvelope([], []); LoadCustomBoundaries([]); LoadExportOptions(new()); LoadLoops(null);

        _syncing = true;
        try
        {
            BackgroundSoloButton.IsChecked = false;
            AuditionMatchCheck.IsChecked = false;
            DeltaCheck.IsChecked = false;
            Timeline.SliceMode = false;
            CustomBoundariesCheck.IsChecked = false;
            WaveformEnvelopeCheck.IsChecked = true; ShowGains.IsChecked = true;
            PreviewRadio.IsChecked = true;
            _visibleMusicHeight = new GridLength(82, GridUnitType.Star);
            _visibleBackgroundHeight = new GridLength(18, GridUnitType.Star);
            BackgroundVisibilityCheck.IsChecked = true;
            AudioLanes.RowDefinitions[0].Height = _visibleMusicHeight;
            AudioLanes.RowDefinitions[2].Height = _visibleBackgroundHeight;
        }
        finally { _syncing = false; }
        SyncControls(); ApplyTimelineSettings();
        Timeline.LivePreviewActive = false;
        Timeline.SelectedTrack = null; FileList.SelectedItem = null;
        Timeline.Playhead = 0; Timeline.EmptyDurationHint = 0;
        Timeline.ResetProjectInteractions(); BackgroundLane.SetHover(null);
        _playbackTracks = []; _player.UpdateTracks(_playbackTracks);
        RefreshList(); Timeline.FitAll(); UpdateTime(); UpdatePlayButton();
        UpdateCursorPosition(null);
        ReleaseSummary.Text = "No audio loaded";
        Title = $"Loudline {AppVersion.Display} — music loudness matching";
        ExportButton.IsEnabled = false;
        SetPreviewStatus(true); SetIdle("");
        _dirty = false;
        UpdateResponsiveLayout();
    }
}

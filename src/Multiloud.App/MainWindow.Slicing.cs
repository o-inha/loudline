using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private void InitializeSlicing()
    {
        // Cutting is an immediate command. The main waveform remains a normal
        // playhead/scrubbing surface after every cut.
        Timeline.SliceMode = false;
    }

    private async void SliceAtPlayheadClick(object sender, RoutedEventArgs e)
    {
        await SliceAtPlayheadAsync();
        e.Handled = true;
    }

    private Task SliceAtPlayheadAsync()
    {
        if (!_initialized || _closed || _closingProject || _importing || _exporting) return Task.CompletedTask;
        double position = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        double offset = 0;
        foreach (var track in _tracks)
        {
            double end = offset + track.Duration;
            if (position >= offset && position < end)
                return SliceTrackAsync(track, position - offset);
            offset = end;
        }
        return Task.CompletedTask;
    }

    private bool SliceNeedsAnalysis(AudioTrack track) => _settings.LevelingEnabled || _settings.MatchingEnabled ||
        _settings.LimiterEnabled;

    private sealed record SlicePlaybackIntent(double Position, bool Playing, bool Paused, bool Original, long NavigationVersion);

    private bool RearmSlicePlaybackAfterPreparation(SlicePlaybackIntent? intent)
    {
        if (intent == null || intent.NavigationVersion != _importNavigationVersion || _closed || _closingProject ||
            _exporting || intent.Original != (OriginalRadio.IsChecked == true && !IsPeakDelta)) return false;
        ArmPlaybackBeforeStructure(intent.Position, intent.Playing, intent.Paused);
        UpdatePlayButton();
        return intent.Original ? _backgroundOriginalResume != null : _interruptedPlayback != null;
    }

    private async Task SliceTrackAsync(AudioTrack track, double seconds, Task? beforeCopy = null)
    {
        if (_closed || _closingProject || _importing || _exporting || !_tracks.Contains(track) || track.Original.Length == 0) return;
        double frame = Math.Round(seconds * track.SampleRate);
        if (!double.IsFinite(frame) || frame <= 0 || frame >= track.OriginalFrameCount) return;
        FinishStrengthGesture(false);
        var originalSamples = track.Original;
        var sourceId = track.SourceId;
        var baseline = Capture(); var undoBefore = _undo.ToArray(); var redoBefore = _redo.ToArray();
        bool dirtyBefore = _dirty;
        var cts = new CancellationTokenSource(); _importCts = cts; _importing = true; _atomicSourceOperation = true;
        var ct = cts.Token;
        bool published = false, rollback = false;
        SlicePlaybackIntent? listening = null;
        ImportPositionAnchor? pendingListening = null;
        try
        {
            var pieces = AudioTrackSlicer.SplitShared(track, seconds, ct);
            pieces.Left.Status = pieces.Right.Status = "Preparing slice";
            var capturedListening = CaptureRollbackPlaybackIntent();
            bool playing = capturedListening != null && !capturedListening.Paused, paused = capturedListening?.Paused == true;
            double position = capturedListening?.PositionSeconds ?? Timeline.Playhead;
            int index = _tracks.IndexOf(track);
            PushUndo(); PreserveProjectEnvelopeStructure(); RememberActivePreview(track);
            _renderCts?.Cancel(); ++_revision;
            CancelInterruptedPlayback(); _player.Stop();
            if (playing || paused) listening = new(position, playing, paused,
                OriginalRadio.IsChecked == true && !IsPeakDelta, _importNavigationVersion);
            _tracks.RemoveAt(index); _tracks.Insert(index, pieces.Right); _tracks.Insert(index, pieces.Left);
            _renderKeys.Remove(track.Id);
            SyncProjectEnvelope(preserveShape: true);
            Timeline.Playhead = position;
            // Preserve the audible position while the structural publication
            // replaces the old clip. Active modules may still need to analyze
            // the new ranges before Result playback can continue.
            if (playing && PlaybackTrackAt(CapturePlaybackTracks(), position) is { } waiting &&
                _tracks.FirstOrDefault(item => item.Id == waiting.Id) is { } waitingTrack)
            {
                double offset = _tracks.TakeWhile(item => item.Id != waitingTrack.Id).Sum(PlaybackDuration);
                pendingListening = new(waitingTrack.Id, waitingTrack.SourceId, Math.Max(0, position - offset),
                    _importNavigationVersion, true, OriginalRadio.IsChecked == true && !IsPeakDelta);
                _originalImportResume = pendingListening;
            }
            SelectTrack(pieces.Right);
            _dirty = true; published = true;
            ExportButton.IsEnabled = false;
            SetBusy("Preparing slices…", true);
            RefreshBackground(); RefreshList(); UpdateTime();

            // These clips are immutable views over the decoded source. A cut
            // needs no PCM copying, bed render, or loudness scan in bypass.
            if (beforeCopy != null) await beforeCopy.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (_closed || _closingProject || track.SourceId != sourceId || !ReferenceEquals(track.Original, originalSamples) ||
                !_tracks.Contains(pieces.Left) || !_tracks.Contains(pieces.Right)) return;
            if (ReferenceEquals(_originalImportResume, pendingListening)) _originalImportResume = null;
            RearmSlicePlaybackAfterPreparation(listening);
            RefreshList();
            foreach (var piece in new[] { pieces.Left, pieces.Right })
            {
                // Gear settings can change during preparation. The source slots
                // keep their own controls; use the current module state now.
                if (!SliceNeedsAnalysis(piece))
                { piece.WorkState = TrackWorkState.Idle; piece.Status = "Ready"; continue; }
                piece.WorkState = TrackWorkState.Analyzing; piece.Status = "Analyzing loudness…";
                SetBusy($"Analyzing {piece.Name}…", true); RefreshList();
                var original = piece.Original;
                var prepared = await Task.Run(() =>
                {
                    var samples = piece.GetOriginalRegion(ct);
                    return (Samples: samples, Analysis: LoudnessAnalyzer.Analyze(samples, piece.SampleRate, piece.Channels, ct));
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (!_tracks.Contains(piece) || !ReferenceEquals(piece.Original, original)) return;
                piece.Analysis = prepared.Analysis; piece.WorkState = TrackWorkState.Queued; piece.Status = "Queued for preview";
                QueueRender(addition: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { rollback = published; }
        catch (Exception error)
        {
            rollback = published;
            if (!_closed && !_closingProject) ShowError("Could not slice audio", error);
        }
        finally
        {
            _importing = false; _atomicSourceOperation = false;
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            if (ReferenceEquals(_originalImportResume, pendingListening)) _originalImportResume = null;
            bool cancelled = ct.IsCancellationRequested;
            cts.Dispose();
            if (!_closed && !_closingProject)
            {
                if (rollback)
                {
                    _renderCts?.Cancel(); ++_revision;
                    _restoringProcessing = true;
                    try { Restore(baseline); }
                    finally { _restoringProcessing = false; }
                    _undo.Clear(); foreach (var entry in undoBefore.Reverse()) _undo.Push(entry);
                    _redo.Clear(); foreach (var entry in redoBefore.Reverse()) _redo.Push(entry);
                    _dirty = dirtyBefore;
                    if (_tracks.All(item => PreviewCurrent(item, _settings))) RememberCompletedProcessing();
                    RearmSlicePlaybackAfterPreparation(listening);
                }
                RefreshBackground(); RefreshList(); UpdateTime();
                if (!rollback && published && !cancelled)
                {
                    // Rendering may already be queued from the first analyzed
                    // piece. Establish its atomic Undo boundary once source
                    // preparation no longer owns the processing transaction.
                    BeginProcessingTransaction();
                    QueueRender();
                }
                else CompletePreview(_revision);
            }
        }
    }
}

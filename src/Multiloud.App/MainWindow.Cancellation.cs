using System.IO;
using System.Windows;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    // A processing transaction can span several coalesced edits. Keep the state
    // from before the first edit until all of its audio variants are current.
    private EditSnapshot? _pendingProcessingEdit, _processingRollback, _completedProcessing;
    private EditSnapshot[] _completedProcessingHistory = [], _pendingProcessingHistory = [], _processingRollbackHistory = [];
    private bool _completedProcessingDirty;
    private bool _pendingProcessingDirty, _processingRollbackDirty;
    private bool _processingPaused, _restoringProcessing, _sourceProcessingPending, _atomicSourceOperation;

    private void BeginSourceProcessing()
    {
        _sourceProcessingPending = true;
        _processingPaused = false;
        FinishProcessingTransaction();
        _completedProcessing = null;
        _completedProcessingHistory = [];
    }

    private void RememberProcessingEdit(EditSnapshot snapshot)
    {
        if (_restoringProcessing || _processingRollback != null) return;
        _pendingProcessingEdit = snapshot;
        _pendingProcessingHistory = _undo.ToArray();
        _pendingProcessingDirty = _dirty;
    }

    private void BeginProcessingTransaction()
    {
        if (_importing || _restoringProcessing || _processingRollback != null ||
            _sourceProcessingPending && _pendingProcessingEdit == null) return;
        var baseline = _pendingProcessingEdit ?? _completedProcessing;
        if (baseline == null) return;
        _processingRollback = baseline;
        _processingRollbackHistory = _pendingProcessingEdit != null ? _pendingProcessingHistory : _completedProcessingHistory;
        _processingRollbackDirty = _pendingProcessingEdit != null ? _pendingProcessingDirty : _completedProcessingDirty;
        _pendingProcessingEdit = null;
    }

    private void RememberCompletedProcessing()
    {
        // A cached Restore can finish synchronously before Cancel restores the
        // original dirty flag and history. Capture only the completed state.
        if (_restoringProcessing) return;
        FinishProcessingTransaction();
        _sourceProcessingPending = false;
        _completedProcessing = Capture();
        _completedProcessingHistory = _undo.ToArray();
        _completedProcessingDirty = _dirty;
    }

    private void FinishProcessingTransaction()
    {
        _processingRollback = null;
        _pendingProcessingEdit = null;
    }

    private static void MarkNotProcessed(AudioTrack track)
    {
        // Cancellation says nothing about the existence of a file. Keep any
        // immutable decoded audio that was already published for later resume.
        if (track.WorkState == TrackWorkState.Missing) return;
        track.SavedDuration = track.Duration;
        track.WorkState = TrackWorkState.Unprocessed;
        track.Status = "File not processed";
    }

    private void FinishSourceCancellation()
    {
        _processingPaused = true;
        FinishProcessingTransaction();
        foreach (var track in _tracks)
            if (track.WorkState is TrackWorkState.Analyzing or TrackWorkState.Queued or TrackWorkState.Processing ||
                track.WorkState != TrackWorkState.Missing && (!SourceAnalysisSatisfied(track, _settings) || !PreviewCurrent(track, _settings)))
                MarkNotProcessed(track);
        foreach (var source in _backgroundLayer.Clips.Select(clip => clip.Source).Distinct())
            if (source.WorkState != TrackWorkState.Missing && source.Original.Length == 0) MarkNotProcessed(source);
        RefreshList();
        CompletePreview(_revision);
    }

    private sealed record RollbackPlaybackIntent(Guid TrackId, Guid SourceId, double LocalSeconds,
        double PositionSeconds, bool Original, bool Paused);

    private RollbackPlaybackIntent? CaptureRollbackPlaybackIntent()
    {
        bool playing = _player.IsPlaying, paused = _player.IsPaused;
        if (!playing && !paused && _interruptedPlayback == null &&
            !(_backgroundOriginalResume is { } background && background.NavigationVersion == _importNavigationVersion)) return null;
        bool original = OriginalRadio.IsChecked == true && !IsPeakDelta;
        double position = playing || paused ? _player.PositionSeconds : Timeline.Playhead;
        if (_interruptedPlayback is { } pending)
            return new(pending.TrackId, pending.SourceId, pending.LocalSeconds, position, false, false);
        if (!playing && !paused && _backgroundOriginalResume is { } pendingOriginal)
            return new(pendingOriginal.TrackId, pendingOriginal.SourceId, pendingOriginal.LocalSeconds, position, true, pendingOriginal.Paused);
        var state = PlaybackTrackAt(CapturePlaybackTracks(), position);
        var track = state == null ? null : _tracks.FirstOrDefault(item => item.Id == state.Id);
        double offset = track == null ? 0 : _tracks.TakeWhile(item => item.Id != track.Id).Sum(PlaybackDuration);
        return new(track?.Id ?? Guid.Empty, track?.SourceId ?? Guid.Empty,
            Math.Max(0, position - offset), position, original, paused);
    }

    private void RestoreRollbackPlaybackIntent(RollbackPlaybackIntent? intent, Action<double>? resume)
    {
        if (intent == null || _closed || _exporting || intent.Original != (OriginalRadio.IsChecked == true && !IsPeakDelta)) return;
        var track = _tracks.FirstOrDefault(item => item.Id == intent.TrackId && item.SourceId == intent.SourceId);
        double position = track == null ? intent.PositionSeconds :
            _tracks.TakeWhile(item => item.Id != track.Id).Sum(PlaybackDuration) + Math.Min(intent.LocalSeconds, PlaybackDuration(track));
        Timeline.Playhead = position; UpdateTime();
        if (PlaybackTrackAt(CapturePlaybackTracks(), position)?.IsReady(intent.Original) == true)
        {
            if (resume != null) resume(position);
            else
            {
                StartPlayback(position);
                if (intent.Paused && _player.IsPlaying) _player.Pause();
                if (intent.Paused) _playbackRequested = false;
            }
        }
        else if (intent.Original) ArmBackgroundOriginalResume(position, intent.Paused);
        else if (!intent.Paused) ArmInterruptedPlayback(position, true, false, _revision);
        UpdatePlayButton();
    }

    private void CancelProcessing(Action<double>? resume = null)
    {
        FinishStrengthGesture(false);
        var listening = _processingRollback == null ? null : CaptureRollbackPlaybackIntent();
        CancelInterruptedPlayback();
        _backgroundOriginalResume = null;
        _renderCts?.Cancel();
        ++_revision; _renderStartedRevision = _renderedRevision = -1;
        _plannedRenderKeys = null;
        if (_processingRollback is { } previous)
        {
            bool dirty = _processingRollbackDirty;
            var history = _processingRollbackHistory;
            // Export-only edits made while DSP is running remain independent of
            // canceling that DSP change, including their own undo frames.
            var exportState = Capture();
            var independent = _undo.TakeWhile(item => !history.Any(saved => ReferenceEquals(saved, item)))
                .Where(item => item.ExportOnly || item.LoopsOnly).ToArray();
            var restored = previous with
            {
                CustomTrackBoundaries = exportState.CustomTrackBoundaries,
                ExportOptions = exportState.ExportOptions, FloatExport = exportState.FloatExport, Loops = exportState.Loops
            };
            bool exportChanged = !previous.CustomTrackBoundaries.SequenceEqual(exportState.CustomTrackBoundaries) ||
                previous.ExportOptions != exportState.ExportOptions || previous.FloatExport != exportState.FloatExport || previous.Loops != exportState.Loops;
            FinishProcessingTransaction();
            _undo.Clear();
            foreach (var item in history.Reverse()) _undo.Push(item);
            foreach (var item in independent.Reverse()) _undo.Push(previous with
            {
                CustomTrackBoundaries = item.CustomTrackBoundaries, ExportOptions = item.ExportOptions,
                FloatExport = item.FloatExport, Loops = item.Loops, ExportOnly = item.ExportOnly, LoopsOnly = item.LoopsOnly
            });
            if (_undo.Count > 12)
            {
                var keep = _undo.Take(12).Reverse().ToArray(); _undo.Clear();
                foreach (var item in keep) _undo.Push(item);
            }
            _redo.Clear();
            _processingPaused = false;
            // Restore itself must not consume a live device's old intent before
            // its source-local position and restored readiness are reconciled.
            if (listening != null) { _player.Stop(); Timeline.Playhead = listening.PositionSeconds; }
            _restoringProcessing = true;
            try { Restore(restored); }
            finally { _restoringProcessing = false; }
            _dirty = dirty || exportChanged;
            if (_tracks.All(track => SourceAnalysisSatisfied(track, _settings) && PreviewCurrent(track, _settings))) RememberCompletedProcessing();
            RestoreRollbackPlaybackIntent(listening, resume);
        }
        else FinishSourceCancellation();
    }

    private bool HasUnprocessedSources => _tracks.Any(track => track.WorkState != TrackWorkState.Missing && !SourceAnalysisSatisfied(track, _settings)) ||
        _backgroundLayer.Clips.Any(clip => clip.Source.WorkState != TrackWorkState.Missing && clip.Source.Original.Length == 0);

    private async Task ResumeProcessingAsync()
    {
        if (_closed || _importing || _exporting) return;
        BeginSourceProcessing();
        if (!HasUnprocessedSources) { QueueRender(); return; }
        _importing = true; ExportButton.IsEnabled = false;
        var cts = new CancellationTokenSource(); _importCts = cts;
        var ct = cts.Token;
        BeginProjectSourceDecodeCache();
        try
        {
            foreach (var source in _backgroundLayer.Clips.Select(clip => clip.Source).Distinct().ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (source.WorkState == TrackWorkState.Missing || source.Original.Length > 0) continue;
                if (!File.Exists(source.FilePath)) { MarkUnavailable(source); continue; }
                try { await LoadBackgroundSourceAsync(source, source.FilePath, ct); }
                catch (OperationCanceledException) { MarkNotProcessed(source); throw; }
                catch (Exception error) { source.WorkState = TrackWorkState.Failed; source.Status = $"Could not process file: {error.Message}"; }
            }
            foreach (var track in _tracks.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (track.WorkState == TrackWorkState.Missing || track.Analysis != null || TryPublishIdentityPreview(track, _settings)) continue;
                if (track.Original.Length == 0 && !File.Exists(track.FilePath)) { MarkUnavailable(track); continue; }
                string projectPath = _projectPath ?? Path.Combine(Environment.CurrentDirectory, "Release.loudline");
                var saved = SavedTrack(track, projectPath);
                bool needsSuggestion = track.Envelope.Count == 0;
                try
                {
                    if (track.Original.Length == 0)
                        await LoadProjectSourceAsync(track, saved, track.FilePath, ct, _settings);
                    else
                    {
                        // Cancellation during analysis already left a fully
                        // decoded immutable source. Resume at that stage.
                        track.WorkState = TrackWorkState.Analyzing; track.Status = "Analyzing loudness…";
                        SetBusy($"Analyzing {track.Name}…", true); RefreshList();
                        var samples = track.Original; int rate = track.SampleRate, channels = track.Channels;
                        var prepared = await Task.Run(() =>
                        {
                            var region = track.GetOriginalRegion(ct);
                            return (Samples: region, Analysis: LoudnessAnalyzer.Analyze(region, rate, channels, ct));
                        }, ct);
                        ct.ThrowIfCancellationRequested();
                        if (!_tracks.Contains(track) || !ReferenceEquals(track.Original, samples)) throw new OperationCanceledException(ct);
                        track.Analysis = prepared.Analysis; track.WorkState = TrackWorkState.Queued; track.Status = "Queued for preview";
                    }
                    if (needsSuggestion && track.Analysis != null)
                    {
                        track.Envelope = EnvelopeDesigner.Design(track.Analysis, track.Duration, _settings);
                        UpdateTransitions();
                    }
                }
                catch (OperationCanceledException) { MarkNotProcessed(track); throw; }
                catch (Exception error) { track.WorkState = TrackWorkState.Failed; track.Status = $"Could not process file: {error.Message}"; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            EndProjectSourceDecodeCache();
            _importing = false;
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            bool cancelled = ct.IsCancellationRequested;
            cts.Dispose();
            if (!_closed)
            {
                if (cancelled) FinishSourceCancellation();
                else { RefreshList(); QueueRender(addition: true); }
            }
        }
    }
}

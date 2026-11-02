using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async void MergeAtPlayheadClick(object sender, RoutedEventArgs e)
    {
        try { await MergeAtPlayheadAsync(); }
        catch (Exception error) { if (!_closed) ShowError("Could not merge audio", error); }
    }

    private static int NearestMergeBoundary(IReadOnlyList<AudioTrack> tracks, double position)
    {
        if (tracks.Count < 2) return -1;
        double at = 0, distance = double.PositiveInfinity;
        int nearest = 1;
        for (int index = 1; index < tracks.Count; index++)
        {
            at += tracks[index - 1].Duration;
            double delta = Math.Abs(at - position);
            if (delta < distance) { distance = delta; nearest = index; }
        }
        return nearest;
    }

    private async Task<bool> MergeAtPlayheadAsync(Task? beforePrepare = null)
    {
        if (_closed || _closingProject || _importing || _exporting) return false;
        double position = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        int boundary = NearestMergeBoundary(_tracks, position);
        if (boundary < 0) return false;
        var left = _tracks[boundary - 1]; var right = _tracks[boundary];
        if (left.Original.Length == 0 || right.Original.Length == 0) return false;
        FinishStrengthGesture(false);
        var baseline = Capture(); var undo = _undo.ToArray(); var redo = _redo.ToArray(); bool dirty = _dirty;
        var authored = _projectEnvelope.ToArray(); var suggestion = _projectSuggestion.ToArray();
        var captured = CaptureRollbackPlaybackIntent();
        SlicePlaybackIntent? listening = captured == null ? null : new(captured.PositionSeconds,
            !captured.Paused, captured.Paused, captured.Original, _importNavigationVersion);
        var cts = new CancellationTokenSource(); var ct = cts.Token;
        _importCts = cts; _importing = true; _atomicSourceOperation = true;
        bool published = false, rollback = false, gateHeld = false;
        try
        {
            var merged = AudioTrackMerger.CreateSlot(left, right);
            long frames = (long)Math.Round((left.Duration + right.Duration) * merged.SampleRate);
            if (frames * merged.Channels > 536000000)
                throw new InvalidOperationException("The combined source exceeds the supported audio size. Use smaller sections.");
            PushUndo(); PreserveProjectEnvelopeStructure(); RememberActivePreview(left); RememberActivePreview(right);
            CancelSourceWorkForReplacement(ref listening);
            _tracks.RemoveAt(boundary); _tracks.RemoveAt(boundary - 1); _tracks.Insert(boundary - 1, merged);
            _renderKeys.Remove(left.Id); _renderKeys.Remove(right.Id);
            RestoreProjectEnvelope(authored, suggestion);
            Timeline.Playhead = position; SelectTrack(merged); _dirty = true; published = true;
            ExportButton.IsEnabled = false; SetBusy("Preparing merged source…", true);
            RefreshBackground(); RefreshList(); UpdateTime();
            // The boundary is already gone. Same-source adjacent cuts share
            // their original PCM; other sources are joined on a worker.
            if (beforePrepare != null) await beforePrepare.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            await _renderGate.WaitAsync(ct); gateHeld = true;
            if (merged.Original.Length == 0)
            {
                long samples = frames * merged.Channels;
                long additional = samples * (5 + (ModulesBypassed(_settings) ? 0 : 9 + 8 / merged.Channels));
                if (EstimateMergeWorkingAudioBytes(undo.Concat(redo)) + additional > 12L * 1024 * 1024 * 1024)
                    throw new InvalidOperationException("Merging would exceed the 12 GB working audio budget.");
                // The published placeholder keeps its light summary until the
                // complete source and pyramid are ready. Never expose PCM with
                // an unfinished pyramid to the UI's waveform renderer.
                var prepared = new AudioTrack { FilePath = merged.FilePath, MergedSourceId = merged.MergedSourceId,
                    MergedSources = merged.MergedSources, SampleRate = merged.SampleRate, Channels = merged.Channels,
                    SavedDuration = merged.SavedDuration };
                await Task.Run(() => AudioTrackMerger.Materialize(prepared, ct), ct);
                ct.ThrowIfCancellationRequested();
                if (_closed || _closingProject || !_tracks.Contains(merged) || !ReferenceEquals(_importCts, cts)) return false;
                merged.Original = prepared.Original; merged.OriginalFrameOffset = 0; merged.OriginalFrameLength = null;
                merged.OriginalWaveform = prepared.OriginalWaveform; merged.OriginalPeaks = prepared.OriginalPeaks;
                merged.SavedDuration = prepared.Duration;
            }
            if (EstimateMergeWorkingAudioBytes(undo.Concat(redo)) > 12L * 1024 * 1024 * 1024)
                throw new InvalidOperationException("Merging would exceed the 12 GB working audio budget.");
            if (_closed || !_tracks.Contains(merged)) return false;
            if (!ModulesBypassed(_settings))
            {
                merged.WorkState = TrackWorkState.Analyzing; merged.Status = "Analyzing loudness…";
                RefreshList();
                merged.Analysis = await Task.Run(() => LoudnessAnalyzer.Analyze(merged.GetOriginalRegion(ct),
                    merged.SampleRate, merged.Channels, ct), ct);
            }
            ct.ThrowIfCancellationRequested();
            merged.WorkState = TrackWorkState.Queued; merged.Status = "Queued for preview";
            RestoreProjectEnvelope(_projectEnvelope.ToArray(), _projectSuggestion.ToArray());
            _projectEnvelopeLoaded = _tracks.Any(track => track.Analysis == null);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { rollback = published; return false; }
        catch { rollback = published; throw; }
        finally
        {
            if (gateHeld) _renderGate.Release();
            _importing = false; _atomicSourceOperation = false;
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            cts.Dispose();
            if (!_closed && !_closingProject)
            {
                if (rollback)
                {
                    _restoringProcessing = true;
                    try { Restore(baseline); } finally { _restoringProcessing = false; }
                    _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
                    _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
                    _dirty = dirty;
                }
                else if (published) BeginProcessingTransaction();
                QueueRender(); RearmSlicePlaybackAfterPreparation(listening);
                RefreshBackground(); RefreshList(); UpdateTime();
            }
        }
    }

    private long EstimateMergeWorkingAudioBytes(IEnumerable<EditSnapshot> operationHistory)
    {
        // Repeated joins can retain previous composite arrays for Undo. Count
        // their unique roots too, including redo states held for cancellation,
        // before allocating the next joined source.
        var history = _undo.Concat(_redo).Concat(operationHistory).ToArray();
        var backgrounds = _backgroundLayer.Clips.Select(clip => clip.Source);
        long active = EstimateWorkingAudioBytes(_tracks, backgrounds, false);
        long retained = EstimateWorkingAudioBytes(_tracks.Concat(history.SelectMany(snapshot => snapshot.Tracks.Select(edit => edit.Track))),
            backgrounds.Concat(history.SelectMany(snapshot => snapshot.Background.Clips.Select(clip => clip.Source))), false);
        return EstimateWorkingAudioBytes() + Math.Max(0, retained - active);
    }
}

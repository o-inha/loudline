using System.IO;
using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyCancellationSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty;
        bool originalView = OriginalRadio.IsChecked == true, comparisonView = CompareRadio.IsChecked == true;
        var selected = Selected; double playhead = Timeline.Playhead;
        string status = StatusText.Text;
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineCancellation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool gateHeld = false;
        try
        {
            foreach (var item in _tracks) RememberActivePreview(item);
            CancelInterruptedPlayback(); _player.Stop(); PreviewRadio.IsChecked = true;
            _tracks.Clear(); _renderKeys.Clear(); _backgroundLayer = new();
            _settings = new ProcessingSettings { LevelingEnabled = false, LimiterEnabled = false, TargetLufs = -20 };
            var track = await Task.Run(() => CreateRenderPriorityTrack(9, 2, _settings));
            _tracks.Add(track); RestoreProjectEnvelope([], []);
            _renderKeys[track.Id] = RenderKey(track, _settings);
            _processingPaused = _sourceProcessingPending = false; FinishProcessingTransaction();
            SyncControls(); RefreshList(); CompletePreview(_revision);
            await WaitForSmokePreviewAsync();
            _dirty = false; RememberCompletedProcessing();
            var oldRender = track.Render; var oldEnvelope = _projectEnvelope.ToArray();
            var oldSettings = _settings; int oldUndoCount = _undo.Count, oldRenderCount = _renderExecutionCount;
            await _renderGate.WaitAsync(); gateHeld = true;
            PushUndo(); _settings = _settings with { TargetLufs = -17, Strength = .3 }; _dirty = true;
            SyncControls(); QueueRender();
            RequireCancellation(CancelButton.Visibility == Visibility.Visible && RebuildPreviewButton.Visibility == Visibility.Collapsed,
                "Processing exposed Resume and Cancel simultaneously or hid cancellation during settling.");
            CancelClick(this, new RoutedEventArgs());
            RequireCancellation(_settings == oldSettings && StrengthSlider.Value == oldSettings.Strength &&
                ReferenceEquals(track.Render, oldRender) && _projectEnvelope.SequenceEqual(oldEnvelope) &&
                _undo.Count == oldUndoCount && !_dirty && _renderExecutionCount == oldRenderCount,
                "Cancel did not restore the previous parameters, authored envelope, cached output and undo/dirty state.");
            _renderGate.Release(); gateHeld = false;
            await WaitForSmokePreviewAsync();

            // A discrete/programmatic strength edit can have no new undo frame.
            // The completed processing snapshot must still provide its baseline.
            oldSettings = _settings;
            _settings = _settings with { TargetLufs = -16.5 }; QueueRender();
            CancelClick(this, new RoutedEventArgs());
            RequireCancellation(_settings == oldSettings && ReferenceEquals(track.Render, oldRender) && !_dirty,
                "Cancellation without a new Undo frame retained new parameters or dirtied the previously clean project.");
            await WaitForSmokePreviewAsync();

            // Cancel restores a listener waiting for the changed Result rather
            // than dropping their intent after the old cached PCM is restored.
            oldSettings = _settings;
            PushUndo(); _settings = _settings with { TargetLufs = -16.25 }; QueueRender();
            Timeline.Playhead = .75;
            RequireCancellation(ArmInterruptedPlayback(.75, true, false, _revision),
                "The rollback playback fixture did not retain an interrupted listener.");
            var resumed = new List<double>();
            CancelProcessing(resumed.Add);
            RequireCancellation(_settings == oldSettings && resumed.SequenceEqual(new[] { .75 }) &&
                !HasInterruptedPlayback && !_player.IsPlaying && !_player.IsPaused && !_dirty,
                "Cancel did not resume the restored cached Result exactly once at the listening position.");
            CancelInterruptedPlayback();
            await WaitForSmokePreviewAsync();

            string a = Path.Combine(directory, "001.wav"), b = Path.Combine(directory, "002.wav");
            WriteBackgroundSmokeAudio(a, 1, 12000, 1, (frame, _) => .03 * Math.Sin(2 * Math.PI * 440 * frame / 12000));
            WriteBackgroundSmokeAudio(b, 1, 12000, 1, (frame, _) => .04 * Math.Sin(2 * Math.PI * 530 * frame / 12000));
            // Import publishes every selected source slot before its first await.
            // Cancel immediately exercises both in-flight and not-yet-started files.
            var importing = ImportFilesAsync([a, b]);
            CancelClick(this, new RoutedEventArgs());
            await importing;
            var pending = _tracks.Where(item => item.FilePath == a || item.FilePath == b).ToArray();
            RequireCancellation(pending.Length == 2 && pending.All(item => item.WorkState == TrackWorkState.Unprocessed &&
                item.Status == "File not processed" && File.Exists(item.FilePath)) &&
                RebuildPreviewButton.Visibility == Visibility.Visible &&
                RebuildPreviewButton.Content?.ToString() == "Resume processing" && CancelButton.Visibility == Visibility.Collapsed,
                "Cancelled import removed source slots, called found files missing, or failed to offer one footer Resume action.");
            await ResumeProcessingAsync(); await WaitForSmokePreviewAsync();
            RequireCancellation(pending.All(item => item.Analysis != null && item.Original.Length > 0 &&
                item.WorkState == TrackWorkState.Idle && PreviewCurrent(item, _settings)),
                "Resume did not decode, analyze and render every retained cancelled source.");
            var backgroundImport = ImportBackgroundFilesAsync([a, b], .25);
            CancelClick(this, new RoutedEventArgs());
            await backgroundImport;
            RequireCancellation(_backgroundLayer.Clips.Count == 2 && _backgroundLayer.Clips.All(clip =>
                clip.Source.WorkState != TrackWorkState.Missing && File.Exists(clip.Source.FilePath)) &&
                _backgroundLayer.Clips.Any(clip => clip.Source.WorkState == TrackWorkState.Unprocessed) &&
                Math.Abs(_backgroundLayer.Clips[0].StartSeconds - .25) < 1e-9 &&
                Math.Abs(_backgroundLayer.Clips[1].StartSeconds - _backgroundLayer.Clips[0].EndSeconds) < 1e-9,
                "Cancelled background import lost selected files or their sequential source placements.");
            await ResumeProcessingAsync(); await WaitForSmokePreviewAsync();
            RequireCancellation(_backgroundLayer.Clips.All(clip => clip.Source.Original.Length > 0 &&
                clip.Source.WorkState == TrackWorkState.Idle) && _tracks.All(track => PreviewCurrent(track, _settings)),
                "Resume did not load retained background files and refresh the final mix.");
            var missing = new AudioTrack { FilePath = Path.Combine(directory, "missing.wav"), SavedDuration = 1,
                WorkState = TrackWorkState.Missing, Status = "File not found" };
            _tracks.Add(missing); MarkNotProcessed(missing);
            RequireCancellation(missing.WorkState == TrackWorkState.Missing && missing.Status == "File not found",
                "Cancellation relabeled a genuinely absent source as unprocessed.");
            _tracks.Remove(missing);
        }
        finally
        {
            CancelInterruptedPlayback(); _importCts?.Cancel(); _renderCts?.Cancel(); ++_revision;
            if (gateHeld) _renderGate.Release();
            await _renderGate.WaitAsync(); _renderGate.Release();
            _processingPaused = _sourceProcessingPending = false; FinishProcessingTransaction();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; Timeline.Playhead = playhead;
            if (originalView) OriginalRadio.IsChecked = true;
            else if (comparisonView) CompareRadio.IsChecked = true;
            else PreviewRadio.IsChecked = true;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            StatusText.Text = status;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RequireCancellation(bool value, string message)
    { if (!value) throw new InvalidOperationException("Cancellation: " + message); }
}

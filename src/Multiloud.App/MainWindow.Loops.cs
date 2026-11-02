using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private PlaybackLoopState _loops = PlaybackLoopState.Empty;

    private void InitializeLoops()
    {
        Timeline.LoopState = _loops;
        Timeline.LoopEditStarted += PushLoopUndo;
        Timeline.LoopsEdited += state =>
        {
            _loops = state;
            _player.UpdateLoops(state);
            _dirty = true;
            RefreshWaitingLoopPlayback();
        };
        _player.UpdateLoops(_loops);
    }

    private void PushLoopUndo()
    {
        if (!_initialized || _syncing || _closed || _closingProject || _exporting) return;
        _undo.Push(Capture() with { LoopsOnly = true });
        _redo.Clear();
        if (_undo.Count > 12)
        {
            var keep = _undo.Take(12).Reverse().ToArray(); _undo.Clear();
            foreach (var item in keep) _undo.Push(item);
        }
    }

    private void LoopToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncing || _closed || _closingProject) return;
        if (_exporting) { ApplyLoopState(_loops); return; }
        var next = _loops with { AEnabled = LoopACheck.IsChecked == true, BEnabled = LoopBCheck.IsChecked == true };
        if (next == _loops) return;
        PushLoopUndo();
        ApplyLoopState(next);
        _dirty = true;
    }

    private void ApplyLoopState(PlaybackLoopState state)
    {
        _loops = state;
        Timeline.LoopState = state;
        _player.UpdateLoops(state);
        bool syncing = _syncing; _syncing = true;
        try { LoopACheck.IsChecked = state.AEnabled; LoopBCheck.IsChecked = state.BEnabled; }
        finally { _syncing = syncing; }
        RefreshWaitingLoopPlayback();
    }

    private bool RefreshWaitingLoopPlayback(Action<double>? resume = null)
    {
        if (_closed || _closingProject || _openingProject || _exporting || _syncing ||
            _player.IsPlaying || _player.IsPaused || IsPlayheadScrubbing ||
            !(_playbackRequested || HasInterruptedPlayback || HasBackgroundOriginalResume || HasImportPlaybackIntent)) return false;
        var states = CapturePlaybackTracks();
        _playbackTracks = states; _player.UpdateTracks(states);
        double position = _player.ResolvePlayPosition(Timeline.Playhead);
        var current = PlaybackTrackAt(states, position);
        CancelInterruptedPlayback(); _backgroundOriginalResume = null; _originalImportResume = null;
        Timeline.Playhead = position; UpdateTime();
        bool original = OriginalRadio.IsChecked == true;
        if (current?.IsReady(original) == true)
        {
            _player.RememberLoopContinuation(position);
            if (resume != null) { _playbackRequested = true; resume(position); }
            else StartPlayback(position);
            UpdatePlayButton(); return true;
        }
        bool armed = original && !IsPeakDelta
            ? ArmBackgroundOriginalResume(position, paused: false)
            : ArmInterruptedPlayback(position, true, false, _revision);
        if (!armed) _playbackRequested = false;
        UpdatePlayButton(); return false;
    }

    private PlaybackLoopState CaptureLoops() => _loops;

    private void LoadLoops(PlaybackLoopState? state)
    {
        state ??= PlaybackLoopState.Empty;
        static LoopRange? Valid(LoopRange? range) => range != null &&
            double.IsFinite(range.StartSeconds) && double.IsFinite(range.EndSeconds) &&
            range.StartSeconds >= 0 && range.EndSeconds > range.StartSeconds ? range : null;
        Timeline.CompleteLoopInteraction(allowSeek: false);
        ApplyLoopState(state with { A = Valid(state.A), B = Valid(state.B) });
    }
}

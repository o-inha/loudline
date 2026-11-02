using System.Windows;
using Multiloud.App.Services;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _changingPeakDelta;
    private bool IsPeakDelta => DeltaCheck?.IsChecked == true;

    private void SyncPeakDeltaControls()
    {
        if (AuditionMatchCheck != null) AuditionMatchCheck.IsEnabled = !IsPeakDelta;
    }

    private void PeakDeltaChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncing || _closed) return;
        CancelPlayheadScrubbing();
        bool playing = _player.IsPlaying, paused = _player.IsPaused;
        bool requested = playing || _playbackRequested || HasInterruptedPlayback;
        double position = playing || paused ? _player.PositionSeconds : Timeline.Playhead;
        CancelInterruptedPlayback(); _backgroundOriginalResume = null;
        if (IsPeakDelta && IsBackgroundSolo)
        {
            bool syncing = _syncing; _syncing = true;
            try { BackgroundSoloButton.IsChecked = false; }
            finally { _syncing = syncing; }
        }
        SyncPeakDeltaControls();
        _changingPeakDelta = true;
        try { PublishPlaybackTracks(); }
        finally { _changingPeakDelta = false; }
        if (requested || paused)
        {
            var state = PlaybackTrackAt(_playbackTracks, position);
            if (state?.IsReady(OriginalRadio.IsChecked == true) == true)
            {
                RefreshAuditionTransport(position, paused);
                _playbackRequested = _player.IsPlaying;
            }
            else
            {
                if (requested)
                {
                    if (IsPeakDelta || OriginalRadio.IsChecked != true) ArmInterruptedPlayback(position, true, false, _revision);
                    else ArmBackgroundOriginalResume(position, false);
                }
                _player.Stop(); Timeline.Playhead = position; UpdateTime(); UpdatePlayButton();
            }
        }
    }

    // A new Delta recipe changes monitoring only. Other tracks completing must
    // not restart the currently audible recipe, and a pending Result must never
    // fall back to Original while the ear switch remains active.
    private bool HandlePeakDeltaPublication(bool playing, bool paused, double position,
        IReadOnlyList<PlaybackTrackState> updated)
    {
        if (_changingPeakDelta) return true;
        var pinned = _player.CurrentTrack;
        if (!IsPeakDelta && pinned?.DeltaRequested != true) return false;
        if (!playing && !paused)
        {
            TryResumeInterruptedPlayback(_revision);
            return true;
        }
        var audible = PlaybackTrackAt(updated, position);
        if (audible?.IsReady(OriginalRadio.IsChecked == true) != true)
        {
            if (playing) ArmInterruptedPlayback(position, true, false, _revision);
            _player.Stop(); Timeline.Playhead = position; UpdateTime(); UpdatePlayButton();
            return true;
        }
        var after = pinned == null ? audible : updated.FirstOrDefault(track => track.Id == pinned.Id);
        if (pinned?.DeltaRequested != after?.DeltaRequested || pinned?.PeakDelta != after?.PeakDelta)
            RefreshAuditionTransport(position, paused);
        return true;
    }
}

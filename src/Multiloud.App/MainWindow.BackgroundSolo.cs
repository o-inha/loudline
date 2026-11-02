using System.Windows;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private readonly Guid _backgroundSoloId = Guid.NewGuid();
    private BackgroundLayer? _soloSourceLayer, _soloPlaybackLayer;
    private int _soloSourceSignature;
    private bool _changingBackgroundSolo;
    private bool IsBackgroundSolo => BackgroundSoloButton?.IsChecked == true;

    private PlaybackTrackState[] CaptureBackgroundSoloTracks()
    {
        // Source readiness can change while the immutable placement record stays
        // the same during import. No PCM is copied or processed on this path.
        var signature = new HashCode();
        foreach (var clip in _backgroundLayer.Clips)
        { signature.Add(clip.Source.Original); signature.Add(clip.Source.Duration); }
        int sources = signature.ToHashCode();
        if (!ReferenceEquals(_soloSourceLayer, _backgroundLayer) || sources != _soloSourceSignature)
        {
            _soloSourceLayer = _backgroundLayer; _soloSourceSignature = sources;
            _soloPlaybackLayer = _backgroundLayer with
            {
                Enabled = true,
                // Unloaded sources remain silent until their immutable PCM is
                // published. Ready background can be auditioned immediately.
                Clips = _backgroundLayer.Clips.Where(clip => clip.Source.Original.Length > 0).ToArray()
            };
        }
        double duration = _tracks.Count > 0 ? _tracks.Sum(track => track.Duration) :
            Math.Max(0, _backgroundLayer.Clips.Select(clip => clip.EndSeconds).DefaultIfEmpty(0).Max());
        duration = Math.Round(duration * 48000) / 48000.0;
        return duration <= 0 || _soloPlaybackLayer == null ? [] :
            [new PlaybackTrackState(_backgroundSoloId, 48000, 2, duration, null, null,
                SoloBackground: _soloPlaybackLayer)];
    }

    private void BackgroundSoloChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncing || _closed) return;
        bool playing = _player.IsPlaying, paused = _player.IsPaused;
        if (IsBackgroundSolo && IsPeakDelta)
        {
            bool syncing = _syncing; _syncing = true;
            try { DeltaCheck.IsChecked = false; }
            finally { _syncing = syncing; }
            SyncPeakDeltaControls();
        }
        double position = playing || paused ? _player.PositionSeconds : Timeline.Playhead;
        // A solo change replaces monitoring only. It never pushes undo, marks
        // dirty, changes render keys, or requests/cancels analysis and rendering.
        CancelInterruptedPlayback();
        _backgroundOriginalResume = null;
        if (!IsBackgroundSolo) { _soloSourceLayer = null; _soloPlaybackLayer = null; _soloSourceSignature = 0; }
        _changingBackgroundSolo = true;
        try { PublishPlaybackTracks(); }
        finally { _changingBackgroundSolo = false; }
        if (playing || paused)
        {
            var states = CapturePlaybackTracks();
            if (PlaybackTrackAt(states, position)?.IsReady(OriginalRadio.IsChecked == true) == true)
            {
                RefreshAuditionTransport(position, paused);
                _playbackRequested = _player.IsPlaying;
            }
            else
            {
                _player.Stop(); Timeline.Playhead = position;
                if (!IsBackgroundSolo)
                {
                    if (OriginalRadio.IsChecked == true) ArmBackgroundOriginalResume(position, paused);
                    else if (playing) ArmInterruptedPlayback(position, true, false, _revision);
                }
                UpdateTime(); UpdatePlayButton();
            }
        }
    }

    // Called after publishing the immutable states. Music render publications
    // cannot restart Solo; only a bed edit/readiness change refreshes its reader.
    private bool HandleBackgroundSoloPublication(bool playing, bool paused, double position,
        IReadOnlyList<PlaybackTrackState> updated)
    {
        if (_changingBackgroundSolo) return true;
        if (!IsBackgroundSolo) return false;
        if (playing || paused)
        {
            var pinned = _player.CurrentTrack;
            var next = updated.FirstOrDefault();
            if (next == null) { _player.Stop(); Timeline.Playhead = position; UpdatePlayButton(); }
            else if (!ReferenceEquals(pinned?.SoloBackground, next.SoloBackground) || pinned?.Duration != next.Duration)
                RefreshAuditionTransport(Math.Min(position, next.Duration), paused);
        }
        return true;
    }
}

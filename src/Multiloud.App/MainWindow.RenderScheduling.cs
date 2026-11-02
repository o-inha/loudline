using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private sealed record InterruptedPlayback(Guid TrackId, Guid SourceId, double LocalSeconds, int Revision);
    private InterruptedPlayback? _interruptedPlayback;
    // Separate from the native device state: a pending boundary callback may be
    // dispatched after a user has already paused the device.
    private bool _playbackRequested;
    private bool HasInterruptedPlayback => _interruptedPlayback != null;

    /// <summary>Changes work order only; album/list/source order stays untouched.</summary>
    private static AudioTrack[] RenderOrder(IReadOnlyList<AudioTrack> tracks, double position)
    {
        if (tracks.Count == 0) return [];
        position = double.IsNaN(position) || position < 0 ? 0 : position;
        int first = tracks.Count - 1;
        double offset = 0;
        for (int index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            double duration = double.IsFinite(track.Duration) ? Math.Max(0, track.Duration) : 0;
            if ((duration == 0 && track.Original.Length == 0 && position >= offset) ||
                (position >= offset && position < offset + duration))
            { first = index; break; }
            offset += duration;
        }
        var ordered = new AudioTrack[tracks.Count];
        for (int index = 0; index < ordered.Length; index++) ordered[index] = tracks[(first + index) % tracks.Count];
        return ordered;
    }

    private void UpdatePlayButton() => PlayButton.Content = _player.IsPlaying || HasInterruptedPlayback || HasBackgroundOriginalResume || HasImportPlaybackIntent ? "Ⅱ" : "▶";

    private void CancelInterruptedPlayback()
    {
        _interruptedPlayback = null;
        _playbackRequested = false;
        ++_importNavigationVersion;
        UpdatePlayButton();
    }

    // Called before stopping an invalidated stream, and by readiness-boundary
    // handling. Explicit inputs also permit tests without opening an audio device.
    private bool ArmInterruptedPlayback(double position, bool wasPlaying, bool original, int revision)
    {
        if (!wasPlaying || original && !IsPeakDelta || revision != _revision || _closed || _exporting || !double.IsFinite(position)) return false;
        var state = PlaybackTrackAt(CapturePlaybackTracks(), position);
        var track = state == null ? null : _tracks.FirstOrDefault(item => item.Id == state.Id);
        if (track == null || track.Original.Length == 0 ||
            track.WorkState is TrackWorkState.Missing or TrackWorkState.Failed) return false;
        double offset = _tracks.TakeWhile(item => item.Id != track.Id).Sum(item => PlaybackDuration(item));
        _interruptedPlayback = new(track.Id, track.SourceId, Math.Clamp(position - offset, 0, PlaybackDuration(track)), revision);
        _playbackRequested = true;
        UpdatePlayButton();
        return true;
    }

    private void RebaseInterruptedPlayback(int revision)
    {
        if (_interruptedPlayback is not { } pending) return;
        if (_tracks.FirstOrDefault(track => track.Id == pending.TrackId && track.SourceId == pending.SourceId) is not { } track)
        { CancelInterruptedPlayback(); return; }
        _interruptedPlayback = pending with { Revision = revision };
        Timeline.Playhead = _tracks.TakeWhile(item => item.Id != track.Id).Sum(item => PlaybackDuration(item)) + Math.Min(pending.LocalSeconds, PlaybackDuration(track));
    }

    private bool TryResumeInterruptedPlayback(int revision, Action<double>? resume = null)
    {
        if (_interruptedPlayback is not { } pending || revision != _revision || pending.Revision != revision) return false;
        var track = _tracks.FirstOrDefault(item => item.Id == pending.TrackId && item.SourceId == pending.SourceId);
        if (!_playbackRequested || _closed || _exporting || OriginalRadio.IsChecked == true && !IsPeakDelta || track == null ||
            track.Original.Length == 0 || track.WorkState is TrackWorkState.Missing or TrackWorkState.Failed)
        { CancelInterruptedPlayback(); return false; }
        if (track.WorkState != TrackWorkState.Idle || !PreviewCurrent(track, _settings)) return false;
        double position = _tracks.TakeWhile(item => item.Id != track.Id).Sum(item => PlaybackDuration(item)) + Math.Min(pending.LocalSeconds, PlaybackDuration(track));
        // A newly inserted source can have an unknown duration before this
        // track. Keep the source-local intent until its absolute position can be
        // resolved to the intended track, rather than consuming it at that gap.
        var ready = PlaybackTrackAt(CapturePlaybackTracks(), position);
        if (ready?.Id != track.Id || !ready.IsReady(OriginalRadio.IsChecked == true)) return false;
        // Consume first. A failing/absent device cannot cause a retry loop on
        // subsequent progress publications; normal device recovery owns that case.
        _interruptedPlayback = null;
        Timeline.Playhead = position; UpdateTime();
        if (resume != null) resume(position);
        else { _player.RememberLoopContinuation(position); StartPlayback(position); }
        UpdatePlayButton();
        return true;
    }
}

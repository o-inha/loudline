using Multiloud.Core;
using Multiloud.App.Services;

namespace Multiloud.App;

public partial class MainWindow
{
    private sealed record BackgroundOriginalResume(Guid TrackId, Guid SourceId, double LocalSeconds,
        long NavigationVersion, bool Paused);
    private BackgroundOriginalResume? _backgroundOriginalResume;
    private bool HasBackgroundOriginalResume => _backgroundOriginalResume is { Paused: false } pending &&
        pending.NavigationVersion == _importNavigationVersion && OriginalRadio.IsChecked == true && !IsPeakDelta;

    private bool BackgroundPlaybackReady(AudioTrack track, float[]? background)
        => track.Original.Length > 0 && track.OriginalFrameCount > 0 && BackgroundBlockReason(track) == null;

    private bool BackgroundExportReady(AudioTrack track)
    {
        if (track.Render?.LiveSource != null)
            return PreviewCurrent(track, _settings) && BackgroundBlockReason(track) == null;
        if (!BackgroundRequired(track)) return true;
        var background = CurrentBackgroundSamples(track);
        return BackgroundBlockReason(track) == null && track.Render is { IncludesBackground: true } render &&
            background != null && ReferenceEquals(render.BackgroundSamples, background) &&
            render.Samples.Length == (long)BackgroundOutputRange(track).Frames * 2 &&
            render.Samples.Length == background.Length;
    }

    private bool ArmBackgroundOriginalResume(double position, bool paused)
    {
        if (_closed || _exporting || OriginalRadio.IsChecked != true || IsPeakDelta || !double.IsFinite(position)) return false;
        var states = CapturePlaybackTracks();
        var state = PlaybackTrackAt(states, position);
        var track = state == null ? null : _tracks.FirstOrDefault(item => item.Id == state.Id);
        if (track == null || track.Original.Length == 0 || track.WorkState == TrackWorkState.Missing) return false;
        double offset = states.TakeWhile(item => item.Id != track.Id).Sum(item => item.Duration);
        _backgroundOriginalResume = new(track.Id, track.SourceId, Math.Clamp(position - offset, 0, PlaybackDuration(track)),
            _importNavigationVersion, paused);
        _playbackRequested = !paused;
        return true;
    }

    private bool TryResumeBackgroundOriginal(Action<double>? resume = null)
    {
        if (_backgroundOriginalResume is not { } pending) return false;
        var track = _tracks.FirstOrDefault(item => item.Id == pending.TrackId && item.SourceId == pending.SourceId);
        if (pending.NavigationVersion != _importNavigationVersion || _closed || _exporting ||
            OriginalRadio.IsChecked != true || IsPeakDelta || track == null || track.Original.Length == 0 || track.WorkState == TrackWorkState.Missing)
        { _backgroundOriginalResume = null; return false; }
        var states = CapturePlaybackTracks();
        double position = states.TakeWhile(item => item.Id != track.Id).Sum(item => item.Duration) +
            Math.Min(pending.LocalSeconds, PlaybackDuration(track));
        var state = PlaybackTrackAt(states, position);
        // Unknown durations inserted before the source must not consume its
        // source-local intent, and an active bed must never fall back to silence.
        if (state?.Id != track.Id || state.OriginalSamples == null) return false;
        _backgroundOriginalResume = null;
        Timeline.Playhead = position; UpdateTime();
        if (resume != null) resume(position);
        else
        {
            ResumeBackgroundOriginalAt(position, pending.Paused);
        }
        UpdatePlayButton();
        return true;
    }

    private void ResumeBackgroundOriginalAt(double position, bool paused, Action<double>? start = null)
    {
        _player.RememberLoopContinuation(position);
        (start ?? StartPlayback)(position);
        if (!paused) return;
        if (_player.IsPlaying) _player.Pause();
        _playbackRequested = false;
    }

    private void HandleBackgroundOriginalPublication(PlaybackTrackState? pinned, PlaybackTrackState? after,
        double position, bool paused, bool monitorGainChanged)
    {
        bool sourceChanged = pinned != null && (after == null ||
            !ReferenceEquals(pinned.OriginalSamples, after.OriginalSamples) ||
            pinned.LiveOriginal != after.LiveOriginal ||
            pinned.LiveOriginal == null && !ReferenceEquals(pinned.BackgroundSamples, after.BackgroundSamples) ||
            pinned.BackgroundRequired != after.BackgroundRequired);
        if (!sourceChanged && !monitorGainChanged) return;
        var audible = PlaybackTrackAt(CapturePlaybackTracks(), position);
        if (audible?.OriginalSamples == null)
        {
            ArmBackgroundOriginalResume(position, paused);
            _player.Stop(); Timeline.Playhead = position; UpdateTime(); UpdatePlayButton();
            return;
        }
        _backgroundOriginalResume = null;
        RefreshAuditionTransport(position, paused);
    }

    private bool HandleBackgroundOriginalWaiting(Guid id, double position, Action<double>? resume = null)
    {
        if (!_playbackRequested || OriginalRadio.IsChecked != true || IsPeakDelta ||
            PlaybackTrackAt(CapturePlaybackTracks(), position)?.Id != id || !ArmBackgroundOriginalResume(position, false)) return false;
        if (TryResumeBackgroundOriginal(resume)) return true;
        Timeline.Playhead = position; UpdateTime(); UpdatePlayButton();
        StatusText.Text = "Waiting for background audio";
        return false;
    }
}

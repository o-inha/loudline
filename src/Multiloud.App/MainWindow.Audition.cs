using System.Windows;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private (double Original, double Result) AuditionGains(AudioTrack track)
    {
        if (IsPeakDelta || AuditionMatchCheck.IsChecked != true || track.WorkState == TrackWorkState.Missing ||
            track.Analysis is not { } analysis || track.Render is not { } render ||
            !double.IsFinite(render.OutputLufs) || render.OutputLufs <= -69)
            return (0, 0);
        double originalLufs = analysis.IntegratedLufs;
        if (BackgroundRequired(track))
        {
            // The A side also contains this fixed bed. Reusing music-only LUFS
            // would create an incorrect monitor offset; never scan PCM on the UI.
            if (!render.IncludesBackground || !ReferenceEquals(render.BackgroundSamples, CurrentBackgroundSamples(track)) ||
                render.OriginalMixLufs is not { } mixedLufs || !double.IsFinite(mixedLufs) || mixedLufs <= -69)
                return (0, 0);
            originalLufs = mixedLufs;
        }
        else if (render.IncludesBackground) return (0, 0); // A removed/disabled bed's old Result is not an A/B reference.
        if (!double.IsFinite(originalLufs) || originalLufs <= -69) return (0, 0);
        var reference = Math.Min(originalLufs, render.OutputLufs);
        return (Math.Clamp(reference - originalLufs, -120, 0), Math.Clamp(reference - render.OutputLufs, -120, 0));
    }

    private PlaybackTrackState PlaybackStateFor(AudioTrack track)
    {
        var gains = AuditionGains(track);
        bool required = BackgroundRequired(track);
        var background = required ? CurrentBackgroundSamples(track) : null;
        var original = OriginalPlaybackSamples(track);
        bool backgroundReady = BackgroundPlaybackReady(track, background);
        var liveOriginal = original is { Length: > 0 } && backgroundReady ? LiveSourceFor(track) : null;
        bool resultReady = track.WorkState == TrackWorkState.Idle && SourceAnalysisSatisfied(track, _settings) &&
            PreviewCurrent(track, _settings) && BackgroundExportReady(track);
        return new PlaybackTrackState(track.Id, PlaybackSampleRate(track), PlaybackChannels(track), PlaybackDuration(track),
            original is { Length: > 0 } && backgroundReady ? original : null,
            resultReady ? track.Render!.Samples : null,
            gains.Original, gains.Result, background, required,
            LiveOriginal: liveOriginal, LiveResult: resultReady ? track.Render?.LiveSource : null,
            PeakDelta: IsPeakDelta && resultReady ? PeakDeltaSource.ForTrack(track) : null, DeltaRequested: IsPeakDelta);
    }

    private void AuditionMatchChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncing) return;
        // Publishing refreshes the current provider when its effective monitor
        // gain changes, including when a new Result is published during Original.
        PublishPlaybackTracks();
    }

    private void RefreshAuditionTransport(double position, bool paused)
    {
        try
        {
            // Replace only the monitoring provider at an audio callback boundary.
            // The same device and playing/paused state remain active.
            if (!_player.Seek(position, OriginalRadio.IsChecked == true))
            {
                _player.Stop(); Timeline.Playhead = position;
                StartPlayback(position);
                if (paused) { _player.Pause(); _playbackRequested = false; }
            }
            Timeline.Playhead = position; UpdateTime();
        }
        catch (Exception ex) { PlaybackFailed(ex); }
    }
}

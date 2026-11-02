using System.IO;
using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyImmediateSlicingSmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; double playhead = Timeline.Playhead; var originalView = Timeline.ViewMode;
        bool? originalRadio = OriginalRadio.IsChecked, comparisonRadio = CompareRadio.IsChecked, solo = BackgroundSoloButton.IsChecked;
        Task? pending = null; TaskCompletionSource? copyGate = null;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Immediate slice smoke: " + message); }
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            BackgroundSoloButton.IsChecked = false; PreviewRadio.IsChecked = true;
            foreach (var item in _tracks) RememberActivePreview(item);
            var off = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            var source = await Task.Run(() =>
            {
                const int rate = 8000, frames = rate * 3600;
                var samples = new float[frames];
                for (int first = 0; first < frames; first += 80)
                    for (int j = 0; j < 80 && first + j < frames; j++) samples[first + j] = j < 40 ? .1f : -.1f;
                var wave = WaveformPyramid.Build(samples, 1);
                return new AudioTrack { FilePath = Path.Combine(Path.GetTempPath(), "One-hour slice source.wav"),
                    Original = samples, SampleRate = rate, Channels = 1, OriginalWaveform = wave,
                    OriginalPeaks = BuildDisplayPeaks(wave), WorkState = TrackWorkState.Idle };
            });
            _tracks.Clear(); _tracks.Add(source); _renderKeys.Clear();
            var bed = new AudioTrack { FilePath = "One-hour background.wav", Original = source.Original,
                SampleRate = source.SampleRate, Channels = source.Channels, OriginalWaveform = source.OriginalWaveform,
                OriginalPeaks = source.OriginalPeaks, WorkState = TrackWorkState.Idle };
            _backgroundLayer = new BackgroundLayer { Clips = [new BackgroundClip { Source = bed }] };
            _settings = off; RestoreProjectEnvelope([new(0, 0), new(3600, 0)], [new(0, 0), new(3600, 0)]);
            SyncControls(); RefreshList(); QueueRender();
            int executions = _renderExecutionCount;
            const double cut = 1733.375;
            copyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = SliceTrackAsync(source, cut, copyGate.Task);
            Require(!pending.IsCompleted && _tracks.Count == 2 &&
                _tracks.All(item => ReferenceEquals(item.Original, source.Original)) &&
                _tracks[0].OriginalFrameOffset == 0 && _tracks[1].OriginalFrameOffset == (int)(cut * 8000),
                "The visible split copied PCM, waited for source preparation, or lost its shared source range.");
            var left = _tracks[0]; var right = _tracks[1];
            Require(left.Duration == cut && left.Duration + right.Duration == 3600 &&
                left.OriginalWaveform is { Work.RawFrames: 0 } && right.OriginalWaveform is { Work.RawFrames: 0 } &&
                left.OriginalWaveform.FrameCount == (int)(cut * 8000) &&
                right.OriginalWaveform.Query(0, 80) == source.OriginalWaveform!.Query((int)(cut * 8000), (int)(cut * 8000) + 80),
                "The immediate file boundary discarded waveform data or rebuilt a resolution level.");
            foreach (var mode in new[] { Controls.WaveformViewMode.Original, Controls.WaveformViewMode.Preview, Controls.WaveformViewMode.Compare })
            {
                long draws = Timeline.PerformanceCounters.WaveformRedraws;
                Timeline.ViewMode = mode; Timeline.FitAll(); Timeline.Refresh(); Timeline.FlushPendingRendering();
                Require(Timeline.Tracks.Sum(item => item.Duration) == 3600 && Timeline.Tracks.Count == 2 &&
                    Timeline.PerformanceCounters.WaveformRedraws > draws,
                    "A view lost the immediate source-range split.");
                foreach (var piece in new[] { left, right })
                {
                    var drawnSource = new Controls.WaveformTileSource(piece.OriginalWaveform!, piece.OriginalWaveform,
                        piece.OriginalPeaks, piece.SampleRate, piece.Duration);
                    var firstFrameSignal = drawnSource.Query(0, 1, coarse: true);
                    Require(piece.OriginalPeaks.Count > 0 && firstFrameSignal.Min < 0 && firstFrameSignal.Max > 0 &&
                        Timeline.PerformanceCounters.Tiles.PeakQueryCount > 0,
                        "A visible slice had blank cold tiles while its PCM was still preparing.");
                }
            }
            if (screenshot != null)
                CaptureVisual(Timeline, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-instant-slice.png"));
            copyGate.SetResult(); await pending; pending = null;
            Require(_tracks.Count == 2 && left.Analysis == null && right.Analysis == null &&
                _renderExecutionCount == executions && _tracks.All(item => item.WorkState == TrackWorkState.Idle && PreviewCurrent(item, off)) &&
                ReferenceEquals(left.Render?.Samples, left.Original) && ReferenceEquals(right.Render?.Samples, right.Original),
                "Bypassed slices analyzed or processed unchanged music instead of becoming immediately playable Results.");
            Require(ReferenceEquals(left.Original, source.Original) && ReferenceEquals(right.Original, source.Original) &&
                left.OriginalRegion.Span.SequenceEqual(source.Original.AsSpan(0, left.OriginalSampleCount)) &&
                right.OriginalRegion.Span.SequenceEqual(source.Original.AsSpan(left.OriginalSampleCount)) &&
                left.Render?.LiveSource?.Background == _backgroundLayer && right.Render?.LiveSource?.Background == _backgroundLayer &&
                left.Render.SafetyAttenuationDb == 0 && right.Render.SafetyAttenuationDb == 0,
                "Bypassed slice preparation changed/copied samples, rendered a mix, or lost fixed background timing.");
            Require(PlaybackStateFor(left).IsReady(false) && PlaybackStateFor(right).IsReady(false) &&
                CanExportTrack(left) && CanExportTrack(right) && ExportButton.IsEnabled,
                "Unmeasured unchanged slices were not ready for playback/export.");
            VerifySlicePlaybackIntentSmoke(cut + 1, right);
            await SliceTrackAsync(right, 12.5);
            Require(_tracks.Count == 3 && _tracks.All(item => item.Analysis == null && PreviewCurrent(item, off)) &&
                _renderExecutionCount == executions && _tracks[2].SourceStartSeconds == cut + 12.5,
                "A repeated bypassed cut analyzed audio or lost its absolute source range.");

            // The normal independently analyzed/matched path must start as soon
            // as modules are enabled again, without a manual Resume action.
            var small = await Task.Run(() =>
            {
                var samples = Enumerable.Range(0, 16000).Select(i => (float)(Math.Sin(2 * Math.PI * 440 * i / 8000) * .05)).ToArray();
                var wave = WaveformPyramid.Build(samples, 1);
                return new AudioTrack { FilePath = "Deferred slice.wav", Original = samples, SampleRate = 8000, Channels = 1,
                    OriginalWaveform = wave, OriginalPeaks = BuildDisplayPeaks(wave), WorkState = TrackWorkState.Idle };
            });
            _tracks.Clear(); _tracks.Add(small); _renderKeys.Clear(); _backgroundLayer = new(); RestoreProjectEnvelope([], []);
            RefreshList(); QueueRender(); await SliceTrackAsync(small, .75);
            Require(_tracks.All(item => item.Analysis == null), "A bypassed short slice performed unnecessary analysis.");
            _settings = off with { MatchingEnabled = true }; SyncControls(); QueueRender();
            await WaitForSmokePreviewAsync();
            Require(_tracks.All(item => item.Analysis != null && PreviewCurrent(item, _settings)) &&
                _renderExecutionCount > executions,
                "Enabling a module did not automatically analyze and process deferred slices.");
        }
        finally
        {
            _importCts?.Cancel(); copyGate?.TrySetResult();
            if (pending != null) await pending;
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var item in keys) _renderKeys[item.Key] = item.Value;
            _previewCache.Clear(); Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; Timeline.Playhead = playhead;
            OriginalRadio.IsChecked = originalRadio; CompareRadio.IsChecked = comparisonRadio;
            PreviewRadio.IsChecked = originalRadio != true && comparisonRadio != true;
            BackgroundSoloButton.IsChecked = solo; Timeline.ViewMode = originalView;
        }
    }

    private void VerifySlicePlaybackIntentSmoke(double position, AudioTrack expected)
    {
        CancelInterruptedPlayback();
        var intent = new SlicePlaybackIntent(position, true, false, false, _importNavigationVersion);
        if (!RearmSlicePlaybackAfterPreparation(intent) || _interruptedPlayback is not { } resumed ||
            resumed.TrackId != expected.Id || resumed.SourceId != expected.SourceId || Math.Abs(resumed.LocalSeconds - 1) > .000001)
            throw new InvalidOperationException("Immediate slice smoke: ready PCM did not re-arm the playing Result at its exact new source-local position.");
        CancelInterruptedPlayback(); // A manual Stop/seek changes the navigation version.
        if (RearmSlicePlaybackAfterPreparation(intent) || _interruptedPlayback != null || _playbackRequested)
            throw new InvalidOperationException("Immediate slice smoke: delayed PCM preparation overrode a later manual Stop/seek.");
        OriginalRadio.IsChecked = true;
        var paused = new SlicePlaybackIntent(position, false, true, true, _importNavigationVersion);
        if (!RearmSlicePlaybackAfterPreparation(paused) || _backgroundOriginalResume is not { Paused: true } original ||
            original.TrackId != expected.Id || Math.Abs(original.LocalSeconds - 1) > .000001 || _playbackRequested)
            throw new InvalidOperationException("Immediate slice smoke: paused Original lost its source-local position during PCM preparation.");
        CancelInterruptedPlayback(); _backgroundOriginalResume = null; PreviewRadio.IsChecked = true;
    }
}

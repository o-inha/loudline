using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyLoopTimelineSmokeAsync()
    {
        const int rate = 1024;
        var samples = Enumerable.Range(0, rate * 20 * 2).Select(index =>
            (float)(.2 * Math.Sin(index / 2.0 * .08))).ToArray();
        var pyramid = WaveformPyramid.Build(samples, 2);
        var track = new AudioTrack
        {
            FilePath = "Loop timeline.wav", Original = samples, SampleRate = rate, Channels = 2,
            OriginalWaveform = pyramid, OriginalPeaks = [new(-.2f, .2f)], PreviewPeaks = [new(-.2f, .2f)],
            Analysis = new AnalysisResult { IntegratedLufs = -18 },
            Render = new RenderResult { Samples = samples, Waveform = pyramid, OutputLufs = -18 }
        };
        var timeline = new TimelineView
        {
            Tracks = new List<AudioTrack> { track }, ShowEnvelopeOnWaveform = false, ShowGainLines = false,
            Settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false }
        };
        var size = new Size(960, 600);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
        }
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Loop timeline smoke: " + message); }
        Point At(double seconds) => new(timeline.PlotArea.Left + timeline.PlotArea.Width * seconds / 20,
            timeline.LoopRulerBand.Top + timeline.LoopRulerBand.Height * .5);
        void Range(LoopRange? range, double first, double last)
            => Require(range != null && Math.Abs(range.StartSeconds - first) < 1e-8 && Math.Abs(range.EndSeconds - last) < 1e-8,
                "A painted or resized loop lost its timeline positions.");
        Layout(); timeline.FitAll(); Layout();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (timeline.HasPendingTiles && DateTime.UtcNow < deadline) await Task.Delay(5);
        Require(!timeline.HasPendingTiles, "The fixture did not settle waveform tiles.");
        timeline.FlushPendingRendering();
        var before = timeline.PerformanceCounters;
        int starts = 0, commits = 0, seeks = 0, scrubStarts = 0, scrubEnds = 0;
        timeline.LoopEditStarted += () => starts++;
        timeline.LoopEditCommitted += () => commits++;
        timeline.SeekRequested += _ => seeks++;
        timeline.ScrubStarted += _ => scrubStarts++;
        timeline.ScrubCompleted += _ => scrubEnds++;

        timeline.LoopState = new PlaybackLoopState(AEnabled: true);
        Require(timeline.HandleNavigationPointerDown(At(2), 1), "The enabled A loop did not own a ruler drag.");
        timeline.HandleNavigationPointerMove(At(6)); timeline.CompletePointerInteraction();
        Range(timeline.LoopState.A, 2, 6);
        Require(starts == 1 && commits == 1 && seeks == 0, "Painting A sought audio or created multiple edits.");

        timeline.LoopState = timeline.LoopState with { AEnabled = false, BEnabled = true };
        timeline.HandleNavigationPointerDown(At(17), 1);
        timeline.HandleNavigationPointerMove(At(13)); timeline.CompletePointerInteraction();
        Range(timeline.LoopState.B, 13, 17); Range(timeline.LoopState.A, 2, 6);
        Require(starts == 2 && commits == 2 && seeks == 0, "Painting B changed A or sought instead of drawing.");

        timeline.LoopState = timeline.LoopState with { AEnabled = true };
        var both = timeline.LoopState;
        timeline.HandleNavigationPointerDown(At(9), 1);
        timeline.HandleNavigationPointerMove(At(11)); timeline.CompletePointerInteraction();
        Require(timeline.LoopState == both && Math.Abs(timeline.Playhead - 11) < 1e-8 && starts == 2 && scrubStarts == 1 && scrubEnds == 1,
            "Both enabled loops allowed an ambiguous new range or blocked ordinary ruler seeking.");

        timeline.HandleNavigationPointerDown(timeline.LoopHandlePosition(false, false), 1);
        timeline.HandleNavigationPointerMove(At(3.25)); timeline.CompletePointerInteraction();
        timeline.HandleNavigationPointerDown(timeline.LoopHandlePosition(true, true), 1);
        timeline.HandleNavigationPointerMove(At(18.5)); timeline.CompletePointerInteraction();
        Range(timeline.LoopState.A, 3.25, 6); Range(timeline.LoopState.B, 13, 18.5);
        Require(starts == 4 && commits == 4, "Handles did not resize both loops while both buttons were enabled.");
        var saved = timeline.LoopState;
        timeline.HandleNavigationPointerDown(timeline.LoopHandlePosition(false, true), 1);
        timeline.CompletePointerInteraction();
        Require(timeline.LoopState == saved && starts == 4 && commits == 4, "A handle click without motion created an edit.");

        timeline.LoopState = timeline.LoopState with { BEnabled = false };
        timeline.HandleNavigationPointerDown(At(8), 1);
        timeline.HandleNavigationPointerMove(At(10));
        timeline.CompleteLoopInteraction(cancel: true, allowSeek: false);
        Range(timeline.LoopState.A, 3.25, 6);
        timeline.FlushPendingRendering();
        var after = timeline.PerformanceCounters;
        Require(before.WaveformRedraws == after.WaveformRedraws && before.OverviewRedraws == after.OverviewRedraws &&
            before.MetricsPasses == after.MetricsPasses &&
            before.Tiles.PeakQueryCount == after.Tiles.PeakQueryCount && before.Tiles.TileBuildCount == after.Tiles.TileBuildCount,
            "Audition loop gestures rebuilt waveform tiles or audio metrics.");

        uint[] Capture()
        {
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(timeline); return ComparisonPixels(image);
        }
        timeline.LoopState = saved with { AEnabled = false, BEnabled = false }; timeline.FlushPendingRendering();
        var plain = Capture();
        timeline.LoopState = saved with { AEnabled = true, BEnabled = false }; timeline.FlushPendingRendering();
        var colored = Capture();
        var bandPoint = At(4.5);
        int bandIndex = (int)(timeline.LoopRulerBand.Top + 1) * (int)size.Width + (int)bandPoint.X;
        Require(colored[bandIndex] != plain[bandIndex], "Enabling A did not reveal its colored ruler strip.");
        int tinted = (int)(timeline.SignalPlotArea.Top + 30) * (int)size.Width + (int)bandPoint.X;
        Require(colored[tinted] != plain[tinted], "The loop did not faintly tint its track region.");
        timeline.LoopState = saved with { AEnabled = false, BEnabled = false }; timeline.FlushPendingRendering();
        Require(plain.SequenceEqual(Capture()) && timeline.LoopState.A == saved.A && timeline.LoopState.B == saved.B,
            "Hiding loops left paint behind or discarded their remembered locations.");

        // The hover cursor is a retained grey line, without the playhead's
        // triangle. Dragging retains ordinary scrubbing and hides that line.
        timeline.SetHoveredTrack(new Point(-10, -10), force: true);
        var pointer = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width * .423, timeline.SignalCenterY + 12);
        timeline.SetCrosshairPointer(null); Layout(); plain = Capture();
        before = timeline.PerformanceCounters;
        timeline.SetCrosshairPointer(pointer); timeline.FlushPendingRendering(); colored = Capture();
        Require(timeline.HoverCursorLinePosition == pointer &&
            Enumerable.Range((int)timeline.SignalPlotArea.Top + 20, 50).Count(y =>
                Enumerable.Range((int)pointer.X - 1, 3).Any(x => plain[y * (int)size.Width + x] != colored[y * (int)size.Width + x])) >= 45,
            "Hovering did not draw a grey vertical line across the main track.");
        timeline.HandleWaveformPointerDown(track, pointer, 1);
        Require(timeline.HoverCursorLinePosition == null && scrubStarts == 2, "The hover cursor remained during ordinary playhead scrubbing.");
        timeline.CompletePointerInteraction();
        Require(timeline.HoverCursorLinePosition == pointer && scrubEnds == 2, "Releasing the playhead did not restore the hover cursor.");
        after = timeline.PerformanceCounters;
        Require(before.WaveformRedraws == after.WaveformRedraws && before.Tiles.TileBuildCount == after.Tiles.TileBuildCount,
            "The grey hover cursor rebuilt waveform tiles.");

        await WaitForSmokePreviewAsync();
        VerifyWaitingLoopPlaybackSmoke();
        var originalLoops = CaptureLoops(); var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; int revision = _revision, executions = _renderExecutionCount;
        var renders = _tracks.Select(item => item.Render).ToArray(); var keys = _renderKeys.ToArray();
        try
        {
            LoadLoops(new PlaybackLoopState(A: new LoopRange(1, 2), B: new LoopRange(3, 4)));
            _undo.Clear(); _redo.Clear();
            LoopACheck.IsChecked = true;
            Require(_undo.Count == 1 && CaptureLoops().AEnabled && _player.Loops == CaptureLoops(),
                "The A button did not publish one monitor-only edit.");
            var authored = CaptureLoops();
            UndoClick(this, new RoutedEventArgs());
            Require(!CaptureLoops().AEnabled && CaptureLoops().A == authored.A && _revision == revision && _renderExecutionCount == executions,
                "Loop Undo rebuilt audio or lost the stored range.");
            RedoClick(this, new RoutedEventArgs());
            Require(CaptureLoops() == authored && _revision == revision && _renderExecutionCount == executions,
                "Loop Redo did not restore the monitor state without processing.");
            var project = CreateProjectData(Path.Combine(Path.GetTempPath(), "Loop-roundtrip.loudline"));
            var restored = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project));
            Require(restored.Loops == authored, "Saved projects did not retain both loop ranges and enable states.");
            Require(_renderKeys.SequenceEqual(keys) && _tracks.Select((item, index) => ReferenceEquals(item.Render, renders[index])).All(equal => equal),
                "Loop buttons changed the rendered/exported audio.");
        }
        finally
        {
            LoadLoops(originalLoops);
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty;
        }
    }
    private void VerifyWaitingLoopPlaybackSmoke()
    {
        if (_tracks.Count < 2) return;
        var loops = CaptureLoops(); var interrupted = _interruptedPlayback; var background = _backgroundOriginalResume;
        var import = _originalImportResume; bool requested = _playbackRequested, dirty = _dirty, syncing = _syncing;
        long navigation = _importNavigationVersion; double playhead = Timeline.Playhead; string status = StatusText.Text;
        bool original = OriginalRadio.IsChecked == true, comparison = CompareRadio.IsChecked == true;
        var second = _tracks[1]; var work = second.WorkState;
        int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Loop continuation smoke: " + message); }
        try
        {
            PreviewRadio.IsChecked = true;
            CancelInterruptedPlayback(); _backgroundOriginalResume = null; _originalImportResume = null;
            double a = Math.Min(.125, _tracks[0].Duration / 8);
            double b = PlaybackDuration(_tracks[0]) + Math.Min(.125, second.Duration / 8);
            _syncing = true;
            LoadLoops(new PlaybackLoopState(true, new LoopRange(a, a + .05), true, new LoopRange(b, b + .05)));
            _syncing = false;
            second.WorkState = TrackWorkState.Processing;
            Timeline.Playhead = b;
            Require(ArmInterruptedPlayback(b, true, false, _revision), "The unfinished B fixture did not arm.");
            _loops = _loops with { BEnabled = false }; Timeline.LoopState = _loops; _player.UpdateLoops(_loops);
            var resumed = new List<double>();
            Require(RefreshWaitingLoopPlayback(resumed.Add) && resumed.SequenceEqual(new[] { a }) && !HasInterruptedPlayback &&
                Timeline.Playhead == a && !TryResumeInterruptedPlayback(_revision, resumed.Add),
                "Disabling unfinished B failed to resume ready A or left its old wait active.");
            second.WorkState = work;
            CancelInterruptedPlayback(); OriginalRadio.IsChecked = true;
            Require(ArmBackgroundOriginalResume(b, paused: true), "The paused Original fixture did not arm.");
            int before = resumed.Count;
            Require(!RefreshWaitingLoopPlayback(resumed.Add) && resumed.Count == before && !_playbackRequested && _backgroundOriginalResume?.Paused == true,
                "A loop edit turned paused Original playback into autoplay.");
            _backgroundOriginalResume = null;
            _syncing = true; LoadLoops(new PlaybackLoopState(true, new LoopRange(0, a + .05))); _syncing = false;
            double relocated = PlaybackDuration(_tracks[0]) * .5, actual = -1;
            Require(Math.Abs(_player.ResolvePlayPosition(relocated) - relocated) > .01,
                "The structural Original fixture did not lie outside its loop.");
            ResumeBackgroundOriginalAt(relocated, paused: false, value => actual = _player.ResolvePlayPosition(value));
            Require(Math.Abs(actual - relocated) < 1e-9 && !_player.IsPlaying && !_player.IsPaused,
                "Original structural continuation snapped into A or opened audio hardware.");
            Require(_revision == revision && _renderExecutionCount == executions && _undo.Count == history && _dirty == dirty,
                "Monitor-only loop handoff changed processing or edit history.");
        }
        finally
        {
            second.WorkState = work;
            _syncing = true;
            LoadLoops(loops);
            if (original) OriginalRadio.IsChecked = true;
            else if (comparison) CompareRadio.IsChecked = true;
            else PreviewRadio.IsChecked = true;
            _syncing = syncing;
            _interruptedPlayback = interrupted; _backgroundOriginalResume = background; _originalImportResume = import;
            _playbackRequested = requested; _importNavigationVersion = navigation;
            Timeline.Playhead = playhead; UpdateTime(); UpdatePlayButton(); StatusText.Text = status; _dirty = dirty;
            _playbackTracks = CapturePlaybackTracks(); _player.UpdateTracks(_playbackTracks);
        }
    }

}

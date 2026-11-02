using System.IO;
using System.Windows;
using System.Windows.Threading;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyCustomBoundariesSmokeAsync(string? screenshot)
    {
        var samples = Enumerable.Range(0, 20480).Select(frame => (float)(.1 * Math.Sin(frame * .2))).ToArray();
        var track = new AudioTrack
        {
            FilePath = "Boundary interaction.wav", Original = samples, SampleRate = 1024, Channels = 1,
            OriginalWaveform = WaveformPyramid.Build(samples, 1), OriginalPeaks = AudioProcessor.BuildPeaks(samples, 1),
            Analysis = new AnalysisResult { IntegratedLufs = -20 },
            Render = new RenderResult { Samples = samples, RenderedEnvelope = [], OutputLufs = -20 }
        };
        track.Render.Waveform = track.OriginalWaveform;
        var timeline = new TimelineView { Tracks = new[] { track }, ViewMode = WaveformViewMode.Original };
        var size = new Size(960, 500);
        void Layout() { timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering(); }
        async Task Settle()
        {
            Layout(); var deadline = DateTime.UtcNow.AddSeconds(8);
            while (timeline.HasPendingTiles && DateTime.UtcNow < deadline) { await Task.Delay(5); Layout(); }
            RequireCustomBoundaries(!timeline.HasPendingTiles, "The boundary fixture's waveform tiles did not settle.");
            Layout();
        }
        Point At(double time) => new(timeline.CustomBoundaryBar.Left +
            (time - timeline.ViewStartSeconds) / timeline.VisibleDurationSeconds * timeline.CustomBoundaryBar.Width,
            timeline.CustomBoundaryBar.Top + timeline.CustomBoundaryBar.Height / 2);
        void Click(double time)
        {
            RequireCustomBoundaries(timeline.HandleCustomBoundaryPointerDown(At(time)), "A click in the boundary bar was ignored.");
            timeline.CompleteCustomBoundaryInteraction(); Layout();
        }
        Layout(); timeline.FitAll(); Layout();
        var originalPlot = timeline.PlotArea;
        RequireCustomBoundaries(timeline.CustomBoundaryBar.IsEmpty, "The boundary bar is visible in a new timeline.");
        timeline.CustomBoundariesVisible = true; Layout(); await Settle();
        RequireCustomBoundaries(timeline.CustomBoundaryBar.Height == 24 && timeline.PlotArea.Top == originalPlot.Top + 24 &&
            timeline.CustomBoundaryBar.Left == timeline.PlotArea.Left && timeline.CustomBoundaryBar.Width == timeline.PlotArea.Width &&
            timeline.CustomBoundaryBar.Bottom < timeline.PlotArea.Top,
            "The boundary bar exceeds ruler height, overlaps the ruler, or is misaligned with waveform time.");
        int starts = 0, commits = 0, audioEdits = 0, seeks = 0;
        timeline.CustomBoundaryEditStarted += () => starts++;
        timeline.CustomBoundaryEditCommitted += () => commits++;
        timeline.EditStarted += () => audioEdits++;
        timeline.EditCommitted += _ => audioEdits++;
        timeline.SeekRequested += _ => seeks++;
        var before = timeline.PerformanceCounters;
        Click(5); Click(12);
        RequireCustomBoundaries(timeline.CustomTrackBoundaries.Count == 2 &&
            Math.Abs(timeline.CustomTrackBoundaries[0] - 5) < 1e-10 && Math.Abs(timeline.CustomTrackBoundaries[1] - 12) < 1e-10,
            "A new boundary marker was removed by the same click or placed at the wrong time.");
        var first = At(5);
        timeline.HandleCustomBoundaryPointerDown(first);
        timeline.HandleCustomBoundaryPointerMove(At(14));
        timeline.CompleteCustomBoundaryInteraction(); Layout();
        RequireCustomBoundaries(timeline.CustomTrackBoundaries.Count == 2 &&
            Math.Abs(timeline.CustomTrackBoundaries[0] - 12) < 1e-10 && Math.Abs(timeline.CustomTrackBoundaries[1] - 14) < 1e-10,
            "Dragging a boundary could not move it past another marker while retaining sorted distinct positions.");
        Click(12);
        RequireCustomBoundaries(timeline.CustomTrackBoundaries.Count == 1 && Math.Abs(timeline.CustomTrackBoundaries[0] - 14) < 1e-10 &&
            starts == 4 && commits == 4 && audioEdits == 0 && seeks == 0,
            "Marker click removal/drag generated audio edits, seeks, or duplicate undo transactions.");
        var after = timeline.PerformanceCounters;
        RequireCustomBoundaries(after.MetricsPasses == before.MetricsPasses && after.WaveformRedraws == before.WaveformRedraws &&
            after.OverviewRedraws == before.OverviewRedraws && after.Tiles.PeakQueryCount == before.Tiles.PeakQueryCount,
            "Editing export boundaries rebuilt waveform or processing metadata instead of only its marker layer.");
        // A lost capture should retain an existing marker, rather than emulate
        // a completed remove click. Invisible/out-of-project markers stay saved.
        timeline.HandleCustomBoundaryPointerDown(At(14));
        timeline.CompleteCustomBoundaryInteraction(allowClickRemoval: false);
        timeline.CustomTrackBoundaries.Add(35); timeline.InvalidateCustomBoundaries(); Layout();
        timeline.CustomBoundariesVisible = false; Layout();
        RequireCustomBoundaries(timeline.CustomTrackBoundaries.SequenceEqual(new[] { 14d, 35d }) && timeline.PlotArea == originalPlot,
            "Hiding the bar discarded markers or reserved empty height.");
        timeline.CustomBoundariesVisible = true; Layout();
        timeline.HandleNavigationWheel(new Point(580, 150), 240, false);
        var navigationDeadline = DateTime.UtcNow.AddSeconds(3);
        while (timeline.IsNavigationAnimating && DateTime.UtcNow < navigationDeadline) { await Task.Delay(5); Layout(); }
        RequireCustomBoundaries(!timeline.IsNavigationAnimating, "Boundary navigation did not settle.");
        double visible = timeline.ViewStartSeconds + timeline.VisibleDurationSeconds * .7;
        Click(visible);
        RequireCustomBoundaries(timeline.CustomTrackBoundaries.Any(time => Math.Abs(time - visible) < 1e-9),
            "Zoomed/scrolled marker placement used album pixels instead of the displayed camera.");

        await WaitForSmokePreviewAsync();
        var session = Capture(); var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; bool? shown = CustomBoundariesCheck.IsChecked;
        int revision = _revision, executions = _renderExecutionCount;
        var renderKeys = _renderKeys.ToArray(); var renders = _tracks.Select(item => item.Render).ToArray();
        var settings = _settings;
        bool renderGateHeld = false;
        try
        {
            LoadCustomBoundaries([]); _undo.Clear(); _redo.Clear();
            CustomBoundariesCheck.IsChecked = true;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            double time = _tracks.Sum(item => item.Duration) * .37;
            var marker = new Point(Timeline.CustomBoundaryBar.Left +
                (time - Timeline.ViewStartSeconds) / Timeline.VisibleDurationSeconds * Timeline.CustomBoundaryBar.Width,
                Timeline.CustomBoundaryBar.Top + 12);
            // The integration fixture may currently fit a single source. Fit all
            // first so the authored project position is inside its boundary bar.
            Timeline.FitAll();
            marker.X = Timeline.CustomBoundaryBar.Left + time / Timeline.VisibleDurationSeconds * Timeline.CustomBoundaryBar.Width;
            Timeline.HandleCustomBoundaryPointerDown(marker); Timeline.CompleteCustomBoundaryInteraction();
            RequireCustomBoundaries(_customTrackBoundaries.Count == 1 && _dirty && _undo.Count == 1,
                "The main window did not save an export-only boundary edit in Undo history.");
            UndoClick(this, new RoutedEventArgs());
            RequireCustomBoundaries(_customTrackBoundaries.Count == 0, "Boundary Undo did not restore the previous marker list.");
            RedoClick(this, new RoutedEventArgs());
            RequireCustomBoundaries(_customTrackBoundaries.Count == 1, "Boundary Redo lost the marker edit.");
            CustomBoundariesCheck.IsChecked = false;
            RequireCustomBoundaries(_customTrackBoundaries.Count == 1 && _revision == revision && _renderExecutionCount == executions &&
                ReferenceEquals(_settings, settings) && _renderKeys.OrderBy(pair => pair.Key).SequenceEqual(renderKeys.OrderBy(pair => pair.Key)) &&
                _tracks.Select(item => item.Render).SequenceEqual(renders),
                "Boundary visibility, edits, Undo or Redo changed audio processing or readiness.");
            // An export-only Undo frame may contain an older in-flight Render
            // reference. Once that audio finishes, undoing its later marker edit
            // must retain the newly published audio and processing readiness.
            LoadCustomBoundaries([]); _undo.Clear(); _redo.Clear();
            _settings = _settings with { MatchingEnabled = true, TargetLufs = -13.271 };
            SyncControls(); QueueRender();
            var processingSettings = _settings;
            CustomBoundariesCheck.IsChecked = true; Timeline.FitAll();
            marker = new Point(Timeline.CustomBoundaryBar.Left + Timeline.CustomBoundaryBar.Width * .37,
                Timeline.CustomBoundaryBar.Top + 12);
            Timeline.HandleCustomBoundaryPointerDown(marker); Timeline.CompleteCustomBoundaryInteraction();
            await WaitForSmokePreviewAsync();
            int readyRevision = _revision, readyExecutions = _renderExecutionCount;
            var readyRenders = _tracks.Select(item => item.Render).ToArray();
            UndoClick(this, new RoutedEventArgs());
            RequireCustomBoundaries(_customTrackBoundaries.Count == 0 && _settings == processingSettings && _revision == readyRevision &&
                _renderExecutionCount == readyExecutions && _tracks.Select(item => item.Render).SequenceEqual(readyRenders) &&
                _renderedRevision == _revision,
                "Undoing a marker made during processing restored an old audio snapshot or invalidated the new Result.");
            RedoClick(this, new RoutedEventArgs());
            RequireCustomBoundaries(_customTrackBoundaries.Count == 1 && _revision == readyRevision && _renderExecutionCount == readyExecutions &&
                _tracks.Select(item => item.Render).SequenceEqual(readyRenders),
                "Redoing an in-flight marker edit changed the finished audio.");
            LoadCustomBoundaries([]); _undo.Clear(); _redo.Clear();
            var beforeCancelledSettings = _settings;
            await _renderGate.WaitAsync(); renderGateHeld = true;
            PushUndo();
            _settings = _settings with { TargetLufs = -12.919 };
            SyncControls(); QueueRender();
            Timeline.HandleCustomBoundaryPointerDown(marker); Timeline.CompleteCustomBoundaryInteraction();
            RequireCustomBoundaries(_customTrackBoundaries.Count == 1 && CancelButton.Visibility == Visibility.Visible,
                "The cancellation fixture did not create its independent marker while audio processing was pending.");
            CancelClick(this, new RoutedEventArgs());
            RequireCustomBoundaries(_settings == beforeCancelledSettings && _customTrackBoundaries.Count == 1,
                "Canceling an audio update discarded a later independent export boundary edit.");
            int cancelledRevision = _revision, cancelledExecutions = _renderExecutionCount;
            UndoClick(this, new RoutedEventArgs());
            RequireCustomBoundaries(_customTrackBoundaries.Count == 0 && _settings == beforeCancelledSettings &&
                _revision == cancelledRevision && _renderExecutionCount == cancelledExecutions,
                "Undoing the marker after audio cancellation restored the canceled audio settings or restarted processing.");
            _renderGate.Release(); renderGateHeld = false;
            await WaitForSmokePreviewAsync();
            if (screenshot != null)
            {
                RedoClick(this, new RoutedEventArgs());
                CustomBoundariesCheck.IsChecked = true;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-custom-boundaries.png"));
            }
        }
        finally
        {
            if (renderGateHeld) _renderGate.Release();
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; CustomBoundariesCheck.IsChecked = shown;
        }
    }

    private static void RequireCustomBoundaries(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Custom boundaries: " + message); }
}

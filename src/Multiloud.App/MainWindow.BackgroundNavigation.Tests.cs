using System.Windows;
using System.Windows.Media;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBackgroundNavigationSmokeAsync()
    {
        const int nativeRate = 12000, rate = 48000;
        const double seconds = 4;
        var native = Enumerable.Range(0, (int)(nativeRate * seconds))
            .Select(frame => (float)(.18 * Math.Sin(frame * 2 * Math.PI * 321 / nativeRate) * (frame < nativeRate * 2 ? 1 : .3))).ToArray();
        var music = Enumerable.Range(0, (int)(rate * seconds * 2))
            .Select(sample => native[(sample / 2) / 4]).ToArray();
        var bed = Enumerable.Range(0, music.Length).Select(sample =>
            (float)(.025 * Math.Sin((sample / 2) * 2 * Math.PI * (sample % 2 == 0 ? 79 : 113) / rate))).ToArray();
        var settings = new ProcessingSettings { TargetLufs = -17 };
        var envelope = new List<EnvelopePoint> { new(0, 1), new(1, -1), new(seconds, .5) };
        var render = await Task.Run(() => AudioProcessor.RenderWithBackground(music, rate, 2, envelope, 0, settings, bed));
        render.RenderedSampleRate = rate; render.RenderedChannels = 2;
        render.BackgroundOriginalSamples = music;
        render.Waveform = await Task.Run(() => WaveformPyramid.Build(render.Samples, 2));
        var track = new AudioTrack
        {
            FilePath = "Mixed-format navigation.wav", Original = native, SampleRate = nativeRate, Channels = 1,
            OriginalWaveform = WaveformPyramid.Build(native, 1), OriginalPeaks = AudioProcessor.BuildPeaks(native, 1),
            Render = render, PreviewPeaks = AudioProcessor.BuildPeaks(render.Samples, 2), Envelope = envelope,
            Analysis = new AnalysisResult { IntegratedLufs = -20 }
        };
        var timeline = new TimelineView { Tracks = new List<AudioTrack> { track }, Settings = settings };
        var backgroundSource = new AudioTrack
        {
            Original = bed, SampleRate = rate, Channels = 2,
            OriginalWaveform = WaveformPyramid.Build(bed, 2), OriginalPeaks = AudioProcessor.BuildPeaks(bed, 2)
        };
        using var lane = new BackgroundLaneView
        {
            Timeline = timeline,
            Layer = new BackgroundLayer { Clips = [new BackgroundClip { Source = backgroundSource }] }
        };
        // Keep one ordinary wheel gesture inside a detail bucket so this
        // regression measures camera work, rather than legitimate new detail.
        var size = new Size(880, 420);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
            lane.Measure(new Size(size.Width, 90)); lane.Arrange(new Rect(0, 0, size.Width, 90)); lane.UpdateLayout(); lane.FlushPendingRendering();
        }
        async Task Settle()
        {
            Layout(); var deadline = DateTime.UtcNow.AddSeconds(8);
            while ((timeline.HasPendingTiles || lane.HasPendingTiles) && DateTime.UtcNow < deadline)
            { await Task.Delay(5); Layout(); }
            if (timeline.HasPendingTiles || lane.HasPendingTiles)
                throw new InvalidOperationException("Smoke test: mixed-format navigation tiles did not settle.");
            Layout();
        }
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.ViewMode = mode; timeline.FitAll(); await Settle();
            var before = timeline.PerformanceCounters;
            var laneBefore = lane.TileDiagnostics;
            timeline.HandleNavigationWheel(new Point(size.Width * .5, 240), 120, false);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (timeline.IsNavigationAnimating && DateTime.UtcNow < deadline) { await Task.Delay(5); Layout(); }
            if (timeline.IsNavigationAnimating) throw new InvalidOperationException("Smoke test: mixed-format navigation did not finish.");
            await Settle();
            var after = timeline.PerformanceCounters;
            if (after.MetricsPasses != before.MetricsPasses || after.OverviewRedraws != before.OverviewRedraws ||
                after.Tiles.RasterHitCount <= before.Tiles.RasterHitCount || after.Tiles.InteractiveTileBuildCount != before.Tiles.InteractiveTileBuildCount ||
                after.Tiles.PeakQueryCount != before.Tiles.PeakQueryCount || after.Tiles.TileBuildCount != before.Tiles.TileBuildCount ||
                after.Tiles.RasterBuildCount != before.Tiles.RasterBuildCount || after.Tiles.RetainedBytes > 128L * 1024 * 1024 ||
                lane.TileDiagnostics.PeakQueryCount != laneBefore.PeakQueryCount || lane.TileDiagnostics.RetainedBytes > 32L * 1024 * 1024)
                throw new InvalidOperationException($"Smoke test: warm {mode} camera motion with a common-format Result and background lane left the cached-image path: before={before}, after={after}.");
        }

        // Status publications arrive repeatedly during album processing. Unchanged
        // immutable tile jobs must survive until the next complete frame reconciles
        // their identities; hidden controls still discard their snapshots promptly.
        using var renderer = new WaveformTileRenderer(Dispatcher);
        var original = new WaveformTileSource(track.OriginalWaveform!, track.OriginalWaveform, track.OriginalPeaks, nativeRate, seconds);
        var processed = new WaveformTileSource(render.Waveform!, render.Waveform, track.PreviewPeaks, rate, seconds);
        var snapshot = new WaveformTileTrack(track.Id, original, processed);
        var style = new WaveformTileStyle(Brushes.Aquamarine, Brushes.DeepSkyBlue, Brushes.Coral, Brushes.White);
        var visual = new DrawingVisual();
        void Draw(WaveformTileTrack source)
        {
            using var dc = visual.RenderOpen(); renderer.BeginFrame();
            renderer.DrawTrack(dc, source, 0, seconds, new Rect(0, 0, 960, 240), 0, 120, 100, WaveformViewMode.Compare, style);
            renderer.EndFrame();
        }
        Draw(snapshot);
        renderer.InvalidateSources(keepVisibleWork: true);
        var pendingDeadline = DateTime.UtcNow.AddSeconds(8);
        while (renderer.IsBusy && DateTime.UtcNow < pendingDeadline) await Task.Delay(5);
        if (renderer.IsBusy || renderer.Diagnostics.TileBuildCount == 0 || renderer.Diagnostics.CanceledJobCount != 0)
            throw new InvalidOperationException("Smoke test: a visible status refresh canceled unchanged in-flight waveform images.");
        Draw(snapshot);
        var warm = renderer.Diagnostics;
        if (warm.RasterHitCount == 0) throw new InvalidOperationException("Smoke test: preserved waveform image work was not published.");
        renderer.InvalidateSources();
        if (renderer.IsBusy) throw new InvalidOperationException("Smoke test: hiding the waveform retained source jobs.");

        // The first bed is normally aligned with the release start. Its broad
        // magnetic zone uses screen distance, including an exactly matching
        // drop marker; additional clips and Shift keep precise free placement.
        timeline.FitAll(); lane.Layer = new BackgroundLayer(); Layout();
        var files = new DataObject(DataFormats.FileDrop, new[] { "ambience.wav" });
        lane.CanDropFiles = _ => true;
        var nearStart = new Point(lane.PlotArea.Left + 80, 50);
        double droppedAt = -1; lane.FilesDropped += (_, at) => droppedAt = at;
        if (!lane.PreviewFileDrop(files, nearStart) || lane.DropTimeSeconds != 0 || !lane.HandleFileDrop(files, nearStart) || droppedAt != 0)
            throw new InvalidOperationException("Smoke test: the first background drop did not magnetically align its marker and placement to zero.");
        lane.PreviewFileDrop(files, nearStart, bypassSnap: true);
        if (!(lane.DropTimeSeconds > 0)) throw new InvalidOperationException("Smoke test: Shift could not bypass first-background start snapping.");
        lane.HandleFileDrop(files, nearStart, bypassSnap: true);
        if (!(droppedAt > 0)) throw new InvalidOperationException("Smoke test: Shift preview and committed background placement disagree.");
        var positioned = new BackgroundClip { Source = backgroundSource, StartSeconds = 1 };
        lane.Layer = new BackgroundLayer { Clips = [positioned] }; Layout();
        lane.PreviewFileDrop(files, nearStart);
        if (!(lane.DropTimeSeconds > 0)) throw new InvalidOperationException("Smoke test: a subsequent background file was incorrectly snapped to zero.");
        double movedTo = -1; lane.ClipMoved += (_, at) => movedTo = at;
        var grab = new Point(lane.PlotArea.Left + lane.PlotArea.Width * 2 / seconds, 65);
        lane.HandlePointerDown(grab);
        lane.UpdatePointerInteraction(new Point(grab.X - lane.PlotArea.Width * .8 / seconds, grab.Y));
        lane.CompletePointerInteraction();
        if (movedTo != 0 || positioned.StartSeconds != 1)
            throw new InvalidOperationException("Smoke test: the sole background clip did not snap its left edge to the release start without mutating its source.");

        var pendingSource = new AudioTrack { FilePath = "Pending ambience.wav", SavedDuration = 4, WorkState = TrackWorkState.Unprocessed };
        var pending = new BackgroundClip { Source = pendingSource };
        lane.Layer = new BackgroundLayer { Clips = [pending] }; Layout();
        if (!lane.RelinkBounds(pending).IsEmpty || lane.HasPendingTiles)
            throw new InvalidOperationException("Smoke test: a found but unprocessed background source exposed missing-file controls or waveform jobs.");
        int fadeEdits = 0; lane.FadesChanged += (_, _, _) => fadeEdits++;
        var fadeHandle = lane.FadeHandlePosition(pending, true);
        lane.HandlePointerDown(fadeHandle); lane.UpdatePointerInteraction(new Point(fadeHandle.X + 140, fadeHandle.Y)); lane.CompletePointerInteraction();
        if (fadeEdits != 0) throw new InvalidOperationException("Smoke test: an unprocessed background clip exposed an active fade handle.");
        pendingSource.SavedDuration = 0; lane.Refresh(); Layout();
        if (lane.ClipBounds(pending).Width != 120 || pending.EndSeconds != 0 || lane.RelinkBounds(pending) != Rect.Empty)
            throw new InvalidOperationException("Smoke test: cancellation before background duration metadata hid the found source or invented audio timing.");
    }
}

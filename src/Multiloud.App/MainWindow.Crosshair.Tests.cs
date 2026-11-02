using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyCrosshairSmokeAsync()
    {
        var samples = Enumerable.Range(0, 8192).Select(index => index % 2 == 0 ? -.25f : .25f).ToArray();
        var pyramid = WaveformPyramid.Build(samples, 2);
        var tracks = Enumerable.Range(1, 2).Select(index => new AudioTrack
        {
            FilePath = $"Crosshair {index}.wav", Original = samples, SampleRate = 1024, Channels = 2,
            OriginalWaveform = pyramid, OriginalPeaks = [new(-.25f, .25f)], PreviewPeaks = [new(-.25f, .25f)],
            Analysis = new AnalysisResult { IntegratedLufs = -12 }, Envelope = [new(0, 0), new(4, 0)],
            Render = new RenderResult { Samples = samples, Waveform = pyramid, OutputLufs = -12 }
        }).ToList();
        var timeline = new TimelineView
        {
            Tracks = tracks, Settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false },
            ShowEnvelopeOnWaveform = true, ShowGainLines = false
        };
        var size = new Size(960, 600);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
        }
        async Task Warm()
        {
            Layout();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (timeline.HasPendingTiles && DateTime.UtcNow < deadline) await Task.Delay(5);
            if (timeline.HasPendingTiles) throw new InvalidOperationException("Smoke test: crosshair fixture did not settle.");
            timeline.FlushPendingRendering();
        }
        uint[] Capture()
        {
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(timeline); return ComparisonPixels(image);
        }
        int events = 0;
        timeline.CursorInfoChanged += _ => events++;
        Layout(); timeline.FitAll();
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.ViewMode = mode;
            timeline.SetCrosshairPointer(null);
            await Warm();
            var signal = timeline.SignalPlotArea;
            var point = new Point(signal.Left + signal.Width * .637, timeline.SignalCenterY - timeline.SignalAmplitude * .5);
            timeline.SetHoveredTrack(point, force: true); Layout();
            // The primary ruler mark must remain visible over an aligned amber playhead.
            timeline.Playhead = 8 * .637; timeline.FlushPendingRendering();
            var baseline = Capture();
            var before = timeline.PerformanceCounters;
            timeline.SetCrosshairPointer(point); timeline.FlushPendingRendering();
            var withMarkers = Capture();
            if (timeline.CursorInfo is not { } info || timeline.CrosshairPosition != point ||
                Math.Abs(info.TimeSeconds - 8 * .637) > .000001 || Math.Abs(info.AmplitudeDbfs - 20 * Math.Log10(.5)) > .000001 ||
                Math.Abs(info.EnvelopeGainDb - 8 * .5 * .48 / .44) > .000001)
                throw new InvalidOperationException("Smoke test: cursor time, waveform dBFS or golden envelope readout disagrees with its axes.");
            var ruler = new Rect(signal.Left, timeline.OverviewArea.Bottom + 5, signal.Width,
                timeline.PlotArea.Top - timeline.OverviewArea.Bottom - 5);
            var leftAxis = new Rect(0, signal.Top, signal.Left, signal.Height);
            var rightAxis = new Rect(signal.Right, signal.Top, size.Width - signal.Right, signal.Height);
            var primary = ((SolidColorBrush)FindResource("AccentBrush")).Color;
            uint primaryPixel = (uint)(primary.A << 24 | primary.R << 16 | primary.G << 8 | primary.B);
            void VerifyAxisMarkers(uint[] plain, uint[] marked, bool showEnvelope)
            {
                bool rulerMarked = false, leftMarked = false, rightMarked = false;
                for (int index = 0; index < plain.Length; index++)
                {
                    if (plain[index] == marked[index]) continue;
                    var pixel = new Point(index % (int)size.Width + .5, index / (int)size.Width + .5);
                    bool inRuler = ruler.Contains(pixel), inLeft = leftAxis.Contains(pixel), inRight = rightAxis.Contains(pixel);
                    bool hoverStem = timeline.PlotArea.Contains(pixel) && Math.Abs(pixel.X - point.X) <= 1.5;
                    if ((!inRuler && !inLeft && !inRight && !hoverStem) || (inLeft && !showEnvelope))
                        throw new InvalidOperationException("Smoke test: cursor marks escaped their axes or the neutral vertical hover line.");
                    if (marked[index] != primaryPixel) continue;
                    rulerMarked |= inRuler && Math.Abs(pixel.X - point.X) < 3;
                    leftMarked |= inLeft && Math.Abs(pixel.Y - point.Y) < 3;
                    rightMarked |= inRight && Math.Abs(pixel.Y - point.Y) < 3;
                }
                if (!rulerMarked || !rightMarked || leftMarked != showEnvelope)
                    throw new InvalidOperationException("Smoke test: cursor axis marks lost their primary color, position, or envelope visibility.");
            }
            VerifyAxisMarkers(baseline, withMarkers, showEnvelope: true);

            var published = events;
            for (var move = 0; move < 32; move++)
                timeline.SetCrosshairPointer(new Point(signal.Left + signal.Width * (.57 + move * .003), point.Y));
            if (events != published) throw new InvalidOperationException("Smoke test: pointer readouts were not coalesced with dynamic drawing.");
            timeline.FlushPendingRendering();
            var after = timeline.PerformanceCounters;
            if (before.WaveformRedraws != after.WaveformRedraws || before.OverviewRedraws != after.OverviewRedraws ||
                before.EditorRedraws != after.EditorRedraws || before.MetricsPasses != after.MetricsPasses ||
                before.Tiles.PeakQueryCount != after.Tiles.PeakQueryCount || before.Tiles.TileBuildCount != after.Tiles.TileBuildCount ||
                before.Tiles.RasterBuildCount != after.Tiles.RasterBuildCount || before.Tiles.CacheHitCount != after.Tiles.CacheHitCount ||
                after.DynamicRedraws <= before.DynamicRedraws)
                throw new InvalidOperationException("Smoke test: cursor marker movement rebuilt static layers, queried audio or touched waveform tiles.");
            timeline.SetCrosshairPointer(new Point(point.X, timeline.SignalCenterY)); timeline.FlushPendingRendering();
            if (timeline.CursorInfo is not { AmplitudeDbfs: double.NegativeInfinity, EnvelopeGainDb: 0 })
                throw new InvalidOperationException("Smoke test: the cursor zero line is not minus infinity dBFS and zero envelope gain.");
            timeline.SetCrosshairPointer(new Point(point.X, timeline.SignalCenterY + timeline.SignalAmplitude * .5)); timeline.FlushPendingRendering();
            if (timeline.CursorInfo is not { } lower || Math.Abs(lower.AmplitudeDbfs - info.AmplitudeDbfs) > .000001 || Math.Abs(lower.EnvelopeGainDb + info.EnvelopeGainDb) > .000001)
                throw new InvalidOperationException("Smoke test: upper and lower cursor amplitude readings are asymmetric.");
            timeline.SetCrosshairPointer(null); timeline.FlushPendingRendering();
            if (timeline.CursorInfo != null || timeline.CrosshairPosition != null || !baseline.SequenceEqual(Capture()))
                throw new InvalidOperationException("Smoke test: leaving the waveform did not clear its cursor marks and readout.");

            timeline.ShowEnvelopeOnWaveform = false; Layout();
            var hiddenAxis = Capture();
            timeline.SetCrosshairPointer(point); timeline.FlushPendingRendering();
            VerifyAxisMarkers(hiddenAxis, Capture(), showEnvelope: false);
            if (timeline.CursorInfo is not { } hidden || hidden != info)
                throw new InvalidOperationException("Smoke test: hiding the envelope axis changed the cursor measurements.");
            timeline.SetCrosshairPointer(null); timeline.FlushPendingRendering();
            if (!hiddenAxis.SequenceEqual(Capture()))
                throw new InvalidOperationException("Smoke test: leaving the waveform retained marks with the envelope axis hidden.");
            timeline.ShowEnvelopeOnWaveform = true;
        }

        var held = new Point(timeline.SignalPlotArea.Left + timeline.SignalPlotArea.Width * .3, timeline.SignalCenterY);
        timeline.SetCrosshairPointer(held); timeline.FlushPendingRendering();
        timeline.FitTrack(tracks[1]); await Warm();
        if (timeline.CrosshairPosition != held || timeline.CursorInfo is not { } moved || Math.Abs(moved.TimeSeconds - 5.2) > .000001)
            throw new InvalidOperationException("Smoke test: camera movement left a stationary pointer's absolute time stale.");
        timeline.SetCrosshairPointer(new Point(held.X, timeline.PlotArea.Top + 10)); timeline.FlushPendingRendering();
        if (timeline.CursorInfo != null) throw new InvalidOperationException("Smoke test: cursor marks entered the protected filename strip.");

        timeline.FitAll(); Layout();
        double seek = -1;
        timeline.SeekRequested += time => seek = time;
        var header = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width * .7, timeline.PlotArea.Top + 10);
        if (!timeline.HandleNavigationPointerDown(header, 1) || Math.Abs(seek - 5.6) > .000001 ||
            timeline.VisibleDurationSeconds != 8 || timeline.ViewStartSeconds != 0 || !ReferenceEquals(timeline.SelectedTrack, tracks[1]))
            throw new InvalidOperationException("Smoke test: a filename single-click did not seek without changing zoom.");
        timeline.HandleNavigationPointerMove(new Point(header.X + timeline.PlotArea.Width * .05, header.Y));
        timeline.CompletePointerInteraction();
        if (Math.Abs(seek - 6) > .000001) throw new InvalidOperationException("Smoke test: seeking from a filename did not retain ordinary playhead dragging.");
        timeline.HandleNavigationPointerDown(header, 2);
        if (timeline.VisibleDurationSeconds != 4 || timeline.ViewStartSeconds != 4)
            throw new InvalidOperationException("Smoke test: filename double-click no longer fits its track.");
        timeline.FitAll();
        timeline.HandleNavigationPointerDown(new Point(timeline.OverviewArea.Left + timeline.OverviewArea.Width * .75, timeline.OverviewArea.Top + 20), 2);
        if (timeline.VisibleDurationSeconds != 4 || timeline.ViewStartSeconds != 4)
            throw new InvalidOperationException("Smoke test: overview double-click no longer fits its track.");

        // Hover is a neutral background beneath the cached images, independent
        // of selection and the existing higher/lower target colors.
        timeline.SetCrosshairPointer(null); timeline.FitAll();
        tracks[0].TargetLufs = -10; tracks[1].TargetLufs = -18;
        timeline.SelectedTrack = tracks[0]; timeline.Refresh();
        int At(double fraction, double y) => (int)y * (int)size.Width + (int)(timeline.PlotArea.Left + timeline.PlotArea.Width * fraction);
        static int Difference(uint a, uint b) => Enumerable.Range(0, 3).Max(channel =>
            Math.Abs((int)((a >> (channel * 8)) & 255) - (int)((b >> (channel * 8)) & 255)));
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.ViewMode = mode;
            timeline.SetHoveredTrack(new Point(-10, -10), force: true); await Warm();
            var plain = Capture(); var before = timeline.PerformanceCounters;
            var hoverFrames = timeline.HoverBackgroundRedrawCount;
            var point = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width * .73, timeline.SignalPlotArea.Top + 60);
            timeline.SetHoveredTrack(point, force: true); Layout();
            var hovered = Capture(); var after = timeline.PerformanceCounters;
            // The cursor stem deliberately overlays its own column. Inspect
            // background and opaque waveform pixels five pixels beside it,
            // independently of that retained dynamic overlay.
            var left = At(.27, point.Y); var right = At(.73, point.Y) + 5; var wave = At(.73, timeline.SignalCenterY + 10) + 5;
            var expected = new Rect(timeline.PlotArea.Left + timeline.PlotArea.Width / 2, timeline.PlotArea.Top, timeline.PlotArea.Width / 2, timeline.PlotArea.Height);
            if (timeline.HoveredTrackHighlightBounds != expected || plain[left] != hovered[left] ||
                Difference(plain[right], hovered[right]) is < 1 or > 8 ||
                (mode == WaveformViewMode.Compare ? Difference(plain[wave], hovered[wave]) > 8 : plain[wave] != hovered[wave]) ||
                before.WaveformRedraws != after.WaveformRedraws || before.OverviewRedraws != after.OverviewRedraws || before.MetricsPasses != after.MetricsPasses ||
                before.Tiles.PeakQueryCount != after.Tiles.PeakQueryCount || before.Tiles.RasterBuildCount != after.Tiles.RasterBuildCount ||
                before.Tiles.CacheHitCount != after.Tiles.CacheHitCount || timeline.HoverBackgroundRedrawCount <= hoverFrames)
                throw new InvalidOperationException("Smoke test: track hover changed the wrong background, tinted opaque waveforms, or rebuilt cached audio drawing.");
            foreach (var outside in new[] { new Point(point.X, timeline.OverviewArea.Top + 20), new Point(point.X, 75), new Point(-10, -10) })
            {
                timeline.SetHoveredTrack(outside, force: true); Layout();
                if (!timeline.HoveredTrackHighlightBounds.IsEmpty || Capture()[right + 5] != plain[right + 5])
                    throw new InvalidOperationException("Smoke test: track hover remained in the overview, ruler or outside the waveform.");
            }
            timeline.SetHoveredTrack(point, force: true); Layout();
            timeline.FitTrack(tracks[0]); await Warm();
            if (timeline.HoveredTrackHighlightBounds != timeline.PlotArea)
                throw new InvalidOperationException("Smoke test: a stationary pointer highlighted the old track after moving the camera.");
            timeline.FitAll();
        }
        var missing = new AudioTrack { Id = tracks[1].Id, FilePath = "Missing hovered track.wav", SavedDuration = 4, WorkState = TrackWorkState.Missing };
        tracks[1] = missing; timeline.Refresh();
        timeline.SetHoveredTrack(new Point(-10, -10), force: true); await Warm();
        var missingPlain = Capture(); var missingPoint = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width * .73, timeline.SignalPlotArea.Top + 60);
        timeline.SetHoveredTrack(missingPoint, force: true); Layout();
        var missingHovered = Capture(); var missingPixel = At(.73, missingPoint.Y) + 5;
        if (timeline.HoveredTrackHighlightBounds.IsEmpty || Difference(missingPlain[missingPixel], missingHovered[missingPixel]) is < 1 or > 8 ||
            ((missingHovered[missingPixel] >> 16) & 255) <= ((missingHovered[missingPixel] >> 8) & 255))
            throw new InvalidOperationException("Smoke test: hover hid or replaced the missing-file red background.");
    }
}

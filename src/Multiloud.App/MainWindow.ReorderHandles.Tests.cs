using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyWaveformReorderHandlesSmoke()
    {
        // Missing slots exercise the actual editor drawing and hit paths without
        // creating unrelated waveform-worker jobs or changing the open project.
        var tracks = Enumerable.Range(1, 4).Select(index => new AudioTrack
        {
            FilePath = $"{index}.wav", SavedDuration = 10, WorkState = TrackWorkState.Missing
        }).ToArray();
        var timeline = new TimelineView { Settings = _settings };
        var size = new Size(960, 420);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
        }
        uint[] Capture()
        {
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(timeline); return ComparisonPixels(image);
        }
        for (var count = 1; count <= 4; count++)
        {
            timeline.Tracks = tracks.Take(count).ToList(); timeline.FitAll(); Layout();
            var x = timeline.PlotArea.Left + timeline.PlotArea.Width / count / 2;
            var probe = new Point(x, timeline.PlotArea.Top + 37);
            timeline.SetHoveredTrack(new Point(-10, -10), force: true); Layout();
            var before = Capture();
            timeline.SetHoveredTrack(probe, force: true); Layout();
            var after = Capture();
            var visible = count == 4;
            var rect = timeline.TrackHandleRect(tracks[0]);
            // Inspect the left grip dot, away from the independent neutral
            // hover stem that now runs through the pointer's center column.
            var dotPixel = (int)(timeline.PlotArea.Top + 34) * (int)size.Width + (int)(x - 7);
            // The faint neutral background also changes this pixel on hover.
            // A real grip dot adds strong foreground contrast beyond that tint.
            var paintedGrip = Enumerable.Range(0, 3).Any(channel =>
                Math.Abs((int)((before[dotPixel] >> (channel * 8)) & 255) - (int)((after[dotPixel] >> (channel * 8)) & 255)) > 32);
            if (timeline.VisibleInteriorBoundaryCount != count - 1 || rect.IsEmpty == visible ||
                paintedGrip != visible || timeline.HandleTrackGripPointerDown(probe) != visible)
                throw new InvalidOperationException($"Smoke test: {count - 1} interior waveform boundaries did not gate grip painting and input together.");
            timeline.CompletePointerInteraction();
        }

        void View(double start, double duration)
        {
            timeline.FitAll();
            var overview = timeline.OverviewArea;
            var y = overview.Top + overview.Height / 2;
            timeline.HandleNavigationPointerDown(new Point(overview.Right, y), 1);
            timeline.HandleNavigationPointerMove(new Point(overview.Left + duration / 40 * overview.Width, y));
            timeline.CompletePointerInteraction();
            if (start != 0)
            {
                var middle = overview.Left + duration / 40 * overview.Width / 2;
                timeline.HandleNavigationPointerDown(new Point(middle, y), 1);
                timeline.HandleNavigationPointerMove(new Point(middle + start / 40 * overview.Width, y));
                timeline.CompletePointerInteraction();
            }
            Layout();
        }
        foreach (var view in new[]
        {
            (Start: 0.0, Duration: 9.9, Count: 0),
            (Start: 0.0, Duration: 10.0, Count: 1),
            (Start: 0.0, Duration: 20.0, Count: 2),
            (Start: 10.0, Duration: 20.0, Count: 3), // Both viewport edges are included.
            (Start: 10.01, Duration: 19.98, Count: 1),
            (Start: 20.0, Duration: 20.0, Count: 2)  // Album end is never a fourth boundary.
        })
        {
            View(view.Start, view.Duration);
            if (Math.Abs(timeline.ViewStartSeconds - view.Start) > .000001 || Math.Abs(timeline.VisibleDurationSeconds - view.Duration) > .000001 ||
                timeline.VisibleInteriorBoundaryCount != view.Count)
                throw new InvalidOperationException("Smoke test: zooming or panning counted album edges or lost an interior boundary at a viewport edge.");
            var index = Math.Clamp((int)((view.Start + view.Duration / 2) / 10), 0, 3);
            var track = tracks[index];
            var probe = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width / 2, timeline.PlotArea.Top + 37);
            timeline.SetHoveredTrack(probe, force: true); Layout();
            if (timeline.TrackHandleRect(track).IsEmpty != (view.Count < 3))
                throw new InvalidOperationException("Smoke test: a camera change retained stale waveform grip visibility.");
        }
    }
}

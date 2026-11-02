using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyMissingTimelineSmoke()
    {
        var authored = new List<EnvelopePoint> { new(0, 0), new(3, -2), new(7, 0) };
        var first = new AudioTrack
        {
            FilePath = "Missing song.wav", MissingAbsolutePath = @"C:\Unavailable\Missing song.wav",
            SavedDuration = 7, WorkState = TrackWorkState.Missing, Envelope = authored,
            InputGainDb = 1.5, TargetLufs = -12, TrimDb = .5
        };
        var second = new AudioTrack { FilePath = "Other missing song.wav", SavedDuration = 11, WorkState = TrackWorkState.Missing };
        var tracks = new List<AudioTrack> { first, second };
        var timeline = new TimelineView { Tracks = tracks, Settings = _settings };
        var size = new Size(960, 420);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout();
            timeline.FlushPendingRendering();
        }
        Layout(); timeline.FitAll(); Layout();
        if (Math.Abs(timeline.VisibleDurationSeconds - 18) > .001 || first.Duration != 7 || second.Duration != 11)
            throw new InvalidOperationException("Smoke test: missing audio lost its saved timeline duration.");

        var button = timeline.MissingRelinkBounds(first);
        var expectedCenter = timeline.PlotArea.Left + timeline.PlotArea.Width * 7 / 18 / 2;
        if (button.IsEmpty || !timeline.PlotArea.Contains(button) || Math.Abs(button.Left + button.Width / 2 - expectedCenter) > .75)
            throw new InvalidOperationException("Smoke test: missing audio did not expose a centered, visible relink action.");
        // Inspect a quiet part of the slot: this must be a visible muted red
        // placeholder, rather than an empty background or a fabricated waveform.
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(timeline);
        var pixels = ComparisonPixels(bitmap);
        var sample = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width * .12, timeline.PlotArea.Top + timeline.PlotArea.Height * .75);
        var pixel = pixels[(int)sample.Y * bitmap.PixelWidth + (int)sample.X];
        if ((pixel >> 24) != 255 || ((pixel >> 16) & 255) <= ((pixel >> 8) & 255) + 5)
            throw new InvalidOperationException("Smoke test: the missing-audio waveform slot is not visibly marked in muted red.");

        int relinks = 0, edits = 0, rebuilds = 0;
        AudioTrack? relinked = null;
        timeline.TrackRelinkRequested += track => { relinks++; relinked = track; };
        timeline.EditStarted += () => edits++;
        timeline.RebuildRequested += () => rebuilds++;
        var center = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2);
        if (!timeline.HandleMissingRelinkClick(center) || relinks != 1 || !ReferenceEquals(relinked, first) || !ReferenceEquals(timeline.SelectedTrack, first))
            throw new InvalidOperationException("Smoke test: clicking a missing slot did not request relinking the selected track.");
        timeline.HandleWaveformPointerDown(first, center, 1);
        if (relinks != 2 || rebuilds != 0 || timeline.HandleMissingRelinkClick(new Point(timeline.PlotArea.Left, timeline.PlotArea.Bottom - 2)))
            throw new InvalidOperationException("Smoke test: missing-slot input was routed to the wrong action.");

        timeline.SetTrackInputGain(first, 6); timeline.SetTrackTarget(first, -10);
        timeline.BeginTrackValueDrag(first, false, 200, false); timeline.UpdateTrackValueDrag(100, false); timeline.EndNativeValueDrag();
        if (timeline.InsertEnvelopePointOnCurve(first, 2) != -1 || edits != 0 || first.InputGainDb != 1.5 || first.TargetLufs != -12 || first.TrimDb != .5 || !ReferenceEquals(first.Envelope, authored))
            throw new InvalidOperationException("Smoke test: missing audio allowed processing edits before relinking.");

        timeline.SetHoveredTrack(new Point(expectedCenter, timeline.PlotArea.Top + 30), force: true); Layout();
        var trash = Descendants<Button>(timeline).Single(candidate => System.Windows.Automation.AutomationProperties.GetName(candidate) == "Remove track");
        if (trash.Visibility != Visibility.Visible || trash.RenderSize.Width <= 0)
            throw new InvalidOperationException("Smoke test: missing audio lost its remove action.");
        VerifyTrashIconAppearanceSmoke(trash);
        var filenamePoint = new Point(timeline.PlotArea.Left + 2, timeline.PlotArea.Top + 10);
        if (!timeline.HandleNavigationPointerDown(filenamePoint, 1)) timeline.HandleWaveformPointerDown(first, filenamePoint, 1);
        if (Math.Abs(timeline.VisibleDurationSeconds - 18) > .001 || Math.Abs(timeline.Playhead - 18 * 2 / timeline.PlotArea.Width) > .001 ||
            !ReferenceEquals(timeline.SelectedTrack, first))
            throw new InvalidOperationException("Smoke test: a missing track's filename did not seek without changing zoom.");
        timeline.CompletePointerInteraction();
        if (!timeline.HandleNavigationPointerDown(filenamePoint, 2)) timeline.HandleWaveformPointerDown(first, filenamePoint, 2);
        if (Math.Abs(timeline.VisibleDurationSeconds - 7) > .001)
            throw new InvalidOperationException("Smoke test: double-clicking a missing track's filename did not fit its saved duration.");
        timeline.CompletePointerInteraction();
        timeline.FitAll(); Layout();
        timeline.TrackMoveRequested += (track, index) => { TrackOrdering.Move(tracks, track, index); timeline.Refresh(); };
        if (!timeline.HandleTrackDrop(new DataObject(TimelineView.TrackDragDataFormat, first.Id.ToString()), new Point(timeline.PlotArea.Right - 1, timeline.PlotArea.Top + 80)) ||
            !ReferenceEquals(tracks[0], second) || !ReferenceEquals(tracks[1], first) || !ReferenceEquals(first.Envelope, authored))
            throw new InvalidOperationException("Smoke test: missing-audio reordering lost track order or authored edits.");

        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.ViewMode = mode; timeline.FitAll(); Layout();
            var work = timeline.PerformanceCounters.Tiles;
            if (timeline.HasPendingTiles || work.TileBuildCount != 0 || work.PeakQueryCount != 0 || work.FallbackTileCount != 0 || work.RasterBuildCount != 0)
                throw new InvalidOperationException("Smoke test: a missing-audio slot submitted waveform source or tile work.");
        }
        size = new Size(210, 420); Layout(); timeline.FitAll(); Layout();
        var narrow = timeline.MissingRelinkBounds(first);
        if (narrow.IsEmpty || narrow.Width >= 48 || !timeline.PlotArea.Contains(narrow) || !timeline.HandleMissingRelinkClick(new Point(narrow.Left + narrow.Width / 2, narrow.Top + narrow.Height / 2)))
            throw new InvalidOperationException("Smoke test: a narrow missing slot lost its compact relink action.");
    }
}

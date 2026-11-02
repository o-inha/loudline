using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyAppliedEnvelopeSmokeAsync()
    {
        var samples = Enumerable.Range(0, 12288).Select(index => index % 2 == 0 ? -.1f : .1f).ToArray();
        var pyramid = WaveformPyramid.Build(samples, 1);
        var settings = new ProcessingSettings { Strength = 1, MatchingEnabled = false, LimiterEnabled = false };
        var track = new AudioTrack
        {
            FilePath = "Applied envelope.wav", Original = samples, SampleRate = 1024, Channels = 1,
            OriginalWaveform = pyramid, OriginalPeaks = Enumerable.Range(0, 1024).Select(_ => new PeakBin(-.1f, .1f)).ToList(),
            Analysis = new AnalysisResult { IntegratedLufs = -12 },
            Envelope = [new(0, 0), new(4, 8), new(8, -6), new(12, 0)],
            TransitionEnvelope = [new(0, 2), new(4, 1), new(8, -1), new(12, -2)]
        };
        track.Render = new RenderResult
        {
            Samples = samples, Waveform = pyramid, PreMatchLufs = -12,
            RenderedLevelingStrength = 1, RenderedEnvelope = EnvelopeMath.Canonicalize(TransitionDesigner.CombinedEnvelope(track))
        };
        track.PreviewPeaks = track.OriginalPeaks;
        var authored = track.Envelope;
        var points = authored.ToArray();
        var transitions = track.TransitionEnvelope.ToArray();
        var render = track.Render; var analysis = track.Analysis;
        var timeline = new TimelineView { Tracks = [track], Settings = settings, ShowGainLines = false };
        var size = new Size(960, 600);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
        }
        async Task Warm()
        {
            Layout();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (timeline.HasPendingTiles && DateTime.UtcNow < deadline) { await Task.Delay(5); timeline.FlushPendingRendering(); }
            if (timeline.HasPendingTiles) throw new InvalidOperationException("Smoke test: applied-envelope fixture tiles did not settle.");
        }
        uint[] Capture()
        {
            var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(timeline); return ComparisonPixels(image);
        }
        bool GoldNear(uint[] pixels, Point point)
        {
            for (int y = (int)point.Y - 6; y <= (int)point.Y + 6; y++)
                for (int x = (int)point.X - 6; x <= (int)point.X + 6; x++)
                {
                    var pixel = pixels[y * (int)size.Width + x];
                    var r = (pixel >> 16) & 255; var g = (pixel >> 8) & 255; var b = pixel & 255;
                    if (r > 200 && g > 140 && b < 150 && r > g + 25) return true;
                }
            return false;
        }
        void RequireAuthoredUnchanged()
        {
            if (!ReferenceEquals(track.Envelope, authored) || !track.Envelope.SequenceEqual(points) ||
                !track.TransitionEnvelope.SequenceEqual(transitions) || !ReferenceEquals(track.Render, render) || !ReferenceEquals(track.Analysis, analysis))
                throw new InvalidOperationException("Smoke test: displaying envelope Strength mutated authored points, transitions or processed audio.");
        }

        Layout(); timeline.FitAll(); Layout();
        var full = timeline.EnvelopePointPosition(track, 1);
        var center = timeline.SignalCenterY;
        var pixelsPerDb = (center - full.Y) / 9;
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.Settings = settings; timeline.ViewMode = mode; await Warm();
            timeline.LivePreviewActive = true;
            var before = timeline.PerformanceCounters.Tiles;
            long liveSamples = -1;
            foreach (var strength in new[] { .75, .5, .25, .65 })
            {
                timeline.Settings = settings with { Strength = strength }; Layout();
                var point = timeline.EnvelopePointPosition(track, 1);
                if (Math.Abs(point.Y - (center + (full.Y - center) * strength)) > .000001 ||
                    Math.Abs(timeline.AppliedEnvelopeGainAt(track, 4) - 9 * strength) > .000001 ||
                    timeline.EnvelopePointGainLabel(track, 1) != $"{9 * strength:+0.00;−0.00;0.00} dB")
                    throw new InvalidOperationException("Smoke test: an envelope point or its dB label did not follow applied Strength on a fixed axis.");
                if (!GoldNear(Capture(), point)) throw new InvalidOperationException("Smoke test: the painted envelope did not follow its applied-strength point geometry.");
                timeline.SetCrosshairPointer(point); timeline.FlushPendingRendering();
                if (timeline.CursorInfo is not { } cursor || Math.Abs(cursor.EnvelopeGainDb - 9 * strength) > .000001)
                    throw new InvalidOperationException("Smoke test: the applied envelope disagrees with the cursor's golden dB axis.");
                timeline.SetCrosshairPointer(null);
                RequireAuthoredUnchanged();
                var current = timeline.PerformanceCounters.Tiles;
                if (current.TileBuildCount != before.TileBuildCount || current.RasterBuildCount != before.RasterBuildCount ||
                    liveSamples >= 0 && current.InteractivePeakSampleBuildCount != liveSamples || current.RetainedBytes > 128L * 1024 * 1024)
                    throw new InvalidOperationException("Smoke test: a Strength draft rebuilt fine tiles, resampled unchanged live peaks or exceeded the cache budget.");
                liveSamples = current.InteractivePeakSampleBuildCount;
            }
            timeline.LivePreviewActive = false;
        }

        timeline.ViewMode = WaveformViewMode.Original;
        timeline.Settings = settings with { Strength = .5 }; Layout();
        int starts = 0, commits = 0, seeks = 0;
        timeline.EditStarted += () => starts++;
        timeline.EditCommitted += _ => commits++;
        timeline.SeekRequested += _ => seeks++;
        var origin = timeline.EnvelopePointPosition(track, 1);
        timeline.HandleWaveformPointerDown(track, origin, 1);
        timeline.HandleWaveformPointerMove(new Point(origin.X, origin.Y - 2 * pixelsPerDb));
        if (Math.Abs(track.Envelope[1].GainDb - 12) > .000001 || Math.Abs(timeline.AppliedEnvelopeGainAt(track, 4) - 6.5) > .000001)
            throw new InvalidOperationException("Smoke test: half-strength envelope dragging did not inverse the applied dB movement.");
        timeline.HandleWaveformPointerMove(new Point(origin.X + timeline.PlotArea.Width / 12, origin.Y - 2 * pixelsPerDb));
        if (Math.Abs(track.Envelope[1].Time - 5) > .000001 || Math.Abs(track.Envelope[1].GainDb - (13 - TransitionDesigner.GainAt(track, 5))) > .000001)
            throw new InvalidOperationException("Smoke test: an applied-strength drag did not subtract the transition at the new time.");
        timeline.HandleWaveformPointerMove(new Point(origin.X, origin.Y - 10000));
        if (track.Envelope[1].GainDb != 24) throw new InvalidOperationException("Smoke test: an inverse-strength drag exceeded the authored upper gain bound.");
        timeline.HandleWaveformPointerMove(new Point(origin.X, origin.Y + 10000));
        if (track.Envelope[1].GainDb != -24) throw new InvalidOperationException("Smoke test: an inverse-strength drag exceeded the authored lower gain bound.");
        timeline.HandleWaveformPointerMove(origin); timeline.CompletePointerInteraction();
        if (starts != 1 || commits != 1) throw new InvalidOperationException("Smoke test: one applied-strength drag created more than one edit transaction.");
        RequireAuthoredUnchanged();

        var key = RenderKey(track, timeline.Settings);
        var inserted = timeline.InsertEnvelopePointOnCurve(track, 2);
        timeline.CompletePointerInteraction();
        if (inserted < 0 || RenderKey(track, timeline.Settings) != key || !ReferenceEquals(track.Render, render) ||
            Math.Abs(timeline.AppliedEnvelopeGainAt(track, 2) - 2.75) > .000001)
            throw new InvalidOperationException("Smoke test: inserting on a scaled envelope changed its canonical processed curve.");
        track.Envelope.RemoveAt(inserted); timeline.Refresh();

        foreach (var inactive in new[] { settings with { Strength = 0 }, settings with { LevelingEnabled = false, Strength = .75 } })
        {
            timeline.ShowGainLines = true; timeline.Settings = inactive; Layout();
            var count = starts; var seekCount = seeks;
            if (Enumerable.Range(0, track.Envelope.Count).Any(index => timeline.EnvelopePointPosition(track, index).Y != center ||
                    timeline.EnvelopePointGainLabel(track, index) != $"{0:+0.00;−0.00;0.00} dB") ||
                timeline.InsertEnvelopePointOnCurve(track, 3) != -1)
                throw new InvalidOperationException("Smoke test: zero or bypassed Strength retained a nonzero envelope or allowed insertion.");
            var point = timeline.EnvelopePointPosition(track, 1);
            timeline.HandleWaveformPointerDown(track, point, 1);
            timeline.HandleWaveformPointerMove(new Point(point.X, point.Y - 50));
            timeline.CompletePointerInteraction();
            if (starts != count || seeks <= seekCount) throw new InvalidOperationException("Smoke test: a flat inactive envelope captured a gain edit instead of seeking.");
            RequireAuthoredUnchanged();
        }
        timeline.Settings = settings; Layout();
        if (timeline.EnvelopePointPosition(track, 1) != full || timeline.AppliedEnvelopeGainAt(track, 4) != 9)
            throw new InvalidOperationException("Smoke test: restoring Strength did not restore the authored envelope display.");
        RequireAuthoredUnchanged();
    }
}

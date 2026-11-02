using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyWaveformCenteringSmokeAsync(string? screenshot)
    {
        // Full-scale opposing stereo channels produce a symmetric filled signal.
        // Inspect rendered pixels, not only the geometry properties used to draw it.
        var samples = Enumerable.Range(0, 4096).Select(index => index % 2 == 0 ? -1f : 1f).ToArray();
        var pyramid = WaveformPyramid.Build(samples, 2);
        var track = new AudioTrack
        {
            FilePath = "Centered waveform.wav", Original = samples, SampleRate = 1024, Channels = 2,
            OriginalWaveform = pyramid, OriginalPeaks = [new(-1, 1)],
            Analysis = new AnalysisResult { IntegratedLufs = 0 },
            Envelope = [new(0, 0), new(2, 0)],
            Render = new RenderResult { Samples = samples, Waveform = pyramid }, PreviewPeaks = [new(-1, 1)]
        };
        foreach (var size in new[] { new Size(960, 600), new Size(520, 280) })
        {
            var timeline = new TimelineView
            {
                Tracks = [track], Settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false },
                ShowEnvelopeOnWaveform = false, ShowGainLines = false
            };
            void Layout()
            {
                timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
            }
            async Task Warm()
            {
                Layout();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (timeline.HasPendingTiles && DateTime.UtcNow < deadline) await Task.Delay(10);
                if (timeline.HasPendingTiles) throw new InvalidOperationException("Smoke test: centered waveform tiles did not settle.");
                timeline.Refresh(); timeline.FlushPendingRendering();
            }
            RenderTargetBitmap Capture()
            {
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(timeline); return image;
            }
            static bool SignalPixel(uint pixel)
            {
                var red = (pixel >> 16) & 255; var green = (pixel >> 8) & 255; var blue = pixel & 255;
                return green > red + 25 && green > blue + 10;
            }
            Layout(); timeline.FitAll(); Layout();
            var plot = timeline.PlotArea;
            var signal = timeline.SignalPlotArea;
            var expectedCenter = (signal.Top + signal.Bottom) * .5;
            if (Math.Abs(signal.Top - plot.Top - 22) > .001 || signal.Bottom != plot.Bottom ||
                Math.Abs(timeline.SignalCenterY - expectedCenter) > .001 ||
                Math.Abs(timeline.EnvelopePointPosition(track, 0).Y - expectedCenter) > .001 ||
                Math.Abs(timeline.GainLineY(track) - expectedCenter) > .001)
                throw new InvalidOperationException("Smoke test: filename-only reservation, waveform, envelope and gain centers disagree.");

            foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
            {
                timeline.ViewMode = mode; await Warm();
                var bitmap = Capture(); var pixels = ComparisonPixels(bitmap);
                int x = (int)(signal.Left + signal.Width * .25);
                var rows = Enumerable.Range((int)Math.Ceiling(signal.Top), (int)Math.Floor(signal.Bottom) - (int)Math.Ceiling(signal.Top))
                    .Where(y => SignalPixel(pixels[y * bitmap.PixelWidth + x])).ToArray();
                if (rows.Length == 0 || Math.Abs((rows[0] + rows[^1] + 1) * .5 - expectedCenter) > 1.5 ||
                    Math.Abs(rows[0] - (expectedCenter - timeline.SignalAmplitude)) > 3 ||
                    Math.Abs(rows[^1] + 1 - (expectedCenter + timeline.SignalAmplitude)) > 3)
                    throw new InvalidOperationException($"Smoke test: {mode} waveform pixels are not centered with symmetric headroom below the filename.");
                if (Enumerable.Range((int)Math.Ceiling(plot.Top + 1), 20).Any(y => SignalPixel(pixels[y * bitmap.PixelWidth + x])))
                    throw new InvalidOperationException("Smoke test: the waveform overwrote its reserved filename strip.");

                var before = timeline.PerformanceCounters;
                timeline.SetHoveredTrack(new Point(signal.Left + signal.Width * .5, signal.Top + 30), force: true); Layout();
                var input = Descendants<TextBlock>(timeline).Single(text => text.Text == "Input");
                var target = Descendants<TextBlock>(timeline).Single(text => text.Text == "Target");
                foreach (var label in new[] { input, target })
                {
                    var bounds = label.TransformToAncestor(timeline).TransformBounds(new Rect(label.RenderSize));
                    if (bounds.Width <= 0 || !signal.Contains(bounds))
                        throw new InvalidOperationException("Smoke test: track hover controls are not overlaid inside the signal area.");
                }
                var after = timeline.PerformanceCounters;
                var hovered = ComparisonPixels(Capture());
                bool WaveformChanged(int y)
                {
                    var a = pixels[y * bitmap.PixelWidth + x]; var b = hovered[y * bitmap.PixelWidth + x];
                    if (mode != WaveformViewMode.Compare && y > rows[0] + 2 && y < rows[^1] - 2) return a != b;
                    // Compare and antialiased contour pixels are translucent.
                    // A neutral background can change their composite slightly.
                    return Enumerable.Range(0, 3).Any(channel =>
                        Math.Abs((int)((a >> (channel * 8)) & 255) - (int)((b >> (channel * 8)) & 255)) > 8);
                }
                if (timeline.SignalPlotArea != signal || timeline.SignalCenterY != expectedCenter ||
                    before.WaveformRedraws != after.WaveformRedraws || before.Tiles.RasterBuildCount != after.Tiles.RasterBuildCount ||
                    rows.Any(WaveformChanged))
                    throw new InvalidOperationException("Smoke test: hovering changed waveform size, position, cached rendering or unobscured pixels.");
                timeline.SetHoveredTrack(new Point(-10, -10), force: true); Layout();
            }
            if (screenshot != null && size.Width == 960)
            {
                timeline.ShowEnvelopeOnWaveform = true; timeline.ShowGainLines = true;
                timeline.SetHoveredTrack(new Point(signal.Left + signal.Width * .5, signal.Top + 30), force: true); Layout();
                CaptureVisual(timeline, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-waveform-centered.png"));
            }
        }
    }
}

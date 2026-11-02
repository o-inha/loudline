using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyAllRasterModesSmokeAsync()
    {
        var original = Enumerable.Range(0, 2048).Select(index => index % 2 == 0 ? -.4f : .4f).ToArray();
        var processed = Enumerable.Range(0, 2048).Select(index => index % 2 == 0 ? -.7f : .7f).ToArray();
        var originalPyramid = WaveformPyramid.Build(original, 2);
        var processedPyramid = WaveformPyramid.Build(processed, 2);
        var source = new WaveformTileSource(originalPyramid, originalPyramid, [new(-.4f, .4f)], 512, 2);
        var preview = new WaveformTileSource(processedPyramid, processedPyramid, [new(-.7f, .7f)], 512, 2);
        var snapshot = new WaveformTileTrack(Guid.NewGuid(), source, preview);
        var style = new WaveformTileStyle(new SolidColorBrush(Color.FromRgb(0, 200, 80)),
            new SolidColorBrush(Color.FromRgb(0, 120, 255)), new SolidColorBrush(Color.FromRgb(255, 65, 100)),
            new SolidColorBrush(Color.FromRgb(230, 230, 230)));
        var area = new Rect(0, 0, 1024, 128);
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            using var renderer = new WaveformTileRenderer(Dispatcher);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
            void Draw(double duration, WaveformTileTrack track, bool finish = true)
            {
                using var dc = visual.RenderOpen();
                renderer.BeginFrame();
                renderer.DrawTrack(dc, track, 0, duration, area, 0, 64, 51.2, mode, style);
                if (finish) renderer.EndFrame();
            }
            async Task Settle()
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (renderer.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(10);
                if (renderer.IsBusy) throw new InvalidOperationException("Smoke test: raster navigation fixture did not settle.");
            }
            Draw(2, snapshot); await Settle(); Draw(2, snapshot);
            var warm = renderer.Diagnostics;
            if (warm.RasterHitCount < 2 || warm.RasterBuildCount != 2)
                throw new InvalidOperationException($"Smoke test: {mode} did not use its two background-built images.");
            var bitmap = new RenderTargetBitmap(1024, 128, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = ComparisonPixels(bitmap);
            uint center = pixels[64 * 1024 + 256], upper = pixels[36 * 1024 + 256];
            if (mode == WaveformViewMode.Compare ? Math.Abs((center >> 24) - 168L) > 1 : center != 0xFF00C850)
                throw new InvalidOperationException($"Smoke test: {mode} changed waveform fill color or opacity.");
            if (mode == WaveformViewMode.Original ? upper != 0 : (upper >> 24) == 0)
                throw new InvalidOperationException($"Smoke test: {mode} displayed the wrong source amplitude.");

            // Cross a detail boundary from 512 to 600 pixels/second. Inspect the
            // synchronous draw before scheduling any new background source work.
            Draw(1024.0 / 600, snapshot, false);
            var transitioning = renderer.Diagnostics;
            if (transitioning.ResolutionFallbackHitCount <= warm.ResolutionFallbackHitCount ||
                transitioning.PeakQueryCount != warm.PeakQueryCount || transitioning.FallbackTileCount != warm.FallbackTileCount)
                throw new InvalidOperationException($"Smoke test: {mode} detail change flashed coarse geometry or queried source data on the UI thread.");
            renderer.EndFrame(); await Settle(); Draw(1024.0 / 600, snapshot);
            if (renderer.Diagnostics.RetainedBytes > 128L * 1024 * 1024)
                throw new InvalidOperationException("Smoke test: resolution reuse exceeded the waveform cache budget.");

            if (mode == WaveformViewMode.Original) continue;
            var changedSource = new WaveformTileSource(new object(), processedPyramid, [new(-.7f, .7f)], 512, 2);
            var changed = snapshot with { Processed = changedSource };
            long fallbacks = renderer.Diagnostics.ResolutionFallbackHitCount;
            Draw(1024.0 / 700, changed, false);
            if (renderer.Diagnostics.ResolutionFallbackHitCount != fallbacks)
                throw new InvalidOperationException("Smoke test: a changed preview reused stale resolution images.");
        }
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyColdZoomContinuitySmokeAsync()
    {
        var samples = Enumerable.Range(0, 32768).Select(index =>
            (index % 2 == 0 ? -1f : 1f) * ((index / 2) % 128 < 16 ? .9f : .2f)).ToArray();
        var processed = samples.Select(sample => sample * .7f).ToArray();
        var originalPyramid = WaveformPyramid.Build(samples, 2);
        var processedPyramid = WaveformPyramid.Build(processed, 2);
        var original = new WaveformTileSource(originalPyramid, originalPyramid, [new(-.9f, .9f)], 2048, 8);
        var preview = new WaveformTileSource(processedPyramid, processedPyramid, [new(-.63f, .63f)], 2048, 8);
        var snapshot = new WaveformTileTrack(Guid.NewGuid(), original, preview);
        var style = new WaveformTileStyle(Brushes.SeaGreen, Brushes.DodgerBlue, Brushes.Coral, Brushes.LightGray);
        var area = new Rect(0, 0, 1024, 128);
        const double centerTime = 3.1 + (1024.0 / 600) / 2;
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            using var renderer = new WaveformTileRenderer(Dispatcher);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
            void Draw(double scale, WaveformTileTrack track, WaveformTileStyle palette, bool finish = true)
            {
                var duration = area.Width / scale;
                using var dc = visual.RenderOpen();
                renderer.BeginFrame();
                renderer.DrawTrack(dc, track, centerTime - duration / 2, duration, area, 0, 64, 50, mode, palette);
                if (finish) renderer.EndFrame();
            }
            async Task Settle()
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (renderer.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(5);
                if (renderer.IsBusy) throw new InvalidOperationException("Smoke test: cold zoom refinement did not settle.");
            }
            Draw(600, snapshot, style); await Settle(); Draw(600, snapshot, style);
            // 600 -> 512 is a cold zoom-out bucket. Its first target tile starts
            // before the old cached range, while its entire visible core is covered.
            // The later jumps cross fresh buckets in both directions; 1500 -> 8000
            // also exercises the bounded last-ready resolution hint.
            foreach (var scale in new[] { 512.0, 900, 1500, 8000, 3500, 1200, 300 })
            {
                var before = renderer.Diagnostics;
                Draw(scale, snapshot, style, finish: false);
                var cold = renderer.Diagnostics;
                if (cold.FallbackTileCount != before.FallbackTileCount || cold.PeakQueryCount != before.PeakQueryCount ||
                    cold.ResolutionFallbackHitCount <= before.ResolutionFallbackHitCount || cold.MaxResolutionFallbackImagesPerTile > 3)
                    throw new InvalidOperationException($"Smoke test: {mode} cold zoom at {scale} px/s flashed sparse geometry, queried sources, or exceeded its image-quad bound.");
                var image = new RenderTargetBitmap(1024, 128, 96, 96, PixelFormats.Pbgra32);
                image.Render(visual);
                var pixels = ComparisonPixels(image);
                if (Enumerable.Range(0, 8).Any(index => (pixels[64 * 1024 + 32 + index * 128] >> 24) == 0))
                    throw new InvalidOperationException("Smoke test: a cold zoom left a blank visible waveform region.");
                renderer.EndFrame(); await Settle(); Draw(scale, snapshot, style);
                var refined = renderer.Diagnostics;
                if (refined.RasterBuildCount <= before.RasterBuildCount || refined.RetainedBytes > 128L * 1024 * 1024)
                    throw new InvalidOperationException("Smoke test: cold zoom failed to refine or exceeded the tile cache budget.");
                for (var frame = 0; frame < 6; frame++) Draw(scale, snapshot, style);
                var warm = renderer.Diagnostics;
                if (warm.TileBuildCount != refined.TileBuildCount || warm.RasterBuildCount != refined.RasterBuildCount ||
                    warm.PeakQueryCount != refined.PeakQueryCount || warm.FallbackTileCount != refined.FallbackTileCount || warm.RasterHitCount <= refined.RasterHitCount)
                    throw new InvalidOperationException("Smoke test: warm zoom frames rebuilt detail instead of reusing cached image quads.");
            }

            var changedOriginal = new WaveformTileSource(new object(), originalPyramid, [new(-.9f, .9f)], 2048, 8);
            var changedPreview = new WaveformTileSource(new object(), processedPyramid, [new(-.63f, .63f)], 2048, 8);
            var changed = snapshot with { Original = changedOriginal, Processed = changedPreview };
            var oldSource = renderer.Diagnostics;
            Draw(700, changed, style, finish: false);
            if (renderer.Diagnostics.ResolutionFallbackHitCount != oldSource.ResolutionFallbackHitCount)
                throw new InvalidOperationException("Smoke test: cold zoom reused image detail from a different audio/gain source.");
            renderer.EndFrame(); await Settle(); Draw(700, changed, style);
            var oldPalette = renderer.Diagnostics;
            Draw(700, changed, style with { Fill = Brushes.MediumPurple }, finish: false);
            if (renderer.Diagnostics.ResolutionFallbackHitCount != oldPalette.ResolutionFallbackHitCount || renderer.Diagnostics.RasterHitCount != oldPalette.RasterHitCount)
                throw new InvalidOperationException("Smoke test: cold zoom reused an image with a mismatched palette.");
            renderer.EndFrame(); await Settle();
            await VerifyTilePublicationDuringFrameSmokeAsync(mode, style, area);
        }
    }

    private async Task VerifyTilePublicationDuringFrameSmokeAsync(WaveformViewMode mode, WaveformTileStyle style, Rect area)
    {
        using var peaks = new PublicationGatePeaks(Dispatcher);
        using var renderer = new WaveformTileRenderer(Dispatcher);
        var source = new WaveformTileSource(peaks, null, peaks, 1024, 2);
        var track = new WaveformTileTrack(Guid.NewGuid(), source, source);
        var visual = new DrawingVisual();
        void Draw(bool begin, bool finish)
        {
            using var dc = visual.RenderOpen();
            if (begin) renderer.BeginFrame();
            renderer.DrawTrack(dc, track, 0, 2, area, 0, 64, 50, mode, style);
            if (finish) renderer.EndFrame();
        }
        try
        {
            Draw(begin: true, finish: true);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!peaks.Entered.IsSet && DateTime.UtcNow < deadline) await Task.Delay(5);
            if (!peaks.Entered.IsSet) throw new InvalidOperationException("Smoke test: tile-publication gate was not reached.");
            // Force the old race deterministically: workers finish after a new
            // BeginFrame but before that frame re-registers the still-needed track.
            renderer.BeginFrame(); peaks.Release.Set();
            while (renderer.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(5);
            if (renderer.IsBusy) throw new InvalidOperationException("Smoke test: gated waveform work did not settle.");
            var ready = renderer.Diagnostics;
            Draw(begin: false, finish: false);
            var reused = renderer.Diagnostics;
            if (reused.RasterHitCount <= ready.RasterHitCount || reused.FallbackTileCount != ready.FallbackTileCount || reused.PeakQueryCount != ready.PeakQueryCount)
                throw new InvalidOperationException("Smoke test: a still-needed tile was discarded while the next frame was being assembled.");
            renderer.EndFrame();
        }
        finally { peaks.Release.Set(); }
    }

    private sealed class PublicationGatePeaks(Dispatcher dispatcher) : IReadOnlyList<PeakBin>, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        public int Count => 1024;
        public PeakBin this[int index]
        {
            get
            {
                if (!dispatcher.CheckAccess())
                {
                    Entered.Set();
                    if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Gated waveform source was not released.");
                }
                return new(-.5f, .5f);
            }
        }
        public IEnumerator<PeakBin> GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
}

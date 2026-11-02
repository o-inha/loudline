using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyComparisonRasterSmokeAsync()
    {
        var original = Enumerable.Repeat(new WaveformExtrema(-.4f, .4f), 9).ToArray();
        var processed = Enumerable.Range(0, 9).Select(i => i < 4
            ? new WaveformExtrema(-.7f, .7f) : new WaveformExtrema(-.15f, .15f)).ToArray();
        var fixture = new ComparisonTileSamples(0, 1, 128, original, processed);
        var spec = new ComparisonRasterSpec(128, 1024, .5, .4,
            0xA800C850, 0xE60078FF, 0xE6FF4164, 0xFFE6E6E6);
        var raster = await Task.Run(() => ComparisonTileRasterizer.Build(fixture, spec, CancellationToken.None));
        if (!raster.Image.IsFrozen) throw new InvalidOperationException("Raster smoke: a worker published a mutable bitmap.");
        // Access on the UI thread and another worker verifies the frozen handoff, rather than
        // merely checking a flag on the creating thread.
        var pixels = ComparisonPixels(raster.Image);
        var otherThreadPixels = await Task.Run(() => ComparisonPixels(raster.Image));
        if (!pixels.SequenceEqual(otherThreadPixels)) throw new InvalidOperationException("Raster smoke: frozen pixels changed across threads.");
        var stride = raster.Image.PixelWidth;
        var blue = pixels[36 * stride + 19];
        var coral = pixels[49 * stride + 115];
        if ((blue & 255) < ((blue >> 16) & 255) + 80 || (blue & 255) < ((blue >> 8) & 255) + 40)
            throw new InvalidOperationException("Raster smoke: a louder preview lost its blue difference region.");
        if (((coral >> 16) & 255) < (coral & 255) + 70)
            throw new InvalidOperationException("Raster smoke: a quieter preview lost its coral difference region.");
        var fill = pixels[64 * stride + 19];
        if (Math.Abs((fill >> 24) - 168L) > 1)
            throw new InvalidOperationException("Raster smoke: translucent processed fill changed opacity.");
        var edge = pixels[43 * stride + 19];
        if (((edge >> 16) & 255) < 160 || Math.Abs((long)((edge >> 16) & 255) - ((edge >> 8) & 255)) > 10)
            throw new InvalidOperationException("Raster smoke: a boost covered the original contour.");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await Task.Run(() => ComparisonTileRasterizer.Build(fixture, spec, cancellation.Token));
                throw new InvalidOperationException("Raster smoke: canceled work produced a bitmap.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        foreach (var dpi in new[] { 1.0, 1.5, 2.0 }) await VerifyComparisonRasterSeamAsync(dpi);
    }

    private async Task VerifyComparisonRasterSeamAsync(double dpi)
    {
        // Both channels have constant, opposite extrema. The real renderer must therefore draw
        // a completely flat translucent plate across its internal 512-column tile boundary.
        var original = new float[2048];
        var processed = new float[2048];
        for (var i = 0; i < 1024; i++)
        {
            original[i * 2] = -.4f; original[i * 2 + 1] = .4f;
            processed[i * 2] = -.7f; processed[i * 2 + 1] = .7f;
        }
        var originalPyramid = WaveformPyramid.Build(original, 2);
        var processedPyramid = WaveformPyramid.Build(processed, 2);
        var source = new WaveformTileSource(originalPyramid, originalPyramid, [new(-.4f, .4f)], 512, 2);
        var preview = new WaveformTileSource(processedPyramid, processedPyramid, [new(-.7f, .7f)], 512, 2);
        var snapshot = new WaveformTileTrack(Guid.NewGuid(), source, preview);
        var style = new WaveformTileStyle(new SolidColorBrush(Color.FromRgb(0, 200, 80)),
            new SolidColorBrush(Color.FromRgb(0, 120, 255)), new SolidColorBrush(Color.FromRgb(255, 65, 100)),
            new SolidColorBrush(Color.FromRgb(230, 230, 230)));
        using var renderer = new WaveformTileRenderer(Dispatcher);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
        var area = new Rect(.37, 0, 1024, 128);
        void Draw()
        {
            using var dc = visual.RenderOpen();
            renderer.BeginFrame();
            renderer.DrawTrack(dc, snapshot, 0, 2, area, 0, 64, 51.2, WaveformViewMode.Compare, style, dpi);
            renderer.EndFrame();
        }
        Draw();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (renderer.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Draw();
        if (renderer.IsBusy || renderer.Diagnostics.ComparisonRasterBuildCount != 2 || renderer.Diagnostics.ComparisonRasterHitCount < 2)
            throw new InvalidOperationException($"Raster smoke: {dpi}× comparison tiles did not finish.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(1026 * dpi), (int)Math.Ceiling(128 * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = ComparisonPixels(bitmap);
        var seam = (int)Math.Round((area.Left + 512) * dpi);
        foreach (var logicalY in new[] { 4.0, 36.0, 64.0 })
        {
            var row = Math.Min(bitmap.PixelHeight - 1, (int)Math.Round(logicalY * dpi));
            var expected = pixels[row * bitmap.PixelWidth + seam - 16];
            if (logicalY == 4 && expected != 0 || logicalY == 64 && Math.Abs((expected >> 24) - 168L) > 1)
                throw new InvalidOperationException($"Raster smoke: {dpi}× transparent plate has unexpected alpha.");
            for (var x = seam - 8; x <= seam + 8; x++)
            {
                var actual = pixels[row * bitmap.PixelWidth + x];
                for (var channel = 0; channel < 4; channel++)
                    if (Math.Abs((long)((actual >> (channel * 8)) & 255) - ((expected >> (channel * 8)) & 255)) > 3)
                        throw new InvalidOperationException($"Raster smoke: {dpi}× tile seam changed color/alpha at row {row}, x {x}.");
            }
        }
        var before = renderer.Diagnostics;
        Draw(); Draw();
        var after = renderer.Diagnostics;
        if (after.ComparisonRasterBuildCount != before.ComparisonRasterBuildCount || after.PeakQueryCount != before.PeakQueryCount ||
            after.ComparisonRasterHitCount <= before.ComparisonRasterHitCount || after.RetainedBytes > 128L * 1024 * 1024)
            throw new InvalidOperationException("Raster smoke: a warm comparison rebuilt data or exceeded its cache budget.");
    }

    private static uint[] ComparisonPixels(BitmapSource image)
    {
        var pixels = new uint[checked(image.PixelWidth * image.PixelHeight)];
        image.CopyPixels(pixels, checked(image.PixelWidth * 4), 0);
        return pixels;
    }
}

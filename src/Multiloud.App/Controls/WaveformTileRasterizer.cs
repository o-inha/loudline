using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Multiloud.App.Controls;

/// <summary>
/// Background-only rasterization for Original and Preview. Its fill interpolation, padding,
/// coordinate bounds, and premultiplied alpha match the processed fill in Compare, without
/// allocating comparison bands or an original-contour coverage mask.
/// </summary>
internal static class WaveformTileRasterizer
{
    public static ComparisonTileImage BuildFill(ComparisonTileSamples samples, ComparisonRasterSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dpi = spec.Dpi;
        var padding = (int)Math.Ceiling(dpi * 2) + 1;
        var widthInColumns = (samples.End - samples.Start) * samples.PixelsPerSecond;
        var width = Math.Max(1, (int)Math.Ceiling(widthInColumns * dpi)) + 2 * padding;
        var height = spec.PixelHeight;
        var pixels = new uint[checked(width * height)];
        var fill = Premultiply(spec.Fill);
        var columns = samples.Processed.Length - 1;
        double Y(double value) => height * (spec.CenterRatio - value * spec.AmplitudeRatio);

        for (var x = 0; x < width; x++)
        {
            if ((x & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var at = Math.Clamp((x + .5 - padding) / dpi / Math.Max(.000001, widthInColumns) * columns, 0, columns);
            var index = Math.Min(columns - 1, (int)at);
            var mix = Math.Clamp(at - index, 0, 1);
            var processed = samples.Processed[index];
            var next = samples.Processed[index + 1];
            var top = Y(processed.Max + (next.Max - processed.Max) * mix);
            var bottom = Y(processed.Min + (next.Min - processed.Min) * mix);
            FillSpan(pixels, width, height, x, top, bottom, fill);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        var bounds = new Rect(samples.Start - padding / (samples.PixelsPerSecond * dpi),
            -spec.CenterRatio / spec.AmplitudeRatio, width / (samples.PixelsPerSecond * dpi), 1 / spec.AmplitudeRatio);
        return new ComparisonTileImage(bitmap, bounds, spec, 4096L + (long)width * height * 8);
    }

    private static uint Premultiply(uint color)
    {
        var a = color >> 24;
        var r = ((color >> 16) & 255) * a / 255;
        var g = ((color >> 8) & 255) * a / 255;
        var b = (color & 255) * a / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint Coverage(uint color, double coverage)
    {
        var weight = (uint)Math.Clamp((int)Math.Round(coverage * 255), 0, 255);
        var a = (color >> 24) * weight / 255;
        var r = ((color >> 16) & 255) * weight / 255;
        var g = ((color >> 8) & 255) * weight / 255;
        var b = (color & 255) * weight / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static void FillSpan(uint[] pixels, int width, int height, int x, double from, double to, uint color)
    {
        var top = Math.Clamp(Math.Min(from, to), 0, height);
        var bottom = Math.Clamp(Math.Max(from, to), 0, height);
        if (bottom - top < .000001) return;
        var first = (int)Math.Floor(top);
        var last = Math.Min(height, (int)Math.Ceiling(bottom));
        for (var y = first; y < last; y++)
        {
            var coverage = Math.Min(y + 1, bottom) - Math.Max(y, top);
            pixels[y * width + x] = coverage >= .999999 ? color : Coverage(color, coverage);
        }
    }
}

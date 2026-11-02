using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.Core;

namespace Multiloud.App.Controls;

internal readonly record struct ComparisonRasterSpec(int PixelHeight, int Dpi1024, double CenterRatio,
    double AmplitudeRatio, uint Fill, uint Boost, uint Cut, uint Outline)
{
    public double Dpi => Dpi1024 / 1024.0;
    public bool SamePalette(ComparisonRasterSpec other) => Fill == other.Fill && Boost == other.Boost && Cut == other.Cut && Outline == other.Outline;
}

internal sealed record ComparisonTileSamples(double Start, double End, double PixelsPerSecond,
    WaveformExtrema[] Original, WaveformExtrema[] Processed);
internal sealed record ComparisonTileImage(BitmapSource Image, Rect LocalBounds, ComparisonRasterSpec Spec, long Bytes);

/// <summary>
/// Builds a transparent comparison texture on the tile workers. This deliberately uses no
/// Visual, Dispatcher, RenderTargetBitmap, or UI-thread rasterization. The frozen result is a
/// single image quad during camera motion; the CPU never flattens/strokes its contours again.
/// </summary>
internal static class ComparisonTileRasterizer
{
    public static ComparisonTileImage Build(ComparisonTileSamples samples, ComparisonRasterSpec spec, CancellationToken cancellationToken)
    {
        var dpi = spec.Dpi;
        var padding = (int)Math.Ceiling(dpi * 2) + 1;
        var widthInColumns = (samples.End - samples.Start) * samples.PixelsPerSecond;
        var width = Math.Max(1, (int)Math.Ceiling(widthInColumns * dpi)) + 2 * padding;
        var height = spec.PixelHeight;
        var pixels = new uint[checked(width * height)];
        var fill = Premultiply(spec.Fill);
        var boost = Premultiply(spec.Boost);
        var cut = Premultiply(spec.Cut);
        var outline = Premultiply(spec.Outline);
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
            Span(pixels, width, height, x, top, bottom, fill, true);

            // The difference bands use the same left-column extrema as the vector path.
            // Their color denotes which boundary extends farther from the common zero line.
            var original = samples.Original[index];
            Span(pixels, width, height, x, Y(original.Max), Y(processed.Max), processed.Max > original.Max ? boost : cut);
            Span(pixels, width, height, x, Y(original.Min), Y(processed.Min), processed.Min < original.Min ? boost : cut);
        }

        // Paint the original last, so boosts cannot cover its contour. Distance coverage keeps
        // fractional-pixel and steep contour segments continuous at every display scale.
        var radius = Math.Max(.5, dpi * .5);
        var contourCoverage = new byte[pixels.Length];
        for (var i = 0; i < columns; i++)
        {
            if ((i & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var x0 = padding + i / (double)columns * widthInColumns * dpi;
            var x1 = padding + (i + 1) / (double)columns * widthInColumns * dpi;
            Line(contourCoverage, width, height, x0, Y(samples.Original[i].Max), x1, Y(samples.Original[i + 1].Max), radius);
            Line(contourCoverage, width, height, x0, Y(samples.Original[i].Min), x1, Y(samples.Original[i + 1].Min), radius);
        }
        // Stroke coverage is a union: shared segment ends and a silent upper/lower contour
        // must not darken merely because more than one segment covers the same pixel.
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var at = y * width + x;
                var coverage = contourCoverage[at];
                if (coverage != 0) Over(ref pixels[at], coverage == 255 ? outline : Coverage(outline, coverage / 255.0));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        var bounds = new Rect(samples.Start - padding / (samples.PixelsPerSecond * dpi),
            -spec.CenterRatio / spec.AmplitudeRatio, width / (samples.PixelsPerSecond * dpi), 1 / spec.AmplitudeRatio);
        // Count native bitmap storage and an allowance for its uploaded GPU copy. The temporary
        // managed build buffer is bounded by the two workers and is not retained by this object.
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

    private static void Over(ref uint destination, uint source)
    {
        var inverse = 255 - (source >> 24);
        if (inverse == 0 || destination == 0) { destination = source; return; }
        var a = (source >> 24) + ((destination >> 24) * inverse + 127) / 255;
        var r = ((source >> 16) & 255) + (((destination >> 16) & 255) * inverse + 127) / 255;
        var g = ((source >> 8) & 255) + (((destination >> 8) & 255) * inverse + 127) / 255;
        var b = (source & 255) + ((destination & 255) * inverse + 127) / 255;
        destination = (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static void Span(uint[] pixels, int width, int height, int x, double from, double to, uint color, bool overwrite = false)
    {
        var top = Math.Clamp(Math.Min(from, to), 0, height);
        var bottom = Math.Clamp(Math.Max(from, to), 0, height);
        if (bottom - top < .000001) return;
        var first = (int)Math.Floor(top);
        var last = Math.Min(height, (int)Math.Ceiling(bottom));
        for (var y = first; y < last; y++)
        {
            var coverage = Math.Min(y + 1, bottom) - Math.Max(y, top);
            var value = coverage >= .999999 ? color : Coverage(color, coverage);
            if (overwrite) pixels[y * width + x] = value;
            else Over(ref pixels[y * width + x], value);
        }
    }

    private static void Line(byte[] mask, int width, int height, double x0, double y0, double x1, double y1, double radius)
    {
        var firstX = (int)Math.Clamp(Math.Floor(Math.Min(x0, x1) - radius - .5), 0, width);
        var lastX = (int)Math.Clamp(Math.Ceiling(Math.Max(x0, x1) + radius + .5), 0, width);
        var firstY = (int)Math.Clamp(Math.Floor(Math.Min(y0, y1) - radius - .5), 0, height);
        var lastY = (int)Math.Clamp(Math.Ceiling(Math.Max(y0, y1) + radius + .5), 0, height);
        var dx = x1 - x0; var dy = y1 - y0;
        var length = Math.Max(.000000001, dx * dx + dy * dy);
        for (var y = firstY; y < lastY; y++)
            for (var x = firstX; x < lastX; x++)
            {
                var at = y * width + x;
                if (mask[at] == 255) continue;
                var along = Math.Clamp(((x + .5 - x0) * dx + (y + .5 - y0) * dy) / length, 0, 1);
                var px = x + .5 - (x0 + along * dx);
                var py = y + .5 - (y0 + along * dy);
                var coverage = Math.Clamp(radius + .5 - Math.Sqrt(px * px + py * py), 0, 1);
                var value = (byte)Math.Clamp((int)Math.Round(coverage * 255), 0, 255);
                if (value > mask[at]) mask[at] = value;
            }
    }
}

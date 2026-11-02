using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Multiloud.App.Controls;

/// <summary>Labels the existing linear-amplitude waveform and dB gain envelope.</summary>
internal static class TimelineScales
{
    private const double GutterWidth = 36;
    private const double LabelGap = 16;
    private static readonly double[] WaveformLevels = [0, -3, -6, -12, -18, -24, -30, -36, -48];
    private static readonly double[] EnvelopeLevels = [3, 6, 12, 18, 24];
    private readonly record struct Tick(double Y, string Label, int Priority);
    private readonly record struct Label(Tick Tick, FormattedText Text, double Top);

    /// <summary>
    /// Draws only outside plot. The text factory supplies cached 10-DIP labels;
    /// returned FormattedText objects are never modified by this helper.
    /// </summary>
    internal static void Draw(DrawingContext dc, Rect plot, Rect envelopeArea, double envelopeRange,
        bool showEnvelope, Brush background, Brush muted, Brush border, Brush gold,
        Func<string, Brush, FormattedText> text)
    {
        if (plot.IsEmpty || plot.Height <= 0 || !double.IsFinite(plot.Top + plot.Right + plot.Height)) return;
        double center = TimelineLayout.CenterY(plot);
        var rightTicks = new List<Tick>(19) { new(center, "−∞", 0) };
        for (int index = 0; index < WaveformLevels.Length; index++)
        {
            double db = WaveformLevels[index];
            double magnitude = Math.Pow(10, db / 20);
            string label = db == 0 ? "0" : "−" + (-db).ToString("0", CultureInfo.InvariantCulture);
            int priority = db == 0 ? 0 : index + 1;
            rightTicks.Add(new(TimelineLayout.WaveformY(plot, magnitude), label, priority));
            rightTicks.Add(new(TimelineLayout.WaveformY(plot, -magnitude), label, priority));
        }
        DrawAxis(dc, plot, rightTicks, "dBFS", false, background, muted, border, text);

        // Clearing the left gutter also removes the axis when the envelope is hidden.
        dc.DrawRectangle(background, null, new Rect(plot.Left - GutterWidth, plot.Top, GutterWidth, plot.Height));
        if (!showEnvelope || envelopeArea.IsEmpty || !double.IsFinite(envelopeRange) || envelopeRange <= 0) return;
        var leftTicks = new List<Tick>(13);
        void AddGain(double db, int priority)
        {
            double y = TimelineLayout.GainY(envelopeArea, db, envelopeRange);
            string label = db == 0 ? "0" : (db > 0 ? "+" : "−") + Math.Abs(db).ToString("0.#", CultureInfo.InvariantCulture);
            leftTicks.Add(new(y, label, priority));
        }
        AddGain(0, 0);
        AddGain(envelopeRange, 1);
        AddGain(-envelopeRange, 1);
        for (int index = 0; index < EnvelopeLevels.Length; index++)
        {
            double db = EnvelopeLevels[index];
            if (db >= envelopeRange) continue; // Range endpoints are already present.
            AddGain(db, index + 2);
            AddGain(-db, index + 2);
        }
        DrawAxis(dc, plot, leftTicks, "dB", true, background, gold, gold, text);
    }

    private static void DrawAxis(DrawingContext dc, Rect plot, List<Tick> ticks, string heading,
        bool left, Brush background, Brush foreground, Brush tickBrush, Func<string, Brush, FormattedText> text)
    {
        var headingText = text(heading, foreground);
        double headingTop = plot.Top + 4;
        // Keep both 0 dBFS labels even in a very short viewport. Its header can
        // occupy the empty ruler gutter instead of colliding with the top label.
        double firstPriorityTop = ticks.Where(tick => tick.Priority == 0)
            .Min(tick => tick.Y - text(tick.Label, foreground).Height * .5);
        if (firstPriorityTop < headingTop + headingText.Height + 2)
            headingTop = plot.Top - headingText.Height - 3;
        // Full scale is close to the signal edge. Let labels extend half their
        // height into the outside gutter so small windows keep both 0 dBFS ticks;
        // this does not reserve any signal space or draw over its filename strip.
        var halfLabelHeight = ticks.Max(tick => text(tick.Label, foreground).Height * .5);
        var gutterTop = Math.Min(plot.Top - halfLabelHeight, headingTop);
        var gutter = new Rect(left ? plot.Left - GutterWidth : plot.Right,
            gutterTop, GutterWidth, plot.Bottom + halfLabelHeight - gutterTop);
        dc.PushClip(new RectangleGeometry(gutter));
        dc.DrawRectangle(background, null, gutter);

        double headingX = left ? plot.Left - 8 - headingText.WidthIncludingTrailingWhitespace : plot.Right + 7;
        dc.DrawText(headingText, new Point(headingX, headingTop));
        var selected = new List<Label>(ticks.Count);
        foreach (var tick in ticks.OrderBy(tick => tick.Priority).ThenBy(tick => tick.Y))
        {
            var labelText = text(tick.Label, foreground);
            double top = tick.Y - labelText.Height * .5;
            if (top < gutter.Top || top + labelText.Height > gutter.Bottom) continue;
            bool collides = selected.Any(existing =>
                Math.Abs(existing.Tick.Y - tick.Y) < Math.Max(LabelGap, (existing.Text.Height + labelText.Height) * .5 + 2));
            if (collides) continue;
            if (tick.Priority > 0 && top < headingTop + headingText.Height + 2 && top + labelText.Height > headingTop - 2) continue;
            selected.Add(new(tick, labelText, top));
        }

        var pen = new Pen(tickBrush, 1);
        foreach (var label in selected)
        {
            double edge = left ? plot.Left - .5 : plot.Right + .5;
            dc.DrawLine(pen, new Point(edge, label.Tick.Y), new Point(edge + (left ? -4 : 4), label.Tick.Y));
            double x = left ? plot.Left - 8 - label.Text.WidthIncludingTrailingWhitespace : plot.Right + 7;
            dc.DrawText(label.Text, new Point(x, label.Top));
        }
        dc.Pop();
    }
}

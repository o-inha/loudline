using System.Windows;
using System.Windows.Media;
using Multiloud.Core;

namespace Multiloud.App.Controls;

public sealed partial class TimelineView
{
    private static int EnvelopeLowerBound(IReadOnlyList<EnvelopePoint> points, double time, bool strictlyAfter)
    {
        int low = 0, high = points.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (strictlyAfter ? points[middle].Time <= time : points[middle].Time < time) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private bool GainLabelOverlapsEnvelope(AudioTrack track, double offset, Rect label)
    {
        int first = EnvelopeLowerBound(track.Envelope, T(label.Left - 7) - offset, false);
        for (int index = first; index < track.Envelope.Count; index++)
        {
            var point = track.Envelope[index];
            double x = X(offset + point.Time);
            if (x > label.Right + 7) break;
            if (x >= label.Left - 7 && Math.Abs(EnvelopeY(point.GainDb + TransitionDesigner.GainAt(track, point.Time), true) -
                (label.Top + label.Height / 2)) < 14) return true;
        }
        return false;
    }

    private StreamGeometry VisibleEnvelopeGeometry(IReadOnlyList<EnvelopePoint> points, double offset,
        double left, double right, bool waveform)
    {
        var geometry = new StreamGeometry();
        if (points.Count == 0 || right <= left) return geometry;
        var firstTime = T(left) - offset;
        var lastTime = T(right) - offset;
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(left, EnvelopeY(EnvelopeAt(points, firstTime), waveform)), false, false);
            if (_navigationActive) _navigationEnvelopeVertices++;
            void Emit(EnvelopePoint point)
            {
                drawing.LineTo(new Point(X(offset + point.Time), EnvelopeY(point.GainDb, waveform)), true, false);
                if (_navigationActive) _navigationEnvelopeVertices++;
            }
            // Binary seeking avoids revisiting the preceding hour of knots when
            // only a small section is visible. Interpolated viewport endpoints
            // also keep enormous off-screen coordinates out of WPF geometry.
            int index = EnvelopeLowerBound(points, firstTime, true);
            if (!_navigationActive)
            {
                for (; index < points.Count && points[index].Time < lastTime; index++) Emit(points[index]);
            }
            else
            {
                int bucket = int.MinValue, minimum = -1, maximum = -1, last = -1;
                void FlushBucket()
                {
                    if (last < 0) return;
                    // Preserve both dB extrema in their original time order. A
                    // dense cluster remains visible without thousands of nearly
                    // coincident vertices during the brief camera animation.
                    int first = Math.Min(minimum, maximum), second = Math.Max(minimum, maximum);
                    Emit(points[first]);
                    if (second != first) Emit(points[second]);
                    if (last != second) Emit(points[last]);
                }
                for (; index < points.Count && points[index].Time < lastTime; index++)
                {
                    int nextBucket = (int)Math.Floor((X(offset + points[index].Time) - left) / 2);
                    if (nextBucket != bucket)
                    {
                        FlushBucket(); bucket = nextBucket; minimum = maximum = last = index;
                    }
                    else
                    {
                        if (points[index].GainDb < points[minimum].GainDb) minimum = index;
                        if (points[index].GainDb > points[maximum].GainDb) maximum = index;
                        last = index;
                    }
                }
                FlushBucket();
            }
            drawing.LineTo(new Point(right, EnvelopeY(EnvelopeAt(points, lastTime), waveform)), true, false);
            if (_navigationActive) _navigationEnvelopeVertices++;
        }
        geometry.Freeze();
        return geometry;
    }
}

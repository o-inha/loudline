using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Multiloud.App.Controls;

public sealed partial class TimelineView
{
    private const double BoundaryBarHeight = 24;
    private readonly DrawingVisual _customBoundaryVisual = new();
    private List<double> _customTrackBoundaries = [];
    private bool _customBoundariesVisible, _customBoundariesDirty = true;
    private int _dragBoundaryIndex = -1;
    private Point _boundaryPointerOrigin;
    private double _boundaryOriginalTime;
    private bool _boundaryWasAdded, _boundaryMoved, _boundaryEditStarted;

    /// <summary>Absolute project positions; the implicit start/end are not authored markers.</summary>
    public List<double> CustomTrackBoundaries
    {
        get => _customTrackBoundaries;
        set
        {
            CompleteCustomBoundaryInteraction(allowClickRemoval: false);
            _customTrackBoundaries = value ?? [];
            InvalidateCustomBoundaries();
        }
    }

    public bool CustomBoundariesVisible
    {
        get => _customBoundariesVisible;
        set
        {
            if (_customBoundariesVisible == value) return;
            CompleteCustomBoundaryInteraction(allowClickRemoval: false);
            _customBoundariesVisible = value;
            // Only geometry changes. Neither the source descriptors nor the audio
            // processing metrics depend on export boundary visibility.
            InvalidateArrange(); InvalidateVisual(); InvalidateCamera();
        }
    }

    public event Action? CustomBoundaryEditStarted;
    public event Action? CustomBoundariesEdited;
    public event Action? CustomBoundaryEditCommitted;
    private double CustomBoundaryBarHeight => _customBoundariesVisible ? BoundaryBarHeight : 0;
    internal Rect CustomBoundaryBar => !_customBoundariesVisible ? Rect.Empty :
        new Rect(WaveArea.Left, Overview.Bottom + 5, WaveArea.Width, BoundaryBarHeight);
    internal Point CustomBoundaryMarkerPosition(int index) => new(X(_customTrackBoundaries[index]), CustomBoundaryBar.Top + BoundaryBarHeight * .5);
    internal bool IsEditingCustomBoundary => _dragBoundaryIndex >= 0;

    public void InvalidateCustomBoundaries()
    {
        _customBoundariesDirty = true;
        RequestFrame();
    }

    private void RenderCustomBoundaryLayer()
    {
        if (!_customBoundariesDirty) return;
        _customBoundariesDirty = false;
        using var dc = _customBoundaryVisual.RenderOpen();
        if (!_customBoundariesVisible) return;
        var bar = CustomBoundaryBar;
        dc.DrawRectangle(PanelBrush, null, bar);
        dc.DrawLine(new Pen(BorderBrush, 1), new Point(bar.Left, bar.Bottom), new Point(bar.Right, bar.Bottom));
        if (TotalDuration <= 0) return;
        dc.PushClip(new RectangleGeometry(bar));
        double previousX = double.NegativeInfinity;
        for (int index = FirstBoundaryAtOrAfter(_viewStart); index < _customTrackBoundaries.Count; index++)
        {
            double time = _customTrackBoundaries[index];
            if (time > End || time >= TotalDuration) break;
            if (time <= 0) continue;
            double x = X(time);
            // At album zoom nearby markers merge visually into a thin stem;
            // exact hit testing still resolves the nearest authored position.
            if (x - previousX < 2) continue;
            var pen = new Pen(AccentBrush, 1);
            dc.DrawLine(pen, new Point(x, bar.Top + 2), new Point(x, bar.Bottom - 2));
            var flag = new StreamGeometry();
            using (var figure = flag.Open())
            {
                figure.BeginFigure(new Point(x - 4, bar.Top + 3), true, true);
                figure.LineTo(new Point(x + 4, bar.Top + 3), true, false);
                figure.LineTo(new Point(x, bar.Top + 10), true, false);
            }
            flag.Freeze();
            dc.DrawGeometry(AccentBrush, null, flag);
            if (x - previousX >= 26)
                Text(dc, (index + 2).ToString("00"), 10, x + 5, bar.Top + 8, AccentBrush, 28);
            previousX = x;
        }
        dc.Pop();
    }

    private int FirstBoundaryAtOrAfter(double time)
    {
        int first = 0, end = _customTrackBoundaries.Count;
        while (first < end)
        {
            int middle = first + (end - first) / 2;
            if (_customTrackBoundaries[middle] < time) first = middle + 1; else end = middle;
        }
        return first;
    }

    private int HitCustomBoundary(Point point)
    {
        if (!CustomBoundaryBar.Contains(point) || TotalDuration <= 0) return -1;
        double time = T(point.X);
        int next = FirstBoundaryAtOrAfter(time), nearest = -1;
        double distance = 7;
        for (int index = Math.Max(0, next - 1); index <= Math.Min(_customTrackBoundaries.Count - 1, next); index++)
        {
            double marker = _customTrackBoundaries[index];
            if (marker <= 0 || marker >= TotalDuration) continue;
            double at = Math.Abs(X(marker) - point.X);
            if (at <= distance) { nearest = index; distance = at; }
        }
        return nearest;
    }

    internal bool HandleCustomBoundaryPointerDown(Point point)
    {
        if (!CustomBoundaryBar.Contains(point) || TotalDuration <= 0) return false;
        CompleteCustomBoundaryInteraction(allowClickRemoval: false);
        StopNavigation();
        _boundaryPointerOrigin = point;
        _boundaryMoved = _boundaryEditStarted = _boundaryWasAdded = false;
        _dragBoundaryIndex = HitCustomBoundary(point);
        if (_dragBoundaryIndex < 0)
        {
            double time = T(point.X);
            if (time <= 0 || time >= TotalDuration) return true;
            CustomBoundaryEditStarted?.Invoke();
            _boundaryEditStarted = _boundaryWasAdded = true;
            _dragBoundaryIndex = FirstBoundaryAtOrAfter(time);
            _customTrackBoundaries.Insert(_dragBoundaryIndex, time);
            CustomBoundariesEdited?.Invoke();
            InvalidateCustomBoundaries();
        }
        _boundaryOriginalTime = _customTrackBoundaries[_dragBoundaryIndex];
        Cursor = Cursors.SizeWE;
        return true;
    }

    internal bool HandleCustomBoundaryPointerMove(Point point)
    {
        if (_dragBoundaryIndex >= 0)
        {
            if (!_boundaryMoved && Math.Abs(point.X - _boundaryPointerOrigin.X) < 3 && Math.Abs(point.Y - _boundaryPointerOrigin.Y) < 3)
                return true;
            _boundaryMoved = true;
            double margin = Math.Min(.000001, TotalDuration / 4);
            double next = Clamp(_boundaryOriginalTime + (point.X - _boundaryPointerOrigin.X) / WaveArea.Width * ViewDuration,
                margin, Math.Max(margin, TotalDuration - margin));
            int neighbor = FirstBoundaryAtOrAfter(next);
            // Keep a small distinct interval if a drag lands exactly on another
            // marker, without deleting either marker or generating a zero file.
            if (neighbor < _customTrackBoundaries.Count && neighbor != _dragBoundaryIndex && Math.Abs(_customTrackBoundaries[neighbor] - next) < margin)
                next = Math.Max(margin, _customTrackBoundaries[neighbor] - margin);
            if (neighbor > 0 && neighbor - 1 != _dragBoundaryIndex && Math.Abs(_customTrackBoundaries[neighbor - 1] - next) < margin)
                next = Math.Min(TotalDuration - margin, _customTrackBoundaries[neighbor - 1] + margin);
            if (_customTrackBoundaries[_dragBoundaryIndex] != next)
            {
                if (!_boundaryEditStarted) { CustomBoundaryEditStarted?.Invoke(); _boundaryEditStarted = true; }
                _customTrackBoundaries.RemoveAt(_dragBoundaryIndex);
                _dragBoundaryIndex = FirstBoundaryAtOrAfter(next);
                _customTrackBoundaries.Insert(_dragBoundaryIndex, next);
                CustomBoundariesEdited?.Invoke();
                InvalidateCustomBoundaries();
            }
            Cursor = Cursors.SizeWE;
            return true;
        }
        if (!CustomBoundaryBar.Contains(point)) return false;
        Cursor = HitCustomBoundary(point) >= 0 ? Cursors.Hand : Cursors.Cross;
        return true;
    }

    internal bool CompleteCustomBoundaryInteraction(bool allowClickRemoval = true)
    {
        if (_dragBoundaryIndex < 0) return false;
        int index = _dragBoundaryIndex;
        bool remove = allowClickRemoval && !_boundaryMoved && !_boundaryWasAdded;
        bool edited = _boundaryEditStarted;
        // Clear before callbacks/capture release to prevent a reentrant second edit.
        _dragBoundaryIndex = -1;
        _boundaryMoved = _boundaryWasAdded = _boundaryEditStarted = false;
        if (remove && index < _customTrackBoundaries.Count)
        {
            CustomBoundaryEditStarted?.Invoke();
            _customTrackBoundaries.RemoveAt(index);
            CustomBoundariesEdited?.Invoke();
            edited = true;
        }
        if (edited) CustomBoundaryEditCommitted?.Invoke();
        InvalidateCustomBoundaries();
        if (IsMouseCaptured) ReleaseMouseCapture();
        return true;
    }
}

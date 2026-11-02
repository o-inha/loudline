using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Multiloud.Core;

namespace Multiloud.App.Controls;

public sealed partial class TimelineView
{
    private readonly DrawingVisual _loopBackgroundVisual = new();
    private PlaybackLoopState _loopState = PlaybackLoopState.Empty;
    private bool _loopBackgroundDirty = true;
    private LoopDragKind _loopDrag;
    private bool _loopDragB, _loopEditStarted;
    private double _loopOriginTime;
    private Point _loopPointerOrigin;
    private LoopRange? _loopOriginalRange;
    private enum LoopDragKind { None, PendingDraw, Draw, Left, Right }

    public PlaybackLoopState LoopState
    {
        get => _loopState;
        set
        {
            var next = value ?? PlaybackLoopState.Empty;
            if (_loopState == next) return;
            if (IsEditingLoop && (_loopState.AEnabled != next.AEnabled || _loopState.BEnabled != next.BEnabled))
                CompleteLoopInteraction(allowSeek: false);
            _loopState = next;
            _loopBackgroundDirty = _dynamicDirty = true;
            RequestFrame();
        }
    }

    public event Action? LoopEditStarted;
    public event Action<PlaybackLoopState>? LoopsEdited;
    public event Action? LoopEditCommitted;
    internal bool IsEditingLoop => _loopDrag != LoopDragKind.None;
    internal Rect LoopRulerBand => new(RulerArea.Left, RulerArea.Top + 1, RulerArea.Width, 8);
    private Brush LoopBlue => B("LoopABrush", "#65B7EF");
    private Brush LoopAmber => B("LoopBBrush", "#E5B759");

    private IEnumerable<(bool B, LoopRange Range)> VisibleLoops()
    {
        if (_loopState.AEnabled && ValidLoop(_loopState.A)) yield return (false, _loopState.A!);
        if (_loopState.BEnabled && ValidLoop(_loopState.B)) yield return (true, _loopState.B!);
    }

    private static bool ValidLoop(LoopRange? range) => range != null &&
        double.IsFinite(range.StartSeconds) && double.IsFinite(range.EndSeconds) &&
        range.StartSeconds >= 0 && range.EndSeconds > range.StartSeconds;

    private Rect LoopBand(bool b)
    {
        var band = LoopRulerBand;
        return _loopState.AEnabled && _loopState.BEnabled
            ? new Rect(band.Left, band.Top + (b ? 4 : 0), band.Width, 4) : band;
    }

    private void RenderLoopBackgroundLayer()
    {
        if (!_loopBackgroundDirty) return;
        _loopBackgroundDirty = false;
        using var dc = _loopBackgroundVisual.RenderOpen();
        if (TotalDuration <= 0) return;
        dc.PushClip(new RectangleGeometry(WaveArea));
        foreach (var (b, range) in VisibleLoops())
        {
            double left = Math.Max(WaveArea.Left, X(range.StartSeconds));
            double right = Math.Min(WaveArea.Right, X(Math.Min(TotalDuration, range.EndSeconds)));
            if (right <= left) continue;
            dc.PushOpacity(IsLightTheme ? .065 : .045);
            dc.DrawRectangle(b ? LoopAmber : LoopBlue, null, new Rect(left, WaveArea.Top, right - left, WaveArea.Height));
            dc.Pop();
        }
        dc.Pop();
    }

    private void DrawLoopRuler(DrawingContext dc)
    {
        if (TotalDuration <= 0) return;
        dc.PushClip(new RectangleGeometry(RulerArea));
        foreach (var (b, range) in VisibleLoops())
        {
            var band = LoopBand(b);
            double first = X(range.StartSeconds), last = X(range.EndSeconds);
            double left = Math.Max(WaveArea.Left, first), right = Math.Min(WaveArea.Right, last);
            if (right <= left) continue;
            var brush = b ? LoopAmber : LoopBlue;
            dc.DrawRectangle(brush, null, new Rect(left, band.Top, right - left, Math.Min(3, band.Height)));
            foreach (var (edge, isLeft) in new[] { (first, true), (last, false) })
            {
                if (edge < WaveArea.Left || edge > WaveArea.Right) continue;
                var handle = new StreamGeometry();
                using (var figure = handle.Open())
                {
                    figure.BeginFigure(new Point(edge, band.Top), true, true);
                    figure.LineTo(new Point(edge + (isLeft ? 6 : -6), band.Top), true, false);
                    figure.LineTo(new Point(edge, band.Bottom), true, false);
                }
                handle.Freeze();
                dc.DrawGeometry(brush, null, handle);
            }
        }
        dc.Pop();
    }

    internal Point? HoverCursorLinePosition
    {
        get
        {
            if (_drag != DragKind.None || IsEditingLoop || _valueDragTrack != null ||
                _dragBoundaryIndex >= 0 || _trackDragInProgress || _pendingTrackDrag != null || TotalDuration <= 0) return null;
            var pointer = _crosshairPointer ?? _trackHoverPointer;
            return pointer is { } point && (WaveArea.Contains(point) || RulerArea.Contains(point)) ? point : null;
        }
    }

    private void DrawHoverCursorLine(DrawingContext dc)
    {
        if (HoverCursorLinePosition is not { } point) return;
        dc.PushOpacity(.72);
        dc.DrawLine(new Pen(B("HoverCursorBrush", "#939BA4"), 1),
            new Point(point.X, WaveArea.Top), new Point(point.X, WaveArea.Bottom));
        dc.Pop();
    }

    internal Point LoopHandlePosition(bool b, bool right)
    {
        var range = b ? _loopState.B : _loopState.A;
        var band = LoopBand(b);
        return new Point(X(range == null ? 0 : right ? range.EndSeconds : range.StartSeconds), band.Top + band.Height * .5);
    }

    private (bool B, bool Right)? HitLoopHandle(Point point)
    {
        if (!RulerArea.Contains(point)) return null;
        (bool, bool)? nearest = null;
        double distance = double.PositiveInfinity;
        foreach (var (b, range) in VisibleLoops())
            foreach (bool right in new[] { false, true })
            {
                double time = right ? range.EndSeconds : range.StartSeconds;
                if (time > TotalDuration || time < _viewStart || time > End) continue;
                var handle = LoopHandlePosition(b, right);
                if (Math.Abs(point.X - handle.X) > 7 || Math.Abs(point.Y - handle.Y) > 9) continue;
                double d = (point - handle).LengthSquared;
                if (d < distance) { nearest = (b, right); distance = d; }
            }
        return nearest;
    }

    internal bool HandleLoopPointerDown(Point point)
    {
        if (!RulerArea.Contains(point) || TotalDuration <= 0) return false;
        var handle = HitLoopHandle(point);
        if (handle.HasValue)
        {
            _loopDragB = handle.Value.B;
            _loopDrag = handle.Value.Right ? LoopDragKind.Right : LoopDragKind.Left;
        }
        else if (_loopState.AEnabled != _loopState.BEnabled)
        {
            _loopDragB = _loopState.BEnabled;
            _loopDrag = LoopDragKind.PendingDraw;
        }
        else return false;
        StopNavigation();
        _loopPointerOrigin = point;
        _loopOriginTime = T(point.X);
        _loopOriginalRange = _loopDragB ? _loopState.B : _loopState.A;
        _loopEditStarted = false;
        _dynamicDirty = true; RequestFrame();
        Cursor = Cursors.SizeWE;
        return true;
    }

    internal bool HandleLoopPointerMove(Point point)
    {
        if (_loopDrag == LoopDragKind.None) return false;
        if (TotalDuration <= 0) { CompleteLoopInteraction(cancel: true, allowSeek: false); return true; }
        if (!_loopEditStarted && Math.Abs(point.X - _loopPointerOrigin.X) < 3) return true;
        double time = T(point.X), epsilon = 1.0 / 48000;
        LoopRange next;
        if (_loopDrag is LoopDragKind.PendingDraw or LoopDragKind.Draw)
        {
            double left = Math.Min(_loopOriginTime, time), right = Math.Max(_loopOriginTime, time);
            if (right - left < epsilon) return true;
            next = new LoopRange(left, right);
        }
        else if (_loopOriginalRange is { } original)
        {
            next = _loopDrag == LoopDragKind.Left
                ? original with { StartSeconds = Clamp(time, 0, Math.Max(0, Math.Min(TotalDuration, original.EndSeconds) - epsilon)) }
                : original with { EndSeconds = Clamp(time, Math.Min(TotalDuration, original.StartSeconds + epsilon), TotalDuration) };
        }
        else return true;
        var current = _loopDragB ? _loopState.B : _loopState.A;
        if (current == next) return true;
        if (!_loopEditStarted) { LoopEditStarted?.Invoke(); _loopEditStarted = true; }
        if (_loopDrag == LoopDragKind.PendingDraw) _loopDrag = LoopDragKind.Draw;
        LoopState = _loopDragB ? _loopState with { B = next } : _loopState with { A = next };
        LoopsEdited?.Invoke(_loopState);
        Cursor = Cursors.SizeWE;
        return true;
    }

    internal bool CompleteLoopInteraction(bool cancel = false, bool allowSeek = true)
    {
        if (_loopDrag == LoopDragKind.None) return false;
        bool click = _loopDrag == LoopDragKind.PendingDraw && !_loopEditStarted;
        bool edited = _loopEditStarted;
        var origin = _loopPointerOrigin;
        _loopDrag = LoopDragKind.None; _loopEditStarted = false;
        if (cancel && edited)
        {
            LoopState = _loopDragB ? _loopState with { B = _loopOriginalRange } : _loopState with { A = _loopOriginalRange };
            LoopsEdited?.Invoke(_loopState);
        }
        if (edited) LoopEditCommitted?.Invoke();
        _loopOriginalRange = null;
        if (click && allowSeek && !cancel) BeginSeek(origin);
        if (IsMouseCaptured) ReleaseMouseCapture();
        _dynamicDirty = true; RequestFrame();
        return true;
    }
}

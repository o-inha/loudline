using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Multiloud.Core;

namespace Multiloud.App.Controls;

/// <summary>A small, synchronized source lane. Editing never changes decoded audio.</summary>
public sealed class BackgroundLaneView : FrameworkElement, IDisposable
{
    private const double StartSnapPixels = 96;
    private BackgroundLayer _layer = new();
    private TimelineView? _timeline;
    private bool _timelineAttached, _scheduled, _waveDirty = true, _editorDirty = true, _dynamicDirty = true, _disposed;
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _waveVisual = new(), _editorVisual = new(), _dynamicVisual = new();
    private readonly WaveformTileRenderer _tiles;
    private readonly Dictionary<Guid, (AudioTrack Source, float[] Samples, WaveformPyramid? Pyramid, List<PeakBin> Peaks, WaveformTileTrack Snapshot)> _snapshots = new();
    private BackgroundClip? _hovered, _dragClip;
    private Point? _pointer;
    private DragKind _drag;
    private Point _origin;
    private double _grabOffset, _draftStart, _draftFadeIn, _draftFadeOut;
    private bool _dragChanged;
    private double? _dropTime;
    private ContextMenu? _menu;
    private Geometry? _removeIconGeometry;
    private long _waveDraws, _editorDraws, _dynamicDraws;
    private enum DragKind { None, Move, FadeIn, FadeOut }
    private static readonly Typeface Face = new("Segoe UI");

    public BackgroundLaneView()
    {
        Focusable = true; ClipToBounds = true; AllowDrop = true; MinHeight = 36;
        _visuals = new VisualCollection(this) { _waveVisual, _editorVisual, _dynamicVisual };
        RenderOptions.SetBitmapScalingMode(_waveVisual, BitmapScalingMode.LowQuality);
        _tiles = new WaveformTileRenderer(Dispatcher, 32L * 1024 * 1024);
        _tiles.TilesReady += () => { _waveDirty = true; RequestFrame(); };
        Loaded += (_, _) => { AttachTimeline(); InvalidateLane(true); };
        Unloaded += (_, _) => { DetachTimeline(); _tiles.InvalidateSources(); CompletePointerInteraction(false); _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); };
    }

    public BackgroundLayer Layer
    {
        get => _layer;
        set
        {
            _layer = value ?? new BackgroundLayer();
            var current = _layer.Clips.ToDictionary(clip => clip.Id);
            bool replaced = false;
            foreach (var pair in _snapshots.ToArray())
                if (!current.TryGetValue(pair.Key, out var clip) || !SnapshotMatches(pair.Value, clip.Source))
                { _snapshots.Remove(pair.Key); replaced = true; }
            if (replaced) _tiles.InvalidateSources();
            _tiles.RetainTracks(current.Keys);
            if (_hovered != null) _hovered = _layer.Clips.FirstOrDefault(clip => clip.Id == _hovered.Id);
            InvalidateLane(true);
        }
    }

    public TimelineView? Timeline
    {
        get => _timeline;
        set { if (ReferenceEquals(value, _timeline)) return; DetachTimeline(); _timeline = value; AttachTimeline(); InvalidateLane(true); }
    }

    public Func<IEnumerable<string>, bool>? CanDropFiles { get; set; }
    public event Action<BackgroundClip, double>? ClipMoved;
    public event Action<BackgroundClip, double, double>? FadesChanged;
    public event Action<BackgroundClip>? RemoveRequested;
    public event Action<BackgroundClip>? RelinkRequested;
    public event Action<string[], double>? FilesDropped;
    public event Action<double>? SeekRequested;

    private void AttachTimeline()
    {
        if (_timelineAttached || _timeline == null) return;
        _timeline.ViewportChanged += CameraChanged; _timeline.PlayheadChanged += PlayheadChanged;
        _timelineAttached = true;
    }
    private void DetachTimeline()
    {
        if (!_timelineAttached || _timeline == null) return;
        _timeline.ViewportChanged -= CameraChanged; _timeline.PlayheadChanged -= PlayheadChanged;
        _timelineAttached = false;
    }
    private void CameraChanged()
    {
        if (_drag != DragKind.None && _pointer is { } point) UpdatePointerInteraction(point);
        InvalidateLane(true);
    }
    private void PlayheadChanged(double _) { _dynamicDirty = true; RequestFrame(); }
    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize)); FlushPendingRendering();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); InvalidateLane(true); InvalidateVisual(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); InvalidateLane(true); }
    public void Refresh() { InvalidateLane(true); InvalidateVisual(); }

    private void InvalidateLane(bool waveform)
    {
        _waveDirty |= waveform; _editorDirty = _dynamicDirty = true; RequestFrame();
    }
    private void RequestFrame()
    {
        if (_disposed || _scheduled || !IsLoaded || !IsVisible) return;
        _scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { _scheduled = false; if (!_disposed) FlushPendingRendering(); }));
    }

    internal Rect PlotArea
    {
        get
        {
            var left = _timeline?.PlotArea.Left ?? 48;
            var rightInset = _timeline is { ActualWidth: > 0 } timeline ? timeline.ActualWidth - timeline.PlotArea.Right : left;
            return new Rect(left, 0, Math.Max(1, ActualWidth - left - rightInset), Math.Max(1, ActualHeight));
        }
    }
    private Rect SignalArea => new(PlotArea.Left, 18, PlotArea.Width, Math.Max(1, PlotArea.Height - 20));
    private double Start => _timeline?.ViewStartSeconds ?? 0;
    private double Duration => Math.Max(.05, _timeline?.VisibleDurationSeconds ?? Math.Max(1, _layer.Clips.Select(clip => clip.EndSeconds).DefaultIfEmpty(1).Max()));
    private double X(double time) => PlotArea.Left + (time - Start) / Duration * PlotArea.Width;
    private double T(double x) => Math.Max(0, Start + (x - PlotArea.Left) / PlotArea.Width * Duration);
    private static bool Missing(BackgroundClip clip) => clip.Source.WorkState == TrackWorkState.Missing;
    private static bool Unprocessed(BackgroundClip clip) => clip.Source.WorkState == TrackWorkState.Unprocessed;
    private static bool HasAudio(BackgroundClip clip) => !Missing(clip) && !Unprocessed(clip) && clip.Source.Original.Length > 0;
    private double SnapStart(double time, bool eligible, bool bypass)
        => eligible && !bypass && Start <= .000001 && time >= 0 && time / Duration * PlotArea.Width <= StartSnapPixels ? 0 : time;
    private double FileDropTime(Point point, bool bypassSnap)
        => SnapStart(T(point.X), _layer.Clips.Count == 0, bypassSnap);
    private double ClipStart(BackgroundClip clip) => ReferenceEquals(clip, _dragClip) ? _draftStart : clip.StartSeconds;
    private double FadeIn(BackgroundClip clip) => ReferenceEquals(clip, _dragClip) ? _draftFadeIn : clip.FadeInSeconds;
    private double FadeOut(BackgroundClip clip) => ReferenceEquals(clip, _dragClip) ? _draftFadeOut : clip.FadeOutSeconds;
    private Brush Brush(string key, string fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
    private Brush BackgroundBrush => Brush("BackgroundBrush", "#14191F");
    private Brush PanelBrush => Brush("PanelBrush", "#1D242D");
    private Brush BorderBrush => Brush("BorderBrush", "#35404C");
    private Brush TextBrush => Brush("TextBrush", "#E1E8EF");
    private Brush MutedBrush => Brush("MutedBrush", "#8F9DAE");
    private Brush AccentBrush => Brush("AccentBrush", "#5ACCB5");
    private Brush GoldBrush => Brush("MonitorActiveTextBrush", "#FFE3A4");
    private void Text(DrawingContext dc, string value, Point point, Brush brush, double width)
    {
        if (width < 4) return;
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, point);
    }

    private static bool SnapshotMatches((AudioTrack Source, float[] Samples, WaveformPyramid? Pyramid, List<PeakBin> Peaks, WaveformTileTrack Snapshot) cached, AudioTrack source)
        => ReferenceEquals(cached.Source, source) && ReferenceEquals(cached.Samples, source.Original) && ReferenceEquals(cached.Pyramid, source.OriginalWaveform) && ReferenceEquals(cached.Peaks, source.OriginalPeaks);
    private WaveformTileTrack Snapshot(BackgroundClip clip)
    {
        if (_snapshots.TryGetValue(clip.Id, out var cached) && SnapshotMatches(cached, clip.Source)) return cached.Snapshot;
        var source = clip.Source;
        var audio = new WaveformTileSource((object?)source.OriginalWaveform ?? source.Original, source.OriginalWaveform,
            source.OriginalPeaks, source.SampleRate, source.Duration);
        var snapshot = new WaveformTileTrack(clip.Id, audio, audio);
        _snapshots[clip.Id] = (source, source.Original, source.OriginalWaveform, source.OriginalPeaks, snapshot);
        return snapshot;
    }

    internal void FlushPendingRendering()
    {
        if (_disposed || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        if (_waveDirty)
        {
            _waveDirty = false; _waveDraws++;
            using var dc = _waveVisual.RenderOpen();
            dc.PushClip(new RectangleGeometry(PlotArea));
            _tiles.BeginFrame();
            foreach (var clip in _layer.Clips)
            {
                var bounds = ClipBounds(clip);
                if (bounds.IsEmpty) continue;
                dc.DrawRectangle(Missing(clip) ? Brush("MissingBackgroundBrush", "#352329") : PanelBrush,
                    new Pen(Missing(clip) ? Brush("MissingBorderBrush", "#6D3B45") : BorderBrush, 1), bounds);
                if (Missing(clip) || clip.Source.Original.Length == 0) continue;
                dc.PushClip(new RectangleGeometry(bounds));
                dc.PushOpacity(_layer.Enabled ? .85 : .3);
                var style = new WaveformTileStyle(AccentBrush, AccentBrush, AccentBrush, MutedBrush);
                _tiles.DrawTrack(dc, Snapshot(clip), Start, Duration, SignalArea, ClipStart(clip),
                    SignalArea.Top + SignalArea.Height / 2, SignalArea.Height * .46, WaveformViewMode.Original,
                    style, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.Pop(); dc.Pop();
            }
            _tiles.EndFrame(); dc.Pop();
        }
        if (_editorDirty)
        {
            _editorDirty = false; _editorDraws++;
            using var dc = _editorVisual.RenderOpen(); dc.PushClip(new RectangleGeometry(PlotArea));
            foreach (var clip in _layer.Clips)
            {
                var bounds = ClipBounds(clip); if (bounds.IsEmpty) continue;
                var hovered = ReferenceEquals(clip, _hovered) || ReferenceEquals(clip, _dragClip);
                Text(dc, clip.Source.Name, new Point(bounds.Left + 5, 2), Missing(clip) ? Brush("MissingTextBrush", "#DA9AA3") : TextBrush,
                    bounds.Width - (hovered ? 26 : 8));
                if (Missing(clip))
                {
                    var relink = RelinkBounds(clip);
                    if (!relink.IsEmpty)
                    {
                        dc.DrawRoundedRectangle(BackgroundBrush, new Pen(BorderBrush, 1), relink, 3, 3);
                        Text(dc, relink.Width >= 44 ? "Relink…" : "↗", new Point(relink.Left + 5, relink.Top + 2), TextBrush, relink.Width - 8);
                    }
                }
                else if (Unprocessed(clip))
                {
                    const string label = "File not processed";
                    var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, MutedBrush,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    Text(dc, label, new Point(bounds.Left + Math.Max(4, (bounds.Width - text.Width) / 2),
                        Math.Max(19, (ActualHeight - text.Height) / 2)), MutedBrush, bounds.Width - 8);
                }
                else if (HasAudio(clip))
                {
                    var left = X(ClipStart(clip)); var right = X(ClipStart(clip) + clip.Source.Duration);
                    var a = FadeHandlePosition(clip, true); var b = FadeHandlePosition(clip, false);
                    var fade = new StreamGeometry();
                    using (var shape = fade.Open())
                    {
                        shape.BeginFigure(new Point(left, SignalArea.Bottom - 2), false, false);
                        shape.LineTo(a, true, false); shape.LineTo(b, true, false); shape.LineTo(new Point(right, SignalArea.Bottom - 2), true, false);
                    }
                    dc.DrawGeometry(null, new Pen(GoldBrush, 1), fade);
                    if (hovered)
                    {
                        foreach (var handle in new[] { a, b }) if (PlotArea.Contains(handle))
                            dc.DrawEllipse(BackgroundBrush, new Pen(GoldBrush, 1.3), handle, 4, 4);
                    }
                }
                if (hovered)
                {
                    var remove = RemoveBounds(clip);
                    if (!remove.IsEmpty)
                    {
                        DrawRemoveIcon(dc, remove);
                    }
                }
            }
            dc.Pop();
        }
        if (_dynamicDirty)
        {
            _dynamicDirty = false; _dynamicDraws++;
            using var dc = _dynamicVisual.RenderOpen(); dc.PushClip(new RectangleGeometry(PlotArea));
            if (_timeline is { } timeline && timeline.Playhead >= Start && timeline.Playhead <= Start + Duration)
                dc.DrawLine(new Pen(GoldBrush, 1), new Point(X(timeline.Playhead), 0), new Point(X(timeline.Playhead), ActualHeight));
            if (_dropTime is { } time)
                dc.DrawLine(new Pen(AccentBrush, 2), new Point(X(time), 0), new Point(X(time), ActualHeight));
            dc.Pop();
        }
    }

    internal Rect ClipBounds(BackgroundClip clip)
    {
        var x = X(ClipStart(clip));
        if (Unprocessed(clip) && clip.Source.Duration <= 0)
        {
            // Cancellation can precede duration metadata. Keep the found file
            // visible and removable without inventing an audio duration or
            // changing album timing, placement, or the eventual rendered mix.
            if (x < PlotArea.Left || x > PlotArea.Right) return Rect.Empty;
            var width = Math.Min(120, PlotArea.Width);
            var placeholderLeft = Math.Clamp(x, PlotArea.Left, PlotArea.Right - width);
            return new Rect(placeholderLeft, 0, width, Math.Max(1, ActualHeight));
        }
        var left = Math.Max(PlotArea.Left, x);
        var right = Math.Min(PlotArea.Right, X(ClipStart(clip) + clip.Source.Duration));
        return right <= left ? Rect.Empty : new Rect(left, 0, right - left, Math.Max(1, ActualHeight));
    }
    internal Point FadeHandlePosition(BackgroundClip clip, bool fadeIn)
        => new(X(ClipStart(clip) + (fadeIn ? FadeIn(clip) : clip.Source.Duration - FadeOut(clip))), SignalArea.Top + 5);
    private void DrawRemoveIcon(DrawingContext dc, Rect buttonBounds)
    {
        const double size = 14, stroke = 1.3;
        if (_removeIconGeometry == null)
        {
            // Match the square native Paths in the file list and main waveform:
            // fit the supplied SVG uniformly, leaving room for its round stroke.
            var source = (Geometry)FindResource("TrashIconGeometry");
            var bounds = source.Bounds;
            double scale = (size - stroke) / Math.Max(bounds.Width, bounds.Height);
            var fitted = source.CloneCurrentValue();
            fitted.Transform = new MatrixTransform(scale, 0, 0, scale,
                (size - bounds.Width * scale) / 2 - bounds.Left * scale,
                (size - bounds.Height * scale) / 2 - bounds.Top * scale);
            fitted.Freeze(); _removeIconGeometry = fitted;
        }
        var pen = new Pen(MutedBrush, stroke) { StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new TranslateTransform(buttonBounds.Left + (buttonBounds.Width - size) / 2,
            buttonBounds.Top + (buttonBounds.Height - size) / 2));
        dc.DrawGeometry(null, pen, _removeIconGeometry);
        dc.Pop();
    }

    internal Rect RemoveBounds(BackgroundClip clip)
    {
        var bounds = ClipBounds(clip);
        return bounds.IsEmpty || bounds.Width < 24 ? Rect.Empty : new Rect(bounds.Right - 22, 0, 22, 18);
    }
    internal Rect RelinkBounds(BackgroundClip clip)
    {
        var bounds = ClipBounds(clip);
        if (bounds.IsEmpty || bounds.Width < 20 || !Missing(clip)) return Rect.Empty;
        var width = Math.Min(70, bounds.Width - 6);
        return new Rect(bounds.Left + (bounds.Width - width) / 2, Math.Max(19, (ActualHeight - 19) / 2), width, 19);
    }
    private BackgroundClip? ClipAt(Point point)
        => PlotArea.Contains(point) ? _layer.Clips.Reverse().FirstOrDefault(clip => ClipBounds(clip).Contains(point)) : null;

    internal bool HandlePointerDown(Point point)
    {
        if (!IsEnabled || !PlotArea.Contains(point)) return false;
        var clip = ClipAt(point);
        if (clip == null) { SeekRequested?.Invoke(T(point.X)); return true; }
        SetHover(point);
        if (RemoveBounds(clip).Contains(point)) { RemoveRequested?.Invoke(clip); return true; }
        if (RelinkBounds(clip).Contains(point)) { RelinkRequested?.Invoke(clip); return true; }
        _dragClip = clip; _origin = point; _pointer = point; _dragChanged = false;
        _draftStart = clip.StartSeconds; _draftFadeIn = clip.FadeInSeconds; _draftFadeOut = clip.FadeOutSeconds;
        _grabOffset = T(point.X) - clip.StartSeconds;
        var a = FadeHandlePosition(clip, true); var b = FadeHandlePosition(clip, false);
        _drag = HasAudio(clip) && (a - point).Length <= 8 ? DragKind.FadeIn :
            HasAudio(clip) && (b - point).Length <= 8 ? DragKind.FadeOut : DragKind.Move;
        return true;
    }
    internal bool UpdatePointerInteraction(Point point)
    {
        _pointer = point;
        if (_dragClip == null || _drag == DragKind.None) return false;
        if (!_dragChanged && (point - _origin).Length <= 2) return true;
        _dragChanged = true;
        switch (_drag)
        {
            case DragKind.Move: _draftStart = SnapStart(Math.Max(0, T(point.X) - _grabOffset), _layer.Clips.Count == 1,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
            case DragKind.FadeIn: _draftFadeIn = Math.Clamp(T(point.X) - _draftStart, 0, _dragClip.Source.Duration); break;
            case DragKind.FadeOut: _draftFadeOut = Math.Clamp(_draftStart + _dragClip.Source.Duration - T(point.X), 0, _dragClip.Source.Duration); break;
        }
        InvalidateLane(_drag == DragKind.Move);
        return true;
    }
    internal void CompletePointerInteraction(bool commit = true)
    {
        var clip = _dragClip; var kind = _drag; var changed = _dragChanged;
        var start = _draftStart; var fadeIn = _draftFadeIn; var fadeOut = _draftFadeOut;
        _drag = DragKind.None; _dragClip = null; _dragChanged = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (clip != null && commit)
        {
            if (changed && kind == DragKind.Move && Math.Abs(start - clip.StartSeconds) > .000001) ClipMoved?.Invoke(clip, start);
            else if (changed && kind != DragKind.Move && (Math.Abs(fadeIn - clip.FadeInSeconds) > .000001 || Math.Abs(fadeOut - clip.FadeOutSeconds) > .000001))
                FadesChanged?.Invoke(clip, fadeIn, fadeOut);
            else if (!changed && kind == DragKind.Move) SeekRequested?.Invoke(T(_origin.X));
        }
        InvalidateLane(kind == DragKind.Move);
    }
    internal void SetHover(Point? point)
    {
        _pointer = point;
        var hovered = point.HasValue ? ClipAt(point.Value) : null;
        if (ReferenceEquals(hovered, _hovered)) return;
        _hovered = hovered; _editorDirty = true; RequestFrame();
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus(); var point = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right) { ShowClipMenu(point); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left || !HandlePointerDown(point)) return;
        if (_drag != DragKind.None) CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var point = e.GetPosition(this);
        if (UpdatePointerInteraction(point)) { Cursor = _drag == DragKind.Move ? Cursors.SizeAll : Cursors.SizeWE; return; }
        SetHover(point); var clip = ClipAt(point);
        Cursor = clip == null ? Cursors.IBeam : RemoveBounds(clip).Contains(point) || RelinkBounds(clip).Contains(point) ? Cursors.Hand :
            HasAudio(clip) && ((FadeHandlePosition(clip, true) - point).Length <= 8 || (FadeHandlePosition(clip, false) - point).Length <= 8) ? Cursors.SizeWE : Cursors.SizeAll;
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (_drag == DragKind.None) SetHover(null); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { base.OnMouseUp(e); if (e.ChangedButton == MouseButton.Left) CompletePointerInteraction(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); CompletePointerInteraction(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag != DragKind.None) { CompletePointerInteraction(false); e.Handled = true; }
        if (e.Key == Key.Delete && _hovered is { } clip) { RemoveRequested?.Invoke(clip); e.Handled = true; }
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = HandleNavigationWheel(e.GetPosition(this), e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }
    internal bool HandleNavigationWheel(Point point, int delta, bool pan)
    {
        if (_timeline == null) return false;
        _timeline.NavigateWheel(new Point(point.X, _timeline.SignalCenterY), delta, pan); return true;
    }
    private void ShowClipMenu(Point point)
    {
        var clip = ClipAt(point); if (clip == null) return;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint, HorizontalOffset = point.X, VerticalOffset = point.Y };
        if (Missing(clip))
        {
            var relink = new MenuItem { Header = "Relink…" }; relink.Click += (_, _) => RelinkRequested?.Invoke(clip); menu.Items.Add(relink);
        }
        var remove = new MenuItem { Header = "Remove" }; remove.Click += (_, _) => RemoveRequested?.Invoke(clip); menu.Items.Add(remove);
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };
        _menu = menu; menu.IsOpen = true;
    }
    private bool ReadFiles(IDataObject data, out string[] paths)
    {
        paths = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files : [];
        return paths.Length > 0 && CanDropFiles?.Invoke(paths) != false;
    }
    internal bool PreviewFileDrop(IDataObject data, Point point, bool bypassSnap = false)
    {
        var accepted = PlotArea.Contains(point) && ReadFiles(data, out _);
        _dropTime = accepted ? FileDropTime(point, bypassSnap) : null; _dynamicDirty = true; RequestFrame(); return accepted;
    }
    internal bool HandleFileDrop(IDataObject data, Point point, bool bypassSnap = false)
    {
        string[] paths = [];
        var accepted = PlotArea.Contains(point) && ReadFiles(data, out paths);
        _dropTime = null; _dynamicDirty = true; RequestFrame();
        if (accepted) FilesDropped?.Invoke(paths!, FileDropTime(point, bypassSnap));
        return accepted;
    }
    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; e.Effects = PreviewFileDrop(e.Data, e.GetPosition(this), e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    protected override void OnDragLeave(DragEventArgs e) { base.OnDragLeave(e); _dropTime = null; _dynamicDirty = true; RequestFrame(); }
    protected override void OnDrop(DragEventArgs e) { base.OnDrop(e); if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; HandleFileDrop(e.Data, e.GetPosition(this), e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey)); e.Handled = true; }

    internal WaveformTileDiagnostics TileDiagnostics => _tiles.Diagnostics;
    internal double? DropTimeSeconds => _dropTime;
    internal bool HasPendingTiles => _tiles.IsBusy;
    internal (long Waveform, long Editor, long Dynamic) DrawCounts => (_waveDraws, _editorDraws, _dynamicDraws);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; DetachTimeline(); _tiles.Dispose(); _snapshots.Clear(); _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }
}

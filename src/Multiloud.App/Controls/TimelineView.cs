using System.Globalization;
using System.Diagnostics;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Multiloud.Core;

namespace Multiloud.App.Controls;

public enum WaveformViewMode { Original, Preview, Compare }
public readonly record struct TimelineCursorInfo(double TimeSeconds, double AmplitudeDbfs, double EnvelopeGainDb);
internal readonly record struct TimelineDiagnostics(long CompositionFrames, long WaveformRedraws, long OverviewRedraws,
    long EditorRedraws, long DynamicRedraws, long MetricsPasses, double RenderCpuMilliseconds,
    double MaxRenderCpuMilliseconds, WaveformTileDiagnostics Tiles);

/// <summary>One continuous album timeline, with non-destructive gain editing.</summary>
public sealed partial class TimelineView : FrameworkElement
{
    public const string TrackDragDataFormat = "Multiloud.TrackId";
    private IList<AudioTrack> _tracks = new List<AudioTrack>();
    private ProcessingSettings _settings = new();
    private WaveformViewMode _viewMode = WaveformViewMode.Preview;
    private AudioTrack? _selectedTrack;
    private double _playhead;
    private Point? _crosshairPointer;
    private Point? _trackHoverPointer;
    private Rect _hoverHighlightBounds = Rect.Empty;
    private TimelineCursorInfo? _cursorInfo;
    private double _viewStart;
    private double _viewDuration;
    private double _knownTotalDuration;
    private double _totalDuration;
    private double _emptyDurationHint;
    private double _computedEnvelopeRange = 8, _computedGainRange = 12;
    private DragKind _drag;
    private Point _dragOrigin;
    private double _dragStart, _dragDuration, _dragGain, _dragRange;
    private double _dragNodeTime, _dragNodeY, _dragNodeGain;
    private AudioTrack? _editingTrack;
    private int _editingNode = -1;
    private bool _dragEdited, _dragMotionStarted;
    private bool _showEnvelopeOnWaveform = true;
    private bool _showGainLines = true;
    private bool _livePreviewActive;
    private AudioTrack? _hoveredTrack, _pendingTrackDrag;
    private Point _trackDragStart, _dropPointer;
    private bool _trackDragInProgress;
    private int _dropInsertionIndex = -1;
    private readonly DispatcherTimer _dragPanTimer;
    private bool _navigationActive, _workingAnimation, _renderSubscribed;
    private bool _metricsDirty = true, _waveformDirty = true, _overviewDirty = true, _editorDirty = true, _dynamicDirty = true, _hoverBackgroundDirty = true;
    private long _lastSpinnerTick;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private readonly DrawingVisual _overviewVisual = new(), _trackBackgroundVisual = new(), _hoverBackgroundVisual = new(), _waveformVisual = new(), _editorVisual = new(), _dynamicVisual = new();
    private long _hoverBackgroundRenderCount;
    private readonly WaveformTileRenderer _tileRenderer;
    private readonly Dictionary<Guid, (DrawingSource Key, WaveformTileTrack Snapshot)> _tileSnapshots = new();
    private readonly Dictionary<(Guid Id, bool Preview), DrawingSource> _frameSources = new();
    private long _renderCycleCount, _waveformRenderCount, _overviewRenderCount, _editorRenderCount, _dynamicRenderCount, _metricsPassCount;
    private long _layerCpuTicks, _maxLayerCpuTicks;
    private double _targetViewStart, _targetViewDuration;
    private long _navigationStartedTick;
    private double _navigationFromStart, _navigationFromDuration;
    private const double NavigationSeconds = .22;
    private Rect _hoverEditorArrangedBounds, _removeTrackArrangedBounds;
    private long _navigationEnvelopeNodeDraws, _navigationEnvelopeVertices;
    private int _spinnerPhase;
    private readonly Dictionary<Guid, OverviewDrawing> _overviewDrawings = new();
    private readonly Dictionary<string, Brush> _fallbackBrushes = new();
    private readonly Dictionary<(string Value, double Size, Brush Brush, double Width, double Dpi), FormattedText> _textCache = new();
    private readonly Dictionary<Guid, double> _trackOffsets = new();
    private readonly Dictionary<Guid, int> _trackIndices = new();
    private readonly List<double> _interiorTrackBoundaries = new();
    private bool _handleVisibilityDirty = true;
    private int _visibleInteriorBoundaryCount;
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private const double HoverEditorHeight = 94;
    private const double HoverValueFontSize = 12;
    private readonly VisualCollection _nativeChildren;
    private readonly Border _hoverEditor;
    private readonly TextBox _inputGainBox, _targetLufsBox;
    private readonly TextBlock _inputGainValue, _targetLufsValue;
    private readonly Button _resetTargetButton, _removeTrackButton;
    private AudioTrack? _editorTrack;
    private bool _syncingEditors, _inputTextDirty, _targetTextDirty;
    private AudioTrack? _valueDragTrack, _hoverNodeTrack;
    private TextBlock? _valueDragElement;
    private bool _valueDragTarget, _valueDragChanged;
    private double _valueDragStartY, _valueDragStartValue, _valueDragStep;
    private double? _valueDragOriginalTarget;
    private int _hoverNodeIndex = -1;
    private bool _editUndoStarted;
    private readonly Dictionary<Guid, (int Hash, IReadOnlyList<EnvelopePoint> Points)> _combinedCurves = new();
    private readonly Dictionary<Guid, double> _frameMatchingGain = new();
    private readonly Dictionary<Guid, double> _framePreMatchLufs = new();
    private readonly Dictionary<Guid, bool> _frameApproximate = new();

    private readonly record struct WavePeak(float Min, float Max);
    private sealed record DrawingSource(float[] Original, RenderResult? Render, List<PeakBin> OriginalPeaks, List<PeakBin> PreviewPeaks,
        WaveformPyramid? OriginalPyramid, WaveformPyramid? PreviewPyramid,
        bool Approximate, ProcessingSettings? Settings, double Trim, int EnvelopeHash);
    private sealed record OverviewDrawingKey(DrawingSource Source, double Offset, double TrackDuration, double TotalDuration, Rect Area, bool Preview);
    private sealed record OverviewDrawing(OverviewDrawingKey Key, StreamGeometry Geometry);

    private enum DragKind { None, OverviewMove, OverviewLeft, OverviewRight, Gain, Envelope, Seek }

    public TimelineView()
    {
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        MinHeight = 240;
        _nativeChildren = new VisualCollection(this);
        RenderOptions.SetBitmapScalingMode(_waveformVisual, BitmapScalingMode.LowQuality);
        foreach (var layer in new[] { _overviewVisual, _customBoundaryVisual, _trackBackgroundVisual, _hoverBackgroundVisual, _waveformVisual, _loopBackgroundVisual, _editorVisual, _dynamicVisual }) _nativeChildren.Add(layer);
        _tileRenderer = new WaveformTileRenderer(Dispatcher);
        _tileRenderer.TilesReady += () => { _waveformDirty = true; RequestFrame(); };
        _inputGainBox = MakeHoverTextBox();
        _targetLufsBox = MakeHoverTextBox();
        _inputGainValue = MakeHoverValue(false);
        _targetLufsValue = MakeHoverValue(true);
        _resetTargetButton = new Button { Content = "↺", Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand };
        _resetTargetButton.Click += (_, _) => ResetTrackTarget();
        var editorRows = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right };
        editorRows.Children.Add(MakeHoverValueSection("Input", _inputGainValue, _inputGainBox, "dB", null));
        editorRows.Children.Add(new Border { Height = 4 });
        editorRows.Children.Add(MakeHoverValueSection("Target", _targetLufsValue, _targetLufsBox, "LUFS", _resetTargetButton));
        _hoverEditor = new Border { Child = editorRows, Padding = new Thickness(5), Background = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
        _hoverEditor.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(90, 204, 181));
        var trash = new System.Windows.Shapes.Path { StrokeThickness = 1.3, Width = 14, Height = 14,
            Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        trash.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "TrashIconGeometry");
        trash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        _removeTrackButton = new Button { Content = trash, Width = 24, Height = 23, Padding = new Thickness(3, 2, 3, 2), Margin = new Thickness(0), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
        _removeTrackButton.Click += (_, _) => { if (_editorTrack is { } track) TrackRemoveRequested?.Invoke(track); };
        System.Windows.Automation.AutomationProperties.SetName(_inputGainBox, "Track input gain in decibels");
        System.Windows.Automation.AutomationProperties.SetName(_targetLufsBox, "Track target loudness in LUFS");
        System.Windows.Automation.AutomationProperties.SetName(_resetTargetButton, "Reset track target to global");
        System.Windows.Automation.AutomationProperties.SetName(_removeTrackButton, "Remove track");
        foreach (var child in new FrameworkElement[] { _hoverEditor, _removeTrackButton }) { _nativeChildren.Add(child); AddLogicalChild(child); }
        _inputGainBox.TextChanged += (_, _) => { if (!_syncingEditors) _inputTextDirty = true; };
        _targetLufsBox.TextChanged += (_, _) => { if (!_syncingEditors) _targetTextDirty = true; };
        _inputGainBox.LostKeyboardFocus += (_, _) => { CommitHoverValue(false); CloseNativeTextEdit(false); Dispatcher.BeginInvoke(new Action(ReconcileHoverAfterFocus), DispatcherPriority.Input); };
        _targetLufsBox.LostKeyboardFocus += (_, _) => { CommitHoverValue(true); CloseNativeTextEdit(true); Dispatcher.BeginInvoke(new Action(ReconcileHoverAfterFocus), DispatcherPriority.Input); };
        _inputGainBox.PreviewKeyDown += (_, e) => HoverEditorKeyDown(_inputGainBox, false, e);
        _targetLufsBox.PreviewKeyDown += (_, e) => HoverEditorKeyDown(_targetLufsBox, true, e);
        _dragPanTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _dragPanTimer.Tick += (_, _) => AutoPanTrackDrag();
        Loaded += (_, _) => { UpdateAnimation(); RequestFrame(); };
        Unloaded += (_, _) => { _workingAnimation = false; SetCrosshairPointer(null); SetTrackHoverPointer(null); StopNavigation(); ClearTrackDrop(); DetachRendering(); };
        IsVisibleChanged += (_, _) => { UpdateAnimation(); if (!IsVisible) { SetCrosshairPointer(null); SetTrackHoverPointer(null); StopNavigation(); ClearTrackDrop(); DetachRendering(); } else RequestFrame(); };
    }

    private void InvalidateTimeline()
    {
        _editorDirty = _dynamicDirty = true;
        RequestFrame();
    }

    private void InvalidateData(bool curvesChanged = true)
    {
        _loopBackgroundDirty = true;
        // Source descriptors contain strong PCM/render references. Release stale
        // descriptors immediately, including tracks outside the visible camera
        // or a hidden/minimized control; identical sources retain their identity.
        _frameSources.Clear();
        PruneSourceSnapshots();
        _tileRenderer.InvalidateSources(keepVisibleWork: IsLoaded && IsVisible);
        _metricsDirty = true;
        _handleVisibilityDirty = true;
        if (curvesChanged) _combinedCurves.Clear();
        _waveformDirty = _overviewDirty = _editorDirty = _dynamicDirty = _hoverBackgroundDirty = true;
        RequestFrame();
    }

    private void PruneSourceSnapshots()
    {
        var currentTracks = new Dictionary<Guid, AudioTrack>(Tracks.Count);
        foreach (var track in Tracks) currentTracks[track.Id] = track;
        foreach (var cached in _tileSnapshots.ToArray())
            if (!currentTracks.TryGetValue(cached.Key, out var track) || IsMissing(track) || !SourceReferencesMatch(cached.Value.Key, track))
                _tileSnapshots.Remove(cached.Key);
        foreach (var cached in _overviewDrawings.ToArray())
            if (!currentTracks.TryGetValue(cached.Key, out var track) || IsMissing(track) || !SourceReferencesMatch(cached.Value.Key.Source, track))
                _overviewDrawings.Remove(cached.Key);
    }

    private static bool SourceReferencesMatch(DrawingSource source, AudioTrack track) =>
        ReferenceEquals(source.Original, track.Original) && ReferenceEquals(source.Render, track.Render) &&
        ReferenceEquals(source.OriginalPeaks, track.OriginalPeaks) && ReferenceEquals(source.PreviewPeaks, track.PreviewPeaks) &&
        ReferenceEquals(source.OriginalPyramid, track.OriginalWaveform) && ReferenceEquals(source.PreviewPyramid, track.Render?.Waveform);

    private void InvalidateCamera()
    {
        _loopBackgroundDirty = true;
        _customBoundariesDirty = true;
        _handleVisibilityDirty = true;
        _waveformDirty = _editorDirty = _dynamicDirty = _hoverBackgroundDirty = true;
        if (_editorTrack != null)
        {
            UpdateHoverVisibility(_editorTrack);
            NativeEditorBounds(out var editorBounds, out var removeBounds);
            if (editorBounds != _hoverEditorArrangedBounds || removeBounds != _removeTrackArrangedBounds) InvalidateArrange();
        }
        ViewportChanged?.Invoke();
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (!IsLoaded || !IsVisible) return;
        if (_renderSubscribed) return;
        _renderSubscribed = true;
        CompositionTarget.Rendering += RenderFrame;
    }

    private void DetachRendering()
    {
        if (!_renderSubscribed) return;
        CompositionTarget.Rendering -= RenderFrame;
        _renderSubscribed = false;
        _lastRenderingTime = TimeSpan.MinValue;
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs rendering)
        {
            if (_lastRenderingTime == rendering.RenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        _renderCycleCount++;
        if (_navigationActive) AdvanceNavigation();
        if (_workingAnimation)
        {
            var now = Stopwatch.GetTimestamp();
            if ((now - _lastSpinnerTick) / (double)Stopwatch.Frequency >= .075)
            {
                _lastSpinnerTick = now; _spinnerPhase = (_spinnerPhase + 1) % 12; _dynamicDirty = true;
            }
        }
        RenderPendingLayers();
        if (!_navigationActive && !_workingAnimation && !_metricsDirty && !_waveformDirty && !_overviewDirty && !_editorDirty && !_dynamicDirty && !_hoverBackgroundDirty && !_loopBackgroundDirty)
            DetachRendering();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _loopBackgroundDirty = _customBoundariesDirty = true;
        _waveformDirty = _overviewDirty = _editorDirty = _dynamicDirty = _hoverBackgroundDirty = true;
        if (_hoverEditor != null && !_syncingEditors) SyncHoverEditor();
        InvalidateArrange(); InvalidateVisual(); RequestFrame();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _loopBackgroundDirty = _customBoundariesDirty = true;
        _textCache.Clear();
        if (_inputGainBox != null && _targetLufsBox != null)
        {
            var width = HoverNumberEditorWidth;
            _inputGainBox.Width = _inputGainBox.MaxWidth = width;
            _targetLufsBox.Width = _targetLufsBox.MaxWidth = width;
        }
        _waveformDirty = _overviewDirty = _editorDirty = _dynamicDirty = _hoverBackgroundDirty = true;
        InvalidateMeasure(); InvalidateArrange(); InvalidateVisual(); RequestFrame();
    }

    private double HoverNumberEditorWidth => Math.Ceiling(CachedText("-99", HoverValueFontSize, TextBrush, double.PositiveInfinity).WidthIncludingTrailingWhitespace) + 4;

    private TextBox MakeHoverTextBox()
    {
        var width = HoverNumberEditorWidth;
        var box = new TextBox { Width = width, MaxWidth = width, Height = 22, FontSize = HoverValueFontSize, Padding = new Thickness(1, 0, 1, 0), BorderThickness = new Thickness(1), Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        box.GotKeyboardFocus += (_, _) => { if (_editorTrack != null) Select(_editorTrack); box.SelectAll(); };
        return box;
    }

    private FrameworkElement MakeHoverValueSection(string label, TextBlock value, TextBox box, string unit, Button? reset)
    {
        var section = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        var title = MakeHoverLabel(label, new Thickness(0));
        title.Height = 18;
        section.Children.Add(title);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Height = 22 };
        if (reset != null) row.Children.Add(reset);
        var number = new Grid { Height = 22 };
        number.Children.Add(value); number.Children.Add(box);
        row.Children.Add(number);
        row.Children.Add(MakeHoverLabel(unit, new Thickness(3, 0, 0, 0)));
        section.Children.Add(row);
        return section;
    }

    private TextBlock MakeHoverValue(bool target)
    {
        var value = new TextBlock { FontSize = HoverValueFontSize, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeNS };
        value.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        value.MouseLeftButtonDown += (_, e) =>
        {
            if (_editorTrack == null) return;
            if (e.ClickCount >= 2) BeginNativeTextEdit(target);
            else
            {
                BeginTrackValueDrag(_editorTrack, target, e.GetPosition(this).Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                value.CaptureMouse();
            }
            e.Handled = true;
        };
        value.MouseMove += (_, e) =>
        {
            if (!ReferenceEquals(value, _valueDragElement) || _valueDragTrack == null || e.LeftButton != MouseButtonState.Pressed) return;
            UpdateTrackValueDrag(e.GetPosition(this).Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        };
        value.MouseLeftButtonUp += (_, e) => { EndNativeValueDrag(); e.Handled = true; };
        value.LostMouseCapture += (_, _) => EndNativeValueDrag();
        return value;
    }

    internal void EndNativeValueDrag()
    {
        var track = _valueDragTrack;
        var element = _valueDragElement;
        var changed = _valueDragChanged && track != null && Tracks.Contains(track) && !IsMissing(track);
        _valueDragTrack = null; _valueDragElement = null; _valueDragChanged = false;
        if (element?.IsMouseCaptured == true) element.ReleaseMouseCapture();
        if (changed) EditCommitted?.Invoke(track!);
        SyncHoverEditor(); InvalidateTimeline();
    }

    internal void BeginTrackValueDrag(AudioTrack track, bool target, double startY, bool fine)
    {
        if (!Tracks.Contains(track) || IsMissing(track)) return;
        CommitFocusedNativeTextEdit();
        if (!Tracks.Contains(track) || IsMissing(track)) return;
        Select(track);
        _valueDragTrack = track; _valueDragElement = target ? _targetLufsValue : _inputGainValue; _valueDragTarget = target;
        _valueDragStartY = startY;
        _valueDragStartValue = target ? track.TargetLufs ?? Settings.TargetLufs : track.InputGainDb;
        _valueDragOriginalTarget = track.TargetLufs;
        _valueDragStep = !target && fine ? .1 : 1;
        _valueDragChanged = false;
    }

    internal void UpdateTrackValueDrag(double y, bool fine)
    {
        if (_valueDragTrack == null || !Tracks.Contains(_valueDragTrack) || IsMissing(_valueDragTrack)) return;
        var step = !_valueDragTarget && fine ? .1 : 1;
        if (step != _valueDragStep)
        {
            _valueDragStartValue = _valueDragTarget ? _valueDragTrack.TargetLufs ?? Settings.TargetLufs : _valueDragTrack.InputGainDb;
            _valueDragStartY = y; _valueDragStep = step;
        }
        var steps = Math.Truncate((_valueDragStartY - y) / 12);
        var next = Clamp(_valueDragStartValue + steps * step, _valueDragTarget ? -36 : -30, _valueDragTarget ? -6 : 30);
        var current = _valueDragTarget ? _valueDragTrack.TargetLufs ?? Settings.TargetLufs : _valueDragTrack.InputGainDb;
        double? nextTarget = steps == 0 ? _valueDragOriginalTarget : next;
        if (_valueDragTarget)
        {
            // Returning to the starting step also restores global inheritance.
            // A clamped, numerically unchanged gesture must not create an override.
            if (_valueDragTrack.TargetLufs == nextTarget || steps != 0 && Math.Abs(next - current) < .00001) return;
        }
        else if (Math.Abs(next - current) < .00001) return;
        if (!_valueDragChanged) EditStarted?.Invoke();
        _valueDragChanged = true;
        if (_valueDragTarget) _valueDragTrack.TargetLufs = nextTarget; else _valueDragTrack.InputGainDb = next;
        SyncHoverEditor(true); InvalidateData(false);
    }

    internal void BeginNativeTextEdit(bool target)
    {
        if (_editorTrack == null || IsMissing(_editorTrack)) return;
        CommitFocusedNativeTextEdit();
        EndNativeValueDrag();
        var box = target ? _targetLufsBox : _inputGainBox;
        var value = target ? _targetLufsValue : _inputGainValue;
        SyncHoverEditor(true);
        value.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Visible;
        box.Focus(); box.SelectAll();
    }

    private void CommitFocusedNativeTextEdit()
    {
        bool? target = _inputGainBox.IsKeyboardFocusWithin ? false : _targetLufsBox.IsKeyboardFocusWithin ? true : null;
        // Commit against the existing editor track before a forced refresh can
        // overwrite its text. The later LostKeyboardFocus sees a clean field.
        if (target.HasValue)
        {
            CommitHoverValue(target.Value);
            CloseNativeTextEdit(target.Value);
        }
        // TextBlocks do not take keyboard focus, so also flush a previously
        // focused toolbar field before capturing the new gesture's starting value.
        Focus();
    }

    private void CloseNativeTextEdit(bool target)
    {
        var box = target ? _targetLufsBox : _inputGainBox;
        var value = target ? _targetLufsValue : _inputGainValue;
        if (box.Visibility != Visibility.Visible) return;
        var syncing = _syncingEditors; _syncingEditors = true;
        box.Visibility = Visibility.Collapsed; value.Visibility = Visibility.Visible;
        _syncingEditors = syncing;
    }

    private TextBlock MakeHoverLabel(string value, Thickness margin)
    {
        var label = new TextBlock { Text = value, FontSize = HoverValueFontSize, Margin = margin, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        label.Foreground = Brushes.White;
        return label;
    }

    protected override int VisualChildrenCount => _nativeChildren.Count;
    protected override Visual GetVisualChild(int index) => _nativeChildren[index];
    protected override IEnumerator LogicalChildren
    {
        get
        {
            // Resource/inheritance walks must reach the native editor controls,
            // including after the application switches between light and dark.
            if (_nativeChildren == null) return Array.Empty<object>().GetEnumerator();
            return _nativeChildren.OfType<FrameworkElement>().GetEnumerator();
        }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        _hoverEditor.Measure(new Size(double.PositiveInfinity, HoverEditorHeight));
        _removeTrackButton.Measure(new Size(24, 23));
        return base.MeasureOverride(availableSize);
    }

    private void NativeEditorBounds(out Rect editorBounds, out Rect removeBounds)
    {
        editorBounds = removeBounds = new Rect(0, 0, 0, 0);
        if (_editorTrack == null || !Tracks.Contains(_editorTrack)) return;
        var offset = TrackOffset(_editorTrack);
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + _editorTrack.Duration));
        if (_hoverEditor.Visibility == Visibility.Visible)
        {
            var width = Math.Min(_hoverEditor.DesiredSize.Width, Math.Max(1, right - left - 12));
            editorBounds = new Rect(Math.Max(left + 6, right - width - 8), WaveArea.Top + 26, width, HoverEditorHeight);
        }
        if (_removeTrackButton.Visibility == Visibility.Visible)
            removeBounds = new Rect(Clamp(right - 28, WaveArea.Left, WaveArea.Right - 24), WaveArea.Top + 2, 24, 23);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        NativeEditorBounds(out _hoverEditorArrangedBounds, out _removeTrackArrangedBounds);
        _hoverEditor.Arrange(_hoverEditorArrangedBounds);
        _removeTrackButton.Arrange(_removeTrackArrangedBounds);
        return finalSize;
    }

    private bool IsNativeEditorSource(object source)
    {
        for (var current = source as DependencyObject; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _hoverEditor) || ReferenceEquals(current, _removeTrackButton)) return true;
        return false;
    }

    private void SyncHoverEditor(bool force = false)
    {
        if (_syncingEditors) return;
        _syncingEditors = true;
        try
        {
            var track = _hoveredTrack != null && Tracks.Contains(_hoveredTrack) ? _hoveredTrack : null;
            if (!ReferenceEquals(_editorTrack, track))
            {
                _editorTrack = track;
                force = true;
                _inputTextDirty = _targetTextDirty = false;
            }
            UpdateHoverVisibility(track);
            if (track == null) return;
            if (force || !_inputGainBox.IsKeyboardFocusWithin)
            {
                _inputGainBox.Text = track.InputGainDb.ToString("0.##", CultureInfo.InvariantCulture);
                _inputTextDirty = false;
            }
            if (force || !_targetLufsBox.IsKeyboardFocusWithin)
            {
                _targetLufsBox.Text = (track.TargetLufs ?? Settings.TargetLufs).ToString("0.##", CultureInfo.InvariantCulture);
                _targetTextDirty = false;
            }
            _resetTargetButton.IsEnabled = track.TargetLufs.HasValue;
            _inputGainValue.Text = track.InputGainDb.ToString("0.##", CultureInfo.InvariantCulture);
            _targetLufsValue.Text = (track.TargetLufs ?? Settings.TargetLufs).ToString("0.##", CultureInfo.InvariantCulture);
            if (track.TargetLufs is { } target && Math.Abs(target - Settings.TargetLufs) > .00001)
                _targetLufsBox.Background = target > Settings.TargetLufs
                    ? new SolidColorBrush(Color.FromArgb(42, 70, 145, 215))
                    : new SolidColorBrush(Color.FromArgb(42, 215, 80, 95));
            else _targetLufsBox.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        }
        finally { _syncingEditors = false; }
        InvalidateArrange();
    }

    private void UpdateHoverVisibility(AudioTrack? track)
    {
        var wide = track != null && HoverEditorFits(track);
        _hoverEditor.Visibility = wide && !IsMissing(track!) ? Visibility.Visible : Visibility.Collapsed;
        // Missing clips still need an immediately reachable remove action, even
        // when their slot is too narrow for the normal gain controls.
        var removeFits = wide || track != null && IsMissing(track) && TrackTitleRect(track).Width >= 32;
        _removeTrackButton.Visibility = removeFits ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HoverEditorKeyDown(TextBox box, bool target, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitHoverValue(target); CloseNativeTextEdit(target); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SyncHoverEditor(true); CloseNativeTextEdit(target); Focus(); e.Handled = true; }
    }

    private void ReconcileHoverAfterFocus()
    {
        if (_hoverEditor.IsKeyboardFocusWithin) return;
        if (IsMouseOver) SetHoveredTrack(Mouse.GetPosition(this));
        else
        {
            _hoveredTrack = null;
            SyncHoverEditor();
            InvalidateTimeline();
        }
    }

    private void CommitHoverValue(bool target)
    {
        var track = _editorTrack;
        if (_syncingEditors || track == null || !Tracks.Contains(track) || IsMissing(track) || (target ? !_targetTextDirty : !_inputTextDirty)) return;
        var box = target ? _targetLufsBox : _inputGainBox;
        var text = box.Text.Replace('−', '-');
        var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (valid && double.IsFinite(value))
        {
            value = Math.Round(Clamp(value, target ? -36 : -30, target ? -6 : 30), 2);
            if (target) SetTrackTarget(track, value); else SetTrackInputGain(track, value);
        }
        SyncHoverEditor(true);
    }

    internal void SetTrackInputGain(AudioTrack track, double value)
    {
        if (!Tracks.Contains(track) || IsMissing(track) || !double.IsFinite(value)) return;
        value = Clamp(value, -30, 30);
        if (Math.Abs(track.InputGainDb - value) < .00001) return;
        Select(track);
        EditStarted?.Invoke();
        track.InputGainDb = value;
        InvalidateData(false);
        EditCommitted?.Invoke(track);
    }

    internal void SetTrackTarget(AudioTrack track, double? value)
    {
        if (!Tracks.Contains(track) || IsMissing(track) || (value.HasValue && !double.IsFinite(value.Value))) return;
        if (value.HasValue) value = Clamp(value.Value, -36, -6);
        if (track.TargetLufs == value) return;
        Select(track);
        EditStarted?.Invoke();
        track.TargetLufs = value;
        InvalidateData(false);
        EditCommitted?.Invoke(track);
    }

    private void ResetTrackTarget()
    {
        if (_editorTrack is { } track) SetTrackTarget(track, null);
        SyncHoverEditor(true);
    }

    private IReadOnlyList<EnvelopePoint> CombinedCurve(AudioTrack track)
    {
        if (_combinedCurves.TryGetValue(track.Id, out var cached)) return cached.Points;
        var hash = new HashCode();
        foreach (var point in track.Envelope) { hash.Add(point.Time); hash.Add(point.GainDb); }
        foreach (var point in track.TransitionEnvelope) { hash.Add(point.Time); hash.Add(point.GainDb); }
        var key = hash.ToHashCode();
        var points = EnvelopeMath.Canonicalize(TransitionDesigner.CombinedEnvelope(track));
        if (points.Count == 0 && (track.Envelope.Count > 0 || track.TransitionEnvelope.Count > 0))
            points = [new(0, 0), new(track.Duration, 0)];
        _combinedCurves[track.Id] = (key, points);
        return points;
    }

    private List<EnvelopePoint>? _projectEnvelope;
    public List<EnvelopePoint>? ProjectEnvelope
    {
        get => _projectEnvelope;
        set { _projectEnvelope = value; InvalidateData(); }
    }
    public event Action? ProjectEnvelopeEdited;
    public bool SliceMode { get; set; }
    public event Action<AudioTrack, double>? SliceRequested;
    private List<EnvelopePoint> EditableEnvelope(AudioTrack track) => _projectEnvelope ?? track.Envelope;
    private double EditableEnvelopeOffset(AudioTrack track) => _projectEnvelope == null ? TrackOffset(track) : 0;
    private double EditableTransitionGain(AudioTrack track, double time) => _projectEnvelope == null ? TransitionDesigner.GainAt(track, time) : 0;
    private void NotifyEnvelopeEdited() { if (_projectEnvelope != null) ProjectEnvelopeEdited?.Invoke(); }

    public IList<AudioTrack> Tracks { get => _tracks; set { _tracks = value ?? new List<AudioTrack>(); Refresh(); } }
    public ProcessingSettings Settings
    {
        get => _settings;
        set
        {
            if (_settings == value) return;
            _settings = value;
            SyncHoverEditor();
            InvalidateData(false);
        }
    }
    public WaveformViewMode ViewMode { get => _viewMode; set { if (_viewMode == value) return; _viewMode = value; _waveformDirty = _overviewDirty = true; InvalidateTimeline(); } }
    public bool ShowEnvelopeOnWaveform { get => _showEnvelopeOnWaveform; set { _showEnvelopeOnWaveform = value; InvalidateTimeline(); } }
    public bool ShowGainLines { get => _showGainLines; set { _showGainLines = value; InvalidateTimeline(); } }
    public AudioTrack? SelectedTrack { get => _selectedTrack; set { _selectedTrack = value; InvalidateTimeline(); } }
    public double Playhead { get => _playhead; set { var next = Math.Clamp(value, 0, TotalDuration); if (_playhead == next) return; _playhead = next; _dynamicDirty = true; PlayheadChanged?.Invoke(next); RequestFrame(); } }
    public event Action? ViewportChanged;
    public event Action<double>? PlayheadChanged;
    public double EmptyDurationHint
    {
        get => _emptyDurationHint;
        set
        {
            var duration = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (_emptyDurationHint == duration) return;
            _emptyDurationHint = duration; Refresh();
        }
    }
    public TimelineCursorInfo? CursorInfo => _cursorInfo;
    public event Action<TimelineCursorInfo?>? CursorInfoChanged;
    public event Action<AudioTrack?>? SelectionChanged;
    public event Action<double>? SeekRequested;
    public event Action<double>? ScrubStarted;
    public event Action<double>? ScrubPositionRequested;
    public event Action<double>? ScrubCompleted;
    public event Action? EditStarted;
    public event Action<AudioTrack>? EditCommitted;
    public event Action? RebuildRequested;
    public event Action<AudioTrack, int>? TrackMoveRequested;
    public Func<string[], bool>? CanDropFiles { get; set; }
    public event Action<string[], int>? FilesDropRequested;
    internal int DropInsertionIndex => _dropInsertionIndex;
    public event Action<AudioTrack>? TrackRemoveRequested;
    public event Action<AudioTrack>? TrackRelinkRequested;
    public event Action<AudioTrack, Point>? TrackContextRequested;

    private double TotalDuration => _totalDuration;
    private Rect Overview => new(12, 10, Math.Max(1, ActualWidth - 24), 48);
    private Rect WaveArea => new(48, 91 + CustomBoundaryBarHeight, Math.Max(1, ActualWidth - 96), Math.Max(120, ActualHeight - 99 - CustomBoundaryBarHeight));
    private Rect SignalArea => TimelineLayout.SignalArea(WaveArea);
    internal Rect PlotArea => WaveArea;
    internal Rect SignalPlotArea => SignalArea;
    internal double SignalCenterY => TimelineLayout.CenterY(SignalArea);
    internal double SignalAmplitude => TimelineLayout.WaveformAmplitude(SignalArea);
    internal Rect OverviewArea => Overview;
    private Rect RulerArea => new(WaveArea.Left, Overview.Bottom + 5 + CustomBoundaryBarHeight, WaveArea.Width, WaveArea.Top - Overview.Bottom - 5 - CustomBoundaryBarHeight);
    private double ViewDuration => _viewDuration > 0 ? _viewDuration : Math.Max(TotalDuration, 1);
    private double End => _viewStart + ViewDuration;
    private double EnvelopeRange => _drag == DragKind.Envelope ? _dragRange : _computedEnvelopeRange;
    private double GainRange => _drag == DragKind.Gain ? _dragRange : _computedGainRange;
    private Brush B(string key, string fallback)
    {
        if (TryFindResource(key) is Brush resource) return resource;
        if (_fallbackBrushes.TryGetValue(fallback, out var cached)) return cached;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        brush.Freeze();
        _fallbackBrushes[fallback] = brush;
        return brush;
    }
    private Brush BackgroundBrush => B("BackgroundBrush", "#14191F");
    private Brush PanelBrush => B("PanelBrush", "#1D242D");
    private Brush BorderBrush => B("BorderBrush", "#35404C");
    private Brush TextBrush => B("TextBrush", "#E1E8EF");
    private Brush MutedBrush => B("MutedBrush", "#8F9DAE");
    private Brush AccentBrush => B("AccentBrush", "#5ACCB5");
    private bool IsLightTheme => PanelBrush is SolidColorBrush panel && panel.Color.R * .2126 + panel.Color.G * .7152 + panel.Color.B * .0722 > 140;
    private Brush BoostBrush => B("BoostBrush", IsLightTheme ? "#287AB4" : "#69B7F0");
    private Brush CutBrush => B("CutBrush", IsLightTheme ? "#B84E62" : "#E694A4");
    private Brush OriginalOutlineBrush => B("OriginalOutlineBrush", IsLightTheme ? "#59646F" : "#B0BCC8");
    private Brush MissingFillBrush => B("MissingBackgroundBrush", IsLightTheme ? "#F4E6E8" : "#352329");
    private Brush MissingBorderBrush => B("MissingBorderBrush", IsLightTheme ? "#C48F99" : "#6D3B45");
    private Brush MissingTextBrush => B("MissingTextBrush", IsLightTheme ? "#8E3C47" : "#DA9AA3");
    private static readonly Brush Amber = CreateAmber();
    private static Brush CreateAmber() { var brush = new SolidColorBrush(Color.FromRgb(242, 191, 105)); brush.Freeze(); return brush; }
    private static double Clamp(double value, double lo, double hi) => Math.Max(lo, Math.Min(hi, value));

    public void Refresh()
    {
        InvalidateData();
        EnsureDataMetrics();
        var total = TotalDuration;
        if (total != _knownTotalDuration && _navigationActive) StopNavigation();
        if (_viewDuration <= 0 || (_viewStart < .01 && Math.Abs(_viewDuration - _knownTotalDuration) < .11))
            _viewDuration = Math.Max(.1, total);
        _knownTotalDuration = total;
        ClampView();
        UpdateAnimation();
        if (_hoveredTrack != null && !Tracks.Contains(_hoveredTrack)) _hoveredTrack = null;
        if (_hoverNodeTrack != null && !Tracks.Contains(_hoverNodeTrack)) { _hoverNodeTrack = null; _hoverNodeIndex = -1; }
        SyncHoverEditor();
        foreach (var id in _overviewDrawings.Keys.Concat(_tileSnapshots.Keys).Distinct().Where(id => !Tracks.Any(track => track.Id == id)).ToArray())
        {
            _overviewDrawings.Remove(id);
            _combinedCurves.Remove(id);
            _tileSnapshots.Remove(id);
        }
        _tileRenderer.RetainTracks(Tracks.Select(track => track.Id));
        InvalidateVisual();
        InvalidateCamera();
    }

    private void EnsureDataMetrics()
    {
        if (!_metricsDirty) return;
        _metricsDirty = false;
        _metricsPassCount++;
        UpdateDataMetrics();
    }

    private void UpdateDataMetrics()
    {
        double total = 0, envelope = 0, gain = 0;
        _frameMatchingGain.Clear(); _framePreMatchLufs.Clear(); _frameApproximate.Clear(); _frameSources.Clear(); _trackOffsets.Clear(); _trackIndices.Clear();
        _interiorTrackBoundaries.Clear();
        var trackIndex = 0;
        foreach (var track in Tracks)
        {
            _trackIndices[track.Id] = trackIndex++;
            if (_trackOffsets.Count > 0 && (_interiorTrackBoundaries.Count == 0 || _interiorTrackBoundaries[^1] != total))
                _interiorTrackBoundaries.Add(total);
            _trackOffsets[track.Id] = total;
            total += track.Duration;
            if (IsMissing(track))
            {
                _frameMatchingGain[track.Id] = 0;
                _frameApproximate[track.Id] = false;
                continue;
            }
            var matches = RenderMatchesControls(track);
            var matching = EstimateMatchGain(track, matches);
            _frameMatchingGain[track.Id] = matching;
            _frameApproximate[track.Id] = IsStale(track) || ReferenceEquals(_editingTrack, track) || ReferenceEquals(_valueDragTrack, track) || !matches;
            gain = Math.Max(gain, Math.Abs(track.InputGainDb + matching + track.TrimDb));
            double authored = 0, transition = 0;
            foreach (var point in track.Envelope) authored = Math.Max(authored, Math.Abs(point.GainDb));
            foreach (var point in track.TransitionEnvelope) transition = Math.Max(transition, Math.Abs(point.GainDb));
            envelope = Math.Max(envelope, authored + transition);
        }
        _totalDuration = Tracks.Count == 0 ? Math.Max(total, _emptyDurationHint) : total;
        // Zero-length slots can share a boundary or sit at an album edge. Count
        // each visible dividing line once, never the album's outer edges.
        _interiorTrackBoundaries.RemoveAll(boundary => boundary <= 0 || boundary >= total);
        _handleVisibilityDirty = true;
        _computedEnvelopeRange = Math.Max(8, Math.Ceiling(envelope * 1.2 / 2) * 2);
        _computedGainRange = Math.Max(12, Math.Ceiling(gain * 1.15 / 3) * 3);
    }

    private DrawingSource SourceFor(AudioTrack track, bool preview)
    {
        if (_frameSources.TryGetValue((track.Id, preview), out var cached)) return cached;
        var approximate = preview && (_frameApproximate.TryGetValue(track.Id, out var pending) ? pending : NeedsPreviewEstimate(track));
        var hash = new HashCode();
        if (approximate)
        {
            foreach (var point in track.Envelope) { hash.Add(point.Time); hash.Add(point.GainDb); }
            foreach (var point in track.TransitionEnvelope) { hash.Add(point.Time); hash.Add(point.GainDb); }
            hash.Add(track.InputGainDb); hash.Add(track.TargetLufs);
        }
        var source = new DrawingSource(track.Original, track.Render, track.OriginalPeaks, track.PreviewPeaks,
            track.OriginalWaveform, track.Render?.Waveform,
            approximate, approximate ? Settings : null, approximate ? track.TrimDb : 0, approximate ? hash.ToHashCode() : 0);
        _frameSources[(track.Id, preview)] = source;
        return source;
    }

    private WaveformTileTrack TileSnapshot(AudioTrack track)
    {
        var key = SourceFor(track, true);
        if (_tileSnapshots.TryGetValue(track.Id, out var cached) && cached.Key == key) return cached.Snapshot;
        var original = new WaveformTileSource((object?)track.OriginalWaveform ?? track.Original,
            track.OriginalWaveform, track.OriginalPeaks, track.SampleRate, track.Duration);
        WaveformTileSource processed;
        if (key.Approximate)
        {
            var gain = new WaveformTileGain(CombinedCurve(track), Settings.LevelingEnabled ? Settings.Strength : 0,
                TotalGainEstimate(track), Settings.LimiterEnabled ? Settings.CeilingDb : null);
            processed = new WaveformTileSource(key, track.OriginalWaveform, track.OriginalPeaks,
                track.SampleRate, track.Duration, gain);
        }
        else if (track.Render?.LiveSource is { } live)
        {
            // Bypassed audio is mixed at playback/export time. Draw its native
            // music range without manufacturing a full-length mix waveform.
            // The background already has its own lane and waveform.
            var gain = live.GainDb == 0 ? null : new WaveformTileGain([], 0, live.GainDb, null);
            processed = new WaveformTileSource(track.Render, track.OriginalWaveform, track.OriginalPeaks,
                track.SampleRate, track.Duration, gain);
        }
        else
            processed = new WaveformTileSource((object?)track.Render?.Waveform ?? track.Render ?? (object)track.Original,
                track.Render?.Waveform, track.PreviewPeaks.Count > 0 ? track.PreviewPeaks : track.OriginalPeaks,
                track.Render is { RenderedSampleRate: > 0 } rendered ? rendered.RenderedSampleRate : track.SampleRate, track.Duration);
        var snapshot = new WaveformTileTrack(track.Id, original, processed);
        _tileSnapshots[track.Id] = (key, snapshot);
        return snapshot;
    }

    private static bool IsActivelyWorking(AudioTrack track) => track.WorkState is TrackWorkState.Analyzing or TrackWorkState.Processing;
    private static bool IsMissing(AudioTrack track) => track.WorkState == TrackWorkState.Missing;
    private static bool IsStale(AudioTrack track) => track.WorkState is not (TrackWorkState.Idle or TrackWorkState.Missing);
    private static bool CanEditTrack(AudioTrack track) => !IsMissing(track) && (track.Analysis != null || track.Original.Length > 0);

    private bool CurveMatchesRender(AudioTrack track)
    {
        if (track.Render == null) return false;
        var current = CombinedCurve(track);
        return current.All(point => point.GainDb == 0) && track.Render.RenderedEnvelope.All(point => point.GainDb == 0) || current.SequenceEqual(track.Render.RenderedEnvelope);
    }

    private bool RenderMatchesControls(AudioTrack track)
    {
        var render = track.Render;
        if (render == null) return false;
        var matching = Settings.MatchingEnabled ? Math.Clamp(Settings.MatchingStrength, 0, 1) : 0;
        var leveling = Settings.LevelingEnabled ? Math.Clamp(Settings.Strength, 0, 1) : 0;
        if (CombinedCurve(track).All(point => point.GainDb == 0)) leveling = 0;
        var renderedLeveling = render.RenderedEnvelope.All(point => point.GainDb == 0) ? 0 : render.RenderedLevelingStrength;
        return render.RenderedInputGainDb == track.InputGainDb && render.RenderedTrimDb == track.TrimDb &&
            (matching <= 0 || render.RenderedTargetLufs == (track.TargetLufs ?? Settings.TargetLufs)) &&
            render.RenderedMatchingStrength == matching && renderedLeveling == leveling && (leveling <= 0 || CurveMatchesRender(track)) &&
            render.RenderedLimiterEnabled == Settings.LimiterEnabled && (!Settings.LimiterEnabled || render.RenderedCeilingDb == Math.Clamp(Settings.CeilingDb, -12, 0)) &&
            (!Settings.LimiterEnabled || render.RenderedMaximizerStyle == Settings.MaximizerStyle &&
                render.RenderedMaximizerCharacter == Math.Clamp(Settings.MaximizerCharacter, 0, 1) &&
                render.RenderedTransientPreservation == Math.Clamp(Settings.TransientPreservation, 0, 1) &&
                render.RenderedBassProtection == Math.Clamp(Settings.BassProtection, 0, 1) &&
                render.RenderedDynamicsGuardEnabled == Settings.DynamicsGuardEnabled &&
                (!Settings.DynamicsGuardEnabled || render.RenderedMaxSustainedReductionDb == Math.Clamp(Settings.MaxSustainedReductionDb, 0, 12)));
    }

    private bool NeedsPreviewEstimate(AudioTrack track) => IsStale(track) || ReferenceEquals(_editingTrack, track) ||
        ReferenceEquals(_valueDragTrack, track) || !RenderMatchesControls(track);

    internal double EstimatedMatchGain(AudioTrack track)
    {
        EnsureDataMetrics();
        return _frameMatchingGain.TryGetValue(track.Id, out var cached) ? cached : EstimateMatchGain(track, RenderMatchesControls(track));
    }

    internal double EstimatedOutputLufs(AudioTrack track)
    {
        if (track.Analysis == null || IsMissing(track)) return double.NaN;
        EnsureDataMetrics();
        if (RenderMatchesControls(track) && track.Render is { } ready && double.IsFinite(ready.OutputLufs))
            return ready.OutputLufs;
        var match = _frameMatchingGain.TryGetValue(track.Id, out var cached) ? cached : EstimatedMatchGain(track);
        var music = EstimatePreMatchLufs(track) + match + track.TrimDb;
        return BackgroundLevel(track) is { } bed ? SumLoudness(music, bed) : music;
    }

    private static double? BackgroundLevel(AudioTrack track)
        => track.Render is { IncludesBackground: true, BackgroundLufs: { } bed } && double.IsFinite(bed) && bed > -69 ? bed : null;

    private static double LoudnessEnergy(double lufs) => double.IsFinite(lufs) && lufs > -69 ? Math.Pow(10, lufs / 10) : 0;
    private static double SumLoudness(double music, double bed)
    {
        var energy = LoudnessEnergy(music) + LoudnessEnergy(bed);
        return energy > 0 ? 10 * Math.Log10(energy) : -70;
    }

    private double EstimateMatchGain(AudioTrack track, bool controlsMatch)
    {
        var matching = Settings.MatchingEnabled ? Math.Clamp(Settings.MatchingStrength, 0, 1) : 0;
        if (matching <= 0 || track.Analysis == null) return 0;
        if (controlsMatch) return track.Render!.MatchGainDb;
        var preMatch = EstimatePreMatchLufs(track);
        if (preMatch <= -69) return 0;
        var previous = track.Render is { PreMatchLufs: > -69 } ready ? ready : null;
        var target = track.TargetLufs ?? Settings.TargetLufs;
        if (BackgroundLevel(track) is { } bed)
        {
            // The bed is fixed. Blend the final mix LUFS, then solve only for
            // music energy; a target below the bed floor cannot be reached.
            var mix = SumLoudness(preMatch, bed);
            var desired = mix + (target - mix) * matching;
            var needed = LoudnessEnergy(desired) - LoudnessEnergy(bed);
            var gain = needed > 0 ? 10 * Math.Log10(needed) - preMatch : -36 * matching;
            return Math.Clamp(gain, -36 * matching, 36 * matching);
        }
        var estimate = Math.Clamp(target - preMatch, -36, 36) * matching;
        if (previous != null && previous.RenderedMatchingStrength == matching && previous.RenderedTargetLufs == target)
        {
            var baseline = Math.Clamp(target - previous.PreMatchLufs, -36, 36) * matching;
            return Math.Clamp(previous.MatchGainDb + estimate - baseline, -36 * matching, 36 * matching);
        }
        return estimate;
    }

    private double EstimatePreMatchLufs(AudioTrack track)
    {
        if (_framePreMatchLufs.TryGetValue(track.Id, out var cached)) return cached;
        if (track.Analysis == null) return double.NaN;
        var level = Settings.LevelingEnabled ? Math.Clamp(Settings.Strength, 0, 1) : 0;
        var previous = track.Render is { PreMatchLufs: > -69 } ready ? ready : null;
        double preMatch;
        if (previous != null && previous.RenderedLevelingStrength == level && (level <= 0 || CurveMatchesRender(track)))
            preMatch = previous.PreMatchLufs + track.InputGainDb - previous.RenderedInputGainDb;
        else
        {
            double originalEnergy = 0, leveledEnergy = 0, previousLeveledEnergy = 0;
            var gate = Math.Max(-60, track.Analysis.IntegratedLufs - 20);
            var current = CombinedCurve(track);
            foreach (var frame in track.Analysis.ShortTerm)
            {
                if (!double.IsFinite(frame.Loudness) || frame.Loudness < gate) continue;
                var energy = Math.Pow(10, frame.Loudness / 10);
                var db = EnvelopeAt(current, frame.Time) * level;
                originalEnergy += energy; leveledEnergy += energy * Math.Pow(10, db / 10);
                if (previous != null)
                    previousLeveledEnergy += energy * Math.Pow(10, EnvelopeAt(previous.RenderedEnvelope, frame.Time) * previous.RenderedLevelingStrength / 10);
            }
            if (previous != null)
                // Anchor small edits to the measured preview. A profile estimate is useful
                // for the DELTA, but replacing its absolute LUFS makes tiny edits jump.
                preMatch = previous.PreMatchLufs + track.InputGainDb - previous.RenderedInputGainDb +
                    (leveledEnergy > 0 && previousLeveledEnergy > 0 ? 10 * Math.Log10(leveledEnergy / previousLeveledEnergy) : 0);
            else
                preMatch = track.Analysis.IntegratedLufs + track.InputGainDb +
                    (originalEnergy > 0 && leveledEnergy > 0 ? 10 * Math.Log10(leveledEnergy / originalEnergy) : 0);
        }
        _framePreMatchLufs[track.Id] = preMatch;
        return preMatch;
    }

    private void UpdateAnimation()
    {
        _workingAnimation = IsLoaded && IsVisible && Tracks.Any(IsActivelyWorking);
        if (_workingAnimation) RequestFrame();
    }

    public void FitAll() { StopNavigation(); EnsureDataMetrics(); _viewStart = 0; _viewDuration = Math.Max(1, TotalDuration); ClampView(); InvalidateCamera(); }
    public void FitTrack(AudioTrack track)
    {
        StopNavigation();
        _viewStart = TrackOffset(track);
        _viewDuration = Math.Max(.1, track.Duration);
        ClampView();
        InvalidateCamera();
    }
    private void ClampView()
    {
        var total = Math.Max(.1, TotalDuration);
        _viewDuration = Clamp(_viewDuration <= 0 ? total : _viewDuration, Math.Min(.05, total), total);
        _viewStart = Clamp(_viewStart, 0, Math.Max(0, total - _viewDuration));
    }

    public double ViewStartSeconds => _viewStart;
    public double VisibleDurationSeconds => ViewDuration;
    internal bool IsNavigationAnimating => _navigationActive;

    private void StartNavigation(double start, double duration)
    {
        var total = Math.Max(.1, TotalDuration);
        var nextDuration = Clamp(duration, Math.Min(.05, total), total);
        var nextStart = Clamp(start, 0, Math.Max(0, total - nextDuration));
        if (_navigationActive && nextStart == _targetViewStart && nextDuration == _targetViewDuration) return;
        // Retarget from the currently displayed camera, not the last wheel's
        // origin. A finite ease avoids the former half-second exponential tail.
        if (_navigationActive) AdvanceNavigation();
        _navigationFromStart = _viewStart; _navigationFromDuration = ViewDuration;
        _targetViewDuration = nextDuration; _targetViewStart = nextStart;
        _navigationStartedTick = Stopwatch.GetTimestamp();
        _navigationActive = _viewStart != nextStart || ViewDuration != nextDuration;
        if (_navigationActive) RequestFrame();
    }

    private void AdvanceNavigation()
        => AdvanceNavigationProgress((Stopwatch.GetTimestamp() - _navigationStartedTick) / (double)Stopwatch.Frequency);

    private void AdvanceNavigationProgress(double elapsedSeconds)
    {
        var progress = Clamp(elapsedSeconds / NavigationSeconds, 0, 1);
        var remaining = 1 - progress;
        var fraction = 1 - remaining * remaining * remaining;
        _viewStart = _navigationFromStart + (_targetViewStart - _navigationFromStart) * fraction;
        _viewDuration = _navigationFromDuration + (_targetViewDuration - _navigationFromDuration) * fraction;
        if (progress >= 1)
        {
            _viewStart = _targetViewStart;
            _viewDuration = _targetViewDuration;
            _navigationActive = false;
        }
        ClampView();
        InvalidateCamera();
    }

    // The detached Windows fixture uses the production camera interpolation at
    // deterministic presentation times, without sleeping or scanning long PCM.
    internal void AdvanceNavigationForTesting(double elapsedSeconds)
    {
        if (_navigationActive) AdvanceNavigationProgress(elapsedSeconds);
        RenderPendingLayers();
    }
    internal long NavigationEnvelopeNodeDraws => _navigationEnvelopeNodeDraws;
    internal long NavigationEnvelopeVertices => _navigationEnvelopeVertices;

    private void StopNavigation()
    {
        _navigationActive = false;
        _targetViewStart = _viewStart;
        _targetViewDuration = ViewDuration;
    }
    private double TrackOffset(AudioTrack track)
    {
        if (_trackOffsets.TryGetValue(track.Id, out var cached)) return cached;
        double sum = 0;
        foreach (var t in Tracks) { if (ReferenceEquals(t, track)) return sum; sum += t.Duration; }
        return sum;
    }
    private AudioTrack? TrackAt(double time)
    {
        double offset = 0;
        foreach (var t in Tracks)
        {
            if (time >= offset && time <= offset + t.Duration && t.Duration > 0) return t;
            offset += t.Duration;
        }
        return null;
    }
    private double X(double time) => WaveArea.Left + (time - _viewStart) / ViewDuration * WaveArea.Width;
    private double T(double x) => Clamp(_viewStart + (x - WaveArea.Left) / WaveArea.Width * ViewDuration, 0, TotalDuration);
    private Rect MainEnvelopeArea => SignalArea;
    private double EffectiveEnvelopeStrength => Settings.LevelingEnabled ? Math.Clamp(Settings.Strength, 0, 1) : 0;
    private bool CanEditEnvelope(AudioTrack track) => CanEditTrack(track) && EffectiveEnvelopeStrength > 0;
    // The axis stays in applied dB while its range follows the full authored
    // curve. Strength therefore moves points toward zero without changing them.
    private double EnvelopeY(double combinedGain, bool waveform = false)
        => TimelineLayout.GainY(MainEnvelopeArea, combinedGain * EffectiveEnvelopeStrength, EnvelopeRange);
    private double GainY(double gain) => TimelineLayout.GainY(MainEnvelopeArea, gain, GainRange);
    private double EnvelopeGain(double y, bool waveform = false)
        => EffectiveEnvelopeStrength > 0 ? TimelineLayout.GainAtY(MainEnvelopeArea, y, EnvelopeRange) / EffectiveEnvelopeStrength : 0;
    internal double AppliedEnvelopeGainAt(AudioTrack track, double time)
        => (_projectEnvelope == null ? EnvelopeAt(track.Envelope, time) + TransitionDesigner.GainAt(track, time) : EnvelopeAt(_projectEnvelope, TrackOffset(track) + time)) * EffectiveEnvelopeStrength;
    internal string EnvelopePointGainLabel(AudioTrack track, int index)
        => $"{(EditableEnvelope(track)[index].GainDb + EditableTransitionGain(track, EditableEnvelope(track)[index].Time)) * EffectiveEnvelopeStrength:+0.00;−0.00;0.00} dB";
    private void Select(AudioTrack? track)
    {
        if (!ReferenceEquals(_selectedTrack, track)) { _selectedTrack = track; SelectionChanged?.Invoke(track); }
        InvalidateTimeline();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (ActualWidth < 40 || ActualHeight < 100) return;
        dc.DrawRoundedRectangle(PanelBrush, new Pen(BorderBrush, 1), Overview, 3, 3);
        dc.DrawRectangle(PanelBrush, new Pen(BorderBrush, 1), WaveArea);
        RenderPendingLayers();
    }

    private void RenderPendingLayers()
    {
        if (ActualWidth < 40 || ActualHeight < 100) return;
        var renderStarted = Stopwatch.GetTimestamp();
        EnsureDataMetrics();
        RenderCustomBoundaryLayer();
        RenderLoopBackgroundLayer();
        if (_overviewDirty)
        {
            _overviewDirty = false; _overviewRenderCount++;
            using var dc = _overviewVisual.RenderOpen();
            double offset = 0;
            foreach (var track in Tracks) { if (TotalDuration > 0) DrawOverviewTrack(dc, track, offset); offset += track.Duration; }
        }
        if (_waveformDirty)
        {
            _waveformDirty = false; _waveformRenderCount++;
            using var background = _trackBackgroundVisual.RenderOpen();
            using var dc = _waveformVisual.RenderOpen();
            _tileRenderer.BeginFrame();
            try
            {
                double offset = 0;
                foreach (var track in Tracks)
                {
                    if (offset + track.Duration >= _viewStart && offset <= End)
                    {
                        DrawTrackBackground(background, track, offset);
                        DrawTrackWaveform(dc, track, offset);
                    }
                    offset += track.Duration;
                }
            }
            finally { _tileRenderer.EndFrame(); }
        }
        if (_hoverBackgroundDirty)
        {
            _hoverBackgroundDirty = false; _hoverBackgroundRenderCount++;
            using var dc = _hoverBackgroundVisual.RenderOpen();
            DrawHoveredTrackBackground(dc);
        }
        if (_editorDirty)
        {
            _editorDirty = false; _editorRenderCount++;
            using var dc = _editorVisual.RenderOpen();
            if (TotalDuration > 0)
            {
                double offset = 0;
                foreach (var track in Tracks)
                {
                    if (offset + track.Duration >= _viewStart && offset <= End) DrawTrack(dc, track, offset);
                    offset += track.Duration;
                }
                DrawRuler(dc);
            }
            TimelineScales.Draw(dc, SignalArea, MainEnvelopeArea, EnvelopeRange, ShowEnvelopeOnWaveform,
                BackgroundBrush, MutedBrush, BorderBrush, Amber, (value, brush) => CachedText(value, 10, brush, double.PositiveInfinity));
        }
        if (_dynamicDirty)
        {
            _dynamicDirty = false; _dynamicRenderCount++;
            using var dc = _dynamicVisual.RenderOpen();
            DrawDynamicLayer(dc);
        }
        var elapsed = Stopwatch.GetTimestamp() - renderStarted;
        _layerCpuTicks += elapsed; _maxLayerCpuTicks = Math.Max(_maxLayerCpuTicks, elapsed);
    }

    internal TimelineDiagnostics PerformanceCounters => new(_renderCycleCount, _waveformRenderCount, _overviewRenderCount,
        _editorRenderCount, _dynamicRenderCount, _metricsPassCount, _layerCpuTicks * 1000.0 / Stopwatch.Frequency,
        _maxLayerCpuTicks * 1000.0 / Stopwatch.Frequency, _tileRenderer.Diagnostics);
    internal bool HasPendingTiles => _tileRenderer.IsBusy;
    internal bool LivePreviewActive
    {
        get => _livePreviewActive;
        set { if (_livePreviewActive == value) return; _livePreviewActive = value; _waveformDirty = true; RequestFrame(); }
    }
    internal bool UseComparisonRasterization
    {
        get => _tileRenderer.UseComparisonRasterization;
        set
        {
            if (_tileRenderer.UseComparisonRasterization == value) return;
            _tileRenderer.UseComparisonRasterization = value;
            _waveformDirty = true;
            RequestFrame();
        }
    }
    internal void FlushPendingRendering()
    {
        Dispatcher.VerifyAccess();
        // Offscreen captures have no presentation clock. They use the same
        // elapsed-time camera update as the live CompositionTarget callback.
        if (_navigationActive && (!IsLoaded || !IsVisible)) AdvanceNavigation();
        RenderPendingLayers();
    }
    internal void ResetPerformanceCounters()
    {
        _renderCycleCount = _waveformRenderCount = _overviewRenderCount = _editorRenderCount = _dynamicRenderCount = _metricsPassCount = 0;
        _hoverBackgroundRenderCount = 0;
        _layerCpuTicks = _maxLayerCpuTicks = 0;
        _navigationEnvelopeNodeDraws = _navigationEnvelopeVertices = 0;
    }

    private void DrawTrackBackground(DrawingContext dc, AudioTrack track, double offset)
    {
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        if (right <= left) return;
        if (IsMissing(track))
        {
            dc.DrawRectangle(MissingFillBrush, null, new Rect(left, WaveArea.Top, right - left, WaveArea.Height));
            return;
        }
        if (track.TargetLufs is { } target)
        {
            var difference = target - Settings.TargetLufs;
            var tint = Math.Abs(difference) < .00001 ? MutedBrush : difference > 0 ? B("HigherTargetBrush", "#5096D7") : B("LowerTargetBrush", "#CF6C7A");
            dc.PushOpacity(Math.Abs(difference) < .00001 ? .08 : .10);
            dc.DrawRectangle(tint, null, new Rect(left, WaveArea.Top, right - left, WaveArea.Height));
            dc.Pop();
        }
    }

    private void DrawTrackWaveform(DrawingContext dc, AudioTrack track, double offset)
    {
        if (IsMissing(track)) return;
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        if (right <= left) return;
        var center = SignalCenterY;
        var stale = IsStale(track);
        dc.DrawLine(new Pen(BorderBrush, .6), new Point(left, center), new Point(right, center));
        var area = new Rect(left, SignalArea.Top, right - left, SignalArea.Height);
        dc.PushClip(new RectangleGeometry(area));
        if (stale) dc.PushOpacity(.38);
        var style = new WaveformTileStyle(stale ? MutedBrush : AccentBrush, stale ? MutedBrush : BoostBrush,
            stale ? MutedBrush : CutBrush, OriginalOutlineBrush);
        _tileRenderer.DrawTrack(dc, TileSnapshot(track), _viewStart, ViewDuration, SignalArea, offset,
            center, SignalAmplitude, ViewMode, style, VisualTreeHelper.GetDpi(this).PixelsPerDip,
            _livePreviewActive || _projectEnvelope != null && _drag == DragKind.Envelope ||
            ReferenceEquals(track, _editingTrack) && (_drag is DragKind.Envelope or DragKind.Gain) ||
            ReferenceEquals(track, _valueDragTrack) && _valueDragChanged);
        if (stale) dc.Pop();
        dc.Pop();
    }

    internal long HoverBackgroundRedrawCount => _hoverBackgroundRenderCount;
    internal Rect HoveredTrackHighlightBounds => _hoverHighlightBounds;

    private void SetTrackHoverPointer(Point? point)
    {
        if (point.HasValue && !WaveArea.Contains(point.Value) && !RulerArea.Contains(point.Value)) point = null;
        if (_trackHoverPointer == point) return;
        _trackHoverPointer = point;
        _hoverBackgroundDirty = _dynamicDirty = true;
        RequestFrame();
    }

    private void DrawHoveredTrackBackground(DrawingContext dc)
    {
        _hoverHighlightBounds = Rect.Empty;
        if (_trackHoverPointer is not { } point || !WaveArea.Contains(point) || TotalDuration <= 0) return;
        var time = T(point.X);
        // Usually the existing hovered track is already correct. Resolve again
        // only when the camera moves beneath a stationary pointer or a native
        // editor keeps its old hover/focus while the pointer moves elsewhere.
        var track = _hoveredTrack;
        if (track == null || !_trackIndices.TryGetValue(track.Id, out var index) || index >= Tracks.Count || !ReferenceEquals(Tracks[index], track) ||
            !_trackOffsets.TryGetValue(track.Id, out var offset) || track.Duration <= 0 ||
            time < offset || (time == offset && offset > 0) || time > offset + track.Duration)
            track = TrackAt(time);
        if (track == null) return;
        var start = TrackOffset(track);
        var left = Math.Max(WaveArea.Left, X(start));
        var right = Math.Min(WaveArea.Right, X(start + track.Duration));
        if (right <= left) return;
        _hoverHighlightBounds = new Rect(left, WaveArea.Top, right - left, WaveArea.Height);
        dc.PushOpacity(IsLightTheme ? .024 : .028);
        dc.DrawRectangle(IsLightTheme ? Brushes.Black : Brushes.White, null, _hoverHighlightBounds);
        dc.Pop();
    }

    private void DrawDynamicLayer(DrawingContext dc)
    {
        DrawLoopRuler(dc);
        DrawHoverCursorLine(dc);
        if (Tracks.Count == 0 || TotalDuration <= 0)
        {
            DrawCursorMarkers(dc);
            var working = Tracks.FirstOrDefault(IsActivelyWorking);
            if (working != null) DrawWorkOverlay(dc, working, WaveArea.Left, WaveArea.Right);
            return;
        }
        DrawTrackDropMarker(dc);
        var overviewSelection = new Rect(Overview.Left + _viewStart / TotalDuration * Overview.Width, Overview.Top, Math.Max(2, ViewDuration / TotalDuration * Overview.Width), Overview.Height);
        dc.PushOpacity(.09);
        dc.DrawRectangle(AccentBrush, null, overviewSelection);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(AccentBrush, 1.5), overviewSelection, 2, 2);
        dc.DrawRectangle(AccentBrush, null, new Rect(overviewSelection.Left, Overview.Top + 17, 3, 14));
        dc.DrawRectangle(AccentBrush, null, new Rect(overviewSelection.Right - 3, Overview.Top + 17, 3, 14));
        double offset = 0;
        foreach (var track in Tracks)
        {
            if (IsStale(track) && offset + track.Duration >= _viewStart && offset <= End)
                DrawWorkOverlay(dc, track, Math.Max(WaveArea.Left, X(offset)), Math.Min(WaveArea.Right, X(offset + track.Duration)));
            var x1 = Overview.Left + offset / TotalDuration * Overview.Width;
            var x2 = Overview.Left + (offset + track.Duration) / TotalDuration * Overview.Width;
            if (IsActivelyWorking(track) && x2 - x1 > 30)
                ProcessingSpinner.DrawSpinner(dc, new Point((x1 + x2) / 2, Overview.Top + Overview.Height * .5), 8, _spinnerPhase, AccentBrush);
            offset += track.Duration;
        }
        var overviewPlayheadX = Overview.Left + Clamp(Playhead / TotalDuration, 0, 1) * Overview.Width;
        dc.DrawLine(new Pen(Amber, 1.5), new Point(overviewPlayheadX, Overview.Top + 1), new Point(overviewPlayheadX, Overview.Bottom - 1));
        var overviewHead = new StreamGeometry();
        using (var c = overviewHead.Open())
        {
            c.BeginFigure(new Point(overviewPlayheadX - 3, Overview.Top), true, true);
            c.LineTo(new Point(overviewPlayheadX + 3, Overview.Top), true, false);
            c.LineTo(new Point(overviewPlayheadX, Overview.Top + 5), true, false);
        }
        dc.DrawGeometry(Amber, null, overviewHead);
        if (Playhead >= _viewStart && Playhead <= End)
        {
            var x = X(Playhead);
            dc.DrawLine(new Pen(Amber, 1), new Point(x, RulerArea.Top + 9), new Point(x, WaveArea.Bottom));
            var head = new StreamGeometry();
            using (var c = head.Open()) { c.BeginFigure(new Point(x - 5, RulerArea.Top + 9), true, true); c.LineTo(new Point(x + 5, RulerArea.Top + 9), true, false); c.LineTo(new Point(x, RulerArea.Top + 17), true, false); }
            dc.DrawGeometry(Amber, null, head);
        }
        DrawCursorMarkers(dc);
    }

    internal Point? CrosshairPosition => CursorInfo.HasValue ? _crosshairPointer : null;

    internal void SetCrosshairPointer(Point? point)
    {
        if (point.HasValue && !SignalArea.Contains(point.Value)) point = null;
        if (_crosshairPointer == point) return;
        _crosshairPointer = point;
        if (!point.HasValue) PublishCursorInfo(null);
        _dynamicDirty = true;
        RequestFrame();
    }

    private void PublishCursorInfo(TimelineCursorInfo? info)
    {
        if (_cursorInfo == info) return;
        _cursorInfo = info;
        CursorInfoChanged?.Invoke(info);
    }

    private void DrawCursorMarkers(DrawingContext dc)
    {
        if (_crosshairPointer is not { } point || !SignalArea.Contains(point) || TotalDuration <= 0)
        {
            PublishCursorInfo(null);
            return;
        }
        var magnitude = Math.Abs((SignalCenterY - point.Y) / SignalAmplitude);
        PublishCursorInfo(new TimelineCursorInfo(T(point.X), magnitude == 0 ? double.NegativeInfinity : 20 * Math.Log10(magnitude),
            TimelineLayout.GainAtY(SignalArea, point.Y, EnvelopeRange)));
        // Cursor positions live inside the ruler and the axes' inner tick strips.
        // Keep the signal untouched and reuse only the retained dynamic layer.
        dc.PushClip(new RectangleGeometry(RulerArea));
        dc.DrawRectangle(AccentBrush, null, new Rect(point.X - .75, RulerArea.Bottom - 5, 1.5, 4));
        dc.Pop();
        if (ShowEnvelopeOnWaveform)
        {
            dc.PushClip(new RectangleGeometry(new Rect(SignalArea.Left - 36, SignalArea.Top, 36, SignalArea.Height)));
            dc.DrawRectangle(AccentBrush, null, new Rect(SignalArea.Left - 6, point.Y - .75, 5, 1.5));
            dc.Pop();
        }
        dc.PushClip(new RectangleGeometry(new Rect(SignalArea.Right, SignalArea.Top, 36, SignalArea.Height)));
        dc.DrawRectangle(AccentBrush, null, new Rect(SignalArea.Right + 1, point.Y - .75, 5, 1.5));
        dc.Pop();
    }

    private void DrawOverviewTrack(DrawingContext dc, AudioTrack track, double offset)
    {
        var x1 = Overview.Left + offset / TotalDuration * Overview.Width;
        var x2 = Overview.Left + (offset + track.Duration) / TotalDuration * Overview.Width;
        if (IsMissing(track))
        {
            dc.DrawRectangle(MissingFillBrush, null, new Rect(x1, Overview.Top + 1, Math.Max(0, x2 - x1), Overview.Height - 2));
            dc.DrawLine(new Pen(MissingBorderBrush, 1), new Point(x1, Overview.Top), new Point(x1, Overview.Bottom));
            return;
        }
        var center = Overview.Top + Overview.Height * .5;
        var stale = IsStale(track);
        var key = new OverviewDrawingKey(SourceFor(track, ViewMode != WaveformViewMode.Original), offset, track.Duration, TotalDuration, Overview, ViewMode != WaveformViewMode.Original);
        if (!_overviewDrawings.TryGetValue(track.Id, out var drawing) || drawing.Key != key)
        {
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
                for (var x = (int)x1; x < x2; x += 2)
                {
                    var a = Clamp((x - x1) / Math.Max(1, x2 - x1), 0, 1);
                    var b = Clamp((x + 2 - x1) / Math.Max(1, x2 - x1), 0, 1);
                    var peak = Peak(track, a * track.Duration, b * track.Duration, ViewMode != WaveformViewMode.Original);
                    c.BeginFigure(new Point(x, center - Clamp(peak.Max, 0, 1) * 20), false, false);
                    c.LineTo(new Point(x, center - Clamp(peak.Min, -1, 0) * 20), true, false);
                }
            geometry.Freeze();
            drawing = new OverviewDrawing(key, geometry);
            _overviewDrawings[track.Id] = drawing;
        }
        if (stale) dc.PushOpacity(.42);
        dc.DrawGeometry(null, new Pen(stale ? MutedBrush : AccentBrush, 1.2), drawing.Geometry);
        if (stale) dc.Pop();
        dc.DrawLine(new Pen(BorderBrush, 1), new Point(x1, Overview.Top), new Point(x1, Overview.Bottom));

    }

    private void DrawTrack(DrawingContext dc, AudioTrack track, double offset)
    {
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        if (right <= left) return;
        var selected = ReferenceEquals(track, SelectedTrack);
        if (selected)
        {
            dc.PushOpacity(.065);
            dc.DrawRectangle(AccentBrush, null, new Rect(left, WaveArea.Top, right - left, WaveArea.Bottom - WaveArea.Top));
            dc.Pop();
        }
        var center = SignalCenterY;
        var stale = IsStale(track);
        if (!IsMissing(track))
        {
            if (ShowEnvelopeOnWaveform) DrawEnvelope(dc, track, offset, left, right, true);
            if (ShowGainLines) DrawGainLine(dc, track, offset, left, right);
        }
        var titleReservation = ReferenceEquals(track, _hoveredTrack) && (HoverEditorFits(track) || IsMissing(track)) ? stale && !IsActivelyWorking(track) ? 72 : 42 : stale && !IsActivelyWorking(track) ? 42 : 12;
        Text(dc, track.Name, 11, left + 6, WaveArea.Top + 4, selected ? TextBrush : MutedBrush, right - left - titleReservation);
        if (IsMissing(track)) DrawMissingTrack(dc, track, left, right);
        else if (track.WorkState == TrackWorkState.Unprocessed || track.Analysis == null && !stale && !(track.WorkState == TrackWorkState.Idle && track.Render != null)) Text(dc, track.Status, 12, left + 6, center - 8, MutedBrush, right - left - 12);
        else if (track.Analysis != null && right - left > 160)
            Text(dc, $"{track.Analysis.IntegratedLufs:0.0} LUFS" + (track.Render != null && double.IsFinite(track.Render.OutputLufs) ? $"  →  {track.Render.OutputLufs:0.0} LUFS" : ""), 10, left + 6, WaveArea.Bottom - 16, MutedBrush, right - left - 12);

        if (ReferenceEquals(track, _hoveredTrack)) DrawTrackHandle(dc, track);

        var boundary = X(offset);
        if (boundary >= WaveArea.Left)
            dc.DrawLine(new Pen(BorderBrush, 1.2), new Point(boundary, WaveArea.Top), new Point(boundary, WaveArea.Bottom));
    }

    private void DrawMissingTrack(DrawingContext dc, AudioTrack track, double left, double right)
    {
        var button = MissingRelinkBounds(track);
        if (button.IsEmpty) return;
        var available = Math.Max(0, right - left - 12);
        if (available >= 42)
        {
            var label = CachedText("File not found", 12, MissingTextBrush, available);
            dc.DrawText(label, new Point((left + right - label.Width) / 2, button.Top - label.Height - 9));
        }
        dc.DrawRoundedRectangle(PanelBrush, new Pen(MissingBorderBrush, 1), button, 4, 4);
        if (button.Width >= 48)
        {
            var label = CachedText("Relink…", 12, TextBrush, Math.Max(1, button.Width - 10));
            dc.DrawText(label, new Point(button.Left + (button.Width - label.Width) / 2, button.Top + (button.Height - label.Height) / 2));
        }
        else
        {
            // Keep a recognizable link affordance when a zoomed-out clip is
            // too narrow for text. The same bounds remain clickable.
            var center = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2);
            dc.PushClip(new RectangleGeometry(button));
            var pen = new Pen(TextBrush, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawRoundedRectangle(null, pen, new Rect(center.X - 7, center.Y - 2, 9, 5), 2, 2);
            dc.DrawRoundedRectangle(null, pen, new Rect(center.X - 2, center.Y - 4, 9, 5), 2, 2);
            dc.Pop();
        }
    }

    internal Rect MissingRelinkBounds(AudioTrack track)
    {
        if (!Tracks.Contains(track) || !IsMissing(track)) return Rect.Empty;
        EnsureDataMetrics();
        var offset = TrackOffset(track);
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        if (right - left < 8) return Rect.Empty;
        var width = Math.Min(96, right - left - Math.Min(12, (right - left) * .2));
        return new Rect((left + right - width) / 2, SignalCenterY - 15, width, 30);
    }

    private AudioTrack? HitMissingRelink(Point point)
    {
        if (!WaveArea.Contains(point)) return null;
        var track = TrackAt(T(point.X));
        return track != null && MissingRelinkBounds(track).Contains(point) ? track : null;
    }

    internal bool HandleMissingRelinkClick(Point point)
    {
        var track = HitMissingRelink(point);
        if (track == null) return false;
        Select(track);
        TrackRelinkRequested?.Invoke(track);
        return true;
    }

    private void DrawEnvelope(DrawingContext dc, AudioTrack track, double offset, double left, double right, bool waveform)
    {
        var selected = ReferenceEquals(track, SelectedTrack);
        var clip = new Rect(left, SignalArea.Top, right - left, SignalArea.Height);
        dc.PushClip(new RectangleGeometry(clip));
        if ((EditableEnvelope(track).Count > 0 || track.TransitionEnvelope.Count > 0) && track.Analysis != null)
        {
            var combined = _projectEnvelope == null ? CombinedCurve(track) : _projectEnvelope;
            var curveOffset = _projectEnvelope == null ? TrackOffset(track) : 0;
            var nodeOffset = EditableEnvelopeOffset(track);
            var geo = VisibleEnvelopeGeometry(combined, curveOffset, left, right, waveform);
            dc.PushOpacity(Settings.LevelingEnabled ? 1 : .35);
            if (waveform) dc.DrawGeometry(null, new Pen(BackgroundBrush, 3.6), geo);
            dc.DrawGeometry(null, new Pen(Amber, waveform ? 1.7 : 1.5), geo);
            var editablePoints = EditableEnvelope(track);
            var firstNode = EnvelopeLowerBound(editablePoints, T(left - 6) - nodeOffset, false);
            var lastNodeX = double.NegativeInfinity;
            for (var index = firstNode; index < editablePoints.Count; index++)
            {
                var point = editablePoints[index];
                var x = X(nodeOffset + point.Time);
                if (x > right + 6) break;
                if (x < left - 6) continue;
                bool emphasized = ReferenceEquals(_editingTrack, track) && index == _editingNode ||
                    ReferenceEquals(_hoverNodeTrack, track) && index == _hoverNodeIndex;
                // Overlapping handles add thousands of native draw commands on
                // hour-long curves. During wheel motion retain a readable screen
                // density, then restore every editable point on the final frame.
                if (_navigationActive && !emphasized && x - lastNodeX < 8) continue;
                var radius = waveform ? selected ? 5 : 4 : selected ? 4 : 3;
                dc.DrawEllipse(PanelBrush, new Pen(Amber, waveform ? 1.7 : 1.4), new Point(x, EnvelopeY(point.GainDb + EditableTransitionGain(track, point.Time), waveform)), radius, radius);
                if (_navigationActive) _navigationEnvelopeNodeDraws++;
                lastNodeX = x;
            }
            var tipIndex = ReferenceEquals(_editingTrack, track) && _editingNode >= 0 ? _editingNode : ReferenceEquals(_hoverNodeTrack, track) ? _hoverNodeIndex : -1;
            if (tipIndex >= 0 && tipIndex < EditableEnvelope(track).Count)
            {
                var point = EditableEnvelope(track)[tipIndex];
                var x = X(nodeOffset + point.Time);
                var combinedGain = point.GainDb + EditableTransitionGain(track, point.Time);
                var y = EnvelopeY(combinedGain, waveform);
                var label = EnvelopePointGainLabel(track, tipIndex);
                var width = GainLabelWidth(label, TextBrush, right - left - 4);
                if (width >= 20)
                {
                    var badgeX = Clamp(x + 9, left + 2, right - width - 2);
                    var badgeY = Clamp(y - 23, clip.Top + 2, clip.Bottom - 19);
                    var badge = new Rect(badgeX, badgeY, width, 17);
                    dc.DrawRoundedRectangle(PanelBrush, new Pen(Amber, .7), badge, 3, 3);
                    Text(dc, label, 11, badgeX + 3, badgeY + 1, TextBrush, width - 6);
                }
            }
            dc.Pop();
        }
        dc.Pop();
    }

    internal int VisibleInteriorBoundaryCount
    {
        get
        {
            EnsureDataMetrics();
            if (!_handleVisibilityDirty) return _visibleInteriorBoundaryCount;
            _handleVisibilityDirty = false;
            _visibleInteriorBoundaryCount = 0;
            var epsilon = Math.Max(1e-7, TotalDuration * 1e-10);
            var first = _viewStart - epsilon;
            var last = End + epsilon;
            int lo = 0, hi = _interiorTrackBoundaries.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_interiorTrackBoundaries[mid] < first) lo = mid + 1;
                else hi = mid;
            }
            for (var index = lo; index < _interiorTrackBoundaries.Count && _visibleInteriorBoundaryCount < 3; index++)
            {
                if (_interiorTrackBoundaries[index] > last) break;
                _visibleInteriorBoundaryCount++;
            }
            return _visibleInteriorBoundaryCount;
        }
    }

    internal Rect TrackHandleRect(AudioTrack track)
    {
        if (VisibleInteriorBoundaryCount < 3) return Rect.Empty;
        var offset = TrackOffset(track);
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        if (right - left < 24) return Rect.Empty;
        var width = Math.Min(32, right - left - 4);
        var x = (left + right - width) / 2;
        if (!IsMissing(track) && HoverEditorFits(track)) x = Math.Min(x, right - _hoverEditor.DesiredSize.Width - width - 18);
        return new Rect(Math.Max(left + 2, x), WaveArea.Top + 27, width, 20);
    }

    internal bool HandleTrackGripPointerDown(Point point)
    {
        if (_hoveredTrack == null || !TrackHandleRect(_hoveredTrack).Contains(point)) return false;
        Select(_hoveredTrack);
        _pendingTrackDrag = _hoveredTrack;
        _trackDragStart = point;
        CaptureMouse();
        return true;
    }

    private bool HoverEditorFits(AudioTrack track)
    {
        var offset = TrackOffset(track);
        return Math.Min(WaveArea.Right, X(offset + track.Duration)) - Math.Max(WaveArea.Left, X(offset)) >= 220;
    }

    private Rect TrackTitleRect(AudioTrack track)
    {
        var offset = TrackOffset(track);
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        return right <= left ? Rect.Empty : new Rect(left, WaveArea.Top, right - left, TimelineLayout.FilenameHeight);
    }

    private double TotalGainEstimate(AudioTrack track)
    {
        EnsureDataMetrics();
        return track.InputGainDb + (_frameMatchingGain.TryGetValue(track.Id, out var match) ? match : EstimatedMatchGain(track)) + track.TrimDb;
    }
    internal double GainLineY(AudioTrack track) => Clamp(GainY(TotalGainEstimate(track)), MainEnvelopeArea.Top + 2, MainEnvelopeArea.Bottom - 2);
    private Brush GainLineBrush => B("GainLineBrush", IsLightTheme ? "#4E5864" : "#D0D6DD");
    private Brush GainLineOutlineBrush => B("GainLineOutlineBrush", IsLightTheme ? "#F4F5F7" : "#11151A");
    private string GainLabelText(AudioTrack track) => $"{TotalGainEstimate(track):+0.0;−0.0;0.0} dB";
    private double GainLabelWidth(string label, Brush brush, double available)
        => Math.Min(Math.Ceiling(CachedText(label, 11, brush, double.PositiveInfinity).WidthIncludingTrailingWhitespace) + 6,
            Math.Max(0, available));

    private Rect GainLabelRect(AudioTrack track, double offset, double left, double right)
    {
        var y = GainLineY(track);
        var width = GainLabelWidth(GainLabelText(track), GainLineBrush, Math.Max(20, right - left - 12));
        var label = new Rect(left + 6, y - 19, width, 17);
        var collides = GainLabelOverlapsEnvelope(track, offset, label);
        if (label.Top < MainEnvelopeArea.Top || collides) label.Y = y + 3;
        if (label.Bottom > MainEnvelopeArea.Bottom - 2) label.Y = y - 19;
        return label;
    }

    private void DrawGainLine(DrawingContext dc, AudioTrack track, double offset, double left, double right)
    {
        var brush = GainLineBrush;
        var y = GainLineY(track);
        var thickness = ReferenceEquals(track, SelectedTrack) ? 1.5 : 1;
        var outlineThickness = thickness + 2;
        // Dash lengths are measured in pen widths. Match both passes in screen
        // space so the contrast edge surrounds each dash without filling its gaps.
        var outlineDash = new DashStyle(new[] { 2 * thickness / outlineThickness, 2 * thickness / outlineThickness }, 0);
        var start = new Point(left + 1, y);
        var end = new Point(right - 1, y);
        dc.DrawLine(new Pen(GainLineOutlineBrush, outlineThickness) { DashStyle = outlineDash, DashCap = PenLineCap.Flat }, start, end);
        dc.DrawLine(new Pen(brush, thickness) { DashStyle = DashStyles.Dash, DashCap = PenLineCap.Flat }, start, end);
        var label = GainLabelRect(track, offset, left, right);
        dc.DrawRoundedRectangle(PanelBrush, null, label, 2, 2);
        Text(dc, GainLabelText(track), 11, label.Left + 3, label.Top + 1, brush, label.Width - 6);
    }

    private bool HitGainLabel(AudioTrack track, Point point)
    {
        if (!ShowGainLines || !SignalArea.Contains(point)) return false;
        var offset = TrackOffset(track);
        var left = Math.Max(WaveArea.Left, X(offset));
        var right = Math.Min(WaveArea.Right, X(offset + track.Duration));
        return GainLabelRect(track, offset, left, right).Contains(point);
    }

    private bool HitGainLine(AudioTrack track, Point point) => ShowGainLines && SignalArea.Contains(point) &&
        (Math.Abs(point.Y - GainLineY(track)) <= 5 || HitGainLabel(track, point));

    private void DrawTrackHandle(DrawingContext dc, AudioTrack track)
    {
        var rect = TrackHandleRect(track);
        if (rect.IsEmpty) return;
        dc.PushOpacity(.9);
        dc.DrawRoundedRectangle(PanelBrush, new Pen(BorderBrush, .8), rect, 4, 4);
        dc.Pop();
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 3; col++)
                dc.DrawEllipse(TextBrush, null, new Point(rect.Left + rect.Width / 2 - 7 + col * 7, rect.Top + 7 + row * 6), 1.4, 1.4);
    }

    internal void SetHoveredTrack(Point point, bool force = false)
    {
        SetTrackHoverPointer(point);
        if (!force && _editorTrack != null && Tracks.Contains(_editorTrack) && (_valueDragTrack != null || _hoverEditor.IsMouseOver || _hoverEditor.IsKeyboardFocusWithin || _removeTrackButton.IsMouseOver)) return;
        var track = WaveArea.Contains(point) ? TrackAt(T(point.X)) : null;
        if (ReferenceEquals(track, _hoveredTrack)) return;
        _hoveredTrack = track;
        SyncHoverEditor();
        InvalidateTimeline();
    }

    internal int TrackInsertionIndexAt(Point point)
    {
        var time = Overview.Contains(point)
            ? Clamp((point.X - Overview.Left) / Overview.Width * TotalDuration, 0, TotalDuration)
            : T(point.X);
        var bestIndex = 0;
        var bestDistance = Math.Abs(time);
        double boundary = 0;
        for (int i = 0; i < Tracks.Count; i++)
        {
            boundary += Tracks[i].Duration;
            var distance = Math.Abs(time - boundary);
            if (distance < bestDistance) { bestDistance = distance; bestIndex = i + 1; }
        }
        return bestIndex;
    }

    private bool TryDraggedTrack(IDataObject data, out AudioTrack? track)
    {
        track = null;
        if (!data.GetDataPresent(TrackDragDataFormat, false)) return false;
        var value = data.GetData(TrackDragDataFormat, false);
        var id = value is Guid guid ? guid : Guid.TryParse(value as string, out var parsed) ? parsed : Guid.Empty;
        track = Tracks.FirstOrDefault(candidate => candidate.Id == id);
        return track != null;
    }

    private void DrawTrackDropMarker(DrawingContext dc)
    {
        if (_dropInsertionIndex < 0 || _dropInsertionIndex > Tracks.Count) return;
        double boundary = 0;
        for (int i = 0; i < _dropInsertionIndex; i++) boundary += Tracks[i].Duration;
        var x = Clamp(X(boundary), WaveArea.Left + 1, WaveArea.Right - 1);
        dc.DrawLine(new Pen(AccentBrush, 3), new Point(x, WaveArea.Top), new Point(x, WaveArea.Bottom));
        dc.DrawLine(new Pen(AccentBrush, 3), new Point(x - 6, WaveArea.Top + 1), new Point(x + 6, WaveArea.Top + 1));
        dc.DrawLine(new Pen(AccentBrush, 3), new Point(x - 6, WaveArea.Bottom - 1), new Point(x + 6, WaveArea.Bottom - 1));
        if (TotalDuration > 0)
        {
            var overviewX = Overview.Left + boundary / TotalDuration * Overview.Width;
            dc.DrawLine(new Pen(AccentBrush, 2), new Point(overviewX, Overview.Top), new Point(overviewX, Overview.Bottom));
        }
    }

    private void ClearTrackDrop()
    {
        _dropInsertionIndex = -1;
        _dragPanTimer.Stop();
        InvalidateTimeline();
    }

    private void AutoPanTrackDrag()
    {
        var direction = _dropPointer.X < WaveArea.Left + 32 ? -1 : _dropPointer.X > WaveArea.Right - 32 ? 1 : 0;
        if (_dropInsertionIndex < 0 || direction == 0 || Overview.Contains(_dropPointer)) { _dragPanTimer.Stop(); return; }
        var previous = _viewStart;
        _viewStart += direction * ViewDuration * .035;
        ClampView();
        if (Math.Abs(previous - _viewStart) < .000001) _dragPanTimer.Stop();
        _dropInsertionIndex = TrackInsertionIndexAt(_dropPointer);
        InvalidateCamera();
    }

    private bool TryDroppedFiles(IDataObject data, out string[]? paths)
    {
        paths = data.GetDataPresent(DataFormats.FileDrop, false) ? data.GetData(DataFormats.FileDrop, false) as string[] : null;
        return paths != null && CanDropFiles?.Invoke(paths) == true;
    }

    internal bool PreviewFileDrop(IDataObject data, Point point)
    {
        if (!TryDroppedFiles(data, out _)) { ClearTrackDrop(); return false; }
        StopNavigation(); PreviewTrackDrop(point);
        return true;
    }

    private void UpdateTrackDrag(DragEventArgs e)
    {
        bool internalDrop = e.Data.GetDataPresent(TrackDragDataFormat, false);
        bool fileDrop = e.Data.GetDataPresent(DataFormats.FileDrop, false);
        if (!internalDrop && !fileDrop) { ClearTrackDrop(); return; }
        e.Handled = true;
        bool accepted = internalDrop ? TryDraggedTrack(e.Data, out _) && (e.AllowedEffects & DragDropEffects.Move) != 0
            : TryDroppedFiles(e.Data, out _) && (e.AllowedEffects & DragDropEffects.Copy) != 0;
        if (!accepted) { e.Effects = DragDropEffects.None; ClearTrackDrop(); return; }
        StopNavigation(); PreviewTrackDrop(e.GetPosition(this));
        e.Effects = internalDrop ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    internal void PreviewTrackDrop(Point point)
    {
        _dropPointer = point;
        _dropInsertionIndex = TrackInsertionIndexAt(point);
        if (!Overview.Contains(_dropPointer) && (_dropPointer.X < WaveArea.Left + 32 || _dropPointer.X > WaveArea.Right - 32))
            _dragPanTimer.Start();
        else _dragPanTimer.Stop();
        InvalidateTimeline();
    }

    protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); UpdateTrackDrag(e); }
    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); UpdateTrackDrag(e); }
    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        if (e.Data.GetDataPresent(TrackDragDataFormat, false) || e.Data.GetDataPresent(DataFormats.FileDrop, false)) e.Handled = true;
        ClearTrackDrop();
    }

    internal bool HandleTrackDrop(IDataObject data, Point point)
    {
        bool internalDrop = data.GetDataPresent(TrackDragDataFormat, false);
        bool fileDrop = data.GetDataPresent(DataFormats.FileDrop, false);
        var index = TrackInsertionIndexAt(point);
        ClearTrackDrop();
        if (internalDrop)
        {
            if (TryDraggedTrack(data, out var track)) TrackMoveRequested?.Invoke(track!, index);
        }
        else if (fileDrop && TryDroppedFiles(data, out var paths)) FilesDropRequested?.Invoke(paths!, index);
        return internalDrop || fileDrop;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        bool internalDrop = e.Data.GetDataPresent(TrackDragDataFormat, false);
        bool fileDrop = e.Data.GetDataPresent(DataFormats.FileDrop, false);
        if (!internalDrop && !fileDrop) { ClearTrackDrop(); return; }
        bool accepted = internalDrop ? TryDraggedTrack(e.Data, out _) && (e.AllowedEffects & DragDropEffects.Move) != 0
            : TryDroppedFiles(e.Data, out _) && (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Handled = true;
        e.Effects = accepted ? internalDrop ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None;
        if (accepted) HandleTrackDrop(e.Data, e.GetPosition(this)); else ClearTrackDrop();
    }

    private void DrawWorkOverlay(DrawingContext dc, AudioTrack track, double left, double right)
    {
        var rect = WorkIconRect(track, left, right);
        if (rect.IsEmpty) return;
        var iconCenter = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        if (IsActivelyWorking(track))
        {
            dc.PushOpacity(.9);
            dc.DrawEllipse(PanelBrush, new Pen(BorderBrush, .8), iconCenter, rect.Width / 2, rect.Height / 2);
            dc.Pop();
            ProcessingSpinner.DrawSpinner(dc, iconCenter, Math.Min(15, rect.Width * .39), _spinnerPhase, AccentBrush);
        }
        else if (track.WorkState == TrackWorkState.Queued)
        {
            var pen = new Pen(MutedBrush, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawEllipse(null, pen, iconCenter, 7, 7);
            dc.DrawLine(pen, iconCenter, new Point(iconCenter.X, iconCenter.Y - 4));
            dc.DrawLine(pen, iconCenter, new Point(iconCenter.X + 3.5, iconCenter.Y + 2));
        }
        else
        {
            dc.DrawRoundedRectangle(PanelBrush, new Pen(BorderBrush, .8), rect, 4, 4);
            var path = new StreamGeometry();
            using (var c = path.Open())
            {
                c.BeginFigure(new Point(iconCenter.X + 5.5, iconCenter.Y - 3), false, false);
                c.ArcTo(new Point(iconCenter.X + 5.5, iconCenter.Y + 3), new Size(6.4, 6.4), 0, true, SweepDirection.Counterclockwise, true, false);
            }
            var pen = new Pen(AccentBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, path);
            dc.DrawLine(pen, new Point(iconCenter.X + 5.5, iconCenter.Y - 3), new Point(iconCenter.X + 1.5, iconCenter.Y - 3));
            dc.DrawLine(pen, new Point(iconCenter.X + 5.5, iconCenter.Y - 3), new Point(iconCenter.X + 5.5, iconCenter.Y - 7));
        }
    }

    private Rect WorkIconRect(AudioTrack track, double left, double right)
    {
        if (IsMissing(track)) return Rect.Empty;
        if (right - left < 30) return Rect.Empty;
        if (IsActivelyWorking(track))
        {
            var size = Math.Min(40, right - left - 4);
            return new Rect((left + right - size) / 2, SignalCenterY - size / 2, size, size);
        }
        if (ReferenceEquals(track, _hoveredTrack) && HoverEditorFits(track)) return right - left < 58 ? Rect.Empty : new Rect(right - 56, WaveArea.Top + 2, 24, 24);
        return new Rect(right - 28, WaveArea.Top + 2, 24, 24);
    }

    private AudioTrack? HitWorkIcon(Point p)
    {
        double offset = 0;
        foreach (var track in Tracks)
        {
            if (IsStale(track))
            {
                var rect = WorkIconRect(track, Math.Max(WaveArea.Left, X(offset)), Math.Min(WaveArea.Right, X(offset + track.Duration)));
                if (!rect.IsEmpty && rect.Contains(p)) return track;
            }
            offset += track.Duration;
        }
        return null;
    }

    // The overview uses only published decimated peaks. Exact source inspection is
    // performed by the tile workers, never by a UI-thread render or input event.
    private WavePeak Peak(AudioTrack track, double start, double end, bool preview)
    {
        var approximating = preview && _frameApproximate.GetValueOrDefault(track.Id);
        var bins = preview && !approximating && track.PreviewPeaks.Count > 0 ? track.PreviewPeaks : track.OriginalPeaks;
        float min = 0, max = 0;
        if (bins.Count > 0 && end > start)
        {
            var binStart = Math.Clamp((int)(start / Math.Max(track.Duration, .001) * bins.Count), 0, bins.Count - 1);
            var binEnd = Math.Clamp((int)Math.Ceiling(end / Math.Max(track.Duration, .001) * bins.Count), binStart + 1, bins.Count);
            for (int i = binStart; i < binEnd; i++) { min = Math.Min(min, bins[i].Min); max = Math.Max(max, bins[i].Max); }
        }
        if (approximating)
        {
            var gainDb = TotalGainEstimate(track);
            if (Settings.LevelingEnabled) gainDb += EnvelopeAt(CombinedCurve(track), (start + end) * .5) * Settings.Strength;
            var gain = (float)Math.Pow(10, gainDb / 20);
            min *= gain; max *= gain;
            if (Settings.LimiterEnabled) { var ceiling = (float)Math.Pow(10, Settings.CeilingDb / 20); min = Math.Max(-ceiling, min); max = Math.Min(ceiling, max); }
        }
        else if (preview && track.Render?.LiveSource is { GainDb: not 0 } live)
        {
            var gain = (float)Math.Pow(10, live.GainDb / 20);
            min *= gain; max *= gain;
        }
        return new WavePeak(min, max);
    }

    private static double EnvelopeAt(IReadOnlyList<EnvelopePoint> points, double time)
        => AudioProcessor.EnvelopeGainAt(points, time);

    private void DrawRuler(DrawingContext dc)
    {
        var desired = ViewDuration / Math.Max(2, WaveArea.Width / 100);
        double[] steps = [.01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];
        var step = steps.FirstOrDefault(s => s >= desired);
        if (step <= 0) step = 7200;
        for (var t = Math.Ceiling(_viewStart / step) * step; t <= End + .001; t += step)
        {
            var x = X(t);
            dc.DrawLine(new Pen(BorderBrush, .5), new Point(x, RulerArea.Bottom - 5), new Point(x, WaveArea.Bottom));
            Text(dc, TimeLabel(t, step < 1), 10, x + 3, RulerArea.Top + 8, MutedBrush);
        }
    }
    private static string TimeLabel(double seconds, bool precise = false)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : precise ? $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 10:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
    private void Text(DrawingContext dc, string value, double size, double x, double y, Brush brush, double width = double.PositiveInfinity)
    {
        if (width < 5 || string.IsNullOrEmpty(value)) return;
        dc.DrawText(CachedText(value, size, brush, width), new Point(x, y));
    }

    private FormattedText CachedText(string value, double size, Brush brush, double width)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var roundedWidth = double.IsInfinity(width) ? width : Math.Max(1, Math.Floor(width));
        var key = (value, size, brush, roundedWidth, dpi);
        if (!_textCache.TryGetValue(key, out var text))
        {
            if (_textCache.Count >= 1024) _textCache.Clear();
            text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, size, brush, dpi);
            if (!double.IsInfinity(roundedWidth)) { text.MaxTextWidth = roundedWidth; text.Trimming = TextTrimming.CharacterEllipsis; }
            text.MaxLineCount = 1;
            _textCache[key] = text;
        }
        return text;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (IsNativeEditorSource(e.OriginalSource)) return;
        if (TotalDuration <= 0) return;
        HandleNavigationWheel(e.GetPosition(this), e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    internal void HandleNavigationWheel(Point point, int delta, bool pan)
    {
        if (TotalDuration <= 0) return;
        var start = _navigationActive ? _targetViewStart : _viewStart;
        var duration = _navigationActive ? _targetViewDuration : ViewDuration;
        if (pan) start -= delta / 120.0 * duration * .09;
        else
        {
            var fraction = Clamp((point.X - WaveArea.Left) / WaveArea.Width, 0, 1);
            var anchor = start + fraction * duration;
            duration = Clamp(duration * Math.Pow(.84, delta / 120.0), Math.Min(.05, TotalDuration), TotalDuration);
            start = anchor - fraction * duration;
        }
        StartNavigation(start, duration);
    }

    public void NavigateWheel(Point point, int delta, bool pan) => HandleNavigationWheel(point, delta, pan);
    public bool NavigateHorizontal(Point point, double delta) => HandleHorizontalScroll(point, delta);

    internal bool HandleHorizontalScroll(Point point, int delta) => HandleHorizontalScroll(point, (double)delta);

    internal bool HandleHorizontalScroll(Point point, double delta)
    {
        if (!IsEnabled || TotalDuration <= 0 || !double.IsFinite(delta) || delta == 0 || !new Rect(RenderSize).Contains(point) ||
            _hoverEditor.IsKeyboardFocusWithin || _removeTrackButton.IsKeyboardFocusWithin || _valueDragTrack != null) return false;
        foreach (var editor in new FrameworkElement[] { _hoverEditor, _removeTrackButton })
            if (editor.Visibility == Visibility.Visible && editor.RenderSize.Width > 0 && editor.RenderSize.Height > 0 &&
                new Rect(editor.TranslatePoint(new Point(), this), editor.RenderSize).Contains(point)) return false;
        var start = _navigationActive ? _targetViewStart : _viewStart;
        var duration = _navigationActive ? _targetViewDuration : ViewDuration;
        // WM_MOUSEHWHEEL uses positive/right; retain sub-detent trackpad deltas.
        StartNavigation(start + delta / 120.0 * duration * .09, duration);
        return true;
    }

    internal bool HandleNavigationPointerDown(Point point, int clickCount)
    {
        if (TotalDuration <= 0) return false;
        var titleTrack = WaveArea.Contains(point) ? TrackAt(T(point.X)) : null;
        if (titleTrack != null && TrackTitleRect(titleTrack).Contains(point))
        {
            Select(titleTrack);
            if (clickCount >= 2) { FitTrack(titleTrack); _drag = DragKind.None; }
            else BeginSeek(point);
            return true;
        }
        if (Overview.Contains(point))
        {
            StopNavigation();
            if (clickCount >= 2)
            {
                var time = Clamp((point.X - Overview.Left) / Overview.Width * TotalDuration, 0, TotalDuration);
                var clickedTrack = TrackAt(time);
                if (clickedTrack != null) { Select(clickedTrack); FitTrack(clickedTrack); }
                _drag = DragKind.None;
                return true;
            }
            _dragOrigin = point;
            _dragStart = _viewStart;
            _dragDuration = ViewDuration;
            var left = Overview.Left + _viewStart / TotalDuration * Overview.Width;
            var right = left + ViewDuration / TotalDuration * Overview.Width;
            _drag = Math.Abs(point.X - left) < 7 ? DragKind.OverviewLeft : Math.Abs(point.X - right) < 7 ? DragKind.OverviewRight : DragKind.OverviewMove;
            if (point.X < left || point.X > right)
            {
                _viewStart = (point.X - Overview.Left) / Overview.Width * TotalDuration - ViewDuration * .5;
                ClampView();
                _dragStart = _viewStart;
                InvalidateCamera();
            }
            return true;
        }
        if (RulerArea.Contains(point))
        {
            if (HandleLoopPointerDown(point)) return true;
            BeginSeek(point);
            return true;
        }
        return false;
    }

    private void BeginSeek(Point point)
    {
        StopNavigation();
        Playhead = T(point.X);
        _drag = DragKind.Seek;
        ScrubStarted?.Invoke(Playhead);
        ScrubPositionRequested?.Invoke(Playhead);
        SeekRequested?.Invoke(Playhead);
    }

    internal bool HandleNavigationPointerMove(Point point)
    {
        if (HandleLoopPointerMove(point)) return true;
        switch (_drag)
        {
            case DragKind.OverviewMove:
                _viewStart = _dragStart + (point.X - _dragOrigin.X) / Overview.Width * TotalDuration;
                ClampView();
                InvalidateCamera();
                return true;
            case DragKind.OverviewLeft:
                var newLeft = Clamp(_dragStart + (point.X - _dragOrigin.X) / Overview.Width * TotalDuration, 0, _dragStart + _dragDuration - Math.Min(.05, TotalDuration));
                _viewStart = newLeft;
                _viewDuration = _dragStart + _dragDuration - newLeft;
                ClampView();
                InvalidateCamera();
                return true;
            case DragKind.OverviewRight:
                _viewDuration = _dragDuration + (point.X - _dragOrigin.X) / Overview.Width * TotalDuration;
                ClampView();
                InvalidateCamera();
                return true;
            case DragKind.Seek:
                Playhead = T(point.X);
                ScrubPositionRequested?.Invoke(Playhead);
                SeekRequested?.Invoke(Playhead);
                return true;
            default:
                return false;
        }
    }

    internal bool HandleTrackContextClick(Point point)
    {
        if (!WaveArea.Contains(point)) return false;
        var track = TrackAt(T(point.X));
        if (track == null) return false;
        if (ShowEnvelopeOnWaveform && SignalArea.Contains(point) && CanEditEnvelope(track))
        {
            var index = HitNode(track, point, true);
            if (index >= 0)
            {
                var points = EditableEnvelope(track);
                if (points.Count > 2 && index > 0 && index < points.Count - 1)
                {
                    EditStarted?.Invoke();
                    points.RemoveAt(index);
                    NotifyEnvelopeEdited();
                    InvalidateData();
                    EditCommitted?.Invoke(track);
                    InvalidateTimeline();
                }
                return true;
            }
        }
        TrackContextRequested?.Invoke(track, point);
        return true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsNativeEditorSource(e.OriginalSource)) return;
        Focus();
        if (TotalDuration <= 0) return;
        StopNavigation();
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Left && HandleCustomBoundaryPointerDown(p))
        {
            CaptureMouse(); e.Handled = true; return;
        }
        var track = TrackAt(T(p.X));
        if (e.ChangedButton == MouseButton.Left && HandleMissingRelinkClick(p))
        {
            e.Handled = true;
            return;
        }
        var workIcon = HitWorkIcon(p);
        if (e.ChangedButton == MouseButton.Left && workIcon != null)
        {
            if (!IsActivelyWorking(workIcon) && workIcon.WorkState != TrackWorkState.Queued)
            {
                Select(workIcon);
                RebuildRequested?.Invoke();
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && HandleTrackGripPointerDown(p))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right && HandleTrackContextClick(p))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        _dragOrigin = p; _dragStart = _viewStart; _dragDuration = ViewDuration; _dragEdited = false; _editUndoStarted = false;
        if (HandleNavigationPointerDown(p, e.ClickCount))
        {
            if (_drag != DragKind.None || IsEditingLoop) CaptureMouse();
            InvalidateTimeline();
            e.Handled = true;
            return;
        }
        if (track != null && WaveArea.Contains(p)) HandleWaveformPointerDown(track, p, e.ClickCount);
        if (_drag != DragKind.None) CaptureMouse();
        InvalidateTimeline(); e.Handled = true;
    }

    internal void HandleWaveformPointerDown(AudioTrack track, Point point, int clickCount)
    {
        _dragOrigin = point; _dragEdited = false; _editUndoStarted = false; _dragMotionStarted = false;
        if (HandleMissingRelinkClick(point)) return;
        if (SliceMode) { if (track.Original.Length > 0) SliceRequested?.Invoke(track, T(point.X) - TrackOffset(track)); return; }
        Select(track);
        var editable = CanEditTrack(track) && SignalArea.Contains(point);
        var envelopeEditable = editable && CanEditEnvelope(track);
        var node = ShowEnvelopeOnWaveform && envelopeEditable ? HitNode(track, point, true) : -1;
        if (node >= 0) BeginEnvelopeEdit(track, node, true);
        else if (editable && HitGainLabel(track, point))
        {
            _dragRange = GainRange; _editingTrack = track; _dragGain = track.TrimDb; _drag = DragKind.Gain;
        }
        else if (ShowEnvelopeOnWaveform && envelopeEditable && IsNearMainEnvelope(track, point))
        {
            InsertEnvelopePointOnCurve(track, T(point.X) - TrackOffset(track));
            _dragOrigin = point;
        }
        else if (ShowEnvelopeOnWaveform && editable && !envelopeEditable && IsNearMainEnvelope(track, point))
        {
            // A bypassed curve often coincides with the zero-dB Gains line.
            // Keep the disabled envelope seekable; its explicit Gains label
            // above remains available for independent track-trim editing.
            if (clickCount >= 2) FitTrack(track); else BeginSeek(point);
        }
        else if (editable && HitGainLine(track, point))
        {
            _dragRange = GainRange; _editingTrack = track; _dragGain = track.TrimDb; _drag = DragKind.Gain;
        }
        else if (ShowEnvelopeOnWaveform && envelopeEditable && clickCount >= 2 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            AddEnvelopePoint(track, point, true);
        else if (clickCount >= 2) FitTrack(track);
        else BeginSeek(point);
    }

    internal int InsertEnvelopePointOnCurve(AudioTrack track, double time)
    {
        if (!Tracks.Contains(track) || !CanEditEnvelope(track)) return -1;
        time = Clamp(time, 0, track.Duration) + (_projectEnvelope == null ? 0 : TrackOffset(track));
        var points = EditableEnvelope(track);
        int existing = points.FindIndex(point => Math.Abs(point.Time - time) < 1e-9);
        if (existing >= 0) { BeginEnvelopeEdit(track, existing, true); return existing; }
        var gain = EnvelopeAt(points, time);
        EditStarted?.Invoke();
        if (points.Count == 0) { points.Add(new(0, 0)); points.Add(new(_projectEnvelope == null ? track.Duration : TotalDuration, 0)); }
        var point = new EnvelopePoint(time, gain);
        points.Add(point); points.Sort((a, b) => a.Time.CompareTo(b.Time));
        var index = points.IndexOf(point);
        NotifyEnvelopeEdited();
        BeginEnvelopeEdit(track, index, true);
        _editUndoStarted = true; _dragEdited = true;
        _dragOrigin = new Point(X(EditableEnvelopeOffset(track) + time), EnvelopeY(gain + EditableTransitionGain(track, time), true));
        InvalidateData();
        return index;
    }

    private void SetHoverNode(AudioTrack? track, int index)
    {
        if (ReferenceEquals(_hoverNodeTrack, track) && _hoverNodeIndex == index) return;
        _hoverNodeTrack = track; _hoverNodeIndex = index;
        InvalidateTimeline();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (IsEditingLoop)
        {
            SetCrosshairPointer(null); SetTrackHoverPointer(null); SetHoverNode(null, -1);
            HandleLoopPointerMove(p);
            e.Handled = true;
            return;
        }
        if (HandleCustomBoundaryPointerMove(p))
        {
            SetCrosshairPointer(null); SetTrackHoverPointer(null); SetHoverNode(null, -1);
            e.Handled = true; return;
        }
        SetTrackHoverPointer(p);
        if (IsNativeEditorSource(e.OriginalSource) && _drag == DragKind.None && !IsEditingLoop) { SetCrosshairPointer(null); SetHoverNode(null, -1); return; }
        SetCrosshairPointer(p);
        if (_pendingTrackDrag != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (Math.Abs(p.X - _trackDragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(p.Y - _trackDragStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
                BeginTrackDrag(_pendingTrackDrag);
            e.Handled = true;
            return;
        }
        if (_drag == DragKind.None && !IsEditingLoop && !_trackDragInProgress) SetHoveredTrack(p);
        if (HandleNavigationPointerMove(p)) { InvalidateTimeline(); return; }
        if (HandleWaveformPointerMove(p)) return;
        if (_hoveredTrack != null && TrackHandleRect(_hoveredTrack).Contains(p)) { Cursor = Cursors.SizeAll; return; }
        if (HitMissingRelink(p) != null) { SetHoverNode(null, -1); Cursor = Cursors.Hand; return; }
        var workIcon = HitWorkIcon(p);
        if (workIcon != null)
        {
            Cursor = !IsActivelyWorking(workIcon) && workIcon.WorkState != TrackWorkState.Queued ? Cursors.Hand : Cursors.Arrow;
            return;
        }
        var track = TrackAt(T(p.X));
        if (SliceMode && track != null && WaveArea.Contains(p) && !IsMissing(track)) { SetHoverNode(null, -1); Cursor = Cursors.Cross; return; }
        var editable = track != null && CanEditTrack(track);
        var node = ShowEnvelopeOnWaveform && editable && SignalArea.Contains(p) ? HitNode(track!, p, true) : -1;
        SetHoverNode(node >= 0 ? track : null, node);
        var envelopeEditable = editable && CanEditEnvelope(track!);
        var onCurve = envelopeEditable && ShowEnvelopeOnWaveform && SignalArea.Contains(p) && IsNearMainEnvelope(track!, p);
        var onInactiveCurve = editable && !envelopeEditable && ShowEnvelopeOnWaveform && SignalArea.Contains(p) && IsNearMainEnvelope(track!, p);
        Cursor = Overview.Contains(p) || RulerArea.Contains(p) ? Cursors.SizeWE :
            node >= 0 && envelopeEditable ? Cursors.SizeAll : editable && HitGainLabel(track!, p) ? Cursors.SizeNS : onCurve ? Cursors.Cross :
            editable && !onInactiveCurve && HitGainLine(track!, p) ? Cursors.SizeNS : WaveArea.Contains(p) ? Cursors.IBeam : Cursors.Arrow;
    }

    internal bool HandleWaveformPointerMove(Point point)
    {
        if (_editingTrack == null || !CanEditTrack(_editingTrack)) return false;
        if (_drag == DragKind.Gain)
        {
            if (!_dragMotionStarted && Math.Abs(point.Y - _dragOrigin.Y) <= 2) return true;
            _dragMotionStarted = true;
            if (!_editUndoStarted) { EditStarted?.Invoke(); _editUndoStarted = true; }
            _editingTrack.TrimDb = point.Y == _dragOrigin.Y ? _dragGain :
                Math.Round(Clamp(_dragGain - (point.Y - _dragOrigin.Y) / TimelineLayout.GainAmplitude(MainEnvelopeArea) * _dragRange, -30, 30), 1);
            _dragEdited = true;
        }
        else if (_drag == DragKind.Envelope)
        {
            if (!CanEditEnvelope(_editingTrack)) return true;
            if (!_dragMotionStarted && Math.Abs(point.X - _dragOrigin.X) <= 2 && Math.Abs(point.Y - _dragOrigin.Y) <= 2) return true;
            _dragMotionStarted = true;
            if (!_editUndoStarted) { EditStarted?.Invoke(); _editUndoStarted = true; }
            var points = EditableEnvelope(_editingTrack);
            var time = _dragNodeTime;
            if (_editingNode > 0 && _editingNode < points.Count - 1 && point.X != _dragOrigin.X)
            {
                var gap = Math.Min(.01, (points[_editingNode + 1].Time - points[_editingNode - 1].Time) * .2);
                time = Clamp(_dragNodeTime + (point.X - _dragOrigin.X) / WaveArea.Width * ViewDuration,
                    points[_editingNode - 1].Time + gap, points[_editingNode + 1].Time - gap);
            }
            var desiredGain = EnvelopeGain(_dragNodeY + point.Y - _dragOrigin.Y, true);
            points[_editingNode] = point == _dragOrigin ? new EnvelopePoint(_dragNodeTime, _dragNodeGain) :
                new EnvelopePoint(time, Math.Round(Clamp(desiredGain - EditableTransitionGain(_editingTrack, time), -24, 24), 2));
            NotifyEnvelopeEdited();
            _dragEdited = true;
        }
        else return false;
        InvalidateData(_drag == DragKind.Envelope);
        return true;
    }

    private void BeginTrackDrag(AudioTrack track)
    {
        _pendingTrackDrag = null;
        _trackDragInProgress = true;
        if (IsMouseCaptured) ReleaseMouseCapture();
        var data = new DataObject();
        data.SetData(TrackDragDataFormat, track.Id.ToString());
        try { DragDrop.DoDragDrop(this, data, DragDropEffects.Move); }
        finally
        {
            _trackDragInProgress = false;
            ClearTrackDrop();
            SetHoveredTrack(Mouse.GetPosition(this));
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCrosshairPointer(null);
        SetTrackHoverPointer(null);
        SetHoverNode(null, -1);
        if (!_trackDragInProgress && _valueDragTrack == null && _hoveredTrack != null && !_hoverEditor.IsKeyboardFocusWithin)
        {
            _hoveredTrack = null;
            SyncHoverEditor();
            InvalidateTimeline();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left) return;
        if (CompleteCustomBoundaryInteraction()) { e.Handled = true; return; }
        CompletePointerInteraction();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CompleteCustomBoundaryInteraction(allowClickRemoval: false);
        CompleteLoopInteraction(allowSeek: false);
        CompletePointerInteraction();
    }

    internal void CompletePointerInteraction()
    {
        CompleteLoopInteraction();
        var track = _editingTrack;
        bool wasSeeking = _drag == DragKind.Seek;
        var commit = track != null && _dragEdited && _drag != DragKind.None;
        _pendingTrackDrag = null;
        _drag = DragKind.None; _editingTrack = null; _editingNode = -1; _dragMotionStarted = false;
        if (commit) { InvalidateData(false); EditCommitted?.Invoke(track!); }
        if (wasSeeking) ScrubCompleted?.Invoke(Playhead);
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateTimeline();
    }
    private void BeginEnvelopeEdit(AudioTrack track, int node, bool waveform)
    {
        _dragEdited = false; _editUndoStarted = false; _dragMotionStarted = false;
        _dragRange = EnvelopeRange;
        _editingTrack = track;
        _editingNode = node;
        var point = EditableEnvelope(track)[node];
        _dragNodeTime = point.Time;
        _dragNodeGain = point.GainDb;
        _dragNodeY = EnvelopeY(point.GainDb + EditableTransitionGain(track, _dragNodeTime), true);
        _drag = DragKind.Envelope;
    }

    internal Point EnvelopePointPosition(AudioTrack track, int node)
    {
        var point = EditableEnvelope(track)[node];
        return new Point(X(EditableEnvelopeOffset(track) + point.Time), EnvelopeY(point.GainDb + EditableTransitionGain(track, point.Time), true));
    }

    private void AddEnvelopePoint(AudioTrack track, Point p, bool waveform)
    {
        if (!CanEditEnvelope(track)) return;
        double time = Clamp(T(p.X), .001, Math.Max(.001, TotalDuration - .001)) - EditableEnvelopeOffset(track);
        var points = EditableEnvelope(track);
        if (points.Any(point => Math.Abs(point.Time - time) < .02)) return;
        EditStarted?.Invoke();
        if (points.Count == 0) { points.Add(new(0, 0)); points.Add(new(_projectEnvelope == null ? track.Duration : TotalDuration, 0)); }
        points.Add(new(time, Clamp(EnvelopeGain(p.Y, waveform) - EditableTransitionGain(track, time), -24, 24)));
        points.Sort((a, b) => a.Time.CompareTo(b.Time));
        NotifyEnvelopeEdited();
        InvalidateData();
        EditCommitted?.Invoke(track);
    }

    private bool IsNearMainEnvelope(AudioTrack track, Point p) => SignalArea.Contains(p) &&
        (EditableEnvelope(track).Count > 0 || track.TransitionEnvelope.Count > 0) &&
        Math.Abs(EnvelopeY(EnvelopeAt(EditableEnvelope(track), T(p.X) - EditableEnvelopeOffset(track)) +
            EditableTransitionGain(track, T(p.X) - EditableEnvelopeOffset(track)), true) - p.Y) <= 10;

    private int HitNode(AudioTrack track, Point p, bool waveform = false)
    {
        if (!SignalArea.Contains(p)) return -1;
        var offset = EditableEnvelopeOffset(track);
        var points = EditableEnvelope(track);
        for (int i = 0; i < points.Count; i++)
        {
            var node = points[i];
            if (Math.Abs(X(offset + node.Time) - p.X) <= 8 && Math.Abs(EnvelopeY(node.GainDb + EditableTransitionGain(track, node.Time), waveform) - p.Y) <= 8) return i;
        }
        return -1;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsNativeEditorSource(e.OriginalSource)) return;
        if (e.Key == Key.Escape && IsEditingLoop)
        {
            CompleteLoopInteraction(cancel: true, allowSeek: false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Home) { FitAll(); e.Handled = true; }
        if (e.Key is Key.Left or Key.Right)
        {
            var start = _navigationActive ? _targetViewStart : _viewStart;
            var duration = _navigationActive ? _targetViewDuration : ViewDuration;
            StartNavigation(start + (e.Key == Key.Left ? -1 : 1) * duration * .1, duration);
            e.Handled = true;
        }
    }
}

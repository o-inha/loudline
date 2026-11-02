using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App.Controls;

/// <summary>A small indeterminate indicator which stops ticking when hidden or inactive.</summary>
public sealed class ProcessingSpinner : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ProcessingSpinner),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, ActivityChanged));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ProcessingSpinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer _timer;
    private int _phase;

    public ProcessingSpinner()
    {
        Width = Height = 18;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(75) };
        _timer.Tick += (_, _) => { _phase = (_phase + 1) % 12; InvalidateVisual(); };
        Loaded += (_, _) => UpdateActivity();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateActivity();
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static void ActivityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProcessingSpinner)d).UpdateActivity();
    private void UpdateActivity()
    {
        if (IsLoaded && IsVisible && IsActive) _timer.Start();
        else _timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!IsActive) return;
        DrawSpinner(dc, new Point(ActualWidth / 2, ActualHeight / 2), Math.Max(2, Math.Min(ActualWidth, ActualHeight) * .44), _phase,
            Foreground ?? TryFindResource("AccentBrush") as Brush ?? Brushes.MediumAquamarine);
    }

    internal static void DrawSpinner(DrawingContext dc, Point center, double radius, int phase, Brush brush)
    {
        for (int i = 0; i < 12; i++)
        {
            var angle = (i + phase) * Math.PI / 6;
            var start = new Point(center.X + Math.Cos(angle) * radius * .64, center.Y + Math.Sin(angle) * radius * .64);
            var end = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
            dc.PushOpacity(.16 + i / 11.0 * .84);
            dc.DrawLine(new Pen(brush, Math.Max(1.4, radius * .19)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, start, end);
            dc.Pop();
        }
    }
}

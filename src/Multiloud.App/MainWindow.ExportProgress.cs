using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Multiloud.App.Controls;

namespace Multiloud.App;

public partial class MainWindow
{
    private sealed record ExportProgressUpdate(long Generation, int File, int Total, double Fraction);
    private long _exportProgressGeneration;
    private ExportProgressUpdate? _pendingExportProgress;
    private DispatcherTimer? _exportProgressTimer;
    private ExportProgressAdorner? _exportProgressAdorner;
    private AdornerLayer? _exportProgressLayer;

    private void BeginExportProgress(int count)
    {
        Interlocked.Increment(ref _exportProgressGeneration);
        Interlocked.Exchange(ref _pendingExportProgress, null);
        _exportProgressLayer = AdornerLayer.GetAdornerLayer(WorkspaceGrid);
        _exportProgressAdorner = new ExportProgressAdorner(WorkspaceGrid, () => _exportCts?.Cancel());
        _exportProgressLayer?.Add(_exportProgressAdorner);
        _exportProgressAdorner.Update(1, count, 0);
        _exportProgressTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(50) };
        _exportProgressTimer.Tick -= ExportProgressTick;
        _exportProgressTimer.Tick += ExportProgressTick;
        _exportProgressTimer.Start();
    }

    private void ExportProgressTick(object? sender, EventArgs e) => FlushExportProgress();

    private void FlushExportProgress()
    {
        var update = Interlocked.Exchange(ref _pendingExportProgress, null);
        if (update == null || update.Generation != Volatile.Read(ref _exportProgressGeneration)) return;
        _exportProgressAdorner?.Update(update.File, update.Total, update.Fraction);
    }

    private Action<double> ExportFileProgress(int file, int total, double before, double weight, double sum)
    {
        long generation = Volatile.Read(ref _exportProgressGeneration);
        return fraction =>
        {
            if (generation != Volatile.Read(ref _exportProgressGeneration) || !double.IsFinite(fraction)) return;
            double overall = sum <= 0 ? 0 : (before + weight * Math.Clamp(fraction, 0, 1)) / sum;
            Interlocked.Exchange(ref _pendingExportProgress, new ExportProgressUpdate(generation, file, total, overall));
        };
    }

    private void EndExportProgress()
    {
        Interlocked.Increment(ref _exportProgressGeneration);
        Interlocked.Exchange(ref _pendingExportProgress, null);
        _exportProgressTimer?.Stop();
        if (_exportProgressAdorner != null) _exportProgressLayer?.Remove(_exportProgressAdorner);
        _exportProgressAdorner = null; _exportProgressLayer = null;
    }

    private sealed class ExportProgressAdorner : Adorner
    {
        private readonly VisualCollection _visuals;
        private readonly Grid _surface;
        internal TextBlock FileText { get; }
        internal TextBlock PercentText { get; }
        internal ProgressBar Progress { get; }
        internal Border Card { get; }

        internal ExportProgressAdorner(UIElement adorned, Action cancel) : base(adorned)
        {
            IsHitTestVisible = true;
            _surface = new Grid { Background = new SolidColorBrush(Color.FromArgb(88, 0, 0, 0)) };
            Card = new Border
            {
                Width = 360, Padding = new Thickness(28), CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            _surface.Children.Add(Card);
            var content = new StackPanel(); Card.Child = content;
            var heading = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            heading.Children.Add(new ProcessingSpinner { Width = 26, Height = 26, IsActive = true, Margin = new Thickness(0, 0, 12, 0) });
            heading.Children.Add(new TextBlock { Text = "Exporting…", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(heading);
            PercentText = new TextBlock { FontSize = 34, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 20, 0, 10) };
            PercentText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); content.Children.Add(PercentText);
            Progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, BorderThickness = new Thickness(0) };
            Progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
            Progress.SetResourceReference(ProgressBar.BackgroundProperty, "ButtonBrush"); content.Children.Add(Progress);
            FileText = new TextBlock { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 20) };
            FileText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(FileText);
            var button = new Button { Content = "Cancel", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0) };
            button.Click += (_, _) => cancel(); content.Children.Add(button);
            _visuals = new VisualCollection(this) { _surface }; AddLogicalChild(_surface);
        }

        internal void Update(int file, int count, double fraction)
        {
            double value = Math.Clamp(fraction, 0, 1) * 100;
            PercentText.Text = value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "%";
            FileText.Text = $"File {Math.Clamp(file, 1, Math.Max(1, count))} / {count}";
            Progress.Value = value;
        }

        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];
        protected override System.Collections.IEnumerator LogicalChildren => new object[] { _surface }.GetEnumerator();
        protected override Size MeasureOverride(Size constraint)
        {
            _surface.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        { _surface.Arrange(new Rect(finalSize)); return finalSize; }
    }
}

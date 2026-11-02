using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private BackgroundLayer? _backgroundGainGestureLayer;
    private bool _backgroundGainDragging, _backgroundGainEditing;
    private double _backgroundGainStartY, _backgroundGainStartValue, _backgroundGainDraft, _backgroundGainStep;
    private bool CanEditBackgroundGain => _initialized && !_syncing && !_importing && !_exporting && !_closed;

    private void InitializeBackgroundGain()
    {
        PreviewMouseDown += BackgroundGainOutsideMouseDown;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_backgroundGainDragging) return;
            FinishBackgroundGainDrag(false); e.Handled = true;
        };
        Deactivated += (_, _) => FinishBackgroundGainDrag(false);
        var box = BackgroundGainBox;
        var width = new FormattedText("-99", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize,
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        box.Width = box.MaxWidth = Math.Ceiling(width + box.Padding.Left + box.Padding.Right + box.BorderThickness.Left + box.BorderThickness.Right);
    }

    private void BackgroundGainOutsideMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_backgroundGainEditing || e.OriginalSource is DependencyObject source && IsDescendantOf(source, BackgroundGainBox)) return;
        // Labels and blank header space do not take keyboard focus. Treat their
        // clicks as leaving the number editor too, without consuming the click.
        BackgroundGainCommit(sender, e);
    }

    private static string BackgroundGainText(double gain) => gain.ToString("0.##", CultureInfo.InvariantCulture);

    private void SyncBackgroundGainControls()
    {
        // Undo, project changes, or an independent background edit invalidate the
        // old numeric draft instead of applying it to a different layer later.
        if (_backgroundGainGestureLayer != null && !ReferenceEquals(_backgroundGainGestureLayer, _backgroundLayer))
        {
            FinishBackgroundGainDrag(false);
            CloseBackgroundGainTextEdit();
        }
        if (BackgroundGainValue == null || BackgroundGainBox == null) return;
        BackgroundGainValue.Text = BackgroundGainText(_backgroundGainDragging ? _backgroundGainDraft : _backgroundLayer.GainDb);
        if (!_backgroundGainEditing) BackgroundGainBox.Text = BackgroundGainText(_backgroundLayer.GainDb);
    }

    private void BackgroundGainValueMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanEditBackgroundGain) return;
        if (e.ClickCount >= 2) BeginBackgroundGainTextEdit();
        else
        {
            Timeline.Focus(); // Flush any previous number edit before taking its baseline.
            BeginBackgroundGainDrag(e.GetPosition(this).Y, ShiftHeld);
            if (!BackgroundGainValue.CaptureMouse()) FinishBackgroundGainDrag(false);
        }
        e.Handled = true;
    }

    private void BackgroundGainValueMouseMove(object sender, MouseEventArgs e)
    {
        if (!_backgroundGainDragging || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateBackgroundGainDrag(e.GetPosition(this).Y, ShiftHeld); e.Handled = true;
    }

    private void BackgroundGainValueMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_backgroundGainDragging) return;
        FinishBackgroundGainDrag(true); e.Handled = true;
    }

    private void BackgroundGainLostCapture(object sender, MouseEventArgs e)
    {
        if (_backgroundGainDragging) FinishBackgroundGainDrag(Mouse.LeftButton == MouseButtonState.Released);
    }

    // Shared by actual pointer input and the Windows interaction checks. A drag
    // updates only its displayed value; the expensive fixed-bed mix is committed
    // once, on release, and only when the final number differs from its baseline.
    private void BeginBackgroundGainDrag(double y, bool fine)
    {
        if (!CanEditBackgroundGain) return;
        FinishBackgroundGainDrag(false);
        BackgroundGainCommit(this, new RoutedEventArgs());
        _backgroundGainGestureLayer = _backgroundLayer;
        _backgroundGainStartY = y;
        _backgroundGainStartValue = _backgroundGainDraft = _backgroundLayer.GainDb;
        _backgroundGainStep = fine ? .1 : 1;
        _backgroundGainDragging = true;
        SyncBackgroundGainControls();
    }

    private void UpdateBackgroundGainDrag(double y, bool fine)
    {
        if (!_backgroundGainDragging) return;
        if (!CanEditBackgroundGain || !ReferenceEquals(_backgroundGainGestureLayer, _backgroundLayer))
        { FinishBackgroundGainDrag(false); return; }
        double step = fine ? .1 : 1;
        if (step != _backgroundGainStep)
        {
            _backgroundGainStartY = y; _backgroundGainStartValue = _backgroundGainDraft; _backgroundGainStep = step;
        }
        _backgroundGainDraft = Math.Clamp(_backgroundGainStartValue + Math.Truncate((_backgroundGainStartY - y) / 12) * step, -60, 12);
        SyncBackgroundGainControls();
    }

    private void FinishBackgroundGainDrag(bool commit)
    {
        if (!_backgroundGainDragging) return;
        var layer = _backgroundGainGestureLayer; double value = _backgroundGainDraft;
        _backgroundGainDragging = false; _backgroundGainGestureLayer = null;
        if (BackgroundGainValue.IsMouseCaptured) BackgroundGainValue.ReleaseMouseCapture();
        if (commit && CanEditBackgroundGain && ReferenceEquals(layer, _backgroundLayer) && Math.Abs(value - _backgroundLayer.GainDb) > .000001)
            SetBackgroundLayer(_backgroundLayer with { GainDb = value });
        SyncBackgroundGainControls();
    }

    private void BeginBackgroundGainTextEdit()
    {
        if (!CanEditBackgroundGain) return;
        FinishBackgroundGainDrag(false);
        _backgroundGainGestureLayer = _backgroundLayer;
        _backgroundGainEditing = true;
        BackgroundGainBox.Text = BackgroundGainText(_backgroundLayer.GainDb);
        BackgroundGainValue.Visibility = Visibility.Collapsed;
        BackgroundGainBox.Visibility = Visibility.Visible;
        BackgroundGainBox.Focus(); BackgroundGainBox.SelectAll();
    }

    private void CloseBackgroundGainTextEdit()
    {
        _backgroundGainEditing = false;
        if (!_backgroundGainDragging) _backgroundGainGestureLayer = null;
        BackgroundGainBox.Visibility = Visibility.Collapsed;
        BackgroundGainValue.Visibility = Visibility.Visible;
        BackgroundGainBox.Text = BackgroundGainValue.Text = BackgroundGainText(_backgroundLayer.GainDb);
    }

    private void BackgroundGainCommit(object sender, RoutedEventArgs e)
    {
        if (!_backgroundGainEditing) return;
        bool valid = double.TryParse(BackgroundGainBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double gain) && double.IsFinite(gain) && gain is >= -60 and <= 12;
        bool apply = valid && CanEditBackgroundGain && ReferenceEquals(_backgroundGainGestureLayer, _backgroundLayer);
        CloseBackgroundGainTextEdit(); // Focus loss from collapsing the box must not commit twice.
        if (apply) SetBackgroundLayer(_backgroundLayer with { GainDb = gain });
        else if (!valid) StatusText.Text = "Enter a background gain from −60 to +12 dB.";
    }

    private void BackgroundGainKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BackgroundGainCommit(sender, e);
        else if (e.Key == Key.Escape) CloseBackgroundGainTextEdit();
        else return;
        Keyboard.ClearFocus(); e.Handled = true;
    }
}

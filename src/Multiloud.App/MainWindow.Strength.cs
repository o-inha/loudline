using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private Slider? _dragStrengthSlider;
    private double _strengthDragLastX, _strengthDragValue, _strengthDragOriginalValue;
    private string _strengthDragOriginalDisplay = "";
    private bool _strengthDragOriginalComputedTarget;
    private static bool ShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    private static string StrengthDisplayBucket(double value) => (value * 100).ToString("0.#", CultureInfo.InvariantCulture);

    // Audio rendering, playback readiness, export, and projects always use _settings. Only the
    // waveform sees this transient draft; completion of already-running audio work must keep it.
    private ProcessingSettings DisplaySettings => _dragStrengthSlider is { } slider
        ? WithStrength(_settings, slider, slider.Value) : _settings;
    private void ApplyTimelineSettings(bool refreshTargetReadout = true)
    {
        Timeline.Settings = DisplaySettings;
        if (refreshTargetReadout) RefreshTargetReadout();
    }
    private ProcessingSettings WithStrength(ProcessingSettings settings, Slider slider, double value) =>
        ReferenceEquals(slider, MatchingStrengthSlider) ? settings with { MatchingStrength = value } : settings with { Strength = value };
    private double CommittedStrength(Slider slider) => ReferenceEquals(slider, MatchingStrengthSlider) ? _settings.MatchingStrength : _settings.Strength;

    private void StrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StrengthValueText != null) StrengthValueText.Text = $"{e.NewValue * 100:0.#}%";
        StrengthControlChanged((Slider)sender, e.NewValue);
    }

    private void MatchingStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MatchingStrengthValueText != null) MatchingStrengthValueText.Text = $"{e.NewValue * 100:0.#}%";
        StrengthControlChanged((Slider)sender, e.NewValue);
    }

    private void StrengthControlChanged(Slider slider, double value)
    {
        if (!_initialized || _syncing || _exporting || _closed) return;
        if (ReferenceEquals(_dragStrengthSlider, slider))
        {
            if (ReferenceEquals(slider, MatchingStrengthSlider))
                _targetComputedMode = _strengthDragOriginalComputedTarget || StrengthDisplayBucket(value) != _strengthDragOriginalDisplay;
            ApplyTimelineSettings();
            return;
        }
        // A programmatic/discrete edit of the other slider is a separate committed action.
        // Cancel a captured draft before applying it, just as another editing command does.
        FinishStrengthGesture(false);
        if (CommittedStrength(slider) == value) return;
        // Wheel/key helpers already recorded the gesture. A direct/discrete
        // control change still needs a pre-edit state even before first render.
        if (_pendingProcessingEdit == null && _processingRollback == null) PushUndo();
        _settings = WithStrength(_settings, slider, value);
        if (ReferenceEquals(slider, MatchingStrengthSlider)) _targetComputedMode = true;
        _dirty = true;
        ApplyTimelineSettings();
        QueueRender();
    }

    private void StrengthEditStarted(object sender, MouseButtonEventArgs e) => BeginStrengthDrag((Slider)sender, e);
    private void MatchingStrengthEditStarted(object sender, MouseButtonEventArgs e) => BeginStrengthDrag((Slider)sender, e);
    private void StrengthKeyDown(object sender, KeyEventArgs e) => AdjustStrengthKey((Slider)sender, e);
    private void MatchingStrengthKeyDown(object sender, KeyEventArgs e) => AdjustStrengthKey((Slider)sender, e);

    // These two helpers are shared by real input and the behavioral Windows smoke tests.
    private void BeginStrengthGesture(Slider slider)
    {
        if (!_initialized || _syncing || _exporting || _closed) return;
        FinishStrengthGesture(false);
        _strengthDragOriginalComputedTarget = _targetComputedMode;
        _strengthDragOriginalValue = CommittedStrength(slider);
        _strengthDragOriginalDisplay = StrengthDisplayBucket(_strengthDragOriginalValue);
        SetStrengthSliderSilently(slider, _strengthDragOriginalValue);
        _strengthDragValue = _strengthDragOriginalValue;
        _dragStrengthSlider = slider;
        Timeline.LivePreviewActive = true;
    }

    private void FinishStrengthGesture(bool commit)
    {
        var slider = _dragStrengthSlider;
        if (slider == null) return;
        var original = _strengthDragOriginalValue;
        var final = slider.Value;
        var changed = StrengthDisplayBucket(final) != _strengthDragOriginalDisplay;
        // Clear first: ReleaseMouseCapture synchronously raises LostMouseCapture, which must
        // never commit this same gesture a second time.
        _dragStrengthSlider = null;
        if (ReferenceEquals(slider, MatchingStrengthSlider))
            _targetComputedMode = commit && changed ? true : _strengthDragOriginalComputedTarget;
        Timeline.LivePreviewActive = false;
        _strengthDragOriginalDisplay = "";
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
        if (commit && changed && _initialized && !_syncing && !_exporting && !_closed)
        {
            PushUndo();
            _settings = WithStrength(_settings, slider, final);
            _dirty = true;
            ApplyTimelineSettings();
            QueueRender();
        }
        else
        {
            // The displayed percentage is the transaction boundary. Returning to it restores
            // the exact saved double, including values loaded with more precision than shown.
            SetStrengthSliderSilently(slider, original);
            ApplyTimelineSettings();
        }
    }

    private void SetStrengthSliderSilently(Slider slider, double value)
    {
        var syncing = _syncing;
        _syncing = true;
        try { slider.Value = value; }
        finally { _syncing = syncing; }
    }

    private void BeginStrengthDrag(Slider slider, MouseButtonEventArgs e)
    {
        if (!_initialized || _syncing || _exporting || _closed) return;
        slider.Focus(); // Commit a previously focused text field before taking the baseline.
        BeginStrengthGesture(slider);
        _strengthDragLastX = e.GetPosition(slider).X;
        var thumb = Descendants<Thumb>(slider).FirstOrDefault();
        var onThumb = thumb != null && new Rect(thumb.TranslatePoint(new Point(), slider), thumb.RenderSize).Contains(e.GetPosition(slider));
        if (!ShiftHeld && !onThumb)
        {
            var inset = (thumb?.ActualWidth ?? 12) / 2;
            slider.Value = Math.Round(Math.Clamp((_strengthDragLastX - inset) / Math.Max(1, slider.ActualWidth - 2 * inset), 0, 1) * 100) / 100;
        }
        _strengthDragValue = slider.Value;
        if (!slider.CaptureMouse()) FinishStrengthGesture(false);
        e.Handled = true;
    }

    private void StrengthDragMove(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(sender, _dragStrengthSlider) || _dragStrengthSlider is not { } slider || e.LeftButton != MouseButtonState.Pressed) return;
        var x = e.GetPosition(slider).X;
        var usableWidth = slider.ActualWidth - (Descendants<Thumb>(slider).FirstOrDefault()?.ActualWidth ?? 12);
        _strengthDragValue = Math.Clamp(_strengthDragValue + (x - _strengthDragLastX) / Math.Max(1, usableWidth) * (ShiftHeld ? .1 : 1), 0, 1);
        _strengthDragLastX = x;
        var step = ShiftHeld ? .001 : .01;
        slider.Value = Math.Clamp(Math.Round(_strengthDragValue / step) * step, 0, 1);
        e.Handled = true;
    }

    private void StrengthDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(sender, _dragStrengthSlider)) return;
        FinishStrengthGesture(true);
        e.Handled = true;
    }

    private void StrengthCaptureLost(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _dragStrengthSlider))
            FinishStrengthGesture(Mouse.LeftButton == MouseButtonState.Released);
    }

    private void StrengthWheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        StepStrengthSlider(slider, slider.Value + Math.Sign(e.Delta) * (ShiftHeld ? .001 : .01), true);
        e.Handled = true;
    }

    private void StepStrengthSlider(Slider slider, double value, bool recordUndo)
    {
        if (!_initialized || _syncing || _exporting || _closed) return;
        value = Math.Clamp(value, 0, 1);
        if (slider.Value == value) return;
        if (_dragStrengthSlider != null)
        {
            if (!ReferenceEquals(slider, _dragStrengthSlider)) return;
            slider.Value = value;
            _strengthDragValue = value;
            return;
        }
        if (recordUndo) PushUndo();
        slider.Value = value;
    }

    private void AdjustStrengthKey(Slider slider, KeyEventArgs e)
    {
        double? value = e.Key switch
        {
            Key.Left or Key.Down => slider.Value - (ShiftHeld ? .001 : .01),
            Key.Right or Key.Up => slider.Value + (ShiftHeld ? .001 : .01),
            Key.PageDown => slider.Value - (ShiftHeld ? .01 : .1),
            Key.PageUp => slider.Value + (ShiftHeld ? .01 : .1),
            Key.Home => 0, Key.End => 1, _ => null
        };
        if (value == null) return;
        StepStrengthSlider(slider, value.Value, !e.IsRepeat);
        e.Handled = true;
    }
}

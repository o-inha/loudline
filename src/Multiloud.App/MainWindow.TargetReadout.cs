using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _targetReadoutInitialized, _writingTargetText, _targetTextDirty, _targetEditing;
    private bool _targetComputedMode, _targetReadoutShowingComputed;
    private double _targetReadoutGoal;
    private bool IsComputedTargetReadout => _targetReadoutShowingComputed;

    private void InitializeTargetReadout()
    {
        if (_targetReadoutInitialized) return;
        _targetReadoutInitialized = true;
        TargetBox.TextChanged += TargetTextChanged;
        TargetBox.GotKeyboardFocus += (_, _) =>
        {
            BeginTargetEditing();
            // A mouse press can reposition the caret after GotKeyboardFocus.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (TargetBox.IsKeyboardFocusWithin) TargetBox.SelectAll();
            }));
        };
        ResetTargetReadout();
    }

    private void WriteTargetText(string text)
    {
        if (TargetBox.Text == text) return;
        _writingTargetText = true;
        try { TargetBox.Text = text; }
        finally { _writingTargetText = false; }
    }

    private void SetTargetReadoutColor(bool computed)
    {
        _targetReadoutShowingComputed = computed;
        TargetBox.SetResourceReference(Control.ForegroundProperty, computed ? "MonitorActiveTextBrush" : "TextBrush");
    }

    private void TargetTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_targetReadoutInitialized || _writingTargetText || !_initialized || _syncing) return;
        _targetTextDirty = true;
        // Typing is visibly a goal edit. The previous display mode is retained
        // until validation, so invalid text cannot erase an existing estimate.
        SetTargetReadoutColor(false);
    }

    private void BeginTargetEditing()
    {
        _targetEditing = true;
        if (!_targetTextDirty) WriteTargetText(F(_settings.TargetLufs, "0.#"));
        SetTargetReadoutColor(false);
        TargetBox.SelectAll();
    }

    private void EndTargetEditing()
    {
        _targetEditing = false;
        CommitTarget();
        RefreshTargetReadout();
    }

    private void ResetTargetReadout(bool preserveComputed = false)
    {
        if (!preserveComputed || _targetReadoutGoal != _settings.TargetLufs) _targetComputedMode = false;
        _targetReadoutGoal = _settings.TargetLufs;
        _targetTextDirty = false;
        WriteTargetText(F(_settings.TargetLufs, "0.#"));
        SetTargetReadoutColor(false);
    }

    private void RefreshTargetReadout()
    {
        if (!_targetReadoutInitialized || _syncing || _targetTextDirty) return;
        if (_targetReadoutGoal != _settings.TargetLufs) _targetComputedMode = false;
        _targetReadoutGoal = _settings.TargetLufs;
        var track = Selected;
        bool computed = _targetComputedMode && !_targetEditing && track?.Analysis != null && _tracks.Contains(track);
        var value = computed ? Timeline.EstimatedOutputLufs(track!) : _settings.TargetLufs;
        if (double.IsNaN(value)) { computed = false; value = _settings.TargetLufs; }
        WriteTargetText(F(value, "0.#"));
        SetTargetReadoutColor(computed);
    }

    private void ApplyTargetPreset(double target)
    {
        if (!_initialized || _syncing || _exporting || _closed) return;
        FinishStrengthGesture(false);
        WriteTargetText(target.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _targetTextDirty = true;
        CommitTarget();
        RefreshTargetReadout();
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBackgroundGainSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var layer = _backgroundLayer;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; bool? visible = BackgroundVisibilityCheck.IsChecked;
        _undo.Clear(); _redo.Clear();
        try
        {
            BackgroundVisibilityCheck.IsChecked = true; UpdateLayout();
            int revision = _revision, executions = _renderExecutionCount;
            var keys = _renderKeys.ToArray();
            void RequireDraftOnly(string gesture)
            {
                if (_revision != revision || _renderExecutionCount != executions || _undo.Count != 0 ||
                    !ReferenceEquals(layer, _backgroundLayer) || !_renderKeys.SequenceEqual(keys))
                    throw new InvalidOperationException($"Smoke test: background gain {gesture} changed audio or history before its commit.");
            }

            if (BackgroundGainBox.Visibility != Visibility.Collapsed || BackgroundGainValue.Visibility != Visibility.Visible ||
                BackgroundGainValue.Cursor != Cursors.SizeNS || BackgroundGainBox.Width is < 15 or > 38)
                throw new InvalidOperationException("Smoke test: background gain is not a compact draggable value with a hidden number editor.");

            BeginBackgroundGainDrag(100, false); RequireDraftOnly("mousedown");
            UpdateBackgroundGainDrag(89, false); RequireDraftOnly("sub-step movement");
            if (_backgroundGainDraft != layer.GainDb) throw new InvalidOperationException("Smoke test: background gain dragging is faster than one step per twelve pixels.");
            UpdateBackgroundGainDrag(76, false); RequireDraftOnly("coarse draft");
            if (_backgroundGainDraft != Math.Clamp(layer.GainDb + 2, -60, 12))
                throw new InvalidOperationException("Smoke test: background gain vertical drag uses the wrong direction or step.");
            UpdateBackgroundGainDrag(100, false); FinishBackgroundGainDrag(true); RequireDraftOnly("return to baseline");
            FinishBackgroundGainDrag(true); RequireDraftOnly("duplicate release");

            BeginBackgroundGainDrag(100, true);
            UpdateBackgroundGainDrag(76, true);
            if (Math.Abs(_backgroundGainDraft - Math.Clamp(layer.GainDb + .2, -60, 12)) > .000001)
                throw new InvalidOperationException("Smoke test: Shift does not give background gain tenth-decibel steps.");
            RequireDraftOnly("fine draft"); FinishBackgroundGainDrag(false); RequireDraftOnly("cancelled drag");

            BeginBackgroundGainTextEdit();
            if (!_backgroundGainEditing || BackgroundGainBox.Visibility != Visibility.Visible || BackgroundGainValue.Visibility != Visibility.Collapsed ||
                !BackgroundGainBox.IsKeyboardFocusWithin || BackgroundGainBox.SelectionLength != BackgroundGainBox.Text.Length)
                throw new InvalidOperationException("Smoke test: double-click background gain does not reveal and select its text editor.");
            BackgroundGainBox.Text = "-23";
            CloseBackgroundGainTextEdit(); BackgroundGainCommit(this, new RoutedEventArgs()); RequireDraftOnly("cancelled text edit");

            foreach (var invalid in new[] { "NaN", "Infinity", "-61", "13", "invalid" })
            {
                BeginBackgroundGainTextEdit(); BackgroundGainBox.Text = invalid;
                BackgroundGainCommit(this, new RoutedEventArgs()); RequireDraftOnly("invalid text");
                if (BackgroundGainBox.Visibility != Visibility.Collapsed || BackgroundGainValue.Text != BackgroundGainText(layer.GainDb))
                    throw new InvalidOperationException("Smoke test: invalid background gain did not restore the normal value.");
            }
            BeginBackgroundGainTextEdit(); BackgroundGainBox.Text = layer.GainDb.ToString("R", CultureInfo.InvariantCulture).Replace('.', ',');
            BackgroundGainCommit(this, new RoutedEventArgs()); RequireDraftOnly("unchanged text");

            double changed = layer.GainDb <= 11 ? layer.GainDb + 1 : layer.GainDb - 1;
            BeginBackgroundGainDrag(100, false);
            UpdateBackgroundGainDrag(changed > layer.GainDb ? 88 : 112, false);
            RequireDraftOnly("changed draft"); FinishBackgroundGainDrag(true);
            if (_backgroundLayer.GainDb != changed || _revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: background gain release did not commit exactly one edit.");
            FinishBackgroundGainDrag(true); BackgroundGainCommit(this, new RoutedEventArgs());
            if (_revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: background gain release or later focus loss committed twice.");
            await WaitForSmokePreviewAsync();
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            if (_backgroundLayer.GainDb != layer.GainDb)
                throw new InvalidOperationException("Smoke test: background gain Undo lost its original value.");

            _undo.Clear(); _redo.Clear(); revision = _revision;
            BeginBackgroundGainTextEdit(); BackgroundGainBox.Text = changed.ToString("R", CultureInfo.InvariantCulture).Replace('.', ',');
            Timeline.Focus(); // Actual focus loss is the click-outside commit path.
            if (_backgroundGainEditing || _backgroundLayer.GainDb != changed || _revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: background gain text did not commit once when focus left the field.");
            await WaitForSmokePreviewAsync();
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            if (_backgroundLayer.GainDb != layer.GainDb)
                throw new InvalidOperationException("Smoke test: typed background gain Undo lost its original value.");

            _undo.Clear(); _redo.Clear(); revision = _revision;
            BeginBackgroundGainTextEdit(); BackgroundGainBox.Text = changed.ToString("R", CultureInfo.InvariantCulture);
            BackgroundGainBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = BackgroundGainBox });
            if (!_backgroundGainEditing || _revision != revision || _undo.Count != 0)
                throw new InvalidOperationException("Smoke test: a click inside the background gain editor committed its draft.");
            var label = Descendants<TextBlock>(BackgroundPane).First(text => text.Text == "Gain");
            if (label.Focusable) throw new InvalidOperationException("Smoke test: background gain outside-click fixture must use a nonfocusable label.");
            label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = label });
            BackgroundGainCommit(this, new RoutedEventArgs());
            if (_backgroundGainEditing || _backgroundLayer.GainDb != changed || _revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: clicking a nonfocusable label did not commit background gain once.");
            await WaitForSmokePreviewAsync();
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            if (_backgroundLayer.GainDb != layer.GainDb)
                throw new InvalidOperationException("Smoke test: outside-click background gain Undo lost its original value.");
        }
        finally
        {
            FinishBackgroundGainDrag(false); CloseBackgroundGainTextEdit();
            if (_backgroundLayer.GainDb != layer.GainDb)
            {
                SetBackgroundLayer(layer, history: false);
                await WaitForSmokePreviewAsync();
            }
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty; BackgroundVisibilityCheck.IsChecked = visible;
            SyncBackgroundGainControls(); SetIdle("Ready");
        }
    }
}

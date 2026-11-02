using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyShortcutCatalogSmokeAsync(string? screenshot)
    {
        bool icons = !_buttonLabelsVisible;
        int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
        bool dirty = _dirty;
        Window? dialog = null;
        try
        {
            foreach (bool iconMode in new[] { false, true })
            {
                ApplyResponsiveButtonMode(iconMode);
                foreach (var (button, action) in ShortcutButtons())
                    if (!Equals(button.ToolTip, KeyboardShortcutCatalog.Tooltip(action)) || !ToolTipService.GetIsEnabled(button))
                        throw new InvalidOperationException($"Shortcut help smoke: {action} lost its shortcut tooltip in {(iconMode ? "icon" : "label")} mode.");
                if (!Equals(CustomBoundariesCheck.ToolTip, "Custom export boundaries (C)"))
                    throw new InvalidOperationException("Shortcut help smoke: custom boundaries omitted its export-specific tooltip.");
                if (!Equals(SliceButton.ToolTip, "Slice at playhead position (S)") || Timeline.SliceMode)
                    throw new InvalidOperationException("Shortcut help smoke: Slice is not a direct playhead command with a persistent tooltip.");
            }
            if (KeyboardShortcutsMenuItem.Header?.ToString() != "Keyboard shortcuts" ||
                !KeyboardShortcutCatalog.Entries.Any(item => item.Action == "Merge at nearest boundary" && item.Keys == "M") ||
                !KeyboardShortcutCatalog.Entries.Any(item => item.Action == "Loop A" && item.Keys == "Z") ||
                !KeyboardShortcutCatalog.Entries.Any(item => item.Action == "Loop B" && item.Keys == "X"))
                throw new InvalidOperationException("Shortcut help smoke: Help omitted the shortcut table or its two loop keys.");
            foreach (var factory in new Func<Action, FrameworkElement>[] { CreateLevelingOptionsContent, CreateMaximizerOptionsContent, CreateExportOptionsContent })
            {
                var draft = factory(() => { });
                ApplyContextShortcutTooltips(draft);
                if (Descendants<Button>(draft).Where(button => ContextButtonTooltip(button) != null).Any(button => !IsContextShortcutTooltip(button)))
                    throw new InvalidOperationException("Shortcut help smoke: a settings Apply/Cancel button omitted its Enter/Esc shortcut.");
            }
            dialog = CreateKeyboardShortcutsDialog(); dialog.Owner = this; dialog.Show();
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Render);
            var labels = Descendants<TextBlock>(dialog).Select(label => label.Text).ToArray();
            if (!Descendants<Button>(dialog).Any(button => Equals(button.ToolTip, "Close (Esc)")) ||
                !labels.Contains("Shortcut") || !labels.Contains("Action") || !labels.Contains("Context") ||
                !labels.Contains("Slice at playhead position") || !labels.Contains("Ctrl+Shift+O") ||
                !Descendants<ScrollViewer>(dialog).Any(viewer => viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto))
                throw new InvalidOperationException("Shortcut help smoke: the clean, scrollable shortcut table is incomplete.");
            if (screenshot != null)
                CaptureVisual(dialog, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-keyboard-shortcuts.png"));
            if (_revision != revision || _renderExecutionCount != executions || _undo.Count != history || _dirty != dirty)
                throw new InvalidOperationException("Shortcut help smoke: displaying help or shortcut tooltips changed audio or edit history.");
        }
        finally
        {
            dialog?.Close(); ApplyResponsiveButtonMode(icons);
            _responsiveLastWidth = double.NaN; UpdateResponsiveLayout();
        }
    }
}

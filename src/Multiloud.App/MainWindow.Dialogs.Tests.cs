using Multiloud.App.Controls;
using Multiloud.App.Services;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifySettingsDialogsSmokeAsync(string? screenshot)
    {
        var settings = _settings;
        int revision = _revision, undo = _undo.Count, executions = _renderExecutionCount;
        bool dirty = _dirty;
        Window? open = null;
        void Unchanged()
        {
            if (!ReferenceEquals(settings, _settings) || _revision != revision || _undo.Count != undo ||
                _renderExecutionCount != executions || _dirty != dirty)
                throw new InvalidOperationException("Smoke test: cancelling a settings dialog changed committed audio processing.");
        }
        void Screenshot(Window dialog, string suffix)
        {
            if (screenshot != null)
                CaptureVisual(dialog, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-" + suffix + ".png"));
        }
        try
        {
            LevelingOptionsClick(LevelingOptionsButton, new RoutedEventArgs());
            var content = _settingsFlyoutContent ?? throw new InvalidOperationException("Smoke test: leveling dropdown did not open.");
            await Dispatcher.InvokeAsync(content.UpdateLayout, DispatcherPriority.ContextIdle);
            VerifyCompactFlyoutSmoke(content);
            foreach (string name in new[] { "MaxLevelingBox", "TransitionLengthBox" })
            {
                var box = (TextBox)content.FindName(name);
                if (box.Parent is not Grid row || row.Children.OfType<TextBlock>().SingleOrDefault() is not { } label)
                    throw new InvalidOperationException("Smoke test: leveling numeric description and field no longer share a row.");
                var fieldBounds = box.TransformToAncestor(row).TransformBounds(new Rect(box.RenderSize));
                var labelBounds = label.TransformToAncestor(row).TransformBounds(new Rect(label.RenderSize));
                var face = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
                var text = new FormattedText("999", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face,
                    box.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(box).PixelsPerDip);
                double desired = Math.Ceiling(text.WidthIncludingTrailingWhitespace + box.Padding.Left + box.Padding.Right +
                    box.BorderThickness.Left + box.BorderThickness.Right + 2);
                if (Math.Abs(fieldBounds.Top + fieldBounds.Height / 2 - labelBounds.Top - labelBounds.Height / 2) > 1 ||
                    Math.Abs(box.ActualWidth - desired) > 1 || box.TextAlignment != TextAlignment.Center ||
                    fieldBounds.Left < labelBounds.Right || Math.Abs(fieldBounds.Right - row.ActualWidth) > 1)
                    throw new InvalidOperationException($"Smoke test: leveling dropdown number field {name} is not compact and single-row.");
            }
            if (screenshot != null) CaptureVisual((FrameworkElement)_settingsFlyout!.Child,
                Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-leveling-options-compact.png"));
            ((TextBox)content.FindName("MaxLevelingBox")).Text = "11";
            ((Slider)content.FindName("BassSensitivitySlider")).Value = .8;
            EscapeFlyoutSmoke();
            if (_settingsFlyout != null) throw new InvalidOperationException("Smoke test: Escape did not cancel Dynamic leveling options.");
            Unchanged();

            MaximizerOptionsClick(MaximizerOptionsButton, new RoutedEventArgs());
            content = _settingsFlyoutContent ?? throw new InvalidOperationException("Smoke test: peak dropdown did not open.");
            await Dispatcher.InvokeAsync(content.UpdateLayout, DispatcherPriority.ContextIdle);
            VerifyCompactFlyoutSmoke(content);
            if (screenshot != null) CaptureVisual((FrameworkElement)_settingsFlyout!.Child,
                Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-peak-options.png"));
            ((Slider)content.FindName("MaximizerCharacterSlider")).Value = .99;
            ((Slider)content.FindName("MaximizerTransientsSlider")).Value = .99;
            Descendants<Button>(content).Single(button => Equals(button.Content, "Cancel"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_settingsFlyout != null) throw new InvalidOperationException("Smoke test: Cancel did not close Peak protection options.");
            Unchanged();

            foreach (var expected in new[] { UnsavedProjectChoice.Save, UnsavedProjectChoice.Discard, UnsavedProjectChoice.Cancel })
            {
                var saveDialog = new UnsavedProjectDialog { Owner = this };
                open = saveDialog; saveDialog.Show();
                await Dispatcher.InvokeAsync(saveDialog.UpdateLayout, DispatcherPriority.ContextIdle);
                VerifyCompactDialogSmoke(saveDialog);
                var save = (Button)saveDialog.FindName("UnsavedSaveButton");
                var discard = (Button)saveDialog.FindName("UnsavedDiscardButton");
                var cancel = (Button)saveDialog.FindName("UnsavedCancelButton");
                if (!save.IsDefault || discard.IsDefault || cancel.IsDefault || !save.IsKeyboardFocused ||
                    !Equals(save.Content, "Save") || !Equals(discard.Content, "Don't save") || !Equals(cancel.Content, "Cancel"))
                    throw new InvalidOperationException("Smoke test: unsaved-project choices lost explicit labels or their safe Save default.");
                if (expected == UnsavedProjectChoice.Save) Screenshot(saveDialog, "unsaved-project");
                (expected == UnsavedProjectChoice.Save ? save : expected == UnsavedProjectChoice.Discard ? discard : cancel)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (saveDialog.IsVisible || saveDialog.Choice != expected)
                    throw new InvalidOperationException("Smoke test: an unsaved-project button resolved to the wrong choice.");
                open = null; Unchanged();
            }
            foreach (bool escape in new[] { true, false })
            {
                var saveDialog = new UnsavedProjectDialog { Owner = this };
                open = saveDialog; saveDialog.Show();
                await Dispatcher.InvokeAsync(saveDialog.UpdateLayout, DispatcherPriority.ContextIdle);
                if (escape) EscapeDialogSmoke(saveDialog); else saveDialog.Close();
                if (saveDialog.Choice != UnsavedProjectChoice.Cancel || saveDialog.IsVisible)
                    throw new InvalidOperationException("Smoke test: closing the unsaved-project dialog did not safely cancel.");
                open = null; Unchanged();
            }
            int saves = 0;
            if (ResolveSaveConfirmation(UnsavedProjectChoice.Cancel, () => { saves++; return true; }) ||
                !ResolveSaveConfirmation(UnsavedProjectChoice.Discard, () => { saves++; return true; }) || saves != 0 ||
                ResolveSaveConfirmation(UnsavedProjectChoice.Save, () => { saves++; return false; }) || saves != 1 ||
                !ResolveSaveConfirmation(UnsavedProjectChoice.Save, () => { saves++; return true; }) || saves != 2)
                throw new InvalidOperationException("Smoke test: save confirmation failed to honor save failure/cancellation before replacing a project.");

            var source = ProjectFileService.Resolve(new ProjectTrack { Path = "Unfound.wav" },
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Release.loudline"));
            open = new MissingFilesDialog([source]) { Owner = this }; open.Show();
            await Dispatcher.InvokeAsync(open.UpdateLayout, DispatcherPriority.ContextIdle);
            VerifyDialogThemeSmoke(open);
            if (Descendants<Button>(open).Any(button => button.Style == null || Math.Abs(button.ActualHeight - 28) > 1))
                throw new InvalidOperationException("Smoke test: missing-files actions stopped using the app's native button style.");
            open.Close(); open = null; Unchanged();
        }
        finally { CloseSettingsFlyout(); open?.Close(); }
    }

    private void VerifyCompactFlyoutSmoke(FrameworkElement content)
    {
        if (_settingsFlyout is not { IsOpen: true, Child: Border surface } || !ReferenceEquals(_settingsFlyoutContent, content) ||
            content is not StackPanel panel || panel.Margin != new Thickness(16) ||
            surface.Background is not SolidColorBrush background || background.Color != ((SolidColorBrush)FindResource("PanelBrush")).Color ||
            panel.Children.OfType<FrameworkElement>().LastOrDefault() is not { } actions)
            throw new InvalidOperationException("Smoke test: settings dropdown lost its floating compact themed surface.");
        var bounds = actions.TransformToAncestor(panel).TransformBounds(new Rect(actions.RenderSize));
        if (Math.Abs(bounds.Bottom - panel.ActualHeight) > 1 || actions.Margin.Bottom != 0 ||
            Descendants<Button>(content).Any(button => button.Style == null || Math.Abs(button.ActualHeight - 28) > 1 || button.Margin.Bottom != 0) ||
            Descendants<FrameworkElement>(content).Any(element => element.ToolTip != null && !IsContextShortcutTooltip(element)) ||
            Descendants<Button>(content).Where(button => ContextButtonTooltip(button) != null).Any(button => !IsContextShortcutTooltip(button)))
            throw new InvalidOperationException("Smoke test: settings dropdown actions have blank space, mismatched buttons, or incorrect shortcut tooltips.");
    }

    private void EscapeFlyoutSmoke()
    {
        var content = _settingsFlyoutContent ?? throw new InvalidOperationException("Smoke test: no dropdown is open.");
        var source = PresentationSource.FromVisual(content) ?? throw new InvalidOperationException("Smoke test: dropdown has no presentation source.");
        content.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private void VerifyDialogThemeSmoke(Window dialog)
    {
        bool SameColor(Brush actual, string resource) => actual is SolidColorBrush first &&
            dialog.FindResource(resource) is SolidColorBrush second && first.Color == second.Color;
        if (!SameColor(dialog.Background, "BackgroundBrush") || !SameColor(dialog.Foreground, "TextBrush") ||
            dialog.FontFamily.Source != FontFamily.Source || Math.Abs(dialog.FontSize - FontSize) > .01)
            throw new InvalidOperationException("Smoke test: an app dialog lost the app's theme or typography.");
    }

    private void VerifyCompactDialogSmoke(Window dialog)
    {
        VerifyDialogThemeSmoke(dialog);
        if (dialog.SizeToContent != SizeToContent.Height || dialog.ResizeMode != ResizeMode.NoResize ||
            dialog.Content is not StackPanel panel || panel.Margin != new Thickness(20) ||
            panel.Children.OfType<FrameworkElement>().LastOrDefault() is not { } actions)
            throw new InvalidOperationException("Smoke test: options/confirmation dialogs lost their compact content-sized layout.");
        var bounds = actions.TransformToAncestor(panel).TransformBounds(new Rect(actions.RenderSize));
        if (Math.Abs(bounds.Bottom - panel.ActualHeight) > 1 || actions.Margin.Bottom != 0 ||
            Descendants<Button>(dialog).Any(button => button.Style == null || Math.Abs(button.ActualHeight - 28) > 1 || button.Margin.Bottom != 0) ||
            Descendants<FrameworkElement>(dialog).Any(element => element.ToolTip != null && !IsContextShortcutTooltip(element)))
            throw new InvalidOperationException("Smoke test: dialog actions have reserved blank space, mismatched buttons, or unsolicited tooltips.");
    }

    private static void EscapeDialogSmoke(Window dialog)
    {
        var source = PresentationSource.FromVisual(dialog) ?? throw new InvalidOperationException("Smoke test: dialog has no native presentation source.");
        dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Multiloud.App;

public partial class MainWindow
{
    private IEnumerable<(ButtonBase Button, string Action)> ShortcutButtons()
    {
        yield return (PlayButton, "Play / pause");
        yield return (OriginalRadio, "Original"); yield return (PreviewRadio, "Result"); yield return (CompareRadio, "Comparison");
        yield return (WaveformEnvelopeCheck, "Envelope"); yield return (ShowGains, "Gains");
        yield return (BackgroundVisibilityCheck, "Background track"); yield return (BackgroundEnabledCheck, "Enable background");
        yield return (BackgroundSoloButton, "Background Solo"); yield return (CustomBoundariesCheck, "Custom export boundaries");
        yield return (FitAllButton, "Fit all"); yield return (SliceButton, "Slice at playhead position");
        yield return (MergeButton, "Merge at nearest boundary");
        yield return (LoopACheck, "Loop A"); yield return (LoopBCheck, "Loop B");
        yield return (AddAudioButton, "Add audio files"); yield return (FolderProjectButton, "Open Loudline project...");
        yield return (ExportButton, "Export all");
    }

    private string? ShortcutTooltip(ButtonBase button)
    {
        foreach (var pair in ShortcutButtons())
            if (ReferenceEquals(pair.Button, button)) return KeyboardShortcutCatalog.Tooltip(pair.Action);
        return null;
    }

    private void InitializeShortcutTooltips()
    {
        foreach (var (button, action) in ShortcutButtons())
        {
            button.ToolTip = KeyboardShortcutCatalog.Tooltip(action);
            ToolTipService.SetIsEnabled(button, true);
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }

    private bool IsShortcutButtonTooltip(FrameworkElement element) => element is ButtonBase button &&
        ShortcutTooltip(button) is { } expected && Equals(element.ToolTip, expected) && ToolTipService.GetIsEnabled(element);

    private static string? ContextButtonTooltip(Button button)
    {
        string? label = button.Content as string;
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (button.IsDefault) return $"{label} (Enter)";
        if (button.IsCancel || label == "Cancel") return $"{label} (Esc)";
        return null;
    }

    private static void ApplyContextShortcutTooltips(DependencyObject content)
    {
        foreach (var button in Descendants<Button>(content))
        {
            if (ContextButtonTooltip(button) is not { } tooltip) continue;
            button.ToolTip = tooltip;
            ToolTipService.SetIsEnabled(button, true);
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }

    private static bool IsContextShortcutTooltip(FrameworkElement element) => element is Button button &&
        ContextButtonTooltip(button) is { } tooltip && Equals(element.ToolTip, tooltip) && ToolTipService.GetIsEnabled(button);

    private void KeyboardShortcutsClick(object sender, RoutedEventArgs e)
    {
        var dialog = CreateKeyboardShortcutsDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private Window CreateKeyboardShortcutsDialog()
    {
        var dialog = new Window
        {
            Title = "Keyboard shortcuts", Width = 760, Height = Math.Min(720, Math.Max(420, ActualHeight - 60)),
            MinHeight = 380, MinWidth = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, UseLayoutRounding = true
        };
        dialog.SetResourceReference(StyleProperty, typeof(Window));
        var dock = new DockPanel { Margin = new Thickness(18) };
        dialog.Content = dock;
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 78,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => dialog.Close(); DockPanel.SetDock(close, Dock.Bottom); dock.Children.Add(close);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dock.Children.Add(scroll);
        var rows = new Grid(); scroll.Content = rows;
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        void Cell(string text, int row, int column, bool muted = false, bool heading = false)
        {
            var label = new TextBlock { Text = text, Margin = new Thickness(7, 6, 9, 6),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
            if (muted) label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(label, column); Grid.SetRow(label, row); rows.Children.Add(label);
        }
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("Shortcut", 0, 0, heading: true); Cell("Action", 0, 1, heading: true); Cell("Context", 0, 2, heading: true);
        int index = 1;
        foreach (var item in KeyboardShortcutCatalog.Entries)
        {
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (index % 2 == 1)
            {
                var stripe = new Border { Opacity = .3 };
                stripe.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                Grid.SetRow(stripe, index); Grid.SetColumnSpan(stripe, 3); rows.Children.Add(stripe);
            }
            Cell(item.Keys, index, 0); Cell(item.Action, index, 1); Cell(item.Context, index, 2, muted: true);
            index++;
        }
        ApplyContextShortcutTooltips(dock);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
        dialog.SourceInitialized += (_, _) => ThemeService.Apply(ThemeService.Mode);
        return dialog;
    }
}

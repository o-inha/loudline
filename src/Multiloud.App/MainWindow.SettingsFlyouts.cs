using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private Popup? _settingsFlyout;
    private string? _settingsFlyoutKey;
    private FrameworkElement? _settingsFlyoutContent, _settingsFlyoutButton;
    private Rect _settingsFlyoutAnchorBounds;

    private void ToggleSettingsFlyout(string key, FrameworkElement anchor, Func<Action, FrameworkElement> createContent)
    {
        if (_closed || _exporting) return;
        if (_settingsFlyout is { IsOpen: true } && _settingsFlyoutKey == key)
        { CloseSettingsFlyout(); return; }
        CloseSettingsFlyout();
        FinishStrengthGesture(false);
        var popup = new Popup
        {
            PlacementTarget = SettingsSectionAnchor(anchor), Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = SettingsFlyoutPlacement,
            StaysOpen = true, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None, Focusable = false
        };
        var content = createContent(() => { if (ReferenceEquals(_settingsFlyout, popup)) CloseSettingsFlyout(); });
        ApplyContextShortcutTooltips(content);
        var surface = new Border
        {
            Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            UseLayoutRounding = true, SnapsToDevicePixels = true
        };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        surface.SetValue(TextElement.FontFamilyProperty, FontFamily);
        surface.SetValue(TextElement.FontSizeProperty, FontSize);
        surface.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        surface.PreviewKeyDown += SettingsFlyoutKeyDown;
        popup.Child = surface;
        _settingsFlyout = popup; _settingsFlyoutKey = key;
        _settingsFlyoutContent = content; _settingsFlyoutButton = anchor;
        _settingsFlyoutAnchorBounds = SettingsAnchorBounds(anchor);
        PreviewMouseDown += SettingsOwnerMouseDown;
        PreviewKeyDown += SettingsFlyoutKeyDown;
        PreviewMouseWheel += SettingsOwnerMouseWheel;
        LocationChanged += SettingsOwnerChanged;
        StateChanged += SettingsOwnerChanged;
        SizeChanged += SettingsOwnerSizeChanged;
        Deactivated += SettingsOwnerChanged;
        LayoutUpdated += SettingsOwnerLayoutUpdated;
        popup.Closed += (_, _) => { if (ReferenceEquals(_settingsFlyout, popup)) CloseSettingsFlyout(); };
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(_settingsFlyout, popup) || !popup.IsOpen) return;
            content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }));
    }

    // Compute this from the popup's measured size on every layout pass. That
    // keeps its right edge attached to the gear's section even when a draft
    // reveals or collapses additional settings.
    private static CustomPopupPlacement[] SettingsFlyoutPlacement(Size popupSize, Size targetSize, Point offset) =>
    [
        new(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 3), PopupPrimaryAxis.Horizontal),
        new(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 3), PopupPrimaryAxis.Horizontal)
    ];

    private FrameworkElement SettingsSectionAnchor(FrameworkElement button)
    {
        // Export settings belong to the whole connected action, so the area
        // lines up with Export all rather than the gear at its left edge.
        if (ReferenceEquals(button, ExportOptionsButton)) return ExportButtons;
        var sectionStyle = TryFindResource("ProcessingSectionSurface");
        for (DependencyObject? current = button; current != null && !ReferenceEquals(current, this); current = TrackContextParent(current))
            if (current is Border border && ReferenceEquals(border.Style, sectionStyle)) return border;
        return button;
    }

    private Rect SettingsAnchorBounds(FrameworkElement anchor)
    {
        try { return anchor.TransformToAncestor(this).TransformBounds(new Rect(anchor.RenderSize)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    private static bool SettingsWithin(DependencyObject? source, DependencyObject target)
    {
        for (var current = source; current != null; current = TrackContextParent(current))
            if (ReferenceEquals(current, target)) return true;
        return false;
    }

    private void SettingsOwnerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settingsFlyoutContent != null && SettingsWithin(e.OriginalSource as DependencyObject, _settingsFlyoutContent)) return;
        // The original gear owns its second click. Closing on its mouse-down
        // would otherwise make the ensuing Click open a fresh draft immediately.
        if (_settingsFlyoutButton != null && SettingsWithin(e.OriginalSource as DependencyObject, _settingsFlyoutButton)) return;
        CloseSettingsFlyout();
    }

    private void SettingsOwnerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_settingsFlyoutContent == null || !SettingsWithin(e.OriginalSource as DependencyObject, _settingsFlyoutContent)) CloseSettingsFlyout();
    }

    private void SettingsFlyoutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseSettingsFlyout(); e.Handled = true; }
        else if (e.Key == Key.Enter && _settingsFlyoutContent is { } content)
        {
            var apply = Descendants<Button>(content).FirstOrDefault(button => button.IsDefault && button.IsEnabled);
            if (apply != null) { apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        }
    }

    private void SettingsOwnerChanged(object? sender, EventArgs e) => CloseSettingsFlyout();
    private void SettingsOwnerSizeChanged(object sender, SizeChangedEventArgs e) => CloseSettingsFlyout();
    private void SettingsOwnerLayoutUpdated(object? sender, EventArgs e)
    {
        if (_settingsFlyoutButton is { } anchor && SettingsAnchorBounds(anchor) != _settingsFlyoutAnchorBounds) CloseSettingsFlyout();
    }

    private void CloseSettingsFlyout()
    {
        var popup = _settingsFlyout;
        var anchor = _settingsFlyoutButton;
        bool restoreFocus = _settingsFlyoutContent is { } content && Keyboard.FocusedElement is DependencyObject focused &&
            SettingsWithin(focused, content);
        _settingsFlyout = null; _settingsFlyoutKey = null;
        _settingsFlyoutContent = null; _settingsFlyoutButton = null;
        PreviewMouseDown -= SettingsOwnerMouseDown;
        PreviewKeyDown -= SettingsFlyoutKeyDown;
        PreviewMouseWheel -= SettingsOwnerMouseWheel;
        LocationChanged -= SettingsOwnerChanged;
        StateChanged -= SettingsOwnerChanged;
        SizeChanged -= SettingsOwnerSizeChanged;
        Deactivated -= SettingsOwnerChanged;
        LayoutUpdated -= SettingsOwnerLayoutUpdated;
        if (popup != null) { popup.IsOpen = false; popup.Child = null; }
        if (restoreFocus && IsActive && anchor is { IsVisible: true, IsEnabled: true }) anchor.Focus();
    }
}

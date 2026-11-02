using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PathShape = System.Windows.Shapes.Path;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _iconsOnlyButtonStyle, _buttonLabelsVisible = true;
    private double _responsiveLastWidth = double.NaN, _responsiveLastOutputWidth = double.NaN;
    private (double ViewIcons, double ViewLabels, double ProcessingIcons, double ProcessingLabels, double Output) _responsiveMeasuredWidths;
    private List<ResponsiveButtonPair>? _responsiveButtonPairs;
    private sealed record ResponsiveButtonPair(ButtonBase Button, PathShape Icon, TextBlock Label, Thickness LabelledMargin);

    private void InitializeButtonStyle(bool iconsOnly)
    {
        InitializeShortcutTooltips();
        _iconsOnlyButtonStyle = iconsOnly;
        DefaultButtonStyleItem.IsChecked = !iconsOnly;
        IconsOnlyButtonStyleItem.IsChecked = iconsOnly;
        _responsiveLastWidth = double.NaN;
        UpdateResponsiveLayout();
    }

    private void DefaultButtonStyleClick(object sender, RoutedEventArgs e)
    { InitializeButtonStyle(false); SaveAudioPreference(); }
    private void IconsOnlyButtonStyleClick(object sender, RoutedEventArgs e)
    { InitializeButtonStyle(true); SaveAudioPreference(); }

    private List<ResponsiveButtonPair> ResponsiveButtonPairs()
    {
        if (_responsiveButtonPairs != null) return _responsiveButtonPairs;
        _responsiveButtonPairs = [];
        foreach (var button in new ButtonBase[] { OriginalRadio, PreviewRadio, CompareRadio, WaveformEnvelopeCheck,
            ShowGains, BackgroundVisibilityCheck, CustomBoundariesCheck, FitAllButton, FitTrackButton,
            AuditionMatchCheck, SliceButton, MergeButton, InsertSilenceButton, ExportButton, BackgroundEnabledCheck, BackgroundSoloButton })
        {
            if (button.Content is not StackPanel row || row.Children.OfType<PathShape>().SingleOrDefault() is not { } icon ||
                row.Children.OfType<TextBlock>().SingleOrDefault() is not { } label) continue;
            _responsiveButtonPairs.Add(new(button, icon, label, icon.Margin));
        }
        return _responsiveButtonPairs;
    }

    private void ApplyResponsiveButtonMode(bool iconsOnly)
    {
        _buttonLabelsVisible = !iconsOnly;
        foreach (var pair in ResponsiveButtonPairs())
        {
            pair.Label.Visibility = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
            pair.Icon.Margin = iconsOnly ? new Thickness(pair.LabelledMargin.Left, pair.LabelledMargin.Top, 0, pair.LabelledMargin.Bottom) : pair.LabelledMargin;
            string? shortcut = ShortcutTooltip(pair.Button);
            pair.Button.ToolTip = shortcut ?? (iconsOnly ? pair.Label.Text : null);
            ToolTipService.SetIsEnabled(pair.Button, shortcut != null || iconsOnly);
            ToolTipService.SetShowOnDisabled(pair.Button, shortcut != null || iconsOnly);
            // ContentPresenter and Border belong to the button's visual template,
            // not its logical Parent chain. Invalidate them too before reading
            // both widths synchronously; deferred invalidation would otherwise
            // let a labelled button reuse its cached icon-only DesiredSize.
            InvalidateResponsiveButtonAncestors(pair.Label);
            InvalidateResponsiveButtonAncestors(pair.Icon);
        }
        ViewControls.InvalidateMeasure(); ProcessingPanel.InvalidateMeasure();
    }

    private void InvalidateResponsiveButtonAncestors(DependencyObject source)
    {
        for (DependencyObject? current = source; current != null; current = TrackContextParent(current))
        {
            if (current is UIElement element) element.InvalidateMeasure();
            if (ReferenceEquals(current, ViewControls) || ReferenceEquals(current, ProcessingPanel) ||
                ReferenceEquals(current, BackgroundPane)) break;
        }
    }

    private string ResponsiveButtonMeasureSummary() =>
        $"view={_responsiveMeasuredWidths.ViewIcons:0.###}/{_responsiveMeasuredWidths.ViewLabels:0.###}, " +
        $"processing={_responsiveMeasuredWidths.ProcessingIcons:0.###}/{_responsiveMeasuredWidths.ProcessingLabels:0.###}, " +
        $"output={_responsiveMeasuredWidths.Output:0.###} (icon/label widths)";

    private bool IsResponsiveButtonTooltip(FrameworkElement element) => IsShortcutButtonTooltip(element) || IsContextShortcutTooltip(element) || ResponsiveButtonPairs().Any(pair =>
        ReferenceEquals(pair.Button, element) && pair.Label.Visibility == Visibility.Collapsed &&
        Equals(element.ToolTip, pair.Label.Text) && ToolTipService.GetIsEnabled(element));

    private static double ToolbarNaturalWidth(FrameworkElement element)
    {
        element.InvalidateMeasure();
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Icon layout transforms can leave tiny fractions above an integer.
        return Math.Ceiling(element.DesiredSize.Width - .000001);
    }

    private static double HorizontalInsets(Thickness value) => value.Left + value.Right;
    private double WideToolbarInsets => HorizontalInsets(WideToolbar.Padding) + HorizontalInsets(WideToolbar.BorderThickness) +
        HorizontalInsets(WideViewHost.Margin) + HorizontalInsets(WideProcessingHost.Margin) + HorizontalInsets(WideOutputHost.Margin);
    private double CompactToolbarInsets => HorizontalInsets(CompactViewBar.Padding) + HorizontalInsets(CompactViewBar.BorderThickness) +
        HorizontalInsets(CompactViewHost.Margin) + HorizontalInsets(CompactOutputHost.Margin);

    private void UpdateResponsiveLayout()
    {
        if (!_initialized || _updatingResponsive || WaveformPane.ActualWidth <= 0) return;
        double available = WaveformPane.ActualWidth;
        if (Math.Abs(_responsiveLastWidth - available) < .01 && _responsiveLastOutputWidth.Equals(OutputDeviceBox.Width)) return;
        _updatingResponsive = true;
        try
        {
            // Measure both presentations at their natural widths. The controls
            // never wrap, so the camera/layout cannot alternate between a short
            // wrapped width and a long single-row width around a breakpoint.
            OutputDeviceBox.InvalidateMeasure();
            double output = ToolbarNaturalWidth(OutputControls);
            ApplyResponsiveButtonMode(true);
            double iconView = ToolbarNaturalWidth(ViewControls), iconProcessing = ToolbarNaturalWidth(ProcessingPanel);
            ApplyResponsiveButtonMode(false);
            double labelledView = ToolbarNaturalWidth(ViewControls), labelledProcessing = ToolbarNaturalWidth(ProcessingPanel);
            _responsiveMeasuredWidths = (iconView, labelledView, iconProcessing, labelledProcessing, output);
            bool wide = available + .01 >= iconView + iconProcessing + output + WideToolbarInsets;
            bool iconsOnly = _iconsOnlyButtonStyle || (wide
                ? available + .01 < labelledView + labelledProcessing + output + WideToolbarInsets
                : available + .01 < labelledView + output + CompactToolbarInsets);
            ApplyResponsiveButtonMode(iconsOnly);

            if (wide != _wideLayout)
            {
                CompactProcessingHost.Content = null; CompactViewHost.Content = null; CompactOutputHost.Content = null;
                WideProcessingHost.Content = null; WideViewHost.Content = null; WideOutputHost.Content = null;
                if (wide)
                {
                    WideProcessingHost.Content = ProcessingPanel; WideViewHost.Content = ViewControls; WideOutputHost.Content = OutputControls;
                }
                else
                {
                    CompactProcessingHost.Content = ProcessingPanel; CompactViewHost.Content = ViewControls; CompactOutputHost.Content = OutputControls;
                }
                _wideLayout = wide;
            }
            WideToolbar.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            CompactProcessingBar.Visibility = CompactViewBar.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            _responsiveLastWidth = available; _responsiveLastOutputWidth = OutputDeviceBox.Width;
        }
        finally { _updatingResponsive = false; }
    }
}

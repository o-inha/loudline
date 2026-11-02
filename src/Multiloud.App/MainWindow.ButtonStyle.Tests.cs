using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyButtonStyleSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        bool preference = _iconsOnlyButtonStyle, dirty = _dirty;
        double width = Width;
        int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
        try
        {
            Width = 1100;
            IconsOnlyButtonStyleClick(IconsOnlyButtonStyleItem, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); UpdateResponsiveLayout(); UpdateLayout(); }, DispatcherPriority.ContextIdle);
            if (!_iconsOnlyButtonStyle || !IconsOnlyButtonStyleItem.IsChecked || DefaultButtonStyleItem.IsChecked)
                throw new InvalidOperationException("Smoke test: Icons only did not select and apply its exclusive button preference.");
            VerifyResponsiveButtonCaptionsSmoke(iconsOnly: true);
            VerifyResponsiveMeasuredWidthsSmoke();
            DefaultButtonStyleClick(DefaultButtonStyleItem, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); UpdateResponsiveLayout(); UpdateLayout(); }, DispatcherPriority.ContextIdle);
            if (_iconsOnlyButtonStyle || IconsOnlyButtonStyleItem.IsChecked || !DefaultButtonStyleItem.IsChecked || _wideLayout || _buttonLabelsVisible)
                throw new InvalidOperationException($"Smoke test: Default did not restore automatic compact icon buttons in a small window: editor={WaveformPane.ActualWidth:0.###}, wide={_wideLayout}, captions={_buttonLabelsVisible}; {ResponsiveButtonMeasureSummary()}.");
            VerifyResponsiveButtonCaptionsSmoke(iconsOnly: true);
            if (_revision != revision || _renderExecutionCount != executions || _undo.Count != history || _dirty != dirty)
                throw new InvalidOperationException("Smoke test: button style selection or resizing changed project/audio state.");
        }
        finally
        {
            Width = width; InitializeButtonStyle(preference);
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); UpdateResponsiveLayout(); UpdateLayout(); }, DispatcherPriority.ContextIdle);
        }
    }

    private void VerifyResponsiveMeasuredWidthsSmoke()
    {
        if (_responsiveMeasuredWidths.ViewLabels < _responsiveMeasuredWidths.ViewIcons + 100 ||
            _responsiveMeasuredWidths.ProcessingLabels < _responsiveMeasuredWidths.ProcessingIcons + 10)
            throw new InvalidOperationException($"Smoke test: synchronous icon and label measurements reused the same presentation: {ResponsiveButtonMeasureSummary()}.");
    }

    private void VerifyResponsiveButtonCaptionsSmoke(bool iconsOnly)
    {
        foreach (var pair in ResponsiveButtonPairs())
        {
            if (pair.Label.Visibility != (iconsOnly ? Visibility.Collapsed : Visibility.Visible) ||
                pair.Icon.Margin.Right != (iconsOnly ? 0 : pair.LabelledMargin.Right) ||
                ToolTipService.GetIsEnabled(pair.Button) != (iconsOnly || ShortcutTooltip(pair.Button) != null) ||
                ToolTipService.GetShowOnDisabled(pair.Button) != (iconsOnly || ShortcutTooltip(pair.Button) != null) ||
                !Equals(pair.Button.ToolTip, ShortcutTooltip(pair.Button) ?? (iconsOnly ? pair.Label.Text : null)) ||
                (pair.Button.ToolTip != null && !IsResponsiveButtonTooltip(pair.Button)))
                throw new InvalidOperationException($"Smoke test: {pair.Label.Text} did not switch its caption, spacing and tooltip together.");
            if (pair.Icon.Visibility != Visibility.Visible || pair.Button.Height is not (28 or 44))
                throw new InvalidOperationException("Smoke test: icon mode hid an icon or changed the uniform button height.");
        }
    }

    private async Task VerifyResponsiveButtonStylesSmokeAsync(Action<double, double> layout, FrameworkElement host, string? screenshot)
    {
        bool preference = _iconsOnlyButtonStyle;
        double outputWidth = OutputDeviceBox.Width;
        var fileColumn = WorkspaceGrid.ColumnDefinitions[0];
        GridLength fileWidth = fileColumn.Width;
        int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
        bool dirty = _dirty;
        double H(Thickness value) => value.Left + value.Right;
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(WaveformPane).TransformBounds(new Rect(element.RenderSize));
        double WidthOf(FrameworkElement element) => Math.Ceiling(Bounds(element).Width - .000001);
        double wideInsets = H(WideToolbar.Padding) + H(WideToolbar.BorderThickness) + H(WideViewHost.Margin) + H(WideProcessingHost.Margin) + H(WideOutputHost.Margin);
        double compactInsets = H(CompactViewBar.Padding) + H(CompactViewBar.BorderThickness) + H(CompactViewHost.Margin) + H(CompactOutputHost.Margin);
        void Check(bool wide, bool icons, string context)
        {
            if (_wideLayout != wide || _buttonLabelsVisible == icons ||
                WideToolbar.Visibility != (wide ? Visibility.Visible : Visibility.Collapsed) ||
                CompactViewBar.Visibility != (wide ? Visibility.Collapsed : Visibility.Visible) ||
                CompactProcessingBar.Visibility != (wide ? Visibility.Collapsed : Visibility.Visible) ||
                !ReferenceEquals((wide ? WideViewHost : CompactViewHost).Content, ViewControls) ||
                !ReferenceEquals((wide ? WideProcessingHost : CompactProcessingHost).Content, ProcessingPanel))
                throw new InvalidOperationException($"Smoke test: wrong responsive button state in {context}: wide={_wideLayout}, captions={_buttonLabelsVisible}, editor={WaveformPane.ActualWidth:0.###}; {ResponsiveButtonMeasureSummary()}.");
            VerifyResponsiveMeasuredWidthsSmoke();
            VerifyResponsiveButtonCaptionsSmoke(icons);
            if (Math.Abs(ViewControls.ActualHeight - 28) > .25)
                throw new InvalidOperationException("Smoke test: view actions wrapped instead of switching to icons.");
            if (!wide) return;
            var left = Bounds(ViewControls); var middle = Bounds(ProcessingPanel); var right = Bounds(OutputControls);
            double before = middle.Left - left.Right, after = right.Left - middle.Right;
            if (before < 11.5 || after < 11.5 || Math.Abs(before - after) > 1 ||
                left.Left < -1 || right.Right > WaveformPane.ActualWidth + 1)
                throw new InvalidOperationException($"Smoke test: consolidated controls overlap or are not centered in {context}: before={before:0.###}, after={after:0.###}.");
        }
        void Capture(string name)
        {
            if (screenshot != null) CaptureVisual(host, Path.Combine(Path.GetDirectoryName(screenshot)!, name));
        }
        try
        {
            InitializeButtonStyle(false); layout(8192, 1200);
            double initialView = WidthOf(ViewControls);
            foreach (double output in new[] { outputWidth, initialView + 160 })
            {
                OutputDeviceBox.Width = output;
                // Changing a child Width propagates through WPF's deferred
                // measure queue. Complete a generous layout before reading
                // rendered widths; detached CI hosts are not desktop-clipped.
                InitializeButtonStyle(false); layout(8192, 1200);
                Check(true, false, "large default");
                double outside = host.ActualWidth - WaveformPane.ActualWidth;
                double labelledView = WidthOf(ViewControls), labelledProcessing = WidthOf(ProcessingPanel), right = WidthOf(OutputControls);
                double compactLabels = labelledView + right + compactInsets;
                double wideLabels = labelledView + labelledProcessing + right + wideInsets;
                InitializeButtonStyle(true); layout(8192, 1200);
                Check(true, true, "large forced icons");
                double wideIcons = WidthOf(ViewControls) + WidthOf(ProcessingPanel) + right + wideInsets;
                if (compactLabels + 32 >= wideIcons || wideIcons + 32 >= wideLabels)
                    throw new InvalidOperationException("Smoke test: fixture cannot distinguish all four requested responsive states.");
                InitializeButtonStyle(false);
                var states = new[]
                {
                    (Width: compactLabels - 16, Wide: false, Icons: true, Name: "Loudline-toolbar-two-icons.png"),
                    (Width: (compactLabels + wideIcons) / 2, Wide: false, Icons: false, Name: "Loudline-toolbar-two-labels.png"),
                    (Width: (wideIcons + wideLabels) / 2, Wide: true, Icons: true, Name: "Loudline-toolbar-one-icons.png"),
                    (Width: wideLabels + 16, Wide: true, Icons: false, Name: "Loudline-toolbar-one-labels.png")
                };
                foreach (var state in states)
                {
                    layout(outside + state.Width, 1000); Check(state.Wide, state.Icons, state.Name);
                    if (output == outputWidth) Capture(state.Name);
                }
                foreach (var state in states.Reverse())
                { layout(outside + state.Width, 1000); Check(state.Wide, state.Icons, "reverse " + state.Name); }
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    layout(outside + wideIcons + 4, 1000); Check(true, true, "just consolidated");
                    layout(outside + wideIcons - 4, 1000); Check(false, false, "just compact");
                }
                layout(outside + wideIcons + 4, 1000); Check(true, true, "before widening file list");
                fileColumn.Width = new GridLength(fileColumn.ActualWidth + 96);
                layout(outside + wideIcons + 4, 1000);
                if (_wideLayout) throw new InvalidOperationException("Smoke test: consolidation ignored the file pane's actual width.");
                fileColumn.Width = fileWidth;
                layout(outside + wideIcons + 4, 1000); Check(true, true, "restored file list");
                InitializeButtonStyle(true);
                foreach (var state in states)
                { layout(outside + state.Width, 1000); Check(state.Width >= wideIcons, true, "forced " + state.Name); }
                InitializeButtonStyle(false);
            }
            if (_revision != revision || _renderExecutionCount != executions || _undo.Count != history || _dirty != dirty)
                throw new InvalidOperationException("Smoke test: responsive button layout changed audio, history or project state.");
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        }
        finally
        {
            OutputDeviceBox.Width = outputWidth; fileColumn.Width = fileWidth;
            InitializeButtonStyle(preference);
        }
    }
}

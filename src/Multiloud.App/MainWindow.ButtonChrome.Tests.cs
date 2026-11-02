using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyButtonChromeSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var theme = ThemeService.Mode;
        var focused = Keyboard.FocusedElement;
        var processVisibility = RebuildPreviewButton.Visibility;
        var processContent = RebuildPreviewButton.Content;
        bool? solo = BackgroundSoloButton.IsChecked;
        int revision = _revision, executions = _renderExecutionCount;
        bool dirty = _dirty;
        async Task Layout() => await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        static Color ColorOf(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        try
        {
            foreach (string mode in new[] { "Dark", "Light" })
            {
                ThemeService.Apply(mode); UpdateThemeChecks();
                RebuildPreviewButton.Content = "Resume processing";
                RebuildPreviewButton.Visibility = Visibility.Visible;
                RebuildPreviewButton.ApplyTemplate();
                await Layout();
                var resume = Descendants<TextBlock>(RebuildPreviewButton).SingleOrDefault(text => text.Text == "Resume processing");
                if (resume == null || ColorOf(resume.Foreground) != ColorOf(RebuildPreviewButton.Foreground) ||
                    mode == "Dark" && (ColorOf(resume.Foreground).R > 70 || ColorOf(resume.Foreground).G > 70 || ColorOf(resume.Foreground).B > 70))
                    throw new InvalidOperationException("Smoke test: the bright Resume processing button did not paint its explicit contrasting text color.");
                if (ColorOf(ExportOptionsButton.Background) != ColorOf((Brush)FindResource("ButtonBrush")) ||
                    ColorOf(ExportOptionsButton.Foreground) != ColorOf((Brush)FindResource("TextBrush")))
                    throw new InvalidOperationException("Smoke test: export settings gear did not use ordinary button colors.");
                if (ColorOf(RebuildPreviewButton.Background) != ColorOf((Brush)FindResource("AccentBrush")))
                    throw new InvalidOperationException("Smoke test: Resume processing lost its primary action surface.");

                foreach (var button in new Control[] { FitAllButton, ExportOptionsButton, ExportButton, LevelingOptionsButton,
                    MaximizerOptionsButton, DeltaCheck, RebuildPreviewButton, ShowGains, CustomBoundariesCheck, SliceButton, MergeButton,
                    BackgroundSoloButton, OriginalRadio, LimiterCheck })
                {
                    button.ApplyTemplate();
                    if (button.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == IsKeyboardFocusedProperty) ||
                        button.Template.Triggers.OfType<MultiTrigger>().Any(trigger => trigger.Conditions.OfType<Condition>().Any(condition => condition.Property == IsKeyboardFocusedProperty)))
                        throw new InvalidOperationException("Smoke test: a button still changes its appearance because it retains keyboard focus.");
                }
                Keyboard.Focus(FitAllButton); await Layout();
                if (!FitAllButton.IsMouseOver && FitAllButton.Template.FindName("SegmentSurface", FitAllButton) is Border focusedSurface &&
                    ColorOf(focusedSurface.Background) != ColorOf(FitAllButton.Background))
                    throw new InvalidOperationException("Smoke test: a focused ordinary button remained highlighted after the pointer left.");

                BackgroundSoloButton.IsChecked = true; BackgroundSoloButton.ApplyTemplate(); await Layout();
                if (BackgroundSoloButton.Template.FindName("MonitorSurface", BackgroundSoloButton) is not Border soloSurface ||
                    ColorOf(soloSurface.Background) != ColorOf((Brush)FindResource(BackgroundSoloButton.IsMouseOver ? "MonitorActiveHoverBrush" : "MonitorActiveBrush")) ||
                    ColorOf(BackgroundSoloButton.Foreground) != ColorOf((Brush)FindResource("MonitorActiveTextBrush")))
                    throw new InvalidOperationException("Smoke test: Background Solo did not use the amber monitoring-only active state.");
                var soloIcon = Descendants<System.Windows.Shapes.Path>(BackgroundSoloButton).Single();
                var soloLabel = Descendants<TextBlock>(BackgroundSoloButton).Single(text => text.Text == "Solo");
                if (ColorOf(soloIcon.Stroke) != ColorOf(BackgroundSoloButton.Foreground) ||
                    ColorOf(soloLabel.Foreground) != ColorOf(BackgroundSoloButton.Foreground))
                    throw new InvalidOperationException("Smoke test: Solo icon and label did not follow the amber monitoring foreground.");
                BackgroundSoloButton.IsChecked = false;
            }

            ExportOptionsButton.ApplyTemplate(); ExportButton.ApplyTemplate(); await Layout();
            var gear = ExportOptionsButton.TransformToAncestor(ExportButtons).TransformBounds(new Rect(ExportOptionsButton.RenderSize));
            var export = ExportButton.TransformToAncestor(ExportButtons).TransformBounds(new Rect(ExportButton.RenderSize));
            if (Math.Abs(gear.Height - 44) > .25 || Math.Abs(export.Height - 44) > .25 || Math.Abs(export.Left - gear.Right - 1) > .25 ||
                ExportOptionsButton.Template.FindName("ButtonSurface", ExportOptionsButton) is not Border { CornerRadius: var first } ||
                ExportButton.Template.FindName("ButtonSurface", ExportButton) is not Border { CornerRadius: var last } ||
                first != new CornerRadius(4, 0, 0, 4) || last != new CornerRadius(0, 4, 4, 0))
                throw new InvalidOperationException("Smoke test: export gear and action are not a connected group with only outer rounded corners.");
            if (SliceButton.Content is not StackPanel ||
                !Descendants<System.Windows.Shapes.Path>(SliceButton).Any(icon => ReferenceEquals(icon.Data, FindResource("SliceIconGeometry"))) ||
                !Descendants<TextBlock>(SliceButton).Any(text => text.Text == "Slice") || SliceButton.ToolTip != null && !IsResponsiveButtonTooltip(SliceButton) ||
                System.Windows.Automation.AutomationProperties.GetName(SliceButton) != "Slice at playhead position")
                throw new InvalidOperationException("Smoke test: Slice did not show the supplied icon and its requested text.");
            VerifyToolbarIconLayoutSmoke();
            if (_revision != revision || _renderExecutionCount != executions || _dirty != dirty)
                throw new InvalidOperationException("Smoke test: button appearance or monitoring Solo changed audio processing or the project.");
        }
        finally
        {
            BackgroundSoloButton.IsChecked = solo;
            RebuildPreviewButton.Content = processContent;
            RebuildPreviewButton.Visibility = processVisibility;
            ThemeService.Apply(theme); UpdateThemeChecks();
            if (focused != null) Keyboard.Focus(focused); else Keyboard.ClearFocus();
            await Layout();
        }
    }

    private void VerifyToolbarIconLayoutSmoke()
    {
        var expected = new (Control Control, string Label, string Geometry)[]
        {
            (OriginalRadio, "Original", "OriginalIconGeometry"), (PreviewRadio, "Result", "ResultIconGeometry"),
            (CompareRadio, "Comparison", "ComparisonIconGeometry"), (WaveformEnvelopeCheck, "Envelope", "EnvelopeIconGeometry"),
            (ShowGains, "Gains", "GainsIconGeometry"), (BackgroundVisibilityCheck, "Background track", "BackgroundIconGeometry"),
            (CustomBoundariesCheck, "Custom boundaries", "CustomBoundariesIconGeometry"), (FitAllButton, "Fit all", "FitAllIconGeometry"),
            (FitTrackButton, "Fit track", "FitTrackIconGeometry"), (AuditionMatchCheck, "Level-match A/B", "DiffIconGeometry"),
            (SliceButton, "Slice", "SliceIconGeometry"), (MergeButton, "Merge", "MergeIconGeometry"), (BackgroundEnabledCheck, "Enabled", "PowerIconGeometry"),
            (BackgroundSoloButton, "Solo", "SoloIconGeometry")
        };
        static double IconSizeErrorInPixels(System.Windows.Shapes.Path icon, double actual, double wanted, bool horizontal)
        {
            var matrix = icon.LayoutTransform.Value;
            double scale = horizontal ? new Vector(matrix.M11, matrix.M12).Length : new Vector(matrix.M21, matrix.M22).Length;
            var dpi = VisualTreeHelper.GetDpi(icon);
            return Math.Abs(actual - wanted) * scale * (horizontal ? dpi.DpiScaleX : dpi.DpiScaleY);
        }
        foreach (var (control, label, geometry) in expected)
        {
            var icon = Descendants<System.Windows.Shapes.Path>(control).SingleOrDefault(path => ReferenceEquals(path.Data, FindResource(geometry)));
            var text = Descendants<TextBlock>(control).SingleOrDefault(block => block.Text == label);
            if (icon is null || text is null || icon.Parent is not StackPanel row || !ReferenceEquals(text.Parent, row) ||
                row.Orientation != Orientation.Horizontal || row.Children.IndexOf(icon) >= row.Children.IndexOf(text) ||
                // Layout rounding may change local dimensions by more than a
                // quarter DIP through the 2/3 toolbar transform. Judge the
                // rounding in device pixels while retaining the aspect check.
                IconSizeErrorInPixels(icon, icon.ActualWidth, ReferenceEquals(control, AuditionMatchCheck)
                    ? icon.Data.Bounds.Width * ((21 - icon.StrokeThickness) / icon.Data.Bounds.Height) + icon.StrokeThickness : 24, true) > .51 ||
                IconSizeErrorInPixels(icon, icon.ActualHeight, ReferenceEquals(control, AuditionMatchCheck) ? 21 : 24, false) > .51 ||
                icon.Stretch != (ReferenceEquals(control, AuditionMatchCheck) ? Stretch.Uniform : Stretch.None) ||
                icon.VerticalAlignment != VerticalAlignment.Center || icon.StrokeThickness != 2 || icon.StrokeStartLineCap != PenLineCap.Round ||
                icon.StrokeLineJoin != PenLineJoin.Round || control.ActualHeight != 28)
                throw new InvalidOperationException($"Smoke test: {label} lost its consistent supplied icon before the label (size {icon?.ActualWidth:F2} × {icon?.ActualHeight:F2}, stretch {icon?.Stretch}).");
        }
        FrameworkElement[] actual = ViewControls.Children.OfType<FrameworkElement>().ToArray();
        if (actual.Length != 13)
            throw new InvalidOperationException("Smoke test: toolbar control/separator count changed.");
        var expectedOrder = new FrameworkElement[]
        {
            (FrameworkElement)((FrameworkElement)OriginalRadio.Parent).Parent, WaveformEnvelopeCheck, ShowGains,
            BackgroundVisibilityCheck, CustomBoundariesCheck, actual[5], FitButtons, actual[7], AuditionMatchCheck, actual[9], SliceButton, MergeButton, InsertSilenceButton
        };
        if (!actual.SequenceEqual(expectedOrder) || new[] { actual[5], actual[7], actual[9] }.Any(element =>
            element is not Border separator || separator.Width != 1 || separator.Height != 16 ||
            separator.Margin.Left != 10 || separator.Margin.Right != 10 || separator.Opacity != .5))
            throw new InvalidOperationException("Smoke test: toolbar order or its three faint, spaced separators changed.");
        foreach (var section in new[] { LevelingCheck, MatchingCheck, LimiterCheck })
        {
            section.ApplyTemplate();
            if (section.Template.FindName("PowerCheck", section) is not System.Windows.Shapes.Path power ||
                !ReferenceEquals(power.Data, FindResource("PowerIconGeometry")) || power.Width != 24 || power.Height != 24 ||
                power.Stretch != Stretch.None)
                throw new InvalidOperationException("Smoke test: module enable control did not replace its check mark with the supplied power icon.");
        }
    }
}

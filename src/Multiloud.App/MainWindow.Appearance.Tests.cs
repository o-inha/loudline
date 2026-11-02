using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyToolbarButtonHeightsSmoke()
    {
        if (TargetBox.TextAlignment != TextAlignment.Center || CeilingBox.TextAlignment != TextAlignment.Center ||
            Math.Abs(TargetBox.ActualHeight - CeilingBox.ActualHeight) > .25 || Math.Abs(TargetBox.FontSize - CeilingBox.FontSize) > .01 ||
            Math.Abs(TargetBox.ActualWidth - CeilingBox.ActualWidth) > .25)
            throw new InvalidOperationException("Smoke test: the global LUFS and ceiling fields are not centered and consistently sized.");
        if (OriginalRadio.Parent is not StackPanel { Parent: Border group })
            throw new InvalidOperationException("Smoke test: connected view button group is missing.");
        var buttons = Descendants<Button>(ViewControls).Where(button => button.Visibility == Visibility.Visible)
            .Cast<Control>().Concat(new Control[] { WaveformEnvelopeCheck, ShowGains, AuditionMatchCheck })
            .Concat(Descendants<Button>(ProcessingPanel).Where(button => !ReferenceEquals(button, ExportButton) && !ReferenceEquals(button, ExportOptionsButton)));
        if (group.ActualHeight <= 0 || buttons.Any(button => Math.Abs(button.ActualHeight - group.ActualHeight) > .25) ||
            new[] { OriginalRadio, PreviewRadio, CompareRadio }.Any(button => Math.Abs(button.ActualHeight + group.BorderThickness.Top + group.BorderThickness.Bottom - group.ActualHeight) > .25))
            throw new InvalidOperationException("Smoke test: toolbar switches, action buttons and the connected group have different visible heights.");
        if (Math.Abs(ExportButton.ActualHeight - ProcessingPanel.ActualHeight) > .25 ||
            ProcessingPanel.Children.OfType<Border>().Any(section => Math.Abs(section.ActualHeight - ExportButton.ActualHeight) > .25) ||
            !Descendants<TextBlock>(ExportButton).Any(text => text.Text == "Export all") ||
            !Descendants<System.Windows.Shapes.Path>(ExportButton).Any(path => ReferenceEquals(path.Data, FindResource("ExportIconGeometry"))) ||
            ExportButton.Background is not SolidColorBrush exportColor || exportColor.Color != ((SolidColorBrush)FindResource("AccentBrush")).Color)
            throw new InvalidOperationException("Smoke test: primary Export all action lost its section height, supplied icon or accent surface.");
        VerifyToolbarSurfacesSmoke();
        VerifyTransportLayoutSmoke();
    }

    private void VerifyTransportLayoutSmoke()
    {
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(TransportLayout).TransformBounds(new Rect(element.RenderSize));
        var transport = Bounds(TransportControls);
        var information = Bounds(SelectionInfo);
        var cursor = Bounds(CursorPositionText);
        if (Math.Abs((transport.Left + transport.Right - TransportLayout.ActualWidth) / 2) > .5 ||
            information.Right > transport.Left + .5 || cursor.Left < transport.Right - .5 ||
            SelectionInfo.TextAlignment != TextAlignment.Left || CursorPositionText.TextAlignment != TextAlignment.Right ||
            CursorPositionText.FontSize != 12 || SelectionInfo.TextTrimming != TextTrimming.CharacterEllipsis ||
            CursorPositionText.TextTrimming != TextTrimming.CharacterEllipsis || TransportLayout.ActualHeight != 36)
            throw new InvalidOperationException("Smoke test: track information, centered transport and right cursor readout lost their independent layout.");
        string original = CursorPositionText.Text;
        try
        {
            foreach (string text in new[] { "", "00:00:12.345  ·  −12.3 dBFS\nEnvelope +1.2 dB" })
            {
                CursorPositionText.Text = text;
                TransportLayout.UpdateLayout();
                var current = Bounds(TransportControls);
                if (TransportLayout.ActualHeight != 36 || Math.Abs((current.Left + current.Right - TransportLayout.ActualWidth) / 2) > .5)
                    throw new InvalidOperationException("Smoke test: cursor visibility resized the footer or moved the centered transport.");
            }
        }
        finally { CursorPositionText.Text = original; TransportLayout.UpdateLayout(); }
    }

    private void VerifyToolbarSurfacesSmoke()
    {
        foreach (var (label, slider) in new[] { (StrengthLabel, StrengthSlider), (MatchingStrengthLabel, MatchingStrengthSlider) })
        {
            Rect labelBounds = label.TransformToAncestor(ProcessingPanel).TransformBounds(new Rect(label.RenderSize));
            Rect sliderBounds = slider.TransformToAncestor(ProcessingPanel).TransformBounds(new Rect(slider.RenderSize));
            if (Math.Abs((labelBounds.Left + labelBounds.Right - sliderBounds.Left - sliderBounds.Right) / 2) > .25 ||
                labelBounds.Bottom > sliderBounds.Top + .25 || label.TextAlignment != TextAlignment.Center || ProcessingPanel.ActualHeight != 44)
                throw new InvalidOperationException("Smoke test: Strength labels are not centered above their sliders within the compact toolbar.");
        }

        Rect folder = FolderProjectButton.TransformToAncestor(FilesPanel).TransformBounds(new Rect(FolderProjectButton.RenderSize));
        Rect add = AddAudioButton.TransformToAncestor(FilesPanel).TransformBounds(new Rect(AddAudioButton.RenderSize));
        if (FolderProjectButton.Content is not System.Windows.Shapes.Path || !Equals(FolderProjectButton.ToolTip, KeyboardShortcutCatalog.Tooltip("Open Loudline project...")) ||
            !ToolTipService.GetIsEnabled(FolderProjectButton) || Math.Abs(folder.Height - 28) > .25 || Math.Abs(folder.Width - 28) > .25 ||
            folder.Right >= add.Left || Math.Abs(folder.Top - add.Top) > .25 || Math.Abs(folder.Height - add.Height) > .25 ||
            AddAudioButton.HorizontalAlignment != HorizontalAlignment.Stretch || Math.Abs(add.Right - FilesPanel.ActualWidth + 8) > .5)
            throw new InvalidOperationException("Smoke test: project-open icon and stretched Add audio files button lost their layout or requested tooltip.");

        if (LevelingOptionsButton.Content is not System.Windows.Shapes.Path levelingGear ||
            MaximizerOptionsButton.Content is not System.Windows.Shapes.Path protectionGear ||
            levelingGear.Data is null || !ReferenceEquals(levelingGear.Data, protectionGear.Data) ||
            levelingGear.StrokeThickness != protectionGear.StrokeThickness ||
            !Descendants<TextBlock>(ProcessingPanel).Any(text => text.Text == "03  PEAK PROTECTION"))
            throw new InvalidOperationException("Smoke test: module options lost their shared vector gear or Peak protection label.");

        var controls = Descendants<Button>(ViewControls).Cast<Control>().Concat(Descendants<Button>(ProcessingPanel))
            .Concat(new Control[] { FolderProjectButton, AddAudioButton, OriginalRadio, PreviewRadio, CompareRadio, WaveformEnvelopeCheck, ShowGains, AuditionMatchCheck });
        foreach (var control in controls)
        {
            control.ApplyTemplate();
            if (control.BorderThickness != new Thickness(0))
                throw new InvalidOperationException("Smoke test: a toolbar button retained its border.");
            foreach (string surface in new[] { "ButtonSurface", "TextToggleSurface", "SegmentSurface", "MonitorSurface" })
                if (control.Template.FindName(surface, control) is Border border && border.BorderThickness != new Thickness(0))
                    throw new InvalidOperationException("Smoke test: button template retained visible border chrome.");
        }
        foreach (var combo in new[] { OutputDeviceBox })
        {
            combo.ApplyTemplate();
            if (combo.BorderThickness != new Thickness(0) || Math.Abs(combo.ActualHeight - 28) > .25 ||
                Descendants<Border>(combo).Any(border => border.Name == "DeviceBorder" && border.BorderThickness != new Thickness(0)) ||
                combo.Template.FindName("PART_Popup", combo) is not Popup { Child: Border popup } || popup.BorderThickness != new Thickness(0))
                throw new InvalidOperationException("Smoke test: a dropdown retained its border or changed its consistent height.");
        }

        bool? previous = AuditionMatchCheck.IsChecked;
        try
        {
            AuditionMatchCheck.IsChecked = true;
            AuditionMatchCheck.ApplyTemplate();
            if (AuditionMatchCheck.Template.FindName("MonitorSurface", AuditionMatchCheck) is not Border { Background: SolidColorBrush amber } ||
                amber.Color.R <= amber.Color.G || amber.Color.G <= amber.Color.B ||
                amber.Color == ((SolidColorBrush)FindResource("ButtonActiveBrush")).Color)
                throw new InvalidOperationException("Smoke test: enabled A/B monitoring is not distinctly amber.");
        }
        finally { AuditionMatchCheck.IsChecked = previous; }
    }

    // The caller fits/hovers a track and completes layout before this assertion.
    // Inspect actual native-control bounds so container alignment alone cannot
    // conceal a label, unit or reset button that shifts one line's right edge.
    private void VerifyHoverAppearanceSmoke()
    {
        var labels = Descendants<TextBlock>(Timeline).ToArray();
        var input = labels.Single(text => text.Text == "Input");
        var target = labels.Single(text => text.Text == "Target");
        var inputUnit = labels.Single(text => text.Text == "dB");
        var targetUnit = labels.Single(text => text.Text == "LUFS");
        var lines = new[] { input, inputUnit, target, targetUnit };
        Rect TimelineBounds(FrameworkElement element) => element.TransformToAncestor(Timeline).TransformBounds(new Rect(element.RenderSize));
        var bounds = lines.Select(TimelineBounds).ToArray();
        if (bounds.Any(rect => rect.Width <= 0 || rect.Height <= 0) ||
            bounds.Max(rect => rect.Right) - bounds.Min(rect => rect.Right) > .75)
            throw new InvalidOperationException($"Smoke test: hover labels/units do not share a right edge ({string.Join(", ", bounds.Select(rect => rect.Right.ToString("0.##", CultureInfo.InvariantCulture)))}).");
        if (lines.Any(text => text.HorizontalAlignment != HorizontalAlignment.Right || text.TextAlignment != TextAlignment.Right || Math.Abs(text.FontSize - 12) > .01))
            throw new InvalidOperationException("Smoke test: hover labels/units lost right alignment or their consistent font size.");
        if (!(bounds[0].Top < bounds[1].Top && bounds[1].Top < bounds[2].Top && bounds[2].Top < bounds[3].Top))
            throw new InvalidOperationException("Smoke test: hover input and target values are not stacked beneath their labels.");
        if (inputUnit.Parent is not StackPanel inputRow || Math.Abs(bounds[2].Top - TimelineBounds(inputRow).Bottom - 4) > .75)
            throw new InvalidOperationException("Smoke test: hover input and target sections lost their four-pixel gap.");

        var boxes = Descendants<TextBox>(Timeline).Where(box =>
            System.Windows.Automation.AutomationProperties.GetName(box) is "Track input gain in decibels" or "Track target loudness in LUFS").ToArray();
        if (boxes.Length != 2) throw new InvalidOperationException("Smoke test: native track value editors are missing.");
        foreach (var box in boxes)
        {
            var face = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            var reference = new FormattedText("-99", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face,
                box.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(box).PixelsPerDip);
            var expected = Math.Ceiling(reference.WidthIncludingTrailingWhitespace) + box.Padding.Left + box.Padding.Right + box.BorderThickness.Left + box.BorderThickness.Right;
            if (box.TextAlignment != TextAlignment.Right || box.HorizontalAlignment != HorizontalAlignment.Right || Math.Abs(box.FontSize - 12) > .01 ||
                !double.IsFinite(box.MaxWidth) || box.MaxWidth >= 32 || Math.Abs(box.MaxWidth - expected) > .75 || Math.Abs(box.Width - box.MaxWidth) > .01)
                throw new InvalidOperationException($"Smoke test: native track editor is not right-aligned and sized to the three-character reference (width={box.Width:0.##}, max={box.MaxWidth:0.##}, expected={expected:0.##}).");
            if (box.Parent is not Grid number || Descendants<TextBlock>(number).SingleOrDefault() is not { } value ||
                value.HorizontalAlignment != HorizontalAlignment.Right || value.TextAlignment != TextAlignment.Right || Math.Abs(value.FontSize - 12) > .01)
                throw new InvalidOperationException("Smoke test: plain track number lost right alignment or its value font.");
        }

        foreach (var card in Descendants<ListBoxItem>(FileList).Where(item => item.ActualWidth > 0 && item.ActualHeight > 0))
        {
            if (card.Template.FindName("B", card) is not FrameworkElement surface ||
                card.Template.FindName("TrackRemoveButton", card) is not FrameworkElement remove ||
                card.Template.FindName("TrackGrip", card) is not FrameworkElement grip) continue;
            Rect CardBounds(FrameworkElement element) => element.TransformToAncestor(card).TransformBounds(new Rect(element.RenderSize));
            var root = CardBounds(surface); var trash = CardBounds(remove); var handle = CardBounds(grip);
            if (Math.Abs(trash.Top - root.Top - 2) > .75 || Math.Abs(root.Right - trash.Right - 2) > .75)
                throw new InvalidOperationException("Smoke test: file-card trash icon is not inset two pixels from the upper-right corner.");
            if (Math.Abs((handle.Top + handle.Bottom - root.Top - root.Bottom) / 2) > .75 ||
                (handle.Top + handle.Bottom) / 2 <= (trash.Top + trash.Bottom) / 2 + 6 || handle.Top < trash.Bottom - .75)
                throw new InvalidOperationException("Smoke test: file-card reorder grip is not centered below and separated from the trash icon.");
        }
    }
}

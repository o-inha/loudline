using Multiloud.App.Controls;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Multiloud.App;

public partial class MainWindow
{
    private static TextBox CreateCompactDialogNumberBox(FrameworkElement host, string name, double value)
    {
        var box = new TextBox
        {
            Name = name, Text = value.ToString("R", CultureInfo.InvariantCulture),
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0)
        };
        box.SetResourceReference(StyleProperty, typeof(TextBox));
        // Reserve three value glyphs plus the field's own chrome, rather than a full-width row.
        void FitWidth(Control typography)
        {
            var face = new Typeface(typography.FontFamily, typography.FontStyle, typography.FontWeight, typography.FontStretch);
            var text = new FormattedText("999", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face, typography.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(typography).PixelsPerDip);
            box.Width = Math.Ceiling(text.WidthIncludingTrailingWhitespace + box.Padding.Left + box.Padding.Right +
                box.BorderThickness.Left + box.BorderThickness.Right + 2);
        }
        FitWidth(box);
        // Inherited typography, per-monitor DPI and the native TextBox border
        // are final only after the field has joined its window and loaded.
        box.Loaded += (_, _) => FitWidth(box);
        return box;
    }

    private static bool ResolveSaveConfirmation(UnsavedProjectChoice choice, Func<bool> save) =>
        choice == UnsavedProjectChoice.Discard || choice == UnsavedProjectChoice.Save && save();
}

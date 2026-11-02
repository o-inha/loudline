using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PathShape = System.Windows.Shapes.Path;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyTrashIconAppearanceSmoke(Button waveformRemove)
    {
        var suppliedGeometry = (Geometry)FindResource("TrashIconGeometry");
        var card = new ListBoxItem { Content = "Track", Style = (Style)FindResource(typeof(ListBoxItem)) };
        card.ApplyTemplate(); card.Measure(new Size(240, 80)); card.Arrange(new Rect(0, 0, 240, 80));
        if (waveformRemove.Content is not PathShape waveformIcon ||
            card.Template.FindName("TrackRemoveIcon", card) is not PathShape fileIcon)
            throw new InvalidOperationException("Smoke test: track remove actions lost their supplied vector icon.");
        foreach (var icon in new[] { waveformIcon, fileIcon })
        {
            var drawn = icon.RenderedGeometry.Bounds;
            if (!ReferenceEquals(icon.Data, suppliedGeometry) || icon.Width != 14 || icon.Height != 14 ||
                icon.Stretch != Stretch.Uniform || icon.StrokeThickness != 1.3 ||
                icon.StrokeStartLineCap != PenLineCap.Round || icon.StrokeEndLineCap != PenLineCap.Round ||
                icon.StrokeLineJoin != PenLineJoin.Round || drawn.IsEmpty ||
                Math.Abs(drawn.Width / drawn.Height - suppliedGeometry.Bounds.Width / suppliedGeometry.Bounds.Height) > .001)
                throw new InvalidOperationException("Smoke test: file-list and waveform trash icons are inconsistent or stretched.");
        }
    }
}

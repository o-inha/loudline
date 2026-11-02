using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyMenuLayoutSmokeAsync()
    {
        // Native templates must place text at their own padding, without the
        // unused icon column normally supplied by the Windows menu template.
        var menu = new ContextMenu { MinWidth = 280 };
        var plain = new MenuItem { Header = "Export this file…", InputGestureText = "Ctrl+E" };
        var checkable = new MenuItem { Header = "Icons only", IsCheckable = true };
        var parent = new MenuItem { Header = "Button style" };
        parent.Items.Add(new MenuItem { Header = "Default" });
        menu.Items.Add(plain); menu.Items.Add(checkable); menu.Items.Add(parent);
        menu.PlacementTarget = this; menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = 12; menu.VerticalOffset = 32;
        menu.IsOpen = true;
        try
        {
            await SettleMenu();
            foreach (var item in new[] { plain, checkable, parent })
            {
                VerifyHeader(item);
                if (item.Template.FindName("MenuIconSlot", item) is not FrameworkElement { Visibility: Visibility.Collapsed })
                    throw new InvalidOperationException("Smoke test: an unchecked menu item reserved an empty icon slot.");
            }

            checkable.IsChecked = true;
            await SettleMenu();
            VerifyHeader(checkable);
            var header = (FrameworkElement)checkable.Template.FindName("PART_Header", checkable);
            var checkSlot = (FrameworkElement)checkable.Template.FindName("MenuIconSlot", checkable);
            var check = (FrameworkElement)checkable.Template.FindName("MenuCheck", checkable);
            var headerBounds = header.TransformToAncestor(checkable).TransformBounds(new Rect(header.RenderSize));
            var checkBounds = checkSlot.TransformToAncestor(checkable).TransformBounds(new Rect(checkSlot.RenderSize));
            if (!checkable.IsChecked || checkSlot.Visibility != Visibility.Visible || check.Visibility != Visibility.Visible ||
                checkBounds.Width <= 0 || checkBounds.Left < headerBounds.Right - .5)
                throw new InvalidOperationException($"Smoke test: menu check state did not stay visible to the right of its header " +
                    $"(role={checkable.Role}, checked={checkable.IsChecked}, slot={checkSlot.Visibility}, check={check.Visibility}, " +
                    $"slotBounds={checkBounds}, headerBounds={headerBounds}, menu={menu.RenderSize}).");
            checkable.IsChecked = false;
            await SettleMenu();
            if (((FrameworkElement)checkable.Template.FindName("MenuIconSlot", checkable)).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Smoke test: clearing a menu check left its reserved space visible.");
        }
        finally { menu.IsOpen = false; }

        async Task SettleMenu() => await Dispatcher.InvokeAsync(() =>
        {
            // Visibility changes invalidate a child first. Let native layout
            // propagate to the popup instead of reusing a cached row measure.
            menu.InvalidateMeasure(); menu.UpdateLayout();
        }, DispatcherPriority.ContextIdle);

        static void VerifyHeader(MenuItem item)
        {
            if (item.Template.FindName("MenuSurface", item) is not Border surface ||
                item.Template.FindName("PART_Header", item) is not FrameworkElement header)
                throw new InvalidOperationException("Smoke test: custom menu template is missing.");
            var bounds = header.TransformToAncestor(surface).TransformBounds(new Rect(header.RenderSize));
            if (Math.Abs(bounds.Left - surface.Padding.Left) > .5)
                throw new InvalidOperationException($"Smoke test: menu text retained an empty left icon gutter (left={bounds.Left}, padding={surface.Padding.Left}).");
        }
    }
}

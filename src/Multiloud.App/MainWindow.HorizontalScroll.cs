using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Multiloud.App.Services;

namespace Multiloud.App;

public partial class MainWindow
{
    private HwndSource? _horizontalScrollSource;
    private readonly ConditionalWeakTable<ScrollViewer, HorizontalScrollPlan> _horizontalScrollPlans = new();
    private uint _horizontalScrollCharacters = 3;
    private long _horizontalTimelineMessages, _horizontalViewerMessages;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    private void AttachHorizontalScrolling()
    {
        if (_horizontalScrollSource != null) return;
        _horizontalScrollSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _horizontalScrollSource?.AddHook(HorizontalScrollMessage);
        ReadHorizontalScrollPreference();
    }

    private void DetachHorizontalScrolling()
    {
        try { _horizontalScrollSource?.RemoveHook(HorizontalScrollMessage); }
        catch (ObjectDisposedException) { }
        _horizontalScrollSource = null;
    }

    private void ReadHorizontalScrollPreference()
    {
        if (SystemParametersInfo(0x006C, 0, out var characters, 0)) _horizontalScrollCharacters = characters;
    }

    private nint HorizontalScrollMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x001A) { ReadHorizontalScrollPreference(); return 0; }
        if (!NativeWheelInput.TryDecode(message, wParam, lParam, out var wheel)) return 0;
        var point = PointFromScreen(new Point(wheel.ScreenX, wheel.ScreenY));
        if (!new Rect(RenderSize).Contains(point)) return 0;
        if (RouteHorizontalScroll(InputHitTest(point), point, wheel.Delta)) handled = true;
        return 0;
    }

    private bool RouteHorizontalScroll(IInputElement? target, Point windowPoint, double delta)
    {
        for (var node = target as DependencyObject; node != null; node = InputParent(node))
        {
            if (ReferenceEquals(node, Timeline) || ReferenceEquals(node, BackgroundLane))
            {
                var position = TranslatePoint(windowPoint, Timeline);
                if (ReferenceEquals(node, BackgroundLane)) position.Y = Timeline.PlotArea.Top + 1;
                bool moved = Timeline.HandleHorizontalScroll(position, delta);
                if (moved) _horizontalTimelineMessages++;
                return moved;
            }
            if (node is not ScrollViewer viewer || viewer.ScrollableWidth <= 0 || viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            var plan = _horizontalScrollPlans.GetValue(viewer, _ => new HorizontalScrollPlan());
            bool captured = Mouse.Captured is DependencyObject capture && IsDescendantOf(capture, viewer);
            double next = plan.Apply(viewer.HorizontalOffset, viewer.ScrollableWidth, viewer.ViewportWidth, delta,
                _horizontalScrollCharacters, TextElement.GetFontSize(viewer), Environment.TickCount64, captured);
            if (Math.Abs(next - viewer.HorizontalOffset) < .000001) continue;
            viewer.ScrollToHorizontalOffset(next); _horizontalViewerMessages++;
            return true;
        }
        return false;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current != null; current = InputParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static DependencyObject? InputParent(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(node)
    };
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendHorizontalSmokeMessage(nint window, uint message, nint wParam, nint lParam);

    // Run while attached to the visible native window: detached screenshots cannot
    // exercise screen-coordinate conversion, native hooks, or input hit testing.
    internal async Task VerifyHorizontalScrollSmokeAsync()
    {
        if (!IsLoaded || !IsVisible || _horizontalScrollSource == null || _tracks.Count < 2)
            throw new InvalidOperationException("Smoke test: native horizontal scrolling needs a visible window and multiple tracks.");
        double previousWidth = Width, playhead = Timeline.Playhead;
        uint previousCharacters = _horizontalScrollCharacters;
        var previousFocus = Keyboard.FocusedElement;
        int renders = _renderExecutionCount;
        var rendered = _tracks.Select(track => track.Render).ToArray();
        try
        {
            Width = 1100;
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); UpdateResponsiveLayout(); UpdateLayout(); }, DispatcherPriority.Background);
            if (_wideLayout || CompactProcessingBar.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Smoke test: native horizontal toolbar test did not reach compact layout.");
            Timeline.SetHoveredTrack(new Point(), force: true);
            Keyboard.Focus(Timeline);
            Timeline.FitTrack(_tracks[0]);
            Timeline.FlushPendingRendering();
            double duration = Timeline.VisibleDurationSeconds, start = Timeline.ViewStartSeconds;
            double unit = duration * .09 / 120;
            var pointer = Timeline.PointToScreen(new Point(Timeline.ActualWidth * .5, Math.Max(120, Timeline.ActualHeight * .6)));
            long timelineMessages = _horizontalTimelineMessages;
            SendNativeHorizontalSmoke(pointer, 1);
            if (_horizontalTimelineMessages != timelineMessages + 1 || !Timeline.IsNavigationAnimating || Timeline.ViewStartSeconds != start)
                throw new InvalidOperationException("Smoke test: a native +1 horizontal delta was lost or jumped before a render frame.");
            await WaitForHorizontalCameraSmokeAsync();
            RequireHorizontalNear(Timeline.ViewStartSeconds, start + unit, "Native +1 must pan right");
            RequireHorizontalNear(Timeline.VisibleDurationSeconds, duration, "Horizontal input changed zoom");
            SendNativeHorizontalSmoke(pointer, -1);
            await WaitForHorizontalCameraSmokeAsync();
            RequireHorizontalNear(Timeline.ViewStartSeconds, start, "Native -1 must undo a +1 pan");

            // Deliver a high-resolution burst synchronously, before layout or a
            // render event can update the camera. Every sub-detent must survive.
            const int burst = 12;
            for (int index = 0; index < burst; index++) SendNativeHorizontalSmoke(pointer, 1);
            await WaitForHorizontalCameraSmokeAsync();
            RequireHorizontalNear(Timeline.ViewStartSeconds, start + burst * unit, "Tiny horizontal deltas did not accumulate");
            for (int index = 0; index < burst; index++) SendNativeHorizontalSmoke(pointer, -1);
            await WaitForHorizontalCameraSmokeAsync();
            RequireHorizontalNear(Timeline.ViewStartSeconds, start, "Reversed tiny horizontal deltas drifted");
            RequireHorizontalNear(Timeline.VisibleDurationSeconds, duration, "A horizontal burst changed zoom");
            RequireHorizontalNear(Timeline.Playhead, playhead, "Horizontal input sought playback");
            if (_horizontalTimelineMessages != timelineMessages + 2 + burst * 2)
                throw new InvalidOperationException("Smoke test: some native timeline wheel messages bypassed the horizontal hook.");

            var viewer = Descendants<ScrollViewer>(CompactProcessingBar)
                .Single(item => ReferenceEquals(item.Content, CompactProcessingHost));
            if (viewer.ScrollableWidth <= 0)
                throw new InvalidOperationException("Smoke test: compact processing toolbar did not overflow horizontally.");
            // Use a known preference so a CI machine configured to disable wheel
            // scrolling cannot turn this routing regression into a false failure.
            _horizontalScrollCharacters = 3;
            Keyboard.Focus(StrengthSlider);
            await ResetHorizontalViewerSmokeAsync(viewer);
            pointer = StrengthValueText.PointToScreen(new Point(StrengthValueText.ActualWidth * .5, StrengthValueText.ActualHeight * .5));
            long viewerMessages = _horizontalViewerMessages;
            timelineMessages = _horizontalTimelineMessages;
            SendNativeHorizontalSmoke(pointer, 120);
            await WaitForHorizontalOffsetSmokeAsync(viewer, () => viewer.HorizontalOffset > 0);
            double detent = viewer.HorizontalOffset;
            await ResetHorizontalViewerSmokeAsync(viewer);
            for (int index = 0; index < 120; index++) SendNativeHorizontalSmoke(pointer, 1);
            await WaitForHorizontalOffsetSmokeAsync(viewer, () => Math.Abs(viewer.HorizontalOffset - detent) < .001);
            for (int index = 0; index < 120; index++) SendNativeHorizontalSmoke(pointer, -1);
            await WaitForHorizontalOffsetSmokeAsync(viewer, () => viewer.HorizontalOffset < .001);
            if (_horizontalViewerMessages != viewerMessages + 241 || _horizontalTimelineMessages != timelineMessages || Timeline.IsNavigationAnimating)
                throw new InvalidOperationException("Smoke test: toolbar horizontal messages escaped their scroll viewer or lost precision.");
            RequireHorizontalNear(Timeline.ViewStartSeconds, start, "Toolbar scrolling panned the timeline");
            RequireHorizontalNear(Timeline.VisibleDurationSeconds, duration, "Toolbar scrolling zoomed the timeline");
            RequireHorizontalNear(Timeline.Playhead, playhead, "Toolbar scrolling sought playback");
            VerifyHorizontalScrollbarSmoke(viewer, Orientation.Horizontal);
            await VerifyVerticalScrollbarSmokeAsync();
            if (_renderExecutionCount != renders || _tracks.Where((track, index) => !ReferenceEquals(track.Render, rendered[index])).Any())
                throw new InvalidOperationException("Smoke test: horizontal scrolling or scrollbar inspection rebuilt audio.");
        }
        finally
        {
            _horizontalScrollCharacters = previousCharacters;
            Timeline.Playhead = playhead; Timeline.FitAll();
            Width = previousWidth;
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); UpdateResponsiveLayout(); UpdateLayout(); }, DispatcherPriority.Background);
            if (previousFocus is UIElement { IsVisible: true, IsEnabled: true } element) Keyboard.Focus(element);
        }
    }

    private void SendNativeHorizontalSmoke(Point screen, int delta)
    {
        int x = (int)Math.Round(screen.X), y = (int)Math.Round(screen.Y);
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
            throw new InvalidOperationException("Smoke test: native wheel point is outside the signed Win32 coordinate range.");
        nint wParam = unchecked((nint)((long)(ushort)(short)delta << 16));
        nint lParam = unchecked((nint)((uint)(ushort)(short)x | ((uint)(ushort)(short)y << 16)));
        SendHorizontalSmokeMessage(new WindowInteropHelper(this).Handle, NativeWheelInput.HorizontalWheel, wParam, lParam);
    }

    private async Task WaitForHorizontalCameraSmokeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Timeline.IsNavigationAnimating && DateTime.UtcNow < deadline)
        {
            await Task.Delay(16);
            Timeline.FlushPendingRendering();
        }
        if (Timeline.IsNavigationAnimating) throw new InvalidOperationException("Smoke test: native horizontal camera did not settle.");
    }

    private async Task ResetHorizontalViewerSmokeAsync(ScrollViewer viewer)
    {
        viewer.ScrollToHorizontalOffset(0);
        await WaitForHorizontalOffsetSmokeAsync(viewer, () => viewer.HorizontalOffset < .001);
        _horizontalScrollPlans.Remove(viewer);
    }

    private async Task WaitForHorizontalOffsetSmokeAsync(ScrollViewer viewer, Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            await Dispatcher.InvokeAsync(() => viewer.UpdateLayout(), DispatcherPriority.ContextIdle);
            if (completed()) return;
            await Task.Delay(16);
        } while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException($"Smoke test: queued horizontal scroll did not settle (offset {viewer.HorizontalOffset:R}).");
    }

    private static void RequireHorizontalNear(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > .00001)
            throw new InvalidOperationException($"Smoke test: {message}: expected {expected:R}, received {actual:R}.");
    }

    private static void VerifyHorizontalScrollbarSmoke(ScrollViewer viewer, Orientation orientation)
    {
        viewer.ApplyTemplate(); viewer.UpdateLayout();
        string part = orientation == Orientation.Horizontal ? "PART_HorizontalScrollBar" : "PART_VerticalScrollBar";
        if (viewer.Template.FindName(part, viewer) is not ScrollBar bar || bar.Visibility != Visibility.Visible)
            throw new InvalidOperationException($"Smoke test: {orientation} overflow did not expose its scrollbar.");
        bar.ApplyTemplate(); bar.UpdateLayout();
        double thickness = orientation == Orientation.Horizontal ? bar.ActualHeight : bar.ActualWidth;
        if (Math.Abs(thickness - 4) > .01 || bar.Template.FindName("PART_Track", bar) is not Track { Thumb: { } thumb })
            throw new InvalidOperationException($"Smoke test: {orientation} scrollbar thickness was {thickness:R}, expected 4 DIPs.");
        double thumbThickness = orientation == Orientation.Horizontal ? thumb.ActualHeight : thumb.ActualWidth;
        if (Math.Abs(thumbThickness - 4) > .01 || viewer.IsKeyboardFocusWithin && bar.Opacity < .99)
            throw new InvalidOperationException($"Smoke test: {orientation} scrollbar thumb was not a visible 4-DIP target.");
    }

    private async Task VerifyVerticalScrollbarSmokeAsync()
    {
        var selected = FileList.SelectedItem;
        bool syncing = _syncing;
        var temporary = new List<TrackListEntry>();
        _syncing = true;
        try
        {
            for (int index = 0; index < 40; index++)
            {
                var row = new TrackListEntry(new AudioTrack { FilePath = $"scrollbar-{index:00}.wav", Status = "Ready" });
                row.Update(_trackRows.Count); temporary.Add(row); _trackRows.Add(row);
            }
            FileList.Focus();
            await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
            var viewer = Descendants<ScrollViewer>(FileList).First();
            if (viewer.ScrollableHeight <= 0) throw new InvalidOperationException("Smoke test: vertical scrollbar fixture did not overflow.");
            VerifyHorizontalScrollbarSmoke(viewer, Orientation.Vertical);
        }
        finally
        {
            foreach (var row in temporary) _trackRows.Remove(row);
            FileList.SelectedItem = selected;
            if (selected != null) FileList.ScrollIntoView(selected);
            await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
            _syncing = syncing;
        }
    }
}

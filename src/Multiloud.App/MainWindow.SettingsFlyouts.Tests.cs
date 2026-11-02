using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifySettingsFlyoutsSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        CloseSettingsFlyout();
        var session = Capture(); var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty, importing = _importing;
        int revision = _revision, executions = _renderExecutionCount;
        var settings = _settings;
        int windows = Application.Current.Windows.Count;
        double ownerLeft = Left, ownerTop = Top;
        async Task Settle() => await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ContextIdle);
        void Unchanged()
        {
            if (!ReferenceEquals(settings, _settings) || _revision != revision || _renderExecutionCount != executions || _dirty != dirty)
                throw new InvalidOperationException("Smoke test: dismissing a draft dropdown changed committed processing.");
        }
        try
        {
            LevelingOptionsButton.BringIntoView(); await Settle();
            // BringIntoView works within the logical viewport. A CI window can
            // be wider than its physical desktop, so place the first gear on
            // the monitor explicitly before asserting an unclamped native popup.
            var monitor = new SettingsSmokeMonitorInfo { Size = Marshal.SizeOf<SettingsSmokeMonitorInfo>() };
            if (!GetSettingsSmokeMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle, 2), ref monitor))
                throw new InvalidOperationException("Smoke test: owner monitor work area could not be read.");
            var work = new Rect(new Point(monitor.Work.Left, monitor.Work.Top), new Point(monitor.Work.Right, monitor.Work.Bottom));
            var draft = CreateLevelingOptionsContent(() => { });
            draft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var dpi = VisualTreeHelper.GetDpi(this);
            double popupWidth = (draft.DesiredSize.Width + 2) * dpi.DpiScaleX;
            bool canFitUnclamped = popupWidth + 24 <= work.Width;
            var target = SettingsSectionAnchor(LevelingOptionsButton);
            double anchorRight = target.PointToScreen(new Point(target.ActualWidth, 0)).X;
            if (canFitUnclamped)
            {
                double wantedRight = Math.Clamp(work.Left + work.Width * .7, work.Left + popupWidth + 12, work.Right - 12);
                Left += (wantedRight - anchorRight) / dpi.DpiScaleX;
                await Settle();
            }
            var editorBounds = AudioLanes.TransformToAncestor(this).TransformBounds(new Rect(AudioLanes.RenderSize));
            LevelingOptionsClick(LevelingOptionsButton, new RoutedEventArgs()); await Settle();
            var content = _settingsFlyoutContent ?? throw new InvalidOperationException("Smoke test: leveling gear did not expose its dropdown.");
            if (_settingsFlyout is not { IsOpen: true, Placement: PlacementMode.Custom, StaysOpen: true } ||
                Application.Current.Windows.Count != windows ||
                AudioLanes.TransformToAncestor(this).TransformBounds(new Rect(AudioLanes.RenderSize)) != editorBounds)
                throw new InvalidOperationException("Smoke test: a settings dropdown opened a modal window or reflowed the editor.");
            VerifyFlyoutRightEdgeSmoke(requireUnclamped: canFitUnclamped);
            ((TextBox)content.FindName("MaxLevelingBox")).Text = "12";
            LevelingOptionsClick(LevelingOptionsButton, new RoutedEventArgs());
            if (_settingsFlyout != null) throw new InvalidOperationException("Smoke test: clicking the same gear reopened its dropdown instead of closing it.");
            Unchanged();

            LevelingOptionsClick(LevelingOptionsButton, new RoutedEventArgs()); await Settle();
            content = _settingsFlyoutContent!;
            if (((TextBox)content.FindName("MaxLevelingBox")).Text != settings.MaxLevelingDb.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Smoke test: reopening settings retained an abandoned draft.");
            EscapeFlyoutSmoke(); Unchanged();

            // Busy source work never locks settings. Opening and canceling this
            // draft must neither interrupt decoding nor queue a preview.
            _importing = true;
            LevelingOptionsClick(LevelingOptionsButton, new RoutedEventArgs()); await Settle();
            if (_settingsFlyoutKey != "Leveling") throw new InvalidOperationException("Smoke test: processing blocked the leveling dropdown.");
            _importing = importing;
            MaximizerOptionsButton.BringIntoView(); await Settle();
            MaximizerOptionsClick(MaximizerOptionsButton, new RoutedEventArgs()); await Settle();
            if (_settingsFlyoutKey != "Peak" || Application.Current.Windows.Count != windows)
                throw new InvalidOperationException("Smoke test: switching gears did not replace the floating settings content.");
            VerifyFlyoutRightEdgeSmoke();
            Timeline.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = Timeline });
            if (_settingsFlyout != null) throw new InvalidOperationException("Smoke test: clicking outside failed to dismiss a settings draft.");
            Unchanged();

            _undo.Clear(); _redo.Clear();
            MaximizerOptionsClick(MaximizerOptionsButton, new RoutedEventArgs()); await Settle();
            content = _settingsFlyoutContent!;
            double next = settings.MaximizerCharacter < .7 ? .8 : .2;
            ((Slider)content.FindName("MaximizerCharacterSlider")).Value = next;
            ((Button)content.FindName("MaximizerApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_settingsFlyout != null || _settings.MaximizerCharacter != next || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: dropdown Apply did not commit exactly one settings edit and close.");
            await WaitForSmokePreviewAsync();
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            if (_settings.MaximizerCharacter != settings.MaximizerCharacter)
                throw new InvalidOperationException("Smoke test: applied dropdown settings were not undoable.");
        }
        finally
        {
            _importing = importing; CloseSettingsFlyout();
            Left = ownerLeft; Top = ownerTop;
            await Settle();
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SettingsSmokeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SettingsSmokeMonitorInfo
    {
        public int Size;
        public SettingsSmokeRect Monitor, Work;
        public int Flags;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSettingsSmokeMonitorInfo(IntPtr monitor, ref SettingsSmokeMonitorInfo info);

    private void VerifyFlyoutRightEdgeSmoke(bool requireUnclamped = false)
    {
        if (_settingsFlyout is not { IsOpen: true, PlacementTarget: FrameworkElement target, Child: FrameworkElement surface })
            throw new InvalidOperationException("Smoke test: settings flyout is not open.");
        var candidates = SettingsFlyoutPlacement(surface.RenderSize, target.RenderSize, default);
        if (candidates[0].Point.X != target.ActualWidth - surface.ActualWidth ||
            candidates[0].Point.Y != target.ActualHeight + 3)
            throw new InvalidOperationException("Smoke test: dropdown placement did not anchor its measured right edge below the section.");
        var hwnd = (PresentationSource.FromVisual(surface) as HwndSource)?.Handle ?? IntPtr.Zero;
        var info = new SettingsSmokeMonitorInfo { Size = Marshal.SizeOf<SettingsSmokeMonitorInfo>() };
        if (hwnd == IntPtr.Zero || !GetSettingsSmokeMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
            throw new InvalidOperationException("Smoke test: dropdown monitor work area could not be read.");
        var workArea = new Rect(new Point(info.Work.Left, info.Work.Top), new Point(info.Work.Right, info.Work.Bottom));
        var actual = new Rect(surface.PointToScreen(default), surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight)));
        double anchorRight = target.PointToScreen(new Point(target.ActualWidth, 0)).X;
        // Native CI's desktop can be narrower than this editor's minimum size.
        // WPF correctly clamps an off-screen target's popup back into the
        // monitor work area. Verify that placement rather than demanding that
        // its menu follow an invisible gear outside the physical desktop.
        double expectedRight = Math.Clamp(anchorRight, workArea.Left + actual.Width, workArea.Right);
        if (requireUnclamped && Math.Abs(expectedRight - anchorRight) > 2)
            throw new InvalidOperationException("Smoke test: the visible leveling gear did not exercise unclamped dropdown alignment.");
        if (Math.Abs(actual.Right - expectedRight) > 2 || actual.Left < workArea.Left - 2 ||
            actual.Right > workArea.Right + 2 || actual.Top < workArea.Top - 2 || actual.Bottom > workArea.Bottom + 2)
            throw new InvalidOperationException($"Smoke test: dropdown alignment or screen clamping is wrong: " +
                $"actual={actual}, expectedRight={expectedRight}, anchorRight={anchorRight}, workArea={workArea}.");
    }
}

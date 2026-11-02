using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Multiloud.App;
public static class ThemeService
{
    private static BitmapFrame? _darkIcon, _lightIcon;
    private static BitmapFrame AppIcon(bool dark)
    {
        ref BitmapFrame? cached = ref (dark ? ref _darkIcon : ref _lightIcon);
        if (cached != null) return cached;
        cached = BitmapFrame.Create(new Uri($"pack://application:,,,/Assets/{(dark ? "AppIconDark.ico" : "AppIcon.ico")}", UriKind.Absolute));
        cached.Freeze(); return cached;
    }
    public static string Mode { get; private set; } = "System";
    public static void Apply(string mode)
    {
        Mode = mode;
        bool dark = mode == "Dark" || (mode == "System" && ReadDark());
        var colors = new Dictionary<string, string> {
            ["BackgroundBrush"] = dark ? "#161A20" : "#F4F5F7",
            ["PanelBrush"] = dark ? "#20252D" : "#FFFFFF",
            ["ButtonBrush"] = dark ? "#2A313B" : "#E9EDF2",
            ["BorderBrush"] = dark ? "#3A4350" : "#CFD5DF",
            ["TextBrush"] = dark ? "#E8EDF3" : "#1C2633",
            ["MutedBrush"] = dark ? "#97A5B6" : "#5C6B7D",
            ["MissingBackgroundBrush"] = dark ? "#352329" : "#F4E6E8",
            ["MissingTextBrush"] = dark ? "#DA9AA3" : "#8E3C47",
            ["MissingBorderBrush"] = dark ? "#6D3B45" : "#C48F99",
            ["AccentBrush"] = dark ? "#5AD4B1" : "#087F66",
            ["AccentDarkBrush"] = dark ? "#245E53" : "#B9EBDD",
            ["SelectionBrush"] = dark ? "#263E43" : "#D8EFE9",
            // All button variants, including text toggles and segmented views,
            // share hover/active colors. An off toggle remains visibly enabled.
            ["ButtonHoverBrush"] = dark ? "#263E43" : "#D8EFE9",
            ["ButtonActiveBrush"] = dark ? "#245E53" : "#B9EBDD",
            ["ButtonActiveHoverBrush"] = dark ? "#2C6D60" : "#A5DFD0",
            ["ButtonOffBrush"] = dark ? "#252B34" : "#E5E8ED",
            ["ButtonOffTextBrush"] = dark ? "#97A5B6" : "#5C6B7D",
            ["ButtonActiveTextBrush"] = dark ? "#E8EDF3" : "#1C2633",
            // Monitor-only A/B is intentionally distinct from processing toggles.
            ["MonitorActiveBrush"] = dark ? "#6C5017" : "#F4D77C",
            ["MonitorActiveHoverBrush"] = dark ? "#806021" : "#E9CA6A",
            ["MonitorActiveTextBrush"] = dark ? "#FFE3A4" : "#654400",
            ["ScrollThumbBrush"] = dark ? "#8A596675" : "#8A7D8998",
            ["ScrollThumbHoverBrush"] = dark ? "#CC718093" : "#CC68798C",
            ["ScrollThumbActiveBrush"] = dark ? "#E58B99AA" : "#E54E6378"
        };
        foreach (var pair in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            brush.Freeze(); Application.Current.Resources[pair.Key] = brush;
        }
        foreach (Window window in Application.Current.Windows)
        {
            window.Icon = AppIcon(dark);
            int value = dark ? 1 : 0;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
            window.InvalidateVisual();
        }
    }
    private static bool ReadDark()
    {
        try { return (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
        catch { return true; }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

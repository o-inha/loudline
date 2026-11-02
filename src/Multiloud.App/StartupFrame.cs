using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Multiloud.App.Controls;

namespace Multiloud.App;

/// <summary>The real app window's lightweight first content, before the editor is built.</summary>
internal static class StartupFrame
{
    internal static void Prepare(Window window)
    {
        bool dark;
        try { dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
        catch { dark = true; }
        window.Title = "Loudline";
        window.Width = 1440; window.Height = 940; window.MinWidth = 1100; window.MinHeight = 740;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.UseLayoutRounding = true;
        EventHandler? initialized = null;
        initialized = (_, _) =>
        {
            window.SourceInitialized -= initialized;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref value, sizeof(int));
        };
        window.SourceInitialized += initialized;
        window.Background = new SolidColorBrush(dark ? Color.FromRgb(22, 26, 32) : Color.FromRgb(244, 245, 247));
        window.Foreground = new SolidColorBrush(dark ? Color.FromRgb(232, 237, 243) : Color.FromRgb(28, 38, 51));
        var content = new Grid();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new ProcessingSpinner
        {
            Width = 24, Height = 24, IsActive = true, Margin = new Thickness(0, 0, 12, 0),
            Foreground = new SolidColorBrush(dark ? Color.FromRgb(90, 212, 177) : Color.FromRgb(8, 127, 102))
        });
        row.Children.Add(new TextBlock { Text = "Loading…", FontSize = 16, Foreground = window.Foreground, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(row); window.Content = content;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

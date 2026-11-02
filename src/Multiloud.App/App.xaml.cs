using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Multiloud.App;
public partial class App : Application
{
    public static bool SmokeMode { get; private set; }
    internal static bool StartupFrameRendered { get; private set; }
    internal static bool EditorInitializedAfterFrame { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        long managedStartupTimestamp = Stopwatch.GetTimestamp();
        base.OnStartup(e);
        SmokeMode = e.Args.Contains("--smoke-test");
        bool probeRequested = e.Args.Contains("--startup-probe");
        DispatcherUnhandledException += (_, args) =>
        {
            LogException(args.Exception);
            if (probeRequested) { args.Handled = true; Shutdown(1); return; }
            if (SmokeMode) { Shutdown(1); return; }
            MessageBox.Show(args.Exception.Message, "Loudline", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainWindow window;
        try
        {
            var probe = StartupProbe.FromArguments(e.Args, managedStartupTimestamp);
            window = new MainWindow(deferEditor: true);
            MainWindow = window;
            var firstPaint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler rendered = (_, _) =>
            {
                long frameTimestamp = Stopwatch.GetTimestamp();
                StartupFrameRendered = true;
                probe?.RecordFirstFrame(window, frameTimestamp);
                firstPaint.TrySetResult(true);
            };
            EventHandler closed = (_, _) => firstPaint.TrySetResult(false);
            window.ContentRendered += rendered;
            window.Closed += closed;
            window.Show();
            bool painted = await firstPaint.Task;
            window.ContentRendered -= rendered; window.Closed -= closed;
            if (!painted || !window.IsVisible) return;
            if (probe != null) { Shutdown(0); return; }
            // Give the compositor the lightweight frame before constructing the
            // editor on its required UI thread. The same HWND/taskbar entry stays.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            if (!window.IsVisible) return;
            if (SmokeMode)
            {
                int screenshotIndex = Array.IndexOf(e.Args, "--screenshot");
                if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
                    CaptureStartupFrame(window, e.Args[screenshotIndex + 1]);
            }
            ThemeService.Apply("System");
            EditorInitializedAfterFrame = StartupFrameRendered;
            window.InitializeEditor();
        }
        catch (Exception error)
        {
            LogException(error);
            if (!SmokeMode && !probeRequested) MessageBox.Show(error.Message, "Could not start Loudline", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        if (SmokeMode)
        {
            try
            {
                var index = Array.IndexOf(e.Args, "--screenshot");
                await window.RunSmokeTestAsync(index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null);
                Shutdown(0);
            }
            catch (Exception ex) { LogException(ex); Shutdown(1); }
        }
    }
    private static void CaptureStartupFrame(Window window, string screenshot)
    {
        string path = Path.GetFullPath(screenshot);
        path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-startup-frame.png");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    internal static void LogException(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loudline");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last-error.txt"), $"Loudline {AppVersion.Display}\n{ex}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

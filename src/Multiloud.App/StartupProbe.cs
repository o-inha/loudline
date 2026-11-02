using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace Multiloud.App;

/// <summary>
/// Development-only measurement of the real early frame. Windows Stopwatch
/// timestamps share the system QPC clock with the external launch harness.
/// </summary>
internal sealed class StartupProbe
{
    private readonly string _path;
    private readonly long _managedStartupTimestamp;
    private int _recorded;

    private StartupProbe(string path, long managedStartupTimestamp)
    {
        _path = Path.GetFullPath(path);
        if (File.Exists(_path) || Directory.Exists(_path))
            throw new IOException("The startup probe output already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(_path)))
            throw new DirectoryNotFoundException("The startup probe output folder does not exist.");
        _managedStartupTimestamp = managedStartupTimestamp;
    }

    internal static StartupProbe? FromArguments(IReadOnlyList<string> args, long managedStartupTimestamp)
    {
        int index = -1;
        for (int item = 0; item < args.Count; item++)
        {
            if (!string.Equals(args[item], "--startup-probe", StringComparison.Ordinal)) continue;
            if (index >= 0) throw new ArgumentException("Specify only one startup probe output.");
            index = item;
        }
        if (index < 0) return null;
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--startup-probe requires an output JSON path.");
        return new StartupProbe(args[index + 1], managedStartupTimestamp);
    }

    internal void RecordFirstFrame(Window window, long firstFrameTimestamp)
    {
        if (Interlocked.CompareExchange(ref _recorded, 1, 0) != 0) return;
        var report = new
        {
            schemaVersion = 1,
            frameEvent = "MainWindow.ContentRendered",
            firstFrameTimestamp,
            frequency = Stopwatch.Frequency,
            isHighResolution = Stopwatch.IsHighResolution,
            managedStartupTimestamp = _managedStartupTimestamp,
            processId = Environment.ProcessId,
            executablePath = Environment.ProcessPath,
            version = AppVersion.Number,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            editorInitialized = App.EditorInitializedAfterFrame,
            frameWidth = window.ActualWidth,
            frameHeight = window.ActualHeight,
            measurement = "Early Loading frame ContentRendered event; not a physical presentation or virtual desktop placement measurement."
        };
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, _path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

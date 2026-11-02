using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private ProjectExportOptions _exportOptions = new();

    private ProjectExportOptions CaptureExportOptions() => new()
    {
        AsOneFile = _exportOptions.AsOneFile,
        UseCustomBoundaries = _exportOptions.UseCustomBoundaries,
        BoundaryFadeMs = _exportOptions.BoundaryFadeMs
    };

    private void LoadExportOptions(ProjectExportOptions? options)
    {
        _exportOptions = options == null ? new() : new()
        {
            AsOneFile = options.AsOneFile,
            UseCustomBoundaries = options.UseCustomBoundaries,
            BoundaryFadeMs = options.BoundaryFadeMs
        };
    }

    private double[] ExportMarkerPositions()
    {
        double duration = _tracks.Sum(track => track.Duration);
        return _customTrackBoundaries.Where(time => double.IsFinite(time) && time > 0 && time < duration)
            .Order().Distinct().ToArray();
    }

    private void ExportOptionsClick(object sender, RoutedEventArgs e)
    {
        if (_exporting || sender is not FrameworkElement anchor) return;
        FinishStrengthGesture(false);
        ToggleSettingsFlyout("export", anchor, CreateExportOptionsContent);
    }

    private FrameworkElement CreateExportOptionsContent(Action close)
    {
        var panel = new StackPanel { Width = 328, Margin = new Thickness(16) };
        NameScope.SetNameScope(panel, new NameScope());
        void Register(FrameworkElement element) { if (element.Name.Length > 0) panel.RegisterName(element.Name, element); }
        RadioButton Radio(string name, string text, string group) => new()
        {
            Name = name, Content = text, GroupName = group, Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Style = (Style)FindResource("ViewSegment")
        };
        FrameworkElement Group(RadioButton first, RadioButton last, string name)
        {
            first.Tag = "First"; last.Tag = "Last";
            var group = new Grid { Name = name, Margin = new Thickness(0, 0, 0, 12) };
            group.ColumnDefinitions.Add(new ColumnDefinition());
            group.ColumnDefinitions.Add(new ColumnDefinition());
            group.Children.Add(first); Grid.SetColumn(last, 1); group.Children.Add(last);
            Register(first); Register(last); Register(group);
            return group;
        }
        var pcm24 = Radio("ExportPcm24Radio", "24-bit PCM", "LoudlineExportFormat");
        var float32Radio = Radio("ExportFloat32Radio", "32-bit float", "LoudlineExportFormat");
        pcm24.IsChecked = !_floatExport; float32Radio.IsChecked = _floatExport;
        panel.Children.Add(Group(pcm24, float32Radio, "ExportFormatGroup"));

        var individual = Radio("ExportIndividualFilesRadio", "Individual files", "LoudlineExportLayout");
        var oneFile = Radio("ExportOneFileRadio", "One file", "LoudlineExportLayout");
        individual.IsChecked = !_exportOptions.AsOneFile; oneFile.IsChecked = _exportOptions.AsOneFile;
        panel.Children.Add(Group(individual, oneFile, "ExportLayoutGroup"));

        var original = Radio("ExportOriginalBoundariesRadio", "File boundaries", "LoudlineExportBoundaries");
        var custom = Radio("ExportCustomBoundariesRadio", "Custom boundaries", "LoudlineExportBoundaries");
        bool hasMarkers = ExportMarkerPositions().Length > 0;
        custom.IsChecked = hasMarkers && _exportOptions.UseCustomBoundaries;
        original.IsChecked = custom.IsChecked != true;
        var boundaryPanel = new StackPanel { Name = "ExportIndividualOptionsPanel" };
        Register(boundaryPanel);
        boundaryPanel.Children.Add(Group(original, custom, "ExportBoundariesGroup"));
        var fadeRow = new Grid { Name = "ExportBoundaryFadeRow", Margin = new Thickness(0, 0, 0, 4) };
        Register(fadeRow);
        fadeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fadeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fadeRow.Children.Add(new TextBlock { Text = "Boundary fade · ms", VerticalAlignment = VerticalAlignment.Center });
        var fade = CreateCompactDialogNumberBox(panel, "BoundaryFadeBox", _exportOptions.BoundaryFadeMs);
        Register(fade); Grid.SetColumn(fade, 1); fadeRow.Children.Add(fade);
        boundaryPanel.Children.Add(fadeRow); panel.Children.Add(boundaryPanel);
        void UpdateOptions()
        {
            boundaryPanel.Visibility = individual.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            custom.IsEnabled = hasMarkers;
            fadeRow.IsEnabled = custom.IsChecked == true && hasMarkers;
        }
        foreach (var radio in new[] { individual, oneFile, custom, original }) radio.Checked += (_, _) => UpdateOptions();
        UpdateOptions();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Name = "ExportOptionsCancelButton", Content = "Cancel", MinWidth = 76, Margin = new Thickness(0, 0, 6, 0) };
        var apply = new Button { Name = "ExportOptionsApplyButton", Content = "Apply", MinWidth = 76, IsDefault = true, Margin = new Thickness(0) };
        Register(cancel); Register(apply); cancel.Click += (_, _) => close();
        apply.Click += (_, _) =>
        {
            double milliseconds = _exportOptions.BoundaryFadeMs;
            bool validFade = double.TryParse(fade.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double draftFade) &&
                double.IsFinite(draftFade) && draftFade is >= 0 and <= 1000;
            if (validFade) milliseconds = draftFade;
            else if (individual.IsChecked == true && custom.IsChecked == true && hasMarkers)
            { fade.Focus(); fade.SelectAll(); return; }
            var next = new ProjectExportOptions
            {
                AsOneFile = oneFile.IsChecked == true,
                // When no markers exist the UI necessarily selects Original.
                // Retain the preference so the first authored marker selects
                // the default Custom layout, unless Original was chosen explicitly.
                UseCustomBoundaries = hasMarkers ? custom.IsChecked == true : _exportOptions.UseCustomBoundaries,
                BoundaryFadeMs = milliseconds
            };
            bool float32 = float32Radio.IsChecked == true;
            if (next.AsOneFile != _exportOptions.AsOneFile || next.UseCustomBoundaries != _exportOptions.UseCustomBoundaries ||
                next.BoundaryFadeMs != _exportOptions.BoundaryFadeMs || float32 != _floatExport)
            {
                PushExportUndo();
                LoadExportOptions(next); _floatExport = float32; UpdateExportFormatChecks(); _dirty = true;
            }
            close();
        };
        fade.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
        actions.Children.Add(cancel); actions.Children.Add(apply); panel.Children.Add(actions);
        return panel;
    }

    private bool AlbumExportReady => !_closed && !_importing && !_exporting && _tracks.Count > 0 &&
        _renderedRevision == _revision && _tracks.All(track => CanExportTrack(track));

    private bool CheckAlbumExportReady()
    {
        if (_tracks.Count == 0 || _importing || _exporting) return false;
        if (_tracks.Any(track => track.WorkState == TrackWorkState.Missing))
        { StatusText.Text = "Relink or remove missing files before exporting."; return false; }
        if (!AlbumExportReady)
        { StatusText.Text = "The Result is still updating. Export when it is ready."; return false; }
        return true;
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        FinishStrengthGesture(false); CommitTarget(); CommitCeiling();
        BackgroundGainCommit(this, new RoutedEventArgs());
        if (!CheckAlbumExportReady()) return;
        CloseSettingsFlyout();
        var options = CaptureExportOptions(); bool float32 = _floatExport;
        try
        {
            if (options.AsOneFile)
            {
                var picker = new SaveFileDialog
                {
                    Title = "Export project as one WAV file", DefaultExt = ".wav", AddExtension = true,
                    Filter = float32 ? "32-bit float WAV|*.wav" : "24-bit PCM WAV|*.wav",
                    CheckPathExists = true, OverwritePrompt = false,
                    FileName = ExportProjectName() + " - Loudline.wav"
                };
                if (picker.ShowDialog(this) != true) return;
                await ExportAlbumOneFileAsync(picker.FileName, float32);
            }
            else
            {
                var picker = new OpenFolderDialog { Title = "Export processed WAV files to folder" };
                if (picker.ShowDialog(this) != true) return;
                await ExportAlbumToFolderAsync(picker.FolderName, float32, options.UseCustomBoundaries, options.BoundaryFadeMs);
            }
        }
        catch (OperationCanceledException) { /* Export helper owns its completed/partial-file feedback. */ }
        catch (Exception error) { ShowError("Could not export audio", error); }
    }

    private string ExportProjectName()
    {
        var name = _projectPath == null ? "Album" : Path.GetFileNameWithoutExtension(_projectPath);
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "Album" : name;
    }

    private AudioTrack[] CaptureAlbumExportTracks() => _tracks.Select(track => new AudioTrack
    {
        FilePath = track.FilePath, GeneratedSilenceSeconds = track.GeneratedSilenceSeconds,
        SampleRate = track.Render!.RenderedSampleRate > 0 ? track.Render.RenderedSampleRate : track.SampleRate,
        Channels = track.Render.RenderedChannels > 0 ? track.Render.RenderedChannels : track.Channels,
        Render = track.Render
    }).ToArray();

    private HashSet<string> ExportProtectedSourcePaths() => _tracks.Concat(_backgroundLayer.Clips.Select(clip => clip.Source))
        .SelectMany(ExportSourceLeaves).Where(track => !string.IsNullOrWhiteSpace(track.FilePath))
        .Select(track => Path.GetFullPath(track.FilePath))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<AudioTrack> ExportSourceLeaves(AudioTrack source)
        => source.MergedSources is { Count: > 0 } parts ? parts.SelectMany(part => ExportSourceLeaves(part.Source))
            : source.GeneratedSilenceSeconds.HasValue ? [] : [source];

    private static ExportAudioTimeline ExportTimelineFor(IReadOnlyList<AudioTrack> tracks) => new(tracks.Select(track =>
        new ExportAudioSource(track.Render!.Samples, track.SampleRate, track.Channels, track.Name, track.Render.LiveSource)));

    private static void ValidateExportDestination(string path, IReadOnlySet<string> protectedPaths)
    {
        if (protectedPaths.Contains(Path.GetFullPath(path))) throw new IOException("Export must use a different path from every source file.");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"A file already exists at {path}. Choose a different export name.");
    }

    private async Task<IReadOnlyList<string>> ExportAlbumToFolderAsync(string folder, bool float32, bool useCustomBoundaries,
        double boundaryFadeMs = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AlbumExportReady) throw new InvalidOperationException("The complete project Result is not ready to export.");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The export folder does not exist.");
        if (!double.IsFinite(boundaryFadeMs) || boundaryFadeMs is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(boundaryFadeMs));
        var tracks = CaptureAlbumExportTracks(); var protectedPaths = ExportProtectedSourcePaths();
        var markers = ExportMarkerPositions(); bool custom = useCustomBoundaries && markers.Length > 0;
        var timeline = custom ? ExportTimelineFor(tracks) : null;
        var ranges = timeline == null ? null : ExportBoundaryPlan.Custom(timeline, markers);
        int count = ranges?.Count ?? tracks.Length;
        string project = ExportProjectName();
        var destinations = new List<string>();
        for (int index = 0; index < count; index++)
        {
            string name = $"{index + 1:00} {(custom ? project : Path.GetFileNameWithoutExtension(tracks[index].Name))} - Loudline";
            string destination = Path.Combine(Path.GetFullPath(folder), name + ".wav");
            for (int suffix = 2; File.Exists(destination) || Directory.Exists(destination) || protectedPaths.Contains(destination); suffix++)
                destination = Path.Combine(Path.GetFullPath(folder), name + $" ({suffix}).wav");
            destinations.Add(destination);
        }
        return await RunAlbumExportAsync(destinations, async (index, progress, token) =>
        {
            if (custom) await Task.Run(() => TimelineWavExporter.Export(timeline!, ranges![index], destinations[index], float32, boundaryFadeMs, token, progress), token);
            else await Task.Run(() => AudioFileService.ExportWav(tracks[index], destinations[index], float32, token, progress), token);
        }, ct, ranges != null ? ranges.Select(range => (double)range.FrameCount * timeline!.Channels).ToArray()
            : tracks.Select(ExportSampleWork).ToArray());
    }

    private static double ExportSampleWork(AudioTrack track) => track.Render!.LiveSource is { } live
        ? live.HasBackground
            ? (Math.Round((live.AlbumStartSeconds + live.Duration) * 48000) - Math.Round(live.AlbumStartSeconds * 48000)) * 2
            : (double)live.FrameCount * live.Channels
        : track.Render.Samples.LongLength;

    private async Task ExportAlbumOneFileAsync(string path, bool float32, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AlbumExportReady) throw new InvalidOperationException("The complete project Result is not ready to export.");
        path = Path.GetFullPath(path); ValidateExportDestination(path, ExportProtectedSourcePaths());
        var timeline = ExportTimelineFor(CaptureAlbumExportTracks()); var range = ExportBoundaryPlan.Single(timeline)[0];
        await RunAlbumExportAsync([path], (_, progress, token) => Task.Run(() => TimelineWavExporter.Export(timeline, range, path, float32, 0, token, progress), token), ct, [range.FrameCount * (double)timeline.Channels]);
    }

    private Task<IReadOnlyList<string>> RunAlbumExportAsync(IReadOnlyList<string> destinations,
        Func<int, CancellationToken, Task> write, CancellationToken cancellation)
        => RunAlbumExportAsync(destinations, (index, _, token) => write(index, token), cancellation);

    private async Task<IReadOnlyList<string>> RunAlbumExportAsync(IReadOnlyList<string> destinations,
        Func<int, Action<double>, CancellationToken, Task> write, CancellationToken cancellation,
        IReadOnlyList<double>? work = null)
    {
        if (destinations.Count == 0) return [];
        var weights = destinations.Select((_, index) => work != null && index < work.Count &&
            double.IsFinite(work[index]) && work[index] > 0 ? work[index] : 1).ToArray();
        double sum = weights.Sum(), before = 0;
        bool timelineEnabled = Timeline.IsEnabled, processingEnabled = ProcessingPanel.IsEnabled;
        var completed = new List<string>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancelInterruptedPlayback(); CloseSettingsFlyout();
        _exporting = true; _exportCts = cts; _player.Stop(); PlayButton.Content = "▶";
        ExportButton.IsEnabled = false; Timeline.IsEnabled = false; ProcessingPanel.IsEnabled = false; RefreshBackground();
        try
        {
            BeginExportProgress(destinations.Count);
            for (int index = 0; index < destinations.Count; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                SetBusy($"Exporting {index + 1}/{destinations.Count}: {Path.GetFileName(destinations[index])}…", true);
                var progress = ExportFileProgress(index + 1, destinations.Count, before, weights[index], sum);
                progress(0); FlushExportProgress();
                await write(index, progress, cts.Token); completed.Add(destinations[index]);
                progress(1); FlushExportProgress(); before += weights[index];
            }
            SetIdle($"Exported {completed.Count} WAV file{(completed.Count == 1 ? "" : "s")}");
            return completed;
        }
        catch (OperationCanceledException)
        {
            SetIdle($"Export cancelled. {completed.Count} completed file(s) kept; partial file removed."); throw;
        }
        catch
        {
            SetIdle($"Export stopped after {completed.Count} file(s)."); throw;
        }
        finally
        {
            EndExportProgress();
            if (ReferenceEquals(_exportCts, cts)) _exportCts = null;
            cts.Dispose(); _exporting = false;
            Timeline.IsEnabled = timelineEnabled; ProcessingPanel.IsEnabled = processingEnabled;
            ExportButton.IsEnabled = AlbumExportReady; RefreshBackground();
        }
    }
}

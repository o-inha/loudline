using System.IO;
using System.Windows;
using System.Windows.Controls;
using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyExportOptionsSmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; var selected = Selected; double playhead = Timeline.Playhead;
        string status = StatusText.Text;
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineExportOptions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            foreach (var track in _tracks) RememberActivePreview(track);
            _tracks.Clear(); _renderKeys.Clear(); _backgroundLayer = new();
            _settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            var tracks = await Task.Run(() => Enumerable.Range(0, 2).Select(index =>
            {
                var track = CreateRenderPriorityTrack(11 + index, 1 + index, _settings);
                track.TrimDb = -3 + index;
                track.Render = AudioProcessor.Render(track.Original, track.SampleRate, track.Channels, track.Envelope, track.TrimDb, _settings);
                track.Render.Waveform = WaveformPyramid.Build(track.Render.Samples, track.Channels);
                track.PreviewPeaks = BuildDisplayPeaks(track.Render.Waveform);
                return track;
            }).ToArray());
            _tracks.AddRange(tracks); RestoreProjectEnvelope([], []);
            foreach (var track in tracks) _renderKeys[track.Id] = RenderKey(track, _settings);
            _renderedRevision = _revision;
            LoadCustomBoundaries([]); LoadExportOptions(new()); _floatExport = false;
            SyncControls(); RefreshList(); CompletePreview(_revision);
            await WaitForSmokePreviewAsync();
            // This fixture owns its history until finally restores the session.
            // Leave room for the one-step assertion rather than inheriting a
            // previous fixture's full twelve-entry Undo cap.
            _undo.Clear(); _redo.Clear();
            int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
            var renders = tracks.Select(track => track.Render).ToArray();
            void NoAudioChanges(string action)
            {
                if (_revision != revision || _renderExecutionCount != executions || !tracks.Select(track => track.Render).SequenceEqual(renders))
                    throw new InvalidOperationException("Smoke test: export-only " + action + " changed processing or rendered PCM.");
            }
            int closes = 0;
            var content = CreateExportOptionsContent(() => closes++);
            T Field<T>(FrameworkElement host, string name) where T : FrameworkElement => (T)host.FindName(name);
            RequireExportOptions(!Descendants<TextBlock>(content).Any(text => text.Text == "WAV format") &&
                content is Panel formatPanel && ReferenceEquals(formatPanel.Children[0], Field<Grid>(content, "ExportFormatGroup")),
                "Export settings retained a format title or space above the format choices.");
            RequireExportOptions(Field<RadioButton>(content, "ExportOriginalBoundariesRadio").IsChecked == true &&
                !Field<RadioButton>(content, "ExportCustomBoundariesRadio").IsEnabled &&
                CaptureExportOptions().UseCustomBoundaries && CaptureExportOptions().BoundaryFadeMs == 0,
                "No-marker fallback did not select Original while preserving the default custom preference and gapless fade.");
            RequireExportOptions(Field<RadioButton>(content, "ExportPcm24Radio").IsChecked == true &&
                Field<RadioButton>(content, "ExportPcm24Radio").Style == FindResource("ViewSegment") &&
                Field<RadioButton>(content, "ExportFloat32Radio").Style == FindResource("ViewSegment") &&
                Field<RadioButton>(content, "ExportIndividualFilesRadio").Style == FindResource("ViewSegment") &&
                Field<RadioButton>(content, "ExportOneFileRadio").Style == FindResource("ViewSegment") &&
                Field<RadioButton>(content, "ExportOriginalBoundariesRadio").Style == FindResource("ViewSegment") &&
                Field<RadioButton>(content, "ExportCustomBoundariesRadio").Style == FindResource("ViewSegment"),
                "Export choices did not use the shared connected button groups.");
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double individualHeight = content.DesiredSize.Height;
            Field<RadioButton>(content, "ExportFloat32Radio").IsChecked = true;
            Field<RadioButton>(content, "ExportOneFileRadio").IsChecked = true;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RequireExportOptions(Field<StackPanel>(content, "ExportIndividualOptionsPanel").Visibility == Visibility.Collapsed &&
                content.DesiredSize.Height < individualHeight - 40,
                "One-file mode kept the individual boundary controls or their reserved space.");
            Field<RadioButton>(content, "ExportIndividualFilesRadio").IsChecked = true;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RequireExportOptions(Field<StackPanel>(content, "ExportIndividualOptionsPanel").Visibility == Visibility.Visible &&
                Math.Abs(content.DesiredSize.Height - individualHeight) < .01 &&
                Field<RadioButton>(content, "ExportFloat32Radio").IsChecked == true &&
                Field<RadioButton>(content, "ExportPcm24Radio").IsChecked == false,
                "Restoring individual mode lost the compact controls or the exclusive WAV selection.");
            Field<RadioButton>(content, "ExportOneFileRadio").IsChecked = true;
            Field<Button>(content, "ExportOptionsCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequireExportOptions(closes == 1 && !_floatExport && !_exportOptions.AsOneFile && _undo.Count == history,
                "Cancelling the export draft committed preferences or history.");
            NoAudioChanges("draft cancellation");

            // A hidden/unused draft must not block Apply. Keep the previous
            // valid fade so returning to Custom boundaries remains safe.
            content = CreateExportOptionsContent(() => closes++);
            Field<TextBox>(content, "BoundaryFadeBox").Text = "invalid";
            Field<RadioButton>(content, "ExportOneFileRadio").IsChecked = true;
            Field<Button>(content, "ExportOptionsApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequireExportOptions(_exportOptions.AsOneFile && _exportOptions.BoundaryFadeMs == 0 && _undo.Count == history + 1,
                "An invalid hidden fade blocked One-file Apply or replaced the previous valid fade.");
            UndoClick(this, new RoutedEventArgs());
            RequireExportOptions(!_exportOptions.AsOneFile && _exportOptions.BoundaryFadeMs == 0 && _undo.Count == history,
                "One-file preferences with an unused fade did not undo independently.");
            NoAudioChanges("unused fade preferences");

            LoadCustomBoundaries([.65, 1.65]);
            content = CreateExportOptionsContent(() => closes++);
            RequireExportOptions(Field<RadioButton>(content, "ExportCustomBoundariesRadio").IsEnabled &&
                Field<RadioButton>(content, "ExportCustomBoundariesRadio").IsChecked == true,
                "The first custom marker did not enable the default Custom layout.");
            Field<TextBox>(content, "BoundaryFadeBox").Text = "invalid";
            int beforeInvalidApply = closes;
            Field<Button>(content, "ExportOptionsApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequireExportOptions(closes == beforeInvalidApply && _undo.Count == history && !_exportOptions.AsOneFile,
                "An invalid active boundary fade was committed instead of remaining editable.");
            Field<TextBox>(content, "BoundaryFadeBox").Text = "3";
            Field<RadioButton>(content, "ExportFloat32Radio").IsChecked = true;
            Field<RadioButton>(content, "ExportOneFileRadio").IsChecked = true;
            Field<Button>(content, "ExportOptionsApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequireExportOptions(_exportOptions.AsOneFile && _exportOptions.BoundaryFadeMs == 3 && _floatExport && _undo.Count == history + 1,
                $"Export Apply lost its choices or failed to make one export-only Undo step: " +
                $"oneFile={_exportOptions.AsOneFile}, fade={_exportOptions.BoundaryFadeMs:R}, float={_floatExport}, " +
                $"history={_undo.Count}, expected={history + 1}.");
            NoAudioChanges("preferences Apply");
            UndoClick(this, new RoutedEventArgs());
            RequireExportOptions(!_exportOptions.AsOneFile && _exportOptions.BoundaryFadeMs == 0 && !_floatExport &&
                CaptureCustomBoundaries().SequenceEqual(new[] { .65, 1.65 }), "Export-only Undo lost markers or changed unrelated choices.");
            NoAudioChanges("preferences Undo");

            var expectedTimeline = ExportTimelineFor(CaptureAlbumExportTracks());
            var expected = new float[checked((int)expectedTimeline.FrameCount * expectedTimeline.Channels)];
            RequireExportOptions(expectedTimeline.Read(0, expected, 0, (int)expectedTimeline.FrameCount) == expectedTimeline.FrameCount,
                "The complete project snapshot could not be read.");
            string whole = Path.Combine(directory, "whole.wav");
            await ExportAlbumOneFileAsync(whole, float32: true);
            RequireExportOptions(ReadExportOptionsWav(whole, expectedTimeline.SampleRate, expectedTimeline.Channels).SequenceEqual(expected),
                "One-file export changed the final Result or used monitoring gains.");
            string customFolder = Path.Combine(directory, "custom"); Directory.CreateDirectory(customFolder);
            var customPaths = await ExportAlbumToFolderAsync(customFolder, float32: true, useCustomBoundaries: true);
            RequireExportOptions(customPaths.Count == 3, "Custom exports used the two original source borders instead of the two authored markers.");
            var joined = customPaths.SelectMany(path => ReadExportOptionsWav(path, expectedTimeline.SampleRate, expectedTimeline.Channels)).ToArray();
            RequireExportOptions(joined.SequenceEqual(expected), "Joining zero-fade custom files did not reconstruct the exact continuous Result.");
            string originalFolder = Path.Combine(directory, "original"); Directory.CreateDirectory(originalFolder);
            var originalPaths = await ExportAlbumToFolderAsync(originalFolder, float32: true, useCustomBoundaries: false, boundaryFadeMs: 3);
            RequireExportOptions(originalPaths.Count == 2, "Original-boundary export used custom markers.");
            for (int index = 0; index < tracks.Length; index++)
                RequireExportOptions(ReadExportOptionsWav(originalPaths[index], tracks[index].SampleRate, tracks[index].Channels)
                    .SequenceEqual(tracks[index].Render!.Samples), "Original-boundary export changed its native format or applied a custom boundary fade.");
            string fadedFolder = Path.Combine(directory, "faded"); Directory.CreateDirectory(fadedFolder);
            var fadedPaths = await ExportAlbumToFolderAsync(fadedFolder, float32: true, useCustomBoundaries: true, boundaryFadeMs: 3);
            var faded = fadedPaths.SelectMany(path => ReadExportOptionsWav(path, expectedTimeline.SampleRate, expectedTimeline.Channels)).ToArray();
            RequireExportOptions(!faded.SequenceEqual(expected) && faded.Length == expected.Length && faded[0] == expected[0] && faded[^1] == expected[^1],
                "Optional boundary fades were absent, changed duration, or faded the release's natural endpoints.");
            NoAudioChanges("audio export");

            string missingSource = Path.Combine(directory, "unavailable-source.wav");
            string oldPath = tracks[0].FilePath; tracks[0].FilePath = missingSource;
            try
            {
                await ExpectTrackExportFailureAsync<IOException>(() => ExportAlbumOneFileAsync(missingSource, true));
                RequireExportOptions(!File.Exists(missingSource), "A custom/one-file export replaced an unavailable source path.");
            }
            finally { tracks[0].FilePath = oldPath; }
            await ExpectTrackExportFailureAsync<IOException>(() => ExportAlbumOneFileAsync(whole, true));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                string cancelledPath = Path.Combine(directory, "cancelled.wav");
                await ExpectTrackExportFailureAsync<OperationCanceledException>(() => ExportAlbumOneFileAsync(cancelledPath, true, cancelled.Token));
                RequireExportOptions(!File.Exists(cancelledPath) && Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories).Length == 0,
                    "Cancelled export left a partial destination.");
            }
            tracks[0].WorkState = TrackWorkState.Unprocessed;
            await ExpectTrackExportFailureAsync<InvalidOperationException>(() => ExportAlbumOneFileAsync(Path.Combine(directory, "unfinished.wav"), true));
            tracks[0].WorkState = TrackWorkState.Idle;
            RequireExportOptions(ExportButton.IsEnabled && Timeline.IsEnabled && ProcessingPanel.IsEnabled,
                "Export completion failed to restore the ready editor.");
            NoAudioChanges("readiness and source protection");
            if (screenshot != null)
            {
                ExportOptionsClick(ExportOptionsButton, new RoutedEventArgs());
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (_settingsFlyoutContent is { } exportContent && _settingsFlyout?.Child is FrameworkElement exportSurface)
                {
                    RequireExportOptions(ReferenceEquals(_settingsFlyout.PlacementTarget, ExportButtons) &&
                        Math.Abs(ExportButtons.PointToScreen(new Point(ExportButtons.ActualWidth, 0)).X -
                            ExportButton.PointToScreen(new Point(ExportButton.ActualWidth, 0)).X) < 1,
                        "Export settings did not anchor to the right edge of Export all.");
                    VerifyFlyoutRightEdgeSmoke();
                    double expandedHeight = exportSurface.ActualHeight;
                    CaptureVisual(exportSurface, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-export-options.png"));
                    Field<RadioButton>(exportContent, "ExportOneFileRadio").IsChecked = true;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    exportSurface.UpdateLayout();
                    RequireExportOptions(exportSurface.ActualHeight < expandedHeight - 40,
                        "The open export dropdown did not shrink when One file hid its sub-options.");
                    VerifyFlyoutRightEdgeSmoke();
                    CaptureVisual(exportSurface, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-export-options-one-file.png"));
                }
                CloseSettingsFlyout();
            }
        }
        finally
        {
            CloseSettingsFlyout(); CancelInterruptedPlayback(); _player.Stop();
            _renderCts?.Cancel(); ++_revision;
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var item in keys) _renderKeys[item.Key] = item.Value;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; Timeline.Playhead = playhead;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            StatusText.Text = status;
            Directory.Delete(directory, true);
        }
    }

    private static float[] ReadExportOptionsWav(string path, int rate, int channels)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != rate || reader.WaveFormat.Channels != channels || reader.WaveFormat.BitsPerSample != 32)
            throw new InvalidOperationException("Smoke test: album export used the wrong WAV format.");
        var bytes = new byte[reader.Length]; int read = 0;
        while (read < bytes.Length)
        {
            int count = reader.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }
        var result = new float[read / 4]; Buffer.BlockCopy(bytes, 0, result, 0, read); return result;
    }

    private static void RequireExportOptions(bool value, string message)
    { if (!value) throw new InvalidOperationException("Smoke test: " + message); }
}

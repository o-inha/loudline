using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifySingleTrackExportSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        RequireTrackExport(_tracks.Count >= 3, "Single-file export needs three ready fixture tracks.");
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineTrackExport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tracks = _tracks.ToArray();
        var target = tracks[1];
        var snapshots = tracks.Select(track => new
        {
            Track = track, track.Id, track.SourceId, track.FilePath, track.SampleRate, track.Channels,
            track.Original, OriginalCopy = track.Original.ToArray(), track.Render,
            RenderCopy = track.Render!.Samples.ToArray(), track.Analysis, track.OriginalWaveform,
            PreviewWaveform = track.Render.Waveform, track.OriginalPeaks, OriginalPeaksCopy = track.OriginalPeaks.ToArray(),
            track.PreviewPeaks, PreviewPeaksCopy = track.PreviewPeaks.ToArray(), track.Envelope,
            EnvelopeCopy = track.Envelope.ToArray(), track.TransitionEnvelope, TransitionCopy = track.TransitionEnvelope.ToArray(),
            track.TrimDb, track.InputGainDb, track.TargetLufs, track.WorkState, track.Status, Key = _renderKeys[track.Id],
        }).ToArray();
        var settings = _settings; var undo = _undo.ToArray(); var redo = _redo.ToArray();
        int revision = _revision, renderedRevision = _renderedRevision, executions = _renderExecutionCount;
        bool dirty = _dirty, importing = _importing, exporting = _exporting;
        bool timelineEnabled = Timeline.IsEnabled, processingEnabled = ProcessingPanel.IsEnabled;
        var selection = FileList.SelectedItem;
        bool? audition = AuditionMatchCheck.IsChecked;
        var savedRenderCts = _renderCts; var savedImportCts = _importCts; var savedExportCts = _exportCts;
        var progressVisibility = WorkProgress.Visibility; var cancelVisibility = CancelButton.Visibility;
        using var previewWork = new CancellationTokenSource();
        using var importWork = new CancellationTokenSource();
        using var exportWork = new CancellationTokenSource();
        ListBoxItem? container = null;
        try
        {
            SelectTrack(tracks[0]);
            var row = _rowsById[target.Id];
            FileList.ScrollIntoView(row);
            await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
            container = FileList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            RequireTrackExport(container != null, "The clicked export row was not realized.");
            container!.ApplyTemplate(); container.UpdateLayout();
            var clickedChild = Descendants<TextBlock>(container).First();
            RequireTrackExport(ReferenceEquals(Selected, tracks[0]) && ReferenceEquals(TrackForFileListContext(clickedChild), target),
                "A right-click exported the previous selection instead of the row under the pointer.");
            RequireTrackExport(ReferenceEquals(SelectFileListContextTarget(clickedChild), target) && ReferenceEquals(Selected, target) &&
                container.IsSelected && container.IsKeyboardFocusWithin && FileList.ContextMenu?.IsOpen != true,
                "Context targeting did not focus and select the clicked row without opening a menu.");
            container.Tag = tracks[2];
            RequireTrackExport(ReferenceEquals(TrackForFileListContext(clickedChild), tracks[2]),
                "A recycled row ignored its current track Tag.");
            container.Tag = null;
            RequireTrackExport(ReferenceEquals(TrackForFileListContext(clickedChild), target),
                "A row without a track Tag did not fall back to its TrackListEntry.");
            container.ClearValue(FrameworkElement.TagProperty);
            var foreign = new ListBoxItem { Tag = target, DataContext = row };
            RequireTrackExport(TrackForFileListContext(foreign) == null && TrackForFileListContext(FileList) == null &&
                SelectFileListContextTarget(FileList) == null && ReferenceEquals(Selected, target),
                "Blank space or a foreign container reused a selected export target.");

            SelectTrack(tracks[0]); Timeline.FitAll(); Timeline.UpdateLayout(); Timeline.FlushPendingRendering();
            double targetTime = tracks[0].Duration + target.Duration / 2;
            var filenamePoint = new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width *
                (targetTime - Timeline.ViewStartSeconds) / Timeline.VisibleDurationSeconds, Timeline.PlotArea.Top + 10);
            RequireTrackExport(Timeline.HandleTrackContextClick(filenamePoint) && _trackContextMenu is { IsOpen: true } &&
                ReferenceEquals(_trackContextMenu.PlacementTarget, Timeline) && ReferenceEquals(Selected, target) &&
                _trackContextMenu.Items[0] is MenuItem waveformExport && ReferenceEquals(waveformExport.Tag, target),
                "Waveform context export did not target the clicked filename strip.");
            _trackContextMenu!.IsOpen = false;
            RequireTrackExport(!Timeline.HandleTrackContextClick(new Point(Timeline.PlotArea.Left - 1, Timeline.PlotArea.Top + 10)) &&
                _trackContextMenu?.IsOpen != true && ReferenceEquals(Selected, target),
                "An outside waveform click opened a stale selected-track menu.");

            _renderCts = previewWork; _importCts = importWork; _exportCts = exportWork; _exporting = true;
            CancelClick(this, new RoutedEventArgs());
            RequireTrackExport(exportWork.IsCancellationRequested && !previewWork.IsCancellationRequested && !importWork.IsCancellationRequested,
                "Cancelling export also cancelled unrelated preview or import work.");
            _renderCts = savedRenderCts; _importCts = savedImportCts; _exportCts = savedExportCts; _exporting = exporting;

            _renderedRevision = -1;
            tracks[0].WorkState = TrackWorkState.Processing; tracks[2].WorkState = TrackWorkState.Missing;
            RequireTrackExport(CanExportTrack(target), "An unrelated unfinished or missing track blocked a current ready file.");
            var menu = CreateTrackContextMenu(target);
            RequireTrackExport(menu.Items[0] is MenuItem { IsEnabled: true } first && first.Header?.ToString() == "Export this file…" &&
                ReferenceEquals(first.Tag, target), "The ready row's export menu did not retain its own target and readiness.");
            foreach (var state in Enum.GetValues<TrackWorkState>().Where(state => state != TrackWorkState.Idle))
            {
                target.WorkState = state;
                RequireTrackExport(!CanExportTrack(target) && CreateTrackContextMenu(target).Items[0] is MenuItem { IsEnabled: false },
                    $"A {state} file allowed export.");
                if (state == TrackWorkState.Missing)
                    RequireTrackExport(CreateTrackContextMenu(target).Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Relink…"),
                        "The missing row lost its relink action.");
            }
            target.WorkState = TrackWorkState.Idle;
            var render = target.Render!; var analysis = target.Analysis!;
            target.Analysis = null; RequireTrackExport(!CanExportTrack(target), "An unmeasured file allowed export."); target.Analysis = analysis;
            target.Render = null; RequireTrackExport(!CanExportTrack(target), "A file without rendered PCM allowed export."); target.Render = render;
            double trim = target.TrimDb;
            target.TrimDb = trim + .5; RequireTrackExport(!CanExportTrack(target), "Stale processing settings allowed export."); target.TrimDb = trim;
            string key = _renderKeys[target.Id];
            _renderKeys[target.Id] = RenderKey(new AudioTrack
            {
                Id = target.Id, SampleRate = target.SampleRate, Channels = target.Channels, Original = target.Original,
                TrimDb = target.TrimDb, InputGainDb = target.InputGainDb, TargetLufs = target.TargetLufs,
                Envelope = target.Envelope, TransitionEnvelope = target.TransitionEnvelope,
            }, _settings);
            RequireTrackExport(!CanExportTrack(target), "A preview from a previous source identity allowed export.");
            _renderKeys[target.Id] = key;
            _importing = true; RequireTrackExport(!CanExportTrack(target), "Importing allowed single-file export."); _importing = false;
            _exporting = true; RequireTrackExport(!CanExportTrack(target), "An active export allowed a second export."); _exporting = false;
            RequireTrackExport(!CanExportTrack(new AudioTrack { Id = target.Id }), "A detached track with a reused slot ID allowed export.");
            tracks[0].WorkState = snapshots[0].WorkState; tracks[2].WorkState = snapshots[2].WorkState;

            // Force an audible monitoring correction; the saved WAV must still
            // contain the measured processed samples at their native format.
            target.Analysis = new AnalysisResult { IntegratedLufs = render.OutputLufs - 6 };
            AuditionMatchCheck.IsChecked = true;
            RequireTrackExport(PlaybackStateFor(target).PreviewAuditionGainDb < 0, "The export fixture did not exercise monitor attenuation.");
            string floatPath = Path.Combine(directory, "only-selected-float.wav");
            _renderCts = previewWork; tracks[0].WorkState = TrackWorkState.Processing;
            SetBusy("Building unrelated preview…", true);
            var floatTask = ExportSingleTrackAsync(target, floatPath, float32: true);
            RequireTrackExport(_exporting && !Timeline.IsEnabled && !ProcessingPanel.IsEnabled,
                "Single-file export did not freeze processing before its first await.");
            // A direct test mutation bypasses the disabled UI to prove the writer
            // already owns the immutable render selected before the first await.
            target.Render = new RenderResult { Samples = target.Original };
            _renderCts = null; tracks[0].WorkState = TrackWorkState.Idle;
            try { await floatTask; }
            finally { target.Render = render; }
            VerifySingleTrackWavSmoke(floatPath, target, render.Samples, float32: true);
            RequireTrackExport(Directory.GetFiles(directory).Length == 1 && !_exporting &&
                Timeline.IsEnabled == timelineEnabled && ProcessingPanel.IsEnabled == processingEnabled,
                "One-file export wrote extra tracks or failed to restore editing.");
            RequireTrackExport(WorkProgress.Visibility == Visibility.Collapsed && CancelButton.Visibility == Visibility.Collapsed,
                "Export restored stale progress after an unrelated preview finished during the write.");
            string pcmPath = Path.Combine(directory, "only-selected-pcm.wav");
            _renderCts = previewWork; tracks[0].WorkState = TrackWorkState.Processing;
            await ExportSingleTrackAsync(target, pcmPath, float32: false);
            VerifySingleTrackWavSmoke(pcmPath, target, render.Samples, float32: false);
            RequireTrackExport(Directory.GetFiles(directory).Length == 2, "24-bit export wrote more than the chosen file.");
            RequireTrackExport(WorkProgress.Visibility == Visibility.Visible && CancelButton.Visibility == Visibility.Visible &&
                !previewWork.IsCancellationRequested, "Export hid or cancelled an unrelated preview that was still processing.");
            _renderCts = savedRenderCts; tracks[0].WorkState = snapshots[0].WorkState; SetIdle("Ready");
            RequireTrackExport(_renderedRevision == -1, "Single-file export changed the unrelated release readiness revision.");

            byte[] originalFile = File.ReadAllBytes(target.FilePath);
            await ExpectTrackExportFailureAsync<IOException>(() => ExportSingleTrackAsync(target, target.FilePath, true));
            RequireTrackExport(File.ReadAllBytes(target.FilePath).SequenceEqual(originalFile), "Export modified an imported source file.");
            string existing = Path.Combine(directory, "existing.wav");
            byte[] sentinel = [11, 22, 33, 44]; File.WriteAllBytes(existing, sentinel);
            await ExpectTrackExportFailureAsync<IOException>(() => ExportSingleTrackAsync(target, existing, true));
            RequireTrackExport(File.ReadAllBytes(existing).SequenceEqual(sentinel), "Export overwrote an existing destination.");
            string otherInput = tracks[0].FilePath;
            tracks[0].FilePath = Path.Combine(directory, "unavailable-input.wav");
            try
            {
                await ExpectTrackExportFailureAsync<IOException>(() => ExportSingleTrackAsync(target, tracks[0].FilePath, true));
                RequireTrackExport(!File.Exists(tracks[0].FilePath), "Export replaced another track's unavailable source path.");
            }
            finally { tracks[0].FilePath = otherInput; }

            var savedBackground = _backgroundLayer;
            string missingBackgroundPath = Path.Combine(directory, "unavailable-background.wav");
            _backgroundLayer = savedBackground with
            {
                Enabled = false,
                Clips = savedBackground.Clips.Append(new BackgroundClip
                {
                    Source = new AudioTrack { FilePath = missingBackgroundPath, SavedDuration = 1, WorkState = TrackWorkState.Missing },
                    // A disabled, out-of-album source must still be protected;
                    // it cannot hide this assertion behind mix readiness.
                    StartSeconds = _tracks.Sum(item => item.Duration) + 60
                }).ToArray()
            };
            try
            {
                await ExpectTrackExportFailureAsync<IOException>(() => ExportSingleTrackAsync(target, missingBackgroundPath, true));
                RequireTrackExport(!File.Exists(missingBackgroundPath), "Export replaced an unavailable background source path.");
            }
            finally { _backgroundLayer = savedBackground; }

            string cancelled = Path.Combine(directory, "cancelled.wav");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ExpectTrackExportFailureAsync<OperationCanceledException>(() => ExportSingleTrackAsync(target, cancelled, true, cancellation.Token));
            }
            RequireTrackExport(!File.Exists(cancelled) && !Directory.EnumerateFiles(directory, "*.partial").Any(),
                "Cancelled export left a destination or temporary WAV behind.");
            string failed = Path.Combine(directory, "failed.wav");
            target.Render = new RenderResult { Samples = [float.NaN, 0] };
            try { await ExpectTrackExportFailureAsync<InvalidDataException>(() => ExportSingleTrackAsync(target, failed, true)); }
            finally { target.Render = render; target.Analysis = analysis; }
            RequireTrackExport(!File.Exists(failed) && !Directory.EnumerateFiles(directory, "*.partial").Any() &&
                !_exporting && Timeline.IsEnabled == timelineEnabled && ProcessingPanel.IsEnabled == processingEnabled,
                "Failed writer did not clean its partial output and restore editing.");

            RequireTrackExport(_tracks.SequenceEqual(tracks) && ReferenceEquals(_settings, settings) && _undo.SequenceEqual(undo) &&
                _redo.SequenceEqual(redo) && _dirty == dirty && _revision == revision && _renderExecutionCount == executions,
                "Single-file export changed project settings, track order, render work, or history.");
            foreach (var before in snapshots)
            {
                var track = before.Track;
                RequireTrackExport(track.Id == before.Id && track.SourceId == before.SourceId && track.FilePath == before.FilePath &&
                    track.SampleRate == before.SampleRate && track.Channels == before.Channels &&
                    ReferenceEquals(track.Original, before.Original) && SameTrackExportBits(track.Original, before.OriginalCopy) &&
                    ReferenceEquals(track.Render, before.Render) && SameTrackExportBits(track.Render!.Samples, before.RenderCopy) &&
                    ReferenceEquals(track.Analysis, before.Analysis) && ReferenceEquals(track.OriginalWaveform, before.OriginalWaveform) &&
                    ReferenceEquals(track.Render.Waveform, before.PreviewWaveform) && ReferenceEquals(track.OriginalPeaks, before.OriginalPeaks) &&
                    track.OriginalPeaks.SequenceEqual(before.OriginalPeaksCopy) && ReferenceEquals(track.PreviewPeaks, before.PreviewPeaks) &&
                    track.PreviewPeaks.SequenceEqual(before.PreviewPeaksCopy) && ReferenceEquals(track.Envelope, before.Envelope) &&
                    track.Envelope.SequenceEqual(before.EnvelopeCopy) && ReferenceEquals(track.TransitionEnvelope, before.TransitionEnvelope) &&
                    track.TransitionEnvelope.SequenceEqual(before.TransitionCopy) && track.TrimDb == before.TrimDb &&
                    track.InputGainDb == before.InputGainDb && track.TargetLufs == before.TargetLufs && _renderKeys[track.Id] == before.Key,
                    "Single-file export changed source audio, rendered audio, waveform data, metadata, or edits.");
            }
        }
        finally
        {
            if (_trackContextMenu is { } context) context.IsOpen = false;
            _renderCts = savedRenderCts; _importCts = savedImportCts; _exportCts = savedExportCts;
            container?.ClearValue(FrameworkElement.TagProperty);
            foreach (var before in snapshots)
            {
                before.Track.FilePath = before.FilePath; before.Track.Render = before.Render; before.Track.Analysis = before.Analysis;
                before.Track.TrimDb = before.TrimDb; before.Track.WorkState = before.WorkState; before.Track.Status = before.Status;
                _renderKeys[before.Id] = before.Key;
            }
            _importing = importing; _exporting = exporting; _renderedRevision = renderedRevision;
            AuditionMatchCheck.IsChecked = audition;
            Timeline.IsEnabled = timelineEnabled; ProcessingPanel.IsEnabled = processingEnabled;
            RefreshList(); FileList.SelectedItem = selection; CompletePreview(_revision);
            _renderedRevision = renderedRevision;
            WorkProgress.Visibility = progressVisibility; CancelButton.Visibility = cancelVisibility;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySingleTrackWavSmoke(string path, AudioTrack track, float[] expected, bool float32)
    {
        int sampleRate = track.Render is { RenderedSampleRate: > 0 } renderRate ? renderRate.RenderedSampleRate : track.SampleRate;
        int channels = track.Render is { RenderedChannels: > 0 } renderChannels ? renderChannels.RenderedChannels : track.Channels;
        using (var reader = new WaveFileReader(path))
            RequireTrackExport(reader.WaveFormat.SampleRate == sampleRate && reader.WaveFormat.Channels == channels &&
                reader.WaveFormat.BitsPerSample == (float32 ? 32 : 24) &&
                reader.WaveFormat.Encoding == (float32 ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm) &&
                reader.Length == (long)expected.Length * (float32 ? 4 : 3),
                "Single-file WAV changed encoding, sample rate, channels, or duration.");
        var decoded = AudioFileService.Load(path);
        RequireTrackExport(decoded.Original.Length == expected.Length, "Single-file WAV has the wrong sample count.");
        if (float32)
            RequireTrackExport(SameTrackExportBits(decoded.Original, expected), "Float WAV contains monitoring gain or another track's PCM.");
        else
            RequireTrackExport(decoded.Original.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 2.0 / 8_388_608),
                "24-bit WAV differs from processed PCM beyond its dither and quantization tolerance.");
    }

    private static bool SameTrackExportBits(IEnumerable<float> actual, IEnumerable<float> expected)
        => actual.Select(BitConverter.SingleToInt32Bits).SequenceEqual(expected.Select(BitConverter.SingleToInt32Bits));

    private static async Task ExpectTrackExportFailureAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Smoke test: single-file export did not reject {typeof(T).Name} fixture.");
    }

    private static void RequireTrackExport(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("Smoke test: " + message);
    }
}

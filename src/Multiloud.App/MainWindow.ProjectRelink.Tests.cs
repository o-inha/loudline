using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyProjectRelinkSmokeAsync(string? screenshot)
    {
        var session = Capture();
        var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        string? projectPath = _projectPath;
        bool floatExport = _floatExport, dirty = _dirty;
        string title = Title, status = StatusText.Text;
        double playhead = Timeline.Playhead;
        var selected = Selected;
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineRelinkSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        try
        {
            string availablePath = Path.Combine(directory, "Available.wav");
            string replacementPath = Path.Combine(directory, "Renamed replacement.wav");
            WriteProjectRelinkAudio(availablePath, 1, 1);
            WriteProjectRelinkAudio(replacementPath, 2, 2);
            string missingPath = Path.Combine(directory, "Offline.wav");
            string missingAbsolute = Path.Combine(directory, "old-location", "Offline.wav");
            EnvelopePoint[] authored = [new(0, 1), new(3, -.5), new(7, -1)];
            ProjectTrack Available() => new()
            {
                Path = "Available.wav", AbsolutePath = availablePath, Duration = 1,
                SampleRate = 12000, Channels = 1, Envelope = [new(0, 0), new(1, 0)], TransitionEnvelope = []
            };
            var data = new ProjectData
            {
                Settings = _settings, FloatExport = !floatExport,
                Tracks =
                [
                    Available(),
                    new ProjectTrack
                    {
                        Path = "Offline.wav", AbsolutePath = missingAbsolute, Duration = 7,
                        SampleRate = 12000, Channels = 2, InputGainDb = 1, TargetLufs = -12, TrimDb = .5,
                        Envelope = authored.ToList(), TransitionEnvelope = []
                    },
                    Available()
                ]
            };
            string path = Path.Combine(directory, "Release.loudline");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data));
            await OpenProjectAsync(path, showMissingDialog: false);
            await WaitForRelinkTracksSmokeAsync(deadline);
            RequireProjectRelink(_tracks.Count == 3 && _tracks[0].FilePath == availablePath && _tracks[1].FilePath == missingPath &&
                _tracks[2].FilePath == availablePath && _tracks[0].Id != _tracks[2].Id && !ReferenceEquals(_tracks[0], _tracks[2]),
                "Project opening changed track order or collapsed a duplicate source slot.");
            VerifyUnresolvedProjectSlotSmoke(_tracks[1], authored);
            VerifyProjectMissingBoundarySmoke();
            await VerifyProjectRelinkRowSmokeAsync(_tracks[1], missing: true);

            string savedPath = Path.Combine(directory, "Saved.loudline");
            var saved = JsonSerializer.Deserialize<ProjectData>(JsonSerializer.Serialize(CreateProjectData(savedPath)))!;
            RequireProjectRelink(saved.Version == 9 && saved.FloatExport == !floatExport && saved.Tracks.Count == 3 &&
                saved.Tracks[0].Path == "Available.wav" && saved.Tracks[0].AbsolutePath == availablePath &&
                saved.Tracks[2].Path == "Available.wav" && saved.Tracks[2].AbsolutePath == availablePath &&
                saved.Tracks[1].Path == "Offline.wav" && saved.Tracks[1].AbsolutePath == missingAbsolute && saved.Tracks[1].Duration == 7 &&
                saved.Tracks[1].InputGainDb == 1 && saved.Tracks[1].TargetLufs == -12 && saved.Tracks[1].TrimDb == .5 &&
                SameRelinkEnvelopeInterior(saved.Tracks[1].Envelope, authored, 7), "Project save lost dual paths, duplicate slots, duration or authored settings.");
            await File.WriteAllTextAsync(savedPath, JsonSerializer.Serialize(saved));
            await OpenProjectAsync(savedPath, showMissingDialog: false);
            await WaitForRelinkTracksSmokeAsync(deadline);
            var missing = _tracks[1];
            VerifyUnresolvedProjectSlotSmoke(missing, authored);
            VerifyProjectMissingBoundarySmoke();
            RequireProjectRelink(_projectPath == savedPath && _floatExport == !floatExport && _tracks[0].FilePath == availablePath &&
                _tracks[2].FilePath == availablePath && _tracks.Sum(track => track.Duration) == 9,
                "Reopening an unresolved project changed its source slots or duration.");
            await VerifyProjectRelinkRowSmokeAsync(missing, missing: true);
            if (screenshot != null) CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-project-missing.png"));

            var unresolvedOrder = _tracks.ToArray();
            var unresolvedRenders = _tracks.Select(track => track.Render).ToArray();
            Guid sourceId = missing.SourceId;
            await RelinkTrackAsync(missing, replacementPath);
            await WaitForRelinkTracksSmokeAsync(deadline);
            var replacement = _tracks[1];
            RequireProjectRelink(!ReferenceEquals(replacement, missing) && replacement.Id == missing.Id && replacement.SourceId != sourceId &&
                ReferenceEquals(_tracks[0], unresolvedOrder[0]) && ReferenceEquals(_tracks[2], unresolvedOrder[2]) &&
                replacement.FilePath == replacementPath && replacement.Duration == 2 && replacement.SampleRate == 12000 && replacement.Channels == 2 &&
                replacement.Analysis != null && replacement.OriginalWaveform != null && replacement.Render != null &&
                replacement.Original.Length == 48000 && replacement.Render.Samples.Length == 48000 &&
                replacement.InputGainDb == 1 && replacement.TargetLufs == -12 && replacement.TrimDb == .5 &&
                replacement.Envelope.Count >= 2 && replacement.Envelope.All(point => double.IsFinite(point.GainDb) && point.Time >= 0 && point.Time <= 2) && ExportButton.IsEnabled && _renderedRevision == _revision,
                "Relinking did not analyze a fresh source in the existing slot with its retained settings.");
            await VerifyProjectRelinkRowSmokeAsync(replacement, missing: false);
            var linkedRenders = _tracks.Select(track => track.Render).ToArray();
            var linkedAnalysis = replacement.Analysis;
            UndoClick(this, new RoutedEventArgs());
            await WaitForRelinkTracksSmokeAsync(deadline);
            RequireProjectRelink(_tracks.SequenceEqual(unresolvedOrder) && ReferenceEquals(_tracks[1], missing) && missing.SourceId == sourceId &&
                _tracks.Where((track, index) => !ReferenceEquals(track.Render, unresolvedRenders[index])).Any() == false,
                "Relink Undo did not restore the missing object and exact previous preview buffers.");
            VerifyUnresolvedProjectSlotSmoke(missing, authored);
            await VerifyProjectRelinkRowSmokeAsync(missing, missing: true);
            RedoClick(this, new RoutedEventArgs());
            await WaitForRelinkTracksSmokeAsync(deadline);
            RequireProjectRelink(ReferenceEquals(_tracks[1], replacement) && replacement.SourceId != sourceId &&
                ReferenceEquals(replacement.Analysis, linkedAnalysis) &&
                _tracks.Where((track, index) => !ReferenceEquals(track.Render, linkedRenders[index])).Any() == false && ExportButton.IsEnabled,
                "Relink Redo lost the replacement source or reused/rebuilt the wrong rendered PCM.");
            await VerifyProjectRelinkRowSmokeAsync(replacement, missing: false);
            if (screenshot != null) CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-project-relinked.png"));

            // Replacing a ready source exercises the cache ownership hazard that
            // a missing source (with no old Render) cannot expose.
            var readyA = replacement;
            var renderA = readyA.Render!;
            var samplesA = renderA.Samples;
            var originalA = readyA.Original;
            string otherPath = Path.Combine(directory, "Different ready replacement.wav");
            WriteProjectRelinkAudio(otherPath, 2, 2, frequency: 731, amplitude: .03);
            await RelinkTrackAsync(readyA, otherPath);
            await WaitForRelinkTracksSmokeAsync(deadline);
            var readyB = _tracks[1];
            RequireProjectRelink(!ReferenceEquals(readyA, readyB) && readyB.Id == readyA.Id && readyB.SourceId != readyA.SourceId &&
                readyB.Duration == readyA.Duration && readyB.Channels == readyA.Channels && readyB.SampleRate == readyA.SampleRate &&
                !readyB.Original.SequenceEqual(originalA) && readyB.Render != null && !readyB.Render.Samples.SequenceEqual(samplesA),
                "A same-length ready-source replacement reused the previous source or rendered PCM.");
            var renderB = readyB.Render!;
            var analysisB = readyB.Analysis;
            int renderedBeforeHistory = _renderExecutionCount;
            UndoClick(this, new RoutedEventArgs());
            await WaitForRelinkTracksSmokeAsync(deadline);
            RequireProjectRelink(ReferenceEquals(_tracks[1], readyA) && ReferenceEquals(readyA.Original, originalA) &&
                ReferenceEquals(readyA.Render, renderA) && ReferenceEquals(readyA.Render?.Samples, samplesA),
                "Ready-source Undo did not restore source A's exact rendered buffer.");
            await VerifyProjectRelinkRowSmokeAsync(readyA, missing: false);
            RedoClick(this, new RoutedEventArgs());
            await WaitForRelinkTracksSmokeAsync(deadline);
            RequireProjectRelink(ReferenceEquals(_tracks[1], readyB) && ReferenceEquals(readyB.Render, renderB) &&
                ReferenceEquals(readyB.Analysis, analysisB) && ReferenceEquals(readyB.Render?.Samples, renderB.Samples) &&
                _renderExecutionCount == renderedBeforeHistory,
                "Ready-source Redo reused source A's PCM or rebuilt source B instead of restoring its cached preview.");
            await VerifyProjectRelinkRowSmokeAsync(readyB, missing: false);
        }
        finally
        {
            CancelSourceWork();
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys.Add(pair.Key, pair.Value);
            _previewCache.Clear();
            foreach (var entry in session.Tracks)
                if (entry.Track.Render != null && _renderKeys.TryGetValue(entry.Track.Id, out var key))
                    _previewCache.Store(key, entry.Track.Render, entry.Track.PreviewPeaks);
            _floatExport = floatExport; _projectPath = projectPath;
            Restore(session);
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty; Title = title; Timeline.Playhead = playhead; Timeline.FitAll();
            if (selected != null) SelectTrack(selected);
            else { FileList.SelectedItem = null; Timeline.SelectedTrack = null; }
            UpdateTime(); StatusText.Text = status;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool SameRelinkEnvelopeInterior(IReadOnlyList<EnvelopePoint> actual, IReadOnlyList<EnvelopePoint> expected, double duration)
    {
        // Legacy independent curves may disagree at the exact shared edge. The
        // project migration preserves their interiors with a one-sample bridge.
        for (double time = 0; time < duration - .001; time += .137)
            if (Math.Abs(AudioProcessor.EnvelopeGainAt(actual, time) - AudioProcessor.EnvelopeGainAt(expected, time)) > .0001) return false;
        return true;
    }

    private async Task WaitForRelinkTracksSmokeAsync(DateTime deadline)
    {
        bool Ready() => _renderCts == null && !_importing && _tracks.All(track => track.WorkState == TrackWorkState.Missing ||
            track.WorkState == TrackWorkState.Idle && track.Analysis != null && PreviewCurrent(track, _settings));
        while (!Ready() && DateTime.UtcNow < deadline) await Task.Delay(20);
        RequireProjectRelink(Ready(), "Project relink fixture did not finish its available previews within twelve seconds.");
    }

    private void VerifyUnresolvedProjectSlotSmoke(AudioTrack missing, IReadOnlyList<EnvelopePoint> authored)
    {
        RequireProjectRelink(missing.WorkState == TrackWorkState.Missing && missing.Duration == 7 && missing.Original.Length == 0 &&
            missing.Analysis == null && missing.Render == null && missing.InputGainDb == 1 && missing.TargetLufs == -12 &&
            missing.TrimDb == .5 && SameRelinkEnvelopeInterior(missing.Envelope, authored, 7) && !ExportButton.IsEnabled,
            "A missing project slot lost duration/settings or became exportable.");
        ExportClick(this, new RoutedEventArgs());
        RequireProjectRelink(!_exporting && StatusText.Text.Contains("Relink", StringComparison.OrdinalIgnoreCase),
            "The export guard did not reject an unresolved project before opening a picker.");
    }

    private void VerifyProjectMissingBoundarySmoke()
    {
        var states = CapturePlaybackTracks();
        RequireProjectRelink(states.Length == 3 && states[0].OriginalSamples != null && states[0].PreviewSamples != null &&
            states[1].Duration == 7 && states[1].OriginalSamples == null && states[1].PreviewSamples == null && states[2].PreviewSamples != null,
            "A missing project source exposed stale audio or hid a ready neighbor.");
        foreach (bool original in new[] { true, false })
        {
            var sequence = new PlaybackSequence(() => states, original, 0);
            var buffer = new float[4096];
            int reads = 0;
            while (sequence.Read(buffer, 0, buffer.Length) > 0 && ++reads < 100) { }
            RequireProjectRelink(reads < 100 && sequence.PendingTrackId == states[1].Id && !sequence.IsComplete &&
                Math.Abs(sequence.StopPositionSeconds - states[0].Duration) < .000001,
                "Playback skipped a missing source instead of stopping at its boundary.");
        }
    }

    private async Task VerifyProjectRelinkRowSmokeAsync(AudioTrack track, bool missing)
    {
        var row = _rowsById[track.Id];
        RequireProjectRelink(ReferenceEquals(row.Track, track) && row.Missing == missing,
            "A relink/Undo/Redo left a recycled file row bound to the previous source object.");
        FileList.ScrollIntoView(row);
        await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
        RequireProjectRelink(FileList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem,
            "The project slot row was not realized for inspection.");
        var item = (ListBoxItem)FileList.ItemContainerGenerator.ContainerFromItem(row)!;
        item.ApplyTemplate(); item.UpdateLayout();
        var relink = Descendants<Button>(item).Single(button => button.Content?.ToString() == "Relink…");
        RequireProjectRelink(ReferenceEquals(item.Tag, track) && relink.IsVisible == missing,
            "The relink button or recycled row handle did not follow the slot's source state.");
        if (missing)
        {
            var background = (item.Template.FindName("B", item) as Border)?.Background as SolidColorBrush;
            RequireProjectRelink(background?.Color == ((SolidColorBrush)FindResource("MissingBackgroundBrush")).Color,
                "A missing project slot did not use its muted red appearance.");
        }
    }

    private static void WriteProjectRelinkAudio(string path, double seconds, int channels, double frequency = 440, double amplitude = .08)
    {
        const int rate = 12000;
        var samples = new float[(int)(seconds * rate) * channels];
        for (int frame = 0; frame < samples.Length / channels; frame++)
            for (int channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] = (float)(amplitude * Math.Sin(2 * Math.PI * (frequency + channel * 71) * frame / rate + channel * .2));
        AudioFileService.ExportWav(new AudioTrack
        {
            FilePath = path + ".source", SampleRate = rate, Channels = channels, Original = samples,
            Render = new RenderResult { Samples = samples }
        }, path, float32: true);
    }

    private static void RequireProjectRelink(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("Smoke test: " + message);
    }
}

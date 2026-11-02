using System.IO;
using System.Text.Json;
using System.Windows;
using Multiloud.App.Services;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBoundaryPersistenceSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        string? projectPath = _projectPath;
        bool needsCopy = _backgroundProjectNeedsCopy, dirty = _dirty;
        string title = Title, status = StatusText.Text;
        double playhead = Timeline.Playhead; var selected = Selected;
        string folder = Path.Combine(Path.GetTempPath(), "LoudlineBoundaries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            int revision = _revision, executions = _renderExecutionCount, cached = _previewCache.Count;
            var renders = _tracks.Select(track => track.Render).ToArray();
            double duration = _tracks.Sum(track => track.Duration);
            double[] markers = [duration * .23, duration * .69, duration + 7];
            var options = new ProjectExportOptions { AsOneFile = true, UseCustomBoundaries = false, BoundaryFadeMs = 3.5 };
            LoadCustomBoundaries(markers); LoadExportOptions(options); _floatExport = true;
            if (_revision != revision || _renderExecutionCount != executions || _previewCache.Count != cached ||
                !_tracks.Select(track => track.Render).SequenceEqual(renders) || _dirty != dirty ||
                !_renderKeys.OrderBy(pair => pair.Key).SequenceEqual(keys.OrderBy(pair => pair.Key)))
                throw new InvalidOperationException("Smoke test: loading export-only markers/settings changed processing, cached output or project dirty state.");
            string file = Path.Combine(folder, "Export layout.loudline");
            var saved = CreateProjectData(file);
            string json = JsonSerializer.Serialize(saved);
            var roundtrip = ProjectFileService.DeserializeProject(json);
            if (roundtrip.Version != 9 || !roundtrip.FloatExport ||
                !roundtrip.CustomTrackBoundaries.SequenceEqual(markers) || roundtrip.ExportOptions != options ||
                !roundtrip.ProjectEnvelope!.SequenceEqual(_projectEnvelope) || roundtrip.Tracks.Count != _tracks.Count)
                throw new InvalidOperationException("Smoke test: saved export choices or independent project envelope were lost.");
            await File.WriteAllTextAsync(file, json);
            LoadCustomBoundaries([]); LoadExportOptions(new()); _floatExport = false;
            await OpenProjectAsync(file, showMissingDialog: false); await WaitForSmokePreviewAsync();
            if (!CaptureCustomBoundaries().SequenceEqual(markers) || CaptureExportOptions() != options || !_floatExport ||
                _backgroundProjectNeedsCopy || File.ReadAllText(file) != json || _tracks.Any(track => track.Render == null))
                throw new InvalidOperationException("Smoke test: reopening a current project changed its export layout, read-only file or ready music.");

            // Explicitly authored custom preference survives having no valid
            // markers; runtime controls perform their original-boundary fallback.
            LoadCustomBoundaries([]);
            var preferred = new ProjectExportOptions { UseCustomBoundaries = true, BoundaryFadeMs = 0 };
            LoadExportOptions(preferred);
            var emptyLayout = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(CreateProjectData(file)));
            if (emptyLayout.CustomTrackBoundaries.Count != 0 || emptyLayout.ExportOptions != preferred)
                throw new InvalidOperationException("Smoke test: a marker-free project forgot its preferred custom export mode.");

            var legacy = CreateProjectData(file); legacy.Version = 7;
            legacy.CustomTrackBoundaries = markers.ToList(); legacy.ExportOptions = options;
            string legacyFile = Path.Combine(folder, "Older layout.loudline");
            string legacyJson = JsonSerializer.Serialize(legacy);
            await File.WriteAllTextAsync(legacyFile, legacyJson);
            await OpenProjectAsync(legacyFile, showMissingDialog: false); await WaitForSmokePreviewAsync();
            if (CaptureCustomBoundaries().Count != 0 || CaptureExportOptions() != new ProjectExportOptions() ||
                !_backgroundProjectNeedsCopy || !_floatExport || File.ReadAllText(legacyFile) != legacyJson)
                throw new InvalidOperationException("Smoke test: legacy migration retained stray export settings, lost its WAV format or stopped suggesting a separate save copy.");
        }
        finally
        {
            CancelSourceWork();
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            _projectPath = projectPath; _backgroundProjectNeedsCopy = needsCopy;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; Title = title; Timeline.Playhead = playhead;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            UpdateTime(); StatusText.Text = status;
            Directory.Delete(folder, recursive: true);
        }
    }
}

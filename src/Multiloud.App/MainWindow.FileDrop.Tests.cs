using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyExternalFileDropSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty, original = OriginalRadio.IsChecked == true, compare = CompareRadio.IsChecked == true;
        double playhead = Timeline.Playhead; var selected = Selected;
        string status = StatusText.Text, title = Title; string? projectPath = _projectPath;
        string folder = Path.Combine(Path.GetTempPath(), "Loudline-file-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        async Task FinishImport()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (_importing && DateTime.UtcNow < deadline) await Task.Delay(10);
            RequireFileDrop(!_importing, "Dropped files did not finish importing.");
            await WaitForSmokePreviewAsync();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        }
        static DataObject Drop(params string[] paths) => new(DataFormats.FileDrop, paths);
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            foreach (var item in _tracks) RememberActivePreview(item);
            var settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false, TransitionSeconds = 2 };
            var existing = await Task.Run(() => Enumerable.Range(0, 3).Select(index => CreateRenderPriorityTrack(index, index + 12, settings)).ToArray());
            foreach (var track in existing) track.Envelope = [new(0, .5), new(track.Duration, -.5)];
            _tracks.Clear(); _tracks.AddRange(existing); _settings = settings; _renderKeys.Clear();
            LoadProjectEnvelope(ProjectEnvelopeDesigner.FromTracks(existing), ProjectEnvelopeDesigner.Design(existing, settings));
            var authoredManual = Difference(_projectEnvelope, _projectSuggestion);
            var previousOffsets = existing.Select(track => existing.TakeWhile(item => !ReferenceEquals(item, track)).Sum(item => item.Duration)).ToArray();
            foreach (var track in existing) _renderKeys[track.Id] = RenderKey(track, settings);
            SyncControls(); PreviewRadio.IsChecked = true; RefreshList(); CompletePreview(_revision); Timeline.FitAll();
            _undo.Clear(); _redo.Clear();

            var paths = new Dictionary<int, string>();
            foreach (int number in new[] { 1, 2, 3, 10, 11 })
            {
                string path = Path.Combine(folder, $"Track {number}.wav"); paths[number] = path;
                AudioFileService.ExportWav(new AudioTrack { FilePath = "synthetic.raw", SampleRate = 8000, Channels = 1,
                    Render = new RenderResult { Samples = existing[0].Original.Take(8000).ToArray() } }, path, true);
            }
            string project = Path.Combine(folder, "not-audio.loudline");
            File.WriteAllText(project, "{}");
            var data = Drop(paths[10], project, paths[2], paths[1], paths[2], Path.Combine(folder, "missing.wav"));
            var point = new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * existing[0].Duration / existing.Sum(track => track.Duration), Timeline.PlotArea.Top + 70);
            RequireFileDrop(Timeline.PreviewFileDrop(data, point) && Timeline.DropInsertionIndex == 1 && _tracks.SequenceEqual(existing),
                "Explorer-style FileDrop did not highlight the nearest existing waveform edge without changing order.");
            RequireFileDrop(Timeline.HandleTrackDrop(data, point) && Timeline.DropInsertionIndex == -1,
                "Waveform file drop did not clear its shared insertion marker.");
            await FinishImport();
            var imported = new[] { paths[1], paths[2], paths[10] };
            RequireFileDrop(_tracks.Select(track => track.FilePath).SequenceEqual(new[] { existing[0].FilePath }.Concat(imported).Concat(existing.Skip(1).Select(track => track.FilePath))) &&
                FileList.Items.Cast<TrackListEntry>().Select(row => row.Track).SequenceEqual(_tracks) && Timeline.Tracks.SequenceEqual(_tracks),
                "The external batch was not naturally sorted and inserted consecutively at the requested track boundary.");
            var currentManual = Difference(_projectEnvelope, _projectSuggestion);
            RequireFileDrop(existing.Select((track, index) =>
            {
                double start = _tracks.TakeWhile(item => !ReferenceEquals(item, track)).Sum(item => item.Duration);
                for (double local = settings.TransitionSeconds + .25; local < track.Duration - settings.TransitionSeconds - .25; local += .25)
                    if (Math.Abs(AudioProcessor.EnvelopeGainAt(currentManual, start + local) -
                        AudioProcessor.EnvelopeGainAt(authoredManual, previousOffsets[index] + local)) > 1e-9) return false;
                return true;
            }).All(value => value), "External insertion overwrote a source-local manual envelope displacement outside its changed edge windows.");

            // The same real FileDrop format uses the existing list adorner and
            // imports before its first visible row, rather than always appending.
            FileList.ScrollIntoView(_trackRows[0]); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var firstRow = FileList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            RequireFileDrop(firstRow != null, "The file-list drop fixture has no visible first row.");
            var listPoint = firstRow!.TranslatePoint(new Point(5, 1), FileList);
            var listData = Drop(paths[3]);
            RequireFileDrop(PreviewFileListDrop(listData, listPoint) == 0 && _fileListDropMarker != null &&
                HandleFileListDrop(listData, listPoint) && _fileListDropMarker == null,
                "The file-list external drop did not reuse and clear its boundary adorner.");
            await FinishImport();
            RequireFileDrop(_tracks[0].FilePath == paths[3] && ReferenceEquals(_tracks[1], existing[0]),
                "Dropping above the file list did not insert at the beginning.");

            FileList.ScrollIntoView(_trackRows[^1]); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            RequireFileDrop(FileListDropBoundary(new Point(5, FileList.ActualHeight - 1)).Index == _tracks.Count,
                "A drop below the last item did not target the album end.");
            var remaining = Drop(paths[11]);
            Timeline.FitAll();
            RequireFileDrop(Timeline.PreviewFileDrop(remaining, new Point(Timeline.PlotArea.Right - 1, point.Y)) && Timeline.DropInsertionIndex == _tracks.Count,
                "The rightmost waveform edge did not target append.");
            var beforeInvalid = _tracks.ToArray(); int history = _undo.Count;
            var invalid = Drop(project, Path.Combine(folder, "missing.wav"), paths[1]);
            RequireFileDrop(!Timeline.PreviewFileDrop(invalid, point) && Timeline.DropInsertionIndex == -1 &&
                Timeline.HandleTrackDrop(invalid, point) && PreviewFileListDrop(invalid, listPoint) == -1 && _fileListDropMarker == null &&
                _tracks.SequenceEqual(beforeInvalid) && _undo.Count == history && !_importing && _projectPath == projectPath && Title == title,
                "Unsupported, missing or duplicate dropped paths changed order/history or opened a project.");

            var empty = new TimelineView { CanDropFiles = _ => true };
            empty.Measure(new Size(800, 300)); empty.Arrange(new Rect(0, 0, 800, 300)); empty.Refresh();
            int emptyBoundary = -1;
            empty.FilesDropRequested += (_, index) => emptyBoundary = index;
            RequireFileDrop(empty.PreviewFileDrop(remaining, new Point(200, 150)) && empty.DropInsertionIndex == 0 &&
                empty.HandleTrackDrop(remaining, new Point(200, 150)) && emptyBoundary == 0 && empty.DropInsertionIndex == -1,
                "An empty waveform did not provide insertion boundary zero.");

            // An unknown earlier duration must hold (not consume) a source-local
            // resume. Once known, the same source resumes at its shifted offset.
            var target = existing[^1];
            double targetOffset = _tracks.TakeWhile(track => !ReferenceEquals(track, target)).Sum(track => track.Duration);
            RequireFileDrop(ArmInterruptedPlayback(targetOffset + .25, true, false, _revision), "The insertion resume fixture did not arm.");
            var unknown = new AudioTrack { WorkState = TrackWorkState.Analyzing };
            _tracks.Insert(0, unknown);
            double resumed = -1;
            RequireFileDrop(!TryResumeInterruptedPlayback(_revision, value => resumed = value) && HasInterruptedPlayback,
                "An unknown inserted duration consumed the intended later-track resume.");
            unknown.SampleRate = 8000; unknown.Channels = 1; unknown.Original = new float[8000];
            RequireFileDrop(TryResumeInterruptedPlayback(_revision, value => resumed = value) && Math.Abs(resumed - (targetOffset + 1.25)) < 1e-9,
                "Resolving an inserted duration lost the interrupted track's local playback position.");
            _tracks.Remove(unknown); CancelInterruptedPlayback(); RefreshList();

            var failed = new AudioTrack { WorkState = TrackWorkState.Analyzing };
            _tracks.Insert(1, failed); var interruptedImport = Capture();
            failed.WorkState = TrackWorkState.Failed; _tracks.Remove(failed);
            Restore(interruptedImport);
            RequireFileDrop(!_tracks.Contains(failed) && _tracks.All(track => track.WorkState != TrackWorkState.Analyzing),
                "Settings Undo resurrected a failed import placeholder without a source worker.");
            VerifyFailedImportJoinSmoke();
        }
        finally
        {
            ClearFileListDropMarker(); Timeline.PreviewFileDrop(new DataObject(), new Point());
            CancelInterruptedPlayback(); _importCts?.Cancel(); _renderCts?.Cancel();
            while (_importing) await Task.Delay(10);
            _renderCts?.Cancel(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session);
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty;
            if (original) OriginalRadio.IsChecked = true; else if (compare) CompareRadio.IsChecked = true; else PreviewRadio.IsChecked = true;
            Timeline.Playhead = playhead; Timeline.FitAll();
            if (selected != null) SelectTrack(selected);
            UpdateTime(); StatusText.Text = status;
            Directory.Delete(folder, true);
        }
    }

    private void VerifyFailedImportJoinSmoke()
    {
        var album = _tracks.ToArray();
        var authoredProject = _projectEnvelope.ToArray(); var suggestedProject = _projectSuggestion.ToArray();
        static AudioTrack CachedJoinTrack(bool loudEnd) => new()
        {
            Original = new float[8000 * 12], SampleRate = 8000, Channels = 1,
            Envelope = [new(0, .5), new(12, .5)],
            Analysis = new AnalysisResult
            {
                IntegratedLufs = -20,
                ShortTerm = Enumerable.Range(0, 25).Select(index => new LoudnessFrame(index * .5,
                    loudEnd ? index >= 12 ? -15 : -20 : index <= 12 ? -25 : -20)).ToList()
            }
        };
        try
        {
            var a = CachedJoinTrack(true); var b = CachedJoinTrack(false);
            var c = CachedJoinTrack(true); var d = CachedJoinTrack(false);
            _tracks.Clear(); _tracks.AddRange([a, b, c, d]); UpdateTransitions();
            var shared = _projectEnvelope.ToArray();
            var authoredA = a.Envelope; var authoredB = b.Envelope;
            var unaffectedC = c.Envelope; var unaffectedD = d.Envelope;
            RequireFileDrop(shared.Any(point => Math.Abs(point.GainDb) > .5) && _tracks.All(track => track.TransitionEnvelope.Count == 0),
                "The failed-import fixture did not seed its shared project curve.");
            var pending = new AudioTrack { WorkState = TrackWorkState.Analyzing };
            _tracks.Insert(1, pending);
            UpdateTransitions();
            RequireFileDrop(_projectEnvelope.SequenceEqual(shared),
                "A zero-duration pending source changed the existing project's envelope.");
            RemoveFailedImportTrack(pending);
            RequireFileDrop(_tracks.SequenceEqual(new[] { a, b, c, d }) && pending.WorkState == TrackWorkState.Failed &&
                _projectEnvelope.SequenceEqual(shared) &&
                ReferenceEquals(a.Envelope, authoredA) && ReferenceEquals(b.Envelope, authoredB) &&
                ReferenceEquals(c.Envelope, unaffectedC) && ReferenceEquals(d.Envelope, unaffectedD),
                "Removing a failed pending source changed the continuous project curve or unrelated render views.");
        }
        finally { _tracks.Clear(); _tracks.AddRange(album); RestoreProjectEnvelope(authoredProject, suggestedProject); }
    }

    private static void RequireFileDrop(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Smoke test: " + message);
    }
}

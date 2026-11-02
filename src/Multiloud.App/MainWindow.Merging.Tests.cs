using System.IO;
using System.Text.Json;
using System.Windows;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyMergingSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; double position = Timeline.Playhead; var selected = Selected;
        string status = StatusText.Text, title = Title; string? projectPath = _projectPath;
        bool needsCopy = _backgroundProjectNeedsCopy;
        string directory = Path.Combine(Path.GetTempPath(), "Loudline-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Task<bool>? pending = null;
        void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Merge smoke: " + message); }
        try
        {
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            string aPath = Path.Combine(directory, "Album.wav"), bPath = Path.Combine(directory, "Other.wav");
            AudioTrack Decode(string path, double seconds, double frequency)
            {
                WriteProjectRelinkAudio(path, seconds, 2, frequency, .025);
                var source = AudioFileService.Load(path);
                source.OriginalWaveform = WaveformPyramid.Build(source.Original, source.Channels);
                source.OriginalPeaks = BuildDisplayPeaks(source.OriginalWaveform);
                return source;
            }
            var a = await Task.Run(() => Decode(aPath, 6, 233));
            var b = await Task.Run(() => Decode(bPath, 4, 677));
            var halves = AudioTrackSlicer.SplitShared(a, 3);
            AudioTrack[] initial = [halves.Left, halves.Right, b];
            _tracks.Clear(); _tracks.AddRange(initial); _renderKeys.Clear();
            _settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            var background = new BackgroundLayer { Enabled = true, GainDb = -40,
                Clips = [new BackgroundClip { Source = b, StartSeconds = 1, FadeInSeconds = .1 }] };
            _backgroundLayer = background;
            EnvelopePoint[] authored = [new(0, 1), new(1.25, 3), new(3, -2), new(4.5, 1), new(8, -.5), new(10, 0)];
            EnvelopePoint[] suggested = [new(0, 0), new(10, 0)];
            RestoreProjectEnvelope(authored, suggested); LoadCustomBoundaries([2.5, 7.25]);
            var loops = new PlaybackLoopState(true, new(.5, 1.25), true, new(7, 8)); LoadLoops(loops);
            SyncControls(); RefreshList(); QueueRender(); await WaitForSmokePreviewAsync();
            _undo.Clear(); _redo.Clear(); Timeline.Playhead = 3.1;
            int executions = _renderExecutionCount;
            Require(NearestMergeBoundary(initial, .1) == 1 && NearestMergeBoundary(initial, 9) == 2 &&
                NearestMergeBoundary([a], 1) == -1, "The command did not choose the nearest internal boundary.");
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = MergeAtPlayheadAsync(hold.Task);
            Require(_tracks.Count == 2 && ReferenceEquals(_tracks[0].Original, a.Original) && _tracks[0].Duration == 6 &&
                _tracks[0].OriginalWaveform != null && ReferenceEquals(_tracks[1], b),
                "An adjacent shared merge did not remove its boundary immediately without copying audio.");
            hold.SetResult(); Require(await pending, "A shared merge did not complete."); pending = null;
            await WaitForSmokePreviewAsync();
            var joined = _tracks.ToArray();
            Require(_renderExecutionCount == executions && joined[0].Render?.LiveSource != null && _undo.Count == 1 &&
                _projectEnvelope.SequenceEqual(authored) && _projectSuggestion.SequenceEqual(suggested) &&
                CaptureCustomBoundaries().SequenceEqual(new[] { 2.5, 7.25 }) && CaptureLoops() == loops &&
                ReferenceEquals(_backgroundLayer, background) && Timeline.Playhead == 3.1,
                "A bypassed merge processed audio or moved the authored curve, background, loops or export boundaries.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            Require(_tracks.SequenceEqual(initial) && _projectEnvelope.SequenceEqual(authored), "One Undo did not restore both original clips.");
            RedoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            Require(_tracks.SequenceEqual(joined), "Redo did not restore the same merged source.");

            // Different source files must persist as a reconstruction recipe;
            // section-specific manual balances are retained in the leaf gains.
            _tracks[0].InputGainDb = -2; _tracks[0].TrimDb = -.5; _tracks[0].TargetLufs = -18;
            b.InputGainDb = 1; b.TrimDb = .25; b.TargetLufs = -14;
            Timeline.Playhead = 6;
            Require(await MergeAtPlayheadAsync(), "Different source files could not be merged.");
            await WaitForSmokePreviewAsync();
            var composite = _tracks.Single();
            Require(composite.MergedSources is { Count: 2 } && composite.MergedSources[0].GainDb == -2.5 &&
                composite.MergedSources[1].GainDb == 1.25 && composite.InputGainDb == 0 && composite.TrimDb == 0 &&
                composite.TargetLufs == null && composite.Duration == 10 && composite.OriginalWaveform != null &&
                _renderExecutionCount == executions && _projectEnvelope.SequenceEqual(authored),
                "A heterogeneous merge lost manual balance, changed the timeline or invoked bypassed DSP.");
            await SliceTrackAsync(composite, 5); await WaitForSmokePreviewAsync();
            Require(_tracks.Count == 2 && _tracks.All(track => track.MergedSourceId == composite.MergedSourceId) &&
                ReferenceEquals(_tracks[0].MergedSources, _tracks[1].MergedSources), "Slicing lost the merged root recipe.");
            string savedPath = Path.Combine(directory, "Album.loudline");
            var saved = CreateProjectData(savedPath);
            Require(saved.Tracks.All(track => track.AbsolutePath == null && track.MergedSources?.Count == 2),
                "Merged sources depended on an unpersisted temporary render.");
            await File.WriteAllTextAsync(savedPath, JsonSerializer.Serialize(saved));
            await OpenProjectAsync(savedPath, showMissingDialog: false); await WaitForSmokePreviewAsync();
            Require(_tracks.Count == 2 && ReferenceEquals(_tracks[0].Original, _tracks[1].Original) &&
                _tracks[0].SourceStartSeconds == 0 && _tracks[1].SourceStartSeconds == 5 &&
                _tracks.All(track => track.MergedSources?.Count == 2 && PreviewCurrent(track, _settings)) &&
                _projectEnvelope.SequenceEqual(authored) && CaptureLoops() == loops,
                "Saved merged slices could not reconstruct one shared root and retain exact project state.");

            string updatedPath = Path.Combine(directory, "Updated album.wav");
            var updated = await Task.Run(() => Decode(updatedPath, 6, 419));
            var dependent = await Task.Run(() => StageDependentMergedReplacements(aPath, updatedPath, updated,
                CancellationToken.None).ToArray());
            Require(dependent.Length == 2 && ReferenceEquals(dependent[0].Replacement.Original, dependent[1].Replacement.Original) &&
                !ReferenceEquals(dependent[0].Replacement.Original, _tracks[0].Original) && dependent.All(pair =>
                    pair.Replacement.MergedSources![0].Source.FilePath == updatedPath &&
                    pair.Replacement.MergedSources[0].GainDb == -2.5 && pair.Replacement.Duration == pair.Old.Duration &&
                    pair.Replacement.SourceStartSeconds == pair.Old.SourceStartSeconds),
                "Updating a recipe leaf did not rebuild all parent cuts once while preserving authored leaf gains and ranges.");

            // A moved component is listed/relinked individually, while all cuts
            // of the merged parent are republished in one source replacement.
            string moved = Path.Combine(directory, "Moved"); Directory.CreateDirectory(moved);
            File.Move(aPath, Path.Combine(moved, Path.GetFileName(aPath)));
            Require(ExportProtectedSourcePaths().Contains(aPath) && ExportProtectedSourcePaths().Contains(bPath) &&
                !ExportProtectedSourcePaths().Contains(Path.GetFullPath(_tracks[0].FilePath)),
                "Export source protection omitted recipe leaves or treated a virtual merged display name as an external file.");
            bool protectedLeaf = false;
            try { await ExportSingleTrackAsync(_tracks[0], aPath, false); }
            catch (IOException error) when (error.Message.Contains("source", StringComparison.OrdinalIgnoreCase)) { protectedLeaf = true; }
            Require(protectedLeaf && !File.Exists(aPath), "A single-file export recreated a moved recipe leaf at its protected source path.");
            await OpenProjectAsync(savedPath, showMissingDialog: false);
            await WaitForRelinkTracksSmokeAsync(DateTime.UtcNow.AddSeconds(12));
            Require(_tracks.All(track => track.WorkState == TrackWorkState.Missing), "Missing components did not mark their merged parents missing.");
            var recipe = SavedTrack(_tracks[0], savedPath);
            var link = ProjectFileService.Resolve(recipe, savedPath);
            var leaves = ProjectFileService.LeafSources(link).ToArray();
            Require(leaves.Count(leaf => !leaf.IsResolved) == 1, "Missing component reporting listed virtual parent names.");
            await ProjectFileService.SearchFolder(leaves, moved);
            Require(link.IsResolved, "Locating a component folder did not resolve its merged source.");
            Require(await ReplaceSourceFamilyAsync(_tracks[0], _tracks[0].FilePath, mergedRecipe: recipe), "A located recipe was not relinked.");
            await WaitForSmokePreviewAsync();
            Require(_tracks.All(track => PreviewCurrent(track, _settings)) && ReferenceEquals(_tracks[0].Original, _tracks[1].Original) &&
                _projectEnvelope.SequenceEqual(authored) && _renderExecutionCount == executions,
                "Component relink missed a sibling, changed the curve or processed a bypassed source.");

            var beforeCancel = _tracks.ToArray(); var curve = _projectEnvelope.ToArray(); int history = _undo.Count;
            Timeline.Playhead = 5;
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = MergeAtPlayheadAsync(stop.Task);
            Require(_tracks.Count == 1, "The pending merge boundary was not removed immediately.");
            CancelClick(this, new RoutedEventArgs()); Require(!await pending, "A cancelled merge still completed."); pending = null;
            await WaitForSmokePreviewAsync();
            Require(_tracks.SequenceEqual(beforeCancel) && _undo.Count == history && _projectEnvelope.SequenceEqual(curve),
                "Cancelling a published merge left a partial edit or undo frame.");

            _settings = new ProcessingSettings { LevelingEnabled = true, MatchingEnabled = true,
                LimiterEnabled = true, TargetLufs = -22 };
            SyncControls(); QueueRender(); await WaitForSmokePreviewAsync();
            executions = _renderExecutionCount;
            var beforeActiveMerge = _tracks.ToArray();
            var beforeActiveRenders = beforeActiveMerge.Select(track => track.Render).ToArray();
            var beforeActivePeaks = beforeActiveMerge.Select(track => track.PreviewPeaks).ToArray();
            var beforeActiveKeys = beforeActiveMerge.Select(track => RenderKey(track, _settings)).ToArray();
            Require(await MergeAtPlayheadAsync(), "An active processing merge failed."); await WaitForSmokePreviewAsync();
            Require(_tracks.Count == 1 && _tracks[0].Analysis != null && PreviewCurrent(_tracks[0], _settings) &&
                _renderExecutionCount == executions + 1 && _projectEnvelope.SequenceEqual(authored),
                "An active merge did not analyze/render exactly its new clip or rewrote the authored envelope.");
            var activeMerged = _tracks.Single(); var activeMergedRender = activeMerged.Render;
            var activeMergedPeaks = activeMerged.PreviewPeaks;
            int mergedExecutions = _renderExecutionCount;
            // Reproduce a large album whose previous variants no longer fit
            // the LRU. Undo still owns the removed source objects and therefore
            // their ready Results; it must not process those buffers again.
            _previewCache.Clear();
            UndoClick(this, new RoutedEventArgs());
            Require(_tracks.SequenceEqual(beforeActiveMerge) && _renderedRevision == _revision && ExportButton.IsEnabled &&
                _tracks.Select((track, index) => track.WorkState == TrackWorkState.Idle && PreviewCurrent(track, _settings) &&
                    ReferenceEquals(track.Render, beforeActiveRenders[index]) && ReferenceEquals(track.PreviewPeaks, beforeActivePeaks[index]) &&
                    RenderKey(track, _settings) == beforeActiveKeys[index]).All(value => value),
                "Merge Undo did not immediately restore the exact ready Results after cache eviction.");
            _previewCache.Clear();
            RedoClick(this, new RoutedEventArgs());
            Require(_tracks.Count == 1 && ReferenceEquals(_tracks[0], activeMerged) &&
                ReferenceEquals(activeMerged.Render, activeMergedRender) && ReferenceEquals(activeMerged.PreviewPeaks, activeMergedPeaks) &&
                _renderedRevision == _revision && PreviewCurrent(activeMerged, _settings) && ExportButton.IsEnabled,
                "Merge Redo did not immediately restore its already completed Result after cache eviction.");
            await Task.Delay(650);
            Require(_renderExecutionCount == mergedExecutions,
                "An instant Merge Undo/Redo still scheduled redundant background processing.");

            _previewCache.Clear(); UndoClick(this, new RoutedEventArgs());
            Require(await MergeAtPlayheadAsync(), "An unfinished-render Merge fixture could not publish.");
            await _renderGate.WaitAsync();
            try
            {
                _previewCache.Clear(); UndoClick(this, new RoutedEventArgs());
                Require(_tracks.SequenceEqual(beforeActiveMerge) && _renderedRevision == _revision &&
                    _tracks.Select((track, index) => PreviewCurrent(track, _settings) &&
                        ReferenceEquals(track.Render, beforeActiveRenders[index])).All(value => value),
                    "Undo while a merged preview was pending did not immediately restore both ready files.");
            }
            finally { _renderGate.Release(); }
            await Task.Delay(650);
            Require(_renderExecutionCount == mergedExecutions,
                "An obsolete merged preview ran after Undo restored the previous files.");
        }
        finally
        {
            if (pending is { IsCompleted: false }) { _importCts?.Cancel(); await pending; }
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty; _projectPath = projectPath; _backgroundProjectNeedsCopy = needsCopy; Title = title;
            Timeline.Playhead = position; StatusText.Text = status;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            Directory.Delete(directory, true);
        }
    }
}

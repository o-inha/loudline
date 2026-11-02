using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifySourceReplacementSmokeAsync()
    {
        await WaitForSourceEditPreviewSmokeAsync("Source replacement: fixture entry");
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; double position = Timeline.Playhead; var selection = Selected;
        string status = StatusText.Text;
        string? projectPath = _projectPath; string title = Title;
        bool projectNeedsCopy = _backgroundProjectNeedsCopy;
        string directory = Path.Combine(Path.GetTempPath(), "Loudline-source-replacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Exception? fixtureFailure = null;
        Task<bool>? pending = null;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Source replacement smoke: " + message); }
        try
        {
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            foreach (var track in _tracks) RememberActivePreview(track);
            string originalPath = Path.Combine(directory, "Original album.wav");
            string updatedPath = Path.Combine(directory, "Updated album.wav");
            string shortPath = Path.Combine(directory, "Incomplete source.wav");
            string otherPath = Path.Combine(directory, "Unrelated.wav");
            WriteProjectRelinkAudio(originalPath, 9, 2, 233, .04);
            WriteProjectRelinkAudio(updatedPath, 9, 2, 397, .025);
            WriteProjectRelinkAudio(shortPath, 8, 2, 397, .025);
            WriteProjectRelinkAudio(otherPath, 2, 2, 677, .035);
            AudioTrack Decode(string path)
            {
                var source = AudioFileService.Load(path);
                source.OriginalWaveform = WaveformPyramid.Build(source.Original, source.Channels);
                source.OriginalPeaks = BuildDisplayPeaks(source.OriginalWaveform);
                return source;
            }
            var source = await Task.Run(() => Decode(originalPath));
            var other = await Task.Run(() => Decode(otherPath));
            var firstCut = AudioTrackSlicer.SplitShared(source, 3);
            var secondCut = AudioTrackSlicer.SplitShared(firstCut.Right, 3);
            AudioTrack[] family = [firstCut.Left, secondCut.Left, secondCut.Right];
            for (int index = 0; index < family.Length; index++)
            { family[index].InputGainDb = index - 1; family[index].TrimDb = index * .2; family[index].TargetLufs = -20 + index; }
            _tracks.Clear(); _tracks.AddRange([family[2], other, family[0], family[1]]); _renderKeys.Clear();
            _settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            var background = new BackgroundLayer { GainDb = -50, Clips =
                [new BackgroundClip { Source = other, StartSeconds = .25, FadeInSeconds = .1, FadeOutSeconds = .2 }] };
            _backgroundLayer = background;
            EnvelopePoint[] authored = [new(0, .5), new(1.125, 2.25), new(4.75, -.75), new(8.125, 1.5), new(11, -.25)];
            EnvelopePoint[] suggested = [new(0, 0), new(11, 0)];
            RestoreProjectEnvelope(authored, suggested); LoadCustomBoundaries([1.25, 4.5, 8.25]);
            SyncControls(); RefreshList(); QueueRender(); await WaitForSourceEditPreviewSmokeAsync("Source replacement: bypassed source setup");
            _undo.Clear(); _redo.Clear(); Timeline.Playhead = 7.25; SelectTrack(family[0]);
            var oldOrder = _tracks.ToArray(); var oldRenders = oldOrder.Select(track => track.Render).ToArray();
            var slotIds = oldOrder.Select(track => track.Id).ToArray();
            var oldStarts = family.Select(track => track.SourceStartSeconds).ToArray();
            var oldLengths = family.Select(track => track.SourceDurationSeconds).ToArray();
            int executions = _renderExecutionCount;
            Require(CreateTrackContextMenu(family[0]).Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Relink…"),
                "A ready sliced source did not expose Relink.");
            Require(await ReplaceSourceFamilyAsync(family[0], updatedPath), "The source family was not replaced.");
            await WaitForSourceEditPreviewSmokeAsync("Source replacement: bypassed source replacement");
            var replacedOrder = _tracks.ToArray();
            var replacements = slotIds.Where(id => family.Any(track => track.Id == id))
                .Select(id => _tracks.Single(track => track.Id == id)).ToArray();
            Require(_tracks.Select(track => track.Id).SequenceEqual(slotIds) && ReferenceEquals(_tracks[1], other) &&
                replacements.Length == 3 && replacements.All(track => track.FilePath == updatedPath && track.Analysis == null && track.Render?.LiveSource != null) &&
                replacements.All(track => ReferenceEquals(track.Original, replacements[0].Original) &&
                    ReferenceEquals(track.OriginalWaveform?.SharedSummaryRoot, replacements[0].OriginalWaveform?.SharedSummaryRoot)) &&
                !ReferenceEquals(replacements[0].Original, source.Original) && _renderExecutionCount == executions && _undo.Count == 1,
                "Relink changed order, missed a nonadjacent sibling, copied shared ranges or processed a bypassed mix.");
            foreach (var old in family)
            {
                var current = _tracks.Single(track => track.Id == old.Id);
                Require(current.SourceId != old.SourceId && current.SourceStartSeconds == old.SourceStartSeconds &&
                    current.SourceDurationSeconds == old.SourceDurationSeconds && current.Duration == old.Duration &&
                    current.InputGainDb == old.InputGainDb && current.TrimDb == old.TrimDb && current.TargetLufs == old.TargetLufs,
                    "A slice lost its source range, independent controls or new source identity.");
            }
            Require(_projectEnvelope.SequenceEqual(authored) && _projectSuggestion.SequenceEqual(suggested) &&
                ReferenceEquals(_backgroundLayer, background) && CaptureCustomBoundaries().SequenceEqual(new[] { 1.25, 4.5, 8.25 }) &&
                Timeline.Playhead == 7.25, "Relink changed the authored envelope, export markers, background or playhead.");
            var renders = replacedOrder.Select(track => track.Render).ToArray();
            UndoClick(this, new RoutedEventArgs()); await WaitForSourceEditPreviewSmokeAsync("Source replacement: source replacement Undo");
            Require(_tracks.SequenceEqual(oldOrder) && _tracks.Select((track, index) => ReferenceEquals(track.Render, oldRenders[index])).All(same => same) &&
                _projectEnvelope.SequenceEqual(authored), "Undo did not restore the entire previous source family and exact previews.");
            RedoClick(this, new RoutedEventArgs()); await WaitForSourceEditPreviewSmokeAsync("Source replacement: source replacement Redo");
            Require(_tracks.SequenceEqual(replacedOrder) && _tracks.Select((track, index) => ReferenceEquals(track.Render, renders[index])).All(same => same),
                "Redo did not restore the family as one cached edit.");

            // Fresh source analysis must not silently act as Suggest when the
            // source was replaced while the processing modules were bypassed.
            _settings = _settings with { LevelingEnabled = true, MatchingEnabled = true, LimiterEnabled = true, TargetLufs = -20 };
            SyncControls(); QueueRender(); await WaitForSourceEditPreviewSmokeAsync("Source replacement: enable all modules after bypass");
            Require(_projectEnvelope.SequenceEqual(authored) && _tracks.All(track => track.Analysis != null && PreviewCurrent(track, _settings)),
                "Enabling processing after bypassed Relink rewrote the saved project envelope.");
            var unrelatedResult = other.Render;
            var beforeActive = _tracks.ToArray();
            var previousRoot = _tracks[0].Original;
            // A DAW may overwrite the same source path. Re-selecting it must
            // still decode new samples once, with fresh identities for all cuts.
            File.Delete(updatedPath); WriteProjectRelinkAudio(updatedPath, 9, 2, 941, .02);
            executions = _renderExecutionCount;
            await RelinkTrackAsync(_tracks[0], updatedPath); await WaitForSourceEditPreviewSmokeAsync("Source replacement: active same-path source replacement");
            Require(_tracks.Where(track => track.FilePath == updatedPath).All(track => !ReferenceEquals(track.Original, previousRoot) &&
                ReferenceEquals(track.Original, _tracks[0].Original) && track.Analysis != null && PreviewCurrent(track, _settings)) &&
                _renderExecutionCount - executions == 3 && ReferenceEquals(other.Render, unrelatedResult) && _projectEnvelope.SequenceEqual(authored),
                "Same-path active Relink reused old samples, re-rendered unrelated music or changed the volume curve.");
            Require(_tracks.Where(track => track.FilePath == updatedPath).All(track => !beforeActive.Contains(track)),
                "A source replacement reused old mutable track objects.");

            var stable = _tracks.ToArray(); var stableRenders = stable.Select(track => track.Render).ToArray();
            int history = _undo.Count; bool dirtyBeforeFailure = _dirty;
            bool rejected = false;
            try { await ReplaceSourceFamilyAsync(_tracks[0], shortPath); }
            catch (InvalidOperationException error) { rejected = error.Message.Contains("does not cover", StringComparison.Ordinal); }
            await WaitForSourceEditPreviewSmokeAsync("Source replacement: rejected incomplete source");
            Require(rejected && _tracks.SequenceEqual(stable) && _undo.Count == history && _dirty == dirtyBeforeFailure &&
                _tracks.Select((track, index) => ReferenceEquals(track.Render, stableRenders[index])).All(same => same),
                "An incomplete full-source render partially replaced the family or silently shortened a slice.");

            // Cancellation after decoding/analysis, before atomic publication,
            // must keep every ready source and its cached preview untouched.
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = ReplaceSourceFamilyAsync(_tracks[0], originalPath, () => { staged.TrySetResult(); return hold.Task; });
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(_importing && _atomicSourceOperation, "Relink did not own an atomic cancellable source operation.");
            CancelClick(this, new RoutedEventArgs());
            Require(!await pending, "A cancelled replacement still published new sources."); pending = null;
            await WaitForSourceEditPreviewSmokeAsync("Source replacement: cancelled source replacement");
            Require(_tracks.SequenceEqual(stable) && _undo.Count == history && _dirty == dirtyBeforeFailure &&
                _tracks.All(track => track.WorkState == TrackWorkState.Idle) && _projectEnvelope.SequenceEqual(authored),
                "Cancel left a partial replacement, unprocessed state or an extra undo frame.");
            var saved = JsonSerializer.Deserialize<ProjectData>(JsonSerializer.Serialize(CreateProjectData(Path.Combine(directory, "Saved.loudline"))))!;
            Require(saved.Tracks.Where(track => track.AbsolutePath == updatedPath).Count() == 3 &&
                saved.Tracks.Where(track => track.AbsolutePath == updatedPath).Select(track => track.SourceStartSeconds).Order().SequenceEqual(oldStarts.Order()) &&
                saved.Tracks.Where(track => track.AbsolutePath == updatedPath).Select(track => track.SourceDurationSeconds).Order().SequenceEqual(oldLengths.Order()),
                "Saving a replaced source lost sibling paths or the original cut positions.");
            string savedPath = Path.Combine(directory, "Saved.loudline");
            await File.WriteAllTextAsync(savedPath, JsonSerializer.Serialize(saved));
            await OpenProjectAsync(savedPath, showMissingDialog: false); await WaitForSourceEditPreviewSmokeAsync("Source replacement: reopen replaced-source project");
            Require(_tracks.Count == 4 && _tracks[1].FilePath == otherPath &&
                _tracks.Where(track => track.FilePath == updatedPath).Select(track => track.SourceStartSeconds).Order().SequenceEqual(oldStarts.Order()) &&
                _projectEnvelope.SequenceEqual(authored) && _tracks.All(track => PreviewCurrent(track, _settings)),
                "Reopening the replaced source project lost cuts, order or the authored curve.");
            var missingFamily = _tracks.Where(track => track.FilePath == updatedPath).ToArray();
            foreach (var missing in missingFamily) { MarkUnavailable(missing); _renderKeys.Remove(missing.Id); }
            RestoreProjectEnvelope(authored, _projectSuggestion.ToArray()); RefreshList();
            await RelinkTrackAsync(missingFamily[0], originalPath); await WaitForSourceEditPreviewSmokeAsync("Source replacement: relink missing source family");
            Require(_tracks.Where(track => missingFamily.Any(old => old.Id == track.Id)).All(track =>
                track.FilePath == originalPath && track.Analysis != null && track.WorkState == TrackWorkState.Idle &&
                ReferenceEquals(track.Original, _tracks[0].Original)) &&
                _projectEnvelope.SequenceEqual(authored) && _tracks[1].FilePath == otherPath,
                "Relinking one missing slice did not replace every missing sibling from the same full source.");
            VerifyReplacementPlaybackIntentSmoke();
        }
        catch (Exception error) { fixtureFailure = error; throw; }
        finally
        {
            if (pending is { IsCompleted: false }) { _importCts?.Cancel(); await pending; }
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session); try { await WaitForSourceEditPreviewSmokeAsync("Source replacement: restore prior session"); }
            catch (Exception cleanup) when (fixtureFailure != null)
            { throw new AggregateException("Source replacement fixture and cleanup both failed.", fixtureFailure, cleanup); }
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty; _projectPath = projectPath; _backgroundProjectNeedsCopy = projectNeedsCopy; Title = title;
            Timeline.Playhead = position; StatusText.Text = status;
            if (selection != null && _tracks.Contains(selection)) SelectTrack(selection);
            Directory.Delete(directory, true);
        }
    }

    private void VerifyReplacementPlaybackIntentSmoke()
    {
        bool? original = OriginalRadio.IsChecked, comparison = CompareRadio.IsChecked;
        bool? delta = DeltaCheck.IsChecked, solo = BackgroundSoloButton.IsChecked;
        bool syncing = _syncing;
        double position = _tracks[0].Duration / 2;
        try
        {
            SlicePlaybackIntent? listening = new(position, true, false, false, _importNavigationVersion);
            CancelSourceWorkForReplacement(ref listening);
            if (listening is not { Playing: true } || listening.Position != position ||
                listening.NavigationVersion != _importNavigationVersion)
                throw new InvalidOperationException("Source replacement smoke: an internal preparation stop discarded playback intent.");
            CancelSourceWorkForReplacement(ref listening);
            if (listening is not { Playing: true } || listening.NavigationVersion != _importNavigationVersion)
                throw new InvalidOperationException("Source replacement smoke: atomic publication discarded playback intent.");
            CancelInterruptedPlayback();
            CancelSourceWorkForReplacement(ref listening);
            if (listening != null)
                throw new InvalidOperationException("Source replacement smoke: a manual Stop was overridden by delayed preparation.");

            _syncing = true;
            BackgroundSoloButton.IsChecked = false; OriginalRadio.IsChecked = true; DeltaCheck.IsChecked = true;
            _syncing = syncing; SyncPeakDeltaControls(); PublishPlaybackTracks();
            ArmPlaybackBeforeStructure(position, true, false);
            if (_interruptedPlayback is not { } pending || _backgroundOriginalResume != null ||
                pending.TrackId != _tracks[0].Id)
                throw new InvalidOperationException("Source replacement smoke: Delta in Original view armed the unprocessed route.");
            var rollback = CaptureRollbackPlaybackIntent();
            if (rollback is not { Original: false })
                throw new InvalidOperationException("Source replacement smoke: Delta rollback captured an Original listening mode.");
            double restored = -1;
            RestoreRollbackPlaybackIntent(rollback, value => restored = value);
            if (Math.Abs(restored - position) > .000001)
                throw new InvalidOperationException("Source replacement smoke: Delta rollback did not restore the audible position.");
            CancelInterruptedPlayback();
            var deltaSlice = new SlicePlaybackIntent(position, true, false, false, _importNavigationVersion);
            if (!RearmSlicePlaybackAfterPreparation(deltaSlice) || _interruptedPlayback == null || _backgroundOriginalResume != null)
                throw new InvalidOperationException("Source replacement smoke: slice/relink preparation lost Delta playback in Original view.");
        }
        finally
        {
            CancelInterruptedPlayback(); _backgroundOriginalResume = null;
            _syncing = true;
            DeltaCheck.IsChecked = delta; BackgroundSoloButton.IsChecked = solo;
            OriginalRadio.IsChecked = original; CompareRadio.IsChecked = comparison;
            if (original != true && comparison != true) PreviewRadio.IsChecked = true;
            _syncing = syncing; SyncPeakDeltaControls(); PublishPlaybackTracks();
        }
    }

}

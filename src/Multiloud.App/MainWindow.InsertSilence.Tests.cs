using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyInsertSilenceSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; double position = Timeline.Playhead; var selected = Selected;
        string status = StatusText.Text, title = Title; string? projectPath = _projectPath;
        bool projectNeedsCopy = _backgroundProjectNeedsCopy;
        double previousDuration = _silenceDurationSeconds; var previousPlacement = _silencePlacement;
        string directory = Path.Combine(Path.GetTempPath(), "Loudline-insert-silence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Task<bool>? pending = null;
        void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Insert silence smoke: " + message); }
        try
        {
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            string[] files = Enumerable.Range(0, 3).Select(index => Path.Combine(directory, $"Music {index + 1}.wav")).ToArray();
            AudioTrack[] music = await Task.Run(() => files.Select((path, index) =>
            {
                WriteProjectRelinkAudio(path, index + 2, 2, 233 + index * 127, .03);
                var track = AudioFileService.Load(path);
                track.OriginalWaveform = WaveformPyramid.Build(track.Original, track.Channels);
                track.OriginalPeaks = BuildDisplayPeaks(track.OriginalWaveform);
                return track;
            }).ToArray());
            _tracks.Clear(); _tracks.AddRange(music); _renderKeys.Clear(); _backgroundLayer = new();
            _settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            EnvelopePoint[] authored = [new(0, 1), new(1.25, 3), new(2, -1), new(3.5, 2), new(5, 0), new(7.25, -2), new(9, 1)];
            RestoreProjectEnvelope(authored, [new(0, 0), new(9, 0)]);
            LoadCustomBoundaries([1.5, 6.25]); SyncControls(); RefreshList(); QueueRender(); await WaitForSmokePreviewAsync();
            _undo.Clear(); _redo.Clear(); Timeline.Playhead = 2.6;
            int executions = _renderExecutionCount;

            Require(SilenceInsertionIndices(music, SilencePlacement.Start, 4).SequenceEqual(new[] { 0 }) &&
                SilenceInsertionIndices(music, SilencePlacement.End, 4).SequenceEqual(new[] { 3 }) &&
                SilenceInsertionIndices(music, SilencePlacement.NearestBoundary, .1).SequenceEqual(new[] { 1 }) &&
                SilenceInsertionIndices(music, SilencePlacement.NearestBoundary, 4.75).SequenceEqual(new[] { 2 }) &&
                SilenceInsertionIndices(music, SilencePlacement.AllBoundaries, 4).SequenceEqual(new[] { 1, 2 }) &&
                SilenceInsertionIndices([music[0]], SilencePlacement.NearestBoundary, .25).SequenceEqual(new[] { 0 }) &&
                SilenceInsertionIndices([music[0]], SilencePlacement.NearestBoundary, 1.75).SequenceEqual(new[] { 1 }),
                "Start, end, nearest interior boundary or all-boundaries placement is incorrect.");

            bool closed = false;
            var content = CreateInsertSilenceContent(() => closed = true);
            foreach (string name in new[] { "SilenceStartRadio", "SilenceNearestRadio", "SilenceAllRadio", "SilenceEndRadio" })
                Require(content.FindName(name) is RadioButton, "The placement choices are not a button group.");
            var duration = (TextBox)content.FindName("SilenceDurationBox");
            var apply = (Button)content.FindName("InsertSilenceApplyButton");
            duration.Text = "invalid"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!closed && _tracks.Count == 3 && _undo.Count == 0, "An invalid duration closed the panel or changed audio.");
            duration.Text = "1.05"; ((RadioButton)content.FindName("SilenceNearestRadio")).IsChecked = true;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitForSmokePreviewAsync();
            Require(closed && _tracks.Count == 4 && ReferenceEquals(_tracks[0], music[0]) &&
                _tracks[1].GeneratedSilenceSeconds == 1.05 && _tracks[1].Duration == 1.05 &&
                ReferenceEquals(_tracks[2], music[1]) && ReferenceEquals(_tracks[3], music[2]) &&
                _tracks[1].Original.All(sample => sample == 0) && _tracks[1].Render?.LiveSource != null &&
                _renderExecutionCount == executions && _undo.Count == 1,
                "Decimal silence was not inserted at the nearest boundary as one live-bypass edit.");
            for (double time = 0; time < 9; time += .137)
            {
                double shifted = time < 2 ? time : time + 1.05;
                Require(Math.Abs(AudioProcessor.EnvelopeGainAt(_projectEnvelope, shifted) - AudioProcessor.EnvelopeGainAt(authored, time)) < 1e-9,
                    "An inserted gap rewrote an existing music envelope instead of moving it with the music.");
            }
            Require(CaptureCustomBoundaries().SequenceEqual(new[] { 1.5, 6.25 }), "Insertion moved absolute export markers.");
            var withNearest = _tracks.ToArray(); var curveWithNearest = _projectEnvelope.ToArray();
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            Require(_tracks.SequenceEqual(music) && _projectEnvelope.SequenceEqual(authored), "One Undo did not remove the silence and restore the exact curve.");
            RedoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            Require(_tracks.SequenceEqual(withNearest) && _projectEnvelope.SequenceEqual(curveWithNearest), "Redo did not restore the same generated source and authored curve.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();

            Timeline.Playhead = 0;
            Require(await InsertSilenceAsync(SilencePlacement.Start, 1.5), "Start silence was not inserted.");
            await WaitForSmokePreviewAsync();
            Require(_tracks[0].GeneratedSilenceSeconds == 1.5 && Timeline.Playhead == 0 && _tracks.Skip(1).SequenceEqual(music),
                "Start silence did not precede track one or moved a stopped playhead away from the start.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            Timeline.Playhead = 9;
            Require(await InsertSilenceAsync(SilencePlacement.End, .75), "End silence was not inserted.");
            await WaitForSmokePreviewAsync();
            Require(_tracks[^1].GeneratedSilenceSeconds == .75 && _tracks.Take(3).SequenceEqual(music) && Timeline.Playhead == 9,
                "End silence did not follow the final music track.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();

            var beforeCancel = _tracks.ToArray(); int history = _undo.Count;
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = InsertSilenceAsync(SilencePlacement.Start, 2, hold.Task);
            await Task.Delay(30); CancelClick(this, new RoutedEventArgs());
            Require(!await pending, "A cancelled silence operation still published a source."); pending = null;
            await WaitForSmokePreviewAsync();
            Require(_tracks.SequenceEqual(beforeCancel) && _undo.Count == history && _projectEnvelope.SequenceEqual(authored),
                "Cancelling silence preparation left a partial source or an undo frame.");

            Require(await InsertSilenceAsync(SilencePlacement.AllBoundaries, .5), "All-boundaries silence was not inserted.");
            await WaitForSmokePreviewAsync();
            Require(_tracks.Count == 5 && _tracks[1].GeneratedSilenceSeconds == .5 && _tracks[3].GeneratedSilenceSeconds == .5 &&
                ReferenceEquals(_tracks[1].Original, _tracks[3].Original) && _tracks.Where(track => !track.GeneratedSilenceSeconds.HasValue).SequenceEqual(music) &&
                _renderExecutionCount == executions,
                "All-boundaries insertion copied silent PCM, inserted at endpoints or processed unchanged music in live bypass.");
            Require(!CreateTrackContextMenu(_tracks[1]).Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Relink…"),
                "Project-owned silence offered a meaningless external-file relink.");

            var silenceToSlice = _tracks[1];
            await SliceTrackAsync(silenceToSlice, .2); await WaitForSmokePreviewAsync();
            Require(_tracks.Count == 6 && _tracks[1].GeneratedSilenceSeconds == .5 && _tracks[2].GeneratedSilenceSeconds == .5 &&
                _tracks[1].Duration == .2 && _tracks[2].Duration == .3 && _tracks[2].SourceStartSeconds == .2,
                "A generated silence cut lost its rooted source metadata.");
            var savedCurve = _projectEnvelope.ToArray();
            string savedPath = Path.Combine(directory, "Album.loudline");
            var project = CreateProjectData(savedPath);
            Require(project.Tracks.Count(track => track.GeneratedSilenceSeconds.HasValue) == 3 &&
                project.Tracks.Where(track => track.GeneratedSilenceSeconds.HasValue).All(track => track.AbsolutePath == null),
                "Generated sources were saved as external absolute WAV dependencies.");
            await File.WriteAllTextAsync(savedPath, JsonSerializer.Serialize(project));
            // An unrelated same-name WAV must never override a generated source.
            await File.WriteAllTextAsync(Path.Combine(directory, "Silence.wav"), "This is not audio.");
            await OpenProjectAsync(savedPath, showMissingDialog: false); await WaitForSmokePreviewAsync();
            Require(_tracks.Count == 6 && _tracks.All(track => track.WorkState == TrackWorkState.Idle && PreviewCurrent(track, _settings)) &&
                _tracks.Where(track => track.GeneratedSilenceSeconds.HasValue).All(track => track.Original.All(sample => sample == 0)) &&
                _tracks[2].SourceStartSeconds == .2 && _tracks[2].Duration == .3 && _projectEnvelope.SequenceEqual(savedCurve),
                "Reopening failed to reconstruct generated slices independently from real files or lost the authored envelope.");

            string backgroundPath = Path.Combine(directory, "Background.wav");
            WriteProjectRelinkAudio(backgroundPath, 12, 2, 111, .005);
            var background = AudioFileService.Load(backgroundPath);
            _backgroundLayer = new() { Enabled = true, GainDb = -10, Clips = [new() { Source = background }] };
            RefreshBackground(); QueueRender(); await WaitForSmokePreviewAsync();
            var gap = _tracks.First(track => track.GeneratedSilenceSeconds.HasValue);
            var live = gap.Render!.LiveSource!;
            var heard = new float[512]; live.CreateReader(48000, 2).Read(0, heard, 0, 256);
            var expected = new float[512]; new BackgroundRenderer.SegmentReader(_backgroundLayer, 48000, 2)
                .Read(BackgroundAlbumStart(gap), expected, 0, 256);
            Require(heard.AsSpan().SequenceEqual(expected) && heard.Any(sample => sample != 0),
                "Inserted music silence muted or adjusted the continuous fixed background.");
        }
        finally
        {
            if (pending is { IsCompleted: false }) { _importCts?.Cancel(); await pending; }
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty; _projectPath = projectPath; _backgroundProjectNeedsCopy = projectNeedsCopy; Title = title;
            _silenceDurationSeconds = previousDuration; _silencePlacement = previousPlacement;
            Timeline.Playhead = position; StatusText.Text = status;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            Directory.Delete(directory, true);
        }
    }
}

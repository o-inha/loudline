using System.IO;
using System.Text.Json;
using System.Windows;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifySlicingSmokeAsync()
    {
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        string? oldPath = _projectPath; string oldTitle = Title;
        bool oldDirty = _dirty, oldNeedsCopy = _backgroundProjectNeedsCopy;
        double oldPlayhead = Timeline.Playhead; var oldSelected = Selected;
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineSliceSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Exception? fixtureFailure = null;
        static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Slice smoke: " + message); }
        try
        {
            const int rate = 12000;
            float[] samples = new float[rate * 3];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)(Math.Sin(2 * Math.PI * 550 * i / rate) * (i < rate * 1.25 ? .03 : .2));
            string sourcePath = Path.Combine(directory, "Unchanged source.wav");
            AudioFileService.ExportWav(new AudioTrack { FilePath = sourcePath + ".original", SampleRate = rate, Channels = 1,
                Render = new RenderResult { Samples = samples } }, sourcePath, true);
            string projectPath = Path.Combine(directory, "Slices.loudline");
            var data = new ProjectData
            {
                Settings = new ProcessingSettings { TargetLufs = -20, Strength = .65, LimiterEnabled = false },
                Tracks = [new ProjectTrack { Path = Path.GetFileName(sourcePath), Duration = 3, SampleRate = rate, Channels = 1,
                    Envelope = [new(0, 0), new(1, 2), new(3, 0)], TransitionEnvelope = [] }],
                ProjectEnvelope = [new(0, 0), new(1, 2), new(3, 0)],
                ProjectEnvelopeSuggestion = [new(0, 0), new(3, 0)]
            };
            await File.WriteAllTextAsync(projectPath, JsonSerializer.Serialize(data));
            await OpenProjectAsync(projectPath, false); await WaitForSourceEditPreviewSmokeAsync("Slicing: open slice source");
            var source = _tracks.Single(); var authored = _projectEnvelope.ToArray();
            Check(!Timeline.SliceMode && Equals(SliceButton.ToolTip, "Slice at playhead position (S)"), "Slicing retained a cursor tool or lost its playhead tooltip.");
            var beforeCancelRender = source.Render;
            var canceledSlice = SliceTrackAsync(source, 1.25);
            CancelClick(this, new RoutedEventArgs());
            await canceledSlice;
            Check(_tracks.Count == 1 && ReferenceEquals(_tracks[0], source) && ReferenceEquals(source.Render, beforeCancelRender) &&
                PreviewCurrent(source, _settings) && !_processingPaused,
                "Cancel during slice analysis changed the original ready audio or paused unrelated processing.");
            // Source preparation is independent of the render gate. Hold exact
            // DSP here so this assertion tests a pending render deterministically
            // instead of depending on a wall-clock coalescing delay.
            await _renderGate.WaitAsync();
            try
            {
                await SliceTrackAsync(source, 1.25);
                CancelClick(this, new RoutedEventArgs());
            }
            finally { _renderGate.Release(); }
            await WaitForSourceEditPreviewSmokeAsync("Slicing: cancel pending slice render");
            Check(_tracks.Count == 1 && ReferenceEquals(_tracks[0], source) && ReferenceEquals(source.Render, beforeCancelRender),
                "Cancel during slice rendering did not restore the unsplit track and its cached audio.");
            Timeline.Playhead = 1.25;
            var command = SliceAtPlayheadAsync();
            Check(_tracks.Count == 2 && !Timeline.SliceMode, "The playhead slice command did not immediately publish a cut while retaining normal scrubbing.");
            await command; await WaitForSourceEditPreviewSmokeAsync("Slicing: slice at playhead");
            Check(_tracks.Count == 2 && _tracks[0].OriginalRegion.ToArray().Concat(_tracks[1].OriginalRegion.ToArray()).SequenceEqual(samples),
                "A slice lost source samples or did not create two independent tracks.");
            Check(_tracks[0].Analysis != null && _tracks[1].Analysis != null &&
                !ReferenceEquals(_tracks[0].Analysis, _tracks[1].Analysis) && _tracks.All(track => track.Render != null && PreviewCurrent(track, _settings)),
                "A slice reused whole-file analysis or failed to render independently.");
            Check(Math.Abs(_tracks[0].Render!.OutputLufs + 20) < .12 && Math.Abs(_tracks[1].Render!.OutputLufs + 20) < .12,
                "The sliced files did not receive individual LUFS matching.");
            for (int i = 0; i <= 30; i++) Check(Math.Abs(AudioProcessor.EnvelopeGainAt(_projectEnvelope, i / 10.0) -
                AudioProcessor.EnvelopeGainAt(authored, i / 10.0)) < 1e-8, "Slicing changed the continuous project envelope.");
            var left = _tracks[0]; var right = _tracks[1];
            Check(left.SourceStartSeconds == 0 && left.SourceDurationSeconds == 1.25 &&
                right.SourceStartSeconds == 1.25 && right.SourceDurationSeconds == 1.75,
                "The persisted source regions do not match the slice position.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSourceEditPreviewSmokeAsync("Slicing: slice Undo");
            Check(_tracks.Count == 1 && ReferenceEquals(_tracks[0], source), "Undo did not restore the whole source.");
            RedoClick(this, new RoutedEventArgs()); await WaitForSourceEditPreviewSmokeAsync("Slicing: slice Redo");
            Check(_tracks.Count == 2 && ReferenceEquals(_tracks[0], left) && ReferenceEquals(_tracks[1], right),
                "Redo did not restore the independent slice identities.");
            await SliceTrackAsync(right, .75); await WaitForSourceEditPreviewSmokeAsync("Slicing: repeated slice");
            Check(_tracks.Count == 3 && _tracks[2].SourceStartSeconds == 2 &&
                _tracks.SelectMany(track => track.OriginalRegion.ToArray()).SequenceEqual(samples), "A repeated slice drifted from its original source.");
            var saved = CreateProjectData(projectPath);
            await File.WriteAllTextAsync(projectPath, JsonSerializer.Serialize(saved));
            await OpenProjectAsync(projectPath, false); await WaitForSourceEditPreviewSmokeAsync("Slicing: reopen sliced project");
            Check(_tracks.Count == 3 && _tracks.SelectMany(track => track.OriginalRegion.ToArray()).SequenceEqual(samples) &&
                _tracks.Select(track => track.SourceStartSeconds).SequenceEqual(new[] { 0d, 1.25, 2d }),
                "Reopening a sliced project loaded whole files or the wrong source ranges.");
            string replacementPath = Path.Combine(directory, "Replacement.wav");
            var replacement = samples.Select(value => value * .5f).ToArray();
            AudioFileService.ExportWav(new AudioTrack { FilePath = replacementPath + ".original", SampleRate = rate, Channels = 1,
                Render = new RenderResult { Samples = replacement } }, replacementPath, true);
            await RelinkTrackAsync(_tracks[1], replacementPath); await WaitForSourceEditPreviewSmokeAsync("Slicing: relink sliced family");
            Check(_tracks[1].OriginalRegion.ToArray().SequenceEqual(replacement.Skip(rate * 5 / 4).Take(rate * 3 / 4)),
                "Relinking a slice discarded its source offset or duration.");
        }
        catch (Exception error) { fixtureFailure = error; throw; }
        finally
        {
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            _previewCache.Clear();
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            _projectPath = oldPath; _backgroundProjectNeedsCopy = oldNeedsCopy;
            Restore(session); try { await WaitForSourceEditPreviewSmokeAsync("Slicing: restore prior session"); }
            catch (Exception cleanup) when (fixtureFailure != null)
            { throw new AggregateException("Slicing fixture and cleanup both failed.", fixtureFailure, cleanup); }
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = oldDirty; Title = oldTitle; Timeline.Playhead = oldPlayhead;
            if (oldSelected != null) SelectTrack(oldSelected);
            UpdateTime(); Directory.Delete(directory, true);
        }
    }
}

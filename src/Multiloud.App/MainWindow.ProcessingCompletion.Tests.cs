using System.Windows;
using System.IO;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyProcessingCompletionSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty, original = OriginalRadio.IsChecked == true, comparison = CompareRadio.IsChecked == true;
        bool? solo = BackgroundSoloButton.IsChecked;
        var view = Timeline.ViewMode;
        double playhead = Timeline.Playhead; var selected = Selected;
        string status = StatusText.Text;
        string directory = Path.Combine(Path.GetTempPath(), "Loudline-processing-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Task? importOperation = null;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Processing completion smoke: " + message); }
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            BackgroundSoloButton.IsChecked = false; PreviewRadio.IsChecked = true;
            foreach (var track in _tracks) RememberActivePreview(track);
            const int count = 6, seconds = 12, rate = 8000;
            var root = await Task.Run(() =>
            {
                double[] amplitudes = [.018, .14, .035, .3, .055, .19];
                var samples = new float[count * seconds * rate];
                for (int frame = 0; frame < samples.Length; frame++)
                {
                    int part = frame / (seconds * rate);
                    double local = frame % (seconds * rate) / (double)rate;
                    double level = amplitudes[part] * (local < 3 ? .35 : local < 8 ? 1 : .55);
                    samples[frame] = (float)(level * (.7 * Math.Sin(2 * Math.PI * (170 + 43 * part) * local)
                        + .2 * Math.Sin(2 * Math.PI * 973 * local)));
                }
                var waveform = WaveformPyramid.Build(samples, 1);
                return new AudioTrack { FilePath = "Deferred album.wav", Original = samples, SampleRate = rate,
                    Channels = 1, OriginalWaveform = waveform, OriginalPeaks = BuildDisplayPeaks(waveform),
                    WorkState = TrackWorkState.Idle };
            });
            var bed = new AudioTrack { FilePath = "Fixed bed.wav", Original = root.Original,
                SampleRate = rate, Channels = 1, OriginalWaveform = root.OriginalWaveform,
                OriginalPeaks = root.OriginalPeaks, WorkState = TrackWorkState.Idle };
            var layer = new BackgroundLayer { GainDb = -42, Clips = [new BackgroundClip { Source = bed }] };
            var off = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            AnalysisResult? firstKnown = null, lastKnown = null;
            foreach (bool knownEnds in new[] { false, true })
            {
                var parts = new List<AudioTrack>();
                var remainder = root;
                for (int index = 0; index < count - 1; index++)
                {
                    var split = AudioTrackSlicer.SplitShared(remainder, seconds);
                    parts.Add(split.Left); remainder = split.Right;
                }
                parts.Add(remainder);
                if (knownEnds) { parts[0].Analysis = firstKnown; parts[^1].Analysis = lastKnown; }
                _tracks.Clear(); _tracks.AddRange(parts); _renderKeys.Clear();
                _backgroundLayer = layer; _settings = off; RestoreProjectEnvelope([], []);
                SyncControls(); RefreshList(); QueueRender();
                Require(_tracks.All(track => PreviewCurrent(track, off) && CanExportTrack(track)),
                    "The sliced bypass fixture was not initially ready.");
                var ids = parts.Select(track => track.Id).ToArray();
                int before = _renderExecutionCount;
                PushUndo(); _settings = new ProcessingSettings();
                Timeline.Playhead = seconds * 3 + 2;
                SyncControls(); QueueRender();
                var published = new Dictionary<Guid, (RenderResult Render, string Key)>();
                bool sawPartial = false, auditioned = false;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                int visits = 0;
                while ((_renderedRevision != _revision || _renderCts != null) && DateTime.UtcNow < deadline)
                {
                    Require(_renderExecutionCount - before <= count,
                        "Completed clips were repeatedly re-rendered while further source analyses arrived.");
                    if (_renderExecutionCount > before)
                        Require(parts.All(track => track.Analysis != null),
                            "Rendering began before the continuous leveling envelope had all source analyses.");
                    foreach (var track in parts)
                    {
                        if (published.TryGetValue(track.Id, out var prior))
                            Require(PreviewCurrent(track, _settings) && ReferenceEquals(prior.Render, track.Render) && prior.Key == RenderKey(track, _settings) &&
                                track.WorkState == TrackWorkState.Idle,
                                "A completed Result became stale without an audio edit.");
                        else if (track.Render?.LiveSource == null && PreviewCurrent(track, _settings))
                            published.Add(track.Id, (track.Render!, RenderKey(track, _settings)));
                    }
                    if (published.Count is > 0 and < count)
                    {
                        sawPartial = true;
                        if (!auditioned)
                        {
                            var ready = parts.First(track => published.ContainsKey(track.Id));
                            double start = parts.TakeWhile(track => track.Id != ready.Id).Sum(track => track.Duration);
                            var states = CapturePlaybackTracks();
                            var sequence = PlaybackService.CreateSampleProvider(() => states, false, start + .25);
                            var buffer = new float[256];
                            Require(sequence.Read(buffer, 0, buffer.Length) == buffer.Length && buffer.All(float.IsFinite),
                                "Listening to an already completed clip was blocked by other clips' processing.");
                            auditioned = true;
                        }
                    }
                    // Ordinary listening/display publications must not create a
                    // fresh processing revision or reshuffle the album itself.
                    if (++visits % 4 == 0)
                    {
                        Timeline.Playhead = (visits % count + .25) * seconds;
                        RefreshList();
                    }
                    await Task.Delay(5);
                }
                Require(_renderedRevision == _revision && _renderCts == null && parts.All(track =>
                    track.Analysis != null && track.WorkState == TrackWorkState.Idle && PreviewCurrent(track, _settings)) &&
                    _renderExecutionCount - before == count && ExportButton.IsEnabled,
                    "The source cohort did not finish exactly once per clip and become exportable.");
                Require(sawPartial && auditioned && parts.Select(track => track.Id).SequenceEqual(ids) && _tracks.SequenceEqual(parts),
                    "Completed-track playback or fixed album order was lost while the remaining clips rendered.");
                if (knownEnds)
                    Require(ReferenceEquals(parts[0].Analysis, firstKnown) && ReferenceEquals(parts[^1].Analysis, lastKnown),
                        "Known endpoint analyses were discarded while the middle clips were analyzed.");
                firstKnown = parts[0].Analysis; lastKnown = parts[^1].Analysis;
                int completed = _renderExecutionCount, stableRevision = _revision;
                var stableKeys = parts.Select(track => RenderKey(track, _settings)).ToArray();
                var renders = parts.Select(track => track.Render).ToArray();
                foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
                {
                    Timeline.ViewMode = mode; Timeline.Playhead += .1; RefreshList();
                    await Task.Delay(30);
                }
                Require(_revision == stableRevision && _renderExecutionCount == completed &&
                    stableKeys.SequenceEqual(parts.Select(track => RenderKey(track, _settings))),
                    "Playback/display changes invalidated a finished album.");
                QueueRender(addition: true); await Task.Delay(650);
                Require(_renderExecutionCount == completed && _renderedRevision == _revision && _renderCts == null &&
                    parts.Select((track, index) => ReferenceEquals(track.Render, renders[index]) &&
                        stableKeys[index] == RenderKey(track, _settings)).All(value => value),
                    "An unchanged processing notification restarted the completed album.");
            }

            // Source-owned imports publish analyses one file at a time too.
            // Their queue must wait for the same complete global detector input
            // rather than rendering the first imported files repeatedly.
            string[] paths = Enumerable.Range(0, 3)
                .Select(index => Path.Combine(directory, $"{index + 1:00} imported section.wav")).ToArray();
            await Task.Run(() =>
            {
                for (int index = 0; index < paths.Length; index++)
                {
                    var range = root.Original.AsSpan(index * seconds * rate, seconds * rate).ToArray();
                    AudioFileService.ExportWav(new AudioTrack
                    {
                        FilePath = paths[index] + ".source", SampleRate = rate, Channels = 1,
                        Render = new RenderResult { Samples = range }
                    }, paths[index], true);
                }
            });
            _tracks.Clear(); _renderKeys.Clear(); _backgroundLayer = layer;
            _settings = new ProcessingSettings(); RestoreProjectEnvelope([], []);
            SyncControls(); RefreshList(); QueueRender();
            int importedBefore = _renderExecutionCount;
            var importPublished = new Dictionary<Guid, RenderResult>();
            Task import = ImportFilesAsync(paths);
            importOperation = import;
            var importDeadline = DateTime.UtcNow.AddSeconds(60);
            while ((!import.IsCompleted || _renderedRevision != _revision || _renderCts != null) && DateTime.UtcNow < importDeadline)
            {
                Require(_renderExecutionCount - importedBefore <= paths.Length,
                    "A source-owned import repeatedly rendered its earlier files.");
                if (_renderExecutionCount > importedBefore)
                    Require(_tracks.Count == paths.Length && _tracks.All(track => track.Analysis != null),
                        "Source-owned rendering began against an incomplete global leveling detector.");
                foreach (var track in _tracks)
                {
                    if (importPublished.TryGetValue(track.Id, out var ready))
                        Require(ReferenceEquals(ready, track.Render) && PreviewCurrent(track, _settings) && track.WorkState == TrackWorkState.Idle,
                            "A completed imported track was invalidated by a later source analysis.");
                    else if (track.Render?.LiveSource == null && PreviewCurrent(track, _settings))
                        importPublished.Add(track.Id, track.Render!);
                }
                await Task.Delay(5);
            }
            await import;
            importOperation = null;
            Require(_tracks.Count == paths.Length && _tracks.Select(track => track.FilePath).SequenceEqual(paths) &&
                _renderExecutionCount - importedBefore == paths.Length && _renderedRevision == _revision && _renderCts == null &&
                _tracks.All(track => track.Analysis != null && PreviewCurrent(track, _settings) && track.WorkState == TrackWorkState.Idle) && ExportButton.IsEnabled,
                "The real multi-file import did not finish with one final render per source.");
            int importedDone = _renderExecutionCount;
            QueueRender(addition: true); await Task.Delay(650);
            Require(_renderExecutionCount == importedDone && _renderedRevision == _revision && _renderCts == null,
                "A completed imported album restarted without an edit.");
        }
        finally
        {
            if (importOperation is { IsCompleted: false })
            {
                _importCts?.Cancel();
                await importOperation;
            }
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; Timeline.Playhead = playhead; StatusText.Text = status;
            OriginalRadio.IsChecked = original; CompareRadio.IsChecked = comparison;
            PreviewRadio.IsChecked = !original && !comparison; BackgroundSoloButton.IsChecked = solo;
            Timeline.ViewMode = view;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            Directory.Delete(directory, true);
        }
    }
}

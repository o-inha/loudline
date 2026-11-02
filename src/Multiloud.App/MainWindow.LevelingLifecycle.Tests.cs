using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyLevelingLifecycleSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; double playhead = Timeline.Playhead; var selected = Selected;
        string status = StatusText.Text;
        string folder = Path.Combine(Path.GetTempPath(), "Loudline-leveling-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Task? import = null;
        TrackListEntry? processingRow = null, loadingRow = null;
        PropertyChangedEventHandler? processingHandler = null, loadingHandler = null;
        Exception? callbackError = null;
        bool changedDuringRender = false, changedDuringAnalysis = false;
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            foreach (var item in _tracks) RememberActivePreview(item);
            var settings = new ProcessingSettings { BassSensitivity = .25, MatchingEnabled = false, LimiterEnabled = false, Strength = 1 };
            var track = await Task.Run(() => CreateLevelingLifecycleTrack(settings));
            _tracks.Clear(); _tracks.Add(track); _renderKeys.Clear(); _settings = settings;
            LoadProjectEnvelope(ProjectEnvelopeDesigner.FromTracks([track]), ProjectEnvelopeDesigner.Design([track], settings));
            _renderKeys[track.Id] = RenderKey(track, settings);
            SyncControls(); RefreshList(); CompletePreview(_revision); Timeline.Playhead = 2;
            _undo.Clear(); _redo.Clear();
            var analysis = track.Analysis!; var source = track.Original;
            var energy = analysis.LevelingEnergy;
            var authored = _projectEnvelope.ToArray();
            var oldSuggestion = _projectSuggestion.ToArray();

            // Supersede an actual in-flight render at its normal UI yield. The
            // callback is dispatched after RefreshList, avoiding reentrant edits.
            processingRow = _rowsById[track.Id];
            processingHandler = (_, args) =>
            {
                if (args.PropertyName != nameof(TrackListEntry.Processing) || !processingRow.Processing || changedDuringRender) return;
                changedDuringRender = true;
                var obsolete = _renderCts;
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        RequireLevelingLifecycle(ApplyLevelingOptions(_settings with { MaxLevelingDb = 4.75 }), "Apply was blocked during preview processing.");
                        RequireLevelingLifecycle(obsolete?.IsCancellationRequested == true, "Apply did not cancel superseded preview work.");
                    }
                    catch (Exception ex) { callbackError = ex; }
                }, DispatcherPriority.Send);
            };
            processingRow.PropertyChanged += processingHandler;
            RequireLevelingLifecycle(ApplyLevelingOptions(settings with { BassSensitivity = 1, TransitionSeconds = 12 }), "Advanced leveling Apply was rejected.");
            await WaitForSmokePreviewAsync();
            if (callbackError != null) throw callbackError;
            RequireLevelingLifecycle(changedDuringRender && _settings.MaxLevelingDb == 4.75 &&
                ReferenceEquals(track.Analysis, analysis) && ReferenceEquals(track.Analysis!.LevelingEnergy, energy) &&
                ReferenceEquals(track.Original, source) && PreviewCurrent(track, _settings),
                $"Settings supersession failed: processing={changedDuringRender}, maximum={_settings.MaxLevelingDb:R}, " +
                $"analysisSame={ReferenceEquals(track.Analysis, analysis)}, energySame={ReferenceEquals(track.Analysis?.LevelingEnergy, energy)}, " +
                $"sourceSame={ReferenceEquals(track.Original, source)}, previewCurrent={PreviewCurrent(track, _settings)}.");
            var nextSuggestion = ProjectEnvelopeDesigner.Design([track], _settings);
            foreach (double at in authored.Select(point => point.Time).Concat(oldSuggestion.Select(point => point.Time)).Concat(nextSuggestion.Select(point => point.Time)).Distinct())
            {
                double expected = AudioProcessor.EnvelopeGainAt(authored, at) + AudioProcessor.EnvelopeGainAt(nextSuggestion, at) - AudioProcessor.EnvelopeGainAt(oldSuggestion, at);
                RequireLevelingLifecycle(Math.Abs(AudioProcessor.EnvelopeGainAt(track.Envelope, at) - expected) < 1e-9,
                    "Changing the suggestion erased a manual envelope displacement.");
            }
            processingRow.PropertyChanged -= processingHandler; processingHandler = null;

            // Start the real decoder, apply once while Loading, then again at
            // the analysis yield. The separate import token must remain alive.
            string input = Path.Combine(folder, "new source.wav");
            AudioFileService.ExportWav(new AudioTrack { FilePath = "synthetic.raw", SampleRate = track.SampleRate,
                Channels = track.Channels, Render = new RenderResult { Samples = source } }, input, true);
            import = ImportFilesAsync([input]);
            var loading = _tracks.Single(item => item.FilePath == input);
            loadingRow = _rowsById[loading.Id];
            loadingHandler = (_, args) =>
            {
                if (args.PropertyName != nameof(TrackListEntry.Status) || loadingRow.Status != "Analyzing loudness…" || changedDuringAnalysis) return;
                changedDuringAnalysis = true;
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        RequireLevelingLifecycle(_importing && loading.Analysis == null &&
                            ApplyLevelingOptions(_settings with { BassSensitivity = .25 }), "Apply was blocked during source analysis.");
                    }
                    catch (Exception ex) { callbackError = ex; }
                }, DispatcherPriority.Send);
            };
            loadingRow.PropertyChanged += loadingHandler;
            var importToken = _importCts;
            RequireLevelingLifecycle(_importing && loading.Analysis == null &&
                ApplyLevelingOptions(_settings with { TransitionSeconds = 10 }), "Apply was blocked while a file was loading.");
            int noOpRevision = _revision, noOpHistory = _undo.Count, noOpExecutions = _renderExecutionCount;
            var previewToken = _renderCts;
            RequireLevelingLifecycle(!ApplyLevelingOptions(_settings) && _revision == noOpRevision && _undo.Count == noOpHistory &&
                _renderExecutionCount == noOpExecutions && ReferenceEquals(previewToken, _renderCts) &&
                ReferenceEquals(importToken, _importCts) && importToken is { IsCancellationRequested: false },
                "Unchanged Apply restarted work, or advanced settings cancelled source loading.");
            await import; await WaitForSmokePreviewAsync();
            if (callbackError != null) throw callbackError;
            RequireLevelingLifecycle(changedDuringAnalysis && loading.Analysis != null &&
                _projectSuggestion.SequenceEqual(ProjectEnvelopeDesigner.Design(_tracks, _settings)) &&
                loading.Envelope.SequenceEqual(ProjectEnvelopeDesigner.Crop(_projectEnvelope, track.Duration, loading.Duration)) &&
                ReferenceEquals(track.Analysis, analysis) && ReferenceEquals(track.Original, source),
                "A source that finished after Apply did not use the latest settings.");
            var loadedAnalysis = loading.Analysis; var loadedSource = loading.Original;
            var beforeUndo = _undo.Peek();
            RequireLevelingLifecycle(beforeUndo.Tracks.Single(item => ReferenceEquals(item.Track, loading)).SuggestOnLoad,
                "Undo did not record the pending source's need for an automatic suggestion.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            RequireLevelingLifecycle(ReferenceEquals(loading.Analysis, loadedAnalysis) && ReferenceEquals(loading.Original, loadedSource) &&
                _projectSuggestion.SequenceEqual(ProjectEnvelopeDesigner.Design(_tracks, beforeUndo.Settings)) &&
                loading.Envelope.SequenceEqual(ProjectEnvelopeDesigner.Crop(_projectEnvelope, track.Duration, loading.Duration)),
                "Undo after loading restored an empty curve or rescanned source analysis.");
        }
        finally
        {
            if (processingHandler != null && processingRow != null) processingRow.PropertyChanged -= processingHandler;
            if (loadingHandler != null && loadingRow != null) loadingRow.PropertyChanged -= loadingHandler;
            CancelInterruptedPlayback(); _importCts?.Cancel(); _renderCts?.Cancel();
            if (import != null) await import;
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session);
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; Timeline.Playhead = playhead;
            if (selected != null) SelectTrack(selected);
            UpdateTime(); StatusText.Text = status;
            Directory.Delete(folder, true);
        }
    }

    private static AudioTrack CreateLevelingLifecycleTrack(ProcessingSettings settings)
    {
        const int rate = 8000, duration = 24;
        var samples = new float[rate * duration];
        for (int frame = 0; frame < samples.Length; frame++)
        {
            double time = frame / (double)rate;
            samples[frame] = (float)(.04 * Math.Sin(2 * Math.PI * 700 * time) +
                (time < 12 ? .03 : .3) * Math.Sin(2 * Math.PI * 80 * time));
        }
        var track = new AudioTrack { FilePath = "authored source.wav", SampleRate = rate, Channels = 1, Original = samples,
            Analysis = LoudnessAnalyzer.Analyze(samples, rate, 1), WorkState = TrackWorkState.Idle, Status = "Ready" };
        track.Envelope = TransitionDesigner.Combine(EnvelopeDesigner.Design(track.Analysis, duration, settings),
            new[] { new EnvelopePoint(0, 1), new EnvelopePoint(11, 2), new EnvelopePoint(duration, -.5) });
        track.OriginalWaveform = WaveformPyramid.Build(samples, 1); track.OriginalPeaks = BuildDisplayPeaks(track.OriginalWaveform);
        track.Render = AudioProcessor.Render(samples, rate, 1, track.Envelope, 0, settings);
        track.Render.Waveform = WaveformPyramid.Build(track.Render.Samples, 1); track.PreviewPeaks = BuildDisplayPeaks(track.Render.Waveform);
        return track;
    }

    private static void RequireLevelingLifecycle(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Smoke test: " + message);
    }
}

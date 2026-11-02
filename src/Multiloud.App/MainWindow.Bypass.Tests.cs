using System.IO;
using System.Windows;
using Multiloud.Core;
using Multiloud.App.Services;
using System.Text.Json;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBypassSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty, original = OriginalRadio.IsChecked == true, compare = CompareRadio.IsChecked == true;
        var selected = Selected; double playhead = Timeline.Playhead;
        string status = StatusText.Text, title = Title;
        string? projectPath = _projectPath;
        bool needsCopy = _backgroundProjectNeedsCopy;
        var directory = Path.Combine(Path.GetTempPath(), "LoudlineIdentity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            foreach (var track in _tracks) RememberActivePreview(track);
            var on = new ProcessingSettings();
            var off = on with { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
            var fixtures = await Task.Run(() =>
            {
                AudioTrack Create(string name, double amplitude)
                {
                    const int rate = 12000;
                    var samples = Enumerable.Range(0, rate * 2)
                        .Select(frame => (float)(amplitude * Math.Sin(2 * Math.PI * 440 * frame / rate))).ToArray();
                    var track = new AudioTrack { FilePath = Path.Combine(directory, name), Original = samples,
                        SampleRate = rate, Channels = 1, Envelope = [new(0, 6), new(2, 6)], TargetLufs = -17 };
                    track.Analysis = LoudnessAnalyzer.Analyze(samples, rate, 1);
                    track.OriginalWaveform = WaveformPyramid.Build(samples, 1);
                    track.OriginalPeaks = BuildDisplayPeaks(track.OriginalWaveform);
                    return track;
                }
                var safe = Create("safe.wav", .1);
                safe.Render = AudioProcessor.Render(safe.Original, safe.SampleRate, safe.Channels,
                    safe.Envelope, 0, on with { TargetLufs = -17 });
                safe.Render.Waveform = WaveformPyramid.Build(safe.Render.Samples, safe.Channels);
                safe.PreviewPeaks = BuildDisplayPeaks(safe.Render.Waveform);
                return (Safe: safe, Unsafe: Create("unsafe.wav", 1.2), Silent: Create("silence.wav", 0));
            });
            var safe = fixtures.Safe; var analysis = safe.Analysis; var processed = safe.Render;
            _tracks.Clear(); _tracks.Add(safe); _renderKeys.Clear(); _backgroundLayer = new();
            _settings = on; RestoreProjectEnvelope([new(0, 6), new(2, 6)], [new(0, 6), new(2, 6)]);
            _renderKeys[safe.Id] = RenderKey(safe, on);
            SyncControls(); PreviewRadio.IsChecked = true; RefreshList(); CompletePreview(_revision);
            int executions = _renderExecutionCount;
            _settings = off; SyncControls(); QueueRender();
            RequireIdentity(_renderedRevision == _revision && _renderCts == null && _renderExecutionCount == executions &&
                safe.WorkState == TrackWorkState.Idle && PreviewCurrent(safe, off),
                "Turning all modules off did not publish an eligible source immediately without a render job.");
            var identity = safe.Render!;
            RequireIdentity(ReferenceEquals(identity.Samples, safe.Original) && ReferenceEquals(identity.Waveform, safe.OriginalWaveform) &&
                ReferenceEquals(safe.PreviewPeaks, safe.OriginalPeaks) && ReferenceEquals(safe.Analysis, analysis) &&
                identity.OutputLufs == analysis!.IntegratedLufs && identity.TruePeakDb == analysis.TruePeakDb &&
                identity.RenderedTargetLufs == safe.TargetLufs && identity.RenderedSampleRate == safe.SampleRate &&
                identity.RenderedChannels == safe.Channels && identity.RetainedIntermediateBytes == 0 &&
                identity.Work.LevelingFramesProcessed == 0 && identity.Work.DetectorFramesProcessed == 0 &&
                identity.Work.LoudnessFramesProcessed == 0 && identity.Work.FinalPeakFramesProcessed == 0,
                "Identity Result copied source audio, rebuilt its pyramid, discarded analysis, or published incorrect metadata.");
            RequireIdentity(PreviewRadio.IsChecked == true && PreviewRadio.IsEnabled && CompareRadio.IsEnabled,
                "Identity reuse unexpectedly changed or disabled the user's display mode.");
            string export = Path.Combine(directory, "identity-export.wav");
            await ExportSingleTrackAsync(safe, export, true);
            VerifySingleTrackWavSmoke(export, safe, safe.Original, true);
            RequireIdentity(_renderExecutionCount == executions && ReferenceEquals(safe.Render, identity) &&
                ReferenceEquals(safe.Original, identity.Samples), "Exporting the identity Result rebuilt or mutated its source.");

            _settings = on; SyncControls(); QueueRender();
            RequireIdentity(_renderedRevision == _revision && _renderExecutionCount == executions && ReferenceEquals(safe.Render, processed),
                "Re-enabling processing did not restore the cached processed variant.");
            _settings = off; SyncControls(); QueueRender();
            RequireIdentity(_renderedRevision == _revision && _renderExecutionCount == executions && ReferenceEquals(safe.Render, identity),
                "Returning to bypass did not restore the exact cached identity variant.");

            void Reject(ProcessingSettings settings, string reason)
            {
                var before = safe.Render;
                RequireIdentity(!TryPublishIdentityPreview(safe, settings) && ReferenceEquals(safe.Render, before),
                    "Identity path ignored " + reason + ".");
            }
            safe.InputGainDb = 1;
            RequireIdentity(TryPublishIdentityPreview(safe, off) && safe.Render?.LiveSource?.GainDb == 1 &&
                _renderExecutionCount == executions, "A static input gain unnecessarily rendered bypassed audio.");
            safe.InputGainDb = 0; safe.TrimDb = -1;
            RequireIdentity(TryPublishIdentityPreview(safe, off) && safe.Render?.LiveSource?.GainDb == -1 &&
                _renderExecutionCount == executions, "A static output trim unnecessarily rendered bypassed audio.");
            safe.TrimDb = 0;
            Reject(off with { LevelingEnabled = true }, "enabled leveling");
            Reject(off with { MatchingEnabled = true }, "enabled matching");
            Reject(off with { LimiterEnabled = true }, "enabled peak protection");
            _backgroundLayer = new BackgroundLayer { Clips = [new BackgroundClip { Source = safe }] };
            RequireIdentity(TryPublishIdentityPreview(safe, off) && safe.Render?.LiveSource?.Background == _backgroundLayer &&
                safe.Render.SafetyAttenuationDb == 0 && double.IsNaN(safe.Render.OutputLufs) && _renderExecutionCount == executions,
                "An enabled background required a rendered mix, invented mix loudness or imposed peak attenuation in bypass.");
            _backgroundLayer = new();
            RequireIdentity(TryPublishIdentityPreview(fixtures.Unsafe, off) &&
                ReferenceEquals(fixtures.Unsafe.Render!.Samples, fixtures.Unsafe.Original) &&
                fixtures.Unsafe.Render.SafetyAttenuationDb == 0 && fixtures.Unsafe.Render.TruePeakDb > off.CeilingDb,
                "The all-modules-off bypass still imposed a hidden ceiling or altered the source.");
            var measured = fixtures.Unsafe.Analysis;
            fixtures.Unsafe.Analysis = null;
            RequireIdentity(TryPublishIdentityPreview(fixtures.Unsafe, off) &&
                fixtures.Unsafe.Analysis == null && double.IsNaN(fixtures.Unsafe.Render!.OutputLufs) &&
                double.IsNaN(fixtures.Unsafe.Render.TruePeakDb) && double.IsNaN(fixtures.Unsafe.Render.PreMatchLufs) &&
                ReferenceEquals(fixtures.Unsafe.Render.Samples, fixtures.Unsafe.Original),
                "An unmeasured slice invented loudness/peak values or required an audio scan for bypass.");
            fixtures.Unsafe.Analysis = measured;
            RequireIdentity(TryPublishIdentityPreview(fixtures.Silent, off) && fixtures.Silent.Render is { OutputLufs: -70, Warning: not null } &&
                ReferenceEquals(fixtures.Silent.Render.Samples, fixtures.Silent.Original) && _renderExecutionCount == executions,
                "Silent identity reuse lost its measurement, warning, or zero-work behavior.");
            await VerifyDeferredBypassWorkflowSmokeAsync(directory, off, safe);
        }
        finally
        {
            CancelInterruptedPlayback(); _player.Stop(); _renderCts?.Cancel(); ++_revision;
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; _projectPath = projectPath; _backgroundProjectNeedsCopy = needsCopy; Title = title;
            if (_renderedRevision == _revision) RememberCompletedProcessing();
            Timeline.Playhead = playhead;
            OriginalRadio.IsChecked = original; CompareRadio.IsChecked = compare; PreviewRadio.IsChecked = !original && !compare;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            StatusText.Text = status;
            Directory.Delete(directory, true);
        }
    }

    private async Task VerifyDeferredBypassWorkflowSmokeAsync(string directory, ProcessingSettings off, AudioTrack source)
    {
        string importedPath = Path.Combine(directory, "deferred-import.wav");
        await Task.Run(() => AudioFileService.ExportWav(new AudioTrack
        {
            FilePath = importedPath + ".source", SampleRate = source.SampleRate, Channels = source.Channels,
            Render = new RenderResult { Samples = source.Original }
        }, importedPath, true));
        int executions = _renderExecutionCount;
        await ImportFilesAsync([importedPath]);
        var imported = _tracks.Single(track => track.FilePath == importedPath);
        RequireIdentity(imported.Analysis == null && IsCurrentSourceBypass(imported, off) &&
            imported.WorkState == TrackWorkState.Idle && _renderExecutionCount == executions &&
            CanExportTrack(imported) && CapturePlaybackTracks().Single(track => track.Id == imported.Id).IsReady(false),
            "Importing with all modules off performed loudness analysis or failed to expose immediate Result/playback/export readiness.");

        // A parameter edit requesting the first analysis is still Cancel & Undo,
        // rather than a source import that would discard the prior settings.
        bool gateHeld = false;
        try
        {
            await _renderGate.WaitAsync(); gateHeld = true;
            PushUndo(); _settings = off with { MatchingEnabled = true }; SyncControls(); QueueRender();
            RequireIdentity(_processingRollback != null && imported.Analysis == null && _renderExecutionCount == executions,
                "Enabling a module skipped the normal cancel/undo transaction or started blocked audio work.");
            CancelProcessing();
            RequireIdentity(!_settings.MatchingEnabled && IsCurrentSourceBypass(imported, _settings) &&
                imported.Analysis == null && imported.WorkState == TrackWorkState.Idle && _renderExecutionCount == executions,
                "Canceling deferred analysis did not restore the immediately ready bypass and previous controls.");
        }
        finally { if (gateHeld) _renderGate.Release(); }
        await DrainBypassWorkerSmokeAsync();

        PushUndo(); _settings = off with { MatchingEnabled = true }; SyncControls(); QueueRender();
        await WaitForSmokePreviewAsync(); await DrainBypassWorkerSmokeAsync();
        RequireIdentity(imported.Analysis != null && imported.Render is { } && PreviewCurrent(imported, _settings) &&
            !ReferenceEquals(imported.Render.Samples, imported.Original) && _renderExecutionCount > executions,
            "Enabling a module did not automatically analyze and process a previously unmeasured import.");
        executions = _renderExecutionCount;
        _settings = off; SyncControls(); QueueRender();
        RequireIdentity(IsCurrentSourceBypass(imported, off) && imported.Render!.OutputLufs == imported.Analysis!.IntegratedLufs &&
            _renderExecutionCount == executions && _renderedRevision == _revision,
            "Returning to bypass left stale unknown meters after deferred analysis or restarted DSP.");

        // Opening an authored bypass project must likewise avoid a full scan.
        string openSource = Path.Combine(directory, "deferred-open.wav");
        File.Copy(importedPath, openSource);
        string project = Path.Combine(directory, "deferred-open.loudline");
        var data = new ProjectData { Settings = off, Tracks = [new ProjectTrack
        {
            Path = Path.GetFileName(openSource), AbsolutePath = openSource,
            Duration = imported.Duration, SampleRate = imported.SampleRate, Channels = imported.Channels,
            Envelope = [new(0, 4), new(imported.Duration, 4)]
        }] };
        await File.WriteAllTextAsync(project, JsonSerializer.Serialize(data));
        await OpenProjectAsync(project, showMissingDialog: false);
        RequireIdentity(_tracks.Count == 1 && _tracks[0].Analysis == null && IsCurrentSourceBypass(_tracks[0], _settings) &&
            CanExportTrack(_tracks[0]) && CapturePlaybackTracks().Single().IsReady(false) &&
            _renderExecutionCount == executions && _renderedRevision == _revision,
            "Opening an all-modules-off project measured audio unnecessarily or lost ready bypass playback/export.");
    }

    private async Task DrainBypassWorkerSmokeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_renderCts != null && DateTime.UtcNow < deadline) await Task.Delay(10);
        RequireIdentity(_renderCts == null, "Canceled or completed deferred analysis failed to release its worker ownership.");
    }

    private static void RequireIdentity(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Smoke test: " + message); }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    // Uses real decode/render/cache/export/project paths, but never opens an audio device.
    private async Task VerifyBackgroundSmokeAsync(string? screenshot)
    {
        if (AudioFileService.MaxSamplesPerFile < 3600L * 48000 * 2)
            throw new InvalidOperationException("Smoke test: the source budget rejects a one-hour stereo background.");
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var segments = _backgroundSegments.ToArray(); var preparedOriginals = _backgroundOriginals.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        string? projectPath = _projectPath;
        bool dirty = _dirty, floatExport = _floatExport, needsCopy = _backgroundProjectNeedsCopy;
        bool? original = OriginalRadio.IsChecked, compare = CompareRadio.IsChecked, audition = AuditionMatchCheck.IsChecked;
        string title = Title, status = StatusText.Text;
        double playhead = Timeline.Playhead;
        var selected = Selected;
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineBackgroundSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string aPath = Path.Combine(directory, "Music A.wav"), bPath = Path.Combine(directory, "Music B.wav");
            string bedPath = Path.Combine(directory, "Fixed bed.wav");
            WriteBackgroundSmokeAudio(aPath, 2, 12000, 1, (frame, _) => .035 * Math.Sin(2 * Math.PI * 440 * frame / 12000));
            WriteBackgroundSmokeAudio(bPath, 2, 24000, 2, (frame, channel) => (channel == 0 ? .09 : .065) *
                Math.Sin(2 * Math.PI * (731 + channel * 137) * frame / 24000 + channel * .2));
            WriteBackgroundSmokeAudio(bedPath, 4, 16000, 2, (frame, channel) => (channel == 0 ? .008 : .005) +
                .002 * Math.Sin(2 * Math.PI * (97 + channel * 41) * frame / 16000));
            var data = new ProjectData
            {
                Settings = new ProcessingSettings { LevelingEnabled = false, TargetLufs = -20, LimiterEnabled = false },
                Tracks = new[] { aPath, bPath }.Select((path, index) => new ProjectTrack
                {
                    Path = Path.GetFileName(path), AbsolutePath = path, Duration = 2, SampleRate = index == 0 ? 12000 : 24000, Channels = index == 0 ? 1 : 2,
                    Envelope = [new(0, 0), new(2, 0)], TransitionEnvelope = []
                }).ToList()
            };
            string project = Path.Combine(directory, "Background.loudline");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(data));
            await OpenProjectAsync(project, showMissingDialog: false);
            await WaitForSmokePreviewAsync();
            var music = _tracks.ToArray(); var analyses = music.Select(track => track.Analysis).ToArray();
            var sourcePcm = music.Select(track => track.Original).ToArray();
            await ImportBackgroundFilesAsync([bedPath], 0);
            RequireBackground(_backgroundLayer.Clips.Count == 1, "Background import did not create one positioned source.");
            var source = _backgroundLayer.Clips[0].Source;
            var sourceCopy = source.Original.ToArray();
            var layer = _backgroundLayer with
            {
                GainDb = -6,
                Clips = [_backgroundLayer.Clips[0] with { FadeInSeconds = .25, FadeOutSeconds = .5 }]
            };
            SetBackgroundLayer(layer);
            RequireBackground(music.All(track => !CanExportTrack(track)) && !ExportButton.IsEnabled &&
                CapturePlaybackTracks().All(track => track.LiveOriginal != null && track.IsReady(true) && track.PreviewSamples == null),
                "A changed decoded bed exposed a stale Result/export or unnecessarily blocked live Original playback.");
            await WaitForSmokePreviewAsync();
            RequireBackground(source.Analysis == null && source.Render == null && source.OriginalWaveform != null &&
                source.WorkState == TrackWorkState.Idle && source.Duration == 4 && _tracks.SequenceEqual(music),
                "The fixed bed was loudness-processed, lost its waveform, or entered the music file order.");
            VerifyBackgroundSegmentsSmoke(music, layer);
            VerifyBackgroundOriginalResumeSmoke(music);
            foreach (var track in music)
                RequireBackground(track.Render is { IncludesBackground: true } render && render.OriginalMixLufs.HasValue &&
                    render.BackgroundLufs.HasValue && Math.Abs(render.OutputLufs - _settings.TargetLufs) < .25 &&
                    Math.Abs(LoudnessAnalyzer.Analyze(render.Samples, PlaybackSampleRate(track), PlaybackChannels(track)).IntegratedLufs - render.OutputLufs) < .001,
                    "The music target did not describe the measured final music-plus-background mix.");

            // Music-only controls must reuse the exact bed segment, not regenerate or scale it.
            var beds = music.Select(CurrentBackgroundSamples).ToArray();
            _settings = _settings with { MatchingStrength = .5 };
            music[0].InputGainDb = 1.25; music[1].TrimDb = -.75;
            SyncControls(); QueueRender(); await WaitForSmokePreviewAsync();
            RequireBackground(music.Select((track, index) => ReferenceEquals(CurrentBackgroundSamples(track), beds[index]) &&
                ReferenceEquals(track.Render!.BackgroundSamples, beds[index]) && ReferenceEquals(track.Analysis, analyses[index]) &&
                ReferenceEquals(track.Original, sourcePcm[index])).All(value => value) && source.Original.SequenceEqual(sourceCopy),
                "Music processing rescanned its source or changed/rebuilt the fixed background PCM.");

            // The same immutable layer and final mix variants must survive history traversal.
            _undo.Clear(); _redo.Clear();
            var before = music.Select(track => track.Render).ToArray();
            SetBackgroundLayer(layer with { GainDb = -9 }); await WaitForSmokePreviewAsync();
            var after = music.Select(track => track.Render).ToArray(); var finalLayer = _backgroundLayer;
            RequireBackground(_undo.Count == 1 && music.All(track => track.Render != null), "One bed edit did not create exactly one undo action.");
            int executions = _renderExecutionCount, revision = _revision;
            SetBackgroundLayer(finalLayer with { Clips = finalLayer.Clips.Select(clip => clip with { }).ToArray() });
            RequireBackground(_undo.Count == 1 && _revision == revision && _renderExecutionCount == executions,
                "An unchanged background configuration queued audio or history work.");
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            RequireBackground(_backgroundLayer.GainDb == -6 && music.Select((track, index) => ReferenceEquals(track.Render, before[index])).All(value => value),
                "Background Undo failed to restore exact cached mixed buffers.");
            RedoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            RequireBackground(_backgroundLayer.GainDb == -9 && _renderExecutionCount == executions &&
                music.Select((track, index) => ReferenceEquals(track.Render, after[index])).All(value => value),
                "Background Redo rebuilt audio or restored another bed variant.");

            // Moving a clip across the split changes its album segment, not the source/music objects.
            SetBackgroundLayer(finalLayer with { Clips = [finalLayer.Clips[0] with { StartSeconds = 2 }] });
            await WaitForSmokePreviewAsync();
            RequireBackground(BackgroundRequired(music[0]) && CurrentBackgroundSamples(music[0]) is { } emptyBed && emptyBed.All(sample => sample == 0) &&
                music[0].Render is { IncludesBackground: true, RenderedSampleRate: 48000, RenderedChannels: 2 } &&
                BackgroundRequired(music[1]) && music[1].Render is { IncludesBackground: true } &&
                ReferenceEquals(_backgroundLayer.Clips[0].Source, source) && _tracks.SequenceEqual(music),
                "Moving the bed across a music boundary reused the old segment or reordered source tracks.");
            VerifyBackgroundSegmentsSmoke(music, _backgroundLayer);
            var noOverlap = music[0];
            var expectedMusicOnly = AudioProcessor.Render(PreparedOriginalSamples(noOverlap)!, 48000, 2, noOverlap.Envelope,
                noOverlap.TrimDb, EffectiveSettings(noOverlap, _settings), inputGainDb: noOverlap.InputGainDb, transitionEnvelope: noOverlap.TransitionEnvelope);
            RequireBackground(noOverlap.Render!.Samples.SequenceEqual(expectedMusicOnly.Samples),
                "An active layer's nonoverlapping track changed the common-format music beyond normal processing.");
            SetBackgroundLayer(finalLayer); await WaitForSmokePreviewAsync();

            // A missing required bed blocks both monitoring modes and export; bypass restores music.
            var missing = new AudioTrack
            {
                FilePath = Path.Combine(directory, "Offline bed.wav"), SavedDuration = 4, SampleRate = 12000,
                Channels = 1, WorkState = TrackWorkState.Missing, Status = "File not found"
            };
            SetBackgroundLayer(finalLayer with { Clips = [finalLayer.Clips[0] with { Source = missing }] });
            var states = CapturePlaybackTracks();
            RequireBackground(states.All(track => track.BackgroundRequired && track.OriginalSamples == null && track.PreviewSamples == null) &&
                music.All(track => !CanExportTrack(track)) && !ExportButton.IsEnabled,
                "A missing overlapping background allowed stale monitoring or export.");
            ExportClick(this, new RoutedEventArgs());
            RequireBackground(!_exporting && !ExportButton.IsEnabled, "An unresolved background opened export.");
            SetBackgroundLayer(_backgroundLayer with { Enabled = false }); await WaitForSmokePreviewAsync();
            RequireBackground(CapturePlaybackTracks().All(track => !track.BackgroundRequired && track.OriginalSamples != null && track.PreviewSamples != null) &&
                music.All(CanExportTrack), "A bypassed missing bed blocked otherwise ready music.");
            foreach (var track in music)
            {
                var legacy = AudioProcessor.Render(track.Original, track.SampleRate, track.Channels, track.Envelope,
                    track.TrimDb, EffectiveSettings(track, _settings), inputGainDb: track.InputGainDb, transitionEnvelope: track.TransitionEnvelope);
                RequireBackground(PlaybackSampleRate(track) == track.SampleRate && PlaybackChannels(track) == track.Channels &&
                    ReferenceEquals(OriginalPlaybackSamples(track), track.Original) && track.Render!.Samples.SequenceEqual(legacy.Samples),
                    "Disabling the layer failed to restore exact legacy source-format music.");
            }
            SetBackgroundLayer(finalLayer); await WaitForSmokePreviewAsync();

            // Both export routes consume the exact final mixed Result. Monitoring is independent.
            OriginalRadio.IsChecked = true; AuditionMatchCheck.IsChecked = true;
            for (int index = 0; index < music.Length; index++)
            {
                var track = music[index]; string one = Path.Combine(directory, $"single-{index}.wav");
                await ExportSingleTrackAsync(track, one, float32: true);
                var exported = AudioFileService.Load(one);
                RequireBackground(exported.SampleRate == 48000 && exported.Channels == 2 && exported.Duration == PlaybackDuration(track) &&
                    exported.Original.SequenceEqual(track.Render!.Samples),
                    "Export this file lost the common mix format, omitted/duplicated the bed, or applied monitoring attenuation.");
            }
            var exportSnapshots = music.Select(track => new AudioTrack
            { FilePath = track.FilePath, SampleRate = PlaybackSampleRate(track), Channels = PlaybackChannels(track), Render = track.Render }).ToArray();
            await Task.Run(() =>
            {
                for (int index = 0; index < exportSnapshots.Length; index++)
                    AudioFileService.ExportWav(exportSnapshots[index], Path.Combine(directory, $"batch-{index}.wav"), float32: true);
            });
            for (int index = 0; index < music.Length; index++)
            {
                var exported = AudioFileService.Load(Path.Combine(directory, $"batch-{index}.wav"));
                RequireBackground(exported.SampleRate == 48000 && exported.Channels == 2 && exported.Duration == PlaybackDuration(music[index]) &&
                    exported.Original.SequenceEqual(music[index].Render!.Samples),
                    "The batch export snapshot did not retain the common format and final mixed PCM for each original music split.");
            }
            RequireBackground(source.Original.SequenceEqual(sourceCopy), "Export altered the decoded fixed background source.");

            // Save/reopen uses relative/absolute source paths and recreates the same two-track mix.
            var expected = music.Select(track => track.Render!.Samples.ToArray()).ToArray();
            var saved = CreateProjectData(project);
            RequireBackground(saved.Version == 9 && saved.Background.Clips.Count == 1 && saved.Background.GainDb == -9 &&
                saved.Background.Clips[0].Source.Path == Path.GetFileName(bedPath) && saved.Background.Clips[0].Source.AbsolutePath == bedPath &&
                saved.Background.Clips[0].StartSeconds == 0 && saved.Background.Clips[0].FadeInSeconds == .25 &&
                saved.Background.Clips[0].FadeOutSeconds == .5, "Project save lost fixed-bed placement, fades, gain or dual paths.");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(saved));
            await OpenProjectAsync(project, showMissingDialog: false); await WaitForSmokePreviewAsync();
            RequireBackground(_tracks.Count == 2 && _backgroundLayer.Clips.Count == 1 && _backgroundLayer.GainDb == -9 &&
                _backgroundLayer.Clips[0].Source.Analysis == null && _backgroundLayer.Clips[0].Source.OriginalWaveform != null &&
                _tracks.Select((track, index) => track.Render!.Samples.SequenceEqual(expected[index])).All(value => value),
                "Reopening the project changed the mixed PCM or loudness-processed the background source.");
            VerifyBackgroundSegmentsSmoke(_tracks.ToArray(), _backgroundLayer);
            PreviewRadio.IsChecked = true; Timeline.FitAll();
            var tileDeadline = DateTime.UtcNow.AddSeconds(4);
            do
            {
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Timeline.FlushPendingRendering(); BackgroundLane.FlushPendingRendering(); }, DispatcherPriority.ContextIdle);
                if (!Timeline.HasPendingTiles && !BackgroundLane.HasPendingTiles) break;
                await Task.Delay(10);
            } while (DateTime.UtcNow < tileDeadline);
            RequireBackground(!Timeline.HasPendingTiles && !BackgroundLane.HasPendingTiles, "The background screenshot waveform tiles did not settle.");
            if (screenshot != null) CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-background.png"));
        }
        finally
        {
            CancelSourceWork(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            _backgroundSegments.Clear(); foreach (var pair in segments) _backgroundSegments[pair.Key] = pair.Value;
            _backgroundOriginals.Clear(); foreach (var pair in preparedOriginals) _backgroundOriginals[pair.Key] = pair.Value;
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            _projectPath = projectPath; _floatExport = floatExport; _backgroundProjectNeedsCopy = needsCopy;
            Restore(session); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            OriginalRadio.IsChecked = original; CompareRadio.IsChecked = compare;
            if (original != true && compare != true) PreviewRadio.IsChecked = true;
            AuditionMatchCheck.IsChecked = audition;
            _dirty = dirty; Title = title; Timeline.Playhead = playhead; Timeline.FitAll();
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            UpdateTime(); StatusText.Text = status;
            Directory.Delete(directory, recursive: true);
        }
    }

    private void VerifyBackgroundOriginalResumeSmoke(IReadOnlyList<AudioTrack> tracks)
    {
        var track = tracks[1]; var render = track.Render!;
        double position = PlaybackDuration(tracks[0]) + .625;
        bool wasOriginal = OriginalRadio.IsChecked == true, wasCompare = CompareRadio.IsChecked == true;
        var resumed = new List<double>();
        void Resume(double value) => resumed.Add(value);
        var readyLayer = _backgroundLayer;
        void Pending()
        {
            CancelInterruptedPlayback(); resumed.Clear(); track.Render = null; _backgroundSegments.Remove(track.Id);
            _backgroundLayer = readyLayer with { Clips = readyLayer.Clips.Select(clip => clip with
            {
                Source = new AudioTrack { FilePath = clip.Source.FilePath, SavedDuration = clip.Source.Duration,
                    SampleRate = clip.Source.SampleRate, Channels = clip.Source.Channels,
                    WorkState = TrackWorkState.Missing, Status = "File not found" }
            }).ToArray() };
        }
        void Ready() { _backgroundLayer = readyLayer; track.Render = render; RememberBackgroundSegment(track, render); }
        try
        {
            RequireBackground(!_player.IsPlaying && !_player.IsPaused, "The hardware-free background resume fixture started with an active device.");
            OriginalRadio.IsChecked = true;
            track.Render = null; _backgroundSegments.Remove(track.Id);
            RequireBackground(PlaybackStateFor(track).IsReady(true) && PlaybackStateFor(track).LiveOriginal != null &&
                !PlaybackStateFor(track).IsReady(false),
                "Original monitoring waited for a full bed/mix render despite decoded music and background being available.");
            Ready();
            Pending();
            RequireBackground(ArmBackgroundOriginalResume(position, paused: false) && HasBackgroundOriginalResume &&
                !TryResumeBackgroundOriginal(Resume) && resumed.Count == 0,
                "Original playback resumed before its positioned bed was ready or lost its source-local intent.");
            Ready();
            RequireBackground(TryResumeBackgroundOriginal(Resume) && resumed.SequenceEqual(new[] { position }) &&
                Timeline.Playhead == position && !HasBackgroundOriginalResume && !TryResumeBackgroundOriginal(Resume),
                "Original playback did not resume exactly once at the same music position after its bed became ready.");

            Pending();
            RequireBackground(ArmBackgroundOriginalResume(position, paused: true) && !HasBackgroundOriginalResume && !_playbackRequested &&
                !HandleBackgroundOriginalWaiting(track.Id, position, Resume) && resumed.Count == 0,
                "A paused Original stream acquired an audible automatic-resume intent.");
            Ready(); CancelInterruptedPlayback();

            foreach (var command in new (string Name, Action Run)[]
            {
                ("Stop", () => StopClick(this, new RoutedEventArgs())),
                ("seek", () => { Timeline.Playhead = position + .125; SeekPlayback(position + .125); }),
                ("pause while waiting", () => PlayClick(this, new RoutedEventArgs())),
                ("cancel", () => CancelClick(this, new RoutedEventArgs())),
            })
            {
                Pending(); RequireBackground(ArmBackgroundOriginalResume(position, paused: false), "The Original " + command.Name + " fixture did not arm.");
                command.Run(); Ready();
                double cursor = Timeline.Playhead; string status = StatusText.Text;
                RequireBackground(!HasBackgroundOriginalResume && !TryResumeBackgroundOriginal(Resume) && resumed.Count == 0 &&
                    !HandleBackgroundOriginalWaiting(track.Id, position, Resume) && Timeline.Playhead == cursor && StatusText.Text == status,
                    "A manual " + command.Name + " let a stale background callback restart Original playback or replace the new cursor/status.");
            }
            RequireBackground(!_player.IsPlaying && !_player.IsPaused, "Background resume regression checks opened audio hardware.");
        }
        finally
        {
            Ready(); CancelInterruptedPlayback();
            if (wasOriginal) OriginalRadio.IsChecked = true;
            else if (wasCompare) CompareRadio.IsChecked = true;
            else PreviewRadio.IsChecked = true;
        }
    }

    private void VerifyBackgroundSegmentsSmoke(IReadOnlyList<AudioTrack> tracks, BackgroundLayer layer)
    {
        var whole = BackgroundRenderer.RenderSegment(layer, 0, tracks.Sum(PlaybackDuration), 48000, 2);
        var joined = new List<float>(); double offset = 0;
        foreach (var track in tracks)
        {
            var prepared = PreparedOriginalSamples(track);
            RequireBackground(PlaybackSampleRate(track) == 48000 && PlaybackChannels(track) == 2 &&
                track.Render is { RenderedSampleRate: 48000, RenderedChannels: 2 } &&
                prepared != null && prepared.Length == track.Render.Samples.Length,
                "An active background album did not publish one common stereo format for Original and Result.");
            var expectedRaw = BackgroundRenderer.RenderSegment(new BackgroundLayer { Clips = [new BackgroundClip { Source = track }] },
                0, PlaybackDuration(track), 48000, 2);
            RequireBackground(prepared!.SequenceEqual(expectedRaw),
                "The prepared Original changed music amplitude or channel routing during common-format resampling.");
            var expected = BackgroundRenderer.RenderSegment(layer, offset, PlaybackDuration(track), 48000, 2);
            var actual = CurrentBackgroundSamples(track);
            if (BackgroundRequired(track))
                RequireBackground(actual != null && actual.SequenceEqual(expected) && ReferenceEquals(actual, track.Render!.BackgroundSamples),
                    "Published background PCM disagreed with its positioned/faded album segment.");
            else RequireBackground(actual == null && expected.All(sample => sample == 0), "A nonoverlapping track retained background PCM.");
            joined.AddRange(actual ?? expected); offset += PlaybackDuration(track);
        }
        RequireBackground(joined.SequenceEqual(whole), "Export splits restarted or discontinuously aligned the background timeline.");
    }

    private static void WriteBackgroundSmokeAudio(string path, double seconds, int rate, int channels, Func<int, int, double> sample)
    {
        var pcm = new float[(int)(seconds * rate) * channels];
        for (int frame = 0; frame < pcm.Length / channels; frame++)
            for (int channel = 0; channel < channels; channel++) pcm[frame * channels + channel] = (float)sample(frame, channel);
        AudioFileService.ExportWav(new AudioTrack
        {
            FilePath = path + ".source", SampleRate = rate, Channels = channels, Original = pcm,
            Render = new RenderResult { Samples = pcm }
        }, path, float32: true);
    }

    private static void RequireBackground(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Smoke test: " + message);
    }
}

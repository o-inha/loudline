using System.ComponentModel;
using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyRenderPrioritySmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var savedKeys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty, original = OriginalRadio.IsChecked == true, compare = CompareRadio.IsChecked == true;
        double playhead = Timeline.Playhead; var selected = Selected;
        string status = StatusText.Text;
        var subscriptions = new List<(TrackListEntry Row, PropertyChangedEventHandler Handler)>();
        bool gateHeld = false;
        async Task Drain()
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (_renderCts != null && DateTime.UtcNow < deadline) await Task.Delay(10);
            RequireRenderPriority(_renderCts == null, "Render-priority fixture did not settle within twelve seconds.");
        }
        try
        {
            await Drain();
            foreach (var item in _tracks) RememberActivePreview(item);
            CancelInterruptedPlayback(); _player.Stop();
            var settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = true, LimiterEnabled = false };
            var tracks = await Task.Run(() => Enumerable.Range(0, 4)
                .Select(index => CreateRenderPriorityTrack(index, index + 1, settings)).ToArray());
            _tracks.Clear(); _tracks.AddRange(tracks); _settings = settings; _renderKeys.Clear();
            foreach (var track in tracks) _renderKeys[track.Id] = RenderKey(track, _settings);
            SyncControls(); PreviewRadio.IsChecked = true; RefreshList(); CompletePreview(_revision);
            var current = tracks[2]; const double position = 4.25;
            Timeline.Playhead = position;

            void Order(IReadOnlyList<AudioTrack> source, double at, params AudioTrack[] expected)
            {
                var before = source.ToArray();
                RequireRenderPriority(RenderOrder(source, at).SequenceEqual(expected) && source.SequenceEqual(before),
                    "Render priority reordered the album or selected the wrong current/following/wrapped work.");
            }
            Order(tracks, position, tracks[2], tracks[3], tracks[0], tracks[1]);
            Order(tracks, 3, tracks[2], tracks[3], tracks[0], tracks[1]);
            Order(tracks, 0, tracks); Order(tracks, -1, tracks); Order(tracks, double.NaN, tracks);
            Order(tracks, 10, tracks[3], tracks[0], tracks[1], tracks[2]);
            Order(tracks, double.PositiveInfinity, tracks[3], tracks[0], tracks[1], tracks[2]);
            RequireRenderPriority(RenderOrder(Array.Empty<AudioTrack>(), 0).Length == 0, "An empty album produced render work.");
            var missing = new AudioTrack { SavedDuration = 2, WorkState = TrackWorkState.Missing };
            AudioTrack[] unresolved = [tracks[0], missing, tracks[2], tracks[3]];
            Order(unresolved, 1, missing, tracks[2], tracks[3], tracks[0]);
            missing.SavedDuration = 0;
            Order(unresolved, 2, missing, tracks[2], tracks[3], tracks[0]);

            // Observe the real queue through its persistent UI rows, rather than
            // duplicating its ordering algorithm or requiring a sound device.
            var started = new List<Guid>();
            Action<TrackListEntry>? progressAction = null;
            var rows = tracks.Select(track => _rowsById[track.Id]).ToArray();
            foreach (var row in rows)
            {
                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (args.PropertyName != nameof(TrackListEntry.Processing)) return;
                    if (row.Processing) started.Add(row.Track.Id);
                    progressAction?.Invoke(row);
                };
                row.PropertyChanged += handler; subscriptions.Add((row, handler));
            }
            _settings = settings with { TargetLufs = -13.25 }; SyncControls(); QueueRender();
            await Drain();
            RequireRenderPriority(started.SequenceEqual(new[] { tracks[2].Id, tracks[3].Id, tracks[0].Id, tracks[1].Id }) &&
                _tracks.SequenceEqual(tracks) && _trackRows.Select(row => row.Track).SequenceEqual(tracks) &&
                rows.All(row => ReferenceEquals(_rowsById[row.Track.Id], row)) &&
                _renderedRevision == _revision && tracks.All(track => track.WorkState == TrackWorkState.Idle && PreviewCurrent(track, _settings)),
                "The actual render queue did not prioritize the cursor track, continue forward, and wrap without changing list order.");

            // If the current file is already cached, it is ready immediately;
            // the remaining worker order still begins with the following track.
            started.Clear(); _settings = _settings with { TargetLufs = -12.5 };
            var readyCurrent = AudioProcessor.Render(current.Original, current.SampleRate, current.Channels,
                current.Envelope, current.TrimDb, EffectiveSettings(current, _settings));
            readyCurrent.Waveform = WaveformPyramid.Build(readyCurrent.Samples, current.Channels);
            _previewCache.Store(RenderKey(current, _settings), readyCurrent, BuildDisplayPeaks(readyCurrent.Waveform));
            SyncControls(); QueueRender(); await Drain();
            RequireRenderPriority(ReferenceEquals(current.Render, readyCurrent) &&
                started.SequenceEqual(new[] { tracks[3].Id, tracks[0].Id, tracks[1].Id }) && _tracks.SequenceEqual(tracks),
                "A cached current preview was rebuilt or changed the following/wrapped work order.");
            // A real playhead move during one file must choose the next file
            // after that work completes. Then move onto an already-ready file:
            // the queue should continue forward from it, skipping ready work.
            started.Clear(); Timeline.Playhead = .25;
            int dynamicExecutions = _renderExecutionCount;
            int completed = 0;
            progressAction = row =>
            {
                if (row.Processing || row.Track.WorkState != TrackWorkState.Idle || !PreviewCurrent(row.Track, _settings)) return;
                if (++completed == 1) Timeline.Playhead = 8.25; // Last pending track.
                else if (completed == 2) Timeline.Playhead = .25; // First track is ready now.
            };
            _settings = _settings with { TargetLufs = -11.875 };
            SyncControls(); QueueRender(); int dynamicRevision = _revision;
            await Drain(); progressAction = null;
            RequireRenderPriority(started.SequenceEqual(new[] { tracks[0].Id, tracks[3].Id, tracks[1].Id, tracks[2].Id }) &&
                completed == tracks.Length && _renderExecutionCount - dynamicExecutions == tracks.Length && _revision == dynamicRevision &&
                _renderedRevision == dynamicRevision && _tracks.SequenceEqual(tracks) &&
                tracks.All(track => track.WorkState == TrackWorkState.Idle && PreviewCurrent(track, _settings)),
                "The render queue did not follow playhead moves between files, retried completed work, or restarted its revision.");
            Timeline.Playhead = position;
            foreach (var (row, handler) in subscriptions) row.PropertyChanged -= handler;
            subscriptions.Clear();

            var resumed = new List<double>();
            void Resume(double at) => resumed.Add(at);
            void Pending()
            {
                CancelInterruptedPlayback(); resumed.Clear(); Timeline.Playhead = position;
                current.TrimDb += .125; current.WorkState = TrackWorkState.Queued;
            }
            void Ready()
            {
                current.Render = AudioProcessor.Render(current.Original, current.SampleRate, current.Channels,
                    current.Envelope, current.TrimDb, EffectiveSettings(current, _settings), inputGainDb: current.InputGainDb,
                    transitionEnvelope: current.TransitionEnvelope);
                current.Render.Waveform = WaveformPyramid.Build(current.Render.Samples, current.Channels);
                current.PreviewPeaks = BuildDisplayPeaks(current.Render.Waveform);
                _renderKeys[current.Id] = RenderKey(current, _settings); current.WorkState = TrackWorkState.Idle;
                // Do not publish here: readiness is consumed through the same
                // production resume helper, with only its device call injected.
            }
            bool Arm(bool playing = true, bool fromOriginal = false) =>
                ArmInterruptedPlayback(position, playing, fromOriginal, _revision);

            Pending(); RequireRenderPriority(Arm() && HasInterruptedPlayback, "Playing interruption lost its resume intent.");
            RequireRenderPriority(!TryResumeInterruptedPlayback(_revision, Resume) && resumed.Count == 0,
                "A queued current track resumed before its preview was ready.");
            current.WorkState = TrackWorkState.Idle;
            RequireRenderPriority(!TryResumeInterruptedPlayback(_revision, Resume), "A stale render key resumed playback.");
            Ready();
            RequireRenderPriority(!TryResumeInterruptedPlayback(_revision - 1, Resume) && HasInterruptedPlayback &&
                TryResumeInterruptedPlayback(_revision, Resume) && resumed.SequenceEqual(new[] { position }) &&
                !HasInterruptedPlayback && !TryResumeInterruptedPlayback(_revision, Resume),
                "A current preview did not resume exactly once at the interrupted local/absolute position, or accepted a stale revision.");

            CancelInterruptedPlayback(); resumed.Clear(); var cached = current.Render; int executions = _renderExecutionCount;
            RequireRenderPriority(Arm() && TryResumeInterruptedPlayback(_revision, Resume) && resumed.SequenceEqual(new[] { position }) &&
                ReferenceEquals(current.Render, cached) && _renderExecutionCount == executions,
                "An already-ready cached preview did not resume immediately without DSP work.");
            resumed.Clear();
            RequireRenderPriority(HandlePlaybackWaiting(current.Id, 3, Resume) && resumed.SequenceEqual(new[] { 3.0 }),
                "A delayed boundary callback could not resume an already-ready preview after the render CTS drained.");

            Pending();
            RequireRenderPriority(!Arm(playing: false) && !HasInterruptedPlayback && !Arm(fromOriginal: true) && !HasInterruptedPlayback,
                "Paused playback or Original mode acquired an automatic render-resume intent.");
            Ready();
            RequireRenderPriority(!TryResumeInterruptedPlayback(_revision, Resume) && resumed.Count == 0,
                "A paused stream resumed when processing completed.");
            RequireRenderPriority(!ArmInterruptedPlayback(10, true, false, _revision), "Album end armed a nonexistent current track.");
            _tracks[2] = new AudioTrack { Id = current.Id, SavedDuration = 3, WorkState = TrackWorkState.Missing };
            RequireRenderPriority(!Arm(), "A missing current track acquired a render-resume intent.");
            _tracks[2] = current;

            Pending(); RequireRenderPriority(Arm(), "The source-replacement fixture did not arm.");
            var replacement = CreateRenderPriorityTrack(8, 3, _settings); replacement.Id = current.Id;
            _tracks[2] = replacement; _renderKeys[current.Id] = RenderKey(replacement, _settings);
            RequireRenderPriority(!TryResumeInterruptedPlayback(_revision, Resume) && resumed.Count == 0,
                "A replacement source with the same slot ID resumed an old source's intent.");
            _tracks[2] = current; Ready();

            foreach (var command in new (string Name, Action Run)[]
            {
                ("Stop", () => StopClick(this, new RoutedEventArgs())),
                ("seek", () => { Timeline.Playhead = position + .25; SeekPlayback(position + .25); }),
                ("cancel", () => CancelClick(this, new RoutedEventArgs())),
                ("new project/source reset", CancelSourceWork),
                ("pause while waiting", () => PlayClick(this, new RoutedEventArgs())),
            })
            {
                Pending(); RequireRenderPriority(Arm(), $"The {command.Name} fixture did not arm.");
                command.Run(); Ready();
                double commandPosition = Timeline.Playhead; string commandStatus = StatusText.Text;
                RequireRenderPriority(!HasInterruptedPlayback && !TryResumeInterruptedPlayback(_revision, Resume) && resumed.Count == 0 &&
                    !HandlePlaybackWaiting(current.Id, 3, Resume) && Timeline.Playhead == commandPosition && StatusText.Text == commandStatus,
                    $"A manual {command.Name} allowed a stale callback to restart playback or overwrite the new cursor/status.");
            }

            // Hold actual DSP behind its gate while two settings revisions replace
            // each other. Only the newest intent may consume the ready current file.
            await _renderGate.WaitAsync(); gateHeld = true;
            Pending(); RequireRenderPriority(Arm(), "The superseded-settings fixture did not arm.");
            _settings = _settings with { TargetLufs = -12.75 }; QueueRender(); int obsoleteRevision = _revision;
            _settings = _settings with { TargetLufs = -12.25 }; QueueRender(); int latestRevision = _revision;
            RequireRenderPriority(latestRevision > obsoleteRevision && HasInterruptedPlayback &&
                !TryResumeInterruptedPlayback(obsoleteRevision, Resume) && resumed.Count == 0,
                "Replacing settings lost the playing intent or accepted the previous revision.");
            Ready();
            RequireRenderPriority(TryResumeInterruptedPlayback(latestRevision, Resume) && resumed.SequenceEqual(new[] { position }) &&
                !TryResumeInterruptedPlayback(latestRevision, Resume), "The newest settings did not resume once at the pinned track position.");
            CancelClick(this, new RoutedEventArgs()); _renderGate.Release(); gateHeld = false;
            await Drain();
            RequireRenderPriority(!_player.IsPlaying && !_player.IsPaused && _tracks.SequenceEqual(tracks),
                "Hardware-free render tests opened playback or changed album order.");
            await VerifyDeferredPrioritySmokeAsync(tracks);
        }
        finally
        {
            foreach (var (row, handler) in subscriptions) row.PropertyChanged -= handler;
            CancelInterruptedPlayback(); _renderCts?.Cancel();
            if (gateHeld) _renderGate.Release();
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in savedKeys) _renderKeys[pair.Key] = pair.Value;
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            Restore(session);
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty;
            if (original) OriginalRadio.IsChecked = true; else if (compare) CompareRadio.IsChecked = true; else PreviewRadio.IsChecked = true;
            Timeline.Playhead = playhead; Timeline.FitAll();
            if (selected != null) SelectTrack(selected);
            else { FileList.SelectedItem = null; Timeline.SelectedTrack = null; }
            UpdateTime(); StatusText.Text = status;
        }
    }

    private async Task VerifyDeferredPrioritySmokeAsync(AudioTrack[] tracks)
    {
        CancelInterruptedPlayback(); _player.Stop();
        _settings = new ProcessingSettings { LevelingEnabled = true, Strength = 0,
            MatchingEnabled = false, LimiterEnabled = false };
        foreach (var track in tracks) { track.Analysis = null; track.Render = null; }
        _renderKeys.Clear(); SyncControls(); RefreshList();
        Timeline.Playhead = .25;
        var order = new List<Guid>();
        var subscriptions = new List<(TrackListEntry Row, PropertyChangedEventHandler Handler)>();
        int completed = 0, before = _renderExecutionCount;
        try
        {
            foreach (var track in tracks)
            {
                var row = _rowsById[track.Id];
                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (args.PropertyName != nameof(TrackListEntry.Processing)) return;
                    if (row.Processing && row.Track.WorkState == TrackWorkState.Analyzing) order.Add(row.Track.Id);
                    else if (!row.Processing && row.Track.Analysis != null && row.Track.WorkState == TrackWorkState.Queued)
                    {
                        if (++completed == 1) Timeline.Playhead = 8.25;
                        else if (completed == 2) Timeline.Playhead = .25;
                    }
                    if (row.Processing && row.Track.WorkState == TrackWorkState.Processing)
                        RequireRenderPriority(tracks.All(item => item.Analysis != null),
                            "Playhead reprioritization bypassed the complete project-analysis barrier.");
                };
                row.PropertyChanged += handler; subscriptions.Add((row, handler));
            }
            QueueRender();
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while ((_renderCts != null || _renderedRevision != _revision) && DateTime.UtcNow < deadline) await Task.Delay(10);
            RequireRenderPriority(order.SequenceEqual(new[] { tracks[0].Id, tracks[3].Id, tracks[1].Id, tracks[2].Id }) &&
                completed == tracks.Length && _renderCts == null && _renderedRevision == _revision &&
                _renderExecutionCount - before == tracks.Length && _tracks.SequenceEqual(tracks) &&
                tracks.All(track => track.Analysis != null && track.WorkState == TrackWorkState.Idle && PreviewCurrent(track, _settings)),
                "Deferred analysis did not follow the playhead between files or rendered against an incomplete detector.");
        }
        finally { foreach (var (row, handler) in subscriptions) row.PropertyChanged -= handler; }
    }

    private static AudioTrack CreateRenderPriorityTrack(int index, int seconds, ProcessingSettings settings)
    {
        const int rate = 8000;
        var samples = Enumerable.Range(0, seconds * rate)
            .Select(frame => (float)(.04 * Math.Sin(2 * Math.PI * (220 + 37 * index) * frame / rate))).ToArray();
        var track = new AudioTrack
        {
            FilePath = $"Render priority {index}.wav", SampleRate = rate, Channels = 1, Original = samples,
            Analysis = LoudnessAnalyzer.Analyze(samples, rate, 1),
            Envelope = [new(0, 0), new(seconds, 0)], WorkState = TrackWorkState.Idle, Status = "Ready",
        };
        track.OriginalWaveform = WaveformPyramid.Build(samples, 1);
        track.OriginalPeaks = BuildDisplayPeaks(track.OriginalWaveform);
        track.Render = AudioProcessor.Render(samples, rate, 1, track.Envelope, 0, settings);
        track.Render.Waveform = WaveformPyramid.Build(track.Render.Samples, 1);
        track.PreviewPeaks = BuildDisplayPeaks(track.Render.Waveform);
        return track;
    }

    private static void RequireRenderPriority(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Smoke test: " + message);
    }
}

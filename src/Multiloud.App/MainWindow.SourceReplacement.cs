using System.IO;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private AudioTrack[] SourceFamily(AudioTrack track)
    {
        if (!_tracks.Contains(track) || track.GeneratedSilenceSeconds.HasValue) return [];
        if (track.MergedSourceId is { } merged)
            return _tracks.Where(item => item.MergedSourceId == merged).ToArray();
        string source = Path.GetFullPath(track.FilePath);
        return _tracks.Where(item => !item.GeneratedSilenceSeconds.HasValue && item.MergedSourceId == null && string.Equals(Path.GetFullPath(item.FilePath), source,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static AudioTrack ReplacementRange(AudioTrack previous, AudioTrack decoded, string path,
        CancellationToken ct)
    {
        double duration = previous.SourceDurationSeconds ?? previous.Duration;
        double firstFrame = Math.Round(previous.SourceStartSeconds * decoded.SampleRate);
        double endFrame = Math.Round((previous.SourceStartSeconds + duration) * decoded.SampleRate);
        // A replacement is a new rendering of the same timeline. Never shorten
        // a saved cut silently when the selected full source does not cover it.
        if (!double.IsFinite(duration) || duration <= 0 || firstFrame < 0 ||
            endFrame <= firstFrame || endFrame > decoded.OriginalFrameCount)
            throw new InvalidOperationException($"The replacement does not cover the saved slice of {previous.Name} " +
                $"({previous.SourceStartSeconds:F3}–{previous.SourceStartSeconds + duration:F3} seconds). " +
                "Choose the complete source render with the same timeline start and enough duration.");
        var replacement = new AudioTrack
        {
            Id = previous.Id, FilePath = path,
            MergedSourceId = decoded.MergedSourceId, MergedSources = decoded.MergedSources,
            SourceStartSeconds = previous.SourceStartSeconds, SourceDurationSeconds = duration,
            SampleRate = decoded.SampleRate, Channels = decoded.Channels,
            Original = decoded.Original, OriginalWaveform = decoded.OriginalWaveform,
            OriginalPeaks = decoded.OriginalPeaks,
            TrimDb = previous.TrimDb, InputGainDb = previous.InputGainDb, TargetLufs = previous.TargetLufs,
            Envelope = previous.Envelope.ToList(), TransitionEnvelope = previous.TransitionEnvelope.ToList(),
            WorkState = TrackWorkState.Queued, Status = "Queued for preview"
        };
        SelectDecodedProjectRange(replacement, replacement.SourceStartSeconds, duration, ct);
        replacement.SavedDuration = replacement.Duration;
        return replacement;
    }

    private void CancelSourceWorkForReplacement(ref SlicePlaybackIntent? listening)
    {
        bool retain = listening?.NavigationVersion == _importNavigationVersion;
        CancelSourceWork();
        // This internal stop invalidates old providers, not the user's wish to
        // continue listening. A later manual navigation still cancels the intent.
        listening = retain ? listening! with { NavigationVersion = _importNavigationVersion } : null;
    }

    /// <summary>
    /// Stages one freshly decoded source and every saved range before publishing
    /// any of them. Failed or cancelled preparation leaves the album untouched.
    /// </summary>
    private async Task<bool> ReplaceSourceFamilyAsync(AudioTrack selected, string path, Func<Task>? beforeCommit = null,
        ProjectTrack? mergedRecipe = null)
    {
        if (_closed || _closingProject || _importing || _exporting || !_tracks.Contains(selected)) return false;
        string replacementPath = Path.GetFullPath(path);
        if (mergedRecipe == null && !File.Exists(replacementPath))
            throw new FileNotFoundException("The selected audio file could not be found.", replacementPath);
        FinishStrengthGesture(false);
        var originals = SourceFamily(selected);
        if (originals.Length == 0) return false;
        var capturedListening = CaptureRollbackPlaybackIntent();
        SlicePlaybackIntent? listening = capturedListening == null ? null : new(capturedListening.PositionSeconds,
            !capturedListening.Paused, capturedListening.Paused, capturedListening.Original, _importNavigationVersion);
        double position = capturedListening?.PositionSeconds ?? Timeline.Playhead;
        Guid? selectedId = Selected?.Id;
        var cts = new CancellationTokenSource(); var ct = cts.Token;
        _importCts = cts; _importing = true; _atomicSourceOperation = true;
        ExportButton.IsEnabled = false;
        bool gateHeld = false, committed = false;
        try
        {
            CancelSourceWorkForReplacement(ref listening);
            Timeline.Playhead = position; UpdateTime();
            SetBusy($"Loading replacement: {Path.GetFileName(replacementPath)}…", true);
            await _renderGate.WaitAsync(ct); gateHeld = true;
            // Deliberately bypass the project-opening decode cache. Choosing the
            // same path must read an updated DAW render rather than the old PCM.
            var decoded = await Task.Run(() =>
            {
                if (mergedRecipe != null) return DecodeMergedProjectSource(mergedRecipe, ct);
                var loaded = AudioFileService.Load(replacementPath, ct);
                loaded.OriginalWaveform = WaveformPyramid.Build(loaded.Original, loaded.Channels, ct);
                loaded.OriginalPeaks = BuildDisplayPeaks(loaded.OriginalWaveform, ct);
                return loaded;
            }, ct);
            ct.ThrowIfCancellationRequested();
            var replacements = await Task.Run(() =>
            {
                var direct = originals.Select(old => (Old: old, Replacement: ReplacementRange(old, decoded, replacementPath, ct))).ToList();
                if (selected.MergedSourceId == null)
                    direct.AddRange(StageDependentMergedReplacements(selected.FilePath, replacementPath, decoded, ct));
                return direct.ToArray();
            }, ct);
            bool processing = !ModulesBypassed(_settings);
            if (EstimateWorkingAudioBytes(_tracks.Concat(replacements.Select(pair => pair.Replacement)),
                _backgroundLayer.Clips.Select(clip => clip.Source), processing) > 12L * 1024 * 1024 * 1024)
                throw new InvalidOperationException("Replacing this source would exceed the 12 GB working audio budget.");
            foreach (var pair in replacements)
            {
                ct.ThrowIfCancellationRequested();
                // The analysis is independent of authored gain/envelope edits.
                // A module change while loading is honored at the next yield.
                if (ModulesBypassed(_settings)) continue;
                SetBusy($"Analyzing replacement: {pair.Old.Name}…", true);
                var replacement = pair.Replacement;
                replacement.Analysis = await Task.Run(() => LoudnessAnalyzer.Analyze(replacement.GetOriginalRegion(ct),
                    replacement.SampleRate, replacement.Channels, ct), ct);
            }
            if (beforeCommit != null) await beforeCommit().WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (_closed || _closingProject || originals.Any(old => !_tracks.Contains(old))) return false;

            // Capture current controls immediately before this one structural
            // edit. Explicit settings/envelope edits made while loading survive
            // both the replacement and an Undo of the replacement itself.
            FinishStrengthGesture(false);
            var latestListening = CaptureRollbackPlaybackIntent();
            if (latestListening != null)
                listening = new(latestListening.PositionSeconds, !latestListening.Paused, latestListening.Paused,
                    latestListening.Original, _importNavigationVersion);
            else if (listening?.NavigationVersion != _importNavigationVersion) listening = null;
            position = latestListening?.PositionSeconds ?? Timeline.Playhead;
            var authored = _projectEnvelope.ToArray(); var suggested = _projectSuggestion.ToArray();
            PushUndo();
            CancelSourceWorkForReplacement(ref listening); _plannedRenderKeys = null;
            foreach (var (old, replacement) in replacements)
            {
                RememberActivePreview(old);
                replacement.TrimDb = old.TrimDb; replacement.InputGainDb = old.InputGainDb;
                replacement.TargetLufs = old.TargetLufs;
                replacement.Envelope = old.Envelope.ToList(); replacement.TransitionEnvelope = old.TransitionEnvelope.ToList();
                _tracks[_tracks.IndexOf(old)] = replacement;
                _renderKeys.Remove(old.Id);
            }
            RestoreProjectEnvelope(authored, suggested);
            // In live bypass, defer source analysis until a module needs it,
            // while retaining this authored curve when that analysis arrives.
            _projectEnvelopeLoaded = _tracks.Any(track => track.Analysis == null);
            _dirty = true; committed = true;
            Timeline.Playhead = position;
            if (selectedId is { } id && _tracks.FirstOrDefault(track => track.Id == id) is { } selection)
                SelectTrack(selection);
            RefreshBackground(); RefreshList(); UpdateTime();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        finally
        {
            if (gateHeld) _renderGate.Release();
            _importing = false; _atomicSourceOperation = false;
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            cts.Dispose();
            if (!_closed && !_closingProject)
            {
                if (committed) BeginProcessingTransaction();
                QueueRender();
                RearmSlicePlaybackAfterPreparation(listening);
                RefreshBackground(); RefreshList(); UpdateTime();
            }
        }
    }
}

using System.IO;
using System.Windows;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyCloseProjectSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        string? path = _projectPath, output = _outputDeviceId;
        bool dirty = _dirty, needsCopy = _backgroundProjectNeedsCopy, iconsOnly = _iconsOnlyButtonStyle;
        string title = Title, status = StatusText.Text, theme = ThemeService.Mode;
        bool? original = OriginalRadio.IsChecked, compare = CompareRadio.IsChecked;
        bool? envelope = WaveformEnvelopeCheck.IsChecked, gains = ShowGains.IsChecked;
        bool? visible = BackgroundVisibilityCheck.IsChecked, solo = BackgroundSoloButton.IsChecked;
        bool? custom = CustomBoundariesCheck.IsChecked, audition = AuditionMatchCheck.IsChecked;
        double playhead = Timeline.Playhead; var selected = Selected;
        var musicHeight = AudioLanes.RowDefinitions[0].Height; var backgroundHeight = AudioLanes.RowDefinitions[2].Height;
        var rememberedMusicHeight = _visibleMusicHeight; var rememberedBackgroundHeight = _visibleBackgroundHeight;
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineCloseProject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Close project smoke: " + message); }
        void RequireEmpty()
        {
            Require(_tracks.Count == 0 && _trackRows.Count == 0 && _rowsById.Count == 0 &&
                _backgroundLayer.Clips.Count == 0 && _backgroundLayer.Enabled && _backgroundLayer.GainDb == 0 &&
                _backgroundSegments.Count == 0 && _backgroundOriginals.Count == 0 &&
                _undo.Count == 0 && _redo.Count == 0 && _previewCache.Count == 0 && _renderKeys.Count == 0,
                "audio, source caches, list rows or history remained after closing.");
            Require(_settings == new ProcessingSettings() && !_floatExport && CaptureExportOptions() == new ProjectExportOptions() &&
                _projectEnvelope.Count == 0 && _projectSuggestion.Count == 0 && CaptureCustomBoundaries().Count == 0 &&
                _projectPath == null && !_backgroundProjectNeedsCopy && !_dirty && !_closingProject && !_importing && !_openingProject,
                "project metadata, processing defaults or pending work remained after closing.");
            Require(Timeline.Playhead == 0 && Timeline.SelectedTrack == null && Selected == null &&
                _playbackTracks.Length == 0 && !_player.IsPlaying && !_player.IsPaused &&
                !HasInterruptedPlayback && !HasBackgroundOriginalResume && !HasImportPlaybackIntent && !_playbackRequested,
                "transport or pending playback intent remained after closing.");
            Require(PreviewRadio.IsChecked == true && WaveformEnvelopeCheck.IsChecked == true && ShowGains.IsChecked == true &&
                BackgroundVisibilityCheck.IsChecked == true && BackgroundSoloButton.IsChecked == false &&
                AuditionMatchCheck.IsChecked == false && !Timeline.SliceMode && CustomBoundariesCheck.IsChecked == false &&
                RebuildPreviewButton.Visibility == Visibility.Collapsed && WorkProgress.Visibility == Visibility.Collapsed &&
                CancelButton.Visibility == Visibility.Collapsed && !ExportButton.IsEnabled &&
                ReleaseSummary.Text == "No audio loaded" && CursorPositionText.Text.Length == 0,
                "workspace controls or work indicators did not return to startup state.");
            Require(_outputDeviceId == output && ThemeService.Mode == theme && _iconsOnlyButtonStyle == iconsOnly && (Content as UIElement)?.IsEnabled == true,
                "closing changed app preferences or left the workspace disabled.");
        }
        try
        {
            CancelInterruptedPlayback(); _player.Stop();
            var tracks = _tracks.ToArray(); int revision = _revision, executions = _renderExecutionCount;
            _dirty = true; int saveCalls = 0;
            Require(!await CloseProjectAsync(() => ResolveSaveConfirmation(UnsavedProjectChoice.Cancel, () => { saveCalls++; return true; })) &&
                _tracks.SequenceEqual(tracks) && _revision == revision && _renderExecutionCount == executions && _dirty && saveCalls == 0,
                "Cancel changed the project or performed a save.");
            Require(!await CloseProjectAsync(() => ResolveSaveConfirmation(UnsavedProjectChoice.Save, () => { saveCalls++; return false; })) &&
                _tracks.SequenceEqual(tracks) && _revision == revision && _dirty && saveCalls == 1,
                "a cancelled/failed Save still closed the project.");

            _projectPath = Path.Combine(directory, "Open project.loudline"); _backgroundProjectNeedsCopy = true;
            LoadCustomBoundaries([1]); LoadExportOptions(new() { AsOneFile = true, BoundaryFadeMs = 3 });
            _floatExport = true;
            WaveformEnvelopeCheck.IsChecked = false; ShowGains.IsChecked = false;
            CustomBoundariesCheck.IsChecked = true;
            AuditionMatchCheck.IsChecked = true; BackgroundVisibilityCheck.IsChecked = false;
            Timeline.Playhead = 1;
            await _renderGate.WaitAsync();
            Task<bool> closing;
            try
            {
                _settings = new ProcessingSettings { TargetLufs = -17 };
                QueueRender();
                closing = CloseProjectAsync(() => ResolveSaveConfirmation(UnsavedProjectChoice.Save, () => { saveCalls++; return true; }));
                Require(!closing.IsCompleted && _closingProject && (Content as UIElement)?.IsEnabled == false,
                    "Close failed to wait for active work or left editing enabled during teardown.");
            }
            finally { _renderGate.Release(); }
            Require(await closing && saveCalls == 2, "a successful Save did not permit closing.");
            RequireEmpty();
            await Task.Delay(550); // Previously queued preview continuations must stay cancelled.
            RequireEmpty();

            const int rate = 12000;
            var samples = Enumerable.Range(0, rate * 12).Select(index => (float)(.1 * Math.Sin(index * 2 * Math.PI * 440 / rate))).ToArray();
            string file = Path.Combine(directory, "New source.wav");
            AudioFileService.ExportWav(new AudioTrack { FilePath = file + ".source", SampleRate = rate, Channels = 1,
                Render = new RenderResult { Samples = samples } }, file, true);
            var importing = ImportFilesAsync([file]);
            Require(_importing && _tracks.Count == 1, "source fixture did not publish its pending music slot.");
            Require(await CloseProjectAsync(() => true), "Close could not cancel an active music import.");
            await importing; RequireEmpty();
            var backgroundImport = ImportBackgroundFilesAsync([file], 0);
            Require(_importing && _backgroundLayer.Clips.Count == 1, "source fixture did not publish its pending background slot.");
            Require(await CloseProjectAsync(() => true), "Close could not drain a background metadata/decode operation.");
            await backgroundImport; RequireEmpty();
            Require(await CloseProjectAsync(() => true), "closing an already empty project was not safe.");
            RequireEmpty();
        }
        finally
        {
            CancelSourceWork(); _importCts?.Cancel();
            while (_importing || _openingProject) await Task.Delay(10);
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            Restore(session); await WaitForSmokePreviewAsync();
            _syncing = true;
            try
            {
                OriginalRadio.IsChecked = original; CompareRadio.IsChecked = compare;
                if (original != true && compare != true) PreviewRadio.IsChecked = true;
                WaveformEnvelopeCheck.IsChecked = envelope; ShowGains.IsChecked = gains;
                BackgroundSoloButton.IsChecked = solo; AuditionMatchCheck.IsChecked = audition;
                CustomBoundariesCheck.IsChecked = custom;
                BackgroundVisibilityCheck.IsChecked = visible;
                AudioLanes.RowDefinitions[0].Height = musicHeight; AudioLanes.RowDefinitions[2].Height = backgroundHeight;
                _visibleMusicHeight = rememberedMusicHeight; _visibleBackgroundHeight = rememberedBackgroundHeight;
            }
            finally { _syncing = false; }
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _projectPath = path; _backgroundProjectNeedsCopy = needsCopy; _dirty = dirty;
            Title = title; StatusText.Text = status; Timeline.Playhead = playhead;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            UpdateTime();
            Directory.Delete(directory, recursive: true);
        }
    }
}

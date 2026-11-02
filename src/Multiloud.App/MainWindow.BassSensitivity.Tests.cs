using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBassSensitivitySmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty; string status = StatusText.Text;
        var tracks = _tracks.ToArray(); var analyses = tracks.Select(track => track.Analysis).ToArray();
        var energies = tracks.Select(track => track.Analysis!.LevelingEnergy).ToArray();
        var originals = tracks.Select(track => track.Original).ToArray();
        var envelopes = tracks.Select(track => track.Envelope).ToArray();
        var transitions = tracks.Select(track => track.TransitionEnvelope).ToArray();
        var renders = tracks.Select(track => track.Render).ToArray();
        var peaks = tracks.Select(track => track.PreviewPeaks).ToArray();
        int executions = _renderExecutionCount, revision = _revision, history = 0;
        FrameworkElement? dialog = null;
        bool DraftUnchanged() => ReferenceEquals(_settings, session.Settings) && _revision == revision &&
            _renderExecutionCount == executions && _undo.Count == history && _dirty == dirty &&
            tracks.Select((track, index) => ReferenceEquals(track.Analysis, analyses[index]) &&
                ReferenceEquals(track.Analysis!.LevelingEnergy, energies[index]) && ReferenceEquals(track.Original, originals[index]) &&
                ReferenceEquals(track.Envelope, envelopes[index]) && ReferenceEquals(track.TransitionEnvelope, transitions[index]) &&
                ReferenceEquals(track.Render, renders[index]) && _renderKeys[track.Id] == keys.Single(pair => pair.Key == track.Id).Value).All(value => value);
        try
        {
            // Isolate the action count from the session's bounded 12-entry history.
            _undo.Clear(); _redo.Clear();
            RequireBassSensitivity(new ProcessingSettings().BassSensitivity == .25 && new ProjectData().Version == 9 &&
                new ProjectData().Settings.BassSensitivity == .25, "New projects lost the reduced-bass default or format version.");
            LevelingOptionsClick(LevelingOptionsButton, new RoutedEventArgs());
            dialog = _settingsFlyoutContent ?? throw new InvalidOperationException("Smoke test: leveling dropdown did not open.");
            await Dispatcher.InvokeAsync(() => dialog.UpdateLayout(), DispatcherPriority.ContextIdle);
            var slider = (Slider)dialog.FindName("BassSensitivitySlider");
            var label = (TextBlock)dialog.FindName("BassSensitivityValue");
            var apply = (Button)dialog.FindName("LevelingApplyButton");
            RequireBassSensitivity(slider.Minimum == .25 && slider.Maximum == 1 && slider.Value == _settings.BassSensitivity &&
                slider.ActualWidth > 0 && slider.ActualHeight > 0 && apply.ActualWidth > 0 &&
                Descendants<TextBlock>(dialog).Any(text => text.Text == "Reduced") &&
                Descendants<TextBlock>(dialog).Any(text => text.Text == "Normal"),
                "Advanced leveling lost its bounded native bass control or endpoint labels.");
            if (screenshot != null)
                CaptureVisual((FrameworkElement)_settingsFlyout!.Child, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-leveling-options.png"));
            foreach (double value in new[] { .4, .7, 1.0, .25 })
            {
                slider.Value = value;
                RequireBassSensitivity(label.Text == (value * 100).ToString("0.#") + "%" && DraftUnchanged(),
                    "Dragging the draft bass preference changed audio, suggestions, settings, or undo history.");
            }
            await Task.Delay(650);
            RequireBassSensitivity(DraftUnchanged(), "A draft bass preference queued delayed DSP work.");
            slider.Value = session.Settings.BassSensitivity;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequireBassSensitivity(_settingsFlyout == null && DraftUnchanged() && !ApplyLevelingOptions(session.Settings),
                "Unchanged Apply rebuilt previews or created an undo step.");
            dialog = null;

            double changed = session.Settings.BassSensitivity == 1 ? .25 : 1;
            RequireBassSensitivity(ApplyLevelingOptions(_settings with { BassSensitivity = changed }) &&
                _settings.BassSensitivity == changed && _undo.Count == history + 1 &&
                !ApplyLevelingOptions(_settings) && _undo.Count == history + 1,
                "Applying bass sensitivity did not create exactly one undoable action.");
            await WaitForSmokePreviewAsync();
            var globalSuggestion = ProjectEnvelopeDesigner.Design(tracks, _settings);
            var expectedGlobal = EnvelopeDesigner.Retarget(session.ProjectEnvelope, session.ProjectSuggestion, globalSuggestion, tracks.Sum(track => track.Duration));
            bool SameCurve(IReadOnlyList<EnvelopePoint> first, IReadOnlyList<EnvelopePoint> second) =>
                first.Select(point => point.Time).Concat(second.Select(point => point.Time)).All(time =>
                    Math.Abs(AudioProcessor.EnvelopeGainAt(first, time) - AudioProcessor.EnvelopeGainAt(second, time)) < 1e-9);
            RequireBassSensitivity(SameCurve(_projectEnvelope, expectedGlobal) && tracks.Select((track, index) => ReferenceEquals(track.Analysis, analyses[index]) &&
                ReferenceEquals(track.Analysis!.LevelingEnergy, energies[index]) && ReferenceEquals(track.Original, originals[index]) &&
                SameCurve(track.Envelope, ProjectEnvelopeDesigner.Crop(expectedGlobal, tracks.Take(index).Sum(item => item.Duration), track.Duration))).All(value => value),
                "Bass Apply rescanned/replaced source analysis or failed to regenerate suggestions from cached energies.");
            var changedRenders = tracks.Select(track => track.Render).ToArray();
            int beforeUndo = _renderExecutionCount;
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            RequireBassSensitivity(_settings.BassSensitivity == session.Settings.BassSensitivity && _renderExecutionCount == beforeUndo &&
                tracks.Select((track, index) => ReferenceEquals(track.Render, renders[index]) &&
                    track.Envelope.SequenceEqual(session.Tracks[index].Envelope) &&
                    track.TransitionEnvelope.SequenceEqual(session.Tracks[index].Transitions)).All(value => value),
                "Undo did not restore the prior bass preference, authored curves, and cached audio without DSP.");
            RedoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            RequireBassSensitivity(_settings.BassSensitivity == changed && _renderExecutionCount == beforeUndo &&
                tracks.Select((track, index) => ReferenceEquals(track.Render, changedRenders[index])).All(value => value),
                "Redo did not reuse the applied bass preference and cached audio.");
            var saved = CreateProjectData(Path.Combine(Path.GetTempPath(), "Bass-roundtrip.loudline"));
            var restored = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(saved));
            RequireBassSensitivity(saved.Version == 9 && restored.Version == 9 && restored.Settings.BassSensitivity == changed,
                "The applied bass preference did not survive a current project round trip.");

            int beforeInvalid = _undo.Count; var settings = _settings;
            bool rejected = false;
            try { ApplyLevelingOptions(settings with { BassSensitivity = .24 }); }
            catch (InvalidDataException) { rejected = true; }
            RequireBassSensitivity(rejected && ReferenceEquals(_settings, settings) && _undo.Count == beforeInvalid && _renderExecutionCount == beforeUndo,
                "An invalid advanced bass value changed settings, history, or audio work.");
        }
        finally
        {
            CloseSettingsFlyout();
            _renderCts?.Cancel(); await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            for (int index = 0; index < session.Tracks.Count; index++)
            {
                var item = session.Tracks[index];
                item.Track.Render = renders[index]; item.Track.PreviewPeaks = peaks[index];
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                {
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
                }
            }
            Restore(session);
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _dirty = dirty; StatusText.Text = status;
        }
    }

    private static void RequireBassSensitivity(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Smoke test: " + message);
    }
}

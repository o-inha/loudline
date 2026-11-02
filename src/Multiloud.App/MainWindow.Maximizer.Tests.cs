using Multiloud.App.Services;
using Multiloud.Core;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyMaximizerSmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        var savedSettings = _settings;
        var undo = _undo.ToArray();
        var redo = _redo.ToArray();
        bool dirty = _dirty;
        _undo.Clear(); _redo.Clear();
        try
        {
            if (!_settings.LimiterEnabled)
            {
                _settings = _settings with { LimiterEnabled = true };
                SyncControls(); QueueRender(); await WaitForSmokePreviewAsync();
            }
            SyncMaximizerControls();
            var enabledSettings = _settings;
            var renders = _tracks.Select(track => track.Render).ToArray();
            int revision = _revision, executions = _renderExecutionCount;
            if (ApplyMaximizerSettings(_settings.MaximizerStyle, _settings.MaximizerCharacter, _settings.TransientPreservation) ||
                _revision != revision || _renderExecutionCount != executions || _undo.Count != 0)
                throw new InvalidOperationException("Smoke test: unchanged Maximizer controls changed edit history or processing.");

            var alternate = _settings.MaximizerStyle == MaximizerStyle.Transparent ? MaximizerStyle.Punchy : MaximizerStyle.Transparent;
            bool applied = false;
            var content = CreateMaximizerOptionsContent(() => applied = true);
            var choice = (RadioButton)content.FindName("Maximizer" + alternate + "Button");
            choice.IsChecked = true;
            if (_settings.MaximizerStyle == alternate || _revision != revision || _undo.Count != 0)
                throw new InvalidOperationException("Smoke test: a draft protection style applied before pressing Apply.");
            ((Button)content.FindName("MaximizerApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!applied) throw new InvalidOperationException("Smoke test: applying peak options did not close its draft.");
            if (_settings.MaximizerStyle != alternate || _revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: Maximizer style selection did not commit exactly one undoable preview edit.");
            ApplyMaximizerSettings(_settings.MaximizerStyle, _settings.MaximizerCharacter, _settings.TransientPreservation);
            if (_revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: repeating the selected Maximizer style queued duplicate work.");
            await WaitForSmokePreviewAsync();
            if (screenshot is not null)
            {
                UpdateMaximizerMeter();
                CaptureVisual(ProcessingPanel, Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-maximizer.png"));
            }
            int beforeUndo = _renderExecutionCount;
            UndoClick(this, new RoutedEventArgs());
            await WaitForSmokePreviewAsync();
            if (_settings != enabledSettings || _renderExecutionCount != beforeUndo ||
                _tracks.Where((track, index) => !ReferenceEquals(track.Render, renders[index])).Any())
                throw new InvalidOperationException("Smoke test: Maximizer Undo did not restore the exact cached audio variant.");

            // A bypassed processor can be configured without invalidating audio.
            _settings = _settings with { LimiterEnabled = false };
            SyncControls(); QueueRender(); await WaitForSmokePreviewAsync();
            revision = _revision; executions = _renderExecutionCount;
            int history = _undo.Count;
            renders = _tracks.Select(track => track.Render).ToArray();
            var keys = _tracks.Select(track => PreviewCache.CreateKey(track, _settings)).ToArray();
            if (!ApplyMaximizerSettings(alternate, .27, .81, .73, true, 2) || _revision != revision || _renderExecutionCount != executions || _undo.Count != history + 1 ||
                _tracks.Where((track, index) => !ReferenceEquals(track.Render, renders[index]) || PreviewCache.CreateKey(track, _settings) != keys[index]).Any())
                throw new InvalidOperationException("Smoke test: bypassed Maximizer options rebuilt or invalidated unchanged audio.");
            if (ApplyMaximizerSettings(alternate, .27, .81, .73, true, 2) || _revision != revision || _undo.Count != history + 1)
                throw new InvalidOperationException("Smoke test: repeated bypassed Maximizer options created another edit.");

            var restored = JsonSerializer.Deserialize<ProjectData>(JsonSerializer.Serialize(
                CreateProjectData(Path.Combine(Path.GetTempPath(), "Maximizer-roundtrip.loudline"))));
            if (restored?.Settings.MaximizerStyle != alternate || restored.Settings.MaximizerCharacter != .27 ||
                restored.Settings.TransientPreservation != .81 || restored.Settings.LimiterEnabled ||
                restored.Settings.BassProtection != .73 || !restored.Settings.DynamicsGuardEnabled || restored.Settings.MaxSustainedReductionDb != 2)
                throw new InvalidOperationException("Smoke test: project serialization lost Maximizer style, timing or transient settings.");

            VerifyMaximizerMeterSmoke();
        }
        finally
        {
            _settings = savedSettings;
            SyncControls(); QueueRender(); await WaitForSmokePreviewAsync();
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty;
            UpdateMaximizerMeter();
        }
    }

    private void VerifyMaximizerMeterSmoke()
    {
        var settings = _settings;
        try
        {
            _settings = _settings with { LimiterEnabled = true };
            var measured = new RenderResult
            {
                Samples = [0], MaxReductionDb = 8.25,
                ReductionTrace = [new(0, 1), new(.1, 4), new(.2, 30)]
            };
            double Readout() => double.Parse(MaximizerReductionText.Text[..^3], CultureInfo.CurrentCulture);
            DisplayMaximizerReduction(measured, .15);
            if (Readout() != 4 || Math.Abs(MaximizerReductionFill.Width - MaximizerReductionBar.Width * 4 / 24) > 1e-9)
                throw new InvalidOperationException("Smoke test: Maximizer meter did not follow the current 100 ms reduction block.");
            DisplayMaximizerReduction(measured, .2);
            if (Readout() != 30 || MaximizerReductionFill.Width != MaximizerReductionBar.Width)
                throw new InvalidOperationException("Smoke test: Maximizer meter overflowed its bounded display or truncated the measured reduction.");
            DisplayMaximizerReduction(measured, null);
            if (Math.Abs(MaximizerReductionFill.Width - MaximizerReductionBar.Width * 8.25 / 24) > 1e-9)
                throw new InvalidOperationException("Smoke test: idle Maximizer meter did not display the track maximum reduction.");
            _settings = _settings with { LimiterEnabled = false };
            DisplayMaximizerReduction(measured, .15);
            if (Readout() != 0 || MaximizerReductionFill.Width != 0)
                throw new InvalidOperationException("Smoke test: bypassed Maximizer retained an active reduction meter.");
        }
        finally { _settings = settings; UpdateMaximizerMeter(); }
    }
}

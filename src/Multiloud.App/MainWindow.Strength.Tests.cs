using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyStrengthGesturesSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var savedUndo = _undo.ToArray();
        var savedRedo = _redo.ToArray();
        bool savedDirty = _dirty;
        _undo.Clear();
        try
        {
            foreach (var slider in new[] { StrengthSlider, MatchingStrengthSlider })
            {
                var settings = _settings;
                var renders = _tracks.Select(track => track.Render).ToArray();
                var analyses = _tracks.Select(track => track.Analysis).ToArray();
                var work = _tracks.Select(track => (track.WorkState, track.Status)).ToArray();
                int revision = _revision, executions = _renderExecutionCount;
                int undoCount = _undo.Count, redoCount = _redo.Count;
                bool dirty = _dirty, exportEnabled = ExportButton.IsEnabled;
                string marker = PreviewLabel.Text;
                double original = slider.Value;
                double changed = original > .6 ? .437 : .863;
                bool matching = ReferenceEquals(slider, MatchingStrengthSlider);
                double DisplayValue() => matching ? Timeline.Settings.MatchingStrength : Timeline.Settings.Strength;
                void RequireUnchanged(string action)
                {
                    if (_settings != settings || _revision != revision || _renderExecutionCount != executions ||
                        _undo.Count != undoCount || _redo.Count != redoCount || _dirty != dirty ||
                        ExportButton.IsEnabled != exportEnabled || PreviewLabel.Text != marker ||
                        _tracks.Where((track, index) => !ReferenceEquals(track.Render, renders[index]) ||
                            !ReferenceEquals(track.Analysis, analyses[index]) || (track.WorkState, track.Status) != work[index]).Any())
                        throw new InvalidOperationException($"Smoke test: {slider.Name} {action} altered committed processing, readiness or edit history.");
                }

                // Exercise WPF's class handlers as well as our instance handler.
                // A built-in move-to-point action must not commit ahead of capture.
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                });
                if (!ReferenceEquals(_dragStrengthSlider, slider))
                    throw new InvalidOperationException("Smoke test: the routed slider press bypassed its draft gesture.");
                RequireUnchanged("routed mouse press");
                FinishStrengthGesture(false);
                RequireUnchanged("routed press cancellation");

                // A press without movement is not an edit, even when held beyond
                // the normal preview coalescing interval.
                BeginStrengthGesture(slider);
                await Task.Delay(650);
                RequireUnchanged("stationary press");
                FinishStrengthGesture(true);
                RequireUnchanged("stationary release");

                BeginStrengthGesture(slider);
                long redraws = Timeline.PerformanceCounters.WaveformRedraws;
                slider.Value = changed;
                Timeline.FlushPendingRendering();
                if (Math.Abs(DisplayValue() - changed) > 1e-9 || Timeline.PerformanceCounters.WaveformRedraws <= redraws)
                    throw new InvalidOperationException($"Smoke test: {slider.Name} did not repaint its live draft.");
                if (matching)
                {
                    var track = _tracks[0];
                    double expected = Math.Clamp((track.TargetLufs ?? settings.TargetLufs) - track.Render!.PreMatchLufs, -36, 36) * changed;
                    if (Math.Abs(Timeline.EstimatedMatchGain(track) - expected) > 1e-8)
                        throw new InvalidOperationException("Smoke test: matching drag estimate used the committed percentage.");
                }
                RefreshList();
                if (Math.Abs(DisplayValue() - changed) > 1e-9)
                    throw new InvalidOperationException("Smoke test: background/status refresh discarded the active Strength draft.");
                await Task.Delay(650);
                RequireUnchanged("held live draft");
                slider.Value = original;
                FinishStrengthGesture(true);
                await Task.Delay(550);
                RequireUnchanged("away-and-back release");
                if (slider.Value != original || DisplayValue() != original)
                    throw new InvalidOperationException("Smoke test: reverted Strength gesture lost its original value.");

                // Raw floating point drift that displays the same percentage must
                // preserve the original precise value and cached rendered buffers.
                BeginStrengthGesture(slider);
                slider.Value = Math.Clamp(original + (original < 1 ? 1 : -1) * 1e-8, 0, 1);
                FinishStrengthGesture(true);
                RequireUnchanged("equal displayed percentage");
                if (slider.Value != original) throw new InvalidOperationException("Smoke test: an unchanged percentage retained invisible numeric drift.");

                BeginStrengthGesture(slider);
                slider.Value = changed;
                FinishStrengthGesture(false);
                RequireUnchanged("canceled capture");
                if (slider.Value != original || DisplayValue() != original)
                    throw new InvalidOperationException("Smoke test: canceled Strength capture retained a draft.");

                BeginStrengthGesture(slider);
                slider.Value = changed;
                await Task.Delay(650);
                RequireUnchanged("final held adjustment");
                FinishStrengthGesture(true);
                if (_undo.Count != undoCount + 1 || _revision != revision + 1 ||
                    Math.Abs((matching ? _settings.MatchingStrength : _settings.Strength) - changed) > 1e-9)
                    throw new InvalidOperationException("Smoke test: changed Strength release did not commit exactly one edit and preview request.");
                int committedRevision = _revision, committedUndo = _undo.Count;
                FinishStrengthGesture(true);
                if (_revision != committedRevision || _undo.Count != committedUndo)
                    throw new InvalidOperationException("Smoke test: capture release committed a Strength gesture twice.");
                await WaitForSmokePreviewAsync();
                UndoClick(this, new RoutedEventArgs());
                await WaitForSmokePreviewAsync();
                if (_settings != settings || _tracks.Where((track, index) => !ReferenceEquals(track.Render, renders[index])).Any())
                    throw new InvalidOperationException("Smoke test: one Strength Undo did not restore the original cached previews.");
            }
        }
        finally
        {
            FinishStrengthGesture(false);
            _undo.Clear(); foreach (var entry in savedUndo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in savedRedo.Reverse()) _redo.Push(entry);
            _dirty = savedDirty;
        }
    }
}

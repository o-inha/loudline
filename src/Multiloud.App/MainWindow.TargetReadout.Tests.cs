using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyTargetReadoutSmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        FinishStrengthGesture(false); Keyboard.ClearFocus();
        var track = _tracks[0]; var selected = Selected;
        var savedSettings = _settings; var savedRender = track.Render; var savedAnalysis = track.Analysis;
        var savedEnvelope = track.Envelope; var savedTransitions = track.TransitionEnvelope;
        var savedInput = track.InputGainDb; var savedTrim = track.TrimDb; var savedTarget = track.TargetLufs;
        var savedComputed = _targetComputedMode; var status = StatusText.Text;
        int revision = _revision, executions = _renderExecutionCount, undoCount = _undo.Count;
        bool dirty = _dirty;
        var fixture = new ProcessingSettings { TargetLufs = -14, MatchingStrength = 1, LevelingEnabled = false, LimiterEnabled = false };
        RenderResult Ready(double strength, double output) => new()
        {
            Samples = track.Original, Waveform = track.OriginalWaveform, PreMatchLufs = -20,
            RenderedMatchingStrength = strength, RenderedTargetLufs = -14, MatchGainDb = 6 * strength,
            OutputLufs = output, RenderedEnvelope = Array.Empty<EnvelopePoint>()
        };
        void RefreshFixture()
        {
            // These synthetic track writes bypass the real editor, whose
            // setters invalidate metadata before publishing the new readout.
            // Equal processing settings intentionally keep their cached metrics.
            Timeline.Refresh();
            ApplyTimelineSettings();
        }
        void Require(double value, bool computed, string action)
        {
            var color = ((SolidColorBrush)TargetBox.Foreground).Color;
            var expected = ((SolidColorBrush)FindResource(computed ? "MonitorActiveTextBrush" : "TextBrush")).Color;
            if (!double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var displayed) ||
                Math.Abs(displayed - value) > .051 || IsComputedTargetReadout != computed || color != expected || _settings.TargetLufs != -14)
                throw new InvalidOperationException($"Smoke test: {action} target readout={TargetBox.Text}, computed={IsComputedTargetReadout}, goal={_settings.TargetLufs}.");
        }
        try
        {
            track.Analysis = new AnalysisResult { IntegratedLufs = -20 };
            track.Envelope = [new(0, 0), new(track.Duration, 0)]; track.TransitionEnvelope = [];
            track.InputGainDb = track.TrimDb = 0; track.TargetLufs = null; track.Render = Ready(1, -14);
            _settings = fixture; ResetTargetReadout(); SyncControls(); RefreshList(); SelectTrack(track);
            Require(-14, false, "initial goal");

            BeginStrengthGesture(MatchingStrengthSlider); MatchingStrengthSlider.Value = .5;
            Require(-17, true, "half-strength draft");
            TargetBox.BringIntoView();
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); ProcessingPanel.UpdateLayout(); }, DispatcherPriority.ContextIdle);
            Require(-17, true, "painted half-strength draft");
            // CI's isolated textbox brush was entirely transparent. Capture its
            // native containing panel, as toolbar QA does, and inspect only the
            // field's transformed interior so other amber controls cannot pass.
            var targetBounds = TargetBox.TransformToAncestor(ProcessingPanel).TransformBounds(new Rect(TargetBox.RenderSize));
            var targetImage = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(ProcessingPanel.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(ProcessingPanel.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            var targetVisual = new DrawingVisual();
            using (var drawing = targetVisual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(ProcessingPanel) { ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(ProcessingPanel.RenderSize), Stretch = Stretch.Fill }, null, new Rect(ProcessingPanel.RenderSize));
            targetImage.Render(targetVisual);
            if (screenshot != null)
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(targetImage));
                using var stream = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-target-estimate.png"));
                encoder.Save(stream);
            }
            var targetPixels = ComparisonPixels(targetImage);
            var amber = ((SolidColorBrush)FindResource("MonitorActiveTextBrush")).Color;
            int amberGlyphPixels = 0;
            for (int y = Math.Max(0, (int)Math.Ceiling(targetBounds.Top + 3)); y < Math.Min(targetImage.PixelHeight, targetBounds.Bottom - 3); y++)
                for (int x = Math.Max(0, (int)Math.Ceiling(targetBounds.Left + 5)); x < Math.Min(targetImage.PixelWidth, targetBounds.Right - 5); x++)
                {
                    var pixel = targetPixels[y * targetImage.PixelWidth + x];
                    if (Math.Abs((int)((pixel >> 16) & 255) - amber.R) <= 24 &&
                        Math.Abs((int)((pixel >> 8) & 255) - amber.G) <= 24 &&
                        Math.Abs((int)(pixel & 255) - amber.B) <= 24) amberGlyphPixels++;
                }
            if (amberGlyphPixels < 3) throw new InvalidOperationException($"Smoke test: the computed target has no visible native amber text glyphs. " +
                $"Target={targetBounds}; image={targetImage.PixelWidth}×{targetImage.PixelHeight}; " +
                $"visible={TargetBox.IsVisible}/{ProcessingPanel.IsVisible}; selection={TargetBox.SelectionLength}; focused={TargetBox.IsKeyboardFocusWithin}.");
            CommitTarget(); // The same call is used by export; this is a readout, not input.
            Require(-17, true, "export-style commit guard");
            BeginTargetEditing(); Require(-14, false, "focused goal");
            if (TargetBox.SelectionLength != TargetBox.Text.Length) throw new InvalidOperationException("Smoke test: target focus did not select its stored goal.");
            EndTargetEditing(); Require(-17, true, "untouched target blur");
            RefreshList(); Require(-17, true, "background publication during draft");
            if (_settings.MatchingStrength != 1) throw new InvalidOperationException("Smoke test: the computed target committed a Strength draft.");

            track.TrimDb = 2; RefreshFixture(); Require(-15, true, "manual trim estimate");
            track.TargetLufs = -10; RefreshFixture(); Require(-13, true, "selected target override");
            track.TrimDb = 0; track.TargetLufs = null;
            track.Render = Ready(.5, -17.3); RefreshList(); Require(-17.3, true, "published measured result");
            _settings = fixture with { MatchingEnabled = false }; ApplyTimelineSettings(); Require(-20, true, "matching bypass");
            _settings = fixture; track.Render = Ready(1, -14); RefreshFixture();
            FinishStrengthGesture(false); Require(-14, false, "canceled Strength gesture");

            BeginStrengthGesture(MatchingStrengthSlider); FinishStrengthGesture(true);
            Require(-14, false, "stationary Strength gesture");
            BeginStrengthGesture(MatchingStrengthSlider); MatchingStrengthSlider.Value = .5; MatchingStrengthSlider.Value = 1;
            FinishStrengthGesture(true); Require(-14, false, "returned Strength gesture");

            // A typed or preset goal wins even at partial matching strength.
            _settings = fixture with { MatchingStrength = .5 };
            SetStrengthSliderSilently(MatchingStrengthSlider, .5);
            _targetComputedMode = true; ApplyTimelineSettings(); Require(-17, true, "computed partial result");
            BeginTargetEditing(); TargetBox.Text = "-14.0"; EndTargetEditing(); Require(-14, false, "typed equal goal");
            BeginStrengthGesture(MatchingStrengthSlider); MatchingStrengthSlider.Value = .6;
            Require(-16.4, true, "next Strength draft");
            ApplyTargetPreset(-14); Require(-14, false, "preset equal goal");

            _targetComputedMode = true; RefreshTargetReadout(); Require(-17, true, "restored computed mode");
            SyncControls(); Require(-17, true, "unrelated control synchronization");
            var metrics = Timeline.PerformanceCounters;
            for (int tick = 0; tick < 40; tick++) RefreshTargetReadout();
            var after = Timeline.PerformanceCounters;
            if (after.MetricsPasses != metrics.MetricsPasses || after.Tiles.PeakQueryCount != metrics.Tiles.PeakQueryCount)
                throw new InvalidOperationException("Smoke test: repeated target readouts rescanned processing metrics or waveform peaks.");
            BeginTargetEditing(); TargetBox.Text = "invalid"; EndTargetEditing(); Require(-17, true, "invalid goal recovery");
            SelectTrack(_tracks[1]);
            if (!IsComputedTargetReadout || TargetBox.Text != F(Timeline.EstimatedOutputLufs(_tracks[1]), "0.#"))
                throw new InvalidOperationException("Smoke test: the computed target did not follow the selected track.");
            FileList.SelectedItem = null; Timeline.SelectedTrack = null; RefreshTargetReadout(); Require(-14, false, "no selected track fallback");

            if (_revision != revision || _renderExecutionCount != executions || _undo.Count != undoCount || _dirty != dirty)
                throw new InvalidOperationException("Smoke test: readout, focus, canceled drafts or equal goals altered processing/history.");
        }
        finally
        {
            FinishStrengthGesture(false);
            track.Render = savedRender; track.Analysis = savedAnalysis; track.Envelope = savedEnvelope; track.TransitionEnvelope = savedTransitions;
            track.InputGainDb = savedInput; track.TrimDb = savedTrim; track.TargetLufs = savedTarget;
            _settings = savedSettings; _targetEditing = false; SyncControls();
            _targetComputedMode = savedComputed; RefreshList(); SelectTrack(selected); RefreshTargetReadout();
            StatusText.Text = status;
        }
    }
}

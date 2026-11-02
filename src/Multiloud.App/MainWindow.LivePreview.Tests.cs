using System.Windows;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyLiveWaveformSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var track = _tracks[0];
        var envelope = track.Envelope.ToArray();
        var projectEnvelope = _projectEnvelope.ToArray(); var projectSuggestion = _projectSuggestion.ToArray();
        var render = track.Render;
        var analysis = track.Analysis;
        var mode = Timeline.ViewMode;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        bool dirty = _dirty;
        int executions = _renderExecutionCount;
        try
        {
            // Render crops deliberately omit collinear UI points and file-edge
            // knots. Add an unchanged-shape project knot inside the zoomed song.
            double localTime = track.Duration * .5;
            double absoluteTime = _tracks.TakeWhile(item => !ReferenceEquals(item, track)).Sum(item => item.Duration) + localTime;
            if (!_projectEnvelope.Any(point => Math.Abs(point.Time - absoluteTime) < 1e-9))
                _projectEnvelope.Add(new(absoluteTime, AudioProcessor.EnvelopeGainAt(_projectEnvelope, absoluteTime)));
            _projectEnvelope.Sort((a, b) => a.Time.CompareTo(b.Time)); ProjectEnvelopeEdited();
            int index = _projectEnvelope.FindIndex(point => Math.Abs(point.Time - absoluteTime) < 1e-9);
            foreach (var view in new[] { WaveformViewMode.Preview, WaveformViewMode.Compare })
            {
                Timeline.ViewMode = view;
                Timeline.FitTrack(track);
                await WaitForTimelineTilesAsync();
                var before = Timeline.PerformanceCounters.Tiles;
                var origin = Timeline.EnvelopePointPosition(track, index);
                double initialMatch = Timeline.EstimatedMatchGain(track);
                Timeline.HandleWaveformPointerDown(track, origin, 1);
                Timeline.HandleWaveformPointerMove(new Point(origin.X, origin.Y - .0001));
                if (Math.Abs(Timeline.EstimatedMatchGain(track) - initialMatch) > .005)
                    throw new InvalidOperationException("Smoke test: a tiny envelope edit jumped the global waveform gain estimate.");
                Timeline.HandleWaveformPointerMove(new Point(origin.X, origin.Y - 3));
                Timeline.FlushPendingRendering();
                var first = Timeline.PerformanceCounters.Tiles;
                if (first.InteractivePreviewDrawCount <= before.InteractivePreviewDrawCount)
                    throw new InvalidOperationException($"Smoke test: {view} envelope editing did not use a stable live preview.");
                for (int frame = 0; frame < 8; frame++)
                {
                    Timeline.HandleWaveformPointerMove(new Point(origin.X, origin.Y - 4 - frame));
                    Timeline.FlushPendingRendering();
                    await Task.Delay(35);
                }
                Timeline.FlushPendingRendering();
                var held = Timeline.PerformanceCounters.Tiles;
                if (held.TileBuildCount != before.TileBuildCount || held.ComparisonRasterBuildCount != before.ComparisonRasterBuildCount ||
                    held.InteractivePeakSampleBuildCount != first.InteractivePeakSampleBuildCount ||
                    held.InteractivePreviewDrawCount <= first.InteractivePreviewDrawCount || held.RetainedBytes > 128L * 1024 * 1024)
                    throw new InvalidOperationException($"Smoke test: {view} live preview changed quality paths, resampled unchanged peaks, or exceeded its cache budget: {held}.");
                if (_renderExecutionCount != executions || !ReferenceEquals(track.Render, render) || !ReferenceEquals(track.Analysis, analysis))
                    throw new InvalidOperationException("Smoke test: live envelope drawing altered processed audio before release.");
                Timeline.HandleWaveformPointerMove(origin);
                Timeline.CompletePointerInteraction();
                await Task.Delay(550);
                if (!track.Envelope.SequenceEqual(envelope) || _renderExecutionCount != executions || !ReferenceEquals(track.Render, render))
                    throw new InvalidOperationException("Smoke test: a reverted live waveform gesture reprocessed the audio.");
            }
        }
        finally
        {
            Timeline.CompletePointerInteraction();
            RestoreProjectEnvelope(projectEnvelope, projectSuggestion);
            Timeline.ViewMode = mode; Timeline.Refresh(); Timeline.FitAll();
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyLongWheelNavigationSmokeAsync(string? screenshot)
    {
        const int rate = 8000, seconds = 3600;
        // A real one-hour PCM/pyramid, built on a worker, exercises long-source
        // query boundaries without running an unrelated full-album DSP pass.
        var track = await Task.Run(() =>
        {
            var samples = new float[rate * seconds];
            for (int index = 0; index < samples.Length; index++) samples[index] = index % 2 == 0 ? -.2f : .2f;
            var pyramid = WaveformPyramid.Build(samples, 1);
            var peaks = BuildDisplayPeaks(pyramid);
            return new AudioTrack
            {
                FilePath = "One-hour wheel navigation.wav", Original = samples, OriginalWaveform = pyramid,
                OriginalPeaks = peaks, PreviewPeaks = peaks, SampleRate = rate, Channels = 1,
                Analysis = new AnalysisResult { IntegratedLufs = -20, TruePeakDb = -12 },
                Render = new RenderResult { Samples = samples, Waveform = pyramid, OutputLufs = -20,
                    TruePeakDb = -12, RenderedCeilingDb = -1, RenderedSampleRate = rate, RenderedChannels = 1 }
            };
        });
        var points = Enumerable.Range(0, seconds * 2 + 1).Select(index => new EnvelopePoint(index * .5, 0)).ToList();
        track.Envelope = points;
        var timeline = new TimelineView
        {
            Tracks = new List<AudioTrack> { track }, ProjectEnvelope = points,
            Settings = new ProcessingSettings { MatchingEnabled = false, LimiterEnabled = false },
            ShowEnvelopeOnWaveform = true, ShowGainLines = true
        };
        var size = new Size(1100, 540);
        void Layout()
        {
            timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
        }
        async Task Settle()
        {
            Layout(); var deadline = DateTime.UtcNow.AddSeconds(15);
            while (timeline.HasPendingTiles && DateTime.UtcNow < deadline) { await Task.Delay(5); timeline.FlushPendingRendering(); }
            if (timeline.HasPendingTiles) throw new InvalidOperationException("Smoke test: one-hour waveform images did not settle.");
            timeline.FlushPendingRendering();
        }
        var report = new JsonArray();
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.ViewMode = mode; timeline.FitAll(); await Settle();
            var pointer = new Point(timeline.PlotArea.Left + timeline.PlotArea.Width * .5, 220);
            timeline.SetHoveredTrack(pointer, force: true); timeline.SetCrosshairPointer(pointer); Layout();
            timeline.ResetPerformanceCounters(); var before = timeline.PerformanceCounters;
            var timings = new List<double>();
            double initialDuration = timeline.VisibleDurationSeconds;
            void Gesture(bool pan)
            {
                double initialStart = timeline.ViewStartSeconds, duration = timeline.VisibleDurationSeconds;
                timeline.HandleNavigationWheel(pointer, pan ? -120 : 120, pan);
                if (!timeline.IsNavigationAnimating || timeline.VisibleDurationSeconds != duration || timeline.ViewStartSeconds != initialStart)
                    throw new InvalidOperationException("Smoke test: wheel navigation jumped before its first presentation frame.");
                for (int frame = 1; frame <= 15; frame++)
                {
                    var started = Stopwatch.GetTimestamp();
                    timeline.AdvanceNavigationForTesting(frame / 60.0);
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (frame == 6)
                    {
                        double travel = pan ? (timeline.ViewStartSeconds - initialStart) / (duration * .09)
                            : (duration - timeline.VisibleDurationSeconds) / (duration * .16);
                        if (!timeline.IsNavigationAnimating || travel < .5 || travel > .98)
                            throw new InvalidOperationException("Smoke test: wheel navigation did not present a responsive intermediate camera at 100 ms.");
                    }
                }
                if (timeline.IsNavigationAnimating)
                    throw new InvalidOperationException("Smoke test: a one-hour wheel gesture retained its former half-second easing tail.");
            }
            Gesture(false); Gesture(true);
            await Settle();
            var after = timeline.PerformanceCounters;
            if (Math.Abs(timeline.VisibleDurationSeconds - initialDuration * .84) > 1e-7 ||
                after.MetricsPasses != before.MetricsPasses || after.OverviewRedraws != before.OverviewRedraws ||
                after.Tiles.PeakQueryCount != before.Tiles.PeakQueryCount || after.Tiles.TileBuildCount != before.Tiles.TileBuildCount ||
                after.Tiles.RasterBuildCount != before.Tiles.RasterBuildCount || after.Tiles.RasterHitCount <= before.Tiles.RasterHitCount ||
                after.Tiles.RetainedBytes > 128L * 1024 * 1024)
                throw new InvalidOperationException($"Smoke test: one-hour {mode} warm wheel motion left the cached-image camera path: before={before}, after={after}.");
            // Two gestures have 13 in-motion frames each. Editor work must be
            // bounded by visible pixels, independently of the 7,201 UI knots.
            long nodeLimit = 26 * ((long)Math.Ceiling(timeline.PlotArea.Width / 8) + 3);
            long vertexLimit = 26 * (3 * ((long)Math.Ceiling(timeline.PlotArea.Width / 2) + 1) + 2);
            if (timeline.NavigationEnvelopeNodeDraws > nodeLimit || timeline.NavigationEnvelopeVertices > vertexLimit ||
                timeline.NavigationEnvelopeNodeDraws == 0 || timeline.NavigationEnvelopeVertices == 0 || track.Envelope.Count != points.Count)
                throw new InvalidOperationException("Smoke test: dense one-hour envelope navigation exceeded its screen-space work bound or altered authored knots.");
            timings.Sort();
            report.Add(new JsonObject
            {
                ["View"] = mode.ToString(), ["DurationSeconds"] = seconds, ["EnvelopePoints"] = points.Count,
                ["GestureSettleMilliseconds"] = 220, ["MedianFrameCpuMs"] = timings[timings.Count / 2],
                ["P95FrameCpuMs"] = timings[(int)(timings.Count * .95)],
                ["NavigationEnvelopeNodes"] = timeline.NavigationEnvelopeNodeDraws,
                ["NavigationEnvelopeVertices"] = timeline.NavigationEnvelopeVertices,
                ["NewWaveformQueries"] = after.Tiles.PeakQueryCount - before.Tiles.PeakQueryCount,
                ["NewWaveformTiles"] = after.Tiles.TileBuildCount - before.Tiles.TileBuildCount
            });
        }
        if (screenshot != null)
        {
            var path = Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-render-performance.json");
            var document = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject ?? new JsonObject() : new JsonObject();
            document["OneHourWheelNavigation"] = report;
            await File.WriteAllTextAsync(path, document.ToJsonString(new() { WriteIndented = true }));
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBackgroundLaneSmokeAsync(string? screenshot)
    {
        var music = Enumerable.Range(1, 2).Select(index => new AudioTrack
        { FilePath = $"Music {index}.wav", WorkState = TrackWorkState.Missing, SavedDuration = 12 }).ToArray();
        var samples = Enumerable.Range(0, 10240).Select(index => (float)(Math.Sin(index * .13) * .25)).ToArray();
        var source = new AudioTrack { FilePath = "Background atmosphere.wav", Original = samples, SampleRate = 1024, Channels = 1,
            OriginalWaveform = WaveformPyramid.Build(samples, 1), OriginalPeaks = Enumerable.Range(0, 1024).Select(_ => new PeakBin(-.25f, .25f)).ToList() };
        var clip = new BackgroundClip { Source = source, StartSeconds = 3, FadeInSeconds = 1, FadeOutSeconds = 2 };
        var missing = new BackgroundClip { Source = new AudioTrack { FilePath = "Missing atmosphere.wav", WorkState = TrackWorkState.Missing, SavedDuration = 5 }, StartSeconds = 16 };
        var timeline = new TimelineView { Tracks = music, EmptyDurationHint = 100 };
        using var lane = new BackgroundLaneView { Timeline = timeline, Layer = new BackgroundLayer { Clips = [clip, missing] } };
        var timelineSize = new Size(960, 420); var laneSize = new Size(960, 120);
        void Layout()
        {
            timeline.Measure(timelineSize); timeline.Arrange(new Rect(timelineSize)); timeline.UpdateLayout(); timeline.FlushPendingRendering();
            lane.Measure(laneSize); lane.Arrange(new Rect(laneSize)); lane.UpdateLayout(); lane.FlushPendingRendering();
        }
        async Task Warm()
        {
            Layout(); var deadline = DateTime.UtcNow.AddSeconds(8);
            while (lane.HasPendingTiles && DateTime.UtcNow < deadline) { await Task.Delay(5); Layout(); }
            if (lane.HasPendingTiles) throw new InvalidOperationException("Smoke test: background lane waveform tiles did not settle.");
            Layout();
        }
        Layout(); timeline.FitAll(); await Warm();
        if (timeline.VisibleDurationSeconds != 24 || lane.PlotArea.Left != timeline.PlotArea.Left || lane.PlotArea.Right != timeline.PlotArea.Right ||
            Math.Abs(lane.ClipBounds(clip).Left - (lane.PlotArea.Left + lane.PlotArea.Width * 3 / 24)) > .000001)
            throw new InvalidOperationException("Smoke test: background and music time geometry disagree or a long bed extended the music album.");

        var before = lane.TileDiagnostics; var counts = lane.DrawCounts;
        var body = new Point(lane.PlotArea.Left + lane.PlotArea.Width * 8 / 24, 70);
        lane.SetHover(body); timeline.Playhead = 8; lane.FlushPendingRendering();
        if (lane.DrawCounts.Waveform != counts.Waveform || lane.DrawCounts.Editor <= counts.Editor || lane.DrawCounts.Dynamic <= counts.Dynamic ||
            lane.TileDiagnostics.PeakQueryCount != before.PeakQueryCount || lane.TileDiagnostics.RasterBuildCount != before.RasterBuildCount)
            throw new InvalidOperationException("Smoke test: background hover or playhead movement rebuilt waveform tiles.");

        double moved = -1, fadeIn = -1, fadeOut = -1, seek = -1; int moves = 0, fades = 0, removes = 0, relinks = 0;
        lane.ClipMoved += (item, value) => { if (!ReferenceEquals(item, clip)) throw new InvalidOperationException("Smoke test: background drag targeted another clip."); moved = value; moves++; };
        lane.FadesChanged += (_, input, output) => { fadeIn = input; fadeOut = output; fades++; };
        lane.SeekRequested += value => seek = value;
        lane.RemoveRequested += _ => removes++;
        lane.RelinkRequested += _ => relinks++;
        lane.HandlePointerDown(body); lane.CompletePointerInteraction();
        if (Math.Abs(seek - 8) > .000001 || moves != 0) throw new InvalidOperationException("Smoke test: a background click did not seek without moving its clip.");
        lane.HandlePointerDown(body); lane.UpdatePointerInteraction(new Point(body.X + lane.PlotArea.Width * 2 / 24, body.Y));
        lane.CompletePointerInteraction(); lane.CompletePointerInteraction();
        if (moves != 1 || Math.Abs(moved - 5) > .000001 || clip.StartSeconds != 3 || !ReferenceEquals(source.Original, samples))
            throw new InvalidOperationException("Smoke test: background clip dragging changed source audio or committed more than once.");
        lane.HandlePointerDown(body); lane.UpdatePointerInteraction(new Point(body.X + 100, body.Y)); lane.CompletePointerInteraction(false);
        if (moves != 1) throw new InvalidOperationException("Smoke test: canceling a background drag committed its draft.");

        await Warm(); counts = lane.DrawCounts;
        var handle = lane.FadeHandlePosition(clip, true);
        lane.HandlePointerDown(handle); lane.UpdatePointerInteraction(new Point(handle.X + lane.PlotArea.Width / 24, handle.Y)); lane.FlushPendingRendering();
        if (lane.DrawCounts.Waveform != counts.Waveform) throw new InvalidOperationException("Smoke test: adjusting a background fade rebuilt its source waveform.");
        lane.CompletePointerInteraction();
        if (fades != 1 || Math.Abs(fadeIn - 2) > .000001 || fadeOut != 2 || clip.FadeInSeconds != 1)
            throw new InvalidOperationException("Smoke test: a linear background fade edit lost its time or mutated the immutable clip.");
        var remove = lane.RemoveBounds(clip); lane.HandlePointerDown(new Point(remove.Left + remove.Width / 2, 9));
        var relink = lane.RelinkBounds(missing); lane.HandlePointerDown(new Point(relink.Left + relink.Width / 2, relink.Top + relink.Height / 2));
        if (removes != 1 || relinks != 1) throw new InvalidOperationException("Smoke test: background remove or missing-source relink hit routing failed.");

        string[]? dropped = null; double dropPosition = -1;
        lane.CanDropFiles = paths => paths.All(path => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
        lane.FilesDropped += (paths, position) => { dropped = paths; dropPosition = position; };
        var data = new DataObject(DataFormats.FileDrop, new[] { "atmosphere.wav" });
        var drop = new Point(lane.PlotArea.Left + lane.PlotArea.Width * 7 / 24, 60);
        if (!lane.PreviewFileDrop(data, drop) || Math.Abs(lane.DropTimeSeconds!.Value - 7) > .000001 || !lane.HandleFileDrop(data, drop) ||
            dropped is not ["atmosphere.wav"] || Math.Abs(dropPosition - 7) > .000001 || lane.DropTimeSeconds != null ||
            lane.PreviewFileDrop(new DataObject(DataFormats.FileDrop, new[] { "unsupported.txt" }), drop) || lane.DropTimeSeconds != null)
            throw new InvalidOperationException("Smoke test: a background file drop lost its cursor time or accepted an unsupported source.");

        timeline.FitTrack(music[1]); await Warm();
        if (Math.Abs(lane.ClipBounds(clip).Width - lane.PlotArea.Width / 12) > .000001)
            throw new InvalidOperationException("Smoke test: the background lane did not follow the music viewport.");
        lane.HandleNavigationWheel(new Point(lane.PlotArea.Left + lane.PlotArea.Width * .5, 60), 120, false);
        var animationDeadline = DateTime.UtcNow.AddSeconds(3);
        while (timeline.IsNavigationAnimating && DateTime.UtcNow < animationDeadline) { await Task.Delay(10); Layout(); }
        if (timeline.IsNavigationAnimating || timeline.VisibleDurationSeconds >= 12)
            throw new InvalidOperationException("Smoke test: wheel zoom from the background lane did not animate the shared camera.");
        timeline.FitAll(); await Warm();
        if (screenshot != null) CaptureVisual(lane, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-background-lane.png"));
        lane.Layer = new BackgroundLayer { Clips = [missing] }; Layout(); var missingQueries = lane.TileDiagnostics.PeakQueryCount;
        Layout();
        if (lane.HasPendingTiles || lane.TileDiagnostics.PeakQueryCount != missingQueries || lane.TileDiagnostics.RetainedBytes > 32L * 1024 * 1024)
            throw new InvalidOperationException("Smoke test: a missing background source scheduled waveform work or exceeded the lane cache budget.");
        timeline.Tracks = []; timeline.EmptyDurationHint = 21; timeline.FitAll(); Layout();
        if (timeline.VisibleDurationSeconds != 21) throw new InvalidOperationException("Smoke test: a background-only release did not get a usable empty timeline horizon.");

        // Draft matching targets the sum while applying gain only to music.
        var foreground = new AudioTrack { Original = samples, SampleRate = 1024, Channels = 1,
            Analysis = new AnalysisResult { IntegratedLufs = -20 }, Render = new RenderResult
            { Samples = samples, BackgroundSamples = samples, BackgroundLufs = -24, PreMatchLufs = -20, RenderedMatchingStrength = 1, OutputLufs = -14 } };
        timeline.Tracks = [foreground];
        timeline.Settings = new ProcessingSettings { MatchingStrength = .5, LevelingEnabled = false, LimiterEnabled = false };
        static double Mix(double a, double b) => 10 * Math.Log10(Math.Pow(10, a / 10) + Math.Pow(10, b / 10));
        var baseline = Mix(-20, -24); var desired = baseline + (-14 - baseline) * .5;
        var expectedGain = 10 * Math.Log10(Math.Pow(10, desired / 10) - Math.Pow(10, -24 / 10.0)) + 20;
        if (Math.Abs(timeline.EstimatedMatchGain(foreground) - expectedGain) > .000001 || Math.Abs(timeline.EstimatedOutputLufs(foreground) - desired) > .000001)
            throw new InvalidOperationException("Smoke test: background-aware draft matching treated the fixed bed as adjustable music.");
        foreground.TrimDb = 2; timeline.Refresh();
        if (Math.Abs(timeline.EstimatedOutputLufs(foreground) - Mix(-18 + expectedGain, -24)) > .000001)
            throw new InvalidOperationException("Smoke test: background-aware draft trim changed the bed loudness.");
        timeline.Settings = timeline.Settings with { MatchingEnabled = false };
        if (Math.Abs(timeline.EstimatedOutputLufs(foreground) - Mix(-18, -24)) > .000001)
            throw new InvalidOperationException("Smoke test: background-aware matching bypass lost the unchanged bed.");
        timeline.Settings = timeline.Settings with { MatchingEnabled = true, MatchingStrength = 1, TargetLufs = -30 };
        if (!double.IsFinite(timeline.EstimatedOutputLufs(foreground)) || timeline.EstimatedMatchGain(foreground) != -36)
            throw new InvalidOperationException("Smoke test: an unreachable target below the fixed-bed floor produced an invalid gain estimate.");

        // Native source and common-format result pyramids have different frame
        // clocks. A one-frame result transient must remain at its album time.
        var native = new float[10240];
        var resultSamples = new float[48000 * 10 * 2];
        resultSamples[48000 * 2 * 25 / 4] = 1;
        resultSamples[48000 * 2 * 25 / 4 + 1] = -1;
        var resultPyramid = await Task.Run(() => WaveformPyramid.Build(resultSamples, 2));
        var resampled = new AudioTrack
        {
            FilePath = "Common-format result.wav", Original = native, SampleRate = 1024, Channels = 1,
            OriginalWaveform = WaveformPyramid.Build(native, 1), OriginalPeaks = [new(0, 0)], PreviewPeaks = [new(-1, 1)],
            Analysis = new AnalysisResult { IntegratedLufs = -20 },
            Render = new RenderResult { Samples = resultSamples, Waveform = resultPyramid, RenderedSampleRate = 48000, RenderedChannels = 2 }
        };
        timeline.Tracks = [resampled]; timeline.Settings = new ProcessingSettings { LevelingEnabled = false, MatchingEnabled = false, LimiterEnabled = false };
        timeline.ViewMode = WaveformViewMode.Preview; timeline.ShowEnvelopeOnWaveform = false; timeline.ShowGainLines = false; timeline.FitAll(); Layout();
        var resultDeadline = DateTime.UtcNow.AddSeconds(8);
        while (timeline.HasPendingTiles && DateTime.UtcNow < resultDeadline) { await Task.Delay(5); Layout(); }
        timeline.FlushPendingRendering();
        if (timeline.HasPendingTiles) throw new InvalidOperationException("Smoke test: common-format preview tiles did not settle.");
        var image = new RenderTargetBitmap((int)timelineSize.Width, (int)timelineSize.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(timeline); var pixels = ComparisonPixels(image);
        int expectedX = (int)(timeline.PlotArea.Left + timeline.PlotArea.Width * .625), transientPixels = 0;
        int firstY = (int)(timeline.SignalCenterY - timeline.SignalAmplitude * .8), lastY = (int)(timeline.SignalCenterY - timeline.SignalAmplitude * .2);
        for (int y = firstY; y < lastY; y++)
            for (int x = expectedX - 6; x <= expectedX + 6; x++)
            {
                var pixel = pixels[y * image.PixelWidth + x];
                var red = (pixel >> 16) & 255; var green = (pixel >> 8) & 255; var blue = pixel & 255;
                if (green > red + 20 && green > blue + 10) transientPixels++;
            }
        if (transientPixels < 8) throw new InvalidOperationException("Smoke test: a resampled preview queried its result pyramid with the native source clock.");
    }
}

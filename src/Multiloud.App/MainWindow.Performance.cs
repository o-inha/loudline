using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using Multiloud.Core;
using Multiloud.App.Controls;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task WaitForTimelineTilesAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        long previousQueries = -1;
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.InvokeAsync(() => Timeline.FlushPendingRendering(), DispatcherPriority.Background);
            var counters = Timeline.PerformanceCounters;
            if (!Timeline.HasPendingTiles && counters.Tiles.PeakQueryCount == previousQueries)
            {
                await Dispatcher.InvokeAsync(() => Timeline.FlushPendingRendering(), DispatcherPriority.ContextIdle);
                if (!Timeline.HasPendingTiles) return;
            }
            previousQueries = counters.Tiles.PeakQueryCount;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Smoke test: background waveform tiles did not settle.");
    }

    private async Task VerifyRetainedRenderingSmokeAsync(string? screenshot)
    {
        var track = _tracks[0];
        Timeline.ViewMode = WaveformViewMode.Compare;
        Timeline.FitTrack(track);
        await WaitForTimelineTilesAsync();
        var sources = _tracks.Select(item => item.Render).ToArray();
        var rows = _trackRows.ToArray();
        RefreshList();
        await WaitForTimelineTilesAsync();
        if (!_trackRows.SequenceEqual(rows)) throw new InvalidOperationException("Smoke test: a status refresh replaced persistent file rows.");
        Timeline.ResetPerformanceCounters();
        var before = Timeline.PerformanceCounters;
        double playhead = Timeline.Playhead;
        var playheadSamples = new List<double>();
        var cameraSamples = new List<double>();
        for (int frame = 1; frame <= 40; frame++)
        {
            var start = Stopwatch.GetTimestamp();
            Timeline.Playhead = playhead + frame * .02;
            Timeline.FlushPendingRendering();
            playheadSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        var dynamic = Timeline.PerformanceCounters;
        if (dynamic.WaveformRedraws != 0 || dynamic.OverviewRedraws != 0 || dynamic.EditorRedraws != 0 || dynamic.MetricsPasses != 0 || dynamic.DynamicRedraws != 40 ||
            dynamic.Tiles.TileBuildCount != before.Tiles.TileBuildCount || dynamic.Tiles.PeakQueryCount != before.Tiles.PeakQueryCount)
            throw new InvalidOperationException($"Smoke test: playhead updates touched static waveform work: {dynamic}.");

        // At this 4K size a single .84 zoom step stays inside the same sqrt(2)
        // detail level. All track tiles were warmed above, including pan margins.
        double startDuration = Timeline.VisibleDurationSeconds;
        var pointer = new Point(Timeline.ActualWidth * .5, 240);
        Timeline.HandleNavigationWheel(pointer, 120, false);
        if (!Timeline.IsNavigationAnimating || Timeline.VisibleDurationSeconds != startDuration)
            throw new InvalidOperationException("Smoke test: render-paced zoom jumped before its first frame.");
        await Task.Delay(20); Timeline.FlushPendingRendering();
        if (Timeline.VisibleDurationSeconds >= startDuration || !Timeline.IsNavigationAnimating)
            throw new InvalidOperationException("Smoke test: zoom did not show an intermediate camera position.");
        await FinishCameraAsync(cameraSamples);
        Timeline.HandleNavigationWheel(pointer, -120, true);
        await FinishCameraAsync(cameraSamples);
        await WaitForTimelineTilesAsync();
        var navigation = Timeline.PerformanceCounters;
        if (navigation.WaveformRedraws == 0 || navigation.MetricsPasses != 0 || navigation.OverviewRedraws != 0)
            throw new InvalidOperationException("Smoke test: camera motion rebuilt unchanged data or overview content.");
        if (navigation.Tiles.TileBuildCount != before.Tiles.TileBuildCount || navigation.Tiles.PeakQueryCount != before.Tiles.PeakQueryCount)
            throw new InvalidOperationException($"Smoke test: warm pan/zoom rebuilt waveform tiles or queried source data: before={before.Tiles}, after={navigation.Tiles}.");
        if (navigation.Tiles.ComparisonRasterHitCount <= before.Tiles.ComparisonRasterHitCount ||
            navigation.Tiles.ComparisonRasterBuildCount != before.Tiles.ComparisonRasterBuildCount ||
            navigation.Tiles.ComparisonVectorFallbackCount != before.Tiles.ComparisonVectorFallbackCount)
            throw new InvalidOperationException($"Smoke test: warm Comparison motion did not stay on cached bitmap tiles: {navigation.Tiles}.");
        if (_tracks.Where((item, index) => !ReferenceEquals(item.Render, sources[index])).Any())
            throw new InvalidOperationException("Smoke test: display navigation altered processed audio.");
        if (navigation.Tiles.RetainedBytes > 128L * 1024 * 1024 || navigation.Tiles.PendingTileCount > 256)
            throw new InvalidOperationException("Smoke test: waveform display exceeded its cache or queue budget.");

        // Use the exact same camera and sources for both drawing paths. These are CPU
        // command-generation samples; remote CI cannot measure the user's GPU presentation.
        var vectorComparisonSamples = new List<double>();
        var rasterComparisonSamples = new List<double>();
        for (int frame = 0; frame < 24; frame++)
        {
            Timeline.UseComparisonRasterization = false;
            var start = Stopwatch.GetTimestamp(); Timeline.FlushPendingRendering();
            vectorComparisonSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Timeline.UseComparisonRasterization = true;
            start = Stopwatch.GetTimestamp(); Timeline.FlushPendingRendering();
            rasterComparisonSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        await WaitForTimelineTilesAsync();
        var virtualization = await VerifyFileListVirtualizationSmokeAsync();
        var allViews = await VerifyAllViewRenderingSmokeAsync(track);
        static object Timings(List<double> samples)
        {
            samples.Sort();
            return new { Count = samples.Count, MedianMs = samples[samples.Count / 2], P95Ms = samples[Math.Min(samples.Count - 1, (int)(samples.Count * .95))], MaxMs = samples[^1] };
        }
        if (screenshot != null)
        {
            string path = Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-render-performance.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Description = "Native 3840 x 2160 Comparison; CPU command generation, not GPU present/input-to-photon timing",
                PlayheadFrames = dynamic.DynamicRedraws,
                PlayheadWaveformRedraws = dynamic.WaveformRedraws,
                PlayheadMetricsPasses = dynamic.MetricsPasses,
                WarmNavigationNewTiles = navigation.Tiles.TileBuildCount - before.Tiles.TileBuildCount,
                WarmNavigationPeakQueries = navigation.Tiles.PeakQueryCount - before.Tiles.PeakQueryCount,
                NavigationFrames = navigation.WaveformRedraws,
                TrackCount = _tracks.Count,
                PlayheadCpu = Timings(playheadSamples),
                NavigationCpu = Timings(cameraSamples),
                VectorComparisonCpu = Timings(vectorComparisonSamples),
                RasterComparisonCpu = Timings(rasterComparisonSamples),
                AllViews = allViews,
                WarmNavigationNewComparisonImages = navigation.Tiles.ComparisonRasterBuildCount - before.Tiles.ComparisonRasterBuildCount,
                WarmNavigationComparisonImageHits = navigation.Tiles.ComparisonRasterHitCount - before.Tiles.ComparisonRasterHitCount,
                WarmNavigationComparisonVectorFallbacks = navigation.Tiles.ComparisonVectorFallbackCount - before.Tiles.ComparisonVectorFallbackCount,
                VirtualizedRows = virtualization,
                CacheBytes = navigation.Tiles.RetainedBytes,
                CachedTiles = navigation.Tiles.CachedTileCount
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Timeline.Playhead = playhead; Timeline.FitTrack(track);
        await WaitForTimelineTilesAsync();
    }

    private async Task<object[]> VerifyAllViewRenderingSmokeAsync(AudioTrack track)
    {
        var results = new List<object>();
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            Timeline.ViewMode = mode;
            Timeline.FitTrack(track);
            await WaitForTimelineTilesAsync();
            var before = Timeline.PerformanceCounters;
            var samples = new List<double>();
            var pointer = new Point(Timeline.ActualWidth * .5, 240);
            Timeline.HandleNavigationWheel(pointer, 120, false);
            await FinishCameraAsync(samples);
            Timeline.HandleNavigationWheel(pointer, -120, true);
            await FinishCameraAsync(samples);
            await WaitForTimelineTilesAsync();
            var after = Timeline.PerformanceCounters;
            if (after.Tiles.RasterHitCount <= before.Tiles.RasterHitCount || after.Tiles.RasterBuildCount != before.Tiles.RasterBuildCount ||
                after.Tiles.TileBuildCount != before.Tiles.TileBuildCount || after.Tiles.PeakQueryCount != before.Tiles.PeakQueryCount ||
                after.Tiles.RetainedBytes > 128L * 1024 * 1024)
                throw new InvalidOperationException($"Smoke test: warm {mode} navigation did not stay on bounded cached images: {after.Tiles}.");
            samples.Sort();
            results.Add(new
            {
                View = mode.ToString(),
                Frames = samples.Count,
                MedianCpuMs = samples[samples.Count / 2],
                P95CpuMs = samples[Math.Min(samples.Count - 1, (int)(samples.Count * .95))],
                CachedImageHits = after.Tiles.RasterHitCount - before.Tiles.RasterHitCount,
                NewImages = after.Tiles.RasterBuildCount - before.Tiles.RasterBuildCount,
                SourceQueries = after.Tiles.PeakQueryCount - before.Tiles.PeakQueryCount
            });
        }
        return results.ToArray();
    }

    private async Task<object> VerifyFileListVirtualizationSmokeAsync()
    {
        int originalCount = _trackRows.Count;
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            for (int index = 0; index < 500; index++)
            {
                var row = new TrackListEntry(new AudioTrack { FilePath = $"virtualized-{index:000}.wav", Status = "Ready" });
                row.Update(originalCount + index); _trackRows.Add(row);
            }
            await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
            int CountRealized() => Enumerable.Range(0, _trackRows.Count).Count(index => FileList.ItemContainerGenerator.ContainerFromIndex(index) != null);
            int atTop = CountRealized();
            var last = _trackRows[^1];
            FileList.ScrollIntoView(last);
            await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
            int atBottom = CountRealized();
            if (atTop is < 1 or > 100 || atBottom is < 1 or > 100 ||
                FileList.ItemContainerGenerator.ContainerFromItem(last) is not ListBoxItem item || !ReferenceEquals(item.Tag, last.Track))
                throw new InvalidOperationException($"Smoke test: file-list virtualization/recycled track handles failed: {atTop}/{atBottom} realized for {_trackRows.Count} rows.");
            return new { Total = _trackRows.Count, AtTop = atTop, AtBottom = atBottom };
        }
        finally
        {
            while (_trackRows.Count > originalCount) _trackRows.RemoveAt(_trackRows.Count - 1);
            if (originalCount > 0) FileList.ScrollIntoView(_trackRows[0]);
            await Dispatcher.InvokeAsync(() => FileList.UpdateLayout(), DispatcherPriority.Background);
            _syncing = syncing;
        }
    }

    private async Task FinishCameraAsync(List<double> samples)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Timeline.IsNavigationAnimating && DateTime.UtcNow < deadline)
        {
            await Task.Delay(16);
            var start = Stopwatch.GetTimestamp(); Timeline.FlushPendingRendering();
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        if (Timeline.IsNavigationAnimating) throw new InvalidOperationException("Smoke test: render-paced camera failed to settle.");
    }
}

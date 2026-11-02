using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Multiloud.Core;

namespace Multiloud.App.Controls;

/// <summary>An immutable gain snapshot. Its source identity must change when these values change.</summary>
internal sealed class WaveformTileGain
{
    private readonly EnvelopePoint[] _envelope;
    private readonly double _strength, _constantGainDb;
    private readonly float? _ceiling;

    public WaveformTileGain(IReadOnlyList<EnvelopePoint> envelope, double strength, double constantGainDb, double? ceilingDb)
    {
        _envelope = envelope.ToArray();
        _strength = double.IsFinite(strength) ? strength : 0;
        _constantGainDb = double.IsFinite(constantGainDb) ? constantGainDb : 0;
        _ceiling = ceilingDb is { } ceiling && double.IsFinite(ceiling) ? (float)Math.Pow(10, ceiling / 20) : null;
    }

    internal WaveformExtrema Apply(WaveformExtrema peak, double time)
    {
        var db = _constantGainDb;
        if (_envelope.Length > 0 && _strength != 0)
        {
            var lo = 0;
            var hi = _envelope.Length - 1;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_envelope[mid].Time < time) lo = mid + 1;
                else hi = mid;
            }
            var b = _envelope[lo];
            var a = _envelope[Math.Max(0, lo - 1)];
            var mix = Math.Clamp((time - a.Time) / Math.Max(.000001, b.Time - a.Time), 0, 1);
            db += (a.GainDb + (b.GainDb - a.GainDb) * mix) * _strength;
        }
        var gain = (float)Math.Pow(10, Math.Clamp(db, -160, 160) / 20);
        var min = peak.Min * gain;
        var max = peak.Max * gain;
        if (_ceiling is { } ceiling) { min = Math.Max(-ceiling, min); max = Math.Min(ceiling, max); }
        return new WaveformExtrema(min, max);
    }
}

/// <summary>
/// Published waveform data, never a mutable AudioTrack. Peaks must remain unchanged after publication.
/// Identity names the exact data/gain state and deliberately excludes the camera and drawing style.
/// </summary>
internal sealed class WaveformTileSource
{
    // The LRU may keep geometry from a previous preview, but must not keep its multi-gigabyte
    // PCM alive just to identify that geometry. Tokens outlive their weakly held source keys.
    private static readonly ConditionalWeakTable<object, IdentityToken> IdentityTokens = new();
    private static long _nextIdentity;
    private sealed class IdentityToken(long value) { public readonly long Value = value; }
    private static long Token(object? value) => value is null ? 0 : IdentityTokens.GetValue(value,
        _ => new IdentityToken(Interlocked.Increment(ref _nextIdentity))).Value;

    public WaveformTileSource(object identity, WaveformPyramid? pyramid, IReadOnlyList<PeakBin> peaks,
        int sampleRate, double duration, WaveformTileGain? gain = null)
    {
        Identity = identity;
        Pyramid = pyramid;
        Peaks = peaks;
        SampleRate = Math.Max(1, sampleRate);
        Duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        Gain = gain;
        Key = new SourceKey(Token(identity), Token(pyramid), Token(peaks), SampleRate, Duration);
    }

    public object Identity { get; }
    public WaveformPyramid? Pyramid { get; }
    public IReadOnlyList<PeakBin> Peaks { get; }
    public int SampleRate { get; }
    public double Duration { get; }
    internal WaveformTileGain? Gain { get; }
    internal object Key { get; }
    private sealed record SourceKey(long Identity, long Pyramid, long Peaks, int SampleRate, double Duration);

    internal WaveformExtrema Query(double start, double end, bool coarse)
    {
        start = Math.Clamp(start, 0, Duration);
        end = Math.Clamp(end, start, Duration);
        float min = 0, max = 0;
        if (!coarse && Pyramid is { } pyramid)
        {
            var peak = pyramid.QuerySeconds(start, end, SampleRate);
            min = Math.Min(0, peak.Min); max = Math.Max(0, peak.Max);
        }
        else if (Peaks.Count > 0 && Duration > 0 && end > start)
        {
            var first = Math.Clamp((int)(start / Duration * Peaks.Count), 0, Peaks.Count - 1);
            var last = Math.Clamp((int)Math.Ceiling(end / Duration * Peaks.Count), first + 1, Peaks.Count);
            if (coarse)
            {
                // A missing tile must never turn a camera event into a PCM scan. This temporary
                // drawing probes at most four precomputed bins per column; the worker replaces it.
                var count = Math.Min(4, last - first);
                for (var i = 0; i < count; i++)
                {
                    var at = first + (int)((long)i * (last - first - 1) / Math.Max(1, count - 1));
                    min = Math.Min(min, Peaks[at].Min); max = Math.Max(max, Peaks[at].Max);
                }
            }
            else
            {
                for (var i = first; i < last; i++) { min = Math.Min(min, Peaks[i].Min); max = Math.Max(max, Peaks[i].Max); }
            }
        }
        return Gain?.Apply(new WaveformExtrema(min, max), (start + end) * .5) ?? new WaveformExtrema(min, max);
    }

    internal WaveformExtrema QueryPublishedPeaks(double start, double end)
    {
        start = Math.Clamp(start, 0, Duration); end = Math.Clamp(end, start, Duration);
        float min = 0, max = 0;
        if (Peaks.Count > 0 && Duration > 0 && end > start)
        {
            var first = Math.Clamp((int)(start / Duration * Peaks.Count), 0, Peaks.Count - 1);
            var last = Math.Clamp((int)Math.Ceiling(end / Duration * Peaks.Count), first + 1, Peaks.Count);
            for (var i = first; i < last; i++) { min = Math.Min(min, Peaks[i].Min); max = Math.Max(max, Peaks[i].Max); }
        }
        return new WaveformExtrema(min, max);
    }
}

internal sealed record WaveformTileTrack(Guid TrackId, WaveformTileSource Original, WaveformTileSource Processed);
internal sealed record WaveformTileStyle(Brush Fill, Brush Boost, Brush Cut, Brush OriginalOutline);
internal readonly record struct WaveformTileDiagnostics(long TileBuildCount, long CacheHitCount, long CacheMissCount,
    long PeakQueryCount, long FallbackTileCount, long CanceledJobCount, int PendingTileCount, int CachedTileCount, long RetainedBytes,
    long ComparisonRasterBuildCount, long ComparisonRasterHitCount, long ComparisonVectorFallbackCount, double ComparisonRasterCpuMilliseconds,
    long ComparisonRasterFailureCount, long InteractivePreviewDrawCount, long InteractiveTileBuildCount,
    long InteractivePeakSampleBuildCount, int LastFrameInteractiveTrackCount, long RasterBuildCount, long RasterHitCount,
    long RasterFailureCount, double RasterCpuMilliseconds, long ResolutionFallbackHitCount,
    long ResolutionFallbackImageDrawCount, int MaxResolutionFallbackImagesPerTile);

/// <summary>
/// Frozen, track-time-aligned waveform geometry. Camera motion changes one matrix per track,
/// never the retained vertices. Only visible/overscan misses reach two bounded background workers.
/// </summary>
internal sealed class WaveformTileRenderer : IDisposable
{
    private const int TileColumns = 512;
    private const int CoarseColumns = 32;
    private const int InteractiveColumns = 192;
    private const int WorkerLimit = 2;
    private const int PendingLimit = 256;
    private const int ResolutionFallbackImageLimit = 3;
    private readonly Dispatcher _dispatcher;
    private readonly long _budgetBytes;
    private readonly object _sync = new();
    private readonly Dictionary<TileKey, CacheEntry> _cache = new();
    private readonly LinkedList<TileKey> _lru = new();
    private Dictionary<TileKey, TileRequest> _desired = new(), _frameDesired = new();
    private readonly Dictionary<TileKey, TileJob> _pending = new();
    private readonly Dictionary<LiveTileKey, LiveTileEntry> _liveTiles = new();
    private readonly HashSet<LiveTileKey> _frameLiveSlots = new();
    private readonly Dictionary<(Guid TrackId, WaveformViewMode Mode), ReadyResolution> _readyResolutions = new();
    private int _workers;
    private long _retainedBytes, _tileBuildCount, _cacheHitCount, _cacheMissCount, _peakQueryCount, _fallbackTileCount, _canceledJobCount;
    private long _comparisonRasterBuildCount, _comparisonRasterHitCount, _comparisonVectorFallbackCount, _comparisonRasterTicks, _comparisonRasterFailureCount;
    private long _interactivePreviewDrawCount, _interactiveTileBuildCount, _interactivePeakSampleBuildCount, _liveRetainedBytes;
    private long _rasterBuildCount, _rasterHitCount, _rasterFailureCount, _rasterTicks, _resolutionFallbackHitCount;
    private long _resolutionFallbackImageDrawCount;
    private int _maxResolutionFallbackImagesPerTile;
    private int _lastFrameInteractiveTrackCount;
    private bool _disposed, _notificationQueued;

    private readonly record struct TileKey(Guid TrackId, object Original, object Processed, WaveformViewMode Mode, int Lod, long Index);
    private readonly record struct LiveTileKey(Guid TrackId, object Original, WaveformViewMode Mode, int Lod, long Index);
    private readonly record struct ReadyResolution(object Original, object Processed, int Lod);
    private sealed class LiveTileEntry(WaveformExtrema[] peaks)
    {
        public readonly WaveformExtrema[] OriginalPeaks = peaks;
        public object? GainKey;
        public TileDrawing? Drawing;
        public long Bytes => 256L + OriginalPeaks.LongLength * 8 + (Drawing?.Bytes ?? 0);
    }
    private sealed record TileRequest(TileKey Key, WaveformTileTrack Track, double PixelsPerSecond, double Start, double End,
        double Priority, ComparisonRasterSpec? RasterSpec);
    private sealed class TileJob(TileRequest request)
    {
        public TileRequest Request = request;
        public readonly CancellationTokenSource Cancellation = new();
        public bool Started;
    }
    private sealed class CacheEntry(TileDrawing drawing, LinkedListNode<TileKey> node)
    {
        public TileDrawing Drawing = drawing;
        public readonly LinkedListNode<TileKey> Node = node;
    }
    private sealed record TileDrawing(StreamGeometry Fill, StreamGeometry? Boosts, StreamGeometry? Cuts,
        StreamGeometry? OriginalOutline, long GeometryBytes, bool Final, ComparisonTileSamples? Comparison = null,
        ComparisonTileImage? Raster = null, ComparisonRasterSpec? FailedRasterSpec = null)
    {
        public long Bytes => GeometryBytes + (Raster?.Bytes ?? 0);
    }

    public WaveformTileRenderer(Dispatcher dispatcher, long budgetBytes = 128L * 1024 * 1024)
    {
        _dispatcher = dispatcher;
        _budgetBytes = Math.Max(1024 * 1024, budgetBytes);
    }

    public event Action? TilesReady;
    // A/B validation only; the legacy name now controls rasterization for every waveform mode.
    // This is deliberately not an application setting.
    public bool UseComparisonRasterization { get; set; } = true;
    public long TileBuildCount => Interlocked.Read(ref _tileBuildCount);
    public long CacheHitCount => Interlocked.Read(ref _cacheHitCount);
    public long PeakQueryCount => Interlocked.Read(ref _peakQueryCount);
    public int PendingTileCount { get { lock (_sync) return _pending.Count; } }
    public bool IsBusy => PendingTileCount > 0;
    public WaveformTileDiagnostics Diagnostics
    {
        get
        {
            lock (_sync) return new(TileBuildCount, CacheHitCount, Interlocked.Read(ref _cacheMissCount), PeakQueryCount,
                Interlocked.Read(ref _fallbackTileCount), Interlocked.Read(ref _canceledJobCount), _pending.Count, _cache.Count, _retainedBytes + _liveRetainedBytes,
                Interlocked.Read(ref _comparisonRasterBuildCount), Interlocked.Read(ref _comparisonRasterHitCount),
                Interlocked.Read(ref _comparisonVectorFallbackCount), Interlocked.Read(ref _comparisonRasterTicks) * 1000.0 / Stopwatch.Frequency,
                Interlocked.Read(ref _comparisonRasterFailureCount), Interlocked.Read(ref _interactivePreviewDrawCount),
                Interlocked.Read(ref _interactiveTileBuildCount), Interlocked.Read(ref _interactivePeakSampleBuildCount), _lastFrameInteractiveTrackCount,
                Interlocked.Read(ref _rasterBuildCount), Interlocked.Read(ref _rasterHitCount), Interlocked.Read(ref _rasterFailureCount),
                Interlocked.Read(ref _rasterTicks) * 1000.0 / Stopwatch.Frequency, Interlocked.Read(ref _resolutionFallbackHitCount),
                Interlocked.Read(ref _resolutionFallbackImageDrawCount), _maxResolutionFallbackImagesPerTile);
        }
    }

    public void BeginFrame()
    {
        _dispatcher.VerifyAccess();
        // Workers publish against the last complete frame while this frame is
        // assembled. Clearing their committed set here used to discard valid
        // completions between BeginFrame and the corresponding DrawTrack call.
        lock (_sync) { _frameDesired.Clear(); _frameLiveSlots.Clear(); _lastFrameInteractiveTrackCount = 0; }
    }

    /// <summary>
    /// Reconcile source work after a data/edit revision. A visible control has already requested
    /// its next composition frame, whose EndFrame atomically replaces the desired source set.
    /// Keep its immutable jobs alive until that reconciliation: status-only publications used
    /// to cancel every worker, including tiles for entirely unchanged tracks. Hidden controls
    /// cannot rely on another frame and must release their snapshots immediately.
    /// </summary>
    public void InvalidateSources(bool keepVisibleWork = false)
    {
        _dispatcher.VerifyAccess();
        lock (_sync)
        {
            if (_disposed) return;
            if (keepVisibleWork) return;
            _desired.Clear();
            _frameDesired.Clear();
            foreach (var job in _pending.Values)
            {
                job.Cancellation.Cancel();
                if (!job.Started)
                {
                    job.Cancellation.Dispose();
                    Interlocked.Increment(ref _canceledJobCount);
                }
            }
            // Started workers own their job until its next bounded cancellation check. Removing
            // it here prevents either obsolete publication or accidental requeue while hidden.
            _pending.Clear();
        }
    }

    public void DrawTrack(DrawingContext dc, WaveformTileTrack snapshot, double viewportStart, double viewportDuration,
        Rect area, double trackOffset, double center, double amplitude, WaveformViewMode mode, WaveformTileStyle style, double pixelsPerDip = 1,
        bool interactive = false)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || area.IsEmpty || area.Width <= 0 || viewportDuration <= 0 || !double.IsFinite(viewportDuration) || !double.IsFinite(viewportStart)) return;
        var duration = snapshot.Original.Duration;
        var first = Math.Max(0, viewportStart - trackOffset);
        var last = Math.Min(duration, viewportStart + viewportDuration - trackOffset);
        if (last <= first) return;
        var screenScale = area.Width / viewportDuration;
        var lod = Math.Clamp((int)Math.Ceiling(Math.Log2(screenScale) * 2), -48, 48);
        var tileScale = Math.Pow(2, lod * .5);
        interactive = interactive && mode != WaveformViewMode.Original && snapshot.Processed.Gain != null;
        if (interactive) { Interlocked.Increment(ref _interactivePreviewDrawCount); _lastFrameInteractiveTrackCount++; }
        var rasterSpec = UseComparisonRasterization && !interactive
            ? RasterSpec(area, center, amplitude, pixelsPerDip, style, mode) : null;
        var tileSeconds = TileColumns / tileScale;
        var firstIndex = Math.Max(0, (long)Math.Floor(first / tileSeconds));
        var lastIndex = Math.Max(firstIndex, (long)Math.Floor(Math.BitDecrement(last) / tileSeconds));
        var maxIndex = Math.Max(0, (long)Math.Ceiling(duration / tileSeconds) - 1);
        var originalKey = snapshot.Original.Key;
        var processedKey = mode == WaveformViewMode.Original ? originalKey : snapshot.Processed.Key;
        var completeResolution = !interactive && rasterSpec != null;
        var camera = new MatrixTransform(screenScale, 0, 0, amplitude, area.Left + (trackOffset - viewportStart) * screenScale, center);
        camera.Freeze();
        GeometryGroup? outlines = null;
        dc.PushClip(new RectangleGeometry(area));
        dc.PushTransform(camera);
        for (var index = Math.Max(0, firstIndex - 1); index <= Math.Min(maxIndex, lastIndex + 1); index++)
        {
            var start = index * tileSeconds;
            // Use the next grid coordinate on both sides of a tile join, avoiding a rounding
            // difference between repeated addition and multiplication at a device-pixel seam.
            var end = Math.Min(duration, (index + 1) * tileSeconds);
            var visible = index >= firstIndex && index <= lastIndex;
            var priority = (visible ? 0 : 1e9) + Math.Abs(trackOffset + (start + end) * .5 - (viewportStart + viewportDuration * .5));
            var key = new TileKey(snapshot.TrackId, originalKey, processedKey, mode, lod, index);
            var liveKey = new LiveTileKey(snapshot.TrackId, originalKey, mode, lod, index);
            var request = new TileRequest(key, snapshot, tileScale, start, end, priority, rasterSpec);
            TileDrawing? tile;
            var resolutionChecked = false;
            lock (_sync)
            {
                if (visible) _frameLiveSlots.Add(liveKey);
                if (!interactive) _frameDesired[key] = request;
                tile = interactive ? null : GetCached(key);
            }
            if (!visible) continue;
            if (interactive) tile = LiveDrawing(liveKey, request);
            else if (tile is { Final: true })
            {
                Interlocked.Increment(ref _cacheHitCount);
                if (snapshot.Processed.Gain == null) lock (_sync) RemoveLiveTile(liveKey);
            }
            else
            {
                Interlocked.Increment(ref _cacheMissCount);
                LiveTileEntry? live;
                lock (_sync) _liveTiles.TryGetValue(liveKey, out live);
                if (live != null && snapshot.Processed.Gain != null) tile = LiveDrawing(liveKey, request);
                else if (live?.Drawing != null && Equals(live.GainKey, processedKey)) tile = live.Drawing;
                if (tile is null && rasterSpec is { } fallbackSpec)
                {
                    resolutionChecked = true;
                    if (TryDrawResolutionFallback(dc, request, camera.Matrix, area, center, amplitude, fallbackSpec))
                    {
                        completeResolution = false;
                        continue;
                    }
                }
                if (tile is null)
                {
                    tile = BuildTile(request, true, CancellationToken.None);
                    Interlocked.Increment(ref _fallbackTileCount);
                    lock (_sync) PutCached(key, tile);
                }
            }
            if (rasterSpec is { } wantedRaster)
            {
                if (tile.Raster is { } raster && raster.Spec.SamePalette(wantedRaster))
                {
                    if (!tile.Final) completeResolution = false;
                    DrawRasterCore(dc, raster, start, end, camera.Matrix, area, center, amplitude, wantedRaster, mode);
                    continue;
                }
                if (!resolutionChecked && TryDrawResolutionFallback(dc, request, camera.Matrix, area, center, amplitude, wantedRaster))
                {
                    completeResolution = false;
                    continue;
                }
            }
            completeResolution = false;
            if (mode == WaveformViewMode.Compare)
            {
                Interlocked.Increment(ref _comparisonVectorFallbackCount);
                dc.PushOpacity(.66); dc.DrawGeometry(style.Fill, null, tile.Fill); dc.Pop();
                dc.PushOpacity(.9);
                if (tile.Boosts is { } boost) dc.DrawGeometry(style.Boost, null, boost);
                if (tile.Cuts is { } cut) dc.DrawGeometry(style.Cut, null, cut);
                dc.Pop();
                if (tile.OriginalOutline is { } outline) (outlines ??= new GeometryGroup { Transform = camera }).Children.Add(outline);
            }
            else dc.DrawGeometry(style.Fill, null, tile.Fill);
        }
        if (completeResolution)
            _readyResolutions[(snapshot.TrackId, mode)] = new ReadyResolution(originalKey, processedKey, lod);
        dc.Pop();
        if (outlines is { Children.Count: > 0 })
        {
            // Transform geometry, rather than its drawing context, to keep the contour one
            // device-independent pixel wide even under the nonuniform amplitude/time matrix.
            outlines.Freeze();
            var pen = new Pen(style.OriginalOutline, 1); pen.Freeze();
            dc.DrawGeometry(null, pen, outlines);
        }
        dc.Pop();
    }

    private void DrawRasterCore(DrawingContext dc, ComparisonTileImage raster, double start, double end, Matrix camera,
        Rect area, double center, double amplitude, ComparisonRasterSpec wanted, WaveformViewMode mode)
    {
        // The padded image is sampled smoothly, but only its own time core is composited.
        // Shared device-pixel boundaries prevent translucent seams between adjacent tiles.
        var leftPx = Math.Round((camera.OffsetX + start * camera.M11) * wanted.Dpi) / wanted.Dpi;
        var rightPx = Math.Round((camera.OffsetX + end * camera.M11) * wanted.Dpi) / wanted.Dpi;
        if (rightPx > leftPx)
        {
            var clipStart = (leftPx - camera.OffsetX) / camera.M11;
            var clipWidth = (rightPx - leftPx) / camera.M11;
            dc.PushClip(new RectangleGeometry(new Rect(clipStart, (area.Top - center) / amplitude, clipWidth, area.Height / amplitude)));
            dc.DrawImage(raster.Image, raster.LocalBounds);
            dc.Pop();
        }
        Interlocked.Increment(ref _rasterHitCount);
        if (mode == WaveformViewMode.Compare) Interlocked.Increment(ref _comparisonRasterHitCount);
    }

    private bool TryDrawResolutionFallback(DrawingContext dc, TileRequest request, Matrix camera, Rect area,
        double center, double amplitude, ComparisonRasterSpec wanted)
    {
        // Only the visible core needs coverage. Requiring the rest of a target
        // tile rejected useful detailed history at viewport/track edges.
        var start = Math.Max(request.Start, (area.Left - camera.OffsetX) / camera.M11);
        var end = Math.Min(request.End, (area.Right - camera.OffsetX) / camera.M11);
        if (end <= start) return false;
        // Miss-only fixed probes, never an LRU scan or a source query. Finer
        // history matters on zoom out; every candidate is capped at three image
        // cores, so the warm one-image path and the texture budget stay intact.
        Span<int> candidates = stackalloc int[5];
        candidates[0] = request.Key.Lod + 1; candidates[1] = request.Key.Lod - 1;
        candidates[2] = request.Key.Lod + 2; candidates[3] = request.Key.Lod - 2;
        var candidateCount = 4;
        if (_readyResolutions.TryGetValue((request.Key.TrackId, request.Key.Mode), out var ready) &&
            Equals(ready.Original, request.Key.Original) && Equals(ready.Processed, request.Key.Processed) &&
            ready.Lod != request.Key.Lod && Math.Abs(ready.Lod - request.Key.Lod) > 2)
            candidates[candidateCount++] = ready.Lod;
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var lod = candidates[candidate];
            if (lod is < -48 or > 48) continue;
            var seconds = TileColumns / Math.Pow(2, lod * .5);
            var firstIndex = Math.Max(0, (long)Math.Floor(start / seconds));
            var lastIndex = Math.Max(firstIndex, (long)Math.Floor(Math.BitDecrement(end) / seconds));
            var count = lastIndex - firstIndex + 1;
            if (count > ResolutionFallbackImageLimit) continue;
            ComparisonTileImage? firstImage = null, secondImage = null, thirdImage = null;
            lock (_sync)
            {
                var covered = true;
                for (var index = firstIndex; index <= lastIndex; index++)
                {
                    var key = request.Key with { Lod = lod, Index = index };
                    if (!_cache.TryGetValue(key, out var entry) || !entry.Drawing.Final ||
                        entry.Drawing.Raster is not { } image || !image.Spec.SamePalette(wanted)) { covered = false; break; }
                    if (index == firstIndex) firstImage = image;
                    else if (index == firstIndex + 1) secondImage = image;
                    else thirdImage = image;
                }
                if (!covered) continue;
                // Touch only a completely usable fallback. No failed probe changes eviction order.
                for (var index = firstIndex; index <= lastIndex; index++)
                    GetCached(request.Key with { Lod = lod, Index = index });
            }
            for (var offset = 0; offset < count; offset++)
            {
                var from = Math.Max(start, (firstIndex + offset) * seconds);
                var to = Math.Min(end, (firstIndex + offset + 1) * seconds);
                var image = offset == 0 ? firstImage! : offset == 1 ? secondImage! : thirdImage!;
                DrawRasterCore(dc, image, from, to, camera, area, center, amplitude, wanted, request.Key.Mode);
            }
            Interlocked.Increment(ref _resolutionFallbackHitCount);
            Interlocked.Add(ref _resolutionFallbackImageDrawCount, count);
            _maxResolutionFallbackImagesPerTile = Math.Max(_maxResolutionFallbackImagesPerTile, (int)count);
            return true;
        }
        return false;
    }

    private TileDrawing LiveDrawing(LiveTileKey key, TileRequest request)
    {
        LiveTileEntry? entry;
        lock (_sync) _liveTiles.TryGetValue(key, out entry);
        if (entry == null)
        {
            var columns = Math.Clamp((int)Math.Ceiling((request.End - request.Start) * request.PixelsPerSecond), 1, InteractiveColumns);
            var peaks = new WaveformExtrema[columns + 1];
            for (var i = 0; i <= columns; i++)
            {
                var start = request.Start + i * (request.End - request.Start) / columns;
                var end = Math.Min(request.Track.Original.Duration, start + (request.End - request.Start) / columns);
                // Complete published-bin extrema, not sparse probes or PCM queries. The grid
                // is fixed for the whole live gesture and independent of its changing gain.
                peaks[i] = request.Track.Original.QueryPublishedPeaks(start, end);
                Interlocked.Increment(ref _peakQueryCount);
            }
            entry = new LiveTileEntry(peaks);
            lock (_sync) { _liveTiles[key] = entry; _liveRetainedBytes += entry.Bytes; }
            Interlocked.Increment(ref _interactivePeakSampleBuildCount);
        }
        if (entry.Drawing != null && Equals(entry.GainKey, request.Key.Processed)) return entry.Drawing;
        var drawing = BuildTile(request, false, CancellationToken.None, entry);
        lock (_sync)
        {
            _liveRetainedBytes -= entry.Bytes;
            entry.Drawing = drawing; entry.GainKey = request.Key.Processed;
            _liveRetainedBytes += entry.Bytes;
            TrimCache();
        }
        Interlocked.Increment(ref _interactiveTileBuildCount);
        return drawing;
    }

    private void RemoveLiveTile(LiveTileKey key)
    {
        if (_liveTiles.Remove(key, out var entry)) _liveRetainedBytes -= entry.Bytes;
    }

    private ComparisonRasterSpec? RasterSpec(Rect area, double center, double amplitude, double pixelsPerDip, WaveformTileStyle style, WaveformViewMode mode)
    {
        if (area.Height <= 0 || amplitude <= 0 || !BrushColor(style.Fill, mode == WaveformViewMode.Compare ? .66 : 1, out var fill)) return null;
        uint boost = 0, cut = 0, outline = 0;
        if (mode == WaveformViewMode.Compare && (!BrushColor(style.Boost, .9, out boost) ||
            !BrushColor(style.Cut, .9, out cut) || !BrushColor(style.OriginalOutline, 1, out outline))) return null;
        var dpi1024 = (int)Math.Round((double.IsFinite(pixelsPerDip) ? Math.Clamp(pixelsPerDip, .5, 8) : 1) * 1024);
        var dpi = dpi1024 / 1024.0;
        var minimumTileBytes = (Math.Ceiling(TileColumns * dpi) + 2 * (Math.Ceiling(dpi * 2) + 1)) * 64 * 8
            + TileColumns * 12 * 96 + 32768;
        if (minimumTileBytes > _budgetBytes) return null;
        var requestedHeight = Math.Clamp(Math.Ceiling(area.Height * dpi / 64) * 64, 64, 2048);
        // Bound the WHOLE current raster working set, rather than admitting textures which
        // continually evict another visible tile. Four tile margins cover camera-edge tiles and
        // prefetch; the remaining budget pays for vectors, palette variants, and LRU history.
        // Use the worst density anywhere within a sqrt(2) LOD bucket. Its budget must not
        // fluctuate during zoom animation and turn a camera-only change into a raster rebuild.
        var totalColumns = (Math.Ceiling(area.Width * Math.Sqrt(2) / TileColumns) + 4) * TileColumns;
        var totalPhysicalWidth = totalColumns * dpi + 256;
        var budgetHeight = Math.Max(64, Math.Floor(_budgetBytes * .65 / (8 * totalPhysicalWidth) / 64) * 64);
        var height = (int)Math.Min(requestedHeight, budgetHeight);
        return new ComparisonRasterSpec(height, dpi1024, Math.Round((center - area.Top) / area.Height, 9),
            Math.Round(amplitude / area.Height, 9), fill, boost, cut, outline);
    }

    private static bool BrushColor(Brush brush, double opacity, out uint color)
    {
        if (brush is not SolidColorBrush solid) { color = 0; return false; }
        var c = solid.Color;
        var a = (uint)Math.Clamp((int)Math.Round(c.A * solid.Opacity * opacity), 0, 255);
        color = a << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
        return true;
    }

    /// <summary>Call after every visible track has registered its desired tiles.</summary>
    public void EndFrame()
    {
        _dispatcher.VerifyAccess();
        lock (_sync)
        {
            if (_disposed) return;
            // Publish the complete request set atomically. Reuse both buffers so
            // camera frames add no dictionary allocation/copy and retain only
            // the current frame's source snapshots.
            (_desired, _frameDesired) = (_frameDesired, _desired);
            _frameDesired.Clear();
            foreach (var key in _liveTiles.Keys.Where(key => !_frameLiveSlots.Contains(key)).ToArray()) RemoveLiveTile(key);
            var needed = _desired.Values
                .Where(r => !_cache.TryGetValue(r.Key, out var cached) || !cached.Drawing.Final ||
                    r.RasterSpec is { } spec && cached.Drawing.Raster?.Spec != spec && cached.Drawing.FailedRasterSpec != spec)
                .OrderBy(r => r.Priority).Take(PendingLimit).ToArray();
            var wanted = needed.Select(r => r.Key).ToHashSet();
            foreach (var pair in _pending.ToArray())
            {
                if (wanted.Contains(pair.Key)) continue;
                pair.Value.Cancellation.Cancel();
                if (!pair.Value.Started) { _pending.Remove(pair.Key); pair.Value.Cancellation.Dispose(); Interlocked.Increment(ref _canceledJobCount); }
            }
            foreach (var request in needed)
            {
                if (_pending.TryGetValue(request.Key, out var existing) && !existing.Cancellation.IsCancellationRequested)
                {
                    if (!existing.Started || existing.Request.RasterSpec == request.RasterSpec)
                    {
                        existing.Request = request;
                        continue;
                    }
                    existing.Cancellation.Cancel();
                }
                _pending[request.Key] = new TileJob(request);
            }
            while (_workers < WorkerLimit && _pending.Values.Any(j => !j.Started && !j.Cancellation.IsCancellationRequested))
            {
                _workers++;
                _ = Task.Run(Pump);
            }
        }
    }

    /// <summary>Release data snapshots for closed tracks; offscreen live tracks keep their warm tiles.</summary>
    public void RetainTracks(IEnumerable<Guid> trackIds)
    {
        _dispatcher.VerifyAccess();
        var live = trackIds.ToHashSet();
        lock (_sync)
        {
            foreach (var key in _cache.Keys.Where(k => !live.Contains(k.TrackId)).ToArray()) RemoveCached(key);
            foreach (var key in _liveTiles.Keys.Where(k => !live.Contains(k.TrackId)).ToArray()) RemoveLiveTile(key);
            foreach (var key in _desired.Keys.Where(k => !live.Contains(k.TrackId)).ToArray()) _desired.Remove(key);
            foreach (var key in _frameDesired.Keys.Where(k => !live.Contains(k.TrackId)).ToArray()) _frameDesired.Remove(key);
            foreach (var key in _readyResolutions.Keys.Where(k => !live.Contains(k.TrackId)).ToArray()) _readyResolutions.Remove(key);
            foreach (var pair in _pending.Where(p => !live.Contains(p.Key.TrackId)).ToArray())
            {
                pair.Value.Cancellation.Cancel();
                if (!pair.Value.Started) { _pending.Remove(pair.Key); pair.Value.Cancellation.Dispose(); Interlocked.Increment(ref _canceledJobCount); }
            }
        }
    }

    private void Pump()
    {
        while (true)
        {
            TileJob? job;
            TileRequest request;
            TileDrawing? previous;
            lock (_sync)
            {
                job = _disposed ? null : _pending.Values.Where(j => !j.Started && !j.Cancellation.IsCancellationRequested).MinBy(j => j.Request.Priority);
                if (job is null) { _workers--; return; }
                job.Started = true;
                request = job.Request;
                previous = _cache.TryGetValue(request.Key, out var entry) && entry.Drawing.Final ? entry.Drawing : null;
            }
            TileDrawing? tile = null;
            try
            {
                tile = previous;
                if (tile is null)
                {
                    tile = BuildTile(request, false, job.Cancellation.Token);
                    Interlocked.Increment(ref _tileBuildCount);
                }
                if (request.RasterSpec is { } spec && tile.Comparison is { } comparison && tile.Raster?.Spec != spec)
                {
                    try
                    {
                        var started = Stopwatch.GetTimestamp();
                        var raster = request.Key.Mode == WaveformViewMode.Compare
                            ? ComparisonTileRasterizer.Build(comparison, spec, job.Cancellation.Token)
                            : WaveformTileRasterizer.BuildFill(comparison, spec, job.Cancellation.Token);
                        var elapsed = Stopwatch.GetTimestamp() - started;
                        Interlocked.Add(ref _rasterTicks, elapsed); Interlocked.Increment(ref _rasterBuildCount);
                        if (request.Key.Mode == WaveformViewMode.Compare)
                        {
                            Interlocked.Add(ref _comparisonRasterTicks, elapsed);
                            Interlocked.Increment(ref _comparisonRasterBuildCount);
                        }
                        tile = tile with { Raster = raster, FailedRasterSpec = null };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception)
                    {
                        // Native bitmap allocation can fail. Keep the exact vector tile and
                        // remember this source/spec failure, rather than spinning a perpetual
                        // rebuild/notification loop. A changed palette/size/source may try again.
                        tile = tile with { FailedRasterSpec = spec };
                        Interlocked.Increment(ref _rasterFailureCount);
                        if (request.Key.Mode == WaveformViewMode.Compare) Interlocked.Increment(ref _comparisonRasterFailureCount);
                    }
                }
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref _canceledJobCount); }
            catch (Exception) { /* Keep the coarse tile if published source data is unavailable. */ }
            var publish = false;
            lock (_sync)
            {
                if (!_disposed && tile is not null && !job.Cancellation.IsCancellationRequested &&
                    _desired.TryGetValue(request.Key, out var desired) && desired.RasterSpec == request.RasterSpec)
                {
                    PutCached(request.Key, tile);
                    publish = true;
                }
                if (_pending.TryGetValue(request.Key, out var current) && ReferenceEquals(current, job)) _pending.Remove(request.Key);
                job.Cancellation.Dispose();
            }
            if (publish) NotifyTilesReady();
        }
    }

    private void NotifyTilesReady()
    {
        lock (_sync)
        {
            if (_disposed || _notificationQueued || _dispatcher.HasShutdownStarted) return;
            _notificationQueued = true;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                lock (_sync) { _notificationQueued = false; if (_disposed) return; }
                TilesReady?.Invoke();
            }));
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted)
        {
            lock (_sync) _notificationQueued = false;
        }
    }

    private TileDrawing BuildTile(TileRequest request, bool coarse, CancellationToken cancellationToken, LiveTileEntry? live = null)
    {
        var width = (request.End - request.Start) * request.PixelsPerSecond;
        var columns = live?.OriginalPeaks.Length - 1 ?? Math.Clamp((int)Math.Ceiling(width), 1, coarse ? CoarseColumns : TileColumns);
        var original = request.Key.Mode == WaveformViewMode.Compare ? live?.OriginalPeaks ?? new WaveformExtrema[columns + 1] : null;
        var processed = new WaveformExtrema[columns + 1];
        var x = new double[columns + 1];
        for (var i = 0; i <= columns; i++)
        {
            if ((i & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var start = request.Start + i * (request.End - request.Start) / columns;
            var end = Math.Min(request.Track.Original.Duration, start + (request.End - request.Start) / columns);
            x[i] = start;
            if (live != null)
            {
                processed[i] = request.Track.Processed.Gain?.Apply(live.OriginalPeaks[i], (start + end) * .5) ?? live.OriginalPeaks[i];
                continue;
            }
            if (original is not null)
            {
                original[i] = request.Track.Original.Query(start, end, coarse);
                Interlocked.Increment(ref _peakQueryCount);
            }
            var source = request.Key.Mode == WaveformViewMode.Original ? request.Track.Original : request.Track.Processed;
            processed[i] = source.Query(start, end, coarse);
            Interlocked.Increment(ref _peakQueryCount);
        }
        var fill = new StreamGeometry();
        using (var context = fill.Open())
        {
            context.BeginFigure(new Point(x[0], -processed[0].Max), true, true);
            for (var i = 1; i <= columns; i++) context.LineTo(new Point(x[i], -processed[i].Max), true, false);
            for (var i = columns; i >= 0; i--) context.LineTo(new Point(x[i], -processed[i].Min), true, false);
        }
        fill.Freeze();
        long points = (columns + 1) * 2;
        StreamGeometry? boosts = null, cuts = null, outline = null;
        if (original is not null)
        {
            boosts = new StreamGeometry(); cuts = new StreamGeometry(); outline = new StreamGeometry();
            using (var boost = boosts.Open())
            using (var cut = cuts.Open())
            using (var edge = outline.Open())
            {
                edge.BeginFigure(new Point(x[0], -original[0].Max), false, false);
                for (var i = 1; i <= columns; i++) edge.LineTo(new Point(x[i], -original[i].Max), true, false);
                edge.BeginFigure(new Point(x[0], -original[0].Min), false, false);
                for (var i = 1; i <= columns; i++) edge.LineTo(new Point(x[i], -original[i].Min), true, false);
                points += (columns + 1) * 2;
                for (var i = 0; i < columns; i++)
                {
                    points += AddBand(processed[i].Max > original[i].Max ? boost : cut, x[i], x[i + 1], -original[i].Max, -processed[i].Max);
                    points += AddBand(processed[i].Min < original[i].Min ? boost : cut, x[i], x[i + 1], -original[i].Min, -processed[i].Min);
                }
            }
            boosts.Freeze(); cuts.Freeze(); outline.Freeze();
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Conservative accounting includes native geometry storage, not just managed references.
        var comparison = new ComparisonTileSamples(request.Start, request.End, request.PixelsPerSecond, original ?? processed, processed);
        var sampleBytes = (long)(columns + 1) * (original is null ? 8 : 16);
        return new TileDrawing(fill, boosts, cuts, outline, 8192 + points * 96 + sampleBytes, !coarse, comparison);
    }

    private static int AddBand(StreamGeometryContext context, double left, double right, double from, double to)
    {
        if (Math.Abs(from - to) < .000001) return 0;
        context.BeginFigure(new Point(left, from), true, true);
        context.LineTo(new Point(right, from), true, false);
        context.LineTo(new Point(right, to), true, false);
        context.LineTo(new Point(left, to), true, false);
        return 4;
    }

    private TileDrawing? GetCached(TileKey key)
    {
        if (!_cache.TryGetValue(key, out var entry)) return null;
        _lru.Remove(entry.Node); _lru.AddFirst(entry.Node);
        return entry.Drawing;
    }

    private void PutCached(TileKey key, TileDrawing drawing)
    {
        if (_disposed) return;
        if (_cache.TryGetValue(key, out var existing))
        {
            // A UI fallback must never overwrite a worker result that landed in between.
            if (existing.Drawing.Final && !drawing.Final) return;
            _retainedBytes -= existing.Drawing.Bytes;
            existing.Drawing = drawing;
            _retainedBytes += drawing.Bytes;
            _lru.Remove(existing.Node); _lru.AddFirst(existing.Node);
        }
        else
        {
            var node = _lru.AddFirst(key);
            _cache.Add(key, new CacheEntry(drawing, node));
            _retainedBytes += drawing.Bytes;
        }
        TrimCache();
    }

    private void TrimCache()
    {
        while (_retainedBytes + _liveRetainedBytes > _budgetBytes && _lru.Last is { } tail) RemoveCached(tail.Value);
        while (_retainedBytes + _liveRetainedBytes > _budgetBytes && _liveTiles.Count > 0) RemoveLiveTile(_liveTiles.Keys.First());
    }

    private void RemoveCached(TileKey key)
    {
        if (!_cache.Remove(key, out var entry)) return;
        _retainedBytes -= entry.Drawing.Bytes;
        _lru.Remove(entry.Node);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _pending.Values)
            {
                job.Cancellation.Cancel();
                if (!job.Started) job.Cancellation.Dispose();
            }
            _pending.Clear(); _desired.Clear(); _frameDesired.Clear(); _readyResolutions.Clear(); _cache.Clear(); _lru.Clear(); _liveTiles.Clear(); _frameLiveSlots.Clear(); _retainedBytes = _liveRetainedBytes = 0;
        }
    }
}

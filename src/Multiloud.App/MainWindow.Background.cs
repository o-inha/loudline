using System.IO;
using System.Windows;
using Microsoft.Win32;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private BackgroundLayer _backgroundLayer = new();
    private readonly Dictionary<Guid, (string Key, float[] Samples)> _backgroundSegments = new();
    private readonly Dictionary<Guid, (float[] Source, int Frames, float[] Samples)> _backgroundOriginals = new();
    private BackgroundLayer? _backgroundUiLayer;
    private int _backgroundUiSources;

    private void InitializeBackground()
    {
        InitializeBackgroundGain();
        BackgroundLane.Timeline = Timeline;
        BackgroundLane.CanDropFiles = CanDropBackgroundFiles;
        BackgroundLane.FilesDropped += (paths, position) => _ = ImportBackgroundFilesAsync(paths, position);
        BackgroundLane.ClipMoved += (clip, position) => EditBackgroundClip(clip, position, clip.FadeInSeconds, clip.FadeOutSeconds);
        BackgroundLane.FadesChanged += (clip, fadeIn, fadeOut) => EditBackgroundClip(clip, clip.StartSeconds, fadeIn, fadeOut);
        BackgroundLane.RemoveRequested += clip => SetBackgroundLayer(_backgroundLayer with
        { Clips = _backgroundLayer.Clips.Where(item => item.Id != clip.Id).ToArray() });
        BackgroundLane.RelinkRequested += clip => _ = RelinkBackgroundAsync(clip);
        BackgroundLane.SeekRequested += position => { Timeline.Playhead = position; SeekPlayback(position); };
        RefreshBackground();
    }

    private double BackgroundAlbumStart(AudioTrack track)
    {
        double start = 0;
        foreach (var item in _tracks) { if (ReferenceEquals(item, track)) break; start += item.Duration; }
        return start;
    }
    private bool UsesBackgroundFormat => _backgroundLayer.Enabled && _backgroundLayer.Clips.Count > 0;
    private void ArmPlaybackBeforeStructure(double position, bool playing, bool paused)
    {
        if (OriginalRadio.IsChecked == true && !IsPeakDelta && (playing || paused)) ArmBackgroundOriginalResume(position, paused);
        else ArmInterruptedPlayback(position, playing, false, _revision);
    }
    private (long Start, int Frames) BackgroundOutputRange(AudioTrack track)
    {
        double start = BackgroundAlbumStart(track);
        long first = (long)Math.Round(start * 48000), end = (long)Math.Round((start + track.Duration) * 48000);
        return (first, checked((int)Math.Max(0, end - first)));
    }
    private string? BackgroundKey(AudioTrack track)
    {
        if (!UsesBackgroundFormat) return null;
        var range = BackgroundOutputRange(track);
        return PreviewCache.CreateBackgroundKey(_backgroundLayer, range.Start / 48000.0,
            range.Frames / 48000.0, 48000, 2) ?? "silence:" + range.Frames;
    }
    private bool BackgroundRequired(AudioTrack track) => UsesBackgroundFormat;
    private int PlaybackSampleRate(AudioTrack track) => UsesBackgroundFormat ? 48000 : track.SampleRate;
    private int PlaybackChannels(AudioTrack track) => UsesBackgroundFormat ? 2 : track.Channels;
    private double PlaybackDuration(AudioTrack track) => UsesBackgroundFormat ? BackgroundOutputRange(track).Frames / 48000.0 : track.Duration;
    // Original monitoring streams the source and positioned bed directly; it
    // never waits for resampling/rendering an entire music clip.
    private float[]? OriginalPlaybackSamples(AudioTrack track) =>
        track.OriginalFrameCount > 0 && track.Original.Length > 0 ? track.Original : null;

    private float[]? PreparedOriginalSamples(AudioTrack track)
    {
        if (!UsesBackgroundFormat) return track.Original.Length > 0 ? track.Original : null;
        int frames = BackgroundOutputRange(track).Frames;
        if (_backgroundOriginals.TryGetValue(track.Id, out var cached) && ReferenceEquals(cached.Source, track.Original) && cached.Frames == frames)
            return cached.Samples;
        if (PreviewCurrent(track, _settings) && track.Render?.BackgroundOriginalSamples is { } ready && ready.Length == (long)frames * 2)
        { _backgroundOriginals[track.Id] = (track.Original, frames, ready); return ready; }
        return null;
    }
    private static float[] PrepareBackgroundMusic(LiveAudioSource source, int frames, CancellationToken ct)
    {
        if (source.SampleRate == 48000 && source.Channels == 2 && source.FrameOffset == 0 &&
            source.FrameCount == source.Samples.Length / 2 && source.Samples.Length == (long)frames * 2)
            return source.Samples;
        var reader = source.CreateReader(48000, 2);
        if (reader.FrameCount != frames) throw new InvalidOperationException("The music region is not aligned to the album clock.");
        var result = new float[checked(frames * 2)];
        const int block = 16384;
        for (int first = 0; first < frames; first += block)
            reader.Read(first, result, checked(first * 2), Math.Min(block, frames - first), ct);
        return result;
    }
    private float[]? CurrentBackgroundSamples(AudioTrack track)
    {
        var key = BackgroundKey(track);
        if (key == null) return null;
        if (_backgroundSegments.TryGetValue(track.Id, out var cached) && cached.Key == key) return cached.Samples;
        if (PreviewCurrent(track, _settings) && track.Render?.BackgroundSamples is { } ready)
        { _backgroundSegments[track.Id] = (key, ready); return ready; }
        return null;
    }
    private string? BackgroundBlockReason(AudioTrack track)
    {
        if (!_backgroundLayer.Enabled) return null;
        double start = BackgroundAlbumStart(track), end = start + track.Duration;
        var clip = _backgroundLayer.Clips.FirstOrDefault(item => item.StartSeconds < end && item.EndSeconds > start && item.Source.Original.Length == 0);
        return clip == null ? null : clip.Source.WorkState == TrackWorkState.Missing ? "Background file not found" : "Background loading";
    }
    private void RememberBackgroundSegment(AudioTrack track, RenderResult render)
    {
        var key = BackgroundKey(track);
        if (key != null && render.BackgroundSamples is { } samples) _backgroundSegments[track.Id] = (key, samples);
        else _backgroundSegments.Remove(track.Id);
        if (render.BackgroundOriginalSamples is { } original && render.RenderedChannels == 2)
            _backgroundOriginals[track.Id] = (track.Original, original.Length / 2, original);
    }

    private void RefreshBackground()
    {
        if (BackgroundLane == null) return;
        Timeline.EmptyDurationHint = _tracks.Count == 0 ? Math.Max(0, _backgroundLayer.Clips.Select(clip => clip.EndSeconds).DefaultIfEmpty(0).Max()) : 0;
        foreach (var id in _backgroundSegments.Keys.Where(id => !_tracks.Any(track => track.Id == id)).ToArray()) _backgroundSegments.Remove(id);
        foreach (var id in _backgroundOriginals.Keys.Where(id => !_tracks.Any(track => track.Id == id)).ToArray()) _backgroundOriginals.Remove(id);
        var signature = new HashCode();
        foreach (var clip in _backgroundLayer.Clips) { signature.Add(clip.Source.Original); signature.Add(clip.Source.OriginalWaveform); signature.Add(clip.Source.WorkState); signature.Add(clip.Source.Duration); }
        int sources = signature.ToHashCode();
        if (!ReferenceEquals(_backgroundUiLayer, _backgroundLayer) || sources != _backgroundUiSources)
        {
            BackgroundLane.Layer = _backgroundLayer; _backgroundUiLayer = _backgroundLayer; _backgroundUiSources = sources;
        }
        bool syncing = _syncing; _syncing = true;
        try
        {
            BackgroundEnabledCheck.IsChecked = _backgroundLayer.Enabled;
            SyncBackgroundGainControls();
            BackgroundPane.IsEnabled = !_exporting;
            AddBackgroundButton.IsEnabled = !_importing && !_exporting;
        }
        finally { _syncing = syncing; }
        RefreshAutoProcessState();
    }

    private void SetBackgroundLayer(BackgroundLayer layer, bool history = true)
    {
        if (!_initialized || _closed || _exporting) return;
        ValidateBackgroundLayer(layer);
        if (_backgroundLayer == layer || _backgroundLayer.Enabled == layer.Enabled && _backgroundLayer.GainDb == layer.GainDb && _backgroundLayer.Clips.SequenceEqual(layer.Clips)) return;
        if (history) PushUndo();
        _backgroundLayer = layer; _dirty = true;
        RefreshBackground(); QueueRender();
    }
    private static void ValidateBackgroundLayer(BackgroundLayer layer)
    {
        if (!double.IsFinite(layer.GainDb) || layer.GainDb is < -60 or > 12 || layer.Clips == null ||
            layer.Clips.Select(clip => clip.Id).Distinct().Count() != layer.Clips.Count ||
            layer.Clips.Any(clip => !double.IsFinite(clip.StartSeconds) || clip.StartSeconds is < -86400 or > 86400 ||
                !double.IsFinite(clip.FadeInSeconds) || !double.IsFinite(clip.FadeOutSeconds) ||
                clip.FadeInSeconds < 0 || clip.FadeOutSeconds < 0 || clip.FadeInSeconds > clip.Source.Duration || clip.FadeOutSeconds > clip.Source.Duration))
            throw new ArgumentException("Invalid background gain, position or fades.");
    }
    private void EditBackgroundClip(BackgroundClip clip, double position, double fadeIn, double fadeOut)
    {
        if (_importing || _exporting) return;
        var current = _backgroundLayer.Clips.FirstOrDefault(item => item.Id == clip.Id);
        if (current == null) return;
        double rate = Math.Max(1, current.Source.SampleRate);
        var edited = current with
        {
            StartSeconds = Math.Clamp(Math.Round(position * rate) / rate, -86400, 86400),
            FadeInSeconds = Math.Clamp(fadeIn, 0, current.Source.Duration), FadeOutSeconds = Math.Clamp(fadeOut, 0, current.Source.Duration)
        };
        SetBackgroundLayer(_backgroundLayer with { Clips = _backgroundLayer.Clips.Select(item => item.Id == current.Id ? edited : item).ToArray() });
    }
    private void BackgroundEnabledChanged(object sender, RoutedEventArgs e)
    { if (_initialized && !_syncing && !_importing && !_exporting) SetBackgroundLayer(_backgroundLayer with { Enabled = BackgroundEnabledCheck.IsChecked == true }); }
    private bool CanDropBackgroundFiles(IEnumerable<string> paths) => !_closed && !_importing && !_exporting && paths.Any(path =>
        File.Exists(path) && new[] { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".aif", ".aiff", ".wma" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    private void AddBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (_importing || _exporting) return;
        var picker = new OpenFileDialog { Filter = AudioFileService.FileFilter, Multiselect = true, Title = "Add background audio" };
        if (picker.ShowDialog(this) == true) _ = ImportBackgroundFilesAsync(picker.FileNames, 0);
    }
    private async Task ImportBackgroundFilesAsync(string[] paths, double position)
    {
        if (!CanDropBackgroundFiles(paths)) return;
        var files = TrackOrdering.SortPaths(paths.Where(path => File.Exists(path) && CanDropBackgroundFiles([path])).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return;
        PushUndo(); BeginSourceProcessing(); _importing = true; ExportButton.IsEnabled = false;
        var pending = files.Select(path => new BackgroundClip
        {
            Source = new AudioTrack { FilePath = path, WorkState = TrackWorkState.Unprocessed, Status = "File not processed" },
            StartSeconds = Math.Clamp(position, -86400, 86400)
        }).ToArray();
        _backgroundLayer = _backgroundLayer with { Clips = _backgroundLayer.Clips.Concat(pending).ToArray() };
        _dirty = true; RefreshBackground(); RefreshList();
        var cts = new CancellationTokenSource(); _importCts = cts; var errors = new List<string>();
        try
        {
            SetBusy("Opening background audio…", true);
            // Finish this small metadata-only pass even if Cancel is pressed.
            // It retains every selected source and meaningful, saveable placement
            // without decoding its audio or allocating a waveform/PCM buffer.
            var metadata = await Task.Run(() => pending.Select(clip =>
            {
                try { return (Clip: clip, Info: AudioFileService.ReadSourceInfo(clip.Source.FilePath), Error: (Exception?)null); }
                catch (Exception error) { return (Clip: clip, Info: (Duration: 0d, SampleRate: 48000, Channels: 2), Error: error); }
            }).ToArray());
            if (_closed) return;
            foreach (var item in metadata)
            {
                item.Clip.Source.SavedDuration = item.Info.Duration;
                item.Clip.Source.SampleRate = item.Info.SampleRate; item.Clip.Source.Channels = item.Info.Channels;
                if (item.Error != null) errors.Add($"{item.Clip.Source.Name}: {item.Error.Message}");
            }
            double next = Math.Clamp(position, -86400, 86400);
            var starts = new Dictionary<Guid, double>();
            foreach (var clip in pending) { starts[clip.Id] = Math.Clamp(next, -86400, 86400); next += clip.Source.Duration; }
            _backgroundLayer = _backgroundLayer with { Clips = _backgroundLayer.Clips.Select(clip => starts.TryGetValue(clip.Id, out double at) ? clip with { StartSeconds = at } : clip).ToArray() };
            RefreshBackground(); RefreshList();
            cts.Token.ThrowIfCancellationRequested();
            for (int index = 0; index < pending.Length; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var clip = pending[index]; var source = clip.Source;
                SetBusy($"Loading background: {source.Name}…", true);
                try
                {
                    await LoadBackgroundSourceAsync(source, source.FilePath, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    // Some compressed containers revise their duration during
                    // decode. Keep the remaining batch adjacent to the actual
                    // decoded end rather than to its provisional header estimate.
                    var current = _backgroundLayer.Clips.FirstOrDefault(item => item.Id == clip.Id);
                    if (current != null)
                    {
                        next = current.EndSeconds;
                        starts.Clear();
                        for (int following = index + 1; following < pending.Length; following++)
                        { starts[pending[following].Id] = Math.Clamp(next, -86400, 86400); next += pending[following].Source.Duration; }
                        _backgroundLayer = _backgroundLayer with { Clips = _backgroundLayer.Clips.Select(item => starts.TryGetValue(item.Id, out double at) ? item with { StartSeconds = at } : item).ToArray() };
                    }
                    RefreshBackground(); RefreshList(); QueueRender(addition: true);
                }
                catch (OperationCanceledException) { MarkNotProcessed(source); throw; }
                catch (Exception error)
                {
                    source.WorkState = TrackWorkState.Failed; source.Status = $"Could not process file: {error.Message}";
                    errors.Add($"{source.Name}: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Background import cancelled."; }
        finally
        {
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            bool cancelled = cts.IsCancellationRequested;
            cts.Dispose(); _importing = false;
            if (!_closed)
            {
                if (cancelled) FinishSourceCancellation();
                else { RefreshBackground(); RefreshList(); QueueRender(addition: true); }
            }
        }
        if (errors.Count > 0 && !_closed) ShowError("Some background audio could not be imported", new IOException(string.Join("\n\n", errors.Distinct())));
    }

}

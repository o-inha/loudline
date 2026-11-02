using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;
public partial class MainWindow : Window
{
    private readonly List<AudioTrack> _tracks = [];
    private readonly ObservableCollection<TrackListEntry> _trackRows = [];
    private readonly Dictionary<Guid, TrackListEntry> _rowsById = new();
    private readonly Dictionary<Guid, string> _renderKeys = new();
    private readonly PreviewCache _previewCache = new(2L * 1024 * 1024 * 1024);
    private int _renderExecutionCount;
    private Dictionary<Guid, string>? _plannedRenderKeys;
    private ProcessingSettings _settings = new();
    private readonly PlaybackService _player = new();
    private PlaybackTrackState[] _playbackTracks = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _wideLayout, _updatingResponsive;
    private bool _refreshingDevices, _syncingDevices;
    private string? _outputDeviceId, _defaultDeviceId, _failedDeviceId;
    private bool _resumeAfterDeviceLoss;
    private string _deviceListSignature = "";
    private List<OutputChoice> _outputChoices = [];
    private sealed record OutputChoice(string? Id, string Name);
    private sealed class AudioPreferences
    {
        public AudioPreferences() { }
        public string? OutputDeviceId { get; set; }
        public bool IconsOnlyButtons { get; set; }
    }
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loudline", "preferences.json");
    private static string LegacyPreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiloud", "preferences.json");
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private CancellationTokenSource? _renderCts, _importCts, _exportCts;
    private int _revision, _renderedRevision = -1, _renderStartedRevision = -1;
    private bool _initialized, _syncing, _importing, _exporting, _dirty, _closed;
    private string? _projectPath;
    private bool _floatExport;
    private bool _smokeSawAnalyzing, _smokeSawQueued, _smokeSawProcessing, _smokeSawPartialPlaybackReadiness;
    private readonly Stack<EditSnapshot> _undo = new(), _redo = new();
    private AudioTrack? Selected => (FileList.SelectedItem as TrackListEntry)?.Track;
    private static string F(double value, string format = "0.0") => double.IsFinite(value) && value > -100 ? value.ToString(format, CultureInfo.InvariantCulture) : "−∞";

    private bool _editorInitialized, _editorLoaded;

    public MainWindow() : this(deferEditor: false) { }

    internal MainWindow(bool deferEditor)
    {
        if (deferEditor) StartupFrame.Prepare(this);
        else InitializeEditor();
    }

    internal void InitializeEditor()
    {
        if (_editorInitialized) return;
        _editorInitialized = true;
        bool shown = IsVisible;
        double left = Left, top = Top, width = Width, height = Height;
        WindowState state = WindowState;
        if (shown)
        {
            Content = null;
            ClearValue(BackgroundProperty); ClearValue(ForegroundProperty);
        }
        InitializeComponent();
        if (shown)
        {
            Left = left; Top = top; Width = width; Height = height; WindowState = state;
        }
        FileList.ItemsSource = _trackRows;
        Timeline.Tracks = _tracks;
        ApplyTimelineSettings();
        Timeline.SelectionChanged += track => SelectTrack(track);
        Timeline.TrackMoveRequested += MoveTrack;
        Timeline.CanDropFiles = CanDropAudioFiles;
        Timeline.FilesDropRequested += (paths, index) => _ = ImportDroppedFilesAsync(paths, index);
        Timeline.TrackRemoveRequested += RemoveTrack;
        Timeline.TrackRelinkRequested += RequestTrackRelink;
        Timeline.CursorInfoChanged += UpdateCursorPosition;
        Timeline.TrackContextRequested += (track, point) => ShowTrackContextMenu(track, Timeline, point);
        FileList.PreviewMouseRightButtonDown += FileListTrackContext;
        FileList.PreviewMouseLeftButtonDown += FileListGripDown;
        FileList.PreviewMouseMove += FileListGripMove;
        FileList.PreviewMouseLeftButtonUp += (_, _) => _fileListDragTrack = null;
        FileList.DragOver += FileListTrackDragOver;
        FileList.DragLeave += (_, _) => ClearFileListDropMarker();
        FileList.Drop += FileListTrackDrop;
        Timeline.SeekRequested += SeekPlayback;
        InitializeScrubbing();
        Timeline.EditStarted += PushUndo;
        Timeline.EditCommitted += _ => { _dirty = true; QueueRender(); };
        Timeline.RebuildRequested += () => QueueRender();
        PreviewLabel.MouseLeftButtonUp += (_, _) => { if (RebuildPreviewButton.Visibility == Visibility.Visible) _ = ResumeProcessingAsync(); };
        foreach (var slider in new[] { StrengthSlider, MatchingStrengthSlider })
        {
            slider.PreviewMouseWheel += StrengthWheel;
            slider.PreviewMouseMove += StrengthDragMove;
            slider.PreviewMouseLeftButtonUp += StrengthDragEnd;
            slider.LostMouseCapture += StrengthCaptureLost;
        }
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        WaveformPane.SizeChanged += (_, _) => UpdateResponsiveLayout();
        _timer.Tick += (_, _) => { if (IsPlayheadScrubbing) { UpdateTime(); return; } if (_renderStartedRevision == _revision && _renderCts is { IsCancellationRequested: false }) StopInvalidatedPlayback(); if (_player.IsPlaying) Timeline.Playhead = _player.PositionSeconds; UpdateTime(); UpdateMaximizerMeter(); UpdatePlayButton(); };
        _player.PlaybackEnded += () => { CancelInterruptedPlayback(); Timeline.Playhead = _player.PositionSeconds; UpdateTime(); PlayButton.Content = "▶"; };
        _player.PlaybackWaiting += PlaybackWaiting;
        _player.PlaybackError += PlaybackFailed;
        SystemEvents.UserPreferenceChanged += WindowsPreferenceChanged;
        SourceInitialized += (_, _) => InitializeEditorWindowHandle();
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero) InitializeEditorWindowHandle();
        Closing += ClosingWindow;
        _initialized = true;
        InitializeTargetReadout();
        InitializeProjectEnvelope();
        InitializeBackground();
        InitializeSlicing();
        InitializeCustomBoundaries();
        InitializeLoops();
        InitializeAutoProcess();
        SyncMaximizerControls(); UpdateExportFormatChecks();
        ExportButton.IsEnabled = false;
        VersionText.Text = AppVersion.Display;
        Title = $"Loudline {AppVersion.Display} — music loudness matching";
        SetPreviewStatus(true);
        _timer.Start();
        LoadAudioPreference();
        _deviceTimer.Tick += (_, _) => _ = RefreshOutputDevicesAsync();
        Loaded += async (_, _) => await FinishEditorLoadedAsync();
        if (IsLoaded) _ = FinishEditorLoadedAsync();
        UpdateThemeChecks();
    }

    private void InitializeEditorWindowHandle()
    {
        ThemeService.Apply(ThemeService.Mode);
        AttachHorizontalScrolling();
    }

    private async Task FinishEditorLoadedAsync()
    {
        if (_editorLoaded) return;
        _editorLoaded = true;
        UpdateResponsiveLayout();
        await RefreshOutputDevicesAsync();
        if (!_closed) _deviceTimer.Start();
    }

    private void LoadAudioPreference()
    {
        try
        {
            string source = File.Exists(PreferencesPath) ? PreferencesPath : LegacyPreferencesPath;
            var preferences = File.Exists(source) ? JsonSerializer.Deserialize<AudioPreferences>(File.ReadAllText(source)) : null;
            _outputDeviceId = preferences?.OutputDeviceId;
            InitializeButtonStyle(preferences?.IconsOnlyButtons == true);
            _player.OutputDeviceId = _outputDeviceId;
            if (source == LegacyPreferencesPath && File.Exists(source)) SaveAudioPreference();
        }
        catch { _outputDeviceId = null; _player.OutputDeviceId = null; InitializeButtonStyle(false); }
    }
    private void SaveAudioPreference()
    {
        if (App.SmokeMode) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new AudioPreferences { OutputDeviceId = _outputDeviceId, IconsOnlyButtons = _iconsOnlyButtonStyle }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private async Task RefreshOutputDevicesAsync()
    {
        if (_refreshingDevices || _closed) return;
        _refreshingDevices = true;
        try
        {
            var result = await Task.Run(() => (Devices: PlaybackService.EnumerateOutputDevices(), DefaultId: PlaybackService.GetDefaultOutputDeviceId()));
            if (_closed) return;
            bool missing = _outputDeviceId != null && !result.Devices.Any(device => device.Id == _outputDeviceId);
            bool defaultChanged = _outputDeviceId == null && _defaultDeviceId != result.DefaultId;
            _defaultDeviceId = result.DefaultId;
            string? desired = missing ? null : _outputDeviceId;
            string? resolved = desired ?? _defaultDeviceId;
            string? defaultName = result.Devices.FirstOrDefault(device => device.Id == result.DefaultId)?.Name;
            var choices = new List<OutputChoice> { new(null, defaultName == null ? "Windows default (no device)" : $"Windows default · {defaultName}") };
            choices.AddRange(result.Devices.Select(device => new OutputChoice(device.Id, device.Name)));
            string signature = JsonSerializer.Serialize(choices);
            if (signature != _deviceListSignature || missing)
            {
                _syncingDevices = true;
                try
                {
                    _outputChoices = choices; OutputDeviceBox.ItemsSource = choices;
                    OutputDeviceBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == desired) ?? choices[0];
                    _deviceListSignature = signature;
                }
                finally { _syncingDevices = false; }
            }
            bool recover = _resumeAfterDeviceLoss && resolved != null && resolved != _failedDeviceId;
            if (missing || defaultChanged || recover)
                SwitchOutputDevice(desired, recover, missing ? "Selected output disconnected; using Windows default." : null);
        }
        catch (Exception ex)
        {
            if (!_closed && !_importing && !_exporting && !App.SmokeMode) StatusText.Text = $"Could not refresh audio outputs: {ex.Message}";
        }
        finally { _refreshingDevices = false; }
    }
    private void OutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingDevices || OutputDeviceBox.SelectedItem is not OutputChoice choice || choice.Id == _outputDeviceId) return;
        string? resolved = choice.Id ?? _defaultDeviceId;
        bool recover = _resumeAfterDeviceLoss && resolved != null && resolved != _failedDeviceId;
        SwitchOutputDevice(choice.Id, recover);
    }
    private async void OutputDeviceDropDownOpened(object? sender, EventArgs e) => await RefreshOutputDevicesAsync();
    private async void RefreshDevicesClick(object sender, RoutedEventArgs e) => await RefreshOutputDevicesAsync();
    private void SwitchOutputDevice(string? id, bool recover = false, string? message = null)
    {
        CancelPlayheadScrubbing();
        bool playing = _player.IsPlaying;
        double position = playing || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        _player.Stop(); _player.OutputDeviceId = id; _outputDeviceId = id; SaveAudioPreference();
        Timeline.Playhead = position; UpdateTime();
        if ((id ?? _defaultDeviceId) == null)
        {
            _resumeAfterDeviceLoss |= playing || recover;
            UpdatePlayButton();
            StatusText.Text = "No audio output";
            return;
        }
        _resumeAfterDeviceLoss = false;
        if (playing || recover) StartPlayback(position);
        else UpdatePlayButton();
        if (message != null) StatusText.Text = message;
    }
    private void PlaybackWaiting(Guid id, double position) => HandlePlaybackWaiting(id, position);
    private bool HandlePlaybackWaiting(Guid id, double position, Action<double>? resume = null)
    {
        if (!_playbackRequested) return false;
        Timeline.Playhead = position; UpdateTime();
        // The final render can publish and clear its CTS before this queued
        // callback arrives. A ready Result is also eligible for immediate resume.
        var state = PlaybackTrackAt(CapturePlaybackTracks(), position);
        var track = _tracks.FirstOrDefault(item => item.Id == id);
        if (!IsPeakDelta && OriginalRadio.IsChecked == true && track != null && BackgroundRequired(track))
            return HandleBackgroundOriginalWaiting(id, position, resume);
        bool updating = _renderCts is { IsCancellationRequested: false } ||
            track is { WorkState: TrackWorkState.Idle } && PreviewCurrent(track, _settings);
        bool armed = _playbackRequested && updating && state?.Id == id &&
            ArmInterruptedPlayback(position, true, OriginalRadio.IsChecked == true && !IsPeakDelta, _revision);
        if (armed && TryResumeInterruptedPlayback(_revision, resume)) return true;
        if (!armed) CancelInterruptedPlayback();
        UpdatePlayButton();
        StatusText.Text = track?.WorkState == TrackWorkState.Missing ? "Playback stopped at a missing file" : "Playback paused at an unfinished track";
        return false;
    }
    private void PlaybackFailed(Exception error)
    {
        if (_closed) return;
        CancelInterruptedPlayback();
        Timeline.Playhead = _player.PositionSeconds; UpdateTime(); PlayButton.Content = "▶";
        _resumeAfterDeviceLoss = error is COMException;
        _failedDeviceId = _outputDeviceId ?? _defaultDeviceId;
        if (error is COMException)
        {
            StatusText.Text = "Audio output disconnected";
            _ = RefreshOutputDevicesAsync();
        }
        else ShowError("Playback failed", error);
    }

    private void WindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (ThemeService.Mode == "System" && !_closed) Dispatcher.BeginInvoke(() => { ThemeService.Apply("System"); Timeline.Refresh(); BackgroundLane.Refresh(); StrengthSlider.InvalidateVisual(); });
    }
    private void SetBusy(string text, bool cancel = false) { StatusText.Text = text; WorkProgress.Visibility = Visibility.Visible; RebuildPreviewButton.Visibility = Visibility.Collapsed; CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed; }
    private void SetIdle(string text) { StatusText.Text = text; WorkProgress.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; }
    private void ShowError(string message, Exception ex)
    {
        if (_closingProject) return;
        App.LogException(ex);
        StatusText.Text = $"{message}: {ex.Message}";
        if (!App.SmokeMode) MessageBox.Show(this, ex.Message, message, MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void RefreshList()
    {
        RefreshAutoProcessState();
        if (App.SmokeMode)
        {
            _smokeSawAnalyzing |= _tracks.Any(track => track.WorkState == TrackWorkState.Analyzing);
            _smokeSawQueued |= _tracks.Any(track => track.WorkState == TrackWorkState.Queued);
            _smokeSawProcessing |= _tracks.Any(track => track.WorkState == TrackWorkState.Processing);
        }
        PublishPlaybackTracks();
        if (App.SmokeMode && _playbackTracks.Any(track => track.PreviewSamples != null) && _playbackTracks.Any(track => track.PreviewSamples == null))
            _smokeSawPartialPlaybackReadiness = true;
        var selected = Selected?.Id;
        _syncing = true;
        var activeIds = _tracks.Select(track => track.Id).ToHashSet();
        for (int index = _trackRows.Count - 1; index >= 0; index--)
            if (!activeIds.Contains(_trackRows[index].Track.Id)) { _rowsById.Remove(_trackRows[index].Track.Id); _trackRows.RemoveAt(index); }
        for (int index = 0; index < _tracks.Count; index++)
        {
            var track = _tracks[index];
            if (!_rowsById.TryGetValue(track.Id, out var row))
            {
                row = new TrackListEntry(track); _rowsById.Add(track.Id, row); _trackRows.Insert(index, row);
            }
            else
            {
                if (!ReferenceEquals(row.Track, track))
                {
                    int oldIndex = _trackRows.IndexOf(row);
                    row = new TrackListEntry(track); _rowsById[track.Id] = row; _trackRows[oldIndex] = row;
                }
                if (!ReferenceEquals(_trackRows[index], row)) _trackRows.Move(_trackRows.IndexOf(row), index);
            }
            row.Update(index);
        }
        if (selected is { } id && _rowsById.TryGetValue(id, out var selectedRow)) FileList.SelectedItem = selectedRow;
        _syncing = false;
        ReleaseSummary.Text = $"{_tracks.Count} track{(_tracks.Count == 1 ? "" : "s")}  ·  {TimeSpan.FromSeconds(_tracks.Sum(t => t.Duration)):hh\\:mm\\:ss}";
        UpdateSelectionInfo();
        ApplyTimelineSettings(false);
        Timeline.Refresh();
        RefreshBackground();
        RefreshTargetReadout();
    }
    private void SelectTrack(AudioTrack? track)
    {
        if (_syncing) return;
        if (track != null && _rowsById.TryGetValue(track.Id, out var row)) { FileList.SelectedItem = row; FileList.ScrollIntoView(row); }
        Timeline.SelectedTrack = track; UpdateSelectionInfo();
    }
    private void UpdateSelectionInfo()
    {
        var t = Selected;
        UpdateMaximizerMeter();
        RefreshTargetReadout();
        double peak = t?.Render?.TruePeakDb ?? t?.Analysis?.TruePeakDb ?? double.NaN;
        string peakText = double.IsFinite(peak) ? $"  ·  Peak {F(peak)} dBTP" : "";
        SelectionInfo.Text = t?.Analysis == null ? "" : $"Gain {F((t.Render?.MatchGainDb ?? 0) + t.TrimDb)} dB{peakText}\nLRA {F(t.Analysis.LoudnessRange)} LU  ·  Reduction {F(t.Render?.MaxReductionDb ?? 0)} peak / {F(t.Render?.AverageReductionDb ?? 0)} avg dB  ·  Limited {F(t.Render?.LimitedPercent ?? 0)}% · Heavy {F(t.Render?.HeavyLimitingPercent ?? 0)}%";
    }
    private void FileSelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) { Timeline.SelectedTrack = Selected; UpdateSelectionInfo(); } }
    private void FileDoubleClick(object sender, MouseButtonEventArgs e) { if (Selected != null) Timeline.FitTrack(Selected); }
    private void AddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = AudioFileService.FileFilter, Multiselect = true, Title = "Add music to release" };
        if (dialog.ShowDialog(this) == true) _ = ImportFilesAsync(dialog.FileNames);
    }
    private void FilesDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var paths = FileDropPaths(e.Data);
        e.Effects = paths != null && CanDropAudioFiles(paths) && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void FilesDropped(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var paths = FileDropPaths(e.Data);
        e.Handled = paths != null;
        if (paths != null && (e.AllowedEffects & DragDropEffects.Copy) != 0) _ = ImportDroppedFilesAsync(paths, _tracks.Count);
    }
    private async Task ImportFilesAsync(IEnumerable<string> paths, List<ProjectTrack>? saved = null, int? insertionIndex = null)
    {
        if (_closingProject) return;
        if (_importing || _exporting) { StatusText.Text = "Wait for the current import or export to finish."; return; }
        var existing = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !_tracks.Any(track => string.Equals(track.FilePath, path, StringComparison.OrdinalIgnoreCase)));
        var files = (saved == null ? TrackOrdering.SortPaths(existing) : existing).ToArray();
        if (files.Length == 0) return;
        int boundary = Math.Clamp(insertionIndex ?? _tracks.Count, 0, _tracks.Count);
        PushUndo(); _dirty = true; _importing = true; _processingPaused = false; ExportButton.IsEnabled = false;
        BeginSourceProcessing();
        var pending = files.Select(path => new AudioTrack { FilePath = path, Status = "File not processed", WorkState = TrackWorkState.Unprocessed }).ToArray();
        var insertionAnchor = CaptureImportPosition();
        if (boundary < _tracks.Count) StopForImportStructure(insertionAnchor);
        _tracks.InsertRange(boundary, pending);
        RefreshList(); RestoreImportPosition(insertionAnchor);
        var authoredSettings = _settings;
        _importCts = new CancellationTokenSource(); var ct = _importCts.Token;
        var errors = new List<string>();
        try
        {
            foreach (var track in pending)
            {
                ct.ThrowIfCancellationRequested();
                string path = track.FilePath;
                if (EstimateWorkingAudioBytes() > 12L * 1024 * 1024 * 1024) throw new InvalidOperationException("This release exceeds the 12 GB working audio budget. Export or remove tracks before importing more.");
                track.Status = "Loading audio…"; track.WorkState = TrackWorkState.Analyzing;
                var previousOrder = _tracks.ToArray();
                var positionAnchor = CaptureImportPosition();
                RefreshList(); RestoreImportPosition(positionAnchor); SelectTrack(track);
                SetBusy($"Loading {Path.GetFileName(path)}…", true);
                try
                {
                    var decoded = await Task.Run(() => {
                        var t = AudioFileService.Load(path, ct);
                        t.OriginalWaveform = WaveformPyramid.Build(t.Original, t.Channels, ct);
                        t.OriginalPeaks = BuildDisplayPeaks(t.OriginalWaveform, ct);
                        return t;
                    }, ct);
                    ct.ThrowIfCancellationRequested();
                    if (_closed || !_tracks.Contains(track)) throw new OperationCanceledException(ct);
                    positionAnchor = CaptureImportPosition(positionAnchor);
                    track.Original = decoded.Original; track.SampleRate = decoded.SampleRate; track.Channels = decoded.Channels;
                    track.OriginalWaveform = decoded.OriginalWaveform; track.OriginalPeaks = decoded.OriginalPeaks;
                    if (saved == null && TryPublishIdentityPreview(track, _settings))
                    {
                        UpdateTransitions(previousOrder);
                        RefreshList(); RestoreImportPosition(positionAnchor); QueueRender(addition: true);
                        continue;
                    }
                    track.Status = "Analyzing loudness…"; RefreshList(); RestoreImportPosition(positionAnchor);
                    SetBusy($"Analyzing {track.Name}…", true);
                    var samples = track.Original; int sampleRate = track.SampleRate, channels = track.Channels;
                    var analysis = await Task.Run(() => LoudnessAnalyzer.Analyze(samples, sampleRate, channels, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    if (_closed || !_tracks.Contains(track) || !ReferenceEquals(track.Original, samples)) throw new OperationCanceledException(ct);
                    track.Analysis = analysis;
                    var suggestion = EnvelopeDesigner.Design(analysis, track.Duration, _settings);
                    // Any points authored while this new source was analyzing
                    // are offsets from its initially flat pending envelope.
                    track.Envelope = track.Envelope.Count == 0 ? suggestion : EnvelopeMath.Canonicalize(
                        TransitionDesigner.Combine(suggestion, track.Envelope).Select(point => point with { GainDb = Math.Clamp(point.GainDb, -24, 24) }).ToArray());
                    var state = saved?.FirstOrDefault(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                    if (state != null) { track.TrimDb = Math.Clamp(state.TrimDb, -30, 30); track.InputGainDb = state.InputGainDb; track.TargetLufs = state.TargetLufs; track.Envelope = RetargetLevelingEnvelope(ValidateEnvelope(state.Envelope, track.Duration), analysis, track.Duration, authoredSettings, _settings);
                        track.TransitionEnvelope = state.TransitionEnvelope is { Count: > 0 } ? ValidateEnvelope(state.TransitionEnvelope, track.Duration).Select(point => point with { GainDb = Math.Clamp(point.GainDb, -2, 2) }).ToList() : []; }
                    if (saved == null) UpdateTransitions(previousOrder);
                    else if (saved.Any(item => item.TransitionEnvelope == null)) UpdateTransitions();
                    track.Status = "Queued for preview"; track.WorkState = TrackWorkState.Queued;
                    RefreshList(); QueueRender(addition: true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                finally
                {
                    if (ct.IsCancellationRequested)
                    {
                        if (!SourceAnalysisSatisfied(track, _settings) || !PreviewCurrent(track, _settings)) MarkNotProcessed(track);
                    }
                    else if (!SourceAnalysisSatisfied(track, _settings))
                    {
                        var removalAnchor = CaptureImportPosition(positionAnchor);
                        StopForImportStructure(removalAnchor, allowResume: !ct.IsCancellationRequested);
                        if (ct.IsCancellationRequested && removalAnchor != null)
                            removalAnchor = removalAnchor with { Playing = false, NavigationVersion = _importNavigationVersion };
                        RemoveFailedImportTrack(track);
                        RefreshList(); RestoreImportPosition(removalAnchor, resumeOriginal: !ct.IsCancellationRequested);
                    }
                }
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Import cancelled."; }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            _importing = false; bool cancelled = ct.IsCancellationRequested;
            _importCts.Dispose(); _importCts = null;
            if (!_closed)
            {
                if (cancelled) FinishSourceCancellation();
                else { RefreshList(); RestoreImportPosition(_originalImportResume); QueueRender(addition: true); }
            }
            _originalImportResume = null;
        }
        if (errors.Count > 0 && !_closed) ShowError("Some audio could not be imported", new InvalidOperationException(string.Join("\n\n", errors)));
    }

    private void UpdateTransitions(IReadOnlyList<AudioTrack>? previousOrder = null) => SyncProjectEnvelope();
    private static ProcessingSettings EffectiveSettings(AudioTrack track, ProcessingSettings settings) => settings with { TargetLufs = track.TargetLufs ?? settings.TargetLufs };
    private string RenderKey(AudioTrack track, ProcessingSettings settings) =>
        PreviewCache.CreateKey(track, settings, _backgroundLayer, BackgroundAlbumStart(track));
    private bool PreviewCurrent(AudioTrack track, ProcessingSettings settings) => track.Render != null && _renderKeys.TryGetValue(track.Id, out var key) && key == RenderKey(track, settings);
    private void StopWorkIndicators(string status)
    {
        foreach (var track in _tracks.Where(track => track.WorkState is TrackWorkState.Processing or TrackWorkState.Queued))
        {
            track.WorkState = TrackWorkState.Failed; track.Status = status;
        }
        RefreshList();
    }

    private void SetPreviewStatus(bool ready, bool pending = false)
    {
        PreviewLabel.Text = ready ? "✓" : pending ? "◷" : "↻";
        PreviewLabel.Foreground = (Brush)FindResource(ready ? "AccentBrush" : "MutedBrush");
        PreviewLabel.Cursor = ready || pending ? Cursors.Arrow : Cursors.Hand;
        var rebuildVisibility = !ready && !pending ? Visibility.Visible : Visibility.Collapsed;
        if (RebuildPreviewButton.Visibility != rebuildVisibility) { RebuildPreviewButton.Visibility = rebuildVisibility; UpdateResponsiveLayout(); }
    }
    private void RememberActivePreview(AudioTrack track)
    {
        if (track.Render != null && _renderKeys.TryGetValue(track.Id, out var key) && PreviewCache.IsForSource(track, key))
            _previewCache.Store(key, track.Render, track.PreviewPeaks);
    }
    private void CompletePreview(int revision)
    {
        bool complete = _tracks.All(track => SourceAnalysisSatisfied(track, _settings) && PreviewCurrent(track, _settings) && BackgroundExportReady(track));
        if (complete) { _renderedRevision = revision; if (!_importing) RememberCompletedProcessing(); }
        ExportButton.IsEnabled = complete && _tracks.Count > 0 && !_importing && !_exporting;
        bool pending = !_processingPaused && _tracks.Any(track => track.WorkState is TrackWorkState.Analyzing or TrackWorkState.Queued or TrackWorkState.Processing);
        SetPreviewStatus(complete, pending: pending);
        int missing = _tracks.Count(track => track.WorkState == TrackWorkState.Missing) +
            (_backgroundLayer.Enabled ? _backgroundLayer.Clips.Count(clip => clip.Source.WorkState == TrackWorkState.Missing) : 0);
        if (missing > 0 && !pending)
        {
            PreviewLabel.Text = "!"; PreviewLabel.Foreground = (Brush)FindResource("MissingTextBrush"); PreviewLabel.Cursor = Cursors.Arrow;
            RebuildPreviewButton.Visibility = _tracks.Any(track => track.WorkState == TrackWorkState.Unprocessed) || HasUnprocessedSources
                ? Visibility.Visible : Visibility.Collapsed; UpdateResponsiveLayout();
        }
        int unprocessed = _tracks.Count(track => track.WorkState == TrackWorkState.Unprocessed) +
            _backgroundLayer.Clips.Count(clip => clip.Source.WorkState == TrackWorkState.Unprocessed);
        RebuildPreviewButton.Content = unprocessed > 0 || HasUnprocessedSources ? "Resume processing" : "Process";
        if (!pending && (unprocessed > 0 || HasUnprocessedSources)) RebuildPreviewButton.Visibility = Visibility.Visible;
        int failed = _tracks.Count(track => track.WorkState == TrackWorkState.Failed && !PreviewCurrent(track, _settings));
        if (!_importing && !_exporting)
        {
            if (pending) SetBusy("Processing…", true);
            else SetIdle(missing > 0 ? $"{missing} missing file{(missing == 1 ? "" : "s")}" :
                unprocessed > 0 ? $"{unprocessed} file{(unprocessed == 1 ? "" : "s")} not processed" : failed > 0 ? $"{failed} Result{(failed == 1 ? "" : "s")} unavailable" : _tracks.Count == 0 ? "" : complete ? "Ready" : "Result needs updating");
        }
        RefreshList();
    }
    private async void QueueRender(bool addition = false)
    {
        if (!_initialized || _closed || _closingProject || _exporting) return;
        if (_processingPaused)
        {
            if (addition) { CompletePreview(_revision); return; }
            _processingPaused = false;
        }
        SyncProjectEnvelope();
        bool awaitingProjectSourceAnalysis = _settings.LevelingEnabled && _importing &&
            _tracks.Any(track => track.Analysis == null && track.WorkState is not (TrackWorkState.Missing or TrackWorkState.Failed));
        // Appending a track can change its new neighbor's edge correction. Keep
        // existing work only when all already-planned source variants still match.
        if (!awaitingProjectSourceAnalysis && addition && !NeedsDeferredSourceAnalysis && _renderCts is { IsCancellationRequested: false } && _plannedRenderKeys != null &&
            _plannedRenderKeys.All(pair => _tracks.FirstOrDefault(track => track.Id == pair.Key) is { } track && pair.Value == RenderKey(track, _settings)))
        { RefreshList(); return; }
        if (!awaitingProjectSourceAnalysis && !NeedsDeferredSourceAnalysis && _renderCts is { IsCancellationRequested: false } && _plannedRenderKeys != null &&
            _plannedRenderKeys.Keys.All(id => _tracks.Any(track => track.Id == id)) &&
            _tracks.Where(track => track.Analysis != null).All(track => _plannedRenderKeys.TryGetValue(track.Id, out var key) && key == RenderKey(track, _settings)))
        {
            // Bypassed/irrelevant controls must not restart identical work in flight.
            ApplyTimelineSettings(); RefreshList(); return;
        }
        int revision = ++_revision;
        RebaseInterruptedPlayback(revision);
        _renderCts?.Cancel();
        var settings = _settings;
        // Pin all cache hits before storing old active variants, which can evict
        // other historical entries when a large album exceeds the cache budget.
        var hits = new Dictionary<AudioTrack, (string Key, CachedPreview Preview)>();
        foreach (var track in _tracks.Where(track => track.Original.Length > 0 && !PreviewCurrent(track, settings)))
        {
            string key = RenderKey(track, settings);
            if (_previewCache.TryGet(key, out var cached)) hits.Add(track, (key, cached));
        }
        foreach (var pair in hits)
        {
            var track = pair.Key; RememberActivePreview(track);
            track.Render = pair.Value.Preview.Render; track.PreviewPeaks = pair.Value.Preview.Peaks; _renderKeys[track.Id] = pair.Value.Key;
            RememberBackgroundSegment(track, track.Render);
        }
        foreach (var track in _tracks.Where(track => track.Original.Length > 0))
            TryPublishIdentityPreview(track, settings);
        foreach (var track in _tracks.Where(track => SourceAnalysisSatisfied(track, settings) || TrackNeedsDeferredSourceAnalysis(track)))
        {
            if (_importing && track.Analysis == null && track.WorkState == TrackWorkState.Analyzing) continue;
            bool current = PreviewCurrent(track, settings);
            var blocked = BackgroundBlockReason(track);
            track.WorkState = current ? TrackWorkState.Idle : blocked != null ? TrackWorkState.Failed : TrackWorkState.Queued;
            track.Status = current ? track.Render?.Warning ?? "Ready" : blocked ?? "Queued for preview";
        }
        ApplyTimelineSettings();
        // Source-owned imports/slices finish their analyses themselves. Their
        // final publication will restart this queue with a complete detector.
        if (awaitingProjectSourceAnalysis) { CompletePreview(revision); return; }
        bool needsRender = _tracks.Any(track => track.Analysis != null && BackgroundBlockReason(track) == null && !PreviewCurrent(track, settings));
        // Dynamic leveling has one project-wide detector and envelope. Do not
        // render known clips against a partial suggestion while other decoded
        // clips still need analysis: each new analysis would invalidate them.
        if (NeedsDeferredSourceAnalysis && (settings.LevelingEnabled || !needsRender))
        {
            await EnsureDeferredAnalysisAsync(revision, coalesce: !addition);
            return;
        }
        if (!needsRender)
        {
            // Returning to the active settings is an immediate no-op. Returning
            // to a cached variant swaps buffers without rerunning analysis/DSP.
            CompletePreview(revision);
            return;
        }
        BeginProcessingTransaction();
        ExportButton.IsEnabled = false;
        SetPreviewStatus(false, pending: true);
        if (!_importing) SetBusy("Processing…", true);
        RefreshList();
        var cts = new CancellationTokenSource(); _renderCts = cts; var ct = cts.Token;
        _plannedRenderKeys = _tracks.Where(track => track.Analysis != null && BackgroundBlockReason(track) == null).ToDictionary(track => track.Id, track => RenderKey(track, settings));
        try
        {
            // A short settling period coalesces quick toggles, slider drags and undo.
            if (!addition) await Task.Delay(500, ct);
            await _renderGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                _renderStartedRevision = revision;
                // Choose work from the audible clock after settling/gate wait,
                // before stopping a stale stream and freezing its position.
                double priorityPosition = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
                StopInvalidatedPlayback(priorityPosition);
                var attempted = new HashSet<AudioTrack>();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // Re-read the audible/playhead position only between files.
                    // A seek reprioritizes pending work without restarting the
                    // file already rendering or changing the album's order.
                    double nextPosition = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
                    var track = RenderOrder(_tracks, nextPosition).FirstOrDefault(candidate => !attempted.Contains(candidate) &&
                        candidate.Analysis != null && BackgroundBlockReason(candidate) == null && !PreviewCurrent(candidate, settings));
                    if (track == null) break;
                    attempted.Add(track); // A failed source is tried once in this revision.
                    string key = RenderKey(track, settings);
                    var points = track.Envelope.ToArray(); var transitions = track.TransitionEnvelope.ToArray();
                    double trim = track.TrimDb, inputGain = track.InputGainDb;
                    var effectiveSettings = EffectiveSettings(track, settings);
                    var previousRender = track.Render;
                    var sourceSamples = track.Original; var sourceId = track.SourceId;
                    int sampleRate = track.SampleRate, channels = track.Channels;
                    var backgroundLayer = _backgroundLayer;
                    var sourceRange = LiveSourceFor(track) with { Background = null, DualMono = true };
                    string? bedKey = BackgroundKey(track); var cachedBed = CurrentBackgroundSamples(track);
                    var range = BackgroundOutputRange(track); var cachedOriginal = bedKey == null ? null : PreparedOriginalSamples(track);
                    int renderRate = bedKey == null ? sampleRate : 48000, renderChannels = bedKey == null ? channels : 2;
                    track.Status = "Processing preview…"; track.WorkState = TrackWorkState.Processing; RefreshList();
                    if (!_importing && !_exporting) SetBusy($"Building preview: {track.Name}…", true);
                    (RenderResult Render, List<PeakBin> Peaks) result;
                    try
                    {
                        float[] renderMusic = sourceSamples; float[]? bed = null;
                        if (bedKey != null)
                        {
                            var prepared = await Task.Run(() => (
                                Music: cachedOriginal ?? PrepareBackgroundMusic(sourceRange, range.Frames, ct),
                                Bed: cachedBed ?? BackgroundRenderer.RenderSegment(backgroundLayer, range.Start / 48000.0,
                                    range.Frames / 48000.0, 48000, 2, ct)), ct);
                            ct.ThrowIfCancellationRequested();
                            if (revision != _revision || !_tracks.Contains(track) || track.SourceId != sourceId || !ReferenceEquals(track.Original, sourceSamples)) return;
                            renderMusic = prepared.Music; bed = prepared.Bed;
                            _backgroundOriginals[track.Id] = (sourceSamples, range.Frames, renderMusic);
                            _backgroundSegments[track.Id] = (bedKey, bed);
                            RefreshList(); // Original can resume before the measured Result finishes.
                        }
                        result = await Task.Run(() => {
                            ct.ThrowIfCancellationRequested();
                            Interlocked.Increment(ref _renderExecutionCount);
                            var processorMusic = bed == null ? track.GetOriginalRegion(ct) : renderMusic;
                            var render = bed == null
                                ? AudioProcessor.RenderIncremental(processorMusic, renderRate, renderChannels, points, trim, effectiveSettings,
                                    previousRender?.HasAudibleBackground == true ? null : previousRender, ct, inputGainDb: inputGain, transitionEnvelope: transitions)
                                : AudioProcessor.RenderWithBackground(renderMusic, renderRate, renderChannels, points, trim, effectiveSettings,
                                    bed, ct, inputGainDb: inputGain, transitionEnvelope: transitions, previous: previousRender);
                            render.RenderedSampleRate = renderRate; render.RenderedChannels = renderChannels;
                            if (bed != null) render.BackgroundOriginalSamples = renderMusic;
                            ct.ThrowIfCancellationRequested();
                            render.Waveform = BuildPreviewWaveform(render, previousRender, renderChannels, ct);
                            return (Render: render, Peaks: BuildDisplayPeaks(render.Waveform, ct));
                        }, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (bedKey != null)
                    {
                        if (revision != _revision) return;
                        track.WorkState = TrackWorkState.Failed; track.Status = error.Message;
                        // The old active buffer/key remains an undoable variant.
                        // Current-key comparison and Failed state reject it here.
                        RefreshList(); continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    if (revision != _revision || !_tracks.Contains(track) || track.SourceId != sourceId || !ReferenceEquals(track.Original, sourceSamples)) return;
                    RememberActivePreview(track);
                    track.Render = result.Render; track.PreviewPeaks = result.Peaks; _renderKeys[track.Id] = key;
                    RememberBackgroundSegment(track, result.Render);
                    track.Status = result.Render.Warning ?? "Ready"; track.WorkState = TrackWorkState.Idle;
                    RefreshList();
                }
                CompletePreview(revision);
            }
            finally { _renderGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (revision == _revision && !_closed) { CancelInterruptedPlayback(); StopWorkIndicators("Preview cancelled"); StopInvalidatedPlayback(allowResume: false); SetPreviewStatus(false); if (!_importing && !_exporting) SetIdle("Preview cancelled"); }
        }
        catch (Exception ex)
        {
            if (revision == _revision && !_closed) { CancelInterruptedPlayback(); StopWorkIndicators("Preview failed"); StopInvalidatedPlayback(allowResume: false); SetPreviewStatus(false); if (!_importing && !_exporting) SetIdle("Preview failed"); ShowError("Could not build preview", ex); }
        }
        finally
        {
            bool drain = ReferenceEquals(_renderCts, cts) && !ct.IsCancellationRequested && revision == _revision && !_closed &&
                (_tracks.Any(track => track.Analysis != null && !PreviewCurrent(track, _settings) && track.WorkState == TrackWorkState.Queued) || NeedsDeferredSourceAnalysis);
            if (ReferenceEquals(_renderCts, cts)) { _renderCts = null; _plannedRenderKeys = null; }
            cts.Dispose();
            if (drain) QueueRender(addition: true);
        }
    }

    private void ProcessingChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncing || _exporting) return;
        PushUndo();
        _settings = _settings with { LevelingEnabled = LevelingCheck.IsChecked == true, MatchingEnabled = MatchingCheck.IsChecked == true, LimiterEnabled = LimiterCheck.IsChecked == true };
        _dirty = true; QueueRender();
    }
    private void TargetCommit(object sender, RoutedEventArgs e) => EndTargetEditing();
    private void TargetKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { EndTargetEditing(); Keyboard.ClearFocus(); e.Handled = true; } }
    private void CommitTarget()
    {
        if (!_initialized || _syncing || _exporting) return;
        if (!_targetTextDirty) return; // An amber estimate is never a new processing target.
        FinishStrengthGesture(false);
        if (!double.TryParse(TargetBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var target) || !double.IsFinite(target) || target is < -36 or > -6)
        {
            _targetTextDirty = false; RefreshTargetReadout();
            StatusText.Text = "Enter a target from −36 to −6 LUFS."; return;
        }
        _targetTextDirty = false; _targetComputedMode = false;
        SetTargetReadoutColor(false);
        if (Math.Abs(target - _settings.TargetLufs) < .001) { RefreshTargetReadout(); return; }
        PushUndo(); _settings = _settings with { TargetLufs = target }; _dirty = true; QueueRender();
        RefreshTargetReadout();
    }
    private void PresetsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var preset in new[] { ("Streaming reference  ·  −14 LUFS", -14d), ("Gentle / dynamic  ·  −18 LUFS", -18d), ("Louder release  ·  −11 LUFS", -11d), ("EBU broadcast reference  ·  −23 LUFS", -23d) })
        {
            var item = new MenuItem { Header = preset.Item1 };
            item.Click += (_, _) => ApplyTargetPreset(preset.Item2);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (Button)sender; ((Button)sender).ContextMenu = menu; menu.IsOpen = true;
    }
    private void SuggestClick(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        PushUndo();
        SyncProjectEnvelope(forceSuggestion: true); _dirty = true; QueueRender();
    }
    private void EnvelopeOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        Timeline.ShowEnvelopeOnWaveform = WaveformEnvelopeCheck.IsChecked == true;
    }
    private void ViewChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        CancelPlayheadScrubbing();
        bool wasOriginal = Timeline.ViewMode == WaveformViewMode.Original;
        Timeline.ShowGainLines = ShowGains.IsChecked == true;
        Timeline.ViewMode = OriginalRadio.IsChecked == true ? WaveformViewMode.Original : CompareRadio.IsChecked == true ? WaveformViewMode.Compare : WaveformViewMode.Preview;
        if (!IsBackgroundSolo && !IsPeakDelta && wasOriginal != (Timeline.ViewMode == WaveformViewMode.Original)) CancelInterruptedPlayback();
        if (!IsBackgroundSolo && !IsPeakDelta && _player.IsPlaying && wasOriginal != (Timeline.ViewMode == WaveformViewMode.Original)) { var position = _player.PositionSeconds; _player.Stop(); Timeline.Playhead = position; StartPlayback(position); }
    }
    private void PlayClick(object sender, RoutedEventArgs e)
    {
        CancelPlayheadScrubbing();
        if (HasInterruptedPlayback || HasBackgroundOriginalResume || HasImportPlaybackIntent || (!_player.IsPlaying && _playbackRequested))
        { CancelInterruptedPlayback(); _resumeAfterDeviceLoss = false; return; }
        if (_player.IsPlaying) { CancelInterruptedPlayback(); _resumeAfterDeviceLoss = false; _player.Pause(); Timeline.Playhead = _player.PositionSeconds; PlayButton.Content = "▶"; return; }
        StartPlayback(Timeline.Playhead);
    }
    private PlaybackTrackState[] CapturePlaybackTracks() => IsBackgroundSolo ? CaptureBackgroundSoloTracks() : _tracks.Select(PlaybackStateFor).ToArray();

    private static PlaybackTrackState? PlaybackTrackAt(IReadOnlyList<PlaybackTrackState> tracks, double position)
    {
        double offset = 0;
        foreach (var track in tracks)
        {
            // An undecoded placeholder is a real boundary, not an empty track to skip.
            if ((track.Duration <= 0 && track.OriginalSamples == null && position >= offset) || (position >= offset && position < offset + track.Duration))
                return track;
            offset += track.Duration;
        }
        return null;
    }
    private void PublishPlaybackTracks()
    {
        bool playing = _player.IsPlaying, paused = _player.IsPaused;
        double position = playing || paused ? _player.PositionSeconds : Timeline.Playhead;
        var updated = CapturePlaybackTracks();
        _playbackTracks = updated; _player.UpdateTracks(updated);
        if (IsPlayheadScrubbing) return;
        if (HandleBackgroundSoloPublication(playing, paused, position, updated)) return;
        if (HandlePeakDeltaPublication(playing, paused, position, updated)) return;
        if (!playing && !paused)
        { TryResumeInterruptedPlayback(_revision); TryResumeBackgroundOriginal(); return; }
        var pinned = _player.CurrentTrack;
        // The render callback can already be buffering the next track. Compare its
        // actual pinned track ID, then restart from the audible clock if needed.
        var after = pinned == null ? null : updated.FirstOrDefault(track => track.Id == pinned.Id);
        bool original = OriginalRadio.IsChecked == true;
        bool monitorGainChanged = pinned != null && after != null && (original
            ? pinned.OriginalAuditionGainDb != after.OriginalAuditionGainDb
            : pinned.PreviewAuditionGainDb != after.PreviewAuditionGainDb);
        if (original)
        {
            HandleBackgroundOriginalPublication(pinned, after, position, paused, monitorGainChanged);
            return;
        }
        var audibleSamples = pinned?.PreviewSamples;
        bool previewChanged = audibleSamples != null && after?.PreviewSamples != null &&
            (!ReferenceEquals(audibleSamples, after.PreviewSamples) || pinned?.LiveResult != after.LiveResult);
        bool refreshResult = previewChanged || monitorGainChanged && after?.PreviewSamples != null;
        // Read-ahead can pin B while the audible clock is still in stale A. A
        // change to B must not discard A's resume intent during this restart.
        if (refreshResult && playing && PlaybackTrackAt(updated, position)?.PreviewSamples == null)
        {
            ArmInterruptedPlayback(position, true, false, _revision);
            _player.Stop(); if (!HasInterruptedPlayback && !HasBackgroundOriginalResume) _playbackRequested = false;
            Timeline.Playhead = position; UpdateTime(); UpdatePlayButton();
            return;
        }
        // A ready cached variant can replace the current preview immediately. Future
        // tracks are resolved when reached, so publishing their previews needs no restart.
        if (previewChanged)
        {
            _player.Stop(); Timeline.Playhead = position;
            if (playing) StartPlayback(position);
        }
        else if (monitorGainChanged && after!.PreviewSamples != null) RefreshAuditionTransport(position, paused);
    }
    private void StopInvalidatedPlayback(double? capturedPosition = null, bool allowResume = true)
    {
        if (IsPlayheadScrubbing || IsBackgroundSolo || OriginalRadio.IsChecked == true && !IsPeakDelta || !(_player.IsPlaying || _player.IsPaused)) return;
        double position = capturedPosition ?? _player.PositionSeconds;
        if (PlaybackTrackAt(_playbackTracks, position)?.PreviewSamples != null) return;
        if (allowResume && _renderCts is { IsCancellationRequested: false })
            ArmInterruptedPlayback(position, _player.IsPlaying, false, _revision);
        _player.Stop(); Timeline.Playhead = position; UpdateTime(); UpdatePlayButton();
    }
    private static List<PeakBin> BuildDisplayPeaks(WaveformPyramid pyramid, CancellationToken ct = default)
    {
        int bins = Math.Min(16000, pyramid.FrameCount);
        var peaks = new List<PeakBin>(bins);
        for (int bin = 0; bin < bins; bin++)
        {
            if ((bin & 255) == 0) ct.ThrowIfCancellationRequested();
            var peak = pyramid.Query((int)((long)bin * pyramid.FrameCount / bins), (int)((long)(bin + 1) * pyramid.FrameCount / bins));
            peaks.Add(new PeakBin(Math.Min(0, peak.Min), Math.Max(0, peak.Max)));
        }
        return peaks;
    }

    private static WaveformPyramid BuildPreviewWaveform(RenderResult render, RenderResult? previous, int channels, CancellationToken ct)
    {
        if (previous?.Waveform is { } pyramid && previous.Samples.Length == render.Samples.Length)
        {
            if (ReferenceEquals(previous.Samples, render.Samples)) return pyramid;
            if (render.Work.UsedIncrementalStage) return pyramid.Update(render.Samples, render.Work.OutputChangedStartFrame, render.Work.OutputChangedEndFrame, ct);
        }
        return WaveformPyramid.Build(render.Samples, channels, ct);
    }
    private void SeekPlayback(double position)
    {
        if (IsPlayheadScrubbing) { UpdateTime(); return; }
        CancelInterruptedPlayback(); _resumeAfterDeviceLoss = false;
        if (_player.IsPlaying)
        {
            var target = PlaybackTrackAt(_playbackTracks, position);
            bool original = OriginalRadio.IsChecked == true;
            if (target == null || !target.IsReady(original))
            {
                _player.Stop(); PlayButton.Content = "▶";
                Timeline.Playhead = position;
                StatusText.Text = target == null ? "" : _tracks.FirstOrDefault(track => track.Id == target.Id)?.WorkState == TrackWorkState.Missing ? "File not found" : original ? "Track loading" : "Track processing";
            }
            else
            {
                try { if (!_player.Seek(position)) StartPlayback(position); else _playbackRequested = true; }
                catch (Exception ex) { PlaybackFailed(ex); }
            }
        }
        UpdateTime();
    }

    private void StartPlayback(double position)
    {
        CancelInterruptedPlayback();
        if ((_tracks.Count == 0 && !IsBackgroundSolo) || _exporting) return;
        bool original = OriginalRadio.IsChecked == true;
        var states = CapturePlaybackTracks();
        _playbackTracks = states; _player.UpdateTracks(states);
        double total = states.Sum(track => track.Duration);
        position = Math.Clamp(double.IsFinite(position) ? position : 0, 0, total);
        // Natural album end replays from the start; a pending final placeholder
        // keeps its boundary so it can be resumed after decoding finishes.
        if (PlaybackTrackAt(states, position) == null && position >= total) position = 0;
        position = _player.ResolvePlayPosition(position);
        var current = PlaybackTrackAt(states, position);
        if (current == null || !current.IsReady(original))
        {
            _player.Stop(); PlayButton.Content = "▶";
            Timeline.Playhead = position; UpdateTime();
            StatusText.Text = _tracks.FirstOrDefault(track => track.Id == current?.Id)?.WorkState == TrackWorkState.Missing ? "File not found" : original ? "Track loading" : "Track processing";
            return;
        }
        if (_outputDeviceId == null && _defaultDeviceId == null)
        {
            Timeline.Playhead = position;
            _resumeAfterDeviceLoss = true; _failedDeviceId = null;
            StatusText.Text = "No audio output";
            _ = RefreshOutputDevicesAsync(); return;
        }
        try { _resumeAfterDeviceLoss = false; _player.OutputDeviceId = _outputDeviceId; _player.Play(original, position); _playbackRequested = _player.IsPlaying; UpdatePlayButton(); }
        catch (COMException ex) { PlaybackFailed(ex); }
        catch (Exception ex) { ShowError("Could not start playback", ex); }
    }
    private void StopClick(object sender, RoutedEventArgs e) { CancelPlayheadScrubbing(); CancelInterruptedPlayback(); _resumeAfterDeviceLoss = false; _player.Stop(); Timeline.Playhead = 0; UpdateTime(); PlayButton.Content = "▶"; }
    private void UpdateTime() { TimeText.Text = TimeSpan.FromSeconds(Math.Max(0, Timeline.Playhead)).ToString(@"hh\:mm\:ss\.fff"); }
    private async void RebuildClick(object sender, RoutedEventArgs e) => await ResumeProcessingAsync();
    private void FitAllClick(object sender, RoutedEventArgs e) => Timeline.FitAll();
    private void FitSelectedClick(object sender, RoutedEventArgs e) { if (Selected != null) Timeline.FitTrack(Selected); }
    private void MoveUpClick(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDownClick(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int delta)
    {
        var track = Selected; if (track == null) return;
        int from = _tracks.IndexOf(track), next = from + delta;
        if (next < 0 || next >= _tracks.Count) return;
        MoveTrack(track, delta > 0 ? next + 1 : next);
    }
    private void MoveTrack(AudioTrack track, int insertionIndex)
    {
        if (_exporting || _importing || !_tracks.Contains(track)) return;
        int from = _tracks.IndexOf(track), boundary = Math.Clamp(insertionIndex, 0, _tracks.Count);
        if (boundary == from || boundary == from + 1) return;
        bool playing = _player.IsPlaying;
        double position = playing || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        var anchor = PlaybackTrackAt(CapturePlaybackTracks(), position);
        double localTime = position;
        if (anchor != null) localTime -= _tracks.TakeWhile(item => item.Id != anchor.Id).Sum(item => PlaybackDuration(item));
        var previousOrder = _tracks.ToArray();
        PushUndo(); ArmPlaybackBeforeStructure(position, playing, _player.IsPaused); _player.Stop();
        if (!HasInterruptedPlayback && !HasBackgroundOriginalResume) _playbackRequested = false;
        if (!TrackOrdering.Move(_tracks, track, boundary)) return;
        // Move authored edits intact; only the automatic edge curves for changed
        // neighbors are recalculated from cached loudness measurements.
        UpdateTransitions(previousOrder);
        if (anchor != null) Timeline.Playhead = _tracks.TakeWhile(item => item.Id != anchor.Id).Sum(item => PlaybackDuration(item)) + localTime;
        _dirty = true; QueueRender(); RefreshList(); SelectTrack(track); UpdateTime();
        if (playing && !HasInterruptedPlayback && !HasBackgroundOriginalResume && !_player.IsPlaying) StartPlayback(Timeline.Playhead);
    }

    private AudioTrack? _fileListDragTrack;
    private Point _fileListDragOrigin;
    private TrackInsertionAdorner? _fileListDropMarker;
    private void FileListGripDown(object sender, MouseButtonEventArgs e)
    {
        if (_exporting || _importing) return;
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node != FileList)
        {
            if (node is FrameworkElement { Tag: "TrackRemoveHandle" } remove)
            {
                if ((ItemsControl.ContainerFromElement(FileList, remove) as ListBoxItem)?.Tag is AudioTrack removable)
                { _fileListDragTrack = null; e.Handled = true; RemoveTrack(removable); }
                return;
            }
            if (node is FrameworkElement { Tag: "TrackReorderHandle" } grip)
            {
                var item = ItemsControl.ContainerFromElement(FileList, grip) as ListBoxItem;
                if (item?.Tag is AudioTrack track)
                {
                    _fileListDragTrack = track; _fileListDragOrigin = e.GetPosition(FileList);
                    SelectTrack(track); e.Handled = true;
                }
                return;
            }
            node = VisualTreeHelper.GetParent(node);
        }
    }
    private void FileListGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _fileListDragTrack = null; return; }
        if (_fileListDragTrack is not { } track) return;
        var delta = e.GetPosition(FileList) - _fileListDragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _fileListDragTrack = null;
        try { DragDrop.DoDragDrop(FileList, new DataObject(TimelineView.TrackDragDataFormat, track.Id.ToString()), DragDropEffects.Move); }
        finally { ClearFileListDropMarker(); }
        e.Handled = true;
    }
    private AudioTrack? DraggedTrack(IDataObject data)
    {
        if (_exporting || _importing || !data.GetDataPresent(TimelineView.TrackDragDataFormat)) return null;
        return Guid.TryParse(data.GetData(TimelineView.TrackDragDataFormat) as string, out var id) ? _tracks.FirstOrDefault(track => track.Id == id) : null;
    }
    private (int Index, double Y) FileListDropBoundary(Point point)
    {
        if (_tracks.Count == 0) return (0, 2);
        var nearest = (Index: _tracks.Count, Y: Math.Max(2, FileList.ActualHeight - 2));
        double distance = double.PositiveInfinity;
        for (int index = 0; index < FileList.Items.Count; index++)
        {
            if (FileList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item || item.ActualHeight <= 0) continue;
            var top = item.TranslatePoint(new Point(0, 0), FileList).Y;
            foreach (var candidate in new[] { (Index: index, Y: top), (Index: index + 1, Y: top + item.ActualHeight) })
            {
                if (candidate.Y < 0 || candidate.Y > FileList.ActualHeight) continue;
                double nextDistance = Math.Abs(point.Y - candidate.Y);
                if (nextDistance < distance) { distance = nextDistance; nearest = candidate; }
            }
        }
        return nearest;
    }
    private void FileListTrackDragOver(object sender, DragEventArgs e)
    {
        var paths = FileDropPaths(e.Data);
        bool internalDrop = e.Data.GetDataPresent(TimelineView.TrackDragDataFormat, false);
        if (!internalDrop && paths == null) { ClearFileListDropMarker(); return; }
        bool accepted = internalDrop ? DraggedTrack(e.Data) != null && (e.AllowedEffects & DragDropEffects.Move) != 0
            : paths != null && CanDropAudioFiles(paths) && (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Effects = accepted ? internalDrop ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        PreviewFileListDrop(e.Data, e.GetPosition(FileList), accepted);
    }
    private int PreviewFileListDrop(IDataObject data, Point point, bool? allowed = null)
    {
        var paths = FileDropPaths(data);
        bool accepted = allowed ?? (data.GetDataPresent(TimelineView.TrackDragDataFormat, false) ? DraggedTrack(data) != null
            : paths != null && CanDropAudioFiles(paths));
        if (!accepted) { ClearFileListDropMarker(); return -1; }
        var scroll = Descendants<ScrollViewer>(FileList).FirstOrDefault();
        if (point.Y < 24) scroll?.LineUp();
        else if (point.Y > FileList.ActualHeight - 24) scroll?.LineDown();
        var boundary = FileListDropBoundary(point);
        var layer = AdornerLayer.GetAdornerLayer(FileList);
        if (_fileListDropMarker == null && layer != null) { _fileListDropMarker = new TrackInsertionAdorner(FileList); layer.Add(_fileListDropMarker); }
        if (_fileListDropMarker != null) { _fileListDropMarker.Y = boundary.Y; _fileListDropMarker.InvalidateVisual(); }
        return boundary.Index;
    }
    private void FileListTrackDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        bool internalDrop = e.Data.GetDataPresent(TimelineView.TrackDragDataFormat, false);
        bool fileDrop = e.Data.GetDataPresent(DataFormats.FileDrop, false);
        if (!internalDrop && !fileDrop) { ClearFileListDropMarker(); return; }
        bool allowed = (e.AllowedEffects & (internalDrop ? DragDropEffects.Move : DragDropEffects.Copy)) != 0;
        bool accepted = allowed && HandleFileListDrop(e.Data, e.GetPosition(FileList));
        if (!allowed) ClearFileListDropMarker();
        e.Handled = true;
        e.Effects = accepted ? internalDrop ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None;
    }
    private bool HandleFileListDrop(IDataObject data, Point point)
    {
        var boundary = FileListDropBoundary(point);
        ClearFileListDropMarker();
        if (data.GetDataPresent(TimelineView.TrackDragDataFormat, false))
        {
            var track = DraggedTrack(data);
            if (track == null) return false;
            MoveTrack(track, boundary.Index); return true;
        }
        var paths = FileDropPaths(data);
        if (paths == null || !CanDropAudioFiles(paths)) return false;
        _ = ImportDroppedFilesAsync(paths, boundary.Index);
        return true;
    }
    private void ClearFileListDropMarker()
    {
        if (_fileListDropMarker == null) return;
        AdornerLayer.GetAdornerLayer(FileList)?.Remove(_fileListDropMarker); _fileListDropMarker = null;
    }
    private sealed class TrackInsertionAdorner : Adorner
    {
        public TrackInsertionAdorner(UIElement element) : base(element) { IsHitTestVisible = false; }
        public double Y { get; set; }
        protected override void OnRender(DrawingContext dc)
        {
            var brush = (Brush)Application.Current.FindResource("AccentBrush");
            double y = Math.Clamp(Y, 2, Math.Max(2, ActualHeight - 2));
            dc.DrawLine(new Pen(brush, 3), new Point(5, y), new Point(Math.Max(5, ActualWidth - 5), y));
            dc.DrawEllipse(brush, null, new Point(5, y), 3, 3);
        }
    }
    private void RemoveClick(object sender, RoutedEventArgs e) { if (Selected != null) RemoveTrack(Selected); }
    private void RemoveTrack(AudioTrack track)
    {
        if (_exporting || _importing || !_tracks.Contains(track)) return;
        bool playing = _player.IsPlaying;
        double position = playing || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        double offset = _tracks.TakeWhile(item => item.Id != track.Id).Sum(item => PlaybackDuration(item));
        double removedDuration = PlaybackDuration(track);
        int index = _tracks.IndexOf(track);
        var previousOrder = _tracks.ToArray();
        PushUndo(); ArmPlaybackBeforeStructure(position, playing, _player.IsPaused); _player.Stop();
        if (!HasInterruptedPlayback && !HasBackgroundOriginalResume) _playbackRequested = false;
        _tracks.Remove(track);
        Timeline.SelectedTrack = null;
        Timeline.Playhead = position > offset ? Math.Max(offset, position - removedDuration) : position;
        UpdateTransitions(previousOrder); _dirty = true; QueueRender(); RefreshList();
        if (_tracks.Count > 0) SelectTrack(_tracks[Math.Min(index, _tracks.Count - 1)]);
        UpdateTime();
        if (playing && !HasInterruptedPlayback && !HasBackgroundOriginalResume && !_player.IsPlaying && Timeline.Playhead < _tracks.Sum(item => item.Duration)) StartPlayback(Timeline.Playhead);
    }
    private void ResetSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_exporting || Selected?.Analysis == null) return;
        PushUndo(); Selected.TrimDb = 0; Selected.InputGainDb = 0; Selected.TargetLufs = null; ResetProjectEnvelopeForTrack(Selected); _dirty = true; QueueRender();
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (_exportCts is { } export) { export.Cancel(); return; }
        if (_importCts is { } import)
        {
            if (_atomicSourceOperation) { import.Cancel(); return; }
            CancelInterruptedPlayback(); import.Cancel();
            _renderCts?.Cancel(); ++_revision; _renderStartedRevision = -1;
            _plannedRenderKeys = null;
            FinishSourceCancellation();
            return;
        }
        CancelProcessing();
    }

    private void PushUndo()
    {
        if (!_initialized || _syncing || _exporting) return;
        FinishStrengthGesture(false);
        var snapshot = Capture();
        RememberProcessingEdit(snapshot);
        _undo.Push(snapshot); _redo.Clear();
        if (_undo.Count > 12) { var keep = _undo.Take(12).Reverse().ToArray(); _undo.Clear(); foreach (var entry in keep) _undo.Push(entry); }
    }
    private EditSnapshot Capture() => new(_settings, _tracks.Select(t => new TrackEdit(t, t.TrimDb, t.InputGainDb, t.TargetLufs, t.Envelope.ToList(), t.TransitionEnvelope.ToList(), t.Analysis == null && t.Envelope.Count == 0, CaptureReadyPreview(t))).ToList(), _backgroundLayer, _projectEnvelope.ToList(), _projectSuggestion.ToList(), _targetComputedMode, CaptureCustomBoundaries(), CaptureExportOptions(), _floatExport, PreserveEnvelopeOnAnalysis: _projectEnvelopeLoaded, Loops: CaptureLoops());
    private void Restore(EditSnapshot snapshot)
    {
        FinishStrengthGesture(false);
        if (snapshot.LoopsOnly)
        {
            LoadLoops(snapshot.Loops);
            _dirty = true;
            return;
        }
        if (snapshot.ExportOnly)
        {
            RestoreExportState(snapshot);
            _dirty = true;
            return;
        }
        foreach (var track in _tracks) RememberActivePreview(track);
        double position = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
        ArmPlaybackBeforeStructure(position, _player.IsPlaying, _player.IsPaused);
        _player.Stop(); if (!HasInterruptedPlayback && !HasBackgroundOriginalResume) _playbackRequested = false;
        Timeline.Playhead = position; _tracks.Clear();
        foreach (var item in snapshot.Tracks)
        {
            // A failed/cancelled plain import has no remaining source worker.
            // Do not resurrect its pending placeholder through a settings undo.
            if (item.SuggestOnLoad && item.Track.Analysis == null && item.Track.WorkState == TrackWorkState.Failed) continue;
            item.Track.TrimDb = item.Trim; item.Track.InputGainDb = item.InputGain; item.Track.TargetLufs = item.TargetLufs;
            item.Track.Envelope = item.SuggestOnLoad && item.Track.Analysis != null
                ? EnvelopeDesigner.Design(item.Track.Analysis, item.Track.Duration, snapshot.Settings) : item.Envelope.ToList();
            item.Track.TransitionEnvelope = item.Transitions.ToList(); _tracks.Add(item.Track);
        }
        _settings = snapshot.Settings;
        LoadLoops(snapshot.Loops);
        RestoreExportState(snapshot);
        _targetComputedMode = snapshot.TargetComputed;
        RestoreProjectEnvelope(snapshot.ProjectEnvelope, snapshot.ProjectSuggestion,
            snapshot.Tracks.Where(item => item.SuggestOnLoad && item.Track.Analysis != null).Select(item => item.Track.Id).ToArray());
        _projectEnvelopeLoaded = snapshot.PreserveEnvelopeOnAnalysis && _tracks.Any(track => track.Analysis == null);
        _backgroundLayer = snapshot.Background; RefreshBackground();
        if (snapshot.Tracks.Any(item => item.SuggestOnLoad && item.Track.Analysis != null))
            UpdateTransitions(snapshot.Tracks.Where(item => !item.SuggestOnLoad).Select(item => item.Track).ToArray());
        RestoreReadyPreviews(snapshot);
        SyncControls(); _dirty = true; RefreshList(); QueueRender();
    }
    private void UndoClick(object sender, RoutedEventArgs e)
    {
        FinishStrengthGesture(false); if (_undo.Count == 0 || _importing || _exporting) return;
        var previous = _undo.Pop(); _redo.Push(Capture() with { ExportOnly = previous.ExportOnly, LoopsOnly = previous.LoopsOnly }); Restore(previous);
    }
    private void RedoClick(object sender, RoutedEventArgs e)
    {
        FinishStrengthGesture(false); if (_redo.Count == 0 || _importing || _exporting) return;
        var next = _redo.Pop(); _undo.Push(Capture() with { ExportOnly = next.ExportOnly, LoopsOnly = next.LoopsOnly }); Restore(next);
    }
    private void SyncControls()
    {
        FinishStrengthGesture(false);
        _syncing = true; MatchingStrengthSlider.Value = _settings.MatchingStrength; MatchingStrengthValueText.Text = $"{_settings.MatchingStrength * 100:0.#}%"; StrengthSlider.Value = _settings.Strength; StrengthValueText.Text = $"{_settings.Strength * 100:0.#}%"; ResetTargetReadout(preserveComputed: true);
        LevelingCheck.IsChecked = _settings.LevelingEnabled; MatchingCheck.IsChecked = _settings.MatchingEnabled; LimiterCheck.IsChecked = _settings.LimiterEnabled;
        SyncCeilingControl(); SyncMaximizerControls(); UpdateExportFormatChecks(); _syncing = false;
        RefreshBackground();
        RefreshTargetReadout();
        RefreshAutoProcessState();
    }
    private sealed record TrackEdit(AudioTrack Track, double Trim, double InputGain, double? TargetLufs, List<EnvelopePoint> Envelope, List<EnvelopePoint> Transitions, bool SuggestOnLoad = false, PreviewHistoryReference? Preview = null);
    private sealed record EditSnapshot(ProcessingSettings Settings, List<TrackEdit> Tracks, BackgroundLayer Background,
        List<EnvelopePoint> ProjectEnvelope, List<EnvelopePoint> ProjectSuggestion, bool TargetComputed,
        List<double> CustomTrackBoundaries, ProjectExportOptions ExportOptions, bool FloatExport, bool ExportOnly = false,
        bool PreserveEnvelopeOnAnalysis = false, PlaybackLoopState? Loops = null, bool LoopsOnly = false);
    private static List<EnvelopePoint> ValidateEnvelope(IEnumerable<EnvelopePoint> points, double duration)
    {
        var valid = points.Where(p => double.IsFinite(p.Time) && double.IsFinite(p.GainDb)).Select(p => new EnvelopePoint(Math.Clamp(p.Time, 0, duration), Math.Clamp(p.GainDb, -24, 24))).GroupBy(p => p.Time).Select(g => g.Last()).OrderBy(p => p.Time).ToList();
        if (valid.Count == 0) return [new(0, 0), new(duration, 0)];
        if (valid[0].Time > 0) valid.Insert(0, new(0, valid[0].GainDb));
        if (valid[^1].Time < duration) valid.Add(new(duration, valid[^1].GainDb));
        return valid;
    }
    private static bool IsProjectPath(string path) => Path.GetExtension(path).Equals(".loudline", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".multiloud", StringComparison.OrdinalIgnoreCase);
    private void SaveProjectClick(object sender, RoutedEventArgs e) => SaveProject(false);
    private void SaveProjectAsClick(object sender, RoutedEventArgs e) => SaveProject(true);
    private bool SaveProject(bool saveAs)
    {
        FinishStrengthGesture(false);
        if (_importing || _exporting) { StatusText.Text = "Wait for the current operation before saving."; return false; }
        string? path = _projectPath;
        if (saveAs || path == null || _backgroundProjectNeedsCopy)
        {
            var suggestedName = path == null ? "Release.loudline" : _backgroundProjectNeedsCopy
                ? Path.GetFileNameWithoutExtension(path) + " - Background.loudline" : Path.ChangeExtension(Path.GetFileName(path), ".loudline");
            var dialog = new SaveFileDialog { Title = _backgroundProjectNeedsCopy ? "Save two-track project copy" : "Save Loudline project",
                Filter = "Loudline project|*.loudline", DefaultExt = ".loudline", FileName = suggestedName };
            if (dialog.ShowDialog(this) != true) return false; path = dialog.FileName;
        }
        try
        {
            path = Path.GetFullPath(path);
            if (_tracks.Concat(_backgroundLayer.Clips.Select(clip => clip.Source)).Any(t => string.Equals(Path.GetFullPath(t.FilePath), path, StringComparison.OrdinalIgnoreCase))) throw new IOException("A project cannot replace an imported audio file.");
            if (!IsProjectPath(path)) throw new IOException("Choose a filename ending in .loudline for the project.");
            var project = CreateProjectData(path);
            var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, json); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
            _projectPath = path; _backgroundProjectNeedsCopy = false; _dirty = false; Title = $"{Path.GetFileNameWithoutExtension(path)} — Loudline {AppVersion.Display}"; StatusText.Text = "Project saved"; return true;
        }
        catch (Exception ex) { ShowError("Could not save project", ex); return false; }
    }
    private bool ConfirmSave()
    {
        FinishStrengthGesture(false);
        if (!_dirty || App.SmokeMode) return true;
        var dialog = new UnsavedProjectDialog { Owner = this };
        dialog.ShowDialog();
        return ResolveSaveConfirmation(dialog.Choice, () => SaveProject(false));
    }
    private async void OpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (_importing || _exporting) return;
        FinishStrengthGesture(false);
        var dialog = new OpenFileDialog { Filter = "Loudline projects|*.loudline;*.multiloud" };
        if (dialog.ShowDialog(this) != true || !ConfirmSave()) return;
        try { await OpenProjectAsync(dialog.FileName); }
        catch (Exception ex) { ShowError("Could not open project", ex); }
    }
    private static void ValidateSettings(ProcessingSettings s)
    {
        if (!double.IsFinite(s.BassProtection) || s.BassProtection is < 0 or > 1 || !double.IsFinite(s.MaxSustainedReductionDb) || s.MaxSustainedReductionDb is < 0 or > 12 || !double.IsFinite(s.BassSensitivity) || s.BassSensitivity is < .25 or > 1 || !Enum.IsDefined(s.MaximizerStyle) || !double.IsFinite(s.MaximizerCharacter) || s.MaximizerCharacter is < 0 or > 1 || !double.IsFinite(s.TransientPreservation) || s.TransientPreservation is < 0 or > 1 || !double.IsFinite(s.TargetLufs) || s.TargetLufs is < -36 or > -6 || !double.IsFinite(s.Strength) || s.Strength is < 0 or > 1 || !double.IsFinite(s.MatchingStrength) || s.MatchingStrength is < 0 or > 1 || !double.IsFinite(s.MaxLevelingDb) || s.MaxLevelingDb is < 0 or > 12 || !double.IsFinite(s.TransitionSeconds) || s.TransitionSeconds is < 2 or > 30 || !double.IsFinite(s.CeilingDb) || s.CeilingDb is < -6 or > -.1) throw new InvalidDataException("Project contains unsupported processing values.");
    }
    private void SystemThemeClick(object sender, RoutedEventArgs e) => ChangeTheme("System");
    private void DarkThemeClick(object sender, RoutedEventArgs e) => ChangeTheme("Dark");
    private void LightThemeClick(object sender, RoutedEventArgs e) => ChangeTheme("Light");
    private void ChangeTheme(string mode) { ThemeService.Apply(mode); UpdateThemeChecks(); RefreshList(); BackgroundLane.Refresh(); StrengthSlider.InvalidateVisual(); }
    private void UpdateThemeChecks() { SystemThemeItem.IsChecked = ThemeService.Mode == "System"; DarkThemeItem.IsChecked = ThemeService.Mode == "Dark"; LightThemeItem.IsChecked = ThemeService.Mode == "Light"; }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _closingProject) return;
        var focused = Keyboard.FocusedElement;
        var modifiers = Keyboard.Modifiers;
        if (TryHandleWorkspaceShortcut(e.Key, modifiers, e.IsRepeat, focused)) { e.Handled = true; return; }
        if (IsShortcutEditingContext(focused) || _settingsFlyout is { IsOpen: true } ||
            (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
        if (focused is Slider && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) return;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0, shift = (modifiers & ModifierKeys.Shift) != 0;
        bool known = ctrl && (e.Key is Key.O or Key.S or Key.E or Key.Z or Key.Y) ||
            modifiers == ModifierKeys.None && (e.Key is Key.Space or Key.Delete or Key.Home);
        if (!known) return;
        if (e.IsRepeat) { e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { if (shift) OpenProjectClick(this, e); else AddFilesClick(this, e); }
        else if (ctrl && e.Key == Key.S) SaveProjectClick(this, e);
        else if (ctrl && e.Key == Key.E) ExportClick(this, e);
        else if (ctrl && e.Key == Key.Z) UndoClick(this, e);
        else if (ctrl && e.Key == Key.Y) RedoClick(this, e);
        else if (e.Key == Key.Space) PlayClick(this, e);
        else if (e.Key == Key.Delete) RemoveClick(this, e);
        else if (e.Key == Key.Home) FitAllClick(this, e);
        e.Handled = true;
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        CloseSettingsFlyout();
        if (!ConfirmSave()) { e.Cancel = true; return; }
        CancelPlayheadScrubbing();
        DetachHorizontalScrolling();
        CancelInterruptedPlayback();
        _closed = true; _timer.Stop(); _deviceTimer.Stop(); _renderCts?.Cancel(); _importCts?.Cancel(); _exportCts?.Cancel();
        SystemEvents.UserPreferenceChanged -= WindowsPreferenceChanged; BackgroundLane.Dispose(); _player.Dispose();
    }

    private async Task WaitForSmokePreviewAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_renderedRevision != _revision && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (_renderedRevision != _revision || _tracks.Any(track => track.WorkState != TrackWorkState.Idle))
            throw new InvalidOperationException($"Smoke test: preview did not finish. Revision {_renderedRevision}/{_revision}; " +
                $"importing={_importing}, paused={_processingPaused}, worker={_renderCts != null}, status={StatusText.Text}; " +
                string.Join("; ", _tracks.Select(track => $"{track.Name}: {track.WorkState}, {track.Status}, " +
                    $"analysis={track.Analysis != null}, frames={track.OriginalFrameCount}, current={PreviewCurrent(track, _settings)}")));
    }
    private async Task VerifyTrackSettingsSmokeAsync()
    {
        if (!IsProjectPath("Release.loudline") || !IsProjectPath("Legacy.multiloud") || IsProjectPath("Source.wav"))
            throw new InvalidOperationException("Smoke test: project extension migration changed source-file protection.");
        var legacy = ProjectFileService.DeserializeProject("{\"Version\":1,\"Settings\":{},\"Tracks\":[{\"Path\":\"legacy.wav\"}]}");
        if (legacy.Settings.MatchingStrength != 1 || legacy.Settings.BassSensitivity != 1 || legacy.Tracks[0].InputGainDb != 0 || legacy.Tracks[0].TargetLufs != null)
            throw new InvalidOperationException("Smoke test: legacy project defaults changed.");
        var persistent = new ProjectData
        {
            Settings = _settings with { MatchingStrength = .35, BassSensitivity = .4 },
            Tracks = [new ProjectTrack { Path = "roundtrip.wav", InputGainDb = 4.5, TargetLufs = -11,
                Envelope = [new(0, -1), new(24, 1)], TransitionEnvelope = [new(0, 1), new(8, 0), new(24, 0)] }]
        };
        var roundtrip = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(persistent));
        if (roundtrip.Version != 9 || roundtrip.Settings.MatchingStrength != .35 || roundtrip.Settings.BassSensitivity != .4 || roundtrip.Tracks[0].InputGainDb != 4.5 ||
            roundtrip.Tracks[0].TargetLufs != -11 || !roundtrip.Tracks[0].TransitionEnvelope!.SequenceEqual(persistent.Tracks[0].TransitionEnvelope!))
            throw new InvalidOperationException("Smoke test: track settings or edge curves did not survive project round-trip.");
        var first = _tracks[0]; var analysis = first.Analysis;
        var originalTransitions = first.TransitionEnvelope.ToArray();
        PushUndo(); first.InputGainDb = 3; first.TargetLufs = -11; MatchingStrengthSlider.Value = .35;
        await WaitForSmokePreviewAsync();
        var overridden = first.Render;
        int beforeGlobalTarget = _renderExecutionCount;
        TargetBox.Text = "-15"; CommitTarget(); await WaitForSmokePreviewAsync();
        if (!ReferenceEquals(first.Render, overridden) || _renderExecutionCount != beforeGlobalTarget + 2 || !ReferenceEquals(first.Analysis, analysis))
            throw new InvalidOperationException("Smoke test: a global target change rebuilt or changed the overridden track.");
        UndoClick(this, new RoutedEventArgs()); // Global target only.
        UndoClick(this, new RoutedEventArgs()); // Input, override, and matching blend.
        await WaitForSmokePreviewAsync();
        if (first.InputGainDb != 0 || first.TargetLufs != null || _settings.MatchingStrength != 1 || !first.TransitionEnvelope.SequenceEqual(originalTransitions))
            throw new InvalidOperationException("Smoke test: Undo did not restore exact per-track settings and transitions.");
        RedoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (first.InputGainDb != 3 || first.TargetLufs != -11 || _settings.MatchingStrength != .35)
            throw new InvalidOperationException("Smoke test: Redo did not restore per-track settings.");
        UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        var beforeRemoveOrder = _tracks.ToArray();
        var projectBeforeRemove = _projectEnvelope.ToArray();
        var authored = _tracks.Select(track => track.Envelope.ToArray()).ToArray();
        var edges = _tracks.Select(track => track.TransitionEnvelope.ToArray()).ToArray();
        Timeline.FitAll();
        double secondMidpoint = _tracks[0].Duration + _tracks[1].Duration / 2;
        Timeline.SetHoveredTrack(new Point(12 + (Timeline.ActualWidth - 24) * secondMidpoint / _tracks.Sum(track => track.Duration), 170));
        await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
        var inputBox = Descendants<TextBox>(Timeline).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Track input gain in decibels");
        foreach (var mode in new[] { "Light", "Dark" })
        {
            ThemeService.Apply(mode); UpdateThemeChecks(); Timeline.Refresh();
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
            if (inputBox.Background is not SolidColorBrush actualBackground || inputBox.Foreground is not SolidColorBrush actualForeground ||
                actualBackground.Color != ((SolidColorBrush)FindResource("BackgroundBrush")).Color || actualForeground.Color != ((SolidColorBrush)FindResource("TextBrush")).Color)
                throw new InvalidOperationException($"Smoke test: hover editor did not follow the {mode} theme.");
        }
        var removeButton = Descendants<Button>(Timeline).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove track");
        removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForSmokePreviewAsync();
        if (_tracks.Count != 2 || !ReferenceEquals(_tracks[0].Analysis, analysis) || !ReferenceEquals(_tracks[0], beforeRemoveOrder[0]) || !ReferenceEquals(_tracks[1], beforeRemoveOrder[2]))
            throw new InvalidOperationException("Smoke test: removing a track discarded surviving analysis or ordering.");
        UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (!_tracks.SequenceEqual(beforeRemoveOrder) || !_projectEnvelope.SequenceEqual(projectBeforeRemove) || _tracks.Where((track, index) => !track.Envelope.SequenceEqual(authored[index]) || !track.TransitionEnvelope.SequenceEqual(edges[index])).Any())
            throw new InvalidOperationException("Smoke test: removing and restoring a track changed authored or derived envelope values.");
        int beforeNoOpEdits = _renderExecutionCount;
        Timeline.SetTrackInputGain(first, 1.25); Timeline.SetTrackTarget(first, -12);
        if (first.InputGainDb != 1.25 || first.TargetLufs != -12) throw new InvalidOperationException("Smoke test: hover editor changes did not reach the track.");
        Timeline.SetTrackInputGain(first, 0); Timeline.SetTrackTarget(first, null);
        await Task.Delay(650);
        if (_renderExecutionCount != beforeNoOpEdits || _renderedRevision != _revision)
            throw new InvalidOperationException("Smoke test: quickly reverted hover edits rebuilt unchanged audio.");
        var busyTrack = _tracks[^1];
        Timeline.SetTrackInputGain(busyTrack, .5);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (busyTrack.WorkState != TrackWorkState.Processing && DateTime.UtcNow < deadline) await Task.Delay(5);
        if (busyTrack.WorkState != TrackWorkState.Processing) throw new InvalidOperationException("Smoke test: removal regression did not reach active processing.");
        RemoveTrack(busyTrack); await WaitForSmokePreviewAsync();
        if (_tracks.Count != 2 || !ExportButton.IsEnabled) throw new InvalidOperationException("Smoke test: removing the active render left ready tracks stuck pending.");
        UndoClick(this, new RoutedEventArgs()); UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (_tracks.Count != 3 || busyTrack.InputGainDb != 0) throw new InvalidOperationException("Smoke test: active-render removal Undo lost the source or input control.");
    }

    private async Task VerifyTimelineNavigationSmokeAsync()
    {
        Timeline.FitAll();
        double total = _tracks.Sum(track => track.Duration), width = Timeline.ActualWidth - 24;
        if (!Timeline.HandleNavigationPointerDown(new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * .25, 75), 1) || Math.Abs(Timeline.Playhead - total * .25) > .001)
            throw new InvalidOperationException("Smoke test: ruler click did not seek.");
        Timeline.HandleNavigationPointerMove(new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * .4, 75));
        Timeline.CompletePointerInteraction();
        if (Math.Abs(Timeline.Playhead - total * .4) > .001) throw new InvalidOperationException("Smoke test: ruler playhead drag did not seek.");
        double secondMidpoint = _tracks[0].Duration + _tracks[1].Duration / 2;
        if (!Timeline.HandleNavigationPointerDown(new Point(12 + width * secondMidpoint / total, 30), 2) ||
            Math.Abs(Timeline.ViewStartSeconds - _tracks[0].Duration) > .001 || Math.Abs(Timeline.VisibleDurationSeconds - _tracks[1].Duration) > .001)
            throw new InvalidOperationException("Smoke test: overview double-click did not fit the clicked track.");
        Timeline.CompletePointerInteraction();
        Timeline.FitAll();
        Timeline.HandleNavigationWheel(new Point(12 + width * .5, 200), 120, false);
        if (!Timeline.IsNavigationAnimating || Math.Abs(Timeline.VisibleDurationSeconds - total) > .001)
            throw new InvalidOperationException("Smoke test: wheel zoom jumped instead of animating.");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Timeline.IsNavigationAnimating && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (Timeline.IsNavigationAnimating || Timeline.VisibleDurationSeconds >= total || Timeline.ViewStartSeconds <= 0)
            throw new InvalidOperationException("Smoke test: animated zoom did not settle around its anchor.");
        double beforePan = Timeline.ViewStartSeconds;
        Timeline.HandleNavigationWheel(new Point(12 + width * .5, 200), -120, true);
        if (!Timeline.IsNavigationAnimating || Math.Abs(Timeline.ViewStartSeconds - beforePan) > .001)
            throw new InvalidOperationException("Smoke test: wheel pan jumped instead of animating.");
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (Timeline.IsNavigationAnimating && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (Timeline.IsNavigationAnimating || Timeline.ViewStartSeconds <= beforePan)
            throw new InvalidOperationException("Smoke test: animated pan did not settle in the requested direction.");
        if (Timeline.ToolTip != null || Descendants<FrameworkElement>(this).Any(element => element.ToolTip != null &&
            (!ReferenceEquals(element, FolderProjectButton) || !Equals(element.ToolTip, "Open Loudline project...")) &&
            (!ReferenceEquals(element, DeltaCheck) || !Equals(element.ToolTip, "Delta")) && !IsResponsiveButtonTooltip(element)))
            throw new InvalidOperationException("Smoke test: a removed hover tooltip remained in the editor.");
        Timeline.FitTrack(_tracks[0]);
        Timeline.Playhead = total - _tracks[^1].Duration / 2;
        await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
        Timeline.FlushPendingRendering();
        var bitmap = new RenderTargetBitmap((int)Timeline.ActualWidth, (int)Timeline.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var snapshot = new DrawingVisual();
        using (var drawing = snapshot.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(Timeline), null, new Rect(0, 0, Timeline.ActualWidth, Timeline.ActualHeight));
        bitmap.Render(snapshot);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        int expectedX = (int)Math.Round(12 + Timeline.Playhead / total * width), goldPixels = 0;
        for (int y = 12; y < 56; y++)
            for (int x = Math.Max(0, expectedX - 2); x <= Math.Min(bitmap.PixelWidth - 1, expectedX + 2); x++)
            {
                int offset = y * stride + x * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (r > 180 && g > 100 && r > g + 25 && g > b + 35) goldPixels++;
            }
        if (Timeline.Playhead <= Timeline.ViewStartSeconds + Timeline.VisibleDurationSeconds || goldPixels < 12)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var diagnostic = File.Create(Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "Loudline-overview-check.png"));
            encoder.Save(diagnostic);
            throw new InvalidOperationException($"Smoke test: overview marker pixels={goldPixels}, x={expectedX}, width={bitmap.PixelWidth}, playhead={Timeline.Playhead}, viewport={Timeline.ViewStartSeconds}+{Timeline.VisibleDurationSeconds}.");
        }
        double playhead = Timeline.Playhead;
        Timeline.HandleNavigationPointerDown(new Point(expectedX, 30), 1);
        Timeline.HandleNavigationPointerMove(new Point(expectedX - 25, 30));
        Timeline.CompletePointerInteraction();
        if (Timeline.Playhead != playhead) throw new InvalidOperationException("Smoke test: passive overview marker changed playback position.");
        Timeline.FitAll(); Timeline.Playhead = 0; UpdateTime();
    }
    private async Task VerifyWaveformEditingSmokeAsync(string? screenshot)
    {
        var track = _tracks[0];
        var render = track.Render; var analysis = track.Analysis;
        var points = track.Envelope.ToArray();
        var projectPoints = _projectEnvelope.ToArray();
        var originalKeys = _tracks.ToDictionary(item => item.Id, item => RenderKey(item, _settings));
        if (points.Length < 2) throw new InvalidOperationException("Smoke test: no envelope to edit.");
        string key = RenderKey(track, _settings);
        int before = _renderExecutionCount;
        double time = points[0].Time + (points[1].Time - points[0].Time) * .371;
        int index = Timeline.InsertEnvelopePointOnCurve(track, time);
        Timeline.CompletePointerInteraction();
        await Task.Delay(650);
        if (index < 0 || _projectEnvelope.Count != projectPoints.Length + 1 || !track.Envelope.SequenceEqual(points) || RenderKey(track, _settings) != key ||
            !ReferenceEquals(track.Render, render) || !ReferenceEquals(track.Analysis, analysis) || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: inserting a point on the line changed or reprocessed the audio.");
        UndoClick(this, new RoutedEventArgs());
        await WaitForSmokePreviewAsync();
        if (!_projectEnvelope.SequenceEqual(projectPoints) || !track.Envelope.SequenceEqual(points) || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: point-insertion Undo rebuilt unchanged audio.");
        Timeline.FitTrack(track);
        index = Timeline.InsertEnvelopePointOnCurve(track, time);
        var position = Timeline.EnvelopePointPosition(track, index);
        if (!Timeline.HandleWaveformPointerMove(new Point(position.X, position.Y - 18)) || Math.Abs(_projectEnvelope[index].GainDb - AudioProcessor.EnvelopeGainAt(projectPoints, time)) < .01)
            throw new InvalidOperationException("Smoke test: a newly inserted point could not be dragged in the same gesture.");
        await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
        if (screenshot != null) CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-envelope-point.png"));
        var affectedTracks = _tracks.Where(item => RenderKey(item, _settings) != originalKeys[item.Id]).ToArray();
        Timeline.CompletePointerInteraction(); await WaitForSmokePreviewAsync();
        if (!affectedTracks.Contains(track) || _renderExecutionCount != before + affectedTracks.Length || track.Render?.Work.UsedIncrementalStage != true)
            throw new InvalidOperationException("Smoke test: a project point drag did not incrementally render exactly the files inside its neighboring-point interval.");
        UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (!_projectEnvelope.SequenceEqual(projectPoints) || !track.Envelope.SequenceEqual(points) || !ReferenceEquals(track.Render, render))
            throw new InvalidOperationException("Smoke test: insert-and-drag needed more than one Undo or lost its cached original preview.");
        before = _renderExecutionCount;
        foreach (bool fine in new[] { false, true })
        {
            double input = track.InputGainDb;
            Timeline.BeginTrackValueDrag(track, false, 200, fine);
            Timeline.UpdateTrackValueDrag(189, fine);
            if (track.InputGainDb != input) throw new InvalidOperationException("Smoke test: input drag changed before a full 12-pixel step.");
            Timeline.UpdateTrackValueDrag(188, fine);
            if (Math.Abs(track.InputGainDb - input - (fine ? .1 : 1)) > 1e-8 || _renderExecutionCount != before)
                throw new InvalidOperationException("Smoke test: input drag precision or deferred rendering changed.");
            Timeline.EndNativeValueDrag(); await WaitForSmokePreviewAsync();
            UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
            if (track.InputGainDb != input || !ReferenceEquals(track.Render, render)) throw new InvalidOperationException("Smoke test: input drag Undo did not restore cached audio.");
            before = _renderExecutionCount;
        }
        double target = track.TargetLufs ?? _settings.TargetLufs;
        Timeline.BeginTrackValueDrag(track, true, 200, false); Timeline.UpdateTrackValueDrag(176, false);
        if (track.TargetLufs != target + 2) throw new InvalidOperationException("Smoke test: target drag did not use one-LUFS steps.");
        Timeline.EndNativeValueDrag(); await WaitForSmokePreviewAsync();
        UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (track.TargetLufs != null || !ReferenceEquals(track.Render, render)) throw new InvalidOperationException("Smoke test: target drag Undo lost global inheritance.");
        before = _renderExecutionCount;
        Timeline.SetHoveredTrack(new Point(200, 170), force: true);
        await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
        Timeline.BeginNativeTextEdit(false);
        var inputBox = Descendants<TextBox>(Timeline).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Track input gain in decibels");
        inputBox.Text = "3";
        Timeline.BeginNativeTextEdit(true);
        if (track.InputGainDb != 3) throw new InvalidOperationException("Smoke test: switching numeric editors discarded the typed input gain.");
        var targetBox = Descendants<TextBox>(Timeline).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Track target loudness in LUFS");
        targetBox.Text = "-13";
        Timeline.BeginTrackValueDrag(track, false, 200, false);
        if (track.TargetLufs != -13) throw new InvalidOperationException("Smoke test: starting a numeric drag discarded the other typed value.");
        Timeline.EndNativeValueDrag();
        UndoClick(this, new RoutedEventArgs()); UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (track.InputGainDb != 0 || track.TargetLufs != null || !ReferenceEquals(track.Render, render) || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: switching numeric editors did not preserve reversible cached edits.");
        int middleNode = _projectEnvelope.FindIndex(point => point.Time > 0 && point.Time < track.Duration);
        if (middleNode < 0) middleNode = 0; // A project endpoint is still editable vertically.
        var nodeOrigin = Timeline.EnvelopePointPosition(track, middleNode);
        Timeline.HandleWaveformPointerDown(track, nodeOrigin, 1);
        Timeline.HandleWaveformPointerMove(new Point(nodeOrigin.X + 12, nodeOrigin.Y - 18));
        Timeline.HandleWaveformPointerMove(nodeOrigin);
        Timeline.CompletePointerInteraction(); await Task.Delay(650);
        if (!track.Envelope.SequenceEqual(points) || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: returning an envelope drag to its origin left an edit or reprocessed audio.");
        WaveformEnvelopeCheck.IsChecked = false;
        var gainOrigin = new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * .55, Timeline.GainLineY(track));
        Timeline.HandleWaveformPointerDown(track, gainOrigin, 1);
        Timeline.HandleWaveformPointerMove(new Point(gainOrigin.X, gainOrigin.Y - 18));
        Timeline.HandleWaveformPointerMove(gainOrigin);
        Timeline.CompletePointerInteraction(); await Task.Delay(650);
        WaveformEnvelopeCheck.IsChecked = true;
        if (track.TrimDb != 0 || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: returning a gain drag to its origin left an edit or reprocessed audio.");
        TargetBox.Focus(); TargetBox.Text = "-15";
        Timeline.BeginTrackValueDrag(track, true, 200, false); Timeline.UpdateTrackValueDrag(188, false);
        if (_settings.TargetLufs != -15 || track.TargetLufs != -14)
            throw new InvalidOperationException("Smoke test: starting a track drag did not commit the typed global target first.");
        Timeline.EndNativeValueDrag();
        UndoClick(this, new RoutedEventArgs()); UndoClick(this, new RoutedEventArgs()); await WaitForSmokePreviewAsync();
        if (_settings.TargetLufs != -14 || track.TargetLufs != null || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: committed global and track drags did not undo without unnecessary rendering.");
        Timeline.Settings = _settings with { TargetLufs = -14.123 };
        Timeline.BeginTrackValueDrag(track, true, 200, false);
        Timeline.UpdateTrackValueDrag(189, false);
        if (track.TargetLufs != null) throw new InvalidOperationException("Smoke test: a sub-step target drag changed a precise inherited target.");
        Timeline.UpdateTrackValueDrag(176, false);
        if (Math.Abs((track.TargetLufs ?? 0) + 12.123) > 1e-8) throw new InvalidOperationException("Smoke test: target dragging lost the inherited fractional value.");
        Timeline.UpdateTrackValueDrag(200, false); Timeline.EndNativeValueDrag();
        Timeline.Settings = _settings;
        await Task.Delay(650);
        if (track.TargetLufs != null || _renderExecutionCount != before || !ReferenceEquals(track.Render, render))
            throw new InvalidOperationException("Smoke test: a reverted target drag lost inheritance or reprocessed unchanged audio.");
        var strength = MatchingStrengthSlider.Value;
        MatchingStrengthSlider.Value = .47;
        double expected = Math.Clamp((track.TargetLufs ?? _settings.TargetLufs) - track.Render!.PreMatchLufs, -36, 36) * .47;
        if (Math.Abs(Timeline.EstimatedMatchGain(track) - expected) > 1e-8 || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: matching waveform estimate waited for rendering or used the old strength.");
        MatchingStrengthSlider.Value = strength;
        await Task.Delay(650);
        if (_renderExecutionCount != before || !ReferenceEquals(track.Render, render))
            throw new InvalidOperationException("Smoke test: reverted live strength estimate rebuilt unchanged audio.");
        ShowGains.IsChecked = false;
        if (Timeline.ShowGainLines) throw new InvalidOperationException("Smoke test: Gains toggle did not hide gain lines.");
        ShowGains.IsChecked = true;
        if (!Timeline.ShowGainLines || _renderExecutionCount != before)
            throw new InvalidOperationException("Smoke test: Gains display toggle changed the audio.");
        Timeline.FitAll();
        double second = _tracks[0].Duration + _tracks[1].Duration / 2;
        double releaseDuration = _tracks.Sum(item => item.Duration);
        var filenamePoint = new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * second / releaseDuration, Timeline.PlotArea.Top + 10);
        if (!Timeline.HandleNavigationPointerDown(filenamePoint, 1)) Timeline.HandleWaveformPointerDown(_tracks[1], filenamePoint, 1);
        if (Math.Abs(Timeline.Playhead - second) > .001 || Math.Abs(Timeline.ViewStartSeconds) > .001 ||
            Math.Abs(Timeline.VisibleDurationSeconds - releaseDuration) > .001 || !ReferenceEquals(Timeline.SelectedTrack, _tracks[1]))
            throw new InvalidOperationException("Smoke test: clicking the filename did not seek without changing zoom.");
        Timeline.CompletePointerInteraction();
        if (!Timeline.HandleNavigationPointerDown(filenamePoint, 2)) Timeline.HandleWaveformPointerDown(_tracks[1], filenamePoint, 2);
        if (Math.Abs(Timeline.ViewStartSeconds - _tracks[0].Duration) > .001 || Math.Abs(Timeline.VisibleDurationSeconds - _tracks[1].Duration) > .001)
            throw new InvalidOperationException("Smoke test: double-clicking the filename no longer fits its track.");
        Timeline.CompletePointerInteraction(); Timeline.FitAll();
    }

    private async Task VerifyResponsiveLayoutSmokeAsync(string? screenshot)
    {
        // A detached native host gives CI a genuine 4K layout without inheriting
        // the smaller desktop window's layout clip or stretching a clipped image.
        var content = (FrameworkElement)Content;
        double originalWidth = content.Width, originalHeight = content.Height;
        Content = null;
        var host = new Border { Child = content, Background = (Brush)FindResource("BackgroundBrush") };
        NameScope.SetNameScope(host, NameScope.GetNameScope(this));
        TextElement.SetFontFamily(host, FontFamily); TextElement.SetFontSize(host, FontSize);
        content.Width = double.NaN; content.Height = double.NaN;
        void Layout(double width, double height)
        {
            host.Width = width; host.Height = height;
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height));
            UpdateResponsiveLayout();
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
        }
        try
        {
            await VerifyResponsiveToolbarThresholdsSmokeAsync(Layout, host, screenshot);
            Layout(3840, 2160);
            if (!_wideLayout || WideProcessingHost.Content != ProcessingPanel || WideViewHost.Content != ViewControls || WideOutputHost.Content != OutputControls ||
                CompactProcessingBar.Visibility != Visibility.Collapsed || CompactViewBar.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Smoke test: large window did not consolidate the toolbar.");
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(WaveformPane).TransformBounds(new Rect(element.RenderSize));
            var view = Bounds(ViewControls); var processing = Bounds(ProcessingPanel); var output = Bounds(OutputControls);
            if (view.Right > processing.Left + 1 || processing.Right > output.Left + 1 || view.Left < -1 || output.Right > WaveformPane.ActualWidth + 1)
                throw new InvalidOperationException("Smoke test: wide toolbar controls overlap or extend beyond the editor.");
            if (Math.Abs(FilesPanel.TranslatePoint(new Point(), content).Y - WideToolbar.TranslatePoint(new Point(), content).Y) > 1)
                throw new InvalidOperationException("Smoke test: file list did not extend to the menu in the wide layout.");
            Timeline.FitTrack(_tracks[0]); Timeline.SetHoveredTrack(new Point(200, 170), force: true);
            Layout(3840, 2160);
            VerifyHoverAppearanceSmoke();
            VerifyToolbarButtonHeightsSmoke();
            await VerifyRetainedRenderingSmokeAsync(screenshot);
            if (screenshot != null)
            {
                var image = CaptureVisual(host, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-wide.png"));
                var rightPixel = new byte[4]; image.CopyPixels(new Int32Rect(image.PixelWidth - 24, 100, 1, 1), rightPixel, 4, 0);
                if (rightPixel[0] + rightPixel[1] + rightPixel[2] == 0) throw new InvalidOperationException("Smoke test: 4K layout screenshot was clipped by the CI desktop window.");
            }
            Layout(1100, 740);
            if (_wideLayout || CompactProcessingHost.Content != ProcessingPanel || CompactViewHost.Content != ViewControls || CompactOutputHost.Content != OutputControls || WideToolbar.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Smoke test: small window did not restore the compact toolbar.");
            VerifyToolbarButtonHeightsSmoke();
            if (screenshot != null) CaptureVisual(host, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-compact.png"));
        }
        finally
        {
            host.Child = null; content.Width = originalWidth; content.Height = originalHeight; Content = content;
            Timeline.SetHoveredTrack(new Point(), force: true); Timeline.FitAll();
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
            UpdateResponsiveLayout();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private async Task CaptureMenuSmokeAsync(string screenshot)
    {
        string ImagePath(string suffix) => Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + suffix + ".png");
        var menu = Descendants<Menu>(this).First();
        var file = menu.Items.OfType<MenuItem>().First();
        file.IsSubmenuOpen = true;
        await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render); await Task.Delay(100);
        var popup = file.Template.FindName("PART_Popup", file) as System.Windows.Controls.Primitives.Popup;
        if (popup?.Child is not FrameworkElement panel || !popup.IsOpen) throw new InvalidOperationException("Smoke test: File menu did not open.");
        CaptureVisual(panel, ImagePath("-menu")); file.IsSubmenuOpen = false;
        var view = menu.Items.OfType<MenuItem>().First(item => item.Header?.ToString() == "_View");
        view.IsSubmenuOpen = true; await Task.Delay(100);
        var theme = view.Items.OfType<MenuItem>().First(item => item.Header?.ToString() == "Theme");
        theme.IsSubmenuOpen = true; await Task.Delay(100);
        var themePopup = theme.Template.FindName("PART_Popup", theme) as System.Windows.Controls.Primitives.Popup;
        if (themePopup?.Child is not FrameworkElement themePanel || !themePopup.IsOpen || !DarkThemeItem.IsChecked) throw new InvalidOperationException("Smoke test: theme submenu did not preserve checkmarks.");
        CaptureVisual(themePanel, ImagePath("-submenu")); theme.IsSubmenuOpen = false; view.IsSubmenuOpen = false;
        var presets = Descendants<Button>(this).First(button => button.Content?.ToString()?.StartsWith("Presets") == true);
        PresetsClick(presets, new RoutedEventArgs()); await Task.Delay(100);
        if (presets.ContextMenu is not { IsOpen: true } context) throw new InvalidOperationException("Smoke test: presets menu did not open.");
        CaptureVisual(context, ImagePath("-contextmenu")); context.IsOpen = false;
    }
    private static RenderTargetBitmap CaptureVisual(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        foreach (var timeline in Descendants<TimelineView>(visual)) timeline.FlushPendingRendering();
        if (visual is TimelineView self) self.FlushPendingRendering();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)), Math.Max(1, (int)Math.Ceiling(visual.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var snapshot = new DrawingVisual();
        using (var drawing = snapshot.RenderOpen()) drawing.DrawRectangle(new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(visual.RenderSize), Stretch = Stretch.Fill }, null, new Rect(visual.RenderSize));
        bitmap.Render(snapshot); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream); return bitmap;
    }

    private void CaptureScreenshot(string path)
    {
        Timeline.FlushPendingRendering();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    public async Task RunSmokeTestAsync(string? screenshot)
    {
        ThemeService.Apply("Dark"); StrengthSlider.InvalidateVisual(); Timeline.Refresh(); UpdateThemeChecks();
        string dir = Path.Combine(Path.GetTempPath(), "LoudlineSmoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var smokeFailures = new List<Exception>();
        async Task SmokeCheck(string name, Func<Task> check)
        {
            try { await check(); }
            catch (Exception error)
            {
                smokeFailures.Add(new InvalidOperationException($"Native UI fixture failed: {name}", error));
                // Keep every failure in the log even if a later fixture fails or
                // the runner reaches its timeout before the final aggregate.
                App.LogException(new AggregateException("Native UI fixture failures so far", smokeFailures));
            }
        }
        try
        {
            for (int index = 0; index < 3; index++)
            {
                const int rate = 12000; int channels = index == 1 ? 1 : 2; double duration = 24 + index * 5;
                var samples = new float[(int)(duration * rate) * channels];
                for (int f = 0; f < samples.Length / channels; f++)
                {
                    double time = f / (double)rate, fade = Math.Min(1, Math.Min(time / 1.5, (duration - time) / 2));
                    double level = time < duration * .4 ? .13 : .35;
                    for (int ch = 0; ch < channels; ch++) samples[f * channels + ch] = (float)(fade * level * (.6 * Math.Sin(2 * Math.PI * (220 + index * 55) * time) + .2 * Math.Sin(2 * Math.PI * 440 * time + ch * .3)) * (1 + .2 * Math.Sin(time * 2)));
                }
                string input = Path.Combine(dir, $"{index + 1:00} {(index == 0 ? "Morning light" : index == 1 ? "Between rooms" : "The long way home")}.wav");
                var track = new AudioTrack { FilePath = Path.Combine(dir, "synthetic-source.raw"), Original = samples, Channels = channels, SampleRate = rate, Render = new RenderResult { Samples = samples } };
                AudioFileService.ExportWav(track, input, true);
                var loaded = AudioFileService.Load(input);
                loaded.Analysis = await Task.Run(() => LoudnessAnalyzer.Analyze(loaded.Original, loaded.SampleRate, loaded.Channels));
                loaded.Envelope = EnvelopeDesigner.Design(loaded.Analysis, loaded.Duration, _settings);
                loaded.OriginalWaveform = await Task.Run(() => WaveformPyramid.Build(loaded.Original, loaded.Channels));
                loaded.OriginalPeaks = BuildDisplayPeaks(loaded.OriginalWaveform);
                loaded.Render = await Task.Run(() => AudioProcessor.Render(loaded.Original, loaded.SampleRate, loaded.Channels, loaded.Envelope, 0, _settings));
                loaded.Render.Waveform = await Task.Run(() => WaveformPyramid.Build(loaded.Render.Samples, loaded.Channels));
                loaded.PreviewPeaks = BuildDisplayPeaks(loaded.Render.Waveform); loaded.Status = "Ready";
                if (loaded.Render.Samples.Length != samples.Length || loaded.Render.Samples.Any(s => !float.IsFinite(s))) throw new InvalidOperationException("Smoke test: invalid rendered samples.");
                AudioFileService.ExportWav(loaded, Path.Combine(dir, $"export-{index}.wav"), false);
                var roundtrip = AudioFileService.Load(Path.Combine(dir, $"export-{index}.wav"));
                if (roundtrip.Original.Length != samples.Length) throw new InvalidOperationException("Smoke test: WAV length changed.");
                _tracks.Add(loaded);
            }
            var project = new ProjectData { Settings = _settings, Tracks = _tracks.Select(t => new ProjectTrack { Path = t.FilePath, TrimDb = t.TrimDb, InputGainDb = t.InputGainDb, TargetLufs = t.TargetLufs, TransitionEnvelope = t.TransitionEnvelope.ToList(), Envelope = t.Envelope }).ToList() };
            var restored = JsonSerializer.Deserialize<ProjectData>(JsonSerializer.Serialize(project));
            if (restored?.Tracks.Count != 3 || restored.Tracks[0].Envelope.Count != project.Tracks[0].Envelope.Count) throw new InvalidOperationException("Smoke test: project round trip failed.");
            // Exercise the real async import/preview queue, including its visible
            // phase states, rather than seeding only already-rendered track rows.
            var sources = _tracks.Select(track => track.FilePath).ToArray();
            _tracks.Clear(); _renderKeys.Clear(); RefreshList();
            await ImportFilesAsync(sources.Reverse());
            VerifyImportedOriginalDetailSmoke();
            if (!_tracks.Select(track => track.FilePath).SequenceEqual(sources)) throw new InvalidOperationException("Smoke test: imported file batch was not sorted by filename.");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (_renderedRevision != _revision && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (_renderedRevision != _revision || _tracks.Count != 3 || _tracks.Any(track => track.WorkState != TrackWorkState.Idle || track.Render == null)) throw new InvalidOperationException("Smoke test: preview queue did not finish.");
            if (!_smokeSawAnalyzing || !_smokeSawQueued || !_smokeSawProcessing) throw new InvalidOperationException("Smoke test: processing indicators missed a phase.");
            if (!_smokeSawPartialPlaybackReadiness) throw new InvalidOperationException("Smoke test: ready tracks were not published while later tracks processed.");
            for (int attempt = 0; OutputDeviceBox.Items.Count == 0 && attempt < 40; attempt++) { await RefreshOutputDevicesAsync(); await Task.Delay(50); }
            if (OutputDeviceBox.Items.Count == 0) throw new InvalidOperationException("Smoke test: output selector was not initialized.");
            var originalRenders = _tracks.Select(track => track.Render).ToArray();
            var originalAnalyses = _tracks.Select(track => track.Analysis).ToArray();
            int beforeToggle = _renderExecutionCount;
            LevelingCheck.IsChecked = false;
            await Task.Delay(60);
            LevelingCheck.IsChecked = true;
            await Task.Delay(650);
            if (_renderExecutionCount != beforeToggle || _tracks.Where((track, i) => !ReferenceEquals(track.Render, originalRenders[i]) || !ReferenceEquals(track.Analysis, originalAnalyses[i])).Any())
                throw new InvalidOperationException("Smoke test: quick toggle rebuilt unchanged audio.");
            if (_tracks.Any(track => track.WorkState != TrackWorkState.Idle) || PreviewLabel.Text != "✓") throw new InvalidOperationException("Smoke test: cached preview retained a stale marker.");
            LevelingCheck.IsChecked = false;
            await WaitForSmokePreviewAsync();
            int beforeRestore = _renderExecutionCount;
            StrengthSlider.Value = .35; StrengthSlider.Value = .65;
            if (_renderExecutionCount != beforeRestore || _renderedRevision != _revision) throw new InvalidOperationException("Smoke test: bypassed Strength caused work.");
            LevelingCheck.IsChecked = true;
            await Task.Delay(650);
            if (_renderExecutionCount != beforeRestore || _tracks.Where((track, i) => !ReferenceEquals(track.Render, originalRenders[i])).Any()) throw new InvalidOperationException("Smoke test: cached variant was rendered again.");
            UndoClick(this, new RoutedEventArgs());
            if (_settings.LevelingEnabled || _renderExecutionCount != beforeRestore || _renderedRevision != _revision) throw new InvalidOperationException("Smoke test: Undo did not reuse the cached variant.");
            RedoClick(this, new RoutedEventArgs());
            if (!_settings.LevelingEnabled || _renderExecutionCount != beforeRestore || _renderedRevision != _revision) throw new InvalidOperationException("Smoke test: Redo did not reuse the cached variant.");
            int beforeTrim = _renderExecutionCount;
            _tracks[2].TrimDb = -6; QueueRender();
            await WaitForSmokePreviewAsync();
            if (_renderExecutionCount != beforeTrim + 1) throw new InvalidOperationException("Smoke test: editing one track rebuilt other tracks.");
            var ordered = _tracks.ToArray();
            var orderedRenders = _tracks.Select(track => track.Render).ToArray();
            var orderedEnvelopes = _tracks.Select(track => track.Envelope).ToArray();
            int beforeMove = _renderExecutionCount;
            Timeline.FitAll();
            var moveData = new DataObject(TimelineView.TrackDragDataFormat, ordered[0].Id.ToString());
            if (!Timeline.HandleTrackDrop(moveData, new Point(Timeline.ActualWidth - 13, 200))) throw new InvalidOperationException("Smoke test: track drag data was not recognized.");
            if (!_tracks.SequenceEqual(new[] { ordered[1], ordered[2], ordered[0] }) ||
                !FileList.Items.Cast<TrackListEntry>().Select(item => item.Track).SequenceEqual(_tracks) ||
                !Timeline.Tracks.SequenceEqual(_tracks)) throw new InvalidOperationException("Smoke test: reordered list and waveform diverged.");
            MoveTrack(ordered[0], 0);
            await Task.Delay(650);
            await SmokeCheck("Reorder restoration", () =>
            {
                var changedRenders = _tracks.Select((track, index) => (track, index))
                    .Where(item => !ReferenceEquals(item.track.Render, orderedRenders[item.index])).Select(item => item.index + 1).ToArray();
                var changedShapes = _tracks.Select((track, index) => (Index: index + 1,
                    Before: EnvelopeMath.Canonicalize(orderedEnvelopes[index]), After: EnvelopeMath.Canonicalize(track.Envelope)))
                    .Where(item => !item.Before.SequenceEqual(item.After)).ToArray();
                if (_renderExecutionCount != beforeMove || changedRenders.Length > 0 || changedShapes.Length > 0)
                {
                    string details = string.Join("; ", changedShapes.Select(item =>
                    {
                        double delta = item.Before.Concat(item.After).Select(point => point.Time).Distinct()
                            .Select(time => Math.Abs(AudioProcessor.EnvelopeGainAt(item.Before, time) - AudioProcessor.EnvelopeGainAt(item.After, time)))
                            .DefaultIfEmpty(0).Max();
                        return $"track {item.Index}: canonical knots {item.Before.Count}→{item.After.Count}, maximum gain change {delta:R} dB";
                    }));
                    throw new InvalidOperationException($"Smoke test: reordering changed envelope shape or rebuilt existing previews. " +
                        $"DSP executions {beforeMove}→{_renderExecutionCount}; replaced renders [{string.Join(", ", changedRenders)}]; {details}");
                }
                return Task.CompletedTask;
            });
            await SmokeCheck(nameof(VerifyTrackSettingsSmokeAsync), VerifyTrackSettingsSmokeAsync);
            Timeline.ShowEnvelopeOnWaveform = false; Timeline.Refresh();
            Timeline.ShowEnvelopeOnWaveform = true; WaveformEnvelopeCheck.IsChecked = true;
            _renderedRevision = _revision; ExportButton.IsEnabled = true; RefreshList(); SelectTrack(_tracks[0]); Timeline.FitAll();
            CompareRadio.IsChecked = true; SetIdle("Ready");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.Render);
            await Task.Delay(350);
            await SmokeCheck(nameof(VerifyTimelineNavigationSmokeAsync), VerifyTimelineNavigationSmokeAsync);
            await SmokeCheck(nameof(VerifyWaveformEditingSmokeAsync), () => VerifyWaveformEditingSmokeAsync(screenshot));
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
            if (screenshot != null)
            await SmokeCheck("Playback readiness and desktop captures", async () =>
            {
                Timeline.SetTrackInputGain(_tracks[0], 2); Timeline.SetTrackTarget(_tracks[0], -12);
                Timeline.SetTrackTarget(_tracks[1], -18);
                MatchingStrengthSlider.Value = .75;
                await WaitForSmokePreviewAsync();
                Timeline.SetHoveredTrack(new Point(0, 0), force: true);
                await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                CaptureScreenshot(screenshot);
                Timeline.FitTrack(_tracks[0]); Timeline.Playhead = _tracks.Sum(track => track.Duration) - _tracks[^1].Duration / 2; UpdateTime();
                await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-overview.png"));
                Timeline.FitAll(); Timeline.Playhead = 0; UpdateTime();
                Timeline.SetHoveredTrack(new Point(60, 170), force: true);
                Timeline.PreviewTrackDrop(new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * _tracks[0].Duration / _tracks.Sum(track => track.Duration), 170));
                await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                await Task.Delay(100);
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-reorder.png"));
                double secondHover = _tracks[0].Duration + _tracks[1].Duration / 2;
                Timeline.SetHoveredTrack(new Point(Timeline.PlotArea.Left + Timeline.PlotArea.Width * secondHover / _tracks.Sum(track => track.Duration), 170), force: true);
                await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-target.png"));
                // A self-drop clears the marker without changing the album.
                Timeline.HandleTrackDrop(new DataObject(TimelineView.TrackDragDataFormat, _tracks[0].Id.ToString()), new Point(Timeline.PlotArea.Left, 170));
                Timeline.SetHoveredTrack(new Point(0, 0), force: true);
                _tracks[1].WorkState = TrackWorkState.Processing; _tracks[1].Status = "Processing preview…";
                _tracks[2].WorkState = TrackWorkState.Queued; _tracks[2].Status = "Queued for preview";
                RefreshList(); SetBusy("Processing preview: Between rooms…", true);
                if (_playbackTracks[0].PreviewSamples == null || _playbackTracks[1].PreviewSamples != null || _playbackTracks[2].PreviewSamples != null || _playbackTracks.Any(track => track.OriginalSamples == null))
                    throw new InvalidOperationException("Smoke test: processing blocked a ready preview or exposed an unfinished one.");
                double boundary = _tracks[0].Duration;
                if (PlaybackTrackAt(_playbackTracks, 0)?.PreviewSamples == null || PlaybackTrackAt(_playbackTracks, boundary + 1)?.PreviewSamples != null)
                    throw new InvalidOperationException("Smoke test: playback start eligibility did not follow the cursor track.");
                _playbackRequested = true; // Simulate the active stream that reached this unfinished boundary.
                PlaybackWaiting(_tracks[1].Id, boundary);
                CancelInterruptedPlayback();
                if (Math.Abs(Timeline.Playhead - boundary) > .0001) throw new InvalidOperationException("Smoke test: waiting playback jumped past its boundary.");
                await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
                await Task.Delay(200);
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-processing.png"));
                foreach (var track in _tracks) { track.WorkState = TrackWorkState.Idle; track.Status = "Ready"; }
                RefreshList(); SetIdle("Ready");
                await CaptureMenuSmokeAsync(screenshot);
            });
            await SmokeCheck(nameof(VerifyMaximizerSmokeAsync), () => VerifyMaximizerSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyStartupAndExportProgressSmokeAsync), () => VerifyStartupAndExportProgressSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyPeakDeltaSmoke), () => { VerifyPeakDeltaSmoke(); return Task.CompletedTask; });
            await SmokeCheck(nameof(VerifyBassSensitivitySmokeAsync), () => VerifyBassSensitivitySmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyLevelingLifecycleSmokeAsync), VerifyLevelingLifecycleSmokeAsync);
            await SmokeCheck(nameof(VerifySettingsDialogsSmokeAsync), () => VerifySettingsDialogsSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyAuditionMatchSmoke), () => { VerifyAuditionMatchSmoke(); return Task.CompletedTask; });
            await SmokeCheck(nameof(VerifyWaveformReorderHandlesSmoke), () => { VerifyWaveformReorderHandlesSmoke(); return Task.CompletedTask; });
            await SmokeCheck(nameof(VerifyStrengthGesturesSmokeAsync), VerifyStrengthGesturesSmokeAsync);
            await SmokeCheck(nameof(VerifyTargetReadoutSmokeAsync), () => VerifyTargetReadoutSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyCeilingFieldSmokeAsync), VerifyCeilingFieldSmokeAsync);
            await SmokeCheck(nameof(VerifyLiveWaveformSmokeAsync), VerifyLiveWaveformSmokeAsync);
            await SmokeCheck(nameof(VerifyAppliedEnvelopeSmokeAsync), VerifyAppliedEnvelopeSmokeAsync);
            await SmokeCheck(nameof(VerifyHorizontalScrollSmokeAsync), VerifyHorizontalScrollSmokeAsync);
            await SmokeCheck(nameof(VerifyComparisonRasterSmokeAsync), VerifyComparisonRasterSmokeAsync);
            await SmokeCheck(nameof(VerifyAllRasterModesSmokeAsync), VerifyAllRasterModesSmokeAsync);
            await SmokeCheck(nameof(VerifyLongWheelNavigationSmokeAsync), () => VerifyLongWheelNavigationSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyColdZoomContinuitySmokeAsync), VerifyColdZoomContinuitySmokeAsync);
            await SmokeCheck(nameof(VerifyCrosshairSmokeAsync), VerifyCrosshairSmokeAsync);
            await SmokeCheck(nameof(VerifyWaveformCenteringSmokeAsync), () => VerifyWaveformCenteringSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyMissingTimelineSmoke), () => { VerifyMissingTimelineSmoke(); return Task.CompletedTask; });
            await SmokeCheck(nameof(VerifyRelinkDialogSmokeAsync), () => VerifyRelinkDialogSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyProjectRelinkSmokeAsync), () => VerifyProjectRelinkSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifySourceReplacementSmokeAsync), VerifySourceReplacementSmokeAsync);
            await SmokeCheck(nameof(VerifyResponsiveLayoutSmokeAsync), () => VerifyResponsiveLayoutSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyCursorReadoutSmokeAsync), () => VerifyCursorReadoutSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifySingleTrackExportSmokeAsync), VerifySingleTrackExportSmokeAsync);
            await SmokeCheck(nameof(VerifyRenderPrioritySmokeAsync), VerifyRenderPrioritySmokeAsync);
            await SmokeCheck(nameof(VerifyExternalFileDropSmokeAsync), VerifyExternalFileDropSmokeAsync);
            await SmokeCheck(nameof(VerifyBackgroundLaneSmokeAsync), () => VerifyBackgroundLaneSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyBackgroundNavigationSmokeAsync), VerifyBackgroundNavigationSmokeAsync);
            await SmokeCheck(nameof(VerifyBackgroundSmokeAsync), () => VerifyBackgroundSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyBackgroundSoloSmokeAsync), VerifyBackgroundSoloSmokeAsync);
            await SmokeCheck(nameof(VerifyBackgroundGainSmokeAsync), VerifyBackgroundGainSmokeAsync);
            await SmokeCheck(nameof(VerifyTwoTrackUiSmokeAsync), () => VerifyTwoTrackUiSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyCancellationSmokeAsync), VerifyCancellationSmokeAsync);
            await SmokeCheck(nameof(VerifySlicingSmokeAsync), VerifySlicingSmokeAsync);
            await SmokeCheck(nameof(VerifyImmediateSlicingSmokeAsync), () => VerifyImmediateSlicingSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyProjectEnvelopeSmokeAsync), VerifyProjectEnvelopeSmokeAsync);
            await SmokeCheck(nameof(VerifyButtonChromeSmokeAsync), VerifyButtonChromeSmokeAsync);
            await SmokeCheck(nameof(VerifySettingsFlyoutsSmokeAsync), VerifySettingsFlyoutsSmokeAsync);
            await SmokeCheck(nameof(VerifyCustomBoundariesSmokeAsync), () => VerifyCustomBoundariesSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyBoundaryPersistenceSmokeAsync), VerifyBoundaryPersistenceSmokeAsync);
            await SmokeCheck(nameof(VerifyExportOptionsSmokeAsync), () => VerifyExportOptionsSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyMenuLayoutSmokeAsync), VerifyMenuLayoutSmokeAsync);
            await SmokeCheck(nameof(VerifyButtonStyleSmokeAsync), VerifyButtonStyleSmokeAsync);
            await SmokeCheck(nameof(VerifyBypassSmokeAsync), VerifyBypassSmokeAsync);
            await SmokeCheck(nameof(VerifyCloseProjectSmokeAsync), VerifyCloseProjectSmokeAsync);
            await SmokeCheck(nameof(VerifyScrubbingSmokeAsync), VerifyScrubbingSmokeAsync);
            await SmokeCheck(nameof(VerifySharedProjectRangesSmokeAsync), VerifySharedProjectRangesSmokeAsync);
            await SmokeCheck(nameof(VerifyAutoProcessAndShortcutsSmokeAsync), () => VerifyAutoProcessAndShortcutsSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyShortcutCatalogSmokeAsync), () => VerifyShortcutCatalogSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyAboutDialogSmokeAsync), () => VerifyAboutDialogSmokeAsync(screenshot));
            await SmokeCheck(nameof(VerifyExplorerIconSmoke), () => { VerifyExplorerIconSmoke(); return Task.CompletedTask; });
            await SmokeCheck(nameof(VerifyLoopTimelineSmokeAsync), VerifyLoopTimelineSmokeAsync);
            await SmokeCheck(nameof(VerifyInsertSilenceSmokeAsync), VerifyInsertSilenceSmokeAsync);
            await SmokeCheck(nameof(VerifyMergingSmokeAsync), VerifyMergingSmokeAsync);
            await SmokeCheck(nameof(VerifyProcessingCompletionSmokeAsync), VerifyProcessingCompletionSmokeAsync);
            if (smokeFailures.Count > 0)
                throw new AggregateException($"{smokeFailures.Count} native UI fixture(s) failed.", smokeFailures);
        }
        finally { Directory.Delete(dir, true); }
    }
}

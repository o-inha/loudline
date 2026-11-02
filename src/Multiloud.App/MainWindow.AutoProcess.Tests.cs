using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyAutoProcessAndShortcutsSmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        var session = Capture(); var keys = _renderKeys.ToArray();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        string? path = _projectPath; string title = Title, status = StatusText.Text;
        bool dirty = _dirty, needsCopy = _backgroundProjectNeedsCopy;
        bool original = OriginalRadio.IsChecked == true, comparison = CompareRadio.IsChecked == true;
        bool envelope = WaveformEnvelopeCheck.IsChecked == true, gains = ShowGains.IsChecked == true;
        bool background = BackgroundVisibilityCheck.IsChecked == true, solo = BackgroundSoloButton.IsChecked == true;
        bool boundaries = CustomBoundariesCheck.IsChecked == true;
        bool audition = AuditionMatchCheck.IsChecked == true, fileListVisible = !_fileListHidden;
        var fileWidth = WorkspaceGrid.ColumnDefinitions[0].Width; double fileMin = WorkspaceGrid.ColumnDefinitions[0].MinWidth;
        var savedFileWidth = _visibleFileListWidth; double savedFileMin = _visibleFileListMinimum;
        var musicHeight = AudioLanes.RowDefinitions[0].Height; var backgroundHeight = AudioLanes.RowDefinitions[2].Height;
        var rememberedMusic = _visibleMusicHeight; var rememberedBackground = _visibleBackgroundHeight;
        double playhead = Timeline.Playhead; var selected = Selected;
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Auto-process/shortcuts smoke: " + message); }
        bool KeyPress(Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false, IInputElement? focused = null) =>
            TryHandleWorkspaceShortcut(key, modifiers, repeat, focused);
        try
        {
            CancelSourceWork(); _player.Stop(); ResetEmptyProject(); RefreshAutoProcessState();
            Require(AutoProcessToggle.Visibility == Visibility.Visible && AutoProcessToggle.IsChecked == true && AutoProcessToggle.IsEnabled,
                "the startup/closed-project empty slate did not offer enabled auto-processing.");
            Require(AutoProcessToggle.Height == 56 && AutoProcessToggle.HorizontalAlignment == HorizontalAlignment.Center &&
                AutoProcessToggle.VerticalAlignment == VerticalAlignment.Center && AutoProcessToggle.ToolTip == null,
                "the empty action was not centered, large and free of help text.");
            int revision = _revision, executions = _renderExecutionCount;
            AutoProcessToggle.IsChecked = false;
            Require(!_settings.LevelingEnabled && !_settings.MatchingEnabled && !_settings.LimiterEnabled &&
                LevelingCheck.IsChecked == false && MatchingCheck.IsChecked == false && LimiterCheck.IsChecked == false,
                "turning off auto-process did not disable all three modules.");
            _settings = _settings with { MatchingEnabled = true }; SyncControls(); RefreshAutoProcessState();
            Require(AutoProcessToggle.IsChecked == false, "a mixed module selection appeared fully enabled.");
            AutoProcessToggle.IsChecked = true;
            Require(_settings.LevelingEnabled && _settings.MatchingEnabled && _settings.LimiterEnabled &&
                !_dirty && _undo.Count == 0 && _redo.Count == 0 && _revision == revision && _renderExecutionCount == executions,
                "the empty toggle left a partial module state, created an unsaved edit or requested audio work.");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ContextIdle);
            if (screenshot != null) CaptureVisual(AudioLanes, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-empty-auto-process.png"));

            var placeholder = new AudioTrack { FilePath = "Pending source.wav", WorkState = TrackWorkState.Queued };
            _tracks.Add(placeholder); RefreshAutoProcessState();
            Require(AutoProcessToggle.Visibility == Visibility.Collapsed, "the empty action covered a music source slot.");
            _tracks.Clear();
            _backgroundLayer = new() { Clips = [new() { Source = placeholder }] }; RefreshAutoProcessState();
            Require(AutoProcessToggle.Visibility == Visibility.Collapsed, "a background-only project appeared empty.");
            _backgroundLayer = new(); RefreshAutoProcessState();

            Require(KeyPress(Key.D1) && OriginalRadio.IsChecked == true && KeyPress(Key.NumPad2) && PreviewRadio.IsChecked == true &&
                KeyPress(Key.D3) && CompareRadio.IsChecked == true, "1/2/3 did not select their view buttons.");
            foreach (var pair in new[] { (Key.E, WaveformEnvelopeCheck), (Key.G, ShowGains),
                (Key.B, BackgroundVisibilityCheck), (Key.C, CustomBoundariesCheck) })
            {
                bool before = pair.Item2.IsChecked == true;
                Require(KeyPress(pair.Item1) && pair.Item2.IsChecked != before, pair.Item1 + " did not toggle its button.");
                Require(KeyPress(pair.Item1, repeat: true) && pair.Item2.IsChecked != before, pair.Item1 + " oscillated on key repeat.");
                Require(KeyPress(pair.Item1) && pair.Item2.IsChecked == before, pair.Item1 + " did not toggle back.");
            }
            Require(KeyPress(Key.A), "A was not handled as Fit all.");
            Require(KeyPress(Key.S) && !Timeline.SliceMode && _tracks.Count == 0, "S did not invoke the playhead slice command on an empty project.");
            Require(KeyPress(Key.M) && _tracks.Count == 0, "M did not invoke the nearest-boundary merge command on an empty project.");
            bool enabled = BackgroundEnabledCheck.IsChecked == true;
            Require(KeyPress(Key.B, ModifierKeys.Shift) && BackgroundEnabledCheck.IsChecked != enabled &&
                KeyPress(Key.B, ModifierKeys.Shift) && BackgroundEnabledCheck.IsChecked == enabled,
                "Shift+B did not control background enable independently of visibility.");
            Require(KeyPress(Key.S, ModifierKeys.Shift) && BackgroundSoloButton.IsChecked == true &&
                KeyPress(Key.S, ModifierKeys.Shift) && BackgroundSoloButton.IsChecked == false,
                "Shift+S did not control Solo independently of Slice.");
            Require(!KeyPress(Key.S, focused: new TextBox()) && !KeyPress(Key.E, focused: new ComboBox()) &&
                !KeyPress(Key.G, ModifierKeys.Control) && !KeyPress(Key.G, ModifierKeys.Alt) &&
                !KeyPress(Key.G, ModifierKeys.Windows), "text entry or modified keys triggered a workspace shortcut.");
            bool gainBefore = ShowGains.IsChecked == true;
            ShowGains.IsEnabled = false;
            try { Require(KeyPress(Key.G) && ShowGains.IsChecked == gainBefore, "a shortcut changed a disabled button."); }
            finally { ShowGains.ClearValue(IsEnabledProperty); }
            Require(_renderExecutionCount == executions, "view/tool shortcuts on an empty project ran DSP.");
            Require(CloseProjectMenuItem.InputGestureText == "Ctrl+W", "the close-project menu omitted its shortcut.");
            _dirty = false;
            Require(KeyPress(Key.W, ModifierKeys.Control), "Ctrl+W was not handled.");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_closingProject && DateTime.UtcNow < deadline) await Task.Delay(10);
            Require(!_closingProject && _tracks.Count == 0 && _backgroundLayer.Clips.Count == 0 && !_dirty &&
                _undo.Count == 0 && !_fileListHidden && AutoProcessToggle.Visibility == Visibility.Visible &&
                AutoProcessToggle.IsChecked == true, "Ctrl+W did not restore the startup empty slate.");
        }
        finally
        {
            CancelSourceWork();
            await _renderGate.WaitAsync(); _renderGate.Release();
            _renderKeys.Clear(); foreach (var pair in keys) _renderKeys[pair.Key] = pair.Value;
            foreach (var item in session.Tracks)
                if (item.Track.Render != null && _renderKeys.TryGetValue(item.Track.Id, out var key))
                    _previewCache.Store(key, item.Track.Render, item.Track.PreviewPeaks);
            Restore(session); await WaitForSmokePreviewAsync();
            _syncing = true;
            try
            {
                OriginalRadio.IsChecked = original; CompareRadio.IsChecked = comparison;
                if (!original && !comparison) PreviewRadio.IsChecked = true;
                WaveformEnvelopeCheck.IsChecked = envelope; ShowGains.IsChecked = gains;
                BackgroundSoloButton.IsChecked = solo; AuditionMatchCheck.IsChecked = audition;
                CustomBoundariesCheck.IsChecked = boundaries;
                BackgroundVisibilityCheck.IsChecked = background;
                AudioLanes.RowDefinitions[0].Height = musicHeight; AudioLanes.RowDefinitions[2].Height = backgroundHeight;
                _visibleMusicHeight = rememberedMusic; _visibleBackgroundHeight = rememberedBackground;
            }
            finally { _syncing = false; }
            SetFileListVisible(true);
            WorkspaceGrid.ColumnDefinitions[0].Width = fileListVisible ? fileWidth : savedFileWidth;
            WorkspaceGrid.ColumnDefinitions[0].MinWidth = fileListVisible ? fileMin : savedFileMin;
            SetFileListVisible(fileListVisible);
            _visibleFileListWidth = savedFileWidth; _visibleFileListMinimum = savedFileMin;
            _undo.Clear(); foreach (var item in undo.Reverse()) _undo.Push(item);
            _redo.Clear(); foreach (var item in redo.Reverse()) _redo.Push(item);
            _projectPath = path; _backgroundProjectNeedsCopy = needsCopy; _dirty = dirty;
            Title = title; StatusText.Text = status; Timeline.Playhead = playhead;
            if (selected != null && _tracks.Contains(selected)) SelectTrack(selected);
            RefreshAutoProcessState(); UpdateTime();
        }
    }
}

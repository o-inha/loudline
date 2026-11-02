using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _syncingAutoProcess;

    private void InitializeAutoProcess()
    {
        foreach (var module in new[] { LevelingCheck, MatchingCheck, LimiterCheck })
        {
            module.Checked += AutoProcessModuleStateChanged;
            module.Unchecked += AutoProcessModuleStateChanged;
        }
        RefreshAutoProcessState();
    }

    private void AutoProcessModuleStateChanged(object sender, RoutedEventArgs e) => RefreshAutoProcessState();

    private void RefreshAutoProcessState()
    {
        if (AutoProcessToggle == null) return;
        _syncingAutoProcess = true;
        try
        {
            AutoProcessToggle.IsChecked = _settings.LevelingEnabled && _settings.MatchingEnabled && _settings.LimiterEnabled;
            AutoProcessToggle.Visibility = _tracks.Count == 0 && _backgroundLayer.Clips.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            AutoProcessToggle.IsEnabled = !_closed && !_closingProject && !_importing && !_openingProject && !_exporting;
        }
        finally { _syncingAutoProcess = false; }
    }

    private void AutoProcessChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingAutoProcess || _syncing || _closed || _closingProject || _importing ||
            _openingProject || _exporting || _tracks.Count > 0 || _backgroundLayer.Clips.Count > 0) return;
        bool enabled = AutoProcessToggle.IsChecked == true;
        if (_settings.LevelingEnabled == enabled && _settings.MatchingEnabled == enabled && _settings.LimiterEnabled == enabled) return;
        // This selects how the next import will behave. An empty session has no
        // audio edit to undo or unsaved project to protect.
        _settings = _settings with { LevelingEnabled = enabled, MatchingEnabled = enabled, LimiterEnabled = enabled };
        SyncControls(); ApplyTimelineSettings(); RefreshAutoProcessState();
    }

    private static bool IsShortcutEditingContext(IInputElement? focused) =>
        focused is TextBox or PasswordBox or ComboBox or ComboBoxItem;

    // Kept separate from KeyEventArgs so native UI checks exercise exactly the
    // same shortcut dispatch as physical input, without synthesizing OS keys.
    private bool TryHandleWorkspaceShortcut(Key key, ModifierKeys modifiers, bool repeat, IInputElement? focused)
    {
        if (!_initialized || _closed || _closingProject || IsShortcutEditingContext(focused) ||
            (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0 ||
            _settingsFlyout is { IsOpen: true }) return false;
        if (modifiers == ModifierKeys.Control && key == Key.W)
        {
            if (!repeat && !_exporting) CloseProjectClick(this, new RoutedEventArgs());
            return true;
        }
        ToggleButton? toggle = null;
        Button? command = null;
        bool radio = false;
        if (modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.D1: case Key.NumPad1: toggle = OriginalRadio; radio = true; break;
                case Key.D2: case Key.NumPad2: toggle = PreviewRadio; radio = true; break;
                case Key.D3: case Key.NumPad3: toggle = CompareRadio; radio = true; break;
                case Key.E: toggle = WaveformEnvelopeCheck; break;
                case Key.G: toggle = ShowGains; break;
                case Key.B: toggle = BackgroundVisibilityCheck; break;
                case Key.C: toggle = CustomBoundariesCheck; break;
                case Key.A: command = FitAllButton; break;
                case Key.S: command = SliceButton; break;
                case Key.M: command = MergeButton; break;
                case Key.Z: toggle = LoopACheck; break;
                case Key.X: toggle = LoopBCheck; break;
                case Key.F:
                    if (!repeat) SetFileListVisible(_fileListHidden);
                    return true;
                default: return false;
            }
        }
        else if (modifiers == ModifierKeys.Shift)
        {
            if (key == Key.B) toggle = BackgroundEnabledCheck;
            else if (key == Key.S) toggle = BackgroundSoloButton;
            else return false;
        }
        else return false;
        if (repeat) return true; // Holding a toggle key must not oscillate it.
        if (toggle is { IsEnabled: true })
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, radio || toggle.IsChecked != true);
        else if (command is { IsEnabled: true }) command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }
}

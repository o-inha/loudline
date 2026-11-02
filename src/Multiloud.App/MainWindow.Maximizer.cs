using Multiloud.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Multiloud.App;

public partial class MainWindow
{
    private void SyncMaximizerControls()
    {
        SyncPeakDeltaControls();
        UpdateMaximizerMeter();
    }

    private bool ApplyMaximizerSettings(MaximizerStyle style, double character, double transients,
        double? bassProtection = null, bool? dynamicsGuardEnabled = null, double? maxSustainedReductionDb = null)
    {
        if (!_initialized || _syncing || _exporting) return false;
        var settings = _settings with
        {
            MaximizerStyle = style, MaximizerCharacter = character, TransientPreservation = transients,
            BassProtection = bassProtection ?? _settings.BassProtection,
            DynamicsGuardEnabled = dynamicsGuardEnabled ?? _settings.DynamicsGuardEnabled,
            MaxSustainedReductionDb = maxSustainedReductionDb ?? _settings.MaxSustainedReductionDb
        };
        ValidateSettings(settings);
        if (settings == _settings) return false;
        PushUndo();
        _settings = settings;
        _dirty = true;
        SyncMaximizerControls();
        ApplyTimelineSettings();
        if (_settings.LimiterEnabled) QueueRender();
        return true;
    }

    private void MaximizerOptionsClick(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        FinishStrengthGesture(false);
        ToggleSettingsFlyout("Peak", MaximizerOptionsButton, CreateMaximizerOptionsContent);
    }

    private FrameworkElement CreateMaximizerOptionsContent(Action close)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Width = 324 };
        NameScope.SetNameScope(panel, new NameScope());
        MaximizerStyle draftStyle = _settings.MaximizerStyle;
        var styles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var style in Enum.GetValues<MaximizerStyle>())
        {
            var choice = new RadioButton
            {
                Name = "Maximizer" + style + "Button", Content = style.ToString(), GroupName = "PeakProtectionStyle",
                Style = (Style)FindResource("ViewSegment"), Tag = style == MaximizerStyle.Transparent ? "First" : "Last",
                IsChecked = style == draftStyle, Margin = new Thickness(0), MinWidth = 112
            };
            panel.RegisterName(choice.Name, choice);
            choice.Checked += (_, _) => draftStyle = style;
            if (styles.Children.Count > 0)
            {
                var separator = new Border { Width = 1 };
                separator.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
                styles.Children.Add(separator);
            }
            styles.Children.Add(choice);
        }
        panel.Children.Add(styles);

        Slider AddControl(string name, string title, double initial, string? left = null, string? right = null)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 5) });
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var slider = new Slider
            {
                Name = name, Minimum = 0, Maximum = 1, Value = initial, SmallChange = .01, LargeChange = .1,
                IsMoveToPointEnabled = true, Height = 28
            };
            panel.RegisterName(name, slider);
            var value = new TextBlock
            {
                Text = (initial * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%",
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (_, _) => value.Text = (slider.Value * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
            row.Children.Add(slider);
            Grid.SetColumn(value, 1); row.Children.Add(value);
            if (left != null && right != null)
            {
                var ends = new Grid();
                foreach (var (label, alignment) in new[] { (left, HorizontalAlignment.Left), (right, HorizontalAlignment.Right) })
                {
                    var end = new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = alignment };
                    end.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                    ends.Children.Add(end);
                }
                Grid.SetRow(ends, 1); row.Children.Add(ends);
            }
            panel.Children.Add(row);
            return slider;
        }

        var character = AddControl("MaximizerCharacterSlider", "Response", _settings.MaximizerCharacter, "Fast", "Slow");
        var transients = AddControl("MaximizerTransientsSlider", "Transient preservation", _settings.TransientPreservation);
        var bass = AddControl("MaximizerBassProtectionSlider", "Bass control", _settings.BassProtection, "Broadband", "Selective");
        var safeguard = new CheckBox
        {
            Name = "MaximizerDynamicsGuardCheck", Content = "Dynamics safeguard", IsChecked = _settings.DynamicsGuardEnabled,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10)
        };
        panel.RegisterName(safeguard.Name, safeguard); panel.Children.Add(safeguard);
        var guardRow = new Grid { Margin = new Thickness(0, 0, 0, 14), IsEnabled = safeguard.IsChecked == true };
        guardRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        guardRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        guardRow.Children.Add(new TextBlock { Text = "Maximum sustained reduction · dB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var reduction = CreateCompactDialogNumberBox(panel, "MaximizerSustainedReductionBox", _settings.MaxSustainedReductionDb);
        panel.RegisterName(reduction.Name, reduction); Grid.SetColumn(reduction, 1); guardRow.Children.Add(reduction);
        safeguard.Checked += (_, _) => guardRow.IsEnabled = true;
        safeguard.Unchecked += (_, _) => guardRow.IsEnabled = false;
        panel.Children.Add(guardRow);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "MissingTextBrush"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 78, Margin = new Thickness(0, 0, 6, 0) };
        cancel.Click += (_, _) => close();
        var apply = new Button { Name = "MaximizerApplyButton", Content = "Apply", IsDefault = true, MinWidth = 78, Margin = new Thickness(0) };
        panel.RegisterName(apply.Name, apply);
        apply.Click += (_, _) =>
        {
            try
            {
                if (!double.TryParse(reduction.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum))
                    throw new ArgumentException("Enter a valid reduction value.");
                ApplyMaximizerSettings(draftStyle, character.Value, transients.Value, bass.Value, safeguard.IsChecked == true, maximum);
                close();
            }
            catch (Exception exception) { error.Text = exception.Message; error.Visibility = Visibility.Visible; }
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); panel.Children.Add(actions);
        return panel;
    }

    private void UpdateMaximizerMeter()
    {
        AudioTrack? track = Selected;
        double? localSeconds = null;
        if (_player.IsPlaying)
        {
            double position = _player.PositionSeconds;
            var playing = PlaybackTrackAt(_playbackTracks, position);
            if (playing is not null)
            {
                double offset = 0;
                foreach (var item in _tracks)
                {
                    if (item.Id == playing.Id)
                    {
                        track = item;
                        localSeconds = Math.Clamp(position - offset, 0, item.Duration);
                        break;
                    }
                    offset += item.Duration;
                }
            }
        }
        DisplayMaximizerReduction(track?.Render, localSeconds);
    }

    private void DisplayMaximizerReduction(RenderResult? render, double? localSeconds)
    {
        double reduction = 0;
        if (_settings.LimiterEnabled && render is not null)
        {
            reduction = render.MaxReductionDb;
            if (localSeconds is { } seconds)
            {
                var trace = render.ReductionTrace;
                reduction = 0;
                if (trace.Count > 0)
                {
                    int low = 0, high = trace.Count - 1;
                    while (low < high)
                    {
                        int middle = low + (high - low + 1) / 2;
                        if (trace[middle].Time <= seconds) low = middle;
                        else high = middle - 1;
                    }
                    reduction = trace[low].ReductionDb;
                }
            }
        }
        if (!double.IsFinite(reduction)) reduction = 0;
        reduction = Math.Max(0, reduction);
        MaximizerReductionFill.Width = MaximizerReductionBar.Width * Math.Clamp(reduction / 24, 0, 1);
        MaximizerReductionText.Text = reduction.ToString("0.0", CultureInfo.CurrentCulture) + " dB";
    }
}

using Multiloud.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Multiloud.App;

public partial class MainWindow
{
    private void LevelingOptionsClick(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        FinishStrengthGesture(false);
        ToggleSettingsFlyout("Leveling", LevelingOptionsButton, CreateLevelingOptionsContent);
    }

    private FrameworkElement CreateLevelingOptionsContent(Action close)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Width = 324 };
        NameScope.SetNameScope(panel, new NameScope());
        TextBox Field(string name, string label, double value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var box = CreateCompactDialogNumberBox(panel, name, value);
            panel.RegisterName(name, box);
            Grid.SetColumn(box, 1); row.Children.Add(box); panel.Children.Add(row); return box;
        }
        var maximum = Field("MaxLevelingBox", "Maximum suggested change · dB", _settings.MaxLevelingDb);
        var transition = Field("TransitionLengthBox", "Transition length · seconds", _settings.TransitionSeconds);
        panel.Children.Add(new TextBlock { Text = "Bass sensitivity", Margin = new Thickness(0, 0, 0, 5) });
        var bassRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        bassRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bassRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        bassRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bassRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bass = new Slider
        {
            Name = "BassSensitivitySlider", Minimum = .25, Maximum = 1, Value = _settings.BassSensitivity,
            SmallChange = .01, LargeChange = .1, Height = 28, IsMoveToPointEnabled = true
        };
        var bassValue = new TextBlock
        {
            Name = "BassSensitivityValue", Text = (_settings.BassSensitivity * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%",
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        panel.RegisterName(bass.Name, bass); panel.RegisterName(bassValue.Name, bassValue);
        bass.ValueChanged += (_, _) => bassValue.Text = (bass.Value * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
        bassRow.Children.Add(bass); Grid.SetColumn(bassValue, 1); bassRow.Children.Add(bassValue);
        var endpoints = new Grid();
        var reduced = new TextBlock { Text = "Reduced", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left };
        var normal = new TextBlock { Text = "Normal", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        reduced.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        normal.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        endpoints.Children.Add(reduced); endpoints.Children.Add(normal);
        Grid.SetRow(endpoints, 1); bassRow.Children.Add(endpoints); panel.Children.Add(bassRow);

        var errorText = new TextBlock { Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        errorText.SetResourceReference(TextBlock.ForegroundProperty, "MissingTextBrush");
        panel.Children.Add(errorText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 78, Margin = new Thickness(0, 0, 6, 0) };
        cancel.Click += (_, _) => close();
        var apply = new Button { Name = "LevelingApplyButton", Content = "Apply", MinWidth = 78, IsDefault = true, Margin = new Thickness(0) };
        panel.RegisterName(apply.Name, apply);
        actions.Children.Add(cancel); actions.Children.Add(apply); panel.Children.Add(actions);
        apply.Click += (_, _) =>
        {
            bool Parse(TextBox box, out double value) => double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            if (!Parse(maximum, out var max) || !Parse(transition, out var seconds))
            { errorText.Text = "Enter a valid number."; errorText.Visibility = Visibility.Visible; return; }
            var settings = _settings with { MaxLevelingDb = max, TransitionSeconds = seconds, BassSensitivity = bass.Value };
            try { ApplyLevelingOptions(settings); }
            catch (Exception ex) { errorText.Text = ex.Message; errorText.Visibility = Visibility.Visible; return; }
            close();
        };
        return panel;
    }

    private bool ApplyLevelingOptions(ProcessingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_initialized || _syncing || _exporting) return false;
        var updated = _settings with
        {
            MaxLevelingDb = settings.MaxLevelingDb, TransitionSeconds = settings.TransitionSeconds,
            BassSensitivity = settings.BassSensitivity
        };
        ValidateSettings(updated);
        if (updated == _settings) return false;
        // Supersede the old preview before reading/changing authored curves.
        // Source decoding and loudness analysis have their own cancellation token
        // and continue; their completion uses the latest committed settings.
        _renderCts?.Cancel();
        PushUndo(); _settings = updated;
        UpdateTransitions(); _dirty = true; SyncControls(); QueueRender();
        return true;
    }

    private void UpdateExportFormatChecks()
    {
        Wav24Item.IsChecked = !_floatExport; WavFloatItem.IsChecked = _floatExport;
    }
    private void Wav24Click(object sender, RoutedEventArgs e) => SetFloatExport(false);
    private void WavFloatClick(object sender, RoutedEventArgs e) => SetFloatExport(true);
    private void SetFloatExport(bool value)
    {
        if (!_exporting && _floatExport != value) { _floatExport = value; _dirty = true; }
        UpdateExportFormatChecks();
    }
}

using Multiloud.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyResponsiveToolbarThresholdsSmokeAsync(Action<double, double> layout, FrameworkElement host, string? screenshot)
    {
        await VerifyResponsiveButtonStylesSmokeAsync(layout, host, screenshot);
        await VerifyEmptyReleaseActionSmokeAsync(screenshot);
        VerifyFileListVisibilitySmoke(layout);
    }

    private async Task VerifyEmptyReleaseActionSmokeAsync(string? screenshot)
    {
        var source = FileList.ItemsSource;
        var selected = FileList.SelectedItem;
        bool syncing = _syncing;
        int revision = _revision, executions = _renderExecutionCount;
        var rows = new ObservableCollection<TrackListEntry>();
        async Task Check(bool empty)
        {
            await Dispatcher.InvokeAsync(() => { FileList.UpdateLayout(); AddAudioButton.UpdateLayout(); }, DispatcherPriority.DataBind);
            string background = empty ? "AccentBrush" : "ButtonBrush";
            string foreground = empty ? "BackgroundBrush" : "TextBrush";
            if (FileList.HasItems == empty || AddAudioButton.Background is not SolidColorBrush actualBackground ||
                actualBackground.Color != ((SolidColorBrush)FindResource(background)).Color ||
                AddAudioButton.Foreground is not SolidColorBrush actualForeground || actualForeground.Color != ((SolidColorBrush)FindResource(foreground)).Color ||
                AddAudioLabel.Foreground is not SolidColorBrush labelForeground || labelForeground.Color != actualForeground.Color ||
                Math.Abs(AddAudioButton.ActualHeight - 28) > .25 || AddAudioButton.HorizontalAlignment != HorizontalAlignment.Stretch)
                throw new InvalidOperationException("Smoke test: Add audio files primary state did not follow whether the release contains any slots.");
        }
        try
        {
            _syncing = true;
            FileList.ItemsSource = rows;
            await Check(true);
            if (screenshot is not null && AddAudioButton.Parent is FrameworkElement header)
                CaptureVisual(header, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-add-action-empty.png"));
            var placeholder = new TrackListEntry(new AudioTrack { FilePath = "Importing.wav", WorkState = TrackWorkState.Analyzing });
            placeholder.Update(0); rows.Add(placeholder); await Check(false);
            placeholder.Track.WorkState = TrackWorkState.Idle; placeholder.Update(0); await Check(false);
            rows.Clear(); await Check(true);
            var missing = new TrackListEntry(new AudioTrack { FilePath = "Missing.wav", WorkState = TrackWorkState.Missing });
            missing.Update(0); rows.Add(missing); await Check(false);
            rows.Clear(); await Check(true);
            if (_revision != revision || _renderExecutionCount != executions)
                throw new InvalidOperationException("Smoke test: empty-release button styling queued audio work.");
        }
        finally
        {
            FileList.ItemsSource = source;
            FileList.SelectedItem = selected;
            _syncing = syncing;
            await Dispatcher.InvokeAsync(() => { FileList.UpdateLayout(); AddAudioButton.UpdateLayout(); }, DispatcherPriority.DataBind);
        }
    }
}

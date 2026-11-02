using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyTwoTrackUiSmokeAsync(string? screenshot)
    {
        bool? visible = BackgroundVisibilityCheck.IsChecked;
        bool hidden = _backgroundHidden;
        var musicHeight = AudioLanes.RowDefinitions[0].Height;
        var dividerHeight = AudioLanes.RowDefinitions[1].Height;
        var backgroundHeight = AudioLanes.RowDefinitions[2].Height;
        double backgroundMin = AudioLanes.RowDefinitions[2].MinHeight;
        var cachedMusic = _visibleMusicHeight;
        var cachedBackground = _visibleBackgroundHeight;
        var cancelVisibility = CancelButton.Visibility;
        var processVisibility = RebuildPreviewButton.Visibility;
        int revision = _revision, executions = _renderExecutionCount;
        bool dirty = _dirty;
        var layer = _backgroundLayer;
        var settings = _settings;
        var renders = _tracks.Select(track => track.Render).ToArray();
        async Task Layout() => await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Render);
        void RequireNoAudioChanges()
        {
            if (_revision != revision || _renderExecutionCount != executions || _dirty != dirty ||
                !ReferenceEquals(_backgroundLayer, layer) || !ReferenceEquals(_settings, settings) ||
                !_tracks.Select(track => track.Render).SequenceEqual(renders))
                throw new InvalidOperationException("Smoke test: background visibility or footer appearance changed audio processing.");
        }
        try
        {
            BackgroundVisibilityCheck.IsChecked = true;
            await Layout();
            FitAllButton.ApplyTemplate(); FitTrackButton.ApplyTemplate();
            var all = FitAllButton.TransformToAncestor(FitButtons).TransformBounds(new Rect(FitAllButton.RenderSize));
            var track = FitTrackButton.TransformToAncestor(FitButtons).TransformBounds(new Rect(FitTrackButton.RenderSize));
            if (!ReferenceEquals(FitAllButton.Parent, FitButtons) || !ReferenceEquals(FitTrackButton.Parent, FitButtons) ||
                Math.Abs(track.Left - all.Right - 1) > .25 || Math.Abs(all.Top - track.Top) > .25 ||
                Math.Abs(all.Height - 28) > .25 || Math.Abs(track.Height - 28) > .25 ||
                FitAllButton.Template.FindName("SegmentSurface", FitAllButton) is not Border { CornerRadius: var firstCorners } ||
                FitTrackButton.Template.FindName("SegmentSurface", FitTrackButton) is not Border { CornerRadius: var lastCorners } ||
                firstCorners != new CornerRadius(3, 0, 0, 3) || lastCorners != new CornerRadius(0, 3, 3, 0))
                throw new InvalidOperationException("Smoke test: Fit all and Fit track are not a connected button group.");
            if (new Control[] { BackgroundVisibilityCheck, SliceButton, CustomBoundariesCheck, BackgroundEnabledCheck, BackgroundSoloButton }
                .Any(button => Math.Abs(button.ActualHeight - 28) > .25))
                throw new InvalidOperationException("Smoke test: new track controls have inconsistent button heights.");
            if (AuditionMatchCheck.Background is not SolidColorBrush inactive ||
                inactive.Color != ((SolidColorBrush)FindResource("ButtonBrush")).Color)
                throw new InvalidOperationException("Smoke test: the inactive A/B button uses a different background than ordinary buttons.");
            if (PreviewLabel.Visibility != Visibility.Collapsed || Descendants<Button>(ViewControls)
                .Any(button => ReferenceEquals(button, RebuildPreviewButton) || Equals(button.Content, "↻")) ||
                !Descendants<TextBlock>(BackgroundPane).Any(text => text.Text == "Background (unprocessed)"))
                throw new InvalidOperationException("Smoke test: stale toolbar processing icons or the old background label remain visible.");

            BackgroundVisibilityCheck.IsChecked = true;
            AudioLanes.RowDefinitions[0].Height = new GridLength(73, GridUnitType.Star);
            AudioLanes.RowDefinitions[2].Height = new GridLength(27, GridUnitType.Star);
            await Layout();
            BackgroundVisibilityCheck.IsChecked = false; await Layout();
            if (BackgroundPane.Visibility != Visibility.Collapsed || BackgroundSplitter.Visibility != Visibility.Collapsed ||
                AudioLanes.RowDefinitions[1].ActualHeight != 0 || AudioLanes.RowDefinitions[2].ActualHeight != 0 ||
                Math.Abs(AudioLanes.RowDefinitions[0].ActualHeight - AudioLanes.ActualHeight) > .25)
                throw new InvalidOperationException("Smoke test: hiding the background lane reserved space for its pane or divider.");
            RequireNoAudioChanges();
            if (screenshot is not null)
                CaptureVisual((FrameworkElement)Content, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-main-only.png"));
            BackgroundVisibilityCheck.IsChecked = true; await Layout();
            if (BackgroundPane.Visibility != Visibility.Visible || BackgroundSplitter.Visibility != Visibility.Visible ||
                AudioLanes.RowDefinitions[0].Height != new GridLength(73, GridUnitType.Star) ||
                AudioLanes.RowDefinitions[2].Height != new GridLength(27, GridUnitType.Star) ||
                AudioLanes.RowDefinitions[2].ActualHeight < 70)
                throw new InvalidOperationException("Smoke test: showing the background lane lost the user's divider position.");
            RequireNoAudioChanges();

            CancelButton.Visibility = RebuildPreviewButton.Visibility = Visibility.Collapsed; await Layout();
            double footer = StatusFooter.ActualHeight;
            foreach (bool processing in new[] { true, false })
            {
                CancelButton.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;
                RebuildPreviewButton.Visibility = processing ? Visibility.Collapsed : Visibility.Visible;
                await Layout();
                var button = processing ? CancelButton : RebuildPreviewButton;
                if (Math.Abs(footer - 32) > .25 || Math.Abs(StatusFooter.ActualHeight - footer) > .25 ||
                    Math.Abs(button.ActualHeight - 22) > .25 || button.Margin.Top != 0 || button.Margin.Bottom != 0)
                    throw new InvalidOperationException("Smoke test: processing actions resize the status bar or exceed their compact height.");
            }
            if (RebuildPreviewButton.Background is not SolidColorBrush process ||
                process.Color != ((SolidColorBrush)FindResource("AccentBrush")).Color)
                throw new InvalidOperationException("Smoke test: the footer Process action is not highlighted.");
            RequireNoAudioChanges();
        }
        finally
        {
            CancelButton.Visibility = cancelVisibility; RebuildPreviewButton.Visibility = processVisibility;
            BackgroundVisibilityCheck.IsChecked = visible;
            AudioLanes.RowDefinitions[0].Height = musicHeight;
            AudioLanes.RowDefinitions[1].Height = dividerHeight;
            AudioLanes.RowDefinitions[2].MinHeight = backgroundMin;
            AudioLanes.RowDefinitions[2].Height = backgroundHeight;
            _backgroundHidden = hidden; _visibleMusicHeight = cachedMusic; _visibleBackgroundHeight = cachedBackground;
            await Layout();
        }
    }
}

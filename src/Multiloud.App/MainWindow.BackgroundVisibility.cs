using System.Windows;

namespace Multiloud.App;

public partial class MainWindow
{
    private GridLength _visibleMusicHeight = new(82, GridUnitType.Star);
    private GridLength _visibleBackgroundHeight = new(18, GridUnitType.Star);
    private bool _backgroundHidden;

    private void BackgroundVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || AudioLanes == null) return;
        bool visible = BackgroundVisibilityCheck.IsChecked == true;
        if (visible == !_backgroundHidden) return;

        var music = AudioLanes.RowDefinitions[0];
        var divider = AudioLanes.RowDefinitions[1];
        var background = AudioLanes.RowDefinitions[2];
        if (visible)
        {
            music.Height = _visibleMusicHeight;
            divider.Height = new GridLength(5);
            background.MinHeight = 70;
            background.Height = _visibleBackgroundHeight;
            BackgroundPane.Visibility = Visibility.Visible;
            BackgroundSplitter.Visibility = Visibility.Visible;
            _backgroundHidden = false;
            BackgroundLane.Refresh();
        }
        else
        {
            // Visibility is a workspace preference. Keep the clips, monitoring,
            // processing settings and the user's divider position unchanged.
            _visibleMusicHeight = music.Height;
            _visibleBackgroundHeight = background.Height;
            BackgroundPane.Visibility = Visibility.Collapsed;
            BackgroundSplitter.Visibility = Visibility.Collapsed;
            background.MinHeight = 0;
            background.Height = new GridLength(0);
            divider.Height = new GridLength(0);
            music.Height = new GridLength(1, GridUnitType.Star);
            _backgroundHidden = true;
        }
    }
}

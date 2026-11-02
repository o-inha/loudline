using System.Windows;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool _fileListHidden;
    private GridLength _visibleFileListWidth = new(250);
    private double _visibleFileListMinimum = 200;

    private void FileListVisibilityClick(object sender, RoutedEventArgs e) => SetFileListVisible(FileListVisibilityItem.IsChecked);

    private void SetFileListVisible(bool visible)
    {
        FileListVisibilityItem.IsChecked = visible;
        if (visible == !_fileListHidden) return;
        var files = WorkspaceGrid.ColumnDefinitions[0];
        var divider = WorkspaceGrid.ColumnDefinitions[1];
        if (visible)
        {
            files.MinWidth = _visibleFileListMinimum;
            files.Width = _visibleFileListWidth;
            divider.Width = new GridLength(5);
            FilesPanel.Visibility = Visibility.Visible;
            FileListSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _visibleFileListWidth = files.Width;
            _visibleFileListMinimum = files.MinWidth;
            if (FileList.IsKeyboardFocusWithin) Timeline.Focus();
            FilesPanel.Visibility = Visibility.Collapsed;
            FileListSplitter.Visibility = Visibility.Collapsed;
            files.MinWidth = 0;
            files.Width = new GridLength(0);
            divider.Width = new GridLength(0);
        }
        _fileListHidden = !visible;
        _responsiveLastWidth = double.NaN;
        UpdateResponsiveLayout();
    }
}

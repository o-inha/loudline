using System.Windows;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyFileListVisibilitySmoke(Action<double, double> layout)
    {
        bool visible = !_fileListHidden, dirty = _dirty;
        var width = WorkspaceGrid.ColumnDefinitions[0].Width;
        double minimum = WorkspaceGrid.ColumnDefinitions[0].MinWidth;
        var remembered = _visibleFileListWidth; double rememberedMinimum = _visibleFileListMinimum;
        int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
        try
        {
            SetFileListVisible(true);
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(313);
            foreach (double viewport in new[] { 1100d, 8192d })
            {
                layout(viewport, 1200);
                double editorWidth = WaveformPane.ActualWidth;
                bool isWide = _wideLayout;
                if (!TryHandleWorkspaceShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.None, false, null))
                    throw new InvalidOperationException("File list smoke: F was not handled.");
                layout(viewport, 1200);
                if (!_fileListHidden || FileListVisibilityItem.IsChecked || FilesPanel.Visibility != Visibility.Collapsed ||
                    FileListSplitter.Visibility != Visibility.Collapsed || WorkspaceGrid.ColumnDefinitions[0].ActualWidth > .01 ||
                    WorkspaceGrid.ColumnDefinitions[1].ActualWidth > .01 || Math.Abs(WaveformPane.ActualWidth - editorWidth - 318) > 1)
                    throw new InvalidOperationException("File list smoke: hiding the list did not give its full width and divider to the waveform area.");
                if (_wideLayout != isWide)
                    throw new InvalidOperationException("File list smoke: the fixture unexpectedly crossed responsive modes.");
                TryHandleWorkspaceShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.None, true, null);
                if (!_fileListHidden) throw new InvalidOperationException("File list smoke: F key repeat reopened the pane.");
                TryHandleWorkspaceShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.None, false, null);
                layout(viewport, 1200);
                if (_fileListHidden || !FileListVisibilityItem.IsChecked || FilesPanel.Visibility != Visibility.Visible ||
                    FileListSplitter.Visibility != Visibility.Visible || Math.Abs(WorkspaceGrid.ColumnDefinitions[0].ActualWidth - 313) > 1 ||
                    Math.Abs(WaveformPane.ActualWidth - editorWidth) > 1)
                    throw new InvalidOperationException("File list smoke: showing the pane did not restore its previous width.");
            }
            if (_revision != revision || _renderExecutionCount != executions || _undo.Count != history || _dirty != dirty)
                throw new InvalidOperationException("File list smoke: changing pane visibility modified the project or processed audio.");
        }
        finally
        {
            SetFileListVisible(true);
            WorkspaceGrid.ColumnDefinitions[0].Width = visible ? width : remembered;
            WorkspaceGrid.ColumnDefinitions[0].MinWidth = visible ? minimum : rememberedMinimum;
            SetFileListVisible(visible);
            _visibleFileListWidth = remembered; _visibleFileListMinimum = rememberedMinimum;
        }
    }
}

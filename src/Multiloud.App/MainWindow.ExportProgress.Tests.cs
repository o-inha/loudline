using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyStartupAndExportProgressSmokeAsync(string? screenshot)
    {
        if (!App.StartupFrameRendered || !App.EditorInitializedAfterFrame || !_editorInitialized || !_editorLoaded)
            throw new InvalidOperationException("Smoke test: the editor was constructed before its real app frame received a first paint.");
        if (Application.Current.MainWindow != this || Application.Current.Windows.OfType<MainWindow>().Count() != 1)
            throw new InvalidOperationException("Smoke test: startup replaced the app frame with a second main window.");
        bool timeline = Timeline.IsEnabled, processing = ProcessingPanel.IsEnabled;
        string status = StatusText.Text;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<double>? oldProgress = null;
        Task<IReadOnlyList<string>>? pending = null;
        try
        {
            pending = RunAlbumExportAsync(["first.wav", "second.wav"], async (index, progress, token) =>
            {
                oldProgress = progress;
                progress(.5);
                await (index == 0 ? first.Task : second.Task).WaitAsync(token);
            }, CancellationToken.None, [1, 3]);
            FlushExportProgress();
            if (!_exporting || _exportProgressAdorner == null || _exportProgressLayer == null ||
                _exportProgressAdorner.FileText.Text != "File 1 / 2" || Math.Abs(_exportProgressAdorner.Progress.Value - 12.5) > 1e-9 ||
                Timeline.IsEnabled || ProcessingPanel.IsEnabled || CancelButton.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Smoke test: export did not expose a visible centered overlay with weighted chunk progress and cancellation.");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var card = _exportProgressAdorner.Card;
            var bounds = card.TransformToAncestor(_exportProgressAdorner).TransformBounds(new Rect(card.RenderSize));
            if (Math.Abs(bounds.Left + bounds.Width / 2 - WorkspaceGrid.ActualWidth / 2) > 1 ||
                Math.Abs(bounds.Top + bounds.Height / 2 - WorkspaceGrid.ActualHeight / 2) > 1)
                throw new InvalidOperationException("Smoke test: the export progress card is not centered over the workspace.");
            if (screenshot != null)
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-export-progress.png"));
            first.TrySetResult();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (_exportProgressAdorner?.FileText.Text != "File 2 / 2" && DateTime.UtcNow < deadline)
            { await Task.Delay(10); FlushExportProgress(); }
            if (_exportProgressAdorner?.FileText.Text != "File 2 / 2" || Math.Abs(_exportProgressAdorner.Progress.Value - 62.5) > 1e-9)
                throw new InvalidOperationException("Smoke test: export progress reset between files or ignored their different lengths.");
            second.TrySetResult(); await pending; pending = null;
            if (_exportProgressAdorner != null || _exportProgressLayer != null || _exportProgressTimer?.IsEnabled == true ||
                _exporting || Timeline.IsEnabled != timeline || ProcessingPanel.IsEnabled != processing)
                throw new InvalidOperationException("Smoke test: completed export left its overlay or disabled editing behind.");
            oldProgress?.Invoke(.7); FlushExportProgress();
            if (_exportProgressAdorner != null)
                throw new InvalidOperationException("Smoke test: a stale export callback resurrected its completed overlay.");

            pending = RunAlbumExportAsync(["cancel.wav"], (_, progress, token) =>
            {
                progress(.25); return Task.Delay(Timeout.Infinite, token);
            }, CancellationToken.None);
            FlushExportProgress();
            _exportCts!.Cancel();
            try { await pending; throw new InvalidOperationException("Smoke test: export cancellation was ignored."); }
            catch (OperationCanceledException) { }
            pending = null;
            if (_exportProgressAdorner != null || _exporting || _exportProgressTimer?.IsEnabled == true)
                throw new InvalidOperationException("Smoke test: a cancelled export retained its center progress overlay.");
            try
            {
                await RunAlbumExportAsync(["failed.wav"], (_, progress, _) =>
                { progress(.3); throw new IOException("Synthetic writer failure"); }, CancellationToken.None);
                throw new InvalidOperationException("Smoke test: export writer failure was swallowed.");
            }
            catch (IOException) { }
            if (_exportProgressAdorner != null || _exporting || _exportProgressTimer?.IsEnabled == true ||
                Timeline.IsEnabled != timeline || ProcessingPanel.IsEnabled != processing)
                throw new InvalidOperationException("Smoke test: failed export retained its overlay or failed to restore editing.");
        }
        finally
        {
            first.TrySetResult(); second.TrySetResult();
            if (pending != null)
            {
                _exportCts?.Cancel();
                try { await pending; } catch (OperationCanceledException) { }
            }
            EndExportProgress(); Timeline.IsEnabled = timeline; ProcessingPanel.IsEnabled = processing;
            SetIdle(status); RefreshBackground(); RefreshList();
        }
    }
}

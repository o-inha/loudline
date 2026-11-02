using System.Windows;

namespace Multiloud.App;

public partial class MainWindow
{
    private List<double> _customTrackBoundaries = [];

    private void InitializeCustomBoundaries()
    {
        Timeline.CustomTrackBoundaries = _customTrackBoundaries;
        Timeline.CustomBoundariesVisible = CustomBoundariesCheck.IsChecked == true;
        Timeline.CustomBoundaryEditStarted += PushExportUndo;
        Timeline.CustomBoundariesEdited += () =>
        {
            _dirty = true;
            CustomBoundaryOptionsChanged();
        };
    }

    private void CustomBoundariesChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        Timeline.CustomBoundariesVisible = CustomBoundariesCheck.IsChecked == true;
    }

    private List<double> CaptureCustomBoundaries() => _customTrackBoundaries.ToList();

    private void LoadCustomBoundaries(IReadOnlyList<double>? positions)
    {
        // Markers keep absolute project positions through source moves/removal.
        // Those beyond a temporarily shorter project stay available for Undo,
        // but neither the visible bar nor export includes them until in range.
        _customTrackBoundaries = positions?.Where(time => double.IsFinite(time) && time > 0)
            .Distinct().Order().ToList() ?? [];
        Timeline.CustomTrackBoundaries = _customTrackBoundaries;
        CustomBoundaryOptionsChanged();
    }

    private void RestoreCustomBoundaries(IReadOnlyList<double> positions) => LoadCustomBoundaries(positions);

    // The export preferences UI can subscribe without making markers part of the
    // processing settings or forcing waveform/source cache invalidation.
    private event Action? CustomBoundaryStateChanged;
    private void CustomBoundaryOptionsChanged() => CustomBoundaryStateChanged?.Invoke();
}

using System.Globalization;
using System.Windows;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyCeilingFieldSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var settings = _settings;
        var renders = _tracks.Select(track => track.Render).ToArray();
        bool dirty = _dirty;
        _undo.Clear();
        try
        {
            int revision = _revision, executions = _renderExecutionCount;
            foreach (var invalid in new[] { "0", "-7", "NaN", "Infinity", "invalid" })
            {
                CeilingBox.Text = invalid; CommitCeiling();
                if (_settings != settings || _revision != revision || _renderExecutionCount != executions || _undo.Count != 0 ||
                    !double.TryParse(CeilingBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double restored) || restored != settings.CeilingDb)
                    throw new InvalidOperationException("Smoke test: invalid ceiling entry changed processing or did not restore the current value.");
            }
            CeilingBox.Text = settings.CeilingDb.ToString("R", CultureInfo.InvariantCulture).Replace('.', ',');
            CommitCeiling();
            if (_revision != revision || _undo.Count != 0)
                throw new InvalidOperationException("Smoke test: unchanged ceiling entry queued processing.");

            double changed = settings.CeilingDb == -1.7 ? -.8 : -1.7;
            CeilingBox.Text = changed.ToString("R", CultureInfo.InvariantCulture).Replace('.', ',');
            CommitCeiling();
            if (_settings.CeilingDb != changed || _revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: typed ceiling did not commit one validated edit.");
            CommitCeiling();
            if (_revision != revision + 1 || _undo.Count != 1)
                throw new InvalidOperationException("Smoke test: Enter followed by focus loss committed the ceiling twice.");
            await WaitForSmokePreviewAsync();
            UndoClick(this, new RoutedEventArgs());
            await WaitForSmokePreviewAsync();
            if (_settings != settings || _tracks.Where((track, index) => !ReferenceEquals(track.Render, renders[index])).Any())
                throw new InvalidOperationException("Smoke test: ceiling Undo did not restore the original settings and cached audio.");
        }
        finally
        {
            _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
            _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
            _dirty = dirty;
            SyncCeilingControl();
            SetIdle("Ready");
        }
    }
}

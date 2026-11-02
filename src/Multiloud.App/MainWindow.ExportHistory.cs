namespace Multiloud.App;

public partial class MainWindow
{
    // Export layout edits have their own history frames. Their undo must never
    // stop playback or restore an older in-flight audio-processing snapshot.
    private void PushExportUndo()
    {
        if (!_initialized || _syncing || _exporting) return;
        FinishStrengthGesture(false);
        _undo.Push(Capture() with { ExportOnly = true });
        _redo.Clear();
        if (_undo.Count > 12)
        {
            var keep = _undo.Take(12).Reverse().ToArray(); _undo.Clear();
            foreach (var entry in keep) _undo.Push(entry);
        }
    }

    private void RestoreExportState(EditSnapshot snapshot)
    {
        RestoreCustomBoundaries(snapshot.CustomTrackBoundaries);
        _floatExport = snapshot.FloatExport;
        LoadExportOptions(snapshot.ExportOptions);
        UpdateExportFormatChecks();
    }
}

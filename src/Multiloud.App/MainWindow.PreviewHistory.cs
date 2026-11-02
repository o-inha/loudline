using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private PreviewHistoryReference? CaptureReadyPreview(AudioTrack track)
        => PreviewCurrent(track, _settings) && track.Render is { } render &&
            _renderKeys.TryGetValue(track.Id, out var key)
            ? new PreviewHistoryReference(key, render, track.PreviewPeaks) : null;

    private void RestoreReadyPreviews(EditSnapshot snapshot)
    {
        foreach (var item in snapshot.Tracks)
        {
            var track = item.Track;
            // Recompute only after controls, the continuous envelope and the
            // background clock are restored. Never label incompatible audio as
            // current merely because its source object survived an Undo.
            if (item.Preview == null || !_tracks.Contains(track) || track.Original.Length == 0 ||
                !item.Preview.TryGet(RenderKey(track, _settings), out var ready)) continue;
            track.Render = ready.Render; track.PreviewPeaks = ready.Peaks;
            _renderKeys[track.Id] = item.Preview.Key;
            RememberBackgroundSegment(track, ready.Render);
            track.WorkState = TrackWorkState.Idle;
            track.Status = ready.Render.Warning ?? "Ready";
        }
    }
}

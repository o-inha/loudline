using System.IO;
using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private static readonly HashSet<string> DroppableAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".aif", ".aiff", ".wma" };
    private long _importNavigationVersion;
    private ImportPositionAnchor? _originalImportResume;
    private bool HasImportPlaybackIntent => _importing && _originalImportResume?.NavigationVersion == _importNavigationVersion;
    private sealed record ImportPositionAnchor(Guid TrackId, Guid SourceId, double LocalSeconds, long NavigationVersion, bool Playing, bool Original);

    private string[] AcceptedDroppedFiles(IEnumerable<string> paths)
    {
        var existing = _tracks.Select(track => track.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            try
            {
                if (!DroppableAudioExtensions.Contains(Path.GetExtension(path)) || !File.Exists(path)) continue;
                string fullPath = Path.GetFullPath(path);
                if (!existing.Contains(fullPath)) accepted.Add(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { }
        }
        return Services.TrackOrdering.SortPaths(accepted).ToArray();
    }

    private bool CanDropAudioFiles(string[] paths) => !_closed && !_closingProject && !_importing && !_exporting && AcceptedDroppedFiles(paths).Length > 0;

    private Task ImportDroppedFilesAsync(string[] paths, int insertionIndex)
        => CanDropAudioFiles(paths) ? ImportFilesAsync(AcceptedDroppedFiles(paths), insertionIndex: insertionIndex) : Task.CompletedTask;

    private static string[]? FileDropPaths(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop, false) ? data.GetData(DataFormats.FileDrop, false) as string[] : null;

    private void RemoveFailedImportTrack(AudioTrack track)
    {
        var previousOrder = _tracks.ToArray();
        track.WorkState = TrackWorkState.Failed; track.Status = "Audio not loaded";
        _tracks.Remove(track);
        // Apply may have cleared joins around this unavailable placeholder.
        // Its removal creates a new join even when insertion never completed.
        UpdateTransitions(previousOrder);
    }

    private ImportPositionAnchor? CaptureImportPosition(ImportPositionAnchor? waiting = null)
    {
        if (_interruptedPlayback is { } pending)
            return new(pending.TrackId, pending.SourceId, pending.LocalSeconds, _importNavigationVersion, true, false);
        bool playing = _player.IsPlaying, paused = _player.IsPaused;
        if (!playing && !paused && waiting != null)
            return waiting.NavigationVersion == _importNavigationVersion ? waiting : null;
        double position = playing || paused ? _player.PositionSeconds : Timeline.Playhead;
        var state = PlaybackTrackAt(_playbackTracks, position);
        if (state == null && _playbackTracks.Length > 0 && position >= _playbackTracks.Sum(track => track.Duration)) state = _playbackTracks[^1];
        var track = state == null ? null : _tracks.FirstOrDefault(track => track.Id == state.Id);
        if (track == null) return null;
        double offset = _playbackTracks.TakeWhile(item => item.Id != track.Id).Sum(item => item.Duration);
        return new(track.Id, track.SourceId, Math.Clamp(position - offset, 0, PlaybackDuration(track)), _importNavigationVersion, playing, OriginalRadio.IsChecked == true);
    }

    private void StopForImportStructure(ImportPositionAnchor? anchor, bool allowResume = true)
    {
        if (!(_player.IsPlaying || _player.IsPaused)) return;
        if (allowResume && anchor is { Playing: true, Original: true }) _originalImportResume = anchor;
        double position = _player.PositionSeconds;
        if (allowResume) ArmInterruptedPlayback(position, _player.IsPlaying, OriginalRadio.IsChecked == true, _revision);
        else CancelInterruptedPlayback();
        _player.Stop();
        if (!HasInterruptedPlayback) _playbackRequested = false;
        UpdatePlayButton();
    }

    private void RestoreImportPosition(ImportPositionAnchor? anchor, bool resumeOriginal = true)
    {
        if (_player.IsPlaying) { Timeline.Playhead = _player.PositionSeconds; UpdateTime(); return; }
        if (anchor == null || anchor.NavigationVersion != _importNavigationVersion ||
            _tracks.FirstOrDefault(track => track.Id == anchor.TrackId && track.SourceId == anchor.SourceId) is not { } track) return;
        double position = _tracks.TakeWhile(item => item.Id != track.Id).Sum(item => PlaybackDuration(item)) + Math.Min(anchor.LocalSeconds, PlaybackDuration(track));
        // RefreshList will publish the new duration/order and resume a pending
        // Result once no unknown earlier duration masks its intended source.
        Timeline.Playhead = position; UpdateTime();
        if (resumeOriginal && anchor.Playing && anchor.Original && !_player.IsPlaying && !HasInterruptedPlayback &&
            PlaybackTrackAt(CapturePlaybackTracks(), position)?.Id == track.Id) StartPlayback(position);
    }
}

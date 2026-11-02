using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private bool TrackNeedsDeferredSourceAnalysis(AudioTrack track) => track.Analysis == null &&
        track.Original.Length > 0 && track.WorkState is not (TrackWorkState.Missing or TrackWorkState.Failed) &&
        !CanUseSourceBypass(track, _settings);

    private bool NeedsDeferredSourceAnalysis => !_importing && _tracks.Any(TrackNeedsDeferredSourceAnalysis);

    // This is processing caused by an edit, not a new source import. Keep it in
    // the render transaction so Cancel restores the edit that requested analysis.
    // A subsequent bypass/Undo cancels the same token and becomes ready at once.
    private async Task EnsureDeferredAnalysisAsync(int revision, bool coalesce = true)
    {
        if (_closed || _closingProject || _exporting || revision != _revision || !NeedsDeferredSourceAnalysis) return;
        BeginProcessingTransaction();
        var cts = new CancellationTokenSource(); _renderCts = cts;
        var ct = cts.Token;
        bool needsProjectAnalysis = _settings.LevelingEnabled;
        _plannedRenderKeys = null;
        ExportButton.IsEnabled = false; SetPreviewStatus(false, pending: true);
        foreach (var track in _tracks.Where(TrackNeedsDeferredSourceAnalysis))
        { track.WorkState = TrackWorkState.Queued; track.Status = "Queued for analysis"; }
        SetBusy("Processing…", true); RefreshList();
        bool finished = false;
        try
        {
            if (coalesce) await Task.Delay(500, ct);
            await _renderGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (revision != _revision || _closed || _closingProject) return;
                _renderStartedRevision = revision;
                double position = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
                StopInvalidatedPlayback(position);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    double nextPosition = _player.IsPlaying || _player.IsPaused ? _player.PositionSeconds : Timeline.Playhead;
                    var track = RenderOrder(_tracks, nextPosition).FirstOrDefault(TrackNeedsDeferredSourceAnalysis);
                    if (track == null) break;
                    var source = track.Original; var id = track.SourceId;
                    int rate = track.SampleRate, channels = track.Channels;
                    track.WorkState = TrackWorkState.Analyzing; track.Status = "Analyzing loudness…";
                    SetBusy($"Analyzing {track.Name}…", true); RefreshList();
                    try
                    {
                        var prepared = await Task.Run(() =>
                        {
                            var samples = track.GetOriginalRegion(ct);
                            return (Samples: samples, Analysis: LoudnessAnalyzer.Analyze(samples, rate, channels, ct));
                        }, ct);
                        ct.ThrowIfCancellationRequested();
                        if (revision != _revision || _closed || _closingProject || !_tracks.Contains(track) ||
                            track.SourceId != id || !ReferenceEquals(track.Original, source)) return;
                        track.Analysis = prepared.Analysis;
                        track.WorkState = TrackWorkState.Queued; track.Status = "Queued for preview";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        if (revision != _revision || !_tracks.Contains(track)) return;
                        track.WorkState = TrackWorkState.Failed;
                        track.Status = $"Could not analyze file: {error.Message}";
                    }
                    RefreshList();
                    // Global leveling needs every pending detector summary
                    // before its shared envelope is final. Analyze that batch
                    // first, then QueueRender publishes one coherent curve.
                    // Independent matching/protection can still expose each
                    // completed clip before analyzing the next source.
                    if (!needsProjectAnalysis) break;
                }
                finished = true;
            }
            finally { _renderGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            bool owner = ReferenceEquals(_renderCts, cts);
            if (owner) { _renderCts = null; _plannedRenderKeys = null; }
            cts.Dispose();
            if (owner && finished && revision == _revision && !_closed && !_closingProject)
                QueueRender(addition: true);
        }
    }
}

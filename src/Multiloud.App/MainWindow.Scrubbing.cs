namespace Multiloud.App;

public partial class MainWindow
{
    private sealed record ScrubTarget(double Position, long Revision);
    private sealed class ScrubGesture
    {
        public required bool Original;
        public ScrubTarget Target = new(0, 0);
        public volatile bool Released, Cancelled;
        public CancellationTokenSource Cancellation = new();
    }
    private sealed record ScrubAudioBackend(
        Func<bool, double, CancellationToken, long> Begin,
        Func<long, bool, double, CancellationToken, bool> Grain,
        Action<long, double, bool> End,
        Action<double> CommitReleasedPosition);
    // Include EndScrub in the serialized region. A released quick click must
    // commit its seek after an older active gesture has restored its transport.
    private readonly SemaphoreSlim _scrubWorkerGate = new(1, 1);
    private ScrubGesture? _scrubGesture;
    private bool IsPlayheadScrubbing => _scrubGesture != null;

    private void InitializeScrubbing()
    {
        Timeline.ScrubStarted += BeginPlayheadScrubbing;
        Timeline.ScrubPositionRequested += UpdatePlayheadScrubbing;
        Timeline.ScrubCompleted += EndPlayheadScrubbing;
    }

    private void BeginPlayheadScrubbing(double position)
    {
        if (_scrubGesture is { Released: false }) CancelPlayheadScrubbing();
        if (_closed || _closingProject || _exporting) return;
        var states = CapturePlaybackTracks();
        if (states.Length == 0) return;
        CancelInterruptedPlayback(); _resumeAfterDeviceLoss = false;
        _playbackTracks = states; _player.UpdateTracks(states);
        var gesture = new ScrubGesture { Original = OriginalRadio.IsChecked == true, Target = new(position, 0) };
        _scrubGesture = gesture;
        // Native smoke checks exercise the exact event/provider path without an audio endpoint.
        if (!App.SmokeMode) _ = RunScrubGestureAsync(gesture);
    }

    private void UpdatePlayheadScrubbing(double position)
    {
        if (_scrubGesture is not { Released: false } gesture) return;
        var previous = Volatile.Read(ref gesture.Target);
        Volatile.Write(ref gesture.Target, new(position, previous.Revision + 1));
        UpdateTime();
    }

    private void EndPlayheadScrubbing(double position)
    {
        if (_scrubGesture is not { } gesture) return;
        var previous = Volatile.Read(ref gesture.Target);
        Volatile.Write(ref gesture.Target, new(position, previous.Revision + 1));
        gesture.Cancelled |= _closed || _closingProject;
        gesture.Released = true;
        gesture.Cancellation.Cancel();
        if (App.SmokeMode) { gesture.Cancellation.Dispose(); _scrubGesture = null; }
        UpdateTime();
    }

    private void CancelPlayheadScrubbing()
    {
        if (_scrubGesture is not { } gesture) return;
        gesture.Cancelled = true; gesture.Released = true;
        gesture.Cancellation.Cancel();
        if (App.SmokeMode) { gesture.Cancellation.Dispose(); _scrubGesture = null; }
    }

    private void CommitReleasedScrubPosition(double position)
    {
        // A quick click can be released before its worker opens any endpoint.
        // It is then an ordinary seek, retaining a paused or playing stream.
        if (_player.IsPaused)
        {
            try { if (!_player.Seek(position, OriginalRadio.IsChecked == true)) _player.Stop(); }
            catch (Exception error) { StatusText.Text = error.Message; }
            Timeline.Playhead = position; UpdateTime();
        }
        else SeekPlayback(position);
    }

    private async Task RunScrubGestureAsync(ScrubGesture gesture, ScrubAudioBackend? backend = null)
    {
        backend ??= new(_player.BeginScrub, _player.Scrub,
            (session, position, resume) => _player.EndScrub(session, position, resume), CommitReleasedScrubPosition);
        Exception? failure = null;
        long session = 0;
        await Task.Run(async () =>
        {
            // Do not cancel this wait: a latest released click must still drain
            // older EndScrub calls before committing its final, ordinary seek.
            await _scrubWorkerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var ct = gesture.Cancellation.Token;
                try
                {
                    if (!gesture.Released)
                    {
                        ct.ThrowIfCancellationRequested();
                        session = backend.Begin(gesture.Original, Volatile.Read(ref gesture.Target).Position, ct);
                        long heard = -1;
                        while (!gesture.Released && !ct.IsCancellationRequested)
                        {
                            var target = Volatile.Read(ref gesture.Target);
                            if (target.Revision != heard)
                            {
                                if (!backend.Grain(session, gesture.Original, target.Position, ct)) break;
                                heard = target.Revision;
                            }
                            // Coalesce pointer events; audio work stays bounded to one tiny grain.
                            await Task.Delay(24, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_closed) { }
                catch (Exception error) { failure = error; }
                finally
                {
                    if (session != 0)
                    {
                        try { backend.End(session, Volatile.Read(ref gesture.Target).Position,
                            !gesture.Cancelled && !_closed && !_closingProject); }
                        catch (Exception error) { failure ??= error; }
                    }
                }
            }
            finally { _scrubWorkerGate.Release(); }
        });
        gesture.Cancellation.Dispose();
        if (!ReferenceEquals(_scrubGesture, gesture)) return;
        _scrubGesture = null;
        if (_closed || _closingProject) return;
        if (session == 0 && gesture.Released && !gesture.Cancelled && failure == null)
            backend.CommitReleasedPosition(Volatile.Read(ref gesture.Target).Position);
        _playbackRequested = _player.IsPlaying;
        UpdatePlayButton();
        if (failure != null) StatusText.Text = failure.Message;
    }
}

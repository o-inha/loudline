using Multiloud.Core;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Multiloud.App.Services;

public sealed record OutputDeviceInfo(string Id, string Name);

/// <summary>
/// Shared-mode Windows playback. Sample-rate conversion is only for monitoring;
/// exports retain each source file's sample rate and channels.
/// </summary>
public sealed class PlaybackService : IDisposable
{
    private readonly object gate = new();
    private SynchronizationContext? context = SynchronizationContext.Current;
    private WasapiOut? player;
    private PlaybackTransport? playbackTransport;
    private PlaybackTrackState[] tracks = [];
    private PlaybackLoopState loops = PlaybackLoopState.Empty;
    private sealed record LoopResume(PlaybackLoopState State, double Position, int Slot);
    private LoopResume? loopResume;
    private int scrubLoopSlot = -1;
    private MMDevice? playbackDevice;
    private string? outputDeviceId;
    private double clockOffsetSeconds;
    private double lastClockSeconds, pausedClockSeconds;
    private double stoppedPosition;
    private bool playbackOriginal;
    private long generation;
    private bool disposed;
    private bool scrubbing, scrubWasPlaying, scrubWasPaused;
    private long scrubSession, scrubSerial;
    private double scrubPosition;

    public event Action? PlaybackEnded;
    public event Action<Guid, double>? PlaybackWaiting;
    public event Action<Exception>? PlaybackError;

    /// <summary>Null follows the Windows default output. Changes apply to the next Play.</summary>
    public string? OutputDeviceId
    {
        get { lock (gate) return outputDeviceId; }
        set { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); outputDeviceId = string.IsNullOrWhiteSpace(value) ? null : value; } }
    }

    public static IReadOnlyList<OutputDeviceInfo> EnumerateOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<OutputDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try { devices.Add(new OutputDeviceInfo(device.ID, device.FriendlyName)); }
                catch (COMException) { /* A device can disconnect while its properties are read. */ }
            }
        }
        return devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string? GetDefaultOutputDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    // The real WASAPI path and the hardware-independent regression tests share this
    // provider graph, including NAudio's byte-buffer-to-float callback boundary.
    internal static ISampleProvider CreateSampleProvider(IReadOnlyList<AudioTrack> tracks, bool original, double seconds)
    {
        var snapshot = SnapshotTracks(tracks);
        return new PlaybackSequence(() => snapshot, original, seconds);
    }

    internal static PlaybackSequence CreateSampleProvider(Func<IReadOnlyList<PlaybackTrackState>> getTracks, bool original, double seconds,
        Func<PlaybackLoopState>? getLoops = null, bool enterLoops = true)
        => new(getTracks, original, seconds, getLoops, enterLoops);

    public PlaybackLoopState Loops => Volatile.Read(ref loops);

    /// <summary>Changes audition routing only; no render, export or device restart.</summary>
    public void UpdateLoops(PlaybackLoopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        PlaybackLoopState previous;
        do
        {
            previous = Volatile.Read(ref loops);
            if (Equals(previous, state)) return;
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref loops, state, previous), previous));
    }

    private PlaybackSequence CreateActiveSequence(bool original, double seconds, bool enterLoops, int preferredSlot = -1)
        => new(() => Volatile.Read(ref tracks), original, seconds, () => Volatile.Read(ref loops), enterLoops, preferredSlot);

    private (double Position, bool EnterLoops, int Slot) ResolvePlay(double requested)
    {
        var snapshot = Volatile.Read(ref tracks);
        double duration = snapshot.Sum(track => track.Duration);
        requested = Math.Clamp(double.IsFinite(requested) ? requested : 0, 0, duration);
        var state = Volatile.Read(ref loops);
        bool resume = loopResume is { } pending && Equals(pending.State, state) &&
            Math.Abs(pending.Position - requested) <= 1d / 48000 + 1e-9;
        int slot = resume ? loopResume!.Slot : -1;
        var plan = new LoopPlaybackPlan(state, (long)Math.Round(duration * 48000),
            (long)Math.Round(requested * 48000), enterLoops: !resume, preferredSlot: slot);
        return (plan.Active ? plan.InitialFrame / 48000.0 : requested, !resume, slot);
    }

    /// <summary>Resolves the actual Play destination before UI readiness checks, without I/O.</summary>
    public double ResolvePlayPosition(double requested)
    {
        lock (gate) return ResolvePlay(requested).Position;
    }

    /// <summary>
    /// Remembers an explicitly chosen seek/resume point while the UI waits for
    /// its audio. This is transport intent only, and never starts playback.
    /// </summary>
    public void RememberLoopContinuation(double position, int preferredSlot = -1)
    {
        if (!double.IsFinite(position)) return;
        lock (gate)
        {
            var state = Volatile.Read(ref loops);
            int slot = preferredSlot;
            if (slot < 0 && loopResume is { } pending && Equals(pending.State, state)) slot = pending.Slot;
            if (slot < 0 && playbackTransport != null)
                slot = playbackTransport.LoopSlotAt(player?.PlaybackState == PlaybackState.Paused ? pausedClockSeconds : lastClockSeconds);
            loopResume = new(state, Math.Max(0, position), slot);
        }
    }

    /// <summary>Publishes readiness and album order atomically; current PCM remains pinned.</summary>
    public void UpdateTracks(IReadOnlyList<PlaybackTrackState> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Volatile.Write(ref this.tracks, tracks.ToArray());
    }

    private static PlaybackTrackState[] SnapshotTracks(IReadOnlyList<AudioTrack> tracks)
    {
        var snapshot = new List<PlaybackTrackState>(tracks.Count);
        double albumStart = 0;
        foreach (var track in tracks)
        {
            var render = track.Render;
            bool prepared = render is { RenderedSampleRate: > 0 } or { RenderedChannels: > 0 };
            int sampleRate = render is { RenderedSampleRate: > 0 } ? render.RenderedSampleRate : track.SampleRate;
            int channels = render is { RenderedChannels: > 0 } ? render.RenderedChannels : track.Channels;
            var original = prepared && render!.BackgroundOriginalSamples != null ? render.BackgroundOriginalSamples : track.Original.Length > 0 ? track.Original : null;
            double duration = render?.LiveSource?.Duration ?? (prepared
                ? render!.Samples.Length / (double)(sampleRate * channels) : track.Duration);
            LiveAudioSource? liveOriginal = track.OriginalFrameCount > 0 && render?.BackgroundOriginalSamples == null &&
                (track.OriginalFrameOffset != 0 || track.OriginalSampleCount != track.Original.Length || render?.LiveSource != null)
                ? new LiveAudioSource(track.Original, track.SampleRate, track.Channels, track.OriginalFrameOffset, track.OriginalFrameCount,
                    albumStart, Background: render?.LiveSource?.Background) : null;
            snapshot.Add(new PlaybackTrackState(track.Id, sampleRate, channels, duration, original, render?.Samples,
                BackgroundSamples: render?.BackgroundSamples,
                BackgroundRequired: render?.IncludesBackground == true,
                LiveOriginal: liveOriginal, LiveResult: render?.LiveSource));
            albumStart += duration;
        }
        return snapshot.ToArray();
    }

    public bool IsPlaying
    {
        get { if (Volatile.Read(ref scrubbing)) return false; lock (gate) return !scrubbing && player?.PlaybackState == PlaybackState.Playing; }
    }

    public bool IsPaused
    {
        get { if (Volatile.Read(ref scrubbing)) return false; lock (gate) return !scrubbing && player?.PlaybackState == PlaybackState.Paused; }
    }

    /// <summary>The immutable track state whose PCM is pinned by the active stream.</summary>
    public PlaybackTrackState? CurrentTrack
    {
        get { lock (gate) return playbackTransport?.CurrentTrack; }
    }

    public double PositionSeconds
    {
        get
        {
            lock (gate)
            {
                if (scrubbing) return scrubPosition;
                if (player is null || player.PlaybackState != PlaybackState.Playing) return stoppedPosition;
                return ReadPosition(player);
            }
        }
    }

    public void Play(IReadOnlyList<AudioTrack> tracks, bool original, double startSeconds)
    {
        UpdateTracks(SnapshotTracks(tracks));
        Play(original, startSeconds);
    }

    public void Play(bool original, double startSeconds)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            context ??= SynchronizationContext.Current;
            var target = ResolvePlay(startSeconds);
            StopCore();
            // Only the current track is pinned. Future tracks resolve the latest
            // published readiness when playback reaches their boundaries.
            var sequence = CreateActiveSequence(original, target.Position, target.EnterLoops, target.Slot);
            loopResume = null;
            clockOffsetSeconds = 0;
            lastClockSeconds = pausedClockSeconds = 0;
            stoppedPosition = sequence.StartSeconds;
            if (sequence.PendingTrackId != null)
            {
                stoppedPosition = sequence.StopPositionSeconds;
                throw new InvalidOperationException(original ? "This track is still decoding. Wait until its audio is ready." : "This track's preview is still processing. Wait until it is ready.");
            }
            if (sequence.IsComplete) return;
            var transport = new PlaybackTransport(sequence);
            OpenOutput(transport, original);
        }
    }

    private void OpenOutput(PlaybackTransport transport, bool original, CancellationToken ct = default)
    {
        long thisGeneration = ++generation;
        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                if (outputDeviceId is null)
                {
                    if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                        throw new InvalidOperationException("Windows has no default audio output. Connect or enable an output device, then try Play again.");
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                else device = enumerator.GetDevice(outputDeviceId);
            }
            if (device.State != DeviceState.Active)
                throw new InvalidOperationException("The selected audio output is unavailable. Choose another output device.");
            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            output.Init(new SampleToWaveProvider(transport));
            ct.ThrowIfCancellationRequested();
            var activeOutput = output;
            output.PlaybackStopped += (_, args) =>
            {
                // Never take gate on the audio thread: Stop waits for that thread to finish.
                ThreadPool.QueueUserWorkItem(_ => OnStopped(activeOutput, transport, thisGeneration, args.Exception));
            };
            player = output;
            playbackTransport = transport;
            playbackOriginal = original;
            playbackDevice = device;
            output.Play();
        }
        catch
        {
            if (ReferenceEquals(player, output)) player = null;
            if (ReferenceEquals(playbackTransport, transport)) playbackTransport = null;
            if (ReferenceEquals(playbackDevice, device)) playbackDevice = null;
            try { output?.Dispose(); }
            finally { device?.Dispose(); }
            throw;
        }
    }

    /// <summary>Builds at most 80 ms of the same immutable monitoring mix as Play, without opening hardware.</summary>
    internal static float[] CreateScrubGrain(IReadOnlyList<PlaybackTrackState> snapshot, bool original,
        double position, CancellationToken ct = default)
    {
        const int frames = 3840, fadeFrames = 480;
        ct.ThrowIfCancellationRequested();
        var sequence = new PlaybackSequence(() => snapshot, original, position);
        if (sequence.PendingTrackId != null || sequence.IsComplete) return [];
        var grain = new float[frames * 2];
        int read = sequence.Read(grain, 0, grain.Length);
        ct.ThrowIfCancellationRequested();
        int actualFrames = read / 2;
        for (int frame = 0; frame < actualFrames; frame++)
        {
            double gain = Math.Min(1, Math.Min(frame / (double)fadeFrames, (actualFrames - 1 - frame) / (double)fadeFrames));
            // Raised-cosine edges avoid sharp discontinuities, including rapid reversals.
            gain = .5 - .5 * Math.Cos(Math.PI * gain);
            grain[frame * 2] *= (float)gain; grain[frame * 2 + 1] *= (float)gain;
        }
        return grain;
    }

    /// <summary>Starts one replaceable-grain session. Call on the scrub worker, never the UI thread.</summary>
    internal long BeginScrub(bool original, double position, CancellationToken ct = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ct.ThrowIfCancellationRequested();
            context ??= SynchronizationContext.Current;
            bool wasPlaying = scrubbing ? scrubWasPlaying : player?.PlaybackState == PlaybackState.Playing;
            bool wasPaused = scrubbing ? scrubWasPaused : player?.PlaybackState == PlaybackState.Paused;
            int beforeLoopSlot = scrubLoopSlot;
            if (!scrubbing && playbackTransport != null && player != null)
            {
                if (wasPlaying) ReadPosition(player);
                beforeLoopSlot = playbackTransport.LoopSlotAt(wasPaused ? pausedClockSeconds : lastClockSeconds);
            }
            // Publish the monitoring state before device initialization. UI timer
            // reads can then return immediately while this worker opens WASAPI.
            scrubbing = true; scrubPosition = position;
            try
            {
                if (player != null && playbackTransport != null && playbackTransport.TryBeginScrub(position))
                {
                    if (player.PlaybackState == PlaybackState.Paused)
                    {
                        double native = player.GetPosition() / (double)player.OutputWaveFormat.AverageBytesPerSecond;
                        clockOffsetSeconds = native - pausedClockSeconds;
                        player.Play();
                    }
                }
                else
                {
                    StopCore(); scrubbing = true;
                    var sequence = CreateSampleProvider(() => Volatile.Read(ref tracks), original, position);
                    var transport = new PlaybackTransport(sequence);
                    transport.TryBeginScrub(position);
                    clockOffsetSeconds = lastClockSeconds = pausedClockSeconds = 0;
                    OpenOutput(transport, original, ct);
                }
                scrubWasPlaying = wasPlaying; scrubWasPaused = wasPaused; scrubLoopSlot = beforeLoopSlot;
                playbackOriginal = original; scrubPosition = stoppedPosition = position;
                return scrubSession = Interlocked.Increment(ref scrubSerial);
            }
            catch
            {
                StopCore(); stoppedPosition = position;
                throw;
            }
        }
    }

    internal bool Scrub(long session, bool original, double position, CancellationToken ct = default)
    {
        var snapshot = Volatile.Read(ref tracks);
        var grain = CreateScrubGrain(snapshot, original, position, ct);
        lock (gate)
        {
            if (disposed || !scrubbing || session != scrubSession || ct.IsCancellationRequested) return false;
            scrubPosition = stoppedPosition = position;
            return playbackTransport?.SetScrubGrain(grain, position) == true;
        }
    }

    /// <summary>Returns to the prior transport state at the released cursor position.</summary>
    internal void EndScrub(long session, double position, bool resume = true)
    {
        lock (gate)
        {
            if (disposed || !scrubbing || session != scrubSession) return;
            var next = CreateActiveSequence(playbackOriginal, position, enterLoops: false, preferredSlot: scrubLoopSlot);
            if (resume && (scrubWasPlaying || scrubWasPaused) && playbackTransport?.EndScrub(next) == true && player != null)
            {
                bool pause = scrubWasPaused;
                scrubbing = false; scrubSession = 0; stoppedPosition = next.StartSeconds;
                if (pause)
                {
                    ReadPosition(player); pausedClockSeconds = lastClockSeconds;
                    player.Pause(); stoppedPosition = next.StartSeconds;
                }
            }
            else
            {
                StopCore(); stoppedPosition = position;
            }
        }
    }

    /// <summary>
    /// Seeks the current mode while preserving its output device and playing or
    /// paused state. The visual cursor moves immediately; audio begins at the new
    /// target after the endpoint's already queued buffer (about 100 ms). Returns
    /// false when there is no live stream, or its EOF has already reached WASAPI;
    /// the caller may then start a new Play. An unready target leaves playback
    /// unchanged and throws the same readiness error as Play. An optional mode
    /// change switches Original/Result monitoring at the same callback boundary.
    /// </summary>
    public bool Seek(double seconds, bool? original = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (player is null || player.PlaybackState == PlaybackState.Stopped || playbackTransport is null) return false;
            bool mode = original ?? playbackOriginal;
            if (player.PlaybackState == PlaybackState.Playing) ReadPosition(player);
            int slot = playbackTransport.LoopSlotAt(player.PlaybackState == PlaybackState.Paused ? pausedClockSeconds : lastClockSeconds);
            var next = CreateActiveSequence(mode, seconds, enterLoops: false, preferredSlot: slot);
            if (next.PendingTrackId is not null)
                throw new InvalidOperationException(mode ? "This track is still decoding. Wait until its audio is ready." : "This track's preview is still processing. Wait until it is ready.");
            if (!playbackTransport.TrySeek(next)) return false;
            playbackOriginal = mode;
            stoppedPosition = next.StartSeconds;
            return true;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (player?.PlaybackState == PlaybackState.Playing)
            {
                stoppedPosition = ReadPosition(player);
                pausedClockSeconds = lastClockSeconds;
                player.Pause();
            }
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (player?.PlaybackState == PlaybackState.Paused)
            {
                // WasapiOut 2.x leaves the endpoint clock running during Pause.
                // Remove that elapsed clock time when resuming the same stream.
                double nativeSeconds = player.GetPosition() / (double)player.OutputWaveFormat.AverageBytesPerSecond;
                clockOffsetSeconds = nativeSeconds - pausedClockSeconds;
                player.Play();
            }
        }
    }

    public void Stop()
    {
        lock (gate) StopCore();
    }

    private void StopCore()
    {
        ++generation;
        bool wasScrubbing = scrubbing;
        scrubbing = false; scrubSession = 0;
        var old = player;
        if (old is null) return;
        var oldDevice = playbackDevice;
        if (old.PlaybackState == PlaybackState.Playing) stoppedPosition = ReadPosition(old);
        if (playbackTransport is { } stoppedTransport)
        {
            int slot = wasScrubbing ? scrubLoopSlot : stoppedTransport.LoopSlotAt(old.PlaybackState == PlaybackState.Paused ? pausedClockSeconds : lastClockSeconds);
            loopResume = new(Volatile.Read(ref loops), stoppedPosition, slot);
        }
        player = null;
        playbackTransport = null;
        playbackDevice = null;
        try { old.Stop(); } // WasapiOut joins its render thread before returning.
        finally
        {
            try { old.Dispose(); }
            finally { oldDevice?.Dispose(); }
        }
    }

    private double ReadPosition(WasapiOut output)
    {
        try
        {
            lastClockSeconds = Math.Max(0, output.GetPosition() / (double)output.OutputWaveFormat.AverageBytesPerSecond - clockOffsetSeconds);
            if (playbackTransport is { } transport) stoppedPosition = transport.PositionAt(lastClockSeconds);
        }
        catch (Exception error) when (error is COMException or ObjectDisposedException or InvalidOperationException)
        {
            // A disconnected output may stop answering its clock before its rendering
            // thread raises PlaybackStopped. Keep the last cursor position until that
            // event reports the device error, so a UI timer cannot crash the application.
        }
        return stoppedPosition;
    }

    private void OnStopped(WasapiOut output, PlaybackTransport transport, long outputGeneration, Exception? error)
    {
        PlaybackSequence sequence;
        lock (gate)
        {
            if (disposed || outputGeneration != generation || !ReferenceEquals(player, output)) return;
            // TrySeek rejects replacement after the transport returns zero, so
            // this identifies the exact epoch which ended the native stream.
            sequence = transport.FinalReadSequence ?? transport.Sequence;
            stoppedPosition = error is null && !scrubbing ? sequence.StopPositionSeconds : stoppedPosition;
            if (error is null && !scrubbing && sequence.PendingTrackId != null)
                loopResume = new(Volatile.Read(ref loops), stoppedPosition, sequence.PendingLoopSlot);
            scrubbing = false; scrubSession = 0;
            player = null;
            playbackTransport = null;
            var oldDevice = playbackDevice;
            playbackDevice = null;
            try { output.Dispose(); }
            finally { oldDevice?.Dispose(); }
        }
        Dispatch(() =>
        {
            // A new Play may have started while this completion was queued to the UI.
            lock (gate) if (disposed || outputGeneration != generation) return;
            if (error is not null) PlaybackError?.Invoke(error);
            else if (sequence.PendingTrackId is { } pending) PlaybackWaiting?.Invoke(pending, sequence.StopPositionSeconds);
            else PlaybackEnded?.Invoke();
        });
    }

    private void Dispatch(Action action)
    {
        if (context is not null) context.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            StopCore();
            disposed = true;
        }
    }

}

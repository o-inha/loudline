using Multiloud.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Multiloud.App.Services;

/// <summary>
/// One published album state. Null samples mean this track is not ready in that
/// mode. Sample arrays must stay immutable after publication. Optional audition
/// gains affect monitoring only, independently for Original and Preview playback.
/// BackgroundSamples is the immutable, source-aligned bed added only to Original;
/// PreviewSamples already contains the final mix and must never add it again.
/// </summary>
public sealed record PlaybackTrackState(Guid Id, int SampleRate, int Channels, double Duration,
    float[]? OriginalSamples, float[]? PreviewSamples,
    double OriginalAuditionGainDb = 0, double PreviewAuditionGainDb = 0,
    float[]? BackgroundSamples = null, bool BackgroundRequired = false,
    BackgroundLayer? SoloBackground = null,
    LiveAudioSource? LiveOriginal = null, LiveAudioSource? LiveResult = null,
    PeakDeltaSource? PeakDelta = null, bool DeltaRequested = false)
{
    public bool IsReady(bool original) => SoloBackground != null ? Duration > 0 :
        DeltaRequested ? PeakDelta?.IsValid == true :
        (original ? LiveOriginal : LiveResult) is { FrameCount: > 0 } ||
        (original ? OriginalSamples : PreviewSamples) is { Length: > 0 } &&
        (!BackgroundRequired || BackgroundSamples != null);
}

/// <summary>
/// Pins the current source, and resolves each following source only on arrival.
/// A pending track ends this sequence at its album boundary; it is never skipped.
/// </summary>
internal sealed class PlaybackSequence : ISampleProvider
{
    private readonly Func<IReadOnlyList<PlaybackTrackState>> getTracks;
    private readonly bool original;
    private readonly Func<PlaybackLoopState>? getLoops;
    private PlaybackLoopState? observedLoops;
    private long observedDurationFrames;
    private LoopPlaybackPlan loopPlan;
    private bool loopMode;
    private long loopEpochFrame;
    private readonly Dictionary<Guid, LoopTrackReader> loopReaders = new();
    private LoopTrackReader? loopReader;
    private long loopReaderStart, loopReaderEnd;
    private bool resolveLoopArrival = true;
    private readonly ClockEpoch?[] clockHistory = new ClockEpoch?[256];
    private int clockHistoryNext, clockHistoryCount;
    private long clockPublication;
    private sealed record ClockEpoch(long FirstFrame, double StartSeconds, LoopPlaybackPlan Plan);
    private readonly float[] readBuffer = new float[4096];
    private ISampleProvider? currentProvider;
    private PlaybackTrackState? currentTrack;
    private Guid? currentTrackId;
    private int currentIndex;
    private double currentOffset, currentDuration;
    private long emittedFrames;
    private double stopPosition;
    private Guid? pendingTrackId;
    private int pendingLoopSlot = -1;
    private bool terminal;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public PlaybackTrackState? CurrentTrack => Volatile.Read(ref currentTrack);
    public double StartSeconds { get; }
    public double Duration => getTracks().Sum(track => track.Duration);
    public bool IsTerminal => Volatile.Read(ref terminal);
    public bool IsComplete => IsTerminal && pendingTrackId is null;
    public Guid? PendingTrackId => IsTerminal ? pendingTrackId : null;
    internal int PendingLoopSlot => IsTerminal && pendingTrackId != null ? pendingLoopSlot : -1;
    public double StopPositionSeconds => IsTerminal ? stopPosition : PositionAtElapsed(Interlocked.Read(ref emittedFrames) / 48000.0);

    public PlaybackSequence(Func<IReadOnlyList<PlaybackTrackState>> getTracks, bool original, double seconds,
        Func<PlaybackLoopState>? getLoops = null, bool enterLoops = true, int preferredLoopSlot = -1)
    {
        ArgumentNullException.ThrowIfNull(getTracks);
        this.getTracks = getTracks;
        this.original = original;
        this.getLoops = getLoops;
        var tracks = getTracks();
        double duration = tracks.Sum(track => track.Duration);
        double requested = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, duration);
        observedLoops = getLoops?.Invoke() ?? PlaybackLoopState.Empty;
        observedDurationFrames = ToFrame(duration);
        loopPlan = new(observedLoops, observedDurationFrames, ToFrame(requested), enterLoops, preferredLoopSlot);
        loopMode = loopPlan.Active;
        StartSeconds = loopMode ? loopPlan.InitialFrame / 48000.0 : requested;
        RecordClock(0, StartSeconds, loopPlan);
        if (loopMode) { var initial = loopPlan.At(0); OpenLoopAt(initial.Position, initial.Slot); }
        else OpenLegacyAt(StartSeconds);
    }

    private static long ToFrame(double seconds) => checked((long)Math.Round(Math.Max(0, seconds) * 48000));

    private void OpenLegacyAt(double seconds)
    {
        double skip = seconds;
        currentOffset = 0;
        var tracks = getTracks();
        for (int index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (track.Duration > 0 && skip >= track.Duration)
            {
                skip -= track.Duration;
                currentOffset += track.Duration;
                continue;
            }
            currentIndex = index;
            OpenTrack(track, skip);
            return;
        }
        Finish(currentOffset);
    }

    private void RecordClock(long firstFrame, double start, LoopPlaybackPlan plan)
    {
        Volatile.Write(ref clockHistory[clockHistoryNext], new ClockEpoch(firstFrame, start, plan));
        clockHistoryNext = (clockHistoryNext + 1) % clockHistory.Length;
        clockHistoryCount = Math.Min(clockHistoryCount + 1, clockHistory.Length);
        // Publish the two indices as one word only after the complete immutable
        // epoch is visible. UI clock reads can never observe a torn loop plan.
        Volatile.Write(ref clockPublication, ((long)clockHistoryCount << 32) | (uint)clockHistoryNext);
    }

    internal double PositionAtElapsed(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        seconds = Math.Max(0, seconds);
        long frame = (long)Math.Floor(seconds * 48000 + 1e-8);
        if (IsTerminal && frame >= Interlocked.Read(ref emittedFrames)) return stopPosition;
        var epoch = ClockAt(frame);
        if (epoch == null) return StartSeconds;
        double elapsed = Math.Max(0, seconds - epoch.FirstFrame / 48000.0);
        if (!epoch.Plan.Active)
        {
            double end = IsTerminal ? stopPosition : Duration;
            return Math.Clamp(epoch.StartSeconds + elapsed, epoch.StartSeconds, Math.Max(epoch.StartSeconds, end));
        }
        long elapsedFrames = Math.Max(0, frame - epoch.FirstFrame);
        return epoch.Plan.At(elapsedFrames).Position / 48000.0;
    }

    private ClockEpoch? ClockAt(long frame)
    {
        long publication = Volatile.Read(ref clockPublication);
        int next = (int)(uint)publication, count = (int)(publication >> 32);
        ClockEpoch? epoch = Volatile.Read(ref clockHistory[(next - count + clockHistory.Length) % clockHistory.Length]);
        for (int i = 1; i <= count; i++)
        {
            var candidate = Volatile.Read(ref clockHistory[(next - i + clockHistory.Length) % clockHistory.Length]);
            if (candidate != null && candidate.FirstFrame <= frame) { epoch = candidate; break; }
        }
        return epoch;
    }

    internal int LoopSlotAtElapsed(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        if (IsTerminal && seconds >= Interlocked.Read(ref emittedFrames) / 48000.0) return pendingLoopSlot;
        long frame = (long)Math.Floor(Math.Max(0, seconds) * 48000 + 1e-8);
        var epoch = ClockAt(frame);
        return epoch?.Plan.Active == true ? epoch.Plan.At(Math.Max(0, frame - epoch.FirstFrame)).Slot : -1;
    }

    private void SyncLoops()
    {
        if (getLoops == null) return;
        var latest = getLoops() ?? PlaybackLoopState.Empty;
        long duration = ToFrame(Duration);
        if (Equals(latest, observedLoops) && duration == observedDurationFrames) return;
        long emitted = Interlocked.Read(ref emittedFrames);
        var oldSegment = loopPlan.At(Math.Max(0, emitted - loopEpochFrame));
        double current = loopMode ? oldSegment.Position / 48000.0 :
            IsTerminal ? stopPosition : PositionAtElapsed(emitted / 48000.0);
        observedLoops = latest; observedDurationFrames = duration;
        var nextPlan = new LoopPlaybackPlan(latest, duration, ToFrame(current),
            enterLoops: loopMode && oldSegment.Slot >= 0, preferredSlot: oldSegment.Slot);
        if (!loopMode && !nextPlan.Active) { loopPlan = nextPlan; return; }
        loopPlan = nextPlan;
        loopEpochFrame = emitted;
        RecordClock(emitted, current, loopPlan);
        loopMode = loopPlan.Active;
        terminal = false; pendingTrackId = null; pendingLoopSlot = -1; currentProvider = null;
        loopReader = null; resolveLoopArrival = true;
        if (loopMode) { var next = loopPlan.At(0); OpenLoopAt(next.Position, next.Slot); }
        else OpenLegacyAt(current);
    }

    private void OpenLoopAt(long frame, int slot)
    {
        var tracks = getTracks();
        double offset = 0;
        for (int index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            long first = ToFrame(offset), end = ToFrame(offset + track.Duration);
            if (track.Duration > 0 && frame >= end) { offset += track.Duration; continue; }
            if (!track.IsReady(original)) { pendingLoopSlot = slot; Finish(frame / 48000.0, track.Id); return; }
            if (!loopReaders.TryGetValue(track.Id, out var reader) || !Equals(reader.State, track) || reader.AlbumOffset != offset)
                loopReaders[track.Id] = reader = new(track, original, offset);
            loopReader = reader; loopReaderStart = first; loopReaderEnd = end;
            currentIndex = index; currentOffset = offset; currentDuration = track.Duration; currentTrackId = track.Id;
            Volatile.Write(ref currentTrack, track);
            resolveLoopArrival = false;
            return;
        }
        Finish(frame / 48000.0);
    }

    private int ReadLoop(float[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count && !IsTerminal)
        {
            long emitted = Interlocked.Read(ref emittedFrames);
            var segment = loopPlan.At(emitted - loopEpochFrame);
            if (resolveLoopArrival || loopReader == null || segment.Position < loopReaderStart || segment.Position >= loopReaderEnd)
                OpenLoopAt(segment.Position, segment.Slot);
            if (IsTerminal) break;
            int frames = (int)Math.Min(Math.Min((count - read) / 2, readBuffer.Length / 2),
                Math.Min(segment.Remaining, loopReaderEnd - segment.Position));
            if (frames <= 0) { Finish(segment.Position / 48000.0); break; }
            int got = loopReader!.Read(segment.Position, readBuffer, 0, frames);
            if (got <= 0) { Finish(segment.Position / 48000.0); break; }
            for (int frame = 0; frame < got; frame++)
            {
                double gain = loopPlan.EdgeGain(emitted - loopEpochFrame + frame);
                buffer[offset + read + frame * 2] = (float)(readBuffer[frame * 2] * gain);
                buffer[offset + read + frame * 2 + 1] = (float)(readBuffer[frame * 2 + 1] * gain);
            }
            read += got * 2;
            Interlocked.Add(ref emittedFrames, got);
            if (got == segment.Remaining) resolveLoopArrival = true;
        }
        return read;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        count -= count % 2;
        if (count <= 0) return 0;
        SyncLoops();
        if (IsTerminal) return 0;
        if (loopMode) return ReadLoop(buffer, offset, count);
        int read = 0;
        while (read < count && !IsTerminal)
        {
            int requested = Math.Min(count - read, readBuffer.Length);
            // Some NAudio conversion providers clear an unreturned tail at EOF.
            // Keep those writes in bounded, reusable storage and expose only the
            // frames reported as valid. The host can be a byte-backed WaveBuffer,
            // so copying into it must remain indexed rather than Array.Copy.
            int got = currentProvider!.Read(readBuffer, 0, requested);
            if (got < 0 || got > requested || got % WaveFormat.Channels != 0)
                throw new InvalidOperationException("The monitoring source returned an invalid audio frame count.");
            if (got == 0) OpenNextTrack();
            else
            {
                for (int i = 0; i < got; i++) buffer[offset + read + i] = readBuffer[i];
                read += got;
                Interlocked.Add(ref emittedFrames, got / 2);
            }
        }
        return read;
    }

    private void OpenNextTrack()
    {
        // The publisher supplies an immutable array, so this reads no UI objects
        // and takes no UI locks from WASAPI's audio callback.
        var tracks = getTracks();
        int previous = -1;
        for (int index = 0; index < tracks.Count; index++)
            if (tracks[index].Id == currentTrackId) { previous = index; break; }
        double boundary = currentOffset + currentDuration;
        if (previous < 0)
        {
            // Structural removal/reordering normally stops playback in the UI.
            // If it races a callback, stop rather than jump to another track.
            Finish(boundary);
            return;
        }
        int next = previous + 1;
        if (next >= tracks.Count) { Finish(boundary); return; }
        // The UI stops on reorder/removal; appending imported tracks is safe.
        if (previous != currentIndex) { Finish(boundary); return; }
        currentOffset = tracks.Take(next).Sum(track => track.Duration);
        currentIndex = next;
        OpenTrack(tracks[next], 0);
    }

    private void OpenTrack(PlaybackTrackState track, double seconds)
    {
        if (track.SoloBackground is { } solo)
        {
            currentTrackId = track.Id;
            currentDuration = track.Duration;
            currentProvider = new BackgroundSoloSampleProvider(solo, track.Duration, seconds);
            Volatile.Write(ref currentTrack, track);
            return;
        }
        if (track.DeltaRequested)
        {
            if (track.PeakDelta is not { IsValid: true } delta)
            {
                Finish(currentOffset, track.Id);
                return;
            }
            currentTrackId = track.Id;
            currentDuration = track.Duration;
            ISampleProvider deltaProvider = delta.CreateSampleProvider(seconds);
            if (delta.SampleRate != 48000) deltaProvider = new WdlResamplingSampleProvider(deltaProvider, 48000);
            if (delta.Channels == 1)
            {
                float centerGain = (float)Math.Sqrt(.5);
                deltaProvider = new MonoToStereoSampleProvider(deltaProvider) { LeftVolume = centerGain, RightVolume = centerGain };
            }
            currentProvider = deltaProvider;
            Volatile.Write(ref currentTrack, track);
            return;
        }
        if ((original ? track.LiveOriginal : track.LiveResult) is { } live)
        {
            currentTrackId = track.Id;
            currentDuration = track.Duration;
            double monitorGain = original ? track.OriginalAuditionGainDb : track.PreviewAuditionGainDb;
            currentProvider = new LiveSampleProvider(live, currentOffset, seconds, monitorGain);
            Volatile.Write(ref currentTrack, track);
            return;
        }
        var samples = original ? track.OriginalSamples : track.PreviewSamples;
        if (samples is null || samples.Length == 0 || track.BackgroundRequired && track.BackgroundSamples is null)
        {
            Finish(currentOffset, track.Id);
            return;
        }
        if (track.SampleRate <= 0 || track.Channels is not (1 or 2) || samples.Length % track.Channels != 0)
            throw new InvalidOperationException("The playback source has an invalid audio format.");
        if (track.BackgroundSamples is { } background && background.Length != samples.Length)
            throw new InvalidOperationException("The playback background is not aligned to its music source.");
        currentTrackId = track.Id;
        currentDuration = samples.Length / (double)(track.SampleRate * track.Channels);
        double auditionGainDb = original ? track.OriginalAuditionGainDb : track.PreviewAuditionGainDb;
        ISampleProvider provider = new ArraySampleProvider(samples, track.SampleRate, track.Channels, seconds, auditionGainDb,
            original ? track.BackgroundSamples : null);
        if (track.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
        if (track.Channels == 1)
        {
            // Equal-power center routing preserves BS.1770 summed channel energy.
            float centerGain = (float)Math.Sqrt(.5);
            provider = new MonoToStereoSampleProvider(provider) { LeftVolume = centerGain, RightVolume = centerGain };
        }
        currentProvider = provider;
        Volatile.Write(ref currentTrack, track);
    }

    private void Finish(double position, Guid? pending = null)
    {
        currentProvider = null;
        Volatile.Write(ref currentTrack, null);
        stopPosition = position;
        pendingTrackId = pending;
        if (pending == null) pendingLoopSlot = -1;
        // Publish the reason and exact album position before the terminal flag.
        Volatile.Write(ref terminal, true);
    }

    private sealed class BackgroundSoloSampleProvider : ISampleProvider
    {
        private readonly BackgroundRenderer.SegmentReader reader;
        private readonly long endFrame;
        private long frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public BackgroundSoloSampleProvider(BackgroundLayer layer, double duration, double seconds)
        {
            if (!double.IsFinite(duration) || duration < 0 || !double.IsFinite(seconds))
                throw new ArgumentOutOfRangeException(nameof(duration));
            // Solo is a monitor path: it can audition a disabled bed without
            // changing the layer's inclusion in the final mix or its saved state.
            reader = new BackgroundRenderer.SegmentReader(layer with { Enabled = true }, 48000, 2);
            endFrame = checked((long)Math.Round(duration * 48000));
            frame = Math.Clamp((long)Math.Floor(Math.Max(0, seconds) * 48000), 0, endFrame);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = (int)Math.Min(count / 2, endFrame - frame);
            if (frames <= 0) return 0;
            reader.Read(frame / 48000.0, buffer, offset, frames);
            frame += frames;
            return frames * 2;
        }
    }

    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] samples;
        private readonly float[]? background;
        private readonly float auditionGain;
        private int position;
        public WaveFormat WaveFormat { get; }

        public ArraySampleProvider(float[] samples, int sampleRate, int channels, double seconds, double auditionGainDb,
            float[]? background = null)
        {
            this.samples = samples;
            this.background = background;
            // Audition matching can only attenuate. Invalid metadata bypasses
            // the monitor adjustment; it must never introduce NaNs or gain.
            double db = double.IsFinite(auditionGainDb) ? Math.Clamp(auditionGainDb, -120, 0) : 0;
            auditionGain = db == 0 ? 1 : (float)Math.Pow(10, db / 20);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            position = (int)Math.Clamp(Math.Floor(seconds * sampleRate) * channels, 0, samples.Length);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            count = Math.Min(count, samples.Length - position);
            count -= count % WaveFormat.Channels;
            // NAudio's float[] callback can alias a byte[] through WaveBuffer.
            // Indexed writes work on that view; Array.Copy rejects its type.
            if (background != null)
                for (int i = 0; i < count; i++) buffer[offset + i] = (samples[position + i] + background[position + i]) * auditionGain;
            else if (auditionGain == 1)
                for (int i = 0; i < count; i++) buffer[offset + i] = samples[position + i];
            else
                for (int i = 0; i < count; i++) buffer[offset + i] = samples[position + i] * auditionGain;
            position += count;
            return count;
        }
    }

    private sealed class LiveSampleProvider : ISampleProvider
    {
        private readonly LiveAudioReader reader;
        private readonly float auditionGain;
        private long frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        internal LiveSampleProvider(LiveAudioSource source, double timelineOffset, double seconds, double auditionGainDb)
        {
            reader = source.CreateReader(48000, 2);
            // Music-only states can retain native fractional durations while
            // the output uses integer stereo frames. Seek on the album clock,
            // rather than flooring each side of a structural cut separately.
            long first = checked((long)Math.Round(timelineOffset * 48000));
            long target = checked((long)Math.Floor((timelineOffset + Math.Max(0, seconds)) * 48000 + 1e-8));
            frame = Math.Clamp(target - first, 0, reader.FrameCount);
            double db = double.IsFinite(auditionGainDb) ? Math.Clamp(auditionGainDb, -120, 0) : 0;
            auditionGain = db == 0 ? 1 : (float)Math.Pow(10, db / 20);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int frames = reader.Read(frame, buffer, offset, count / 2);
            int samples = frames * 2;
            if (auditionGain != 1)
                for (int i = 0; i < samples; i++) buffer[offset + i] *= auditionGain;
            frame += frames;
            return samples;
        }
    }
}

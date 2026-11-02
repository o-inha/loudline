namespace Multiloud.Core;

/// <summary>Joins source regions; processing and the project envelope remain separate.</summary>
public static class AudioTrackMerger
{
    public static bool CanShare(AudioTrack left, AudioTrack right) =>
        left.Original.Length > 0 && ReferenceEquals(left.Original, right.Original) &&
        left.SampleRate == right.SampleRate && left.Channels == right.Channels &&
        left.OriginalFrameOffset + left.OriginalFrameCount == right.OriginalFrameOffset &&
        left.InputGainDb == right.InputGainDb && left.TrimDb == right.TrimDb &&
        left.MergedSourceId == right.MergedSourceId &&
        string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);

    public static AudioTrack CreateSlot(AudioTrack left, AudioTrack right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        if (left.Duration <= 0 || right.Duration <= 0) throw new ArgumentException("Both tracks must contain audio before merging.");
        bool shared = CanShare(left, right);
        bool sameFormat = left.SampleRate == right.SampleRate && left.Channels == right.Channels;
        double duration = shared ? (left.OriginalFrameCount + right.OriginalFrameCount) / (double)left.SampleRate
            : left.Duration + right.Duration;
        var track = new AudioTrack
        {
            FilePath = shared ? left.FilePath : Path.GetFileNameWithoutExtension(left.Name) + " + " + right.Name,
            GeneratedSilenceSeconds = shared ? left.GeneratedSilenceSeconds : null,
            MergedSourceId = shared ? left.MergedSourceId : Guid.NewGuid(),
            MergedSources = shared ? left.MergedSources : Parts(left).Concat(Parts(right)).ToArray(),
            SourceStartSeconds = shared ? left.SourceStartSeconds : 0,
            SourceDurationSeconds = duration, SavedDuration = duration,
            SampleRate = shared || sameFormat ? left.SampleRate : 48000,
            Channels = shared || sameFormat ? left.Channels : 2,
            Original = shared ? left.Original : [],
            OriginalFrameOffset = shared ? left.OriginalFrameOffset : 0,
            OriginalFrameLength = shared ? left.OriginalFrameCount + right.OriginalFrameCount : null,
            InputGainDb = shared ? left.InputGainDb : 0, TrimDb = shared ? left.TrimDb : 0,
            TargetLufs = left.TargetLufs == right.TargetLufs ? left.TargetLufs : null,
            Envelope = JoinCurve(left.Envelope, right.Envelope, left.Duration),
            TransitionEnvelope = JoinCurve(left.TransitionEnvelope, right.TransitionEnvelope, left.Duration),
            OriginalPeaks = left.OriginalPeaks.Concat(right.OriginalPeaks).ToList(),
            WorkState = TrackWorkState.Queued, Status = shared ? "Queued for preview" : "Preparing merged source…"
        };
        if (track.OriginalPeaks.Count > 16000)
        {
            var peaks = track.OriginalPeaks;
            track.OriginalPeaks = Enumerable.Range(0, 16000).Select(index => peaks[(int)((long)index * peaks.Count / 16000)]).ToList();
        }
        if (shared && left.OriginalWaveform?.SharedSummaryRoot is { } root)
            track.OriginalWaveform = root.Slice(track.OriginalFrameOffset, track.OriginalFrameOffset + track.OriginalFrameCount);
        return track;
    }

    public static void Materialize(AudioTrack track, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (track.Original.Length > 0) return;
        if (track.MergedSources is not { Count: > 0 } parts) throw new ArgumentException("The merged source has no reconstruction recipe.");
        double total = parts.Sum(part => part.Source.Duration);
        long frameCount = (long)Math.Round(total * track.SampleRate);
        long sampleCount = checked(frameCount * track.Channels);
        if (sampleCount is <= 0 or > 536000000) throw new InvalidOperationException("The merged source exceeds the supported audio size.");
        var output = new float[(int)sampleCount];
        double album = 0;
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            var source = part.Source;
            if (!double.IsFinite(part.GainDb) || part.GainDb is < -132 or > 132)
                throw new ArgumentException("The accumulated manual gain exceeds the supported merge range.");
            double gain = Math.Pow(10, part.GainDb / 20);
            var reader = new LiveAudioSource(source.Original, source.SampleRate, source.Channels,
                source.OriginalFrameOffset, source.OriginalFrameCount, album).CreateReader(track.SampleRate, track.Channels);
            long first = reader.FirstOutputFrame;
            for (long frame = 0; frame < reader.FrameCount;)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(4096, reader.FrameCount - frame);
                int got = reader.Read(frame, output, checked((int)((first + frame) * track.Channels)), count);
                if (got <= 0) throw new InvalidOperationException("A merged source ended unexpectedly.");
                if (gain != 1)
                    for (int sample = 0; sample < got * track.Channels; sample++)
                    {
                        int at = checked((int)((first + frame) * track.Channels + sample));
                        output[at] = (float)(output[at] * gain);
                        if (!float.IsFinite(output[at])) throw new ArgumentException("The merged gain produces invalid samples.");
                    }
                frame += got;
            }
            album += source.Duration;
        }
        var waveform = WaveformPyramid.Build(output, track.Channels, ct);
        var peaks = AudioProcessor.BuildPeaks(output, track.Channels);
        ct.ThrowIfCancellationRequested();
        track.Original = output;
        track.OriginalFrameOffset = 0; track.OriginalFrameLength = null;
        track.SavedDuration = track.Duration;
        track.OriginalWaveform = waveform;
        track.OriginalPeaks = peaks;
    }

    private static IEnumerable<MergedSourcePart> Parts(AudioTrack source)
    {
        double extra = source.InputGainDb + source.TrimDb;
        if (source.MergedSources is not { Count: > 0 } parts)
        { yield return new(CloneRange(source, 0, source.Duration), extra); yield break; }
        double first = source.SourceStartSeconds, end = first + source.Duration, offset = 0;
        foreach (var part in parts)
        {
            double last = offset + part.Source.Duration;
            double a = Math.Max(first, offset), b = Math.Min(end, last);
            if (b > a) yield return new(CloneRange(part.Source, a - offset, b - a), part.GainDb + extra);
            offset = last;
        }
    }

    private static AudioTrack CloneRange(AudioTrack source, double local, double duration)
    {
        int first = (int)Math.Round(local * source.SampleRate), count = (int)Math.Round(duration * source.SampleRate);
        return new AudioTrack
        {
            FilePath = source.FilePath, MissingAbsolutePath = source.MissingAbsolutePath,
            GeneratedSilenceSeconds = source.GeneratedSilenceSeconds,
            SourceStartSeconds = source.SourceStartSeconds + local, SourceDurationSeconds = duration,
            SavedDuration = duration, SampleRate = source.SampleRate, Channels = source.Channels,
            Original = source.Original, OriginalFrameOffset = source.OriginalFrameOffset + first,
            OriginalFrameLength = count, OriginalWaveform = source.OriginalWaveform?.Slice(first, first + count),
            OriginalPeaks = source.OriginalPeaks, Analysis = source.Analysis,
        };
    }

    private static List<EnvelopePoint> JoinCurve(IReadOnlyList<EnvelopePoint> left, IReadOnlyList<EnvelopePoint> right, double at)
        => left.Concat(right.Select(point => point with { Time = point.Time + at }))
            .GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
}

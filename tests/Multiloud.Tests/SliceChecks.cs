using Multiloud.Core;

internal static class SliceChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Slices preserve every native frame exactly once and keep independent identities", ExactFrames),
        ("Repeated slices retain absolute source ranges for a lossless reopen", RepeatedRanges),
        ("Shared slices keep native frame ranges without copying source PCM", SharedRanges),
        ("Nested shared slices flatten offsets and materialize only their own region", NestedSharedRanges),
        ("Shared slicing and materialization validate bounds and cancellation", SharedRangeBounds),
        ("Slice slots expose exact waveform ranges without copying or analyzing PCM", ImmediateSlots),
        ("Slice envelope cropping preserves the original continuous gain function", EnvelopeCrop),
        ("Slice source extraction handles sample-rate changes and shorter relinks", SourceExtraction),
        ("Slicing cancels before publication and rejects source-edge cuts", CancellationAndBounds),
        ("Each slice receives independent loudness matching", IndependentMatching)
    ];

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static AudioTrack Source(int frames = 200, int channels = 2, int rate = 8000) => new()
    {
        FilePath = "untouched.wav", SampleRate = rate, Channels = channels,
        Original = Enumerable.Range(0, frames * channels).Select(index => index / 1000f).ToArray(),
        InputGainDb = 2, TrimDb = -1, TargetLufs = -18,
        Envelope = [new(0, -2), new(frames / (double)rate, 4)]
    };

    private static void ExactFrames()
    {
        var source = Source();
        var (left, right) = AudioTrackSlicer.Split(source, 73.2 / source.SampleRate);
        Require(left.Original.Length == 146 && right.Original.Length == 254, "The cut was not rounded to a native frame.");
        Require(left.Original.Concat(right.Original).SequenceEqual(source.Original), "Samples were lost, repeated or modified at the split.");
        Require(left.Id != right.Id && left.SourceId != right.SourceId && left.SourceId != source.SourceId,
            "Slices must own independent analysis/render identities.");
        foreach (var piece in new[] { left, right })
            Require(piece.InputGainDb == 2 && piece.TrimDb == -1 && piece.TargetLufs == -18 && piece.FilePath == source.FilePath &&
                piece.Analysis == null && piece.Render == null, "A slice lost authored controls or reused whole-source analysis.");
    }

    private static void RepeatedRanges()
    {
        var original = Source(1000, 1, 44100);
        var (_, right) = AudioTrackSlicer.Split(original, 123.0 / 44100);
        var (middle, end) = AudioTrackSlicer.Split(right, 217.0 / 44100);
        foreach (var piece in new[] { middle, end })
        {
            var reopened = AudioTrackSlicer.ExtractSource(original.Original, 44100, 1, piece.SourceStartSeconds, piece.SourceDurationSeconds);
            Require(reopened.SequenceEqual(piece.Original), "Repeated slice source ranges drifted from their native frames.");
        }
        Require(Math.Abs(end.SourceStartSeconds * 44100 - 340) < 1e-10, "Repeated slice forgot the original source offset.");
    }

    private static void SharedRanges()
    {
        var source = Source(16391, 2, 12000);
        source.OriginalWaveform = WaveformPyramid.Build(source.Original, source.Channels);
        source.OriginalPeaks = AudioProcessor.BuildPeaks(source.Original, source.Channels);
        var (left, right) = AudioTrackSlicer.SplitShared(source, 713.0 / source.SampleRate);
        Require(ReferenceEquals(left.Original, source.Original) && ReferenceEquals(right.Original, source.Original),
            "A shared slice copied its immutable source array.");
        Require(left.OriginalFrameOffset == 0 && left.OriginalFrameCount == 713 &&
            right.OriginalFrameOffset == 713 && right.OriginalFrameCount == 15678 &&
            left.OriginalSampleOffset == 0 && right.OriginalSampleOffset == 1426 &&
            left.OriginalSampleCount + right.OriginalSampleCount == source.Original.Length,
            "Shared slice offsets/counts do not partition native frames exactly.");
        Require(left.OriginalRegion.Span.SequenceEqual(source.Original.AsSpan(0, 1426)) &&
            right.OriginalRegion.Span.SequenceEqual(source.Original.AsSpan(1426)) &&
            Math.Abs(left.Duration + right.Duration - source.Duration) < 1e-12,
            "Shared regions changed source samples or duration.");
        Require(left.OriginalWaveform is { Work.RawFrames: 0, FrameCount: 713 } &&
            right.OriginalWaveform?.Query(0, 1) == source.OriginalWaveform.Query(713, 714) &&
            left.Analysis == null && right.Analysis == null && left.Render == null && right.Render == null &&
            left.SourceId != source.SourceId && left.SourceId != right.SourceId,
            "Shared slices scanned audio, reused whole-track measurements or lost independent identities.");
        Require(ReferenceEquals(source.GetOriginalRegion(), source.Original), "Whole-source materialization duplicated PCM.");
    }

    private static void NestedSharedRanges()
    {
        var source = Source(1000, 2, 44100);
        source.SourceStartSeconds = 2.5;
        source.OriginalWaveform = WaveformPyramid.Build(source.Original, source.Channels);
        var (_, right) = AudioTrackSlicer.SplitShared(source, 123.0 / 44100);
        var (middle, end) = AudioTrackSlicer.SplitShared(right, 217.0 / 44100);
        Require(ReferenceEquals(middle.Original, source.Original) && ReferenceEquals(end.Original, source.Original) &&
            middle.OriginalFrameOffset == 123 && middle.OriginalFrameCount == 217 &&
            end.OriginalFrameOffset == 340 && end.OriginalFrameCount == 660,
            "Repeated shared slicing copied PCM or accumulated an incorrect frame offset.");
        Require(Math.Abs(end.SourceStartSeconds - (2.5 + 340.0 / 44100)) < 1e-12 &&
            end.SourceDurationSeconds == 660.0 / 44100 &&
            end.OriginalWaveform?.Query(0, 1) == source.OriginalWaveform.Query(340, 341),
            "File-time ranges or waveform views drifted after repeated shared cuts.");
        var materialized = middle.GetOriginalRegion();
        Require(materialized.Length == 434 && materialized.SequenceEqual(source.Original.Skip(246).Take(434)),
            "Materialization copied the root rather than only the selected shared region.");
        Require(ReferenceEquals(materialized, middle.GetOriginalRegion()) &&
            middle.RetainedRegionBytes == materialized.LongLength * sizeof(float) &&
            ReferenceEquals(middle.Original, source.Original) && middle.OriginalFrameOffset == 123,
            "Repeated processing failed to reuse its region array or replaced the immutable root range.");
        middle.OriginalFrameOffset = 124;
        Require(middle.RetainedRegionBytes == 0 && !ReferenceEquals(materialized, middle.GetOriginalRegion()) &&
            middle.GetOriginalRegion().SequenceEqual(source.Original.Skip(248).Take(434)),
            "Changing a source range reused or retained its previous materialized region.");
        var (copyLeft, copyRight) = AudioTrackSlicer.Split(end, 11.0 / 44100);
        Require(copyLeft.Original.Length == 22 && copyRight.Original.Length == 1298 &&
            copyLeft.Original.Concat(copyRight.Original).SequenceEqual(source.Original.Skip(680)) &&
            copyLeft.OriginalFrameOffset == 0 && copyLeft.OriginalFrameLength == null,
            "Copying split did not respect a shared parent's local region.");
    }

    private static void SharedRangeBounds()
    {
        var source = Source();
        var (_, right) = AudioTrackSlicer.SplitShared(source, 73.0 / source.SampleRate);
        foreach (double position in new[] { 0d, right.Duration, right.Duration + .1, double.NaN })
        {
            bool rejected = false;
            try { AudioTrackSlicer.SplitShared(right, position); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Shared slicing accepted a cut outside its local range.");
        }
        using var cts = new CancellationTokenSource(); cts.Cancel();
        bool splitCancelled = false, copyCancelled = false;
        try { AudioTrackSlicer.SplitShared(right, right.Duration / 2, cts.Token); }
        catch (OperationCanceledException) { splitCancelled = true; }
        try { right.GetOriginalRegion(cts.Token); } catch (OperationCanceledException) { copyCancelled = true; }
        Require(splitCancelled && copyCancelled, "Shared split/materialization ignored cancellation.");
        var invalid = Source(); invalid.OriginalFrameOffset = 150; invalid.OriginalFrameLength = 100;
        bool invalidSplit = false, invalidCopy = false;
        try { AudioTrackSlicer.SplitShared(invalid, 1.0 / invalid.SampleRate); }
        catch (ArgumentException) { invalidSplit = true; }
        try { invalid.GetOriginalRegion(); } catch (InvalidOperationException) { invalidCopy = true; }
        Require(invalidSplit && invalidCopy, "An out-of-root shared region was accepted.");
    }

    private static void ImmediateSlots()
    {
        var source = Source(16391, 2, 12000);
        source.OriginalWaveform = WaveformPyramid.Build(source.Original, source.Channels);
        source.OriginalPeaks = AudioProcessor.BuildPeaks(source.Original, source.Channels);
        var (left, right) = AudioTrackSlicer.CreateSplitSlots(source, 713.0 / source.SampleRate);
        Require(left.Original.Length == 0 && right.Original.Length == 0 && left.Analysis == null && right.Analysis == null,
            "Publishing slice slots copied or analyzed audio");
        Require(left.Duration == 713.0 / source.SampleRate &&
            Math.Abs(left.Duration + right.Duration - source.Duration) < 1e-12 &&
            right.SourceStartSeconds == source.SourceStartSeconds + left.Duration,
            "Immediate slice slots changed the timeline/source positions");
        Require(left.OriginalWaveform is { Work.RawFrames: 0, FrameCount: 713 } &&
            right.OriginalWaveform is { Work.RawFrames: 0 } &&
            left.OriginalPeaks.Count > 0 && right.OriginalPeaks.Count > 0 &&
            right.OriginalWaveform.Query(0, 1) == source.OriginalWaveform.Query(713, 714),
            "Immediate slices did not share exact existing waveform summaries");
    }

    private static void EnvelopeCrop()
    {
        var source = Source(8000, 1);
        source.Envelope = [new(0, -2), new(.2, 4), new(.8, -3), new(1, 2)];
        source.TransitionEnvelope = [new(0, .7), new(.5, 0), new(1, -.2)];
        var (left, right) = AudioTrackSlicer.Split(source, .413);
        for (int i = 0; i <= 100; i++)
        {
            double time = i / 100.0;
            var piece = time <= left.Duration ? left : right;
            double local = time <= left.Duration ? time : time - left.Duration;
            Require(Math.Abs(AudioProcessor.EnvelopeGainAt(piece.Envelope, local) - AudioProcessor.EnvelopeGainAt(source.Envelope, time)) < 1e-10,
                "Slice changed the authored envelope between control points.");
            Require(Math.Abs(AudioProcessor.EnvelopeGainAt(piece.TransitionEnvelope, local) - AudioProcessor.EnvelopeGainAt(source.TransitionEnvelope, time)) < 1e-10,
                "Slice changed legacy transition corrections.");
        }
    }

    private static void SourceExtraction()
    {
        var source = Source(16000, 1, 16000);
        var result = AudioTrackSlicer.ExtractSource(source.Original, 16000, 1, .25, .5);
        Require(result.SequenceEqual(source.Original.Skip(4000).Take(8000)), "Saved time range did not follow the replacement sample rate.");
        var shorter = AudioTrackSlicer.ExtractSource(source.Original, 16000, 1, .75, .5);
        Require(shorter.SequenceEqual(source.Original.Skip(12000)), "A shorter replacement was not safely clamped.");
        bool rejected = false;
        try { AudioTrackSlicer.ExtractSource(source.Original, 16000, 1, 1, .1); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "An empty region beyond the replacement source was accepted.");
        Require(ReferenceEquals(source.Original, AudioTrackSlicer.ExtractSource(source.Original, 16000, 1, 0, null)), "Whole-source loading should not duplicate PCM.");
    }

    private static void CancellationAndBounds()
    {
        var source = Source();
        foreach (double position in new[] { 0d, source.Duration, -1, double.NaN })
        {
            bool rejected = false;
            try { AudioTrackSlicer.Split(source, position); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "An invalid or source-edge slice was accepted.");
        }
        using var cts = new CancellationTokenSource(); cts.Cancel();
        bool canceled = false;
        try { AudioTrackSlicer.Split(source, source.Duration / 2, cts.Token); } catch (OperationCanceledException) { canceled = true; }
        Require(canceled && source.Original.Length == 400 && source.SourceDurationSeconds == null,
            "Cancelled slicing mutated its original source.");
    }

    private static void IndependentMatching()
    {
        const int rate = 8000;
        var source = new AudioTrack { SampleRate = rate, Channels = 1, Original = new float[rate * 4] };
        for (int i = 0; i < source.Original.Length; i++) source.Original[i] = (float)(Math.Sin(2 * Math.PI * 1000 * i / rate) * (i < rate * 2 ? .02 : .2));
        var (left, right) = AudioTrackSlicer.Split(source, 2);
        var settings = new ProcessingSettings { LevelingEnabled = false, LimiterEnabled = false, TargetLufs = -20 };
        var a = AudioProcessor.Render(left.Original, rate, 1, [], 0, settings);
        var b = AudioProcessor.Render(right.Original, rate, 1, [], 0, settings);
        Require(Math.Abs(a.OutputLufs + 20) < .1 && Math.Abs(b.OutputLufs + 20) < .1 && Math.Abs(a.MatchGainDb - b.MatchGainDb - 20) < .1,
            "Slices were matched as one shared whole-file loudness entity.");
    }
}

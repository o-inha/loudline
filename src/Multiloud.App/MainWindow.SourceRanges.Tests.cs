using System.IO;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifySharedProjectRangesSmokeAsync()
    {
        const int rate = 8000;
        string directory = Path.Combine(Path.GetTempPath(), "Loudline-source-ranges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool oldCacheEnabled = _cacheProjectSourceDecodes;
        var oldCache = _cachedProjectSource;
        var oldRoots = _decodedProjectRoots.ToArray(); var oldMergedRoots = _mergedProjectRoots.ToArray();
        try
        {
            string path = Path.Combine(directory, "shared.wav");
            string otherPath = Path.Combine(directory, "other.wav");
            var samples = Enumerable.Range(0, rate * 3).Select(index => (float)(.04 * Math.Sin(index * .17))).ToArray();
            await Task.Run(() => AudioFileService.ExportWav(new AudioTrack
            {
                FilePath = path + ".source", SampleRate = rate, Channels = 1,
                Render = new RenderResult { Samples = samples }
            }, path, true));
            File.Copy(path, otherPath);
            BeginProjectSourceDecodeCache();
            var decoded = await Task.Run(() =>
            {
                // The first caller crops its descriptor before any subsequent
                // read. The operation cache must retain the untouched root.
                var left = DecodeProjectSource(path, default);
                SelectDecodedProjectRange(left, .25, .75, default);
                _ = DecodeProjectSource(otherPath, default);
                var full = DecodeProjectSource(path, default);
                var right = DecodeProjectSource(path, default);
                SelectDecodedProjectRange(right, 1, 2.5, default);
                return (Full: full, Left: left, Right: right);
            });
            void RequireRange(bool condition, string message)
            { if (!condition) throw new InvalidOperationException("Smoke test: " + message); }
            RequireRange(ReferenceEquals(decoded.Full.Original, decoded.Left.Original) &&
                ReferenceEquals(decoded.Full.Original, decoded.Right.Original) &&
                decoded.Left.OriginalFrameOffset == 2000 && decoded.Left.OriginalFrameCount == 6000 &&
                decoded.Right.OriginalFrameOffset == 8000 && decoded.Right.OriginalFrameCount == 16000,
                "Project source ranges copied PCM, lost saved offsets or failed to clamp a shorter relink.");
            RequireRange(ReferenceEquals(decoded.Left.OriginalWaveform!.SharedSummaryRoot, decoded.Full.OriginalWaveform) &&
                ReferenceEquals(decoded.Right.OriginalWaveform!.SharedSummaryRoot, decoded.Full.OriginalWaveform) &&
                decoded.Left.OriginalWaveform.Work.RawFrames == 0 && decoded.Right.OriginalWaveform.Work.RawFrames == 0 &&
                decoded.Left.Analysis == null && decoded.Right.Analysis == null &&
                decoded.Left.RetainedRegionBytes == 0 && decoded.Right.RetainedRegionBytes == 0,
                "Opening saved ranges rebuilt source waveform resolutions, analyzed audio or materialized processor buffers.");
            RequireRange(decoded.Left.OriginalRegion.Span.SequenceEqual(samples.AsSpan(2000, 6000)) &&
                decoded.Right.OriginalRegion.Span.SequenceEqual(samples.AsSpan(8000, 16000)),
                "Opening saved ranges changed their native source samples.");

            var pieces = new List<AudioTrack> { decoded.Full };
            for (int i = 0; i < 7; i++)
            {
                var last = pieces[^1]; var split = AudioTrackSlicer.SplitShared(last, last.Duration / 2);
                pieces.RemoveAt(pieces.Count - 1); pieces.Add(split.Left); pieces.Add(split.Right);
            }
            long whole = EstimateWorkingAudioBytes([decoded.Full], [], false);
            long sliced = EstimateWorkingAudioBytes(pieces, [], false);
            var backgroundView = new AudioTrack { Original = decoded.Full.Original, SampleRate = rate, Channels = 1,
                OriginalWaveform = decoded.Full.OriginalWaveform };
            RequireRange(sliced < whole + samples.LongLength * sizeof(float) &&
                EstimateWorkingAudioBytes(pieces, [backgroundView], false) < sliced + samples.LongLength * sizeof(float),
                "The working audio guard charged a shared root or root pyramid once per slice/lane.");
            long processedWhole = EstimateWorkingAudioBytes([decoded.Full], [], true);
            long processedSlices = EstimateWorkingAudioBytes(pieces, [], true);
            RequireRange(processedSlices < processedWhole + samples.LongLength * sizeof(float),
                "The processing memory reserve multiplied a full root for each sliced region.");

            var analysis = await Task.Run(() => LoudnessAnalyzer.Analyze(decoded.Left.GetOriginalRegion(), rate, 1));
            RequireRange(double.IsFinite(analysis.IntegratedLufs) &&
                ReferenceEquals(decoded.Left.Original, decoded.Full.Original) && decoded.Left.OriginalFrameOffset == 2000 &&
                decoded.Left.RetainedRegionBytes == 6000L * sizeof(float),
                "Processing a saved slice replaced the immutable root or retained an incorrectly sized region.");
            long materialized = EstimateWorkingAudioBytes([decoded.Left], [], false);
            RequireRange(materialized >= samples.LongLength * sizeof(float) + decoded.Left.RetainedRegionBytes,
                "The memory guard omitted a materialized processor region.");
            MarkUnavailable(decoded.Right);
            RequireRange(decoded.Right.Original.Length == 0 && decoded.Right.OriginalFrameOffset == 0 &&
                decoded.Right.OriginalFrameLength == null && decoded.Right.Duration == 2 &&
                decoded.Right.WorkState == TrackWorkState.Missing,
                "Missing-source publication retained an obsolete root range or lost its timeline duration.");
        }
        finally
        {
            _decodedProjectRoots.Clear(); foreach (var pair in oldRoots) _decodedProjectRoots[pair.Key] = pair.Value;
            _mergedProjectRoots.Clear(); foreach (var pair in oldMergedRoots) _mergedProjectRoots[pair.Key] = pair.Value;
            _cacheProjectSourceDecodes = oldCacheEnabled; _cachedProjectSource = oldCache;
            Directory.Delete(directory, true);
        }
    }
}

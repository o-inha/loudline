using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Multiloud.Core;

// Optional, hardware-independent query benchmark. The Windows desktop smoke
// separately measures retained WPF drawings and frame pacing.
const int trackCount = 20, sampleRate = 48000, channels = 2, columns = 3840;
int seconds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 30;
int repetitions = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
if (seconds is < 1 or > 300 || repetitions is < 1 or > 50)
    throw new ArgumentOutOfRangeException(nameof(args), "Use 1–300 seconds per track and 1–50 repetitions.");

var setup = Stopwatch.StartNew();
var tracks = Enumerable.Range(0, trackCount).Select(index => Fixture.Create(index, seconds, sampleRate, channels)).ToArray();
setup.Stop();
Console.WriteLine($"20-track, 48 kHz stereo, 3840-column Comparison query workload; {seconds}s/track, {repetitions} timed repetitions.");
Console.WriteLine($"Setup {setup.Elapsed.TotalSeconds:F2}s; PCM {tracks.Sum(t => t.Original.LongLength + t.Preview.LongLength) * sizeof(float):N0} bytes; pyramid {tracks.Sum(t => t.OriginalPyramid.RetainedBytes + t.PreviewPyramid.RetainedBytes):N0} bytes.");
Console.WriteLine("Legacy = previous 16,000-bin/detail-loop queries; raw = exact direct PCM scan. Excludes WPF geometry, layout and GPU composition.");

double sink = 0;
foreach (double fraction in new[] { 1.0, .25, .05, .005 })
{
    var spans = MakeSpans(tracks.Length, seconds, sampleRate, fraction, columns);
    long rawFrames = 0, summaryNodes = 0;
    int differingLegacyQueries = 0;
    foreach (var span in spans)
    {
        var track = tracks[span.Track];
        foreach (bool preview in new[] { false, true })
        {
            var samples = preview ? track.Preview : track.Original;
            var pyramid = preview ? track.PreviewPyramid : track.OriginalPyramid;
            var exact = Raw(samples, span.First, span.End, channels);
            var actual = pyramid.Query(span.First, span.End, out var work);
            if (actual.Min != exact.Min || actual.Max != exact.Max)
                throw new InvalidOperationException($"Pyramid differs from exact PCM at fraction {fraction}, track {span.Track}, frames {span.First}–{span.End}.");
            var legacy = Legacy(track, preview, span.First, span.End, channels);
            if (legacy.Min != exact.Min || legacy.Max != exact.Max) differingLegacyQueries++;
            rawFrames += work.RawFrames;
            summaryNodes += work.SummaryNodes;
        }
    }

    double Run(int mode)
    {
        double sum = 0;
        foreach (var span in spans)
        {
            var track = tracks[span.Track];
            for (int source = 0; source < 2; source++)
            {
                bool preview = source == 1;
                var peak = mode switch
                {
                    0 => Raw(preview ? track.Preview : track.Original, span.First, span.End, channels),
                    1 => Legacy(track, preview, span.First, span.End, channels),
                    _ => (preview ? track.PreviewPyramid : track.OriginalPyramid).Query(span.First, span.End),
                };
                sum += peak.Min + peak.Max;
            }
        }
        return sum;
    }

    // Warm all paths before measuring medians; retain checksums to consume output.
    for (int mode = 0; mode < 3; mode++) sink += Run(mode);
    var timings = new double[3][];
    long allocatedBytes = 0;
    for (int mode = 0; mode < 3; mode++)
    {
        timings[mode] = new double[repetitions];
        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            sink += Run(mode);
            timings[mode][iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (mode == 2) allocatedBytes = Math.Max(allocatedBytes, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Array.Sort(timings[mode]);
    }
    double rawMs = timings[0][repetitions / 2], legacyMs = timings[1][repetitions / 2], pyramidMs = timings[2][repetitions / 2];
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        viewportFraction = fraction,
        columns,
        comparisonQueries = spans.Count * 2,
        visibleTracks = spans.Select(span => span.Track).Distinct().Count(),
        exactRawMedianMs = rawMs,
        legacyMedianMs = legacyMs,
        pyramidMedianMs = pyramidMs,
        speedupOverRaw = rawMs / Math.Max(.000001, pyramidMs),
        speedupOverLegacy = legacyMs / Math.Max(.000001, pyramidMs),
        pyramidRawFrames = rawFrames,
        pyramidSummaryNodes = summaryNodes,
        exactRawFrames = spans.Sum(span => (long)span.End - span.First) * 2,
        differingLegacyQueries,
        maximumPyramidPassAllocatedBytes = allocatedBytes,
    }));
}
Console.WriteLine($"Checksum {sink:R}");

static List<QuerySpan> MakeSpans(int count, int seconds, int rate, double fraction, int width)
{
    double total = count * (double)seconds, duration = total * fraction, start = (total - duration) * .413;
    var result = new List<QuerySpan>(width + count);
    for (int x = 0; x < width; x++)
    {
        double a = start + duration * x / width, b = start + duration * (x + 1) / width;
        int first = Math.Min(count - 1, (int)(a / seconds)), last = Math.Min(count - 1, (int)(Math.BitDecrement(b) / seconds));
        for (int track = first; track <= last; track++)
        {
            int from = Math.Clamp((int)Math.Floor((a - track * seconds) * rate), 0, seconds * rate);
            int to = Math.Clamp((int)Math.Ceiling((b - track * seconds) * rate), from, seconds * rate);
            if (from != to) result.Add(new(track, from, to));
        }
    }
    return result;
}

static WaveformExtrema Raw(float[] samples, int firstFrame, int endFrame, int channelCount)
{
    if (firstFrame >= endFrame) return default;
    float min = float.PositiveInfinity, max = float.NegativeInfinity;
    for (int sample = firstFrame * channelCount; sample < endFrame * channelCount; sample++)
    {
        min = Math.Min(min, samples[sample]); max = Math.Max(max, samples[sample]);
    }
    return new(min, max);
}

static WaveformExtrema Legacy(Fixture track, bool preview, int firstFrame, int endFrame, int channelCount)
{
    var samples = preview ? track.Preview : track.Original;
    if ((endFrame - firstFrame) * channelCount < 2048)
    {
        var peak = Raw(samples, firstFrame, endFrame, channelCount);
        return new(Math.Min(0, peak.Min), Math.Max(0, peak.Max));
    }
    var bins = preview ? track.PreviewBins : track.OriginalBins;
    int frameCount = samples.Length / channelCount;
    int first = Math.Clamp((int)(firstFrame / (double)frameCount * bins.Count), 0, bins.Count - 1);
    int last = Math.Clamp((int)Math.Ceiling(endFrame / (double)frameCount * bins.Count), first + 1, bins.Count);
    float min = 0, max = 0;
    for (int bin = first; bin < last; bin++) { min = Math.Min(min, bins[bin].Min); max = Math.Max(max, bins[bin].Max); }
    return new(min, max);
}

readonly record struct QuerySpan(int Track, int First, int End);

sealed record Fixture(float[] Original, float[] Preview, List<PeakBin> OriginalBins, List<PeakBin> PreviewBins,
    WaveformPyramid OriginalPyramid, WaveformPyramid PreviewPyramid)
{
    public static Fixture Create(int index, int seconds, int rate, int channelCount)
    {
        var original = new float[checked(seconds * rate * channelCount)];
        var preview = new float[original.Length];
        uint random = (uint)(index + 17);
        for (int frame = 0; frame < original.Length / channelCount; frame++)
        {
            random = unchecked(random * 1664525 + 1013904223);
            float left = ((random >> 8) / 16777216f * 2 - 1) * (.22f + .6f * (frame % rate) / rate);
            float right = left * ((frame & 1) == 0 ? .85f : -.7f);
            original[frame * 2] = left; original[frame * 2 + 1] = right;
            float gain = .8f + .3f * (frame % (rate * 7)) / (rate * 7);
            preview[frame * 2] = left * gain; preview[frame * 2 + 1] = right * gain;
        }
        return new(original, preview, AudioProcessor.BuildPeaks(original, channelCount), AudioProcessor.BuildPeaks(preview, channelCount),
            WaveformPyramid.Build(original, channelCount), WaveformPyramid.Build(preview, channelCount));
    }
}

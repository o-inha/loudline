using System.Buffers.Binary;
using Multiloud.Core;
using Multiloud.App.Services;

internal static class ExportWriterChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Float WAV custom exports join bit for bit to the single finished album", FloatGapless),
        ("24-bit WAV dithering stays on the album sample clock across exported boundaries", PcmGapless),
        ("WAV boundary fades are optional and touch only internal export edges", Fades),
        ("WAV export failures and cancellation preserve destinations and remove partial files", FailureCleanup),
        ("Streaming WAV export rejects the RIFF size limit before allocating or writing audio", RiffLimit),
        ("Export chunk progress is monotonic and reaches completion only after atomic publication", Progress),
        ("Cancellation from export progress removes partial output and never reports completion", ProgressCancellation)
    ];

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void InFolder(Action<string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LoudlineExportWriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { check(directory); } finally { Directory.Delete(directory, recursive: true); }
    }
    private static ExportAudioTimeline Fixture()
    {
        ExportAudioSource Source(int frames, int seed)
        {
            var random = new Random(seed);
            return new(Enumerable.Range(0, frames * 2).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), 48000, 2);
        }
        return new ExportAudioTimeline([Source(17051, 1), Source(18111, 2)]);
    }
    private sealed record Wave(int Rate, int Channels, int Format, int Bits, byte[] Data, int TotalBytes);
    private static Wave Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Require(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) == bytes.Length - 8 &&
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE", "Invalid RIFF header or padding");
        int format = 0, channels = 0, rate = 0, bits = 0; byte[]? samples = null;
        for (int offset = 12; offset + 8 <= bytes.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)));
            var data = bytes.AsSpan(offset + 8, length);
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data); channels = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(data[4..]); bits = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
            }
            if (id == "data") samples = data.ToArray();
            offset += 8 + length + (length & 1);
        }
        return new(rate, channels, format, bits, samples ?? throw new InvalidDataException("No WAV data"), bytes.Length);
    }
    private static float[] Floats(Wave wave)
    {
        var result = new float[wave.Data.Length / 4];
        for (int i = 0; i < result.Length; i++)
            result[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(wave.Data.AsSpan(i * 4, 4)));
        return result;
    }
    private static void FloatGapless() => InFolder(directory =>
    {
        var timeline = Fixture(); var one = ExportBoundaryPlan.Single(timeline)[0];
        string whole = Path.Combine(directory, "Whole.wav");
        TimelineWavExporter.Export(timeline, one, whole, float32: true);
        var full = Load(whole);
        var ranges = ExportBoundaryPlan.Custom(timeline, new[] { .031231, .201111, .541311 });
        var parts = ranges.Select((range, index) =>
        {
            string path = Path.Combine(directory, $"part{index}.wav");
            TimelineWavExporter.Export(timeline, range, path, true); return Load(path);
        }).ToArray();
        Require(full.Format == 3 && full.Bits == 32 && full.Channels == 2 && full.Rate == 48000 &&
            full.Data.SequenceEqual(parts.SelectMany(part => part.Data)), "Custom float files do not reconstruct the single output exactly");
        var expected = new float[timeline.FrameCount * timeline.Channels];
        timeline.Read(0, expected, 0, (int)timeline.FrameCount);
        Require(Floats(full).SequenceEqual(expected), "Float export changed the finished Result PCM");
    });
    private static void PcmGapless() => InFolder(directory =>
    {
        var timeline = Fixture(); string whole = Path.Combine(directory, "Whole.wav");
        TimelineWavExporter.Export(timeline, ExportBoundaryPlan.Single(timeline)[0], whole, false);
        var full = Load(whole);
        var data = ExportBoundaryPlan.Custom(timeline, new[] { .111133, .371119 }).Select((range, index) =>
        {
            string path = Path.Combine(directory, $"part{index}.wav"); TimelineWavExporter.Export(timeline, range, path, false); return Load(path).Data;
        }).SelectMany(bytes => bytes).ToArray();
        Require(full.Format == 1 && full.Bits == 24 && full.Data.SequenceEqual(data), "24-bit dither restarted at a custom file boundary");
        var odd = new ExportAudioTimeline([new ExportAudioSource([0f, .2f, -.2f], 12000, 1)]);
        string oddPath = Path.Combine(directory, "Odd.wav"); TimelineWavExporter.Export(odd, ExportBoundaryPlan.Single(odd)[0], oddPath, false);
        Require(Load(oddPath) is { Data.Length: 9, TotalBytes: 54 }, "Odd 24-bit mono data was not padded correctly");
    });
    private static void Fades() => InFolder(directory =>
    {
        var timeline = new ExportAudioTimeline([new ExportAudioSource(Enumerable.Repeat(.25f, 1000).ToArray(), 12000, 1)]);
        var ranges = ExportBoundaryPlan.Custom(timeline, new[] { .04 });
        var values = ranges.Select((range, index) =>
        {
            string path = Path.Combine(directory, $"part{index}.wav"); TimelineWavExporter.Export(timeline, range, path, true, 3); return Floats(Load(path));
        }).ToArray();
        Require(values[0][0] == .25f && values[^1][^1] == .25f && values[0][^1] == 0 && values[1][0] == 0,
            "Optional boundary fades changed natural ends or missed internal edges");
        Require(values[0][^40] == .25f && values[1][40] == .25f, "A short boundary fade changed distant audio");
    });
    private static void FailureCleanup() => InFolder(directory =>
    {
        var timeline = Fixture(); string path = Path.Combine(directory, "Keep.wav"); File.WriteAllText(path, "keep this source");
        try { TimelineWavExporter.Export(timeline, ExportBoundaryPlan.Single(timeline)[0], path, true); throw new InvalidOperationException("Overwrote existing file"); }
        catch (IOException) { }
        Require(File.ReadAllText(path) == "keep this source", "Export changed an existing destination");
        using var cts = new CancellationTokenSource(); cts.Cancel(); string cancelled = Path.Combine(directory, "Cancelled.wav");
        try { TimelineWavExporter.Export(timeline, ExportBoundaryPlan.Single(timeline)[0], cancelled, true, ct: cts.Token); throw new InvalidOperationException("Cancelled write succeeded"); }
        catch (OperationCanceledException) { }
        Require(!File.Exists(cancelled), "Cancelled export left a destination");
        var invalid = Enumerable.Repeat(.1f, 20000).ToArray(); invalid[^1] = float.NaN;
        var broken = new ExportAudioTimeline([new ExportAudioSource(invalid, 12000, 1)]);
        string failed = Path.Combine(directory, "Failed.wav");
        try { TimelineWavExporter.Export(broken, ExportBoundaryPlan.Single(broken)[0], failed, true); throw new InvalidOperationException("Invalid write succeeded"); }
        catch (InvalidDataException) { }
        Require(!File.Exists(failed) && !Directory.GetFiles(directory).Any(file => file.EndsWith(".partial", StringComparison.Ordinal)), "Failed streaming export retained a corrupt destination or partial file");
    });
    private static void Progress() => InFolder(directory =>
    {
        var timeline = Fixture(); var range = ExportBoundaryPlan.Single(timeline)[0];
        string tracked = Path.Combine(directory, "Progress.wav"), baseline = Path.Combine(directory, "Baseline.wav");
        var values = new List<double>();
        TimelineWavExporter.Export(timeline, range, tracked, true, progress: fraction =>
        {
            Require(double.IsFinite(fraction) && fraction >= 0 && fraction <= 1, "Export reported invalid progress.");
            Require(values.Count == 0 || fraction >= values[^1], "Export progress moved backwards.");
            Require(File.Exists(tracked) == (fraction == 1), "Export reported completion before the atomic WAV was published.");
            values.Add(fraction);
        });
        TimelineWavExporter.Export(timeline, range, baseline, true);
        Require(values.Count >= 3 && values[0] == 0 && values[^1] == 1 && values.Any(value => value is > 0 and < 1),
            "Chunked export omitted initial, intermediate or completed progress.");
        Require(File.ReadAllBytes(tracked).SequenceEqual(File.ReadAllBytes(baseline)), "Progress reporting changed exported PCM or WAV metadata.");
    });

    private static void ProgressCancellation() => InFolder(directory =>
    {
        var timeline = Fixture(); string path = Path.Combine(directory, "Cancelled with progress.wav");
        using var cancellation = new CancellationTokenSource(); var values = new List<double>();
        bool cancelled = false;
        try
        {
            TimelineWavExporter.Export(timeline, ExportBoundaryPlan.Single(timeline)[0], path, true,
                ct: cancellation.Token, progress: fraction =>
                {
                    values.Add(fraction);
                    if (fraction is > 0 and < 1) cancellation.Cancel();
                });
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && values.Any(value => value is > 0 and < 1) && !values.Contains(1),
            "A cancelled chunked export reported completion or ignored cancellation.");
        Require(!File.Exists(path) && !Directory.EnumerateFiles(directory, "*.partial").Any(),
            "Cancellation through progress left a WAV or a temporary file.");
    });

    private static void RiffLimit() => InFolder(directory =>
    {
        // Shared synthetic PCM makes a multi-hour timeline without allocating it.
        var source = new ExportAudioSource(new float[1_500_000], 48000, 1);
        var timeline = new ExportAudioTimeline(Enumerable.Repeat(source, 1000));
        string path = Path.Combine(directory, "Too large.wav");
        try { TimelineWavExporter.Export(timeline, ExportBoundaryPlan.Single(timeline)[0], path, false); throw new InvalidOperationException("Accepted oversized RIFF"); }
        catch (NotSupportedException) { }
        Require(!File.Exists(path) && Directory.GetFiles(directory).Length == 0, "RIFF preflight wrote data or temporary files");
    });
}

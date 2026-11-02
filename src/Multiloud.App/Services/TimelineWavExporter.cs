using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using Multiloud.Core;

namespace Multiloud.App.Services;

/// <summary>Atomic, bounded-memory writing of any partition of the finished album.</summary>
public static class TimelineWavExporter
{
    public static void Export(ExportAudioTimeline timeline, ExportAudioRange range, string path,
        bool float32, double boundaryFadeMs = 0, CancellationToken ct = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(timeline); ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        if (range.StartFrame < 0 || range.EndFrame > timeline.FrameCount || range.FrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (!double.IsFinite(boundaryFadeMs) || boundaryFadeMs is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(boundaryFadeMs));
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"A file already exists at {path}. Choose a different export name.");
        string? directory = Path.GetDirectoryName(path);
        if (directory == null || !Directory.Exists(directory)) throw new DirectoryNotFoundException("The export folder does not exist.");
        int bytesPerSample = float32 ? 4 : 3;
        long dataBytes = checked(range.FrameCount * timeline.Channels * bytesPerSample);
        int pad = (int)(dataBytes & 1), headerSize = float32 ? 56 : 44;
        if (dataBytes + headerSize - 8 + pad > uint.MaxValue)
            throw new NotSupportedException("This export is too large for a standard WAV file (4 GiB limit). Export it as individual files instead.");
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.partial");
        progress?.Invoke(0);
        long lastProgress = Stopwatch.GetTimestamp(); bool reportedPartial = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 262144))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                FourCc(writer, "RIFF"); writer.Write((uint)(dataBytes + headerSize - 8 + pad));
                FourCc(writer, "WAVE"); FourCc(writer, "fmt "); writer.Write(16u);
                writer.Write((ushort)(float32 ? 3 : 1)); writer.Write((ushort)timeline.Channels);
                writer.Write(timeline.SampleRate); writer.Write(timeline.SampleRate * timeline.Channels * bytesPerSample);
                writer.Write((ushort)(timeline.Channels * bytesPerSample)); writer.Write((ushort)(bytesPerSample * 8));
                if (float32) { FourCc(writer, "fact"); writer.Write(4u); writer.Write((uint)range.FrameCount); }
                FourCc(writer, "data"); writer.Write((uint)dataBytes); writer.Flush();
                const int chunkFrames = 16384;
                var samples = new float[chunkFrames * timeline.Channels];
                var bytes = new byte[samples.Length * bytesPerSample];
                for (long local = 0; local < range.FrameCount;)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(chunkFrames, range.FrameCount - local);
                    if (timeline.Read(range.StartFrame + local, samples, 0, count, ct) != count)
                        throw new InvalidDataException("The finished album ended before its export boundary.");
                    for (int frame = 0; frame < count; frame++)
                    {
                        double gain = boundaryFadeMs == 0 ? 1 : ExportBoundaryPlan.FadeGain(range, local + frame, timeline.SampleRate, boundaryFadeMs);
                        for (int channel = 0; channel < timeline.Channels; channel++)
                        {
                            int sampleIndex = frame * timeline.Channels + channel;
                            float value = gain == 1 ? samples[sampleIndex] : (float)(samples[sampleIndex] * gain);
                            if (!float.IsFinite(value)) throw new InvalidDataException("The Result contains an invalid sample. Reprocess it before exporting.");
                            int offset = sampleIndex * bytesPerSample;
                            if (float32)
                                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
                            else
                            {
                                if (value < -1 || value > 1)
                                    throw new InvalidOperationException("The Result exceeds digital full scale. Enable peak protection or reduce the gain before exporting 24-bit PCM.");
                                // Absolute-clock TPDF stays identical whether the same
                                // audio is written as one WAV or many gapless WAVs.
                                long absolute = checked((range.StartFrame + local + frame) * timeline.Channels + channel);
                                int quantized = (int)Math.Clamp(Math.Round(value * 8388608.0 + Dither(absolute)), -8388608, 8388607);
                                bytes[offset] = (byte)quantized; bytes[offset + 1] = (byte)(quantized >> 8); bytes[offset + 2] = (byte)(quantized >> 16);
                            }
                        }
                    }
                    stream.Write(bytes, 0, count * timeline.Channels * bytesPerSample);
                    local += count;
                    if (progress != null && local < range.FrameCount)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (!reportedPartial || now - lastProgress >= Stopwatch.Frequency / 20)
                        {
                            progress(local / (double)range.FrameCount);
                            lastProgress = now; reportedPartial = true;
                        }
                    }
                }
                if (pad != 0) stream.WriteByte(0);
                ct.ThrowIfCancellationRequested(); stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: false);
            progress?.Invoke(1);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void FourCc(BinaryWriter writer, string value) => writer.Write(Encoding.ASCII.GetBytes(value));
    private static double Dither(long position)
    {
        static uint Mix(ulong value)
        {
            unchecked
            {
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return (uint)((value ^ (value >> 31)) >> 32);
            }
        }
        ulong index = unchecked((ulong)position);
        return (Mix(index) - (double)Mix(index ^ 0x4D554C54494C4F55UL)) / 4294967296.0;
    }
}

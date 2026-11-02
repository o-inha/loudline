using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Multiloud.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Multiloud.App.Services;

/// <summary>Local decoding through Windows codecs; exports always use the rendered PCM.</summary>
public static class AudioFileService
{
    // Keep headroom for the original, processed buffer and analysis on a 32 GiB machine.
    // Accommodate an album-length stereo bed (one hour at 48 kHz is
    // 345,600,000 samples) while keeping any one decoded buffer below 2 GiB.
    public const int MaxSamplesPerFile = 536_000_000;
    public const string FileFilter = "Audio files|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.aif;*.aiff;*.wma|All files|*.*";

    // Container/codec metadata only; does not allocate or decode an audio PCM
    // buffer. This lets cancelled multi-file imports retain accurate clip slots.
    public static (double Duration, int SampleRate, int Channels) ReadSourceInfo(string path)
    {
        using var reader = OpenReader(Path.GetFullPath(path));
        double duration = reader.TotalTime.TotalSeconds;
        if (reader.WaveFormat.Channels is not (1 or 2) || reader.WaveFormat.SampleRate is < 8000 or > 384000)
            throw new NotSupportedException("Loudline accepts mono or stereo audio at 8–384 kHz.");
        if (!double.IsFinite(duration) || duration is < 0 or > 86400)
            throw new InvalidDataException("Invalid audio duration.");
        return (duration, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    }

    public static AudioTrack Load(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file was not found.", path);
        using var reader = OpenReader(path);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        if (channels is not (1 or 2))
            throw new NotSupportedException($"{Path.GetFileName(path)} has {channels} channels. Loudline currently accepts mono and stereo music; convert multichannel files first.");
        if (sampleRate is < 8000 or > 384000)
            throw new NotSupportedException($"The sample rate {sampleRate:N0} Hz is outside the supported 8–384 kHz range.");
        // Reading PCM/float WAV directly also preserves 24/32-bit WAVEFORMATEXTENSIBLE
        // files instead of taking an unnecessary legacy ACM conversion path.
        IWaveProvider source = reader;
        if (reader.WaveFormat.Encoding == WaveFormatEncoding.Extensible)
            source = new FormatOverrideProvider(reader, NormalizeExtensibleFormat(reader.WaveFormat));
        var sampleReader = new SampleChannel(source, forceStereo: false);
        long estimatedSamples = reader.Length / reader.WaveFormat.BlockAlign * channels;
        if (estimatedSamples > MaxSamplesPerFile) throw TooLarge();

        const int chunkSamples = 65_536;
        // For PCM the estimate is exact; compressed codecs occasionally revise the duration.
        int capacity = (int)Math.Clamp(estimatedSamples, chunkSamples, MaxSamplesPerFile);
        float[] samples = new float[capacity];
        var chunk = new float[chunkSamples];
        int length = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = sampleReader.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if ((long)length + read > MaxSamplesPerFile) throw TooLarge();
            int required = length + read;
            if (required > samples.Length)
                Array.Resize(ref samples, (int)Math.Min(MaxSamplesPerFile, Math.Max(required, (long)samples.Length + samples.Length / 2)));
            for (int i = 0; i < read; i++)
                if (!float.IsFinite(chunk[i]))
                    throw new InvalidDataException("The audio contains invalid (NaN or infinite) samples and cannot be processed safely.");
            Array.Copy(chunk, 0, samples, length, read);
            length += read;
        }
        if (length == 0) throw new InvalidDataException("The file contains no decodable audio samples.");
        if (length % channels != 0) throw new InvalidDataException("The decoded audio ends with an incomplete frame.");
        if (samples.Length != length) Array.Resize(ref samples, length);
        ct.ThrowIfCancellationRequested();
        return new AudioTrack
        {
            FilePath = path,
            Original = samples,
            SampleRate = sampleRate,
            Channels = channels,
            Status = "Decoded"
        };
    }

    /// <summary>Writes one track, preserving its native sample rate, channel count and duration.</summary>
    public static void ExportWav(AudioTrack track, string path, bool float32, CancellationToken ct = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ct.ThrowIfCancellationRequested();
        float[] samples = track.Render?.Samples ?? throw new InvalidOperationException("Render the current preview before exporting this track.");
        path = Path.GetFullPath(path);
        if (!track.GeneratedSilenceSeconds.HasValue && string.Equals(path, Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export must use a different path from the original file.");
        if (File.Exists(path)) throw new IOException($"A file already exists at {path}. Choose a different export name.");
        if (track.Render?.LiveSource is { } live)
        {
            var timeline = new ExportAudioTimeline([new ExportAudioSource(samples, track.SampleRate,
                track.Channels, track.Name, live)]);
            TimelineWavExporter.Export(timeline, ExportBoundaryPlan.Single(timeline)[0], path, float32, 0, ct, progress);
            return;
        }
        int bytesPerSample = float32 ? 4 : 3;
        long dataBytes = (long)samples.Length * bytesPerSample;
        int pad = (int)(dataBytes & 1);
        int headerSize = float32 ? 56 : 44;
        if (dataBytes + headerSize - 8 + pad > uint.MaxValue)
            throw new NotSupportedException("This track is too large for a standard WAV file (4 GiB limit).");
        if (samples.Length % track.Channels != 0) throw new InvalidDataException("The rendered audio has an incomplete frame.");
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException("The export folder does not exist.");
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.partial");
        progress?.Invoke(0);
        long lastProgress = Stopwatch.GetTimestamp(); bool reportedPartial = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 262_144))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                WriteFourCc(writer, "RIFF");
                writer.Write((uint)(dataBytes + headerSize - 8 + pad));
                WriteFourCc(writer, "WAVE");
                WriteFourCc(writer, "fmt ");
                writer.Write(16u);
                writer.Write((ushort)(float32 ? 3 : 1));
                writer.Write((ushort)track.Channels);
                writer.Write(track.SampleRate);
                writer.Write(track.SampleRate * track.Channels * bytesPerSample);
                writer.Write((ushort)(track.Channels * bytesPerSample));
                writer.Write((ushort)(bytesPerSample * 8));
                if (float32)
                {
                    WriteFourCc(writer, "fact");
                    writer.Write(4u);
                    writer.Write((uint)(samples.Length / track.Channels));
                }
                WriteFourCc(writer, "data");
                writer.Write((uint)dataBytes);
                writer.Flush();

                var bytes = new byte[65_536 * bytesPerSample];
                var dither = new Random(0x4D554C54);
                const double scale = 8_388_608.0;
                for (int start = 0; start < samples.Length; start += 65_536)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = Math.Min(65_536, samples.Length - start);
                    for (int i = 0; i < count; i++)
                    {
                        float sample = samples[start + i];
                        if (!float.IsFinite(sample)) throw new InvalidDataException("The preview contains an invalid sample. Re-render before exporting.");
                        int offset = i * bytesPerSample;
                        if (float32)
                            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(sample));
                        else
                        {
                            if (sample < -1 || sample > 1)
                                throw new InvalidOperationException("The preview exceeds digital full scale. Enable peak protection or reduce the gain before exporting 24-bit PCM.");
                            // TPDF at one quantization step; rounding at full scale is bounded to 24 bits.
                            int quantized = (int)Math.Clamp(Math.Round(sample * scale + dither.NextDouble() - dither.NextDouble()), -8_388_608, 8_388_607);
                            bytes[offset] = (byte)quantized;
                            bytes[offset + 1] = (byte)(quantized >> 8);
                            bytes[offset + 2] = (byte)(quantized >> 16);
                        }
                    }
                    stream.Write(bytes, 0, count * bytesPerSample);
                    int end = start + count;
                    if (progress != null && end < samples.Length)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (!reportedPartial || now - lastProgress >= Stopwatch.Frequency / 20)
                        {
                            progress(end / (double)samples.Length);
                            lastProgress = now; reportedPartial = true;
                        }
                    }
                }
                if (pad != 0) stream.WriteByte(0);
                ct.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: false);
            progress?.Invoke(1);
        }
        finally
        {
            // Cancellation or an IO failure never leaves a corrupt WAV under the chosen name.
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void WriteFourCc(BinaryWriter writer, string value) => writer.Write(Encoding.ASCII.GetBytes(value));
    private static NotSupportedException TooLarge() => new("This file exceeds the 2 GiB decoded-audio limit. Use a shorter file or a lower sample rate before importing.");

    private static WaveStream OpenReader(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".wav") return new WaveFileReader(path);
        if (extension is ".aif" or ".aiff") return new AiffFileReader(path);
        try
        {
            return new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true,
                SingleReaderObject = true
            });
        }
        catch (COMException)
        {
            // Some installed codecs expose PCM only. No gain or resampling is performed.
            var pcm = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true });
            int sourceBits = extension == ".flac" ? ReadFlacBitDepth(path) : 0;
            if (sourceBits > pcm.WaveFormat.BitsPerSample)
            {
                int decodedBits = pcm.WaveFormat.BitsPerSample;
                pcm.Dispose();
                throw new NotSupportedException($"The Windows FLAC decoder offered only {decodedBits}-bit output for this {sourceBits}-bit file. Convert it to a lossless WAV first to preserve its precision.");
            }
            return pcm;
        }
    }

    private static int ReadFlacBitDepth(string path)
    {
        Span<byte> header = stackalloc byte[26];
        using var file = File.OpenRead(path);
        if (file.Read(header) != header.Length || !header[..4].SequenceEqual("fLaC"u8) || (header[4] & 0x7f) != 0) return 0;
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(header[18..26]);
        return (int)((packed >> 36) & 31) + 1;
    }

    private sealed class FormatOverrideProvider(IWaveProvider source, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;
        public int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    }

    private static WaveFormat NormalizeExtensibleFormat(WaveFormat format)
    {
        // WaveFileReader 2.x represents fmt chunks as WaveFormatExtraData rather
        // than WaveFormatExtensible. Both representations describe the same raw PCM.
        Guid subtype = format switch
        {
            WaveFormatExtensible extensible => extensible.SubFormat,
            WaveFormatExtraData extra when extra.ExtraSize >= 22 => new Guid(extra.ExtraData.AsSpan(6, 16)),
            _ => throw new InvalidDataException("The WAV has an incomplete extensible format header.")
        };
        if (subtype == new Guid("00000001-0000-0010-8000-00aa00389b71"))
            return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        if (subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") && format.BitsPerSample is 32 or 64)
            return WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, format.SampleRate, format.Channels,
                format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample);
        throw new NotSupportedException("This extensible WAV encoding is unsupported. Use PCM or IEEE float WAV.");
    }
}

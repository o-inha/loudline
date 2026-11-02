using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

internal static class AuditionTests
{
    private const int Rate = 48000;
    private static readonly double HalfDb = 20 * Math.Log10(.5);

    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Audition selects each mode's gain and defaults to bit-exact unity", SelectedModeAndUnity),
        ("Audition gain clamps attenuation and bypasses nonfinite values", GainBounds),
        ("Fair A/B attenuation preserves source PCM, render, waveform, and edit data", FairComparisonAndImmutability),
        ("Attenuated byte-backed callbacks preserve offsets, partial reads, and EOF", AliasedOffsetsAndEof),
        ("Audition gain preserves mixed-rate stereo/mono order and track transitions", MixedFormatsAndTransitions),
        ("Resampled audition callbacks preserve byte-buffer bounds through their final tail", ResampledTailBounds),
        ("Audition seeks select the right frame and retain each track's selected gain", Seek),
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance = 1e-6)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected:G10} ± {tolerance:G3}, got {actual:G10}.");

    private static bool SameBits(IEnumerable<float> actual, IEnumerable<float> expected)
        => actual.Select(BitConverter.SingleToInt32Bits).SequenceEqual(expected.Select(BitConverter.SingleToInt32Bits));

    private static PlaybackTrackState State(float[] original, float[]? preview = null,
        double originalDb = 0, double previewDb = 0, int rate = Rate, int channels = 2)
        => new(Guid.NewGuid(), rate, channels, original.Length / (double)(rate * channels),
            original, preview ?? original, originalDb, previewDb);

    private static PlaybackSequence Provider(IReadOnlyList<PlaybackTrackState> states, bool original = true, double seconds = 0)
        => PlaybackService.CreateSampleProvider(() => states, original, seconds);

    private static float[] ReadAll(PlaybackSequence provider)
    {
        var output = new List<float>();
        var buffer = new float[1010];
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            Array.Fill(buffer, 99f);
            int count = provider.Read(buffer, 2, 1003);
            Require(count >= 0 && count <= 1002 && count % 2 == 0, "Read returned an incomplete monitoring frame.");
            Require(buffer.Take(2).All(value => value == 99) && buffer.Skip(2 + count).All(value => value == 99),
                "Read changed samples outside its returned region.");
            if (count == 0) return output.ToArray();
            output.AddRange(buffer.Skip(2).Take(count));
        }
        throw new InvalidOperationException("Audition provider never reached EOF.");
    }

    private static void SelectedModeAndUnity()
    {
        float[] original = [.25f, -.5f, .125f, -.75f];
        float[] preview = [.8f, -.4f, .2f, -.6f];
        var state = State(original, preview, HalfDb, 2 * HalfDb);
        var heardOriginal = ReadAll(Provider([state]));
        var heardPreview = ReadAll(Provider([state], original: false));
        for (int i = 0; i < original.Length; i++)
        {
            Near(heardOriginal[i], original[i] * .5);
            Near(heardPreview[i], preview[i] * .25);
        }
        float[] precise = [0f, -0f, float.Epsilon, -float.Epsilon, .12345679f, -.9876543f];
        // The six-argument constructor is the existing callers' compatibility path.
        var defaults = new PlaybackTrackState(Guid.NewGuid(), Rate, 2, 3.0 / Rate, precise, precise);
        Require(SameBits(ReadAll(Provider([defaults])), precise) && SameBits(ReadAll(Provider([defaults], false)), precise),
            "Default audition gain changed source sample bits.");
    }

    private static void GainBounds()
    {
        float[] source = [.75f, -.25f];
        foreach (var (db, gain) in new (double, double)[]
        {
            (12, 1), (0, 1), (-400, 1e-6), (-120, 1e-6),
            (double.NaN, 1), (double.PositiveInfinity, 1), (double.NegativeInfinity, 1),
        })
        {
            var state = State(source, originalDb: db, previewDb: db);
            foreach (bool original in new[] { true, false })
            {
                var output = ReadAll(Provider([state], original));
                for (int i = 0; i < source.Length; i++) Near(output[i], source[i] * gain, 1e-10);
            }
        }
    }

    private static void FairComparisonAndImmutability()
    {
        foreach (float processedLevel in new[] { .4f, .1f })
        {
            float[] original = [.2f, -.2f, .2f, -.2f];
            float[] processed = [processedLevel, -processedLevel, processedLevel, -processedLevel];
            var track = new AudioTrack
            {
                FilePath = "unchanged.wav", Original = original, OriginalWaveform = WaveformPyramid.Build(original, 2),
                Analysis = new AnalysisResult { IntegratedLufs = -18, TruePeakDb = -4 },
                Render = new RenderResult { Samples = processed, OutputLufs = -12, MatchGainDb = 3,
                    Waveform = WaveformPyramid.Build(processed, 2) },
                OriginalPeaks = [new(-.2f, .2f)], PreviewPeaks = [new(-processedLevel, processedLevel)],
                Envelope = [new(0, -1), new(1, 2)], TransitionEnvelope = [new(0, .5)],
                TrimDb = -2, InputGainDb = 1.5, TargetLufs = -15, Status = "Ready", WorkState = TrackWorkState.Idle,
            };
            var before = new { track.Id, track.SourceId, track.FilePath, track.Original, track.Render, track.Analysis,
                track.OriginalWaveform, PreviewWaveform = track.Render.Waveform, track.OriginalPeaks, track.PreviewPeaks,
                track.Envelope, track.TransitionEnvelope };
            var originalCopy = original.ToArray(); var processedCopy = processed.ToArray();
            var originalPeaks = track.OriginalPeaks.ToArray(); var previewPeaks = track.PreviewPeaks.ToArray();
            var envelope = track.Envelope.ToArray(); var transitions = track.TransitionEnvelope.ToArray();
            var originalExtrema = track.OriginalWaveform.Query(0, 2);
            var previewExtrema = track.Render.Waveform.Query(0, 2);
            double reference = Math.Min(.2, processedLevel);
            var state = new PlaybackTrackState(track.Id, Rate, 2, track.Duration, original, processed,
                20 * Math.Log10(reference / .2), 20 * Math.Log10(reference / processedLevel));
            var a = ReadAll(Provider([state])); var b = ReadAll(Provider([state], false));
            for (int i = 0; i < a.Length; i++)
            {
                Near(a[i], b[i]); Near(Math.Abs(a[i]), reference);
            }
            Require(SameBits(original, originalCopy) && SameBits(processed, processedCopy), "Monitoring modified source or export PCM.");
            Require(ReferenceEquals(track.Original, before.Original) && ReferenceEquals(track.Render, before.Render) &&
                ReferenceEquals(track.Analysis, before.Analysis) && ReferenceEquals(track.OriginalWaveform, before.OriginalWaveform) &&
                ReferenceEquals(track.Render.Waveform, before.PreviewWaveform) &&
                ReferenceEquals(track.OriginalPeaks, before.OriginalPeaks) && ReferenceEquals(track.PreviewPeaks, before.PreviewPeaks) &&
                ReferenceEquals(track.Envelope, before.Envelope) && ReferenceEquals(track.TransitionEnvelope, before.TransitionEnvelope),
                "Monitoring replaced a source, render, waveform, peak list, or authored envelope.");
            Require(track.OriginalPeaks.SequenceEqual(originalPeaks) && track.PreviewPeaks.SequenceEqual(previewPeaks) &&
                track.Envelope.SequenceEqual(envelope) && track.TransitionEnvelope.SequenceEqual(transitions) &&
                track.OriginalWaveform.Query(0, 2) == originalExtrema && track.Render.Waveform.Query(0, 2) == previewExtrema,
                "Monitoring modified waveform summaries, peaks, or editable envelope values.");
            Require(track.Id == before.Id && track.SourceId == before.SourceId && track.FilePath == before.FilePath &&
                track.TrimDb == -2 && track.InputGainDb == 1.5 && track.TargetLufs == -15 && track.Status == "Ready" &&
                track.WorkState == TrackWorkState.Idle && track.Analysis.IntegratedLufs == -18 && track.Analysis.TruePeakDb == -4 &&
                track.Render.OutputLufs == -12 && track.Render.MatchGainDb == 3, "Monitoring changed saved or measured track metadata.");
        }
    }

    private static void AliasedOffsetsAndEof()
    {
        float[] samples = [.2f, -.4f, .6f, -.8f, .3f, -.5f];
        var provider = Provider([State(samples, originalDb: HalfDb)]);
        byte[] bytes = Enumerable.Repeat((byte)0x6B, 24 * sizeof(float)).ToArray();
        var alias = new WaveBuffer(bytes);
        Require(alias.FloatBuffer.GetType() == typeof(byte[]), "Fixture did not create a byte-backed callback.");
        int read = provider.Read(alias.FloatBuffer, 3, 5);
        Require(read == 4, "Odd callback count was not rounded to complete stereo frames.");
        Check(3, read, 0);
        Array.Fill(bytes, (byte)0x6B);
        read = provider.Read(alias.FloatBuffer, 5, 10);
        Require(read == 2, "Final partial callback did not return its two remaining samples.");
        Check(5, read, 4);
        var previous = bytes.ToArray();
        Require(provider.Read(alias.FloatBuffer, 1, 8) == 0 && bytes.SequenceEqual(previous), "EOF wrote into the callback buffer.");
        void Check(int offset, int count, int sourceOffset)
        {
            var values = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            for (int i = 0; i < count; i++) Near(values[offset + i], samples[sourceOffset + i] * .5);
            Require(bytes.Take(offset * sizeof(float)).All(value => value == 0x6B) &&
                bytes.Skip((offset + count) * sizeof(float)).All(value => value == 0x6B),
                "Attenuated callback changed adjacent bytes.");
        }
    }

    private static void MixedFormatsAndTransitions()
    {
        const double duration = .08;
        float[] Constant(int rate, int channels, float left, float right = 0)
        {
            var samples = new float[(int)(rate * duration) * channels];
            for (int frame = 0; frame < samples.Length / channels; frame++)
            {
                samples[frame * channels] = left;
                if (channels == 2) samples[frame * channels + 1] = right;
            }
            return samples;
        }
        var states = new[]
        {
            State(Constant(44100, 2, .2f, -.3f), originalDb: HalfDb, rate: 44100),
            State(Constant(24000, 1, .5f), originalDb: 2 * HalfDb, rate: 24000, channels: 1),
            State(Constant(Rate, 2, -.1f, .4f), originalDb: HalfDb),
        };
        var provider = Provider(states);
        Require(provider.WaveFormat.SampleRate == Rate && provider.WaveFormat.Channels == 2, "Audition changed the monitoring format.");
        var output = ReadAll(provider);
        int frames = (int)(Rate * duration);
        Require(Math.Abs(output.Length / 2 - 3 * frames) <= 4, "Audition gain changed the album timeline.");
        int Mid(int track) => (track * frames + frames / 2) * 2;
        Near(output[Mid(0)], .1, 2e-4); Near(output[Mid(0) + 1], -.15, 2e-4);
        Near(output[Mid(1)], .125 * Math.Sqrt(.5), 2e-4); Near(output[Mid(1) + 1], .125 * Math.Sqrt(.5), 2e-4);
        Near(output[Mid(2)], -.05); Near(output[Mid(2) + 1], .2);
    }

    private static void ResampledTailBounds()
    {
        foreach (var (rate, channels) in new[] { (44100, 2), (24000, 1) })
        foreach (bool original in new[] { true, false })
        {
            var samples = Enumerable.Repeat(.25f, 4800 * channels).ToArray();
            var provider = Provider([State(samples, originalDb: HalfDb, previewDb: 2 * HalfDb,
                rate: rate, channels: channels)], original);
            const int offset = 7, requested = 10003;
            var bytes = new byte[(offset + requested + 11) * sizeof(float)];
            var alias = new WaveBuffer(bytes);
            int total = 0;
            bool partial = false, ended = false;
            for (int iteration = 0; iteration < 20; iteration++)
            {
                Array.Fill(bytes, (byte)0x6B);
                int read = provider.Read(alias.FloatBuffer, offset, requested);
                Require(read >= 0 && read <= requested - 1 && read % 2 == 0,
                    "Resampling returned invalid monitoring frames.");
                Require(bytes.Take(offset * sizeof(float)).All(value => value == 0x6B) &&
                    bytes.Skip((offset + read) * sizeof(float)).All(value => value == 0x6B),
                    "A resampled or mono EOF callback wrote outside its returned audio region.");
                if (read == 0) { ended = true; break; }
                total += read;
                partial |= read < requested - 1;
            }
            Require(ended && partial && total > 4096,
                "Bounds fixture must exercise multiple scratch blocks, a short final read and EOF.");
        }
    }

    private static void Seek()
    {
        float[] first = [.2f, -.2f, .4f, -.4f, .6f, -.6f];
        float[] second = [.8f, -.8f, .3f, -.3f, .5f, -.5f];
        var states = new[] { State(first, originalDb: HalfDb), State(second, originalDb: 2 * HalfDb, previewDb: HalfDb) };
        var across = ReadAll(Provider(states, seconds: 1.25 / Rate));
        var expected = first.Skip(2).Select(value => value * .5f).Concat(second.Select(value => value * .25f)).ToArray();
        Require(across.Length == expected.Length, "Seek changed the remaining album length.");
        for (int i = 0; i < expected.Length; i++) Near(across[i], expected[i]);
        var boundary = ReadAll(Provider(states, original: false, seconds: 3.0 / Rate));
        Require(boundary.Length == second.Length, "Boundary seek did not begin at the next track.");
        for (int i = 0; i < second.Length; i++) Near(boundary[i], second[i] * .5);
        Require(ReadAll(Provider(states, seconds: 6.0 / Rate)).Length == 0, "End seek should return EOF.");
    }
}

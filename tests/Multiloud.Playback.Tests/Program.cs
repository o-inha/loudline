using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Exercise the production provider chain without opening an audio endpoint.
// In particular WaveBuffer.FloatBuffer aliases a byte[] at runtime: Array.Copy
// rejects that host buffer even though ordinary float[] tests pass.
var checks = new (string Name, Action Run)[]
{
    ("Byte-backed WaveBuffer accepts float reads at nonzero offsets", AliasedFloatRead),
    ("Sample-to-wave conversion preserves byte offsets and adjacent data", ByteProviderRead),
    ("Album reads cross track boundaries and return aligned EOF counts", TrackBoundaries),
    ("Mono/stereo mixed sample rates stay ordered on the monitoring timeline", MixedFormats),
    ("Seeking selects the correct frame and track", Seek),
    ("Original/preview selection snapshots the selected PCM", Snapshot),
    ("Empty sequence and end seek return EOF without hardware", EmptyAndEnd),
}.Concat(PlaybackReadinessTests.All).Concat(PreviewCacheTests.All).Concat(BackgroundCacheTests.All).Concat(TrackOrderingTests.All).Concat(PlaybackSeekTests.All).Concat(HorizontalScrollTests.All).Concat(ProjectFileTests.All).Concat(AuditionTests.All).Concat(BackgroundPlaybackTests.All).Concat(ScrubbingTests.All).Concat(LivePlaybackTests.All).Concat(PeakDeltaTests.All).Concat(LoopPlaybackTests.All).Concat(LoopProjectTests.All).Concat(ProjectMergedTests.All).ToArray();
int failures = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} playback/cache checks passed.");
return failures == 0 ? 0 : 1;

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Near(float actual, double expected, double tolerance = 1e-6)
{
    Require(float.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
        $"Expected {expected:G8} ± {tolerance:G3}, got {actual:G8}.");
}

static AudioTrack Track(float[] samples, int rate = 48000, int channels = 2) => new()
{
    FilePath = "fixture.wav", Original = samples, SampleRate = rate, Channels = channels,
    Render = new RenderResult { Samples = samples },
};

static ISampleProvider Provider(AudioTrack[] tracks, double seek = 0, bool original = true)
    => PlaybackService.CreateSampleProvider(tracks, original, seek);

static float[] Pattern(int frames, float offset = 0)
{
    var data = new float[frames * 2];
    for (int i = 0; i < frames; i++)
    {
        data[i * 2] = offset + (i + 1) * .01f;
        data[i * 2 + 1] = -offset - (i + 1) * .02f;
    }
    return data;
}

static float[] Decode(byte[] bytes)
{
    var values = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(float));
    return values;
}

static void AliasedFloatRead()
{
    var samples = Pattern(6);
    var provider = Provider([Track(samples)]);
    byte[] bytes = Enumerable.Repeat((byte)0x5A, 24 * sizeof(float)).ToArray();
    var alias = new WaveBuffer(bytes);
    Require(alias.FloatBuffer.GetType() == typeof(byte[]), "Fixture must use a byte-backed WaveBuffer rather than a normal float array");
    int read = provider.Read(alias.FloatBuffer, 3, 8);
    Require(read == 8, "First read should contain four stereo frames");
    var decoded = Decode(bytes);
    for (int i = 0; i < read; i++) Near(decoded[3 + i], samples[i]);
    Require(bytes.Take(3 * sizeof(float)).All(b => b == 0x5A), "Read modified bytes before its destination offset");
    Require(bytes.Skip((3 + read) * sizeof(float)).All(b => b == 0x5A), "Read modified bytes after its requested region");
    read = provider.Read(alias.FloatBuffer, 5, 12);
    Require(read == 4, "Final short read should return two stereo frames");
    decoded = Decode(bytes);
    for (int i = 0; i < read; i++) Near(decoded[5 + i], samples[8 + i]);
    Require(provider.Read(alias.FloatBuffer, 0, 4) == 0, "An exhausted track must return EOF");
}

static void ByteProviderRead()
{
    var samples = Pattern(12);
    var provider = new SampleToWaveProvider(Provider([Track(samples)]));
    byte[] bytes = Enumerable.Repeat((byte)0x6B, 128).ToArray();
    int read = provider.Read(bytes, 16, 64);
    Require(read == 64, "SampleToWaveProvider should return the requested complete frames");
    var decoded = Decode(bytes);
    for (int i = 0; i < read / sizeof(float); i++) Near(decoded[4 + i], samples[i]);
    Require(bytes.Take(16).All(b => b == 0x6B) && bytes.Skip(80).All(b => b == 0x6B),
        "Conversion wrote outside the byte offset/count region");
    read = provider.Read(bytes, 8, 64);
    Require(read == 32, "Final byte read should return the remaining four frames");
    decoded = Decode(bytes);
    for (int i = 0; i < read / sizeof(float); i++) Near(decoded[2 + i], samples[16 + i]);
}

static void TrackBoundaries()
{
    var first = Pattern(3, .1f);
    var second = Pattern(4, .4f);
    var provider = Provider([Track(first), Track(second)]);
    var values = Enumerable.Repeat(99f, 24).ToArray();
    int read = provider.Read(values, 2, 19); // Odd request rounds down to complete stereo frames.
    Require(read == first.Length + second.Length && read % 2 == 0, "Cross-track read lost samples or returned an incomplete frame");
    var expected = first.Concat(second).ToArray();
    for (int i = 0; i < expected.Length; i++) Near(values[i + 2], expected[i]);
    Require(values.Take(2).All(v => v == 99) && values.Skip(2 + read).All(v => v == 99), "Cross-track read changed adjacent samples");
    Require(provider.Read(values, 0, values.Length) == 0, "Album must stop at EOF");
}

static void MixedFormats()
{
    const double seconds = .06;
    float[] Constant(int rate, int channels, float left, float right = 0)
    {
        var values = new float[(int)(rate * seconds) * channels];
        for (int f = 0; f < values.Length / channels; f++)
        {
            values[f * channels] = left;
            if (channels == 2) values[f * channels + 1] = right;
        }
        return values;
    }
    var provider = Provider([
        Track(Constant(44100, 2, .1f, -.1f), 44100),
        Track(Constant(24000, 1, .2f), 24000, 1),
        Track(Constant(48000, 2, .3f, -.3f)),
    ]);
    Require(provider.WaveFormat.SampleRate == 48000 && provider.WaveFormat.Channels == 2,
        "Monitoring must use one stable stereo sample format");
    var output = new List<float>();
    var buffer = new float[1008];
    for (int iterations = 0; iterations < 100; iterations++)
    {
        int read = provider.Read(buffer, 2, 1003);
        Require(read % 2 == 0 && read >= 0 && read <= 1002, "Mixed-format read returned invalid frame alignment");
        if (read == 0) break;
        output.AddRange(buffer.Skip(2).Take(read));
        Require(iterations < 99, "Mixed-format provider never reached EOF");
    }
    int framesPerTrack = (int)Math.Round(48000 * seconds);
    Require(Math.Abs(output.Count / 2 - framesPerTrack * 3) <= 4,
        $"Resampling changed the album timeline: expected {framesPerTrack * 3} frames, got {output.Count / 2}");
    int Mid(int track) => (track * framesPerTrack + framesPerTrack / 2) * 2;
    Near(output[Mid(0)], .1, 1e-4); Near(output[Mid(0) + 1], -.1, 1e-4);
    Near(output[Mid(1)], .2 * Math.Sqrt(.5), 1e-4); Near(output[Mid(1) + 1], .2 * Math.Sqrt(.5), 1e-4);
    Near(output[Mid(2)], .3, 1e-5); Near(output[Mid(2) + 1], -.3, 1e-5);
}

static void Seek()
{
    var first = Pattern(20, .1f);
    var second = Pattern(30, .4f);
    var tracks = new[] { Track(first), Track(second) };
    var provider = Provider(tracks, (20 + 5.25) / 48000.0);
    var actual = new float[8];
    Require(provider.Read(actual, 0, actual.Length) == actual.Length, "Seek returned too few frames");
    for (int i = 0; i < actual.Length; i++) Near(actual[i], second[10 + i]);
    provider = Provider(tracks, 20 / 48000.0);
    Require(provider.Read(actual, 0, 4) == 4, "Boundary seek returned EOF");
    for (int i = 0; i < 4; i++) Near(actual[i], second[i]);
}

static void Snapshot()
{
    var original = Pattern(8, .1f);
    var processed = Pattern(8, .2f);
    var track = Track(original);
    track.Render = new RenderResult { Samples = processed };
    var preview = Provider([track], original: false);
    var source = Provider([track], original: true);
    // Rendering a new preview must not replace the PCM under an active callback.
    track.Render = new RenderResult { Samples = Pattern(8, .4f) };
    var actual = new float[16];
    Require(preview.Read(actual, 0, actual.Length) == actual.Length, "Preview data missing");
    for (int i = 0; i < actual.Length; i++) Near(actual[i], processed[i]);
    Require(source.Read(actual, 0, actual.Length) == actual.Length, "Original data missing");
    for (int i = 0; i < actual.Length; i++) Near(actual[i], original[i]);
}

static void EmptyAndEnd()
{
    var buffer = new float[8];
    Require(Provider([]).Read(buffer, 0, buffer.Length) == 0, "Empty album must return EOF");
    var track = Track(Pattern(8));
    Require(Provider([track], track.Duration).Read(buffer, 0, buffer.Length) == 0, "Seek to end must return EOF");
    Require(Provider([track], track.Duration + 1).Read(buffer, 0, buffer.Length) == 0, "Seek past end must return EOF");
    var atStart = Provider([track], double.NaN);
    Require(atStart.Read(buffer, 0, 2) == 2, "Invalid seek must clamp to a valid position");
    Near(buffer[0], track.Original[0]);
}

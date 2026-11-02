using Multiloud.App.Services;
using Multiloud.Core;
using NAudio.Wave;

internal static class BackgroundPlaybackTests
{
    private const int Rate = 48000;
    private static readonly double HalfDb = 20 * Math.Log10(.5);

    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Original adds the aligned bed once while Result uses the final mixed PCM", OriginalAndResult),
        ("Monitor matching attenuates the entire mix without changing music or background", MonitorGain),
        ("Background mixing preserves byte-backed offsets and partial EOF bounds", ByteBackedBounds),
        ("A required pending bed blocks both modes at its boundary and pending seek", PendingBed),
        ("Mixed-rate mono/stereo beds retain channel routing and album order", MixedFormats),
        ("Seeks use each song's album-aligned bed segment without restarting its noise", SeekAndContinuity),
        ("Background publications stay pinned within the current source and refresh on seek", PinnedPublication),
        ("Unaligned background metadata is rejected before playback", InvalidAlignment),
        ("Legacy AudioTrack snapshots preserve background Original and final Result playback", LegacyTrackSnapshot),
        ("Common-format background playback preserves stereo on native mono and continuous mixed-rate boundaries", PreparedAlbumFormat),
        ("Background Solo plays exact fixed-gain bed in either view without any music Result", SoloIndependent),
        ("Background Solo streams fractional mixed-rate placement and seeks without restarting fades", SoloMixedFormats),
        ("Background Solo monitor seek swaps at callback boundary without touching export PCM", SoloTransport),
        ("Reusable background reads preserve byte-backed buffer bounds", SoloByteBuffer),
    ];

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance = 1e-6)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected:G10} ± {tolerance:G3}, got {actual:G10}.");

    private static bool SameBits(IEnumerable<float> actual, IEnumerable<float> expected)
        => actual.Select(BitConverter.SingleToInt32Bits).SequenceEqual(expected.Select(BitConverter.SingleToInt32Bits));

    private static PlaybackTrackState State(float[] music, float[]? background, float[]? result = null,
        int rate = Rate, int channels = 2, double originalGain = 0, double resultGain = 0)
        => new(Guid.NewGuid(), rate, channels, music.Length / (double)(rate * channels), music,
            result ?? music.Zip(background ?? new float[music.Length], (a, b) => a + b).ToArray(),
            originalGain, resultGain, background, true);

    private static PlaybackSequence Sequence(IReadOnlyList<PlaybackTrackState> states, bool original = true, double seconds = 0)
        => PlaybackService.CreateSampleProvider(() => states, original, seconds);

    private static float[] ReadAll(PlaybackSequence sequence)
    {
        var result = new List<float>();
        var buffer = new float[1010];
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            Array.Fill(buffer, 99);
            int read = sequence.Read(buffer, 3, 1003);
            Require(read >= 0 && read <= 1002 && read % 2 == 0, "Background callback returned incomplete frames.");
            Require(buffer.Take(3).All(value => value == 99) && buffer.Skip(3 + read).All(value => value == 99),
                "Background callback wrote outside its returned region.");
            if (read == 0) return result.ToArray();
            result.AddRange(buffer.Skip(3).Take(read));
        }
        throw new InvalidOperationException("Background callback never reached EOF.");
    }

    private static void OriginalAndResult()
    {
        float[] music = [.2f, -.3f, .4f, -.5f, .1f, -.2f];
        float[] background = [.02f, .03f, -.04f, -.05f, .06f, .07f];
        float[] result = [.3f, -.1f, .2f, -.6f, .4f, -.2f];
        var musicCopy = music.ToArray(); var bedCopy = background.ToArray(); var resultCopy = result.ToArray();
        var state = State(music, background, result);
        var a = ReadAll(Sequence([state])); var b = ReadAll(Sequence([state], false));
        for (int i = 0; i < music.Length; i++) Near(a[i], music[i] + background[i]);
        Require(SameBits(b, result), "Result added the bed twice or changed already rendered PCM.");
        Require(SameBits(music, musicCopy) && SameBits(background, bedCopy) && SameBits(result, resultCopy),
            "Monitoring modified immutable music, background or export PCM.");
    }

    private static void MonitorGain()
    {
        float[] music = [.4f, -.2f, .2f, -.4f];
        float[] bed = [.1f, .05f, -.1f, -.05f];
        float[] final = [.6f, -.3f, .3f, -.6f];
        var state = State(music, bed, final, originalGain: HalfDb, resultGain: 2 * HalfDb);
        var a = ReadAll(Sequence([state])); var b = ReadAll(Sequence([state], false));
        for (int i = 0; i < music.Length; i++)
        {
            Near(a[i], (music[i] + bed[i]) * .5);
            Near(b[i], final[i] * .25);
        }
    }

    private static void ByteBackedBounds()
    {
        float[] music = [.2f, -.4f, .6f, -.8f, .3f, -.5f];
        float[] bed = [.03f, -.02f, .05f, -.04f, .07f, -.06f];
        var sequence = Sequence([State(music, bed, originalGain: HalfDb)]);
        var bytes = Enumerable.Repeat((byte)0x6B, 40 * sizeof(float)).ToArray();
        var alias = new WaveBuffer(bytes);
        int consumed = 0;
        foreach (var (offset, request, expected) in new[] { (3, 5, 4), (5, 11, 2), (1, 8, 0) })
        {
            Array.Fill(bytes, (byte)0x6B);
            int count = sequence.Read(alias.FloatBuffer, offset, request);
            Require(count == expected, "Background callback lost a partial/odd-count read or EOF.");
            var values = new float[bytes.Length / sizeof(float)]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            for (int i = 0; i < count; i++) Near(values[offset + i], (music[consumed + i] + bed[consumed + i]) * .5);
            Require(bytes.Take(offset * sizeof(float)).All(value => value == 0x6B) &&
                bytes.Skip((offset + count) * sizeof(float)).All(value => value == 0x6B),
                "Background callback wrote outside a byte-backed host buffer.");
            consumed += count;
        }
    }

    private static void PendingBed()
    {
        var first = State([.1f, -.1f, .2f, -.2f], [.01f, .02f, .03f, .04f]);
        var second = State([.3f, -.3f, .4f, -.4f], null);
        PlaybackTrackState[] states = [first, second];
        foreach (bool original in new[] { true, false })
        {
            var sequence = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), original, 0);
            var heard = ReadAll(sequence);
            Require(heard.Length == first.OriginalSamples!.Length && sequence.PendingTrackId == second.Id &&
                Math.Abs(sequence.StopPositionSeconds - first.Duration) < 1e-12,
                "An unavailable bed was silently dropped or its track skipped.");
            var pendingSeek = Sequence(states, original, first.Duration + 1.25 / Rate);
            Require(pendingSeek.PendingTrackId == second.Id && ReadAll(pendingSeek).Length == 0,
                "A pending background seek played stale music or Result samples.");
        }
        float[] readyBed = [.05f, .06f, .07f, .08f];
        var ready = second with { BackgroundSamples = readyBed,
            PreviewSamples = second.OriginalSamples!.Zip(readyBed, (music, bed) => music + bed).ToArray() };
        states = [first, second];
        var live = PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), true, 0);
        var head = new float[2]; Require(live.Read(head, 0, 2) == 2, "Live background fixture did not start.");
        states = [first, ready];
        var expectedTail = first.OriginalSamples!.Skip(2).Zip(first.BackgroundSamples!.Skip(2), (music, bed) => music + bed)
            .Concat(ready.OriginalSamples!.Zip(readyBed, (music, bed) => music + bed));
        Require(SameBits(ReadAll(live), expectedTail) && live.IsComplete,
            "A bed published before its boundary was not picked up seamlessly.");
        var resumed = ReadAll(Sequence(states, true, first.Duration + 1.25 / Rate));
        Near(resumed[0], second.OriginalSamples![2] + ready.BackgroundSamples![2]);
    }

    private static void MixedFormats()
    {
        const double seconds = .08;
        static float[] Constant(int rate, int channels, float left, float right = 0)
        {
            var samples = new float[(int)(rate * seconds) * channels];
            for (int frame = 0; frame < samples.Length / channels; frame++)
            {
                samples[frame * channels] = left;
                if (channels == 2) samples[frame * channels + 1] = right;
            }
            return samples;
        }
        var states = new[]
        {
            State(Constant(44100, 2, .1f, -.2f), Constant(44100, 2, .03f, .04f), rate: 44100),
            State(Constant(24000, 1, .2f), Constant(24000, 1, .05f), rate: 24000, channels: 1),
            State(Constant(Rate, 2, -.3f, .1f), Constant(Rate, 2, .07f, -.04f)),
        };
        var heard = ReadAll(Sequence(states)); int frames = (int)(Rate * seconds);
        Require(Math.Abs(heard.Length / 2 - frames * states.Length) <= 4, "The bed changed mixed-rate album timing.");
        int Mid(int index) => (index * frames + frames / 2) * 2;
        Near(heard[Mid(0)], .13, 2e-4); Near(heard[Mid(0) + 1], -.16, 2e-4);
        Near(heard[Mid(1)], .25 * Math.Sqrt(.5), 2e-4); Near(heard[Mid(1) + 1], .25 * Math.Sqrt(.5), 2e-4);
        Near(heard[Mid(2)], -.23); Near(heard[Mid(2) + 1], .06);
    }

    private static void SeekAndContinuity()
    {
        // Distinct consecutive noise frames expose accidental per-track restart,
        // wrong seek offset and reset-to-zero at a track boundary.
        float[] bed = Enumerable.Range(0, 18).Select(index => (index + 1) / 100f).ToArray();
        var first = State(new float[8], bed.Take(8).ToArray());
        var second = State(new float[10], bed.Skip(8).ToArray());
        var states = new[] { first, second };
        Require(SameBits(ReadAll(Sequence(states)), bed), "The background restarted or disappeared at a song boundary.");
        foreach (double seconds in new[] { 0d, 2.25 / Rate, first.Duration, first.Duration + 3.25 / Rate })
        {
            int offset = (int)Math.Floor(seconds * Rate) * 2;
            Require(SameBits(ReadAll(Sequence(states, true, seconds)), bed.Skip(offset)),
                "An Original seek changed the aligned background phase.");
            Require(SameBits(ReadAll(Sequence(states, false, seconds)), bed.Skip(offset)),
                "A Result seek added/restarted the background or shifted its phase.");
        }
    }

    private static void PinnedPublication()
    {
        var originalBed = new[] { .01f, .02f, .03f, .04f };
        var first = State([.1f, -.1f, .2f, -.2f], originalBed);
        var second = State([.3f, -.3f], [.05f, .06f]);
        PlaybackTrackState[] states = [first, second];
        var transport = new PlaybackTransport(PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), true, 0));
        var changed = first with { BackgroundSamples = [.2f, .3f, .4f, .5f] };
        Volatile.Write(ref states, [changed, second]);
        var buffer = new float[2];
        Require(transport.Read(buffer, 0, 2) == 2, "Pinned background fixture did not read.");
        Near(buffer[0], first.OriginalSamples![0] + originalBed[0]);
        Require(transport.TrySeek(PlaybackService.CreateSampleProvider(() => Volatile.Read(ref states), true, 0)),
            "A background publication could not be resolved through the normal seek handoff.");
        Require(transport.Read(buffer, 0, 2) == 2, "Background seek did not read.");
        Near(buffer[0], changed.OriginalSamples![0] + changed.BackgroundSamples![0]);
        var heardTail = new float[8];
        Require(transport.Read(heardTail, 0, heardTail.Length) == 4, "Background handoff changed the remaining album length.");
        Near(heardTail[2], second.OriginalSamples![0] + second.BackgroundSamples![0]);
    }

    private static void InvalidAlignment()
    {
        foreach (bool original in new[] { true, false })
        {
            bool rejected = false;
            try { _ = Sequence([State([.1f, -.1f, .2f, -.2f], [.03f, -.03f], [.1f, -.1f, .2f, -.2f])], original); }
            catch (InvalidOperationException error) when (error.Message.Contains("aligned", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
            Require(rejected, "A mismatched background length reached the callback.");
        }
    }

    private static void LegacyTrackSnapshot()
    {
        float[] music = [.1f, -.2f, .3f, -.4f]; float[] bed = [.02f, .03f, -.04f, -.05f];
        float[] final = [.15f, -.25f, .35f, -.45f];
        var track = new AudioTrack { Original = music, SampleRate = Rate, Channels = 2,
            Render = new RenderResult { Samples = final, BackgroundSamples = bed } };
        var a = PlaybackService.CreateSampleProvider(new[] { track }, true, 0);
        var b = PlaybackService.CreateSampleProvider(new[] { track }, false, 0);
        var heard = new float[4];
        Require(a.Read(heard, 0, heard.Length) == heard.Length, "Legacy original snapshot lost its background.");
        for (int i = 0; i < heard.Length; i++) Near(heard[i], music[i] + bed[i]);
        Require(b.Read(heard, 0, heard.Length) == heard.Length && SameBits(heard, final),
            "Legacy Result snapshot added the background twice.");
    }

    private static void PreparedAlbumFormat()
    {
        float[] nativeMono = [.2f, .3f, -.2f, -.3f];
        float[] prepared = [.14f, .14f, .16f, .16f, .18f, .18f, .2f, .2f,
            -.14f, -.14f, -.16f, -.16f, -.18f, -.18f, -.2f, -.2f];
        float[] bed = Enumerable.Range(0, prepared.Length).Select(index => (index % 2 == 0 ? 1 : -2) * (index + 1) / 1000f).ToArray();
        float[] final = prepared.Zip(bed, (music, noise) => music * .5f + noise).ToArray();
        var mono = new AudioTrack { Original = nativeMono, SampleRate = 24000, Channels = 1,
            Render = new RenderResult { Samples = final, BackgroundSamples = bed, BackgroundOriginalSamples = prepared,
                RenderedSampleRate = Rate, RenderedChannels = 2 } };
        var a = (PlaybackSequence)PlaybackService.CreateSampleProvider(new[] { mono }, true, 0);
        var b = (PlaybackSequence)PlaybackService.CreateSampleProvider(new[] { mono }, false, 0);
        var original = ReadAll(a);
        Require(original.Length == prepared.Length && a.CurrentTrack == null &&
            SameBits(ReadAll(b), final), "A native mono source lost the prepared stereo format or doubled its final background.");
        for (int i = 0; i < prepared.Length; i++) Near(original[i], prepared[i] + bed[i]);

        // Both rendered segments use the same cumulative 48k stereo clock,
        // despite different native rates/channels. The bed cannot enter WDL's
        // per-track filter startup/tail path or collapse to mono at the join.
        float[] continuousBed = Enumerable.Range(0, 34).Select(index => (index + 1) / 100f).ToArray();
        mono.Render = new RenderResult { Samples = continuousBed.Take(16).ToArray(),
            BackgroundSamples = continuousBed.Take(16).ToArray(), BackgroundOriginalSamples = new float[16],
            RenderedSampleRate = Rate, RenderedChannels = 2 };
        var stereo = new AudioTrack { Original = new float[16], SampleRate = 44100, Channels = 2,
            Render = new RenderResult { Samples = continuousBed.Skip(16).ToArray(),
                BackgroundSamples = continuousBed.Skip(16).ToArray(), BackgroundOriginalSamples = new float[18],
                RenderedSampleRate = Rate, RenderedChannels = 2 } };
        var tracks = new[] { mono, stereo };
        foreach (bool mode in new[] { true, false })
        {
            var sequence = (PlaybackSequence)PlaybackService.CreateSampleProvider(tracks, mode, 0);
            Require(SameBits(ReadAll(sequence), continuousBed), "A mixed native-rate album introduced a bed discontinuity at its common-format boundary.");
            var seek = (PlaybackSequence)PlaybackService.CreateSampleProvider(tracks, mode, 10.25 / Rate);
            Require(SameBits(ReadAll(seek), continuousBed.Skip(20)), "A common-format seek restarted or shifted the stereo bed.");
        }
    }
    private static BackgroundLayer SoloLayer(int rate = Rate, int channels = 2)
    {
        var pcm = new float[rate / 10 * channels];
        for (int frame = 0; frame < pcm.Length / channels; frame++)
        {
            pcm[frame * channels] = (float)(.15 * Math.Sin(frame * 2 * Math.PI * 237 / rate));
            if (channels == 2) pcm[frame * 2 + 1] = (float)(.11 * Math.Cos(frame * 2 * Math.PI * 419 / rate));
        }
        return new BackgroundLayer
        {
            Enabled = false, GainDb = -9,
            Clips = [new BackgroundClip { Source = new AudioTrack { Original = pcm, SampleRate = rate, Channels = channels },
                StartSeconds = -.00325, FadeInSeconds = .01, FadeOutSeconds = .02 }]
        };
    }

    private static PlaybackTrackState SoloState(BackgroundLayer layer, double duration = .12)
        => new(Guid.NewGuid(), Rate, 2, duration, null, null, SoloBackground: layer);

    private static void SoloIndependent()
    {
        var layer = SoloLayer(); var state = SoloState(layer);
        var source = layer.Clips[0].Source.Original.ToArray();
        var expected = BackgroundRenderer.RenderSegment(layer with { Enabled = true }, 0, state.Duration, Rate, 2);
        foreach (bool original in new[] { true, false })
        {
            Require(state.IsReady(original), "Solo incorrectly required decoded music or a processed Result.");
            var heard = ReadAll(Sequence([state], original));
            Require(SameBits(heard, expected), "Solo changed the bed's PCM, fixed gain, placement or fades.");
        }
        Require(!layer.Enabled && layer.GainDb == -9 && SameBits(source, layer.Clips[0].Source.Original),
            "Monitoring Solo changed the layer's export inclusion, gain or source PCM.");
        Require(state.OriginalSamples == null && state.PreviewSamples == null,
            "Solo allocated or pretended to contain a music render.");
    }

    private static void SoloMixedFormats()
    {
        var first = SoloLayer(16000, 1); var second = SoloLayer(44100, 2);
        var layer = first with { Enabled = true, Clips = [first.Clips[0], second.Clips[0] with { StartSeconds = .013125 }] };
        var state = SoloState(layer, .125);
        var full = BackgroundRenderer.RenderSegment(layer, 0, state.Duration, Rate, 2);
        foreach (int frame in new[] { 0, 17, 623, 1200, 4700 })
        {
            var heard = ReadAll(Sequence([state], false, (frame + .125) / Rate));
            Require(heard.Length == full.Length - frame * 2, "Solo changed the global sample clock or seek length.");
            for (int i = 0; i < heard.Length; i++) Near(heard[i], full[frame * 2 + i], 2e-6);
        }
    }

    private static void SoloTransport()
    {
        var layer = SoloLayer(); var solo = SoloState(layer);
        float[] music = Enumerable.Repeat(.45f, 1200).ToArray();
        float[] final = Enumerable.Repeat(.7f, 1200).ToArray();
        var normal = State(music, new float[music.Length], final);
        PlaybackTrackState[] live = [normal];
        var transport = new PlaybackTransport(PlaybackService.CreateSampleProvider(() => live, false, 0));
        var head = new float[20]; Require(transport.Read(head, 0, head.Length) == head.Length, "Music transport did not start.");
        live = [solo];
        Require(transport.TrySeek(PlaybackService.CreateSampleProvider(() => live, false, 37.125 / Rate)),
            "Solo could not replace the monitor provider at a callback boundary.");
        var heard = new float[70]; Require(transport.Read(heard, 0, heard.Length) == heard.Length, "Solo transport did not read.");
        var expected = BackgroundRenderer.RenderSegment(layer with { Enabled = true }, 37d / Rate, heard.Length / 2d / Rate, Rate, 2);
        Require(SameBits(heard, expected) && final.All(x => x == .7f) && music.All(x => x == .45f),
            "Solo mixed music, applied A/B matching, changed export PCM or restarted the bed.");
    }

    private static void SoloByteBuffer()
    {
        var layer = SoloLayer(16000, 1) with { Enabled = true };
        var reader = new BackgroundRenderer.SegmentReader(layer, Rate, 2);
        var bytes = Enumerable.Repeat((byte)0x5A, 100 * sizeof(float)).ToArray();
        var alias = new WaveBuffer(bytes);
        reader.Read(.00625, alias.FloatBuffer, 7, 23);
        var actual = new float[100]; Buffer.BlockCopy(bytes, 0, actual, 0, bytes.Length);
        var expected = BackgroundRenderer.RenderSegment(layer, .00625, 23d / Rate, Rate, 2);
        Require(SameBits(actual.Skip(7).Take(46), expected) &&
            bytes.Take(7 * sizeof(float)).All(x => x == 0x5A) && bytes.Skip(53 * sizeof(float)).All(x => x == 0x5A),
            "A streaming read wrote outside its requested byte-backed frame range.");
    }

}

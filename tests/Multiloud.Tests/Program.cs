using Multiloud.Core;

// A dependency-free executable suite: failures propagate to CI as a nonzero exit.
const int SampleRate = 48000;
int failures = 0;
var checks = new (string Name, Action Run)[]
{
    ("BS.1770 calibration: duplicated stereo and mono 1 kHz", Calibration),
    ("Loudness matching across tracks", MatchTracks),
    ("Matching strength blends automatic correction in dB", MatchingBlend),
    ("Input gain precedes matching and peak protection", InputGain),
    ("Join corrections affect edges without changing manual envelopes", TransitionEdges),
    ("Join suggestions preserve fades, silence and deliberate target offsets", TransitionGuards),
    ("Derived and authored envelopes sum linearly without zero-layer knots", CombinedEnvelopes),
    ("Envelope strength and per-track trim", StrengthAndTrim),
    ("Long quiet/loud sections receive bounded slow corrections", LongSections),
    ("Deliberate fade-out stays unboosted", FadePreservation),
    ("Silence is not amplified", Silence),
    ("Limiter preserves duration and stereo linkage at ceiling", Limiter),
    ("Inter-sample peaks remain under true-peak ceiling", InterSamplePeaks),
    ("Cancellation interrupts analysis and rendering", Cancellation),
};
var allChecks = checks.Concat(LoudnessCacheChecks.All).Concat(IncrementalRenderChecks.All).Concat(WaveformPyramidChecks.All)
    .Concat(AdaptiveMaximizerChecks.All).Concat(PeakDynamicsGuardChecks.All).Concat(SpectralProtectionChecks.All).Concat(BackgroundHeadroomChecks.All)
    .Concat(BassSensitivityChecks.All).Concat(AbruptSectionChecks.All)
    .Concat(BackgroundMixChecks.All).Concat(SliceChecks.All).Concat(ProjectEnvelopeChecks.All).Concat(ExportBoundaryChecks.All).Concat(ExportWriterChecks.All).Concat(LiveMixChecks.All).Concat(GeneratedSilenceChecks.All).Concat(MergeChecks.All).ToArray();
foreach (var (name, run) in allChecks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{allChecks.Length - failures}/{allChecks.Length} checks passed.");
return failures == 0 ? 0 : 1;

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Near(double actual, double expected, double tolerance, string message)
{
    Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
        $"{message}: expected {expected:F3} ± {tolerance:F3}, got {actual:F3}.");
}

static float[] Sine(double seconds, double amplitude, int channels = 2, double frequency = 1000, double phase = 0)
{
    int frames = (int)Math.Round(seconds * SampleRate);
    var data = new float[frames * channels];
    for (int i = 0; i < frames; i++)
    {
        float sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / SampleRate + phase));
        for (int ch = 0; ch < channels; ch++) data[i * channels + ch] = sample;
    }
    return data;
}

static void Calibration()
{
    var stereo = LoudnessAnalyzer.Analyze(Sine(5, 0.1), SampleRate, 2);
    var mono = LoudnessAnalyzer.Analyze(Sine(5, 0.1, 1), SampleRate, 1);
    Near(stereo.IntegratedLufs, -20, 0.25, "Stereo reference tone LUFS");
    Near(mono.IntegratedLufs, -23.01, 0.25, "Mono reference tone LUFS");
    Near(stereo.TruePeakDb, -20, 0.2, "Reference tone peak");
}

static void MatchTracks()
{
    var settings = new ProcessingSettings { LevelingEnabled = false, TargetLufs = -18 };
    var quiet = AudioProcessor.Render(Sine(5, 0.025), SampleRate, 2, [], 0, settings);
    var loud = AudioProcessor.Render(Sine(5, 0.4), SampleRate, 2, [], 0, settings);
    Near(quiet.OutputLufs, -18, 0.25, "Quiet track target");
    Near(loud.OutputLufs, -18, 0.25, "Loud track target");
    Near(quiet.OutputLufs, loud.OutputLufs, 0.1, "Matched track consistency");
    Require(quiet.MatchGainDb > 0 && loud.MatchGainDb < 0, "Expected boost and attenuation respectively");
}

static void StrengthAndTrim()
{
    var input = Sine(2, 0.1);
    var settings = new ProcessingSettings
    {
        MatchingEnabled = false, LimiterEnabled = false, Strength = 0,
    };
    EnvelopePoint[] envelope = [new(0, 6), new(2, -6)];
    var unchanged = AudioProcessor.Render(input, SampleRate, 2, envelope, 0, settings);
    Require(unchanged.Samples.Length == input.Length, "Processing changed sample count");
    for (int i = 0; i < input.Length; i++)
        Near(unchanged.Samples[i], input[i], 1e-7, "Zero-strength envelope must bypass");
    var trimmed = AudioProcessor.Render(input, SampleRate, 2, envelope, -6, settings);
    double factor = Math.Pow(10, -6 / 20.0);
    for (int i = 0; i < input.Length; i += 31)
        Near(trimmed.Samples[i], input[i] * factor, 1e-7, "Manual trim should apply independently");
    Near(AudioProcessor.EnvelopeGainAt(envelope, 1), 0, 1e-8, "Envelope interpolates in dB");
}

static void LongSections()
{
    // Sustained 20-second sections are long enough to require leveling, unlike drum hits.
    var input = new float[60 * SampleRate * 2];
    var section = Sine(20, 0.05);
    Array.Copy(section, 0, input, 0, section.Length);
    section = Sine(20, 0.2);
    Array.Copy(section, 0, input, 20 * SampleRate * 2, section.Length);
    section = Sine(20, 0.05);
    Array.Copy(section, 0, input, 40 * SampleRate * 2, section.Length);
    var analysis = LoudnessAnalyzer.Analyze(input, SampleRate, 2);
    var settings = new ProcessingSettings { MaxLevelingDb = 6, TransitionSeconds = 8 };
    var points = EnvelopeDesigner.Design(analysis, 60, settings);
    Require(points.Count >= 3, "Section envelope needs multiple points");
    Require(points.All(p => p.Time >= 0 && p.Time <= 60 && Math.Abs(p.GainDb) <= 6.001), "Envelope exceeded time/gain bounds");
    double quietGain = AudioProcessor.EnvelopeGainAt(points, 10);
    double loudGain = AudioProcessor.EnvelopeGainAt(points, 30);
    Require(quietGain > loudGain + 1, $"Expected quieter section to receive more gain ({quietGain:F2} vs {loudGain:F2})");
    for (int i = 1; i < points.Count; i++)
    {
        double dt = points[i].Time - points[i - 1].Time;
        Require(dt > 0, "Envelope timestamps must strictly increase");
        // Cached detail permits a faster continuous ramp only at the two
        // confirmed sustained steps; ordinary section drift stays slow.
        double slope = Math.Abs(points[i].GainDb - points[i - 1].GainDb) / dt;
        bool stepWindow = new[] { 20d, 40d }.Any(time => points[i - 1].Time >= time - 2 && points[i].Time <= time + 2);
        Require(slope <= 4.01 || stepWindow && slope <= 6.01,
            "Envelope transition exceeded its safe slew or accelerated outside a sustained step");
    }
}

static void Silence()
{
    var input = new float[SampleRate * 2 * 2];
    var analysis = LoudnessAnalyzer.Analyze(input, SampleRate, 2);
    var points = EnvelopeDesigner.Design(analysis, 2, new ProcessingSettings());
    var result = AudioProcessor.Render(input, SampleRate, 2, points, 0, new ProcessingSettings());
    Require(result.Samples.All(s => s == 0), "Silence produced nonzero samples");
    Require(double.IsFinite(result.OutputLufs) && result.OutputLufs <= -69.9, "Silent loudness must be finite and gated");
    Near(result.MatchGainDb, 0, 0.001, "Silence must not receive matching gain");
    Require(points.All(p => Math.Abs(p.GainDb) < 0.001), "Silence must not receive leveling gain");
}

static void FadePreservation()
{
    var input = Sine(30, 0.2);
    for (int frame = 20 * SampleRate; frame < input.Length / 2; frame++)
    {
        double fade = Math.Max(0, (30 - frame / (double)SampleRate) / 10);
        input[frame * 2] *= (float)fade;
        input[frame * 2 + 1] *= (float)fade;
    }
    var analysis = LoudnessAnalyzer.Analyze(input, SampleRate, 2);
    var envelope = EnvelopeDesigner.Design(analysis, 30, new ProcessingSettings());
    Require(AudioProcessor.EnvelopeGainAt(envelope, 28.5) <= 0.1,
        "A deliberate terminal fade must not be interpreted as a quiet section needing boost");
    Require(AudioProcessor.EnvelopeGainAt(envelope, 30) <= 0.1,
        "Envelope must not boost the end of the fade");
}

static void Limiter()
{
    var input = Sine(3, 0.8);
    for (int i = 0; i < input.Length; i += 2) input[i + 1] = input[i] * 0.5f;
    // Oversized sparse peaks exercise the lookahead without changing stereo ratio.
    for (int frame = 12000; frame < input.Length / 2; frame += 24000)
    {
        input[frame * 2] = 1.8f;
        input[frame * 2 + 1] = 0.9f;
    }
    var settings = new ProcessingSettings { MatchingEnabled = false, LevelingEnabled = false, CeilingDb = -1 };
    var output = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings);
    Require(output.Samples.Length == input.Length, "Limiter changed duration");
    double ceiling = Math.Pow(10, -1 / 20.0);
    Require(output.Samples.All(s => float.IsFinite(s) && Math.Abs(s) <= ceiling + 1e-5), "Sample peak exceeded ceiling");
    Require(output.TruePeakDb <= -0.97, $"True peak exceeded ceiling: {output.TruePeakDb:F3}");
    Require(output.MaxReductionDb > 0, "Limiter should report gain reduction");
    for (int i = 0; i < output.Samples.Length; i += 2)
        Near(output.Samples[i + 1], output.Samples[i] * 0.5, 1e-6, "Limiter must be stereo linked");
}

static void InterSamplePeaks()
{
    // A quarter-sample-phase high-frequency sine has reconstructed peaks above its sample peaks.
    var input = Sine(2, 1.15, 2, SampleRate / 4.0, Math.PI / 4);
    var settings = new ProcessingSettings { MatchingEnabled = false, LevelingEnabled = false, CeilingDb = -1 };
    var output = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings);
    Require(output.TruePeakDb <= -0.97, $"Inter-sample peak exceeded ceiling: {output.TruePeakDb:F3}");
    var measured = LoudnessAnalyzer.Analyze(output.Samples, SampleRate, 2);
    Require(measured.TruePeakDb <= -0.97, $"Independent remeasurement exceeded ceiling: {measured.TruePeakDb:F3}");
}

static void Cancellation()
{
    using var source = new CancellationTokenSource();
    source.Cancel();
    int cancelled = 0;
    try { LoudnessAnalyzer.Analyze(Sine(1, 0.1), SampleRate, 2, source.Token); }
    catch (OperationCanceledException) { cancelled++; }
    try { AudioProcessor.Render(Sine(1, 0.1), SampleRate, 2, [], 0, new ProcessingSettings(), source.Token); }
    catch (OperationCanceledException) { cancelled++; }
    Require(cancelled == 2, "Both analysis and rendering must honor cancellation");
}

static void MatchingBlend()
{
    var input = Sine(3, .05);
    var settings = new ProcessingSettings { LevelingEnabled = false, LimiterEnabled = false, TargetLufs = -18 };
    var none = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings with { MatchingStrength = 0 });
    var half = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings with { MatchingStrength = .5 });
    var full = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings);
    Near(none.MatchGainDb, 0, 1e-8, "Zero matching strength applies no correction");
    Near(half.MatchGainDb, full.MatchGainDb * .5, .01, "Half matching strength blends dB correction");
    Near(half.OutputLufs, (none.OutputLufs + full.OutputLufs) * .5, .03, "Blended loudness lies halfway in LU");
    Near(full.OutputLufs, -18, .1, "Full strength reaches target");
}

static void InputGain()
{
    var input = Sine(3, .05);
    var settings = new ProcessingSettings { LevelingEnabled = false, LimiterEnabled = false, MatchingEnabled = false };
    var baseline = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings);
    var raised = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings, inputGainDb: 6);
    Near(raised.OutputLufs - baseline.OutputLufs, 6, .02, "Input gain applies before processing");
    var matched = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings with { MatchingEnabled = true, TargetLufs = -18 }, inputGainDb: 6);
    var unraised = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings with { MatchingEnabled = true, TargetLufs = -18 });
    Near(matched.OutputLufs, unraised.OutputLufs, .02, "Full matching compensates input gain");
    Near(matched.MatchGainDb - unraised.MatchGainDb, -6, .02, "Input gain is excluded from reported automatic correction");
    var limited = AudioProcessor.Render(input, SampleRate, 2, [], 0, settings with { LimiterEnabled = true }, inputGainDb: 30);
    Require(limited.TruePeakDb <= -.97 && limited.MaxReductionDb > 0, "Input gain bypassed peak protection");
}

static AudioTrack ProfileTrack(string name, double first = -20, double last = -20)
{
    var frames = Enumerable.Range(0, 40).Select(i => new LoudnessFrame(i + .5, i < 8 ? first : i >= 32 ? last : -20)).ToList();
    return new AudioTrack
    {
        FilePath = name, SampleRate = 100, Channels = 1, Original = new float[4000],
        Analysis = new AnalysisResult { IntegratedLufs = -20, ShortTerm = frames },
        Envelope = [new(0, 0), new(15, 1), new(25, -1), new(40, 0)]
    };
}

static void TransitionEdges()
{
    var a = ProfileTrack("A", last: -27); var b = ProfileTrack("B", first: -13);
    var c = ProfileTrack("C", last: -26); var d = ProfileTrack("D", first: -14);
    AudioTrack[] tracks = [a, b, c, d]; var settings = new ProcessingSettings { TransitionSeconds = 8 };
    var authored = tracks.Select(t => t.Envelope).ToArray();
    TransitionDesigner.Update(tracks, settings);
    Require(TransitionDesigner.GainAt(a, 40) > 0 && TransitionDesigner.GainAt(b, 0) < 0, "Quiet/loud join should suggest opposite edge corrections");
    foreach (var track in tracks)
    {
        Require(track.TransitionEnvelope.All(p => Math.Abs(p.GainDb) <= 2.0001), "Join corrections exceeded conservative bound");
        for (double t = 8; t <= 32; t += 2) Near(TransitionDesigner.GainAt(track, t), 0, 1e-8, "Interior must remain unchanged");
    }
    var originalEdges = tracks.Select(t => t.TransitionEnvelope).ToArray();
    Require(TransitionDesigner.Update(tracks, settings).Count == 0, "Repeated update should be a no-op");
    Require(tracks.Select((t, i) => ReferenceEquals(t.TransitionEnvelope, originalEdges[i])).All(x => x), "Unchanged curves must retain references");
    // A manual change at D's beginning must not be reconsidered when removing
    // B creates A→C elsewhere in the album. C→D is the same existing join.
    var authoredBefore = d.Envelope.ToArray();
    d.Envelope[0] = new EnvelopePoint(0, -8);
    TransitionDesigner.Update([a, c, d], settings, previousOrder: tracks);
    Require(ReferenceEquals(d.TransitionEnvelope, originalEdges[3]), "An unaffected join rebuilt its curve");
    d.Envelope.Clear(); d.Envelope.AddRange(authoredBefore);
    TransitionDesigner.Update(tracks, settings);
    Require(tracks.Select((t, i) => t.TransitionEnvelope.SequenceEqual(originalEdges[i])).All(x => x), "Reordering accumulated prior edge gain");
    Require(tracks.Select((t, i) => ReferenceEquals(t.Envelope, authored[i])).All(x => x), "Join refresh replaced manual envelope data");
}

static void TransitionGuards()
{
    var fade = ProfileTrack("Fade", last: -20);
    fade.Analysis = new AnalysisResult
    {
        IntegratedLufs = -20,
        ShortTerm = Enumerable.Range(0, 40).Select(i => new LoudnessFrame(i + .5, i >= 32 ? -20 - (i - 31) * 2 : -20)).ToList()
    };
    var next = ProfileTrack("Next", first: -12);
    TransitionDesigner.Update([fade, next], new ProcessingSettings());
    Near(TransitionDesigner.GainAt(fade, 40), 0, 1e-8, "Fade tail must not be raised");
    Near(TransitionDesigner.GainAt(next, 0), 0, 1e-8, "Intentional fade must not force a neighboring correction");
    var a = ProfileTrack("A", last: -27); var b = ProfileTrack("B", first: -13);
    TransitionDesigner.Update([a, b], new ProcessingSettings());
    var edgeA = a.TransitionEnvelope; var edgeB = b.TransitionEnvelope;
    a.TargetLufs = -23; b.TargetLufs = -11; a.InputGainDb = 6; b.InputGainDb = -6; a.TrimDb = 3;
    TransitionDesigner.Update([a, b], new ProcessingSettings { MatchingStrength = .3, Strength = .2 });
    Require(ReferenceEquals(a.TransitionEnvelope, edgeA) && ReferenceEquals(b.TransitionEnvelope, edgeB), "Join correction undid deliberate target/input/trim offsets or changed with blend knobs");
    TransitionDesigner.Update([a, b], new ProcessingSettings { LevelingEnabled = false, Strength = 0 });
    Require(ReferenceEquals(a.TransitionEnvelope, edgeA) && ReferenceEquals(b.TransitionEnvelope, edgeB), "Bypass or zero Strength changed the raw edge suggestion");
    var silent = ProfileTrack("Silent", last: -80);
    TransitionDesigner.Update([silent, b], new ProcessingSettings());
    Near(TransitionDesigner.GainAt(silent, 40), 0, 1e-8, "Silent tail must not be boosted");
}

static void CombinedEnvelopes()
{
    EnvelopePoint[] authored = [new(0, 0), new(10, 4), new(20, 0)];
    EnvelopePoint[] edges = [new(0, 2), new(5, 0), new(15, 0), new(20, -2)];
    var combined = TransitionDesigner.Combine(authored, edges);
    for (double t = 0; t <= 20; t += .25)
        Near(AudioProcessor.EnvelopeGainAt(combined, t), AudioProcessor.EnvelopeGainAt(authored, t) + AudioProcessor.EnvelopeGainAt(edges, t), 1e-10, "Combined envelope sum");
    var zero = TransitionDesigner.Combine(authored, [new(0, 0), new(8, 0), new(20, 0)]);
    Require(zero.SequenceEqual(authored), "Zero edge layer introduced irrelevant cache/display knots");
}

using Multiloud.App.Services;
using Multiloud.Core;
using System.Runtime.CompilerServices;

internal static class PreviewCacheTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Preview cache returns the exact rendered PCM and peaks", RoundTrip),
        ("Disabled leveling and zero strength ignore envelope controls", IgnoredLeveling),
        ("Empty and flat envelopes reuse the bypassed leveling preview", FlatEnvelopes),
        ("Disabled matching ignores target loudness", IgnoredMatching),
        ("Envelope design settings do not invalidate rendered audio", IgnoredDesignSettings),
        ("Effective audio changes select distinct cache variants", EffectiveChanges),
        ("Maximizer settings isolate active variants and reuse bypassed audio", MaximizerVariants),
        ("Bass protection and dynamics safeguards identify only their effective audio variants", AdvancedProtectionVariants),
        ("Non-finite advanced protection settings cannot enter the preview cache", InvalidProtectionSettings),
        ("Disabled peak protection ignores the ceiling in music and background variants", IgnoredCeiling),
        ("Input gain selects precise variants within its effective limits", InputGain),
        ("Per-track targets override global matching targets canonically", TargetOverrides),
        ("Zero matching strength reuses matching bypass and ignores targets", MatchingStrength),
        ("Transition corrections invalidate only effective leveling curves", TransitionCurves),
        ("Inserting and removing points on an authored envelope reuses preview audio", RedundantAuthoredPoints),
        ("Redundant authored points stay redundant with automatic transition curves", RedundantCombinedPoints),
        ("Track identity and source format isolate previews", TrackIsolation),
        ("Shared-source frame offsets and lengths isolate preview variants", SharedSourceRanges),
        ("Replacing a source in the same slot isolates previews and preserves undo variants", SourceReplacement),
        ("Preview cache evicts the least recently used variant", LeastRecentlyUsed),
        ("Preview cache stays within its byte budget", MemoryBudget),
        ("Preview cache accounts for retained incremental render state", IntermediateMemoryBudget),
        ("Preview cache accounts for waveform pyramid memory without duplicating PCM", WaveformMemoryBudget),
        ("Replacing a cache key does not double-count retained memory", ReplacementAccounting),
        ("Oversized previews do not evict unrelated cached audio", OversizedEntries),
        ("Removing a track clears only its variants", RemoveAndClear),
        ("Zero and negative cache budgets have explicit behavior", CapacityPolicy),
        ("Undo references reuse ready track buffers after the preview cache evicts them", HistoryEviction),
        ("Undo references reject audio from different effective settings or sources", HistoryIsolation),
        ("Undo references do not retain otherwise unused audio or display buffers", HistoryLifetime),
    ];

    private static readonly ProcessingSettings Settings = new();

    private static void HistoryEviction()
    {
        var track = Track(); var render = Render(); var peaks = Peaks();
        track.Render = render; track.PreviewPeaks = peaks;
        string key = PreviewCache.CreateKey(track, Settings);
        var history = new PreviewHistoryReference(key, render, peaks);
        var cache = new PreviewCache(0);
        cache.Store(key, render, peaks);
        Require(cache.Count == 0 && history.TryGet(key, out var previous) &&
            ReferenceEquals(previous.Render, track.Render) && ReferenceEquals(previous.Peaks, track.PreviewPeaks),
            "Cache eviction prevented Undo from reusing the exact ready buffers still owned by its removed track.");
        GC.KeepAlive(track);
    }

    private static void HistoryIsolation()
    {
        var track = Track(); var render = Render(); var peaks = Peaks();
        var history = new PreviewHistoryReference(PreviewCache.CreateKey(track, Settings), render, peaks);
        Require(!history.TryGet(PreviewCache.CreateKey(track, Settings with { TargetLufs = -22 }), out var wrong) && wrong == null,
            "Undo accepted a result rendered for a different target.");
        var replacement = new AudioTrack { Id = track.Id, Original = track.Original, SampleRate = track.SampleRate,
            Channels = track.Channels, Envelope = track.Envelope };
        Require(!history.TryGet(PreviewCache.CreateKey(replacement, Settings), out wrong) && wrong == null,
            "Undo accepted a result rendered from an obsolete source.");
        GC.KeepAlive(render); GC.KeepAlive(peaks);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PreviewHistoryReference History, WeakReference<RenderResult> Render, WeakReference<List<PeakBin>> Peaks) UnownedHistory()
    {
        var render = Render(); var peaks = Peaks();
        return (new PreviewHistoryReference("collected-history", render, peaks), new(render), new(peaks));
    }

    private static void HistoryLifetime()
    {
        var state = UnownedHistory();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!state.Render.TryGetTarget(out _) && !state.Peaks.TryGetTarget(out _) &&
            !state.History.TryGet("collected-history", out var preview) && preview == null,
            "Undo history retained audio outside the active tracks and bounded preview cache.");
    }
    private static AudioTrack Track() => new()
    {
        FilePath = "fixture.wav", SampleRate = 48000, Channels = 2,
        Original = new float[32],
        Envelope = [new(0, -2), new(.0002, 1), new(.0003333333333333333, 0)],
    };
    private static RenderResult Render(int samples = 32, float value = .1f) => new()
    {
        Samples = Enumerable.Repeat(value, samples).ToArray(), OutputLufs = -14, TruePeakDb = -1,
    };
    private static List<PeakBin> Peaks() => [new(-.1f, .1f), new(-.2f, .2f)];
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void EqualKey(AudioTrack track, ProcessingSettings first, ProcessingSettings second, string message)
        => Require(PreviewCache.CreateKey(track, first) == PreviewCache.CreateKey(track, second), message);

    private static void RoundTrip()
    {
        var track = Track(); var render = Render(); var peaks = Peaks();
        var cache = new PreviewCache(); string key = PreviewCache.CreateKey(track, Settings);
        cache.Store(key, render, peaks);
        // Envelope editor snapshots create new point/list objects with the same values.
        track.Envelope = track.Envelope.Select(point => new EnvelopePoint(point.Time, point.GainDb)).ToList();
        Require(cache.TryGet(PreviewCache.CreateKey(track, Settings), out var cached), "Equivalent editor snapshot missed its cached preview");
        Require(ReferenceEquals(cached!.Render, render) && ReferenceEquals(cached.Render.Samples, render.Samples) && ReferenceEquals(cached.Peaks, peaks),
            "Cache hit must reuse the measured render and peaks without copying PCM");
        Require(cache.Count == 1, "Round trip created an extra cache entry");
    }

    private static void IgnoredLeveling()
    {
        var track = Track();
        var disabled = Settings with { LevelingEnabled = false, Strength = .2 };
        string key = PreviewCache.CreateKey(track, disabled);
        track.Envelope = [new(0, 12), new(track.Duration, -8)];
        Require(key == PreviewCache.CreateKey(track, disabled with { Strength = .95 }),
            "Strength/envelope changes cannot affect audio while leveling is disabled");
        EqualKey(track, disabled, Settings with { LevelingEnabled = true, Strength = 0 },
            "Zero-strength leveling and disabled leveling must reuse the same audio variant");
        string zeroKey = PreviewCache.CreateKey(track, Settings with { Strength = 0 });
        track.Envelope = [new(0, -12), new(track.Duration, 9)];
        Require(zeroKey == PreviewCache.CreateKey(track, Settings with { Strength = 0 }), "Zero strength must ignore envelope edits");
    }

    private static void IgnoredMatching()
    {
        var track = Track();
        EqualKey(track, Settings with { MatchingEnabled = false, TargetLufs = -14 }, Settings with { MatchingEnabled = false, TargetLufs = -24 },
            "Changing target LUFS with matching disabled must preserve the cached audio");
    }

    private static void FlatEnvelopes()
    {
        var track = Track();
        string bypass = PreviewCache.CreateKey(track, Settings with { LevelingEnabled = false });
        track.Envelope = [];
        Require(bypass == PreviewCache.CreateKey(track, Settings with { Strength = .25 }) &&
            bypass == PreviewCache.CreateKey(track, Settings with { Strength = 1 }),
            "An empty envelope cannot change audio at any strength");
        track.Envelope = [new(0, 0), new(track.Duration / 2, -0.0), new(track.Duration, 0)];
        Require(bypass == PreviewCache.CreateKey(track, Settings with { Strength = .1 }) &&
            bypass == PreviewCache.CreateKey(track, Settings with { Strength = .95 }),
            "An all-zero envelope must share the bypass key regardless of strength");
        track.Envelope = [new(double.NaN, 3), new(0, double.PositiveInfinity)];
        Require(bypass == PreviewCache.CreateKey(track, Settings),
            "Points rejected by the renderer must not invalidate an effectively empty envelope");
        track.Envelope = [new(0, 0), new(track.Duration / 2, .000001), new(track.Duration, 0)];
        string active = PreviewCache.CreateKey(track, Settings);
        Require(active != bypass && active != PreviewCache.CreateKey(track, Settings with { Strength = 1 }),
            "A nonzero envelope must remain sensitive to Apply and strength changes");
    }

    private static void IgnoredDesignSettings()
    {
        var track = Track();
        EqualKey(track, Settings with { MaxLevelingDb = 1, TransitionSeconds = 2 }, Settings with { MaxLevelingDb = 12, TransitionSeconds = 30 },
            "Envelope design controls do not change an already supplied gain envelope");
    }

    private static void EffectiveChanges()
    {
        var track = Track(); var cache = new PreviewCache();
        cache.Store(PreviewCache.CreateKey(track, Settings), Render(), Peaks());
        var variants = new[]
        {
            Settings with { Strength = .9 }, Settings with { LevelingEnabled = false },
            Settings with { MatchingEnabled = false }, Settings with { TargetLufs = -18 },
            Settings with { LimiterEnabled = false }, Settings with { CeilingDb = -2 },
        };
        foreach (var variant in variants)
            Require(!cache.TryGet(PreviewCache.CreateKey(track, variant), out _), "An effective DSP control change reused stale processed audio");
        track.TrimDb = 1;
        Require(!cache.TryGet(PreviewCache.CreateKey(track, Settings), out _), "Manual track gain change reused stale audio");
        track.TrimDb = 0; track.Envelope[1] = track.Envelope[1] with { GainDb = 2 };
        Require(!cache.TryGet(PreviewCache.CreateKey(track, Settings), out _), "Envelope edit reused stale audio");
    }

    private static void MaximizerVariants()
    {
        var track = Track(); var cache = new PreviewCache();
        string original = PreviewCache.CreateKey(track, Settings);
        var render = Render(); var peaks = Peaks(); cache.Store(original, render, peaks);
        foreach (var changed in new[]
        {
            Settings with { MaximizerStyle = MaximizerStyle.Punchy },
            Settings with { MaximizerCharacter = .3 }, Settings with { TransientPreservation = .8 }
        })
            Require(!cache.TryGet(PreviewCache.CreateKey(track, changed), out _), "A changed maximizer reused an active cached result");
        var bypass = Settings with { LimiterEnabled = false };
        string bypassKey = PreviewCache.CreateKey(track, bypass);
        cache.Store(bypassKey, render, peaks);
        var edited = bypass with { MaximizerStyle = MaximizerStyle.Punchy, MaximizerCharacter = .17, TransientPreservation = .83 };
        Require(PreviewCache.CreateKey(track, edited) == bypassKey && cache.TryGet(bypassKey, out var hit) &&
            ReferenceEquals(hit.Render, render) && ReferenceEquals(hit.Peaks, peaks), "Bypassed maximizer edits invalidated unchanged PCM");
        Require(cache.TryGet(original, out var old) && ReferenceEquals(old.Render, render), "Changing variants lost the original undo result");
        var trace = new RenderResult { Samples = render.Samples, ReductionTrace = Array.AsReadOnly(new[] { new LimiterReductionPoint(0, 2), new LimiterReductionPoint(.1, 1) }) };
        Require(PreviewCache.EstimateBytes(original, trace, peaks) > PreviewCache.EstimateBytes(original, render, peaks),
            "The preview cache omitted retained reduction-trace memory");
    }

    private static void IgnoredCeiling()
    {
        var track = Track(); var off = Settings with { LimiterEnabled = false };
        EqualKey(track, off with { CeilingDb = -12 }, off with { CeilingDb = 0 },
            "A disabled peak-protection ceiling invalidated unchanged music.");
        var background = new BackgroundLayer
        {
            Clips = [new BackgroundClip { Source = Track() }]
        };
        Require(PreviewCache.CreateKey(track, off with { CeilingDb = -12 }, background, 0) ==
            PreviewCache.CreateKey(track, off with { CeilingDb = 0 }, background, 0),
            "A disabled peak-protection ceiling invalidated unchanged background mixing.");
        Require(PreviewCache.CreateKey(track, off with { LimiterEnabled = true, CeilingDb = -12 }) !=
            PreviewCache.CreateKey(track, off with { LimiterEnabled = true, CeilingDb = 0 }),
            "An enabled peak-protection ceiling no longer identifies its processed audio.");
    }

    private static void AdvancedProtectionVariants()
    {
        var track = Track();
        var baseline = Settings with { BassProtection = .5, DynamicsGuardEnabled = false, MaxSustainedReductionDb = 3 };
        var background = new BackgroundLayer { Clips = [new BackgroundClip { Source = Track() }] };
        foreach (bool withBackground in new[] { false, true })
        {
            string Key(ProcessingSettings settings) => withBackground
                ? PreviewCache.CreateKey(track, settings, background, 0)
                : PreviewCache.CreateKey(track, settings);
            string original = Key(baseline);
            foreach (var changed in new[]
            {
                baseline with { BassProtection = 0 }, baseline with { BassProtection = 1 },
                baseline with { DynamicsGuardEnabled = true }
            })
                Require(Key(changed) != original, "An effective advanced peak-protection change reused stale audio.");

            Require(Key(baseline with { MaxSustainedReductionDb = 0 }) == Key(baseline with { MaxSustainedReductionDb = 12 }),
                "A disabled dynamics safeguard invalidated audio when only its inactive limit changed.");
            var guarded = baseline with { DynamicsGuardEnabled = true };
            Require(Key(guarded with { MaxSustainedReductionDb = 0 }) != Key(guarded with { MaxSustainedReductionDb = 12 }),
                "An enabled dynamics safeguard ignored its sustained-reduction limit.");

            var bypass = baseline with { LimiterEnabled = false };
            Require(Key(bypass) == Key(bypass with
            {
                BassProtection = 1, DynamicsGuardEnabled = true, MaxSustainedReductionDb = 12,
                MaximizerStyle = MaximizerStyle.Punchy, MaximizerCharacter = .1, TransientPreservation = .9
            }), "Editing advanced settings in a bypassed protection module invalidated the unchanged mix.");
            Require(Key(baseline with { BassProtection = -1 }) == Key(baseline with { BassProtection = 0 }) &&
                Key(baseline with { BassProtection = 2 }) == Key(baseline with { BassProtection = 1 }),
                "Bass protection did not use the renderer's effective clamped amount.");
            Require(Key(guarded with { MaxSustainedReductionDb = -1 }) == Key(guarded with { MaxSustainedReductionDb = 0 }) &&
                Key(guarded with { MaxSustainedReductionDb = 13 }) == Key(guarded with { MaxSustainedReductionDb = 12 }),
                "The dynamics safeguard did not use the renderer's effective clamped limit.");
        }
    }

    private static void InvalidProtectionSettings()
    {
        var track = Track();
        foreach (bool enabled in new[] { false, true })
        foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        foreach (var settings in new[]
        {
            Settings with { LimiterEnabled = enabled, BassProtection = invalid },
            Settings with { LimiterEnabled = enabled, DynamicsGuardEnabled = enabled, MaxSustainedReductionDb = invalid }
        })
        {
            bool rejected = false;
            try { _ = PreviewCache.CreateKey(track, settings); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "A non-finite advanced peak-protection control entered the audio cache.");
        }
    }

    private static void SharedSourceRanges()
    {
        var track = Track();
        track.OriginalFrameLength = 8;
        string first = PreviewCache.CreateKey(track, Settings);
        track.OriginalFrameOffset = 1;
        Require(first != PreviewCache.CreateKey(track, Settings), "Shared source offset was omitted from its preview identity.");
        track.OriginalFrameOffset = 0; track.OriginalFrameLength = 9;
        Require(first != PreviewCache.CreateKey(track, Settings), "Shared source length was omitted from its preview identity.");
        track.OriginalFrameLength = 8;
        Require(first == PreviewCache.CreateKey(track, Settings), "Restoring a shared source range did not restore its preview identity.");
        track.OriginalFrameLength = 16;
        string full = PreviewCache.CreateKey(track, Settings);
        track.OriginalFrameLength = null;
        Require(full == PreviewCache.CreateKey(track, Settings), "Equivalent explicit and implicit full source lengths use different variants.");
    }

    private static void TrackIsolation()
    {
        var first = Track(); var second = Track(); var cache = new PreviewCache();
        cache.Store(PreviewCache.CreateKey(first, Settings), Render(), Peaks());
        Require(!cache.TryGet(PreviewCache.CreateKey(second, Settings), out _), "Different tracks with identical paths/formats must not share PCM");
        string original = PreviewCache.CreateKey(first, Settings);
        first.SampleRate = 44100;
        Require(original != PreviewCache.CreateKey(first, Settings), "Source sample rate was omitted from cache identity");
        first.SampleRate = 48000; first.Channels = 1;
        Require(original != PreviewCache.CreateKey(first, Settings), "Source channel count was omitted from cache identity");
        first.Channels = 2; first.Original = new float[64];
        Require(original != PreviewCache.CreateKey(first, Settings), "Source PCM length was omitted from cache identity");
    }

    private static void SourceReplacement()
    {
        var original = Track();
        var replacement = Track();
        replacement.Id = original.Id;
        replacement.Original = Enumerable.Repeat(.75f, original.Original.Length).ToArray();
        Require(original.SourceId != replacement.SourceId, "Distinct decoded sources must have distinct immutable source identities");
        string originalKey = PreviewCache.CreateKey(original, Settings);
        string replacementKey = PreviewCache.CreateKey(replacement, Settings);
        Require(originalKey.StartsWith($"{original.Id:N}:{original.SourceId:N}:", StringComparison.Ordinal) &&
            replacementKey.StartsWith($"{replacement.Id:N}:{replacement.SourceId:N}:", StringComparison.Ordinal),
            "Cache keys must explicitly identify both their album slot and immutable source");
        Require(originalKey != replacementKey,
            "Replacing same-path, same-length, same-format PCM in one slot reused its old preview identity");
        Require(PreviewCache.IsForSource(original, originalKey) && PreviewCache.IsForSource(replacement, replacementKey) &&
            !PreviewCache.IsForSource(original, replacementKey) && !PreviewCache.IsForSource(replacement, originalKey) &&
            !PreviewCache.IsForSource(Track(), originalKey) && !PreviewCache.IsForSource(original, null),
            "Source ownership must reject a restored slot's stale replacement key and other album slots");

        var originalRender = Render(value: .1f);
        var replacementRender = Render(value: .7f);
        var originalPeaks = Peaks();
        var replacementPeaks = Peaks();
        var cache = new PreviewCache();
        cache.Store(originalKey, originalRender, originalPeaks);
        Require(!cache.TryGet(replacementKey, out _), "Replacement retrieved the previous source's audio before being rendered");
        cache.Store(replacementKey, replacementRender, replacementPeaks);
        Require(cache.Count == 2 && cache.TryGet(replacementKey, out var replaced) &&
            ReferenceEquals(replaced.Render, replacementRender) && ReferenceEquals(replaced.Peaks, replacementPeaks),
            "Replacement did not retain its own measured audio and display peaks");
        Require(cache.TryGet(PreviewCache.CreateKey(original, Settings), out var restored) &&
            ReferenceEquals(restored.Render, originalRender) && ReferenceEquals(restored.Peaks, originalPeaks),
            "Undo to the prior source lost its exact cached audio or selected replacement PCM");

        cache.RemoveTrack(original.Id);
        Require(cache.Count == 0 && cache.CachedBytes == 0 &&
            !cache.TryGet(originalKey, out _) && !cache.TryGet(replacementKey, out _),
            "Removing an album slot must release all source variants retained for that slot");
    }

    private static void InputGain()
    {
        var track = Track();
        var bypass = Settings with { LevelingEnabled = false, MatchingEnabled = false };
        string zero = PreviewCache.CreateKey(track, bypass);
        track.InputGainDb = .000001;
        Require(zero != PreviewCache.CreateKey(track, bypass), "Input gain must affect the key even when both automatic stages are bypassed");
        track.InputGainDb = 30; string maximum = PreviewCache.CreateKey(track, bypass);
        track.InputGainDb = 31;
        Require(maximum == PreviewCache.CreateKey(track, bypass), "Input gain above the renderer's upper bound must reuse its clamped variant");
        track.InputGainDb = -30; string minimum = PreviewCache.CreateKey(track, bypass);
        track.InputGainDb = -31;
        Require(minimum == PreviewCache.CreateKey(track, bypass), "Input gain below the renderer's lower bound must reuse its clamped variant");
        Require(minimum != maximum, "Opposite input gains shared a preview");
        track.InputGainDb = -0.0;
        Require(zero == PreviewCache.CreateKey(track, bypass), "Negative-zero input gain must share the unity variant");
    }

    private static void TargetOverrides()
    {
        var track = Track();
        string inherited = PreviewCache.CreateKey(track, Settings);
        track.TargetLufs = Settings.TargetLufs;
        Require(inherited == PreviewCache.CreateKey(track, Settings), "An explicit target equal to the global target must share its effective variant");
        Require(inherited == PreviewCache.CreateKey(track, Settings with { TargetLufs = -23 }), "A global target change invalidated an overridden track's unchanged audio");
        track.TargetLufs = -18;
        Require(inherited != PreviewCache.CreateKey(track, Settings), "Changing the effective per-track target reused stale audio");
        track.TargetLufs = null;
        Require(PreviewCache.CreateKey(track, Settings) != PreviewCache.CreateKey(track, Settings with { TargetLufs = -23 }), "An inherited target failed to follow the global control");
        // Nullable numeric zero is still an explicit override, never a missing value.
        track.TargetLufs = 0;
        EqualKey(track, Settings, Settings with { TargetLufs = -23 }, "A zero-valued explicit target was treated as an inherited target");
    }

    private static void MatchingStrength()
    {
        var track = Track();
        var zero = Settings with { MatchingStrength = 0 };
        string bypass = PreviewCache.CreateKey(track, Settings with { MatchingEnabled = false });
        Require(bypass == PreviewCache.CreateKey(track, zero), "Zero matching strength must share the matching-bypass variant");
        track.TargetLufs = -26;
        Require(bypass == PreviewCache.CreateKey(track, zero with { TargetLufs = -18 }), "A target change invalidated zero-strength matching");
        Require(bypass == PreviewCache.CreateKey(track, Settings with { MatchingStrength = -.2 }), "A strength below zero must reuse clamped matching bypass");
        EqualKey(track, Settings, Settings with { MatchingStrength = 1.2 }, "A strength above one must reuse the fully applied matching variant");
        EqualKey(track, Settings with { MatchingEnabled = false, MatchingStrength = .2 },
            Settings with { MatchingEnabled = false, MatchingStrength = .9, TargetLufs = -23 }, "Disabled matching must ignore its strength control");
        string partial = PreviewCache.CreateKey(track, Settings with { MatchingStrength = .4 });
        Require(partial != bypass && partial != PreviewCache.CreateKey(track, Settings), "Partial matching strength reused a different audible variant");
        track.TargetLufs = -22;
        Require(partial != PreviewCache.CreateKey(track, Settings with { MatchingStrength = .4 }), "Active partial matching must remain target-sensitive");
    }

    private static void TransitionCurves()
    {
        var track = Track();
        string unchanged = PreviewCache.CreateKey(track, Settings);
        // Neighbor analysis can move zero-correction knots without changing this track.
        track.TransitionEnvelope = [new(0, 0), new(track.Duration * .2, 0), new(track.Duration, 0)];
        Require(unchanged == PreviewCache.CreateKey(track, Settings), "Zero transition corrections inserted redundant knots and invalidated unchanged audio");
        track.TransitionEnvelope = [new(0, 0), new(track.Duration * .8, 0), new(track.Duration, 0)];
        Require(unchanged == PreviewCache.CreateKey(track, Settings), "Moving zero correction knots invalidated an unaffected neighboring track");
        var disabled = Settings with { LevelingEnabled = false };
        string bypass = PreviewCache.CreateKey(track, disabled);
        string zero = PreviewCache.CreateKey(track, Settings with { Strength = 0 });
        track.TransitionEnvelope = [new(0, 2), new(track.Duration, 0)];
        Require(unchanged != PreviewCache.CreateKey(track, Settings), "An active transition correction reused stale leveled audio");
        Require(bypass == PreviewCache.CreateKey(track, disabled) && zero == PreviewCache.CreateKey(track, Settings with { Strength = 0 }), "Bypassed leveling must ignore automatic transition edits");
        track.TransitionEnvelope = track.Envelope.Select(point => new EnvelopePoint(point.Time, -point.GainDb)).ToList();
        Require(bypass == PreviewCache.CreateKey(track, Settings), "Exactly cancelling base and transition envelopes must share the unity-leveling variant");
    }

    private static void LeastRecentlyUsed()
    {
        var track = Track(); var render = Render(); var peaks = Peaks();
        string a = PreviewCache.CreateKey(track, Settings), b = PreviewCache.CreateKey(track, Settings with { TargetLufs = -18 }), c = PreviewCache.CreateKey(track, Settings with { TargetLufs = -23 });
        long size = PreviewCache.EstimateBytes(a, render, peaks);
        var cache = new PreviewCache(size * 2);
        cache.Store(a, render, peaks); cache.Store(b, Render(), Peaks());
        Require(cache.TryGet(a, out _), "First preview should still be available");
        cache.Store(c, Render(), Peaks());
        Require(cache.Count == 2 && cache.TryGet(a, out _) && cache.TryGet(c, out _) && !cache.TryGet(b, out _),
            "Cache must evict the least recently accessed variant after a hit refreshes recency");
    }

    private static void RedundantAuthoredPoints()
    {
        var track = Track();
        string before = PreviewCache.CreateKey(track, Settings);
        const double time = .0000712345;
        double lineGain = AudioProcessor.EnvelopeGainAt(track.Envelope, time);
        track.Envelope.Insert(1, new(time, lineGain));
        Require(before == PreviewCache.CreateKey(track, Settings), "A point inserted exactly on the existing authored line invalidated unchanged audio");
        track.Envelope.RemoveAt(1);
        Require(before == PreviewCache.CreateKey(track, Settings), "Removing a redundant authored point changed the preview identity");
        track.Envelope.Insert(1, new(time, lineGain + .000001));
        Require(before != PreviewCache.CreateKey(track, Settings), "Dragging a new envelope point to a non-collinear gain reused stale audio");
        // The pre-existing turning point remains a real breakpoint, including its time.
        track.Envelope.RemoveAt(1);
        track.Envelope[1] = track.Envelope[1] with { Time = .00021 };
        Require(before != PreviewCache.CreateKey(track, Settings), "Moving a genuine envelope breakpoint did not select a new preview");
    }

    private static void RedundantCombinedPoints()
    {
        var track = Track();
        // Non-binary-exact gains expose rounding at automatic knots when a
        // redundant authored point is not removed before the layers are summed.
        track.Envelope = [new(0, -2.1), new(.0002, 1.2), new(track.Duration, 0)];
        track.TransitionEnvelope = [new(0, 1), new(track.Duration * .4, 0), new(track.Duration, -.75)];
        string before = PreviewCache.CreateKey(track, Settings);
        // Test on both sides of an automatic edge-curve breakpoint, while the
        // authored envelope remains within the same straight segment.
        foreach (double time in new[] { .0000712345, .00016 })
        {
            double lineGain = AudioProcessor.EnvelopeGainAt(track.Envelope, time);
            track.Envelope.Add(new(time, lineGain)); track.Envelope.Sort((a, b) => a.Time.CompareTo(b.Time));
            Require(before == PreviewCache.CreateKey(track, Settings), "Inserting an authored point changed an identical combined authored/transition curve");
            int index = track.Envelope.FindIndex(point => point.Time == time);
            track.Envelope.RemoveAt(index);
            Require(before == PreviewCache.CreateKey(track, Settings), "Removing a redundant point changed the combined curve identity");
        }
        const double movedTime = .0000712345;
        track.Envelope.Add(new(movedTime, AudioProcessor.EnvelopeGainAt(track.Envelope, movedTime) + .01));
        Require(before != PreviewCache.CreateKey(track, Settings), "An audible authored edit under automatic transition correction reused stale audio");
    }

    private static void MemoryBudget()
    {
        var track = Track(); var first = Render(32); var second = Render(64); var peaks = Peaks();
        string a = PreviewCache.CreateKey(track, Settings), b = PreviewCache.CreateKey(track, Settings with { TargetLufs = -18 });
        long firstCost = PreviewCache.EstimateBytes(a, first, peaks), secondCost = PreviewCache.EstimateBytes(b, second, peaks);
        var cache = new PreviewCache(firstCost + secondCost - 1);
        cache.Store(a, first, peaks); cache.Store(b, second, peaks);
        Require(cache.Count == 1 && cache.TryGet(b, out _) && !cache.TryGet(a, out _), "Budget overflow must evict enough old PCM to fit the new preview");
        Require(cache.CachedBytes == secondCost && cache.CachedBytes <= cache.MaxBytes, "Retained-byte accounting exceeded its configured bound");
    }

    private static void ReplacementAccounting()
    {
        var track = Track(); var render = Render(); var peaks = Peaks();
        string key = PreviewCache.CreateKey(track, Settings);
        long cost = PreviewCache.EstimateBytes(key, render, peaks); var cache = new PreviewCache(cost * 2);
        for (int i = 0; i < 5; i++) cache.Store(key, render, peaks);
        Require(cache.Count == 1 && cache.CachedBytes == cost, "Storing one cache key repeatedly double-counted retained memory");
        var replacement = Render(32, .2f); cache.Store(key, replacement, Peaks());
        Require(cache.Count == 1 && cache.CachedBytes == cost && cache.TryGet(key, out var cached) && ReferenceEquals(cached.Render, replacement),
            "Same-sized replacement did not update the stored result/accounting");
        var smaller = Render(8); cache.Store(key, smaller, peaks);
        Require(cache.CachedBytes == PreviewCache.EstimateBytes(key, smaller, peaks), "Replacement failed to release the previous PCM cost");
    }

    private static void IntermediateMemoryBudget()
    {
        var track = Track();
        for (int i = 0; i < track.Original.Length; i++) track.Original[i] = (float)(.05 * Math.Sin(i * .17));
        var settings = Settings with { MatchingEnabled = false, LimiterEnabled = false };
        var render = AudioProcessor.Render(track.Original, track.SampleRate, track.Channels,
            track.Envelope, 0, settings);
        long retained = render.RetainedIntermediateBytes;
        Require(retained > 0, "A fresh preview must expose the intermediate state it retains for future envelope edits");
        var peaks = Peaks(); string key = PreviewCache.CreateKey(track, settings);
        var bare = new RenderResult { Samples = render.Samples, Warning = render.Warning, RenderedEnvelope = render.RenderedEnvelope,
            ReductionTrace = render.ReductionTrace };
        long finalOnlyCost = PreviewCache.EstimateBytes(key, bare, peaks);
        long completeCost = PreviewCache.EstimateBytes(key, render, peaks);
        Require(completeCost - finalOnlyCost == retained, "Incremental PCM/filter/detector state was omitted from retained-byte accounting");
        var undersized = new PreviewCache(completeCost - 1);
        undersized.Store(key, render, peaks);
        Require(undersized.Count == 0 && undersized.CachedBytes == 0, "A preview whose incremental buffers exceed the budget was incorrectly retained");
        var exact = new PreviewCache(completeCost);
        exact.Store(key, render, peaks);
        Require(exact.Count == 1 && exact.CachedBytes == completeCost && exact.TryGet(key, out var cached) && ReferenceEquals(cached.Render, render),
            "A preview fitting its full retained-state budget was not cached intact");
    }

    private static void WaveformMemoryBudget()
    {
        var track = Track(); var render = Render(8192); var peaks = Peaks();
        string key = PreviewCache.CreateKey(track, Settings);
        long pcmOnlyCost = PreviewCache.EstimateBytes(key, render, peaks);
        var waveform = WaveformPyramid.Build(render.Samples, 2);
        render.Waveform = waveform;
        long cost = PreviewCache.EstimateBytes(key, render, peaks);
        Require(waveform.RetainedBytes > 0 && cost - pcmOnlyCost == waveform.RetainedBytes,
            "Waveform levels must be counted exactly once without duplicating their source PCM");
        var undersized = new PreviewCache(cost - 1);
        undersized.Store(key, render, peaks);
        Require(undersized.Count == 0 && undersized.CachedBytes == 0,
            "Waveform pyramid memory was omitted when enforcing the preview budget");
        var exact = new PreviewCache(cost);
        exact.Store(key, render, peaks);
        Require(exact.CachedBytes == cost && exact.TryGet(key, out var cached) &&
            ReferenceEquals(cached.Render.Waveform, waveform),
            "An exactly fitting waveform preview must reuse its immutable pyramid by reference");
    }

    private static void OversizedEntries()
    {
        var track = Track(); var render = Render(); var peaks = Peaks();
        string a = PreviewCache.CreateKey(track, Settings), b = PreviewCache.CreateKey(track, Settings with { TargetLufs = -18 });
        var cache = new PreviewCache(PreviewCache.EstimateBytes(a, render, peaks) * 2);
        cache.Store(a, render, peaks); cache.Store(b, Render(), Peaks());
        cache.Store(b, Render(10000), Peaks());
        Require(cache.Count == 1 && cache.TryGet(a, out _) && !cache.TryGet(b, out _),
            "An oversized replacement should remove its obsolete variant while preserving unrelated cached audio");
        cache.Store(b, Render(10000), Peaks());
        Require(cache.Count == 1 && cache.TryGet(a, out _), "An oversized new entry evicted unrelated previews");
    }

    private static void RemoveAndClear()
    {
        var track = Track(); var other = Track(); var cache = new PreviewCache();
        cache.Store(PreviewCache.CreateKey(track, Settings), Render(), Peaks());
        cache.Store(PreviewCache.CreateKey(track, Settings with { TargetLufs = -18 }), Render(), Peaks());
        string otherKey = PreviewCache.CreateKey(other, Settings); cache.Store(otherKey, Render(), Peaks());
        cache.RemoveTrack(track.Id);
        Require(cache.Count == 1 && cache.TryGet(otherKey, out _), "Removing a track failed to release only that track's variants");
        cache.Clear(); Require(cache.Count == 0 && cache.CachedBytes == 0, "Clear failed to release all cache entries and accounting");
    }

    private static void CapacityPolicy()
    {
        var track = Track(); var cache = new PreviewCache(0);
        cache.Store(PreviewCache.CreateKey(track, Settings), Render(), Peaks());
        Require(cache.Count == 0 && cache.CachedBytes == 0, "A zero budget must disable PCM retention");
        try { _ = new PreviewCache(-1); throw new InvalidOperationException("A negative budget must be rejected"); }
        catch (ArgumentOutOfRangeException) { }
    }
}

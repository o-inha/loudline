using System.Runtime.CompilerServices;

namespace Multiloud.Core;

/// <summary>
/// Immutable 100 ms energy summaries. Both branches are K-weighted; the second
/// additionally passes through a 90 Hz high-pass filter used only for suggestions.
/// No audible audio or standards-based meter is filtered by this profile.
/// </summary>
public sealed class LevelingEnergyProfile
{
    public int SampleRate { get; }
    public int HopFrames { get; }
    public int FrameCount { get; }
    public IReadOnlyList<double> FullBandEnergy { get; }
    public IReadOnlyList<double> HighPassedEnergy { get; }
    public IReadOnlyList<int> FramesPerBin { get; }
    public long RetainedBytes => 384L + FullBandEnergy.Count * 20L;

    internal LevelingEnergyProfile(int sampleRate, int hopFrames, int frameCount,
        double[] fullBandEnergy, double[] highPassedEnergy, int[] framesPerBin)
    {
        SampleRate = sampleRate; HopFrames = hopFrames; FrameCount = frameCount;
        // Defend the public immutable profile even if its creator reuses arrays.
        FullBandEnergy = Array.AsReadOnly((double[])fullBandEnergy.Clone());
        HighPassedEnergy = Array.AsReadOnly((double[])highPassedEnergy.Clone());
        FramesPerBin = Array.AsReadOnly((int[])framesPerBin.Clone());
    }
}

/// <summary>One cached 100 ms detector interval; Time is its start in seconds.</summary>
public readonly record struct LevelingDetail(double Time, double Duration, double Loudness,
    double FullBandLoudness, double HighPassedLoudness, double BassConfidence);

/// <summary>Relative musical-leveling measurements, deliberately separate from LUFS.</summary>
public sealed class LevelingProfile
{
    public IReadOnlyList<LoudnessFrame> ShortTerm { get; }
    public double ReferenceLoudness { get; }
    public IReadOnlyList<double> BassConfidence { get; }
    public bool UsesReducedBass { get; }
    public IReadOnlyList<LevelingDetail> Detail { get; }

    internal LevelingProfile(IReadOnlyList<LoudnessFrame> shortTerm, double reference,
        IReadOnlyList<double> confidence, bool reduced, IReadOnlyList<LevelingDetail>? detail = null)
    {
        ShortTerm = shortTerm; ReferenceLoudness = reference;
        BassConfidence = confidence; UsesReducedBass = reduced;
        Detail = detail ?? Array.Empty<LevelingDetail>();
    }
}

/// <summary>
/// Derives lightweight suggestions from cached energies, never by scanning PCM.
/// One most-recent derived profile per analysis is cached under a weak owner.
/// </summary>
public static class LevelingDetector
{
    private sealed class CachedProfile
    {
        public readonly object Gate = new();
        public double Sensitivity;
        public LevelingProfile? Profile;
    }
    private static readonly ConditionalWeakTable<AnalysisResult, CachedProfile> Cache = new();

    public static LevelingProfile GetProfile(AnalysisResult analysis, ProcessingSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GetProfile(analysis, settings.BassSensitivity, ct);
    }

    public static LevelingProfile GetProfile(AnalysisResult analysis, double bassSensitivity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ValidateSensitivity(bassSensitivity);
        ct.ThrowIfCancellationRequested();
        var cached = Cache.GetValue(analysis, _ => new CachedProfile());
        lock (cached.Gate)
        {
            ct.ThrowIfCancellationRequested();
            if (cached.Profile != null && cached.Sensitivity == bassSensitivity) return cached.Profile;
            var energy = analysis.LevelingEnergy;
            var result = bassSensitivity == 1 || energy == null
                ? new LevelingProfile(analysis.ShortTerm, analysis.IntegratedLufs,
                    Array.AsReadOnly(Enumerable.Repeat(1d, analysis.ShortTerm.Count).ToArray()), false,
                    energy == null ? null : BuildDetails(energy, bassSensitivity, ct))
                : Derive(analysis, energy, bassSensitivity, ct);
            cached.Sensitivity = bassSensitivity; cached.Profile = result;
            return result;
        }
    }

    internal static void ValidateSensitivity(double sensitivity)
    {
        if (!double.IsFinite(sensitivity) || sensitivity is < .25 or > 1)
            throw new ArgumentOutOfRangeException(nameof(sensitivity), "Bass sensitivity must be between 0.25 and 1.");
    }

    private static LevelingProfile Derive(AnalysisResult analysis, LevelingEnergyProfile energy, double sensitivity, CancellationToken ct)
    {
        int bins = energy.FramesPerBin.Count;
        if (bins == 0 || energy.FullBandEnergy.Count != bins || energy.HighPassedEnergy.Count != bins)
            return new LevelingProfile(analysis.ShortTerm, analysis.IntegratedLufs,
                Array.AsReadOnly(Enumerable.Repeat(1d, analysis.ShortTerm.Count).ToArray()), false);
        var full = new double[bins + 1];
        var high = new double[bins + 1];
        var count = new long[bins + 1];
        for (int i = 0; i < bins; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            full[i + 1] = full[i] + energy.FullBandEnergy[i] * energy.FramesPerBin[i];
            high[i + 1] = high[i] + energy.HighPassedEnergy[i] * energy.FramesPerBin[i];
            count[i + 1] = count[i] + energy.FramesPerBin[i];
        }
        double Full(int start, int end) => (full[end] - full[start]) / Math.Max(1L, count[end] - count[start]);
        double High(int start, int end) => (high[end] - high[start]) / Math.Max(1L, count[end] - count[start]);
        double Mixed(int start, int end) => sensitivity * Full(start, end) + (1 - sensitivity) * High(start, end);

        // Keep the standard full-band activity mask, but compute the reference
        // from the NEW detector energy. Mixing a filtered level with the old LUFS
        // reference would create a constant upward-gain bias.
        var blocks = new List<(double Full, double Mixed)>();
        if (bins < 4) blocks.Add((Full(0, bins), Mixed(0, bins)));
        else
        {
            for (int i = 0; i + 4 <= bins; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (count[i + 4] - count[i] >= energy.HopFrames * 4L)
                    blocks.Add((Full(i, i + 4), Mixed(i, i + 4)));
            }
            if (blocks.Count == 0) blocks.Add((Full(0, bins), Mixed(0, bins)));
        }
        var absolute = blocks.Where(block => LoudnessAnalyzer.EnergyToLufs(block.Full) >= -70).ToArray();
        double reference = -70;
        if (absolute.Length > 0)
        {
            double gate = LoudnessAnalyzer.EnergyToLufs(absolute.Average(block => block.Full)) - 10;
            var active = absolute.Where(block => LoudnessAnalyzer.EnergyToLufs(block.Full) >= gate).ToArray();
            if (active.Length > 0) reference = LoudnessAnalyzer.EnergyToLufs(active.Average(block => block.Mixed));
        }
        double duration = energy.FrameCount / (double)energy.SampleRate;
        var profile = new List<LoudnessFrame>();
        var confidence = new List<double>();
        double reductionAmount = (1 - sensitivity) / .75;
        for (int center = 5; center < bins + 5; center += 10)
        {
            if ((center & 1023) == 5) ct.ThrowIfCancellationRequested();
            int start = Math.Max(0, center - 15), end = Math.Min(bins, center + 15);
            if (start >= end) continue;
            double time = Math.Min(duration, center * energy.HopFrames / (double)energy.SampleRate);
            profile.Add(new LoudnessFrame(time, Math.Max(-90, LoudnessAnalyzer.EnergyToLufs(Mixed(start, end)))));
            // Below roughly 60 Hz little energy reaches the high-pass branch.
            // Preserve activity, but avoid lifting a bass-only quiet passage as
            // aggressively as a section supported by mid/high-frequency content.
            double ratio = High(start, end) / Math.Max(1e-20, Full(start, end));
            double spectralConfidence = Math.Clamp((ratio - .2) / .3, 0, 1);
            confidence.Add(1 - reductionAmount * (1 - spectralConfidence));
        }
        return new LevelingProfile(profile.AsReadOnly(), reference, confidence.AsReadOnly(), true, BuildDetails(energy, sensitivity, ct));
    }

    private static IReadOnlyList<LevelingDetail> BuildDetails(LevelingEnergyProfile energy, double sensitivity, CancellationToken ct)
    {
        int bins = energy.FramesPerBin.Count;
        if (energy.FullBandEnergy.Count != bins || energy.HighPassedEnergy.Count != bins) return Array.Empty<LevelingDetail>();
        var result = new LevelingDetail[bins];
        for (int i = 0; i < bins; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            double full = energy.FullBandEnergy[i], high = energy.HighPassedEnergy[i];
            double mixed = sensitivity * full + (1 - sensitivity) * high;
            double confidence = Math.Clamp((high / Math.Max(1e-20, full) - .2) / .3, 0, 1);
            result[i] = new LevelingDetail(i * energy.HopFrames / (double)energy.SampleRate,
                energy.FramesPerBin[i] / (double)energy.SampleRate,
                Math.Max(-90, LoudnessAnalyzer.EnergyToLufs(mixed)),
                Math.Max(-90, LoudnessAnalyzer.EnergyToLufs(full)),
                Math.Max(-90, LoudnessAnalyzer.EnergyToLufs(high)), confidence);
        }
        return Array.AsReadOnly(result);
    }
}

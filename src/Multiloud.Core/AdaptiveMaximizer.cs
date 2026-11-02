namespace Multiloud.Core;

/// <summary>
/// Offline stereo-linked peak control. A reconstructed-peak constraint is smoothed
/// backwards for anticipation, then recovered forwards using programme energy and
/// gain-reduction memory. Optional complementary bass control precedes the
/// broadband safety stage. No clipping, saturation, channel-independent gain or
/// upsampling of whole PCM buffers is used.
/// </summary>
internal static class AdaptiveMaximizer
{
    internal static void Apply(float[] samples, double[] detector, float[] gains, double constantGain,
        int rate, int channels, double ceiling, MaximizerStyle style, double character,
        double transientPreservation, CancellationToken ct, double bassProtection = 0,
        double[]? frameCeilings = null, int ceilingHopFrames = 1)
    {
        if (!double.IsFinite(bassProtection) || bassProtection is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(bassProtection));
        if (ceilingHopFrames <= 0 || frameCeilings != null &&
            frameCeilings.Length < (gains.Length + ceilingHopFrames - 1L) / ceilingHopFrames)
            throw new ArgumentException("Local peak ceilings do not cover the audio.", nameof(frameCeilings));
        double guard = Math.Pow(10, -.025 / 20), guarded = ceiling * guard;
        var bass = SpectralPeakProtection.Apply(samples, detector, constantGain, rate, channels, ceiling,
            bassProtection, character, frameCeilings, ceilingHopFrames, ct);
        for (int f = 0; f < gains.Length; f++)
        {
            if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
            double budget = frameCeilings == null ? guarded :
                Math.Min(guarded, frameCeilings[f / ceilingHopFrames] * guard);
            double peak = bass == null ? detector[f] * constantGain : TruePeakMeter.FramePeak(samples, channels, f);
            gains[f] = (float)Math.Min(1, Math.Max(0, budget) / Math.Max(1e-20, peak));
        }

        bool transparent = style == MaximizerStyle.Transparent;
        // More transient preservation shortens anticipatory attenuation and lets
        // isolated hits recover sooner; the instantaneous peak constraint stays.
        double anticipation = (transparent ? .004 + .004 * character : .0015 + .002 * character)
            * (1 - .55 * transientPreservation);
        double attack = Math.Exp(-1 / (anticipation * rate)), future = 1;
        for (int f = gains.Length - 1; f >= 0; f--)
        {
            if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
            future = Math.Min(gains[f], 1 - (1 - future) * attack);
            gains[f] = (float)future;
        }

        double baseSeconds = transparent ? .09 + .24 * character : .035 + .14 * character;
        double slowSeconds = transparent ? .25 + .65 * character : .12 + .35 * character;
        double fastSeconds = transparent ? .022 + .055 * character : .009 + .030 * character;
        double baseRelease = Math.Exp(-1 / (baseSeconds * rate));
        double slowRelease = Math.Exp(-1 / (slowSeconds * rate));
        double fastRelease = Math.Exp(-1 / (fastSeconds * rate));
        double fastEnergyStep = 1 - Math.Exp(-1 / (.004 * rate));
        double slowEnergyStep = 1 - Math.Exp(-1 / (.080 * rate));
        double memoryAttack = 1 - Math.Exp(-1 / (.012 * rate));
        double memoryRelease = 1 - Math.Exp(-1 / (.400 * rate));
        double fastEnergy = 0, slowEnergy = 0, memory = 0, previous = 1;
        for (int f = 0; f < gains.Length; f++)
        {
            if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
            double peak = detector[f] * constantGain;
            double energy = peak * peak;
            fastEnergy += (energy - fastEnergy) * fastEnergyStep;
            slowEnergy += (energy - slowEnergy) * slowEnergyStep;
            double depth = 1 - gains[f];
            memory += (depth - memory) * (depth > memory ? memoryAttack : memoryRelease);
            // A sustained bass note accumulates memory and recovers smoothly;
            // a high-crest, short event can use the faster recovery component.
            double transient = Math.Clamp((fastEnergy / Math.Max(1e-20, slowEnergy) - 1.5) / 5, 0, 1)
                * transientPreservation;
            double sustained = Math.Clamp(memory * 2, 0, 1) * (1 - transient);
            double release = baseRelease + (slowRelease - baseRelease) * sustained;
            release += (fastRelease - release) * transient;
            double gain = Math.Min(gains[f], 1 - (1 - previous) * release);
            previous = gain;
            // A frequency-selective stage has no single PCM amplitude ratio.
            // The meter reports the most attenuated band, combined with the
            // common broadband gain; at bass=0 this is the exact linked gain.
            gains[f] = (float)(gain * (bass?.GainAt(f) ?? 1));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = (float)(samples[f * channels + c] * gain);
        }
    }

    /// <summary>
    /// Corrects only reconstructed-peak problem regions, with a flat FIR-support
    /// margin and smooth short ramps. A final independent measurement still decides
    /// whether the last-resort whole-track safety attenuation is necessary.
    /// </summary>
    internal static bool CorrectReconstructedPeaks(float[] samples, float[] gains, PeakBlockCache peaks,
        int rate, int channels, double ceiling, CancellationToken ct, out int firstChanged, out int endChanged)
    {
        int frames = samples.Length / channels;
        firstChanged = frames; endChanged = 0;
        int attackFrames = Math.Max(16, (int)Math.Round(rate * .004));
        int releaseFrames = Math.Max(16, (int)Math.Round(rate * .035));
        for (int bin = 0; bin < peaks.BlockCount;)
        {
            ct.ThrowIfCancellationRequested();
            if (peaks.BlockPeak(bin) <= ceiling) { bin++; continue; }
            int firstBin = bin;
            double peak = 0;
            do { peak = Math.Max(peak, peaks.BlockPeak(bin)); bin++; }
            while (bin < peaks.BlockCount && peaks.BlockPeak(bin) > ceiling);
            double logGain = Math.Log(ceiling / peak) - .01 * Math.Log(10) / 20;
            int plateauFirst = Math.Max(0, peaks.BlockStart(firstBin) - 12);
            int plateauEnd = Math.Min(frames, peaks.BlockEnd(bin - 1) + 12);
            int first = Math.Max(0, plateauFirst - attackFrames);
            int end = Math.Min(frames, plateauEnd + releaseFrames);
            firstChanged = Math.Min(firstChanged, first); endChanged = Math.Max(endChanged, end);
            for (int f = first; f < end; f++)
            {
                if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
                double amount = 1;
                if (f < plateauFirst)
                {
                    double x = (f - first) / (double)Math.Max(1, plateauFirst - first);
                    amount = x * x * (3 - 2 * x);
                }
                else if (f >= plateauEnd)
                {
                    double x = (f - plateauEnd) / (double)Math.Max(1, end - plateauEnd);
                    amount = 1 - x * x * (3 - 2 * x);
                }
                double gain = Math.Exp(logGain * amount);
                gains[f] = (float)(gains[f] * gain);
                for (int c = 0; c < channels; c++) samples[f * channels + c] = (float)(samples[f * channels + c] * gain);
            }
        }
        return firstChanged < endChanged;
    }

    internal static ReductionMetrics MeasureReduction(float[]? gains, int frames, int rate, double safetyAttenuationDb,
        CancellationToken ct)
    {
        int hop = Math.Max(1, (int)Math.Round(rate * .1));
        var trace = new List<LimiterReductionPoint>((frames + hop - 1) / hop);
        double total = 0, maximum = 0, blockMaximum = 0;
        int limited = 0, heavy = 0;
        for (int f = 0; f < frames; f++)
        {
            if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
            double reduction = safetyAttenuationDb;
            if (gains != null && gains[f] < 1) reduction -= 20 * Math.Log10(Math.Max(1e-30, gains[f]));
            maximum = Math.Max(maximum, reduction); blockMaximum = Math.Max(blockMaximum, reduction); total += reduction;
            if (reduction > .1) limited++;
            if (reduction > 3) heavy++;
            if ((f + 1) % hop == 0 || f + 1 == frames)
            {
                trace.Add(new LimiterReductionPoint((f / hop * hop) / (double)rate, blockMaximum));
                blockMaximum = 0;
            }
        }
        return new(maximum, frames == 0 ? 0 : total / frames, frames == 0 ? 0 : 100.0 * limited / frames,
            frames == 0 ? 0 : 100.0 * heavy / frames, trace.AsReadOnly());
    }

    internal sealed record ReductionMetrics(double Maximum, double Average, double LimitedPercent,
        double HeavyPercent, IReadOnlyList<LimiterReductionPoint> Trace);
}

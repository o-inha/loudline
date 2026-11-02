namespace Multiloud.Core;

/// <summary>
/// Compact, time-local reconstructed-peak budgets for immutable background audio.
/// Music alone is attenuated. One millisecond maxima plus reconstruction-support
/// margins keep an isolated background peak from limiting an entire song.
/// </summary>
internal sealed class BackgroundPeakHeadroom
{
    internal double[] Ceilings { get; }
    internal int HopFrames { get; }
    internal double BackgroundPeak { get; }
    private readonly int frames;

    private BackgroundPeakHeadroom(double[] ceilings, int hopFrames, int frames, double peak)
    {
        Ceilings = ceilings;
        HopFrames = hopFrames;
        this.frames = frames;
        BackgroundPeak = peak;
    }

    internal static BackgroundPeakHeadroom Create(float[] background, int rate, int channels,
        double ceiling, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int frames = background.Length / channels;
        int hop = Math.Max(1, (int)Math.Round(rate * .001));
        var peaks = new double[(frames + hop - 1) / hop];
        double maximum = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 8191) == 0) ct.ThrowIfCancellationRequested();
            double peak = TruePeakMeter.FramePeak(background, channels, frame);
            int bin = frame / hop;
            peaks[bin] = Math.Max(peaks[bin], peak);
            maximum = Math.Max(maximum, peak);
        }
        // The interpolation filter reaches twelve native frames to either side.
        // An extra bin also covers interpolation between adjacent control bins.
        int margin = (12 + hop - 1) / hop + 1;
        var ceilings = new double[peaks.Length];
        double guard = Math.Pow(10, -.02 / 20);
        // The final mix is rounded to float before reconstruction. A relative
        // margin on the remaining music headroom becomes vanishingly small
        // when the fixed bed approaches the ceiling. Reserve an absolute few
        // output ULPs as well, covering addition and reconstruction roundoff.
        float roundedCeiling = (float)ceiling;
        double additionReserve = 8 * ((double)MathF.BitIncrement(roundedCeiling) - roundedCeiling);
        for (int bin = 0; bin < peaks.Length; bin++)
        {
            if ((bin & 8191) == 0) ct.ThrowIfCancellationRequested();
            double peak = 0;
            for (int neighbor = Math.Max(0, bin - margin); neighbor <= Math.Min(peaks.Length - 1, bin + margin); neighbor++)
                peak = Math.Max(peak, peaks[neighbor]);
            ceilings[bin] = Math.Max(0, ceiling - peak - additionReserve) * guard;
        }
        return new BackgroundPeakHeadroom(ceilings, hop, frames, maximum);
    }

    /// <summary>
    /// Gain modulation may create new inter-sample peaks. Repair only the music
    /// around affected bins, remeasure, and use a uniform music-only trim solely
    /// if bounded local repairs cannot satisfy the independently measured budget.
    /// </summary>
    internal (int PeakFrames, double SafetyAttenuationDb) ProtectMusic(float[] music, float[] gains,
        int rate, int channels, CancellationToken ct)
    {
        int work = 0;
        var correction = new double[Ceilings.Length];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            bool changed = false;
            Array.Fill(correction, 1);
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 8191) == 0) ct.ThrowIfCancellationRequested();
                double peak = TruePeakMeter.FramePeak(music, channels, frame);
                int bin = frame / HopFrames;
                if (peak <= Ceilings[bin]) continue;
                correction[bin] = Math.Min(correction[bin], Ceilings[bin] / peak * .9988493699365052); // -0.01 dB
                changed = true;
            }
            work = AddFrames(work, frames);
            if (!changed) return (work, 0);
            ApplyLocalCorrection(music, gains, correction, rate, channels, ct);
        }

        // A single scalar cannot create additional gain-modulation peaks. This
        // last-resort trim leaves the fixed bed unchanged and has a direct
        // triangle-inequality proof at each reconstruction phase.
        double safety = 1;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 8191) == 0) ct.ThrowIfCancellationRequested();
            double peak = TruePeakMeter.FramePeak(music, channels, frame);
            if (peak > Ceilings[frame / HopFrames])
                safety = Math.Min(safety, Ceilings[frame / HopFrames] / peak * .9994245193792801); // -0.005 dB
        }
        work = AddFrames(work, frames);
        if (safety < 1)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 16383) == 0) ct.ThrowIfCancellationRequested();
                gains[frame] = (float)(gains[frame] * safety);
                for (int channel = 0; channel < channels; channel++)
                    music[frame * channels + channel] = (float)(music[frame * channels + channel] * safety);
            }
        }
        return (work, safety < 1 ? safety > 0 ? -20 * Math.Log10(safety) : 120 : 0);
    }

    /// <summary>
    /// Repairs a rare post-addition reconstruction overshoot in place. Only the
    /// music contribution around the offending filter support is ducked; the
    /// immutable background remains the exact anchor even at zero music gain.
    /// </summary>
    internal int RepairMixedRoundoff(float[] mix, float[] background, float[] gains,
        int rate, int channels, double ceiling, CancellationToken ct)
    {
        int work = 0;
        var correction = new double[Ceilings.Length];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            bool changed = false;
            Array.Fill(correction, 1);
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 8191) == 0) ct.ThrowIfCancellationRequested();
                if (TruePeakMeter.FramePeak(mix, channels, frame) <= ceiling) continue;
                // Full local ducking is deterministic even when fixed music
                // and bed phases nearly cancel, where a simple peak ratio is
                // not a monotonic constraint on the music gain.
                correction[frame / HopFrames] = 0;
                changed = true;
            }
            work = AddFrames(work, frames);
            if (!changed) return work;
            ApplyLocalCorrection(mix, gains, correction, rate, channels, ct, background);
        }
        // Publication independently checks the complete final mix once more.
        return work;
    }

    private void ApplyLocalCorrection(float[] music, float[] gains, double[] correction,
        int rate, int channels, CancellationToken ct, float[]? background = null)
    {
        int margin = (12 + HopFrames - 1) / HopFrames + 1;
        var padded = new double[correction.Length];
        for (int bin = 0; bin < correction.Length; bin++)
        {
            double gain = 1;
            for (int neighbor = Math.Max(0, bin - margin); neighbor <= Math.Min(correction.Length - 1, bin + margin); neighbor++)
                gain = Math.Min(gain, correction[neighbor]);
            padded[bin] = gain;
        }
        double attack = Math.Exp(-HopFrames / (.004 * rate));
        double release = Math.Exp(-HopFrames / (.035 * rate));
        double next = 1;
        for (int bin = padded.Length - 1; bin >= 0; bin--)
        {
            if ((bin & 8191) == 0) ct.ThrowIfCancellationRequested();
            next = Math.Min(padded[bin], 1 - (1 - next) * attack);
            padded[bin] = next;
        }
        double previous = 1;
        for (int bin = 0; bin < padded.Length; bin++)
        {
            previous = Math.Min(padded[bin], 1 - (1 - previous) * release);
            padded[bin] = previous;
        }
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 16383) == 0) ct.ThrowIfCancellationRequested();
            int bin = frame / HopFrames;
            double left = Math.Min(padded[bin], padded[Math.Max(0, bin - 1)]);
            double right = Math.Min(padded[bin], padded[Math.Min(padded.Length - 1, bin + 1)]);
            double amount = frame % HopFrames / (double)HopFrames;
            double gain = left + (right - left) * amount;
            gains[frame] = (float)(gains[frame] * gain);
            for (int channel = 0; channel < channels; channel++)
            {
                int index = frame * channels + channel;
                music[index] = background == null
                    ? (float)(music[index] * gain)
                    : gain == 0 ? background[index]
                    : gain == 1 ? music[index]
                    : (float)(background[index] + ((double)music[index] - background[index]) * gain);
            }
        }
    }

    private static int AddFrames(int first, int second) => (int)Math.Min(int.MaxValue, (long)first + second);
}

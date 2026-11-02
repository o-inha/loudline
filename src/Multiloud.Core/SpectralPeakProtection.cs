namespace Multiloud.Core;

/// <summary>
/// A complementary, zero-phase bass/residual split used only when peaks need
/// controlling. Reducing the bass first can leave the rest of the music less
/// affected by the final broadband limiter. The original signal is the residual
/// reference, so unity band gains reconstruct it exactly, without a crossover
/// phase shift or an always-on EQ. All filter work uses bounded scratch buffers.
/// </summary>
internal static class SpectralPeakProtection
{
    internal sealed class GainEnvelope(float[] values, int hop)
    {
        public float GainAt(int frame)
        {
            int index = Math.Min(values.Length - 1, frame / hop);
            if (index + 1 == values.Length) return values[index];
            double fraction = frame % hop / (double)hop;
            return (float)(values[index] + (values[index + 1] - values[index]) * fraction);
        }
    }

    internal static GainEnvelope? Apply(float[] samples, double[] detector, double constantGain,
        int rate, int channels, double ceiling, double amount, double character,
        double[]? frameCeilings, int ceilingHopFrames, CancellationToken ct)
    {
        if (amount <= 0 || samples.Length == 0) return null;
        int frames = samples.Length / channels;
        double guard = Math.Pow(10, -.04 / 20);
        double triggerGuard = Math.Pow(10, -.025 / 20);
        double Budget(int frame) => (frameCeilings == null ? ceiling : Math.Min(ceiling, frameCeilings[frame / ceilingHopFrames])) * guard;
        bool needed = false;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 16383) == 0) ct.ThrowIfCancellationRequested();
            if (detector[frame] * constantGain > Budget(frame) / guard * triggerGuard) { needed = true; break; }
        }
        if (!needed) return null;

        // One millisecond control bins are small compared with the intentionally
        // slow bass recovery and avoid another per-sample array for long albums.
        int hop = Math.Max(1, (int)Math.Round(rate * .001));
        var targets = new float[(frames + hop - 1) / hop];
        Array.Fill(targets, 1);
        bool changed = false;
        VisitBassBlocks(samples, rate, channels, ct, (first, count, low, offset) =>
        {
            for (int local = 0; local < count; local++)
            {
                int frame = first + local;
                double budget = Budget(frame), lower = 0, upper = 1, fullPeak = 0, bassPeak = 0;
                bool feasible = true;
                for (int channel = 0; channel < channels; channel++)
                {
                    double full = samples[frame * channels + channel];
                    double bass = low[(offset + local) * channels + channel];
                    double residual = full - bass;
                    fullPeak = Math.Max(fullPeak, Math.Abs(full));
                    bassPeak = Math.Max(bassPeak, Math.Abs(bass));
                    if (Math.Abs(bass) < 1e-12)
                    {
                        if (Math.Abs(residual) > budget) feasible = false;
                        continue;
                    }
                    double a = (-budget - residual) / bass, b = (budget - residual) / bass;
                    lower = Math.Max(lower, Math.Min(a, b));
                    upper = Math.Min(upper, Math.Max(a, b));
                }
                // Do not reshape a treble transient, or chase a stereo phase
                // cancellation that cannot be solved by a common bass gain.
                if (!feasible || fullPeak <= budget || bassPeak < fullPeak * .35 ||
                    upper < Math.Max(0, lower) || upper >= 1) continue;
                float target = (float)(1 - amount * (1 - Math.Clamp(upper, 0, 1)));
                int bin = frame / hop;
                if (target < targets[bin]) { targets[bin] = target; changed = true; }
            }
        });
        if (!changed) return null;

        double attack = Math.Exp(-hop / (.010 * rate));
        double release = Math.Exp(-hop / ((.10 + .30 * character) * rate));
        double future = 1, previous = 1;
        for (int bin = targets.Length - 1; bin >= 0; bin--)
        {
            if ((bin & 16383) == 0) ct.ThrowIfCancellationRequested();
            future = Math.Min(targets[bin], 1 - (1 - future) * attack);
            targets[bin] = (float)future;
        }
        for (int bin = 0; bin < targets.Length; bin++)
        {
            if ((bin & 16383) == 0) ct.ThrowIfCancellationRequested();
            previous = Math.Min(targets[bin], 1 - (1 - previous) * release);
            targets[bin] = (float)previous;
        }
        var envelope = new GainEnvelope(targets, hop);
        VisitBassBlocks(samples, rate, channels, ct, (first, count, low, offset) =>
        {
            for (int local = 0; local < count; local++)
            {
                int frame = first + local;
                double difference = envelope.GainAt(frame) - 1;
                if (difference == 0) continue;
                for (int channel = 0; channel < channels; channel++)
                {
                    int index = frame * channels + channel;
                    samples[index] = (float)(samples[index] + low[(offset + local) * channels + channel] * difference);
                }
            }
        });
        return envelope;
    }

    private static void VisitBassBlocks(float[] samples, int rate, int channels, CancellationToken ct,
        Action<int, int, double[], int> visit)
    {
        int frames = samples.Length / channels;
        int padding = Math.Max(32, (int)Math.Ceiling(rate * .060));
        int chunk = Math.Max(32768, padding);
        var work = new double[(chunk + 2 * padding) * channels];
        var history = new float[padding * channels];
        double k = Math.Tan(Math.PI * 120 / rate), q = Math.Sqrt(.5), norm = 1 / (1 + k / q + k * k);
        double b0 = k * k * norm, b1 = 2 * b0, b2 = b0;
        double a1 = 2 * (k * k - 1) * norm, a2 = (1 - k / q + k * k) * norm;
        for (int first = 0; first < frames; first += chunk)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(chunk, frames - first), paddedFrames = count + 2 * padding;
            for (int local = 0; local < paddedFrames; local++)
            {
                int sourceFrame = first + local - padding;
                for (int channel = 0; channel < channels; channel++)
                {
                    double value;
                    if (sourceFrame < 0) value = samples[channel];
                    else if (sourceFrame >= frames) value = samples[(frames - 1) * channels + channel];
                    else if (sourceFrame < first) value = history[(sourceFrame - first + padding) * channels + channel];
                    else value = samples[sourceFrame * channels + channel];
                    work[local * channels + channel] = value;
                }
            }
            for (int channel = 0; channel < channels; channel++)
            {
                Filter(work, paddedFrames, channels, channel, false, b0, b1, b2, a1, a2);
                Filter(work, paddedFrames, channels, channel, true, b0, b1, b2, a1, a2);
            }
            // Preserve unmodified look-behind before the visitor changes this
            // block in place. Adjacent blocks therefore use the same source.
            if (first + count < frames)
                Array.Copy(samples, (first + count - padding) * channels, history, 0, history.Length);
            visit(first, count, work, padding);
        }
    }

    private static void Filter(double[] values, int frames, int channels, int channel, bool reverse,
        double b0, double b1, double b2, double a1, double a2)
    {
        int first = reverse ? frames - 1 : 0, step = reverse ? -1 : 1;
        double initial = values[first * channels + channel];
        double z1 = (1 - b0) * initial, z2 = (b2 - a2) * initial;
        for (int index = first; index >= 0 && index < frames; index += step)
        {
            int sample = index * channels + channel;
            double input = values[sample], output = b0 * input + z1;
            z1 = b1 * input - a1 * output + z2;
            z2 = b2 * input - a2 * output;
            values[sample] = output;
        }
    }
}

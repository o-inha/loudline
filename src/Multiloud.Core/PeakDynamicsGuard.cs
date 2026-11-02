namespace Multiloud.Core;

/// <summary>
/// Measures sustained energy removed from the music, independently of any
/// background and independently of the limiter's internal band gain meters.
/// A one-second window avoids treating a single short attack as sustained work.
/// </summary>
internal static class PeakDynamicsGuard
{
    internal static double MeasureSustainedReductionDb(float[] leveled, float[] limited,
        double constantGain, int rate, int channels, CancellationToken ct)
    {
        if (leveled.Length != limited.Length || channels is < 1 or > 2 || rate <= 0 ||
            !double.IsFinite(constantGain) || constantGain < 0)
            throw new ArgumentException("Invalid dynamics measurement inputs.");
        int frames = leveled.Length / channels;
        if (frames == 0 || constantGain == 0) return 0;
        int hop = Math.Max(1, rate / 100);
        int bins = Math.Max(1, (rate + hop - 1) / hop);
        var inputBins = new double[bins];
        var outputBins = new double[bins];
        var frameBins = new int[bins];
        var limitedBins = new int[bins];
        int cursor = 0, windowFrames = 0, limitedFrames = 0;
        int minimumLimitedFrames = Math.Min(frames, Math.Max(1, rate / 4));
        double inputEnergy = 0, outputEnergy = 0, maximum = 0;
        double gainSquared = constantGain * constantGain;
        for (int first = 0; first < frames; first += hop)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(frames, first + hop), count = end - first;
            double before = 0, after = 0;
            for (int frame = first; frame < end; frame++)
                for (int channel = 0; channel < channels; channel++)
                {
                    double input = leveled[frame * channels + channel];
                    double output = limited[frame * channels + channel];
                    before += input * input * gainSquared;
                    after += output * output;
                }
            inputEnergy += before - inputBins[cursor];
            outputEnergy += after - outputBins[cursor];
            windowFrames += count - frameBins[cursor];
            limitedFrames -= limitedBins[cursor];
            // Require at least 250 ms of meaningfully attenuated programme in
            // the window. A single loud hit over quiet audio must not lower the
            // whole track just because it dominates the window's energy.
            int occupied = before > count * channels * 1e-6 && before > after * 1.001 ? count : 0;
            limitedFrames += occupied;
            inputBins[cursor] = before; outputBins[cursor] = after;
            frameBins[cursor] = count; limitedBins[cursor] = occupied;
            cursor = (cursor + 1) % bins;
            if (limitedFrames < minimumLimitedFrames || inputEnergy <= windowFrames * channels * 1e-6) continue;
            double reduction = 10 * Math.Log10(inputEnergy / Math.Max(1e-30, outputEnergy));
            maximum = Math.Max(maximum, reduction);
        }
        return Math.Max(0, maximum);
    }
}

namespace Multiloud.Core;

/// <summary>BS.1770 K-weighted, absolute/relative gated loudness for mono and stereo music.</summary>
public static class LoudnessAnalyzer
{
    public static AnalysisResult Analyze(float[] samples, int sampleRate, int channels,
        CancellationToken ct = default)
    {
        var measured = Measure(samples, sampleRate, channels, ct, captureLeveling: true);
        var result = new AnalysisResult
        {
            IntegratedLufs = measured.IntegratedLufs,
            LoudnessRange = measured.LoudnessRange,
            ShortTerm = measured.ShortTerm,
            LevelingEnergy = measured.LevelingEnergy,
            TruePeakDb = ToDb(TruePeakMeter.Measure(samples, channels, ct))
        };
        return new AnalysisResult
        {
            IntegratedLufs = result.IntegratedLufs,
            LoudnessRange = result.LoudnessRange,
            ShortTerm = result.ShortTerm,
            LevelingEnergy = result.LevelingEnergy,
            TruePeakDb = result.TruePeakDb,
            SuggestedEnvelope = EnvelopeDesigner.Design(result,
                samples.Length / (double)(sampleRate * channels), new ProcessingSettings())
        };
    }

    internal static AnalysisResult Measure(float[] samples, int sampleRate, int channels, CancellationToken ct,
        bool captureLeveling = false)
    {
        ct.ThrowIfCancellationRequested();
        Validate(samples, sampleRate, channels);
        int frameCount = samples.Length / channels;
        if (frameCount == 0) return new AnalysisResult();
        int hop = Math.Max(1, (int)Math.Round(sampleRate * .1));
        int binCount = (frameCount + hop - 1) / hop;
        var energies = new double[binCount];
        var highEnergies = captureLeveling ? new double[binCount] : null;
        var counts = new int[binCount];
        var shelf = Enumerable.Range(0, channels).Select(_ => Biquad.Shelf(sampleRate)).ToArray();
        var highPass = Enumerable.Range(0, channels).Select(_ => Biquad.HighPass(sampleRate)).ToArray();
        var detectorHighPass = captureLeveling
            ? Enumerable.Range(0, channels).Select(_ => Biquad.DetectorHighPass(sampleRate)).ToArray() : [];
        for (int f = 0; f < frameCount; f++)
        {
            if ((f & 16383) == 0) ct.ThrowIfCancellationRequested();
            double energy = 0, detectorEnergy = 0;
            for (int c = 0; c < channels; c++)
            {
                float value = samples[f * channels + c];
                if (!float.IsFinite(value)) throw new ArgumentException("Audio contains non-finite samples.", nameof(samples));
                double weighted = highPass[c].Process(shelf[c].Process(value));
                energy += weighted * weighted;
                if (captureLeveling)
                {
                    double detector = detectorHighPass[c].Process(weighted);
                    detectorEnergy += detector * detector;
                }
            }
            int bin = f / hop;
            energies[bin] += energy;
            if (highEnergies != null) highEnergies[bin] += detectorEnergy;
            counts[bin]++;
        }
        var sum = new double[binCount + 1];
        var countSum = new long[binCount + 1];
        for (int i = 0; i < binCount; i++)
        {
            sum[i + 1] = sum[i] + energies[i];
            countSum[i + 1] = countSum[i] + counts[i];
        }
        double WindowEnergy(int start, int end) => (sum[end] - sum[start]) / Math.Max(1L, countSum[end] - countSum[start]);
        var blocks = new List<double>();
        if (binCount < 4) blocks.Add(WindowEnergy(0, binCount));
        else
        {
            for (int i = 0; i + 4 <= binCount; i++)
            {
                // Only complete 400 ms blocks contribute to the integrated gate.
                if (countSum[i + 4] - countSum[i] >= hop * 4L) blocks.Add(WindowEnergy(i, i + 4));
            }
            if (blocks.Count == 0) blocks.Add(WindowEnergy(0, binCount));
        }
        var absolute = blocks.Where(e => EnergyToLufs(e) >= -70).ToArray();
        double integrated = -70;
        if (absolute.Length > 0)
        {
            double relativeGate = EnergyToLufs(absolute.Average()) - 10;
            var gated = absolute.Where(e => EnergyToLufs(e) >= relativeGate).ToArray();
            if (gated.Length > 0) integrated = EnergyToLufs(gated.Average());
        }
        double duration = frameCount / (double)sampleRate;
        var profile = new List<LoudnessFrame>();
        // Three-second windows, evaluated once per second. Truncated edge windows
        // retain useful information for short files without inventing extra samples.
        for (int center = 5; center < binCount + 5; center += 10)
        {
            int start = Math.Max(0, center - 15);
            int end = Math.Min(binCount, center + 15);
            if (start >= end) continue;
            double t = Math.Min(duration, center * hop / (double)sampleRate);
            profile.Add(new LoudnessFrame(t, Math.Max(-90, EnergyToLufs(WindowEnergy(start, end)))));
        }
        double lra = 0;
        // EBU-style short-term relative gate and 10th–95th percentiles. The
        // display is an LRA estimate, particularly for short music excerpts.
        var lraAbs = profile.Where(p => p.Loudness > -70).Select(p => p.Loudness).ToArray();
        if (lraAbs.Length > 1)
        {
            double gate = EnergyToLufs(lraAbs.Select(LufsToEnergy).Average()) - 20;
            var active = lraAbs.Where(x => x >= gate).Order().ToArray();
            if (active.Length > 1) lra = Percentile(active, .95) - Percentile(active, .10);
        }
        LevelingEnergyProfile? levelingEnergy = null;
        if (highEnergies != null)
        {
            var fullMeans = new double[binCount];
            var highMeans = new double[binCount];
            for (int i = 0; i < binCount; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                fullMeans[i] = energies[i] / Math.Max(1, counts[i]);
                highMeans[i] = highEnergies[i] / Math.Max(1, counts[i]);
            }
            levelingEnergy = new LevelingEnergyProfile(sampleRate, hop, frameCount, fullMeans, highMeans, counts);
        }
        return new AnalysisResult { IntegratedLufs = integrated, LoudnessRange = lra, ShortTerm = profile, LevelingEnergy = levelingEnergy };
    }

    internal static void Validate(float[] samples, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels), "Only mono and stereo files are supported.");
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Length % channels != 0) throw new ArgumentException("Incomplete audio frame.", nameof(samples));
    }

    internal static double ToDb(double amplitude) => amplitude <= 1e-6 ? -120 : 20 * Math.Log10(amplitude);
    internal static double EnergyToLufs(double energy) => energy <= 1e-15 ? -150 : -.691 + 10 * Math.Log10(energy);
    private static double LufsToEnergy(double lufs) => Math.Pow(10, (lufs + .691) / 10);
    internal static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double p = (sorted.Length - 1) * percentile;
        int lo = (int)p;
        int hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (p - lo);
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double z1, z2;
        public double Process(double value)
        {
            double output = b0 * value + z1;
            z1 = b1 * value - a1 * output + z2;
            z2 = b2 * value - a2 * output;
            return output;
        }
        public static Biquad Shelf(int rate)
        {
            double k = Math.Tan(Math.PI * 1681.974450955533 / rate);
            const double q = .7071752369554196;
            double vh = Math.Pow(10, 3.999843853973347 / 20);
            double vb = Math.Pow(vh, .4996667741545416);
            double a0 = 1 + k / q + k * k;
            return new((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
        public static Biquad HighPass(int rate)
        {
            double k = Math.Tan(Math.PI * 38.13547087602444 / rate);
            const double q = .5003270373238773;
            double a0 = 1 + k / q + k * k;
            return new(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
        public static Biquad DetectorHighPass(int rate)
        {
            double k = Math.Tan(Math.PI * 90 / rate);
            double q = Math.Sqrt(.5);
            double a0 = 1 + k / q + k * k;
            return new(1 / a0, -2 / a0, 1 / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
    }
}

/// <summary>Four-times, 24-tap windowed-sinc reconstruction peak estimate.</summary>
internal static class TruePeakMeter
{
    private const int Radius = 12;
    private static readonly double[][] Kernels = MakeKernels();

    public static double Measure(float[] samples, int channels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        double peak = 0;
        int frames = samples.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            if ((f & 8191) == 0) ct.ThrowIfCancellationRequested();
            peak = Math.Max(peak, FramePeak(samples, channels, f));
        }
        return peak;
    }

    public static double FramePeak(float[] samples, int channels, int frame)
    {
        int frames = samples.Length / channels;
        double maximum = 0;
        for (int c = 0; c < channels; c++)
        {
            maximum = Math.Max(maximum, Math.Abs(samples[frame * channels + c]));
            foreach (var coefficients in Kernels)
            {
                double value = 0;
                int from = frame - Radius + 1;
                if (from >= 0 && from + 2 * Radius <= frames)
                {
                    int index = from * channels + c;
                    for (int j = 0; j < 2 * Radius; j++, index += channels)
                        value += samples[index] * coefficients[j];
                }
                else
                {
                    for (int j = 0; j < 2 * Radius; j++)
                    {
                        int index = from + j;
                        if (index >= 0 && index < frames) value += samples[index * channels + c] * coefficients[j];
                    }
                }
                maximum = Math.Max(maximum, Math.Abs(value));
            }
        }
        return maximum;
    }

    private static double[][] MakeKernels()
    {
        var result = new double[3][];
        for (int phase = 1; phase <= 3; phase++)
        {
            var coefficients = new double[2 * Radius];
            for (int j = 0; j < coefficients.Length; j++)
            {
                double d = j - Radius + 1 - phase / 4.0;
                double sinc = Math.Abs(d) < 1e-12 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                double window = Math.Abs(d) >= Radius ? 0 : .42 + .5 * Math.Cos(Math.PI * d / Radius) + .08 * Math.Cos(2 * Math.PI * d / Radius);
                coefficients[j] = sinc * window;
            }
            double total = coefficients.Sum();
            for (int j = 0; j < coefficients.Length; j++) coefficients[j] /= total;
            result[phase - 1] = coefficients;
        }
        return result;
    }
}

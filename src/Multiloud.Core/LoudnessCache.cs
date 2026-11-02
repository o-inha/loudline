namespace Multiloud.Core;

/// <summary>
/// Immutable BS.1770 energy bins and exact K-filter checkpoints. A local PCM
/// change resumes at its preceding checkpoint and propagates until the filter
/// state is bit-for-bit equal to the previous result, or until the file ends.
/// </summary>
internal sealed class LoudnessCache
{
    private readonly double[] energies;
    private readonly int[] counts;
    private readonly double[] states;
    private readonly int hop;

    public int SampleRate { get; }
    public int Channels { get; }
    public int FrameCount { get; }
    public int RecomputedFrames { get; }
    public double IntegratedLufs { get; }
    public long RetainedBytes => 128L + energies.LongLength * sizeof(double)
        + counts.LongLength * sizeof(int) + states.LongLength * sizeof(double);

    private LoudnessCache(int sampleRate, int channels, int frames, int hop,
        double[] energies, int[] counts, double[] states, int recomputedFrames)
    {
        SampleRate = sampleRate;
        Channels = channels;
        FrameCount = frames;
        this.hop = hop;
        this.energies = energies;
        this.counts = counts;
        this.states = states;
        RecomputedFrames = recomputedFrames;
        IntegratedLufs = Integrate(energies, counts, hop);
    }

    public static LoudnessCache Create(float[] samples, int rate, int channels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LoudnessAnalyzer.Validate(samples, rate, channels);
        int frames = samples.Length / channels;
        int hop = Math.Max(1, (int)Math.Round(rate * .1));
        int bins = (int)((frames + (long)hop - 1) / hop);
        var energies = new double[bins];
        var counts = new int[bins];
        var states = new double[(bins + 1) * channels * 4];
        var shelf = Enumerable.Range(0, channels).Select(_ => Biquad.Shelf(rate)).ToArray();
        var highPass = Enumerable.Range(0, channels).Select(_ => Biquad.HighPass(rate)).ToArray();
        for (int bin = 0; bin < bins; bin++)
        {
            ct.ThrowIfCancellationRequested();
            int first = bin * hop;
            int last = Math.Min(frames, first + hop);
            energies[bin] = ProcessBin(samples, channels, first, last, shelf, highPass, ct);
            counts[bin] = last - first;
            Capture(states, bin + 1, channels, shelf, highPass);
        }
        return new(rate, channels, frames, hop, energies, counts, states, frames);
    }

    public static LoudnessCache Update(LoudnessCache previous, float[] samples,
        int firstChangedFrame, int lastChangedFrameExclusive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ct.ThrowIfCancellationRequested();
        LoudnessAnalyzer.Validate(samples, previous.SampleRate, previous.Channels);
        int frames = samples.Length / previous.Channels;
        if (frames != previous.FrameCount)
            return Create(samples, previous.SampleRate, previous.Channels, ct);
        int first = Math.Clamp(firstChangedFrame, 0, frames);
        int last = Math.Clamp(lastChangedFrameExclusive, first, frames);
        if (first == last)
            return new(previous.SampleRate, previous.Channels, frames, previous.hop,
                previous.energies, previous.counts, previous.states, 0);
        var energies = (double[])previous.energies.Clone();
        var counts = (int[])previous.counts.Clone();
        var states = (double[])previous.states.Clone();
        int startBin = first / previous.hop;
        int startFrame = startBin * previous.hop;
        var shelf = Enumerable.Range(0, previous.Channels).Select(_ => Biquad.Shelf(previous.SampleRate)).ToArray();
        var highPass = Enumerable.Range(0, previous.Channels).Select(_ => Biquad.HighPass(previous.SampleRate)).ToArray();
        Restore(previous.states, startBin, previous.Channels, shelf, highPass);
        int endFrame = startFrame;
        for (int bin = startBin; bin < energies.Length; bin++)
        {
            ct.ThrowIfCancellationRequested();
            int binFirst = bin * previous.hop;
            endFrame = Math.Min(frames, binFirst + previous.hop);
            energies[bin] = ProcessBin(samples, previous.Channels, binFirst, endFrame, shelf, highPass, ct);
            counts[bin] = endFrame - binFirst;
            Capture(states, bin + 1, previous.Channels, shelf, highPass);
            if (endFrame >= last && SameState(states, previous.states, bin + 1, previous.Channels)) break;
        }
        return new(previous.SampleRate, previous.Channels, frames, previous.hop,
            energies, counts, states, endFrame - startFrame);
    }

    private static double ProcessBin(float[] samples, int channels, int first, int last,
        Biquad[] shelf, Biquad[] highPass, CancellationToken ct)
    {
        double sum = 0;
        for (int frame = first; frame < last; frame++)
        {
            if ((frame & 16383) == 0) ct.ThrowIfCancellationRequested();
            double energy = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                float value = samples[frame * channels + channel];
                if (!float.IsFinite(value)) throw new ArgumentException("Audio contains non-finite samples.", nameof(samples));
                double weighted = highPass[channel].Process(shelf[channel].Process(value));
                energy += weighted * weighted;
            }
            sum += energy;
        }
        return sum;
    }

    private static void Capture(double[] destination, int checkpoint, int channels,
        Biquad[] shelf, Biquad[] highPass)
    {
        int offset = checkpoint * channels * 4;
        for (int channel = 0; channel < channels; channel++)
        {
            destination[offset++] = shelf[channel].Z1;
            destination[offset++] = shelf[channel].Z2;
            destination[offset++] = highPass[channel].Z1;
            destination[offset++] = highPass[channel].Z2;
        }
    }

    private static void Restore(double[] source, int checkpoint, int channels,
        Biquad[] shelf, Biquad[] highPass)
    {
        int offset = checkpoint * channels * 4;
        for (int channel = 0; channel < channels; channel++)
        {
            shelf[channel].Z1 = source[offset++];
            shelf[channel].Z2 = source[offset++];
            highPass[channel].Z1 = source[offset++];
            highPass[channel].Z2 = source[offset++];
        }
    }

    private static bool SameState(double[] left, double[] right, int checkpoint, int channels)
    {
        int first = checkpoint * channels * 4;
        for (int index = first; index < first + channels * 4; index++)
            if (BitConverter.DoubleToInt64Bits(left[index]) != BitConverter.DoubleToInt64Bits(right[index])) return false;
        return true;
    }

    private static double Integrate(double[] energies, int[] counts, int hop)
    {
        if (energies.Length == 0) return -70;
        var sum = new double[energies.Length + 1];
        var countSum = new long[energies.Length + 1];
        for (int index = 0; index < energies.Length; index++)
        {
            sum[index + 1] = sum[index] + energies[index];
            countSum[index + 1] = countSum[index] + counts[index];
        }
        double Window(int start, int end) => (sum[end] - sum[start]) / Math.Max(1L, countSum[end] - countSum[start]);
        var blocks = new List<double>();
        if (energies.Length < 4) blocks.Add(Window(0, energies.Length));
        else
        {
            for (int index = 0; index + 4 <= energies.Length; index++)
                if (countSum[index + 4] - countSum[index] >= hop * 4L) blocks.Add(Window(index, index + 4));
            if (blocks.Count == 0) blocks.Add(Window(0, energies.Length));
        }
        var absolute = blocks.Where(energy => LoudnessAnalyzer.EnergyToLufs(energy) >= -70).ToArray();
        if (absolute.Length == 0) return -70;
        double relativeGate = LoudnessAnalyzer.EnergyToLufs(absolute.Average()) - 10;
        var gated = absolute.Where(energy => LoudnessAnalyzer.EnergyToLufs(energy) >= relativeGate).ToArray();
        return gated.Length == 0 ? -70 : LoudnessAnalyzer.EnergyToLufs(gated.Average());
    }

    // Coefficients and operation order intentionally match LoudnessAnalyzer's
    // reference filter, so resumed checkpoints reproduce a full scan exactly.
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        public double Z1, Z2;
        public double Process(double value)
        {
            double output = b0 * value + Z1;
            Z1 = b1 * value - a1 * output + Z2;
            Z2 = b2 * value - a2 * output;
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
    }
}

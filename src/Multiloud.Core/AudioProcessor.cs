namespace Multiloud.Core;

/// <summary>Offline processing shared by audible preview and export.</summary>
public static partial class AudioProcessor
{
    public static RenderResult Render(float[] samples, int sampleRate, int channels,
        IReadOnlyList<EnvelopePoint> envelope, double trimDb, ProcessingSettings settings,
        CancellationToken ct = default, double inputGainDb = 0,
        IReadOnlyList<EnvelopePoint>? transitionEnvelope = null)
        => RenderIncremental(samples, sampleRate, channels, envelope, trimDb, settings,
            null, ct, inputGainDb, transitionEnvelope);

    /// <summary>Linear interpolation in dB; endpoints hold their values.</summary>
    public static double EnvelopeGainAt(IReadOnlyList<EnvelopePoint> points, double time)
    {
        if (points.Count == 0) return 0;
        if (time <= points[0].Time) return points[0].GainDb;
        if (time >= points[^1].Time) return points[^1].GainDb;
        int low = 0, high = points.Count - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (points[middle].Time <= time) low = middle;
            else high = middle;
        }
        double dt = points[high].Time - points[low].Time;
        if (dt <= 0) return points[high].GainDb;
        double fraction = Math.Clamp((time - points[low].Time) / dt, 0, 1);
        return points[low].GainDb + (points[high].GainDb - points[low].GainDb) * fraction;
    }

    public static List<PeakBin> BuildPeaks(float[] samples, int channels, int maxBins = 16000)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (maxBins <= 0) throw new ArgumentOutOfRangeException(nameof(maxBins));
        int frames = samples.Length / channels;
        if (frames == 0) return [];
        int bins = Math.Min(maxBins, frames);
        var result = new List<PeakBin>(bins);
        for (int bin = 0; bin < bins; bin++)
        {
            int first = (int)((long)bin * frames / bins) * channels;
            int last = (int)((long)(bin + 1) * frames / bins) * channels;
            float min = 0, max = 0;
            for (int i = first; i < last; i++)
            {
                min = Math.Min(min, samples[i]);
                max = Math.Max(max, samples[i]);
            }
            result.Add(new PeakBin(min, max));
        }
        return result;
    }

}

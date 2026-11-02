namespace Multiloud.Core;

/// <summary>
/// Band-limited random access to an immutable generated signal. Consumers supply
/// native samples on demand and may cache a small working set; no rendered copy
/// of the full signal is required. Rate kernels share the live mixer's cache.
/// </summary>
public sealed class RandomAccessResampler
{
    private readonly BackgroundRenderer.SincResampler? resampler;
    public RandomAccessResampler(int sourceRate, int targetRate)
    {
        if (sourceRate is < 8000 or > 384000 || targetRate is < 8000 or > 384000)
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (sourceRate != targetRate) resampler = BackgroundRenderer.SincResampler.Get(sourceRate, targetRate, default);
    }

    public double Read(Func<int, int, double> sampleAt, int frames, double position, int channel)
    {
        ArgumentNullException.ThrowIfNull(sampleAt);
        double nearest = Math.Round(position);
        if (resampler == null && Math.Abs(position - nearest) < 1e-8)
            return nearest < 0 || nearest >= frames ? 0 : sampleAt((int)nearest, channel);
        return (resampler ?? BackgroundRenderer.SincResampler.Unity).Read(sampleAt, frames, position, channel);
    }
}

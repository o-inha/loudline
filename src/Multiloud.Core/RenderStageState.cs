using System.Runtime.CompilerServices;

namespace Multiloud.Core;

internal sealed class RenderStageState
{
    public required float[] Source { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required bool LimiterEnabled { get; init; }
    public required double CeilingDb { get; init; }
    public required float[] Leveled { get; init; }
    public required double[] Detector { get; init; }
    public required LoudnessCache PreMatchMeter { get; init; }
    public required LoudnessCache OutputMeter { get; init; }
    public required PeakBlockCache OutputPeaks { get; init; }
    public long RetainedBytes => Leveled.LongLength * sizeof(float) + Detector.LongLength * sizeof(double)
        // Shared slices materialize a contiguous processor input on demand.
        // A retained stage owns that array even after the track leaves the UI.
        + (ReferenceEquals(Source, Leveled) ? 0 : Source.LongLength * sizeof(float))
        + PreMatchMeter.RetainedBytes + OutputMeter.RetainedBytes + OutputPeaks.RetainedBytes + 512;
}

/// <summary>Intermediate PCM has its own global bound, including active album tracks.</summary>
internal static class RenderStageRetention
{
    private sealed record Entry(WeakReference<RenderResult> Owner, long Bytes);
    private static readonly object Gate = new();
    private static readonly LinkedList<Entry> Recent = new();
    private static readonly ConditionalWeakTable<RenderResult, LinkedListNode<Entry>> Nodes = new();
    private const long Budget = 2L * 1024 * 1024 * 1024;
    private static long bytes;

    public static RenderStageState? Get(RenderResult? result)
    {
        if (result == null) return null;
        lock (Gate)
        {
            var stage = Volatile.Read(ref result.StageState);
            if (stage != null && Nodes.TryGetValue(result, out var node)) { Recent.Remove(node); Recent.AddFirst(node); }
            return stage;
        }
    }

    public static void Attach(RenderResult result, RenderStageState stage)
    {
        lock (Gate)
        {
            for (var node = Recent.Last; node != null;)
            {
                var previous = node.Previous;
                if (!node.Value.Owner.TryGetTarget(out _)) { bytes -= node.Value.Bytes; Recent.Remove(node); }
                node = previous;
            }
            Volatile.Write(ref result.StageState, stage);
            var entry = Recent.AddFirst(new Entry(new WeakReference<RenderResult>(result), stage.RetainedBytes));
            Nodes.Add(result, entry); bytes += entry.Value.Bytes;
            while (bytes > Budget && Recent.Last is { } oldest)
            {
                Recent.RemoveLast(); bytes -= oldest.Value.Bytes;
                if (oldest.Value.Owner.TryGetTarget(out var owner)) { Volatile.Write(ref owner.StageState, null); Nodes.Remove(owner); }
            }
        }
    }
}

/// <summary>Exact reconstruction maxima per 100 ms block, updated with FIR dependency margins.</summary>
internal sealed class PeakBlockCache
{
    private readonly double[] maxima;
    private readonly int hop, frames;
    public int RecomputedFrames { get; }
    public long RetainedBytes => maxima.LongLength * sizeof(double) + 64;
    public double Peak => maxima.Length == 0 ? 0 : maxima.Max();
    internal int BlockCount => maxima.Length;
    internal double BlockPeak(int index) => maxima[index];
    internal int BlockStart(int index) => index * hop;
    internal int BlockEnd(int index) => Math.Min(frames, (index + 1) * hop);

    private PeakBlockCache(float[] samples, int rate, int channels, PeakBlockCache? previous,
        int firstChanged, int lastChanged, CancellationToken ct)
    {
        frames = samples.Length / channels;
        hop = Math.Max(1, (int)Math.Round(rate * .1));
        maxima = previous == null ? new double[(frames + hop - 1) / hop] : (double[])previous.maxima.Clone();
        int first = previous == null ? 0 : Math.Max(0, firstChanged - 12) / hop;
        int end = previous == null ? maxima.Length : Math.Min(maxima.Length, (Math.Min(frames, lastChanged + 12) + hop - 1) / hop);
        if (previous != null && firstChanged >= lastChanged) { RecomputedFrames = 0; return; }
        for (int bin = first; bin < end; bin++)
        {
            double peak = 0;
            int from = bin * hop, to = Math.Min(frames, from + hop);
            for (int f = from; f < to; f++)
            {
                if ((f & 8191) == 0) ct.ThrowIfCancellationRequested();
                peak = Math.Max(peak, TruePeakMeter.FramePeak(samples, channels, f));
            }
            maxima[bin] = peak;
        }
        RecomputedFrames = Math.Max(0, Math.Min(frames, end * hop) - Math.Min(frames, first * hop));
    }
    public static PeakBlockCache Create(float[] samples, int rate, int channels, CancellationToken ct)
        => new(samples, rate, channels, null, 0, samples.Length / channels, ct);
    public static PeakBlockCache Update(PeakBlockCache previous, float[] samples, int rate, int channels, int first, int end, CancellationToken ct)
        => new(samples, rate, channels, previous, first, end, ct);
}

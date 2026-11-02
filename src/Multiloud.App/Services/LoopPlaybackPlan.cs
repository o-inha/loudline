using Multiloud.Core;

namespace Multiloud.App.Services;

/// <summary>
/// A fixed two-range itinerary. Its modulo mapping represents arbitrarily many
/// repeats without retaining an entry for each jump in the output-clock history.
/// </summary>
internal readonly struct LoopPlaybackPlan
{
    internal readonly record struct Range(long Start, long End)
    {
        public long Length => End - Start;
        public bool Contains(long frame) => frame >= Start && frame < End;
    }
    internal readonly record struct Segment(long Position, long Remaining, int Slot);
    private readonly Range a, b;
    private readonly bool hasA, hasB;
    private readonly long duration, initial, prefixLength;
    private readonly int prefixSlot;
    internal bool Active => hasA || hasB;
    internal long InitialFrame => initial;

    internal LoopPlaybackPlan(PlaybackLoopState state, long duration, long start, bool enterLoops, int preferredSlot = -1)
    {
        this.duration = Math.Max(0, duration);
        a = Effective(state.AEnabled ? state.A : null, this.duration);
        b = Effective(state.BEnabled ? state.B : null, this.duration);
        hasA = a.Length > 0; hasB = b.Length > 0;
        start = Math.Clamp(start, 0, this.duration);
        int inside = preferredSlot == 0 && hasA && a.Contains(start) ? 0 :
            preferredSlot == 1 && hasB && b.Contains(start) ? 1 :
            hasA && a.Contains(start) ? 0 : hasB && b.Contains(start) ? 1 : -1;
        if (enterLoops && Active && inside < 0)
        {
            inside = hasA ? 0 : 1;
            start = inside == 0 ? a.Start : b.Start;
        }
        initial = start;
        if (inside >= 0)
        {
            prefixSlot = inside;
            prefixLength = (inside == 0 ? a.End : b.End) - start;
        }
        else
        {
            // A manual seek outside the ranges remains audible. At the next
            // enabled loop end we enter its itinerary; after all ends, EOF hands
            // off to the first enabled loop instead of ending the endpoint.
            prefixSlot = hasA && a.End > start && (!hasB || b.End <= start || a.End <= b.End) ? 0 :
                hasB && b.End > start ? 1 : -1;
            prefixLength = (prefixSlot == 0 ? a.End : prefixSlot == 1 ? b.End : this.duration) - start;
        }
    }

    internal Segment At(long elapsed)
    {
        elapsed = Math.Max(0, elapsed);
        if (!Active) return new(Math.Min(duration, initial + elapsed), Math.Max(0, duration - initial - elapsed), -1);
        if (elapsed < prefixLength)
        {
            long position = initial + elapsed;
            int slot = prefixSlot == 0 && a.Contains(position) ? 0 : prefixSlot == 1 && b.Contains(position) ? 1 : -1;
            return new(position, prefixLength - elapsed, slot);
        }
        elapsed -= prefixLength;
        int first = prefixSlot < 0 ? hasA ? 0 : 1 : Next(prefixSlot);
        if (hasA && hasB)
        {
            elapsed %= a.Length + b.Length;
            var one = first == 0 ? a : b;
            if (elapsed < one.Length) return new(one.Start + elapsed, one.Length - elapsed, first);
            elapsed -= one.Length;
            var two = first == 0 ? b : a;
            return new(two.Start + elapsed, two.Length - elapsed, 1 - first);
        }
        var only = hasA ? a : b;
        elapsed %= only.Length;
        return new(only.Start + elapsed, only.Length - elapsed, hasA ? 0 : 1);
    }

    internal double EdgeGain(long elapsed)
    {
        var segment = At(elapsed);
        if (segment.Slot < 0) return 1;
        var range = segment.Slot == 0 ? a : b;
        if (range.Length < 16) return 1;
        long fade = Math.Min(96, range.Length / 8); // At most 2 ms on the 48 kHz monitor clock.
        double amount = 1;
        if (segment.Remaining <= fade) amount = Math.Clamp((segment.Remaining - 1) / (double)fade, 0, 1);
        if (elapsed >= prefixLength && segment.Position - range.Start < fade)
            amount = Math.Min(amount, (segment.Position - range.Start) / (double)fade);
        return amount >= 1 ? 1 : .5 - .5 * Math.Cos(Math.PI * amount);
    }

    private int Next(int slot) => hasA && hasB ? 1 - slot : hasA ? 0 : 1;

    private static Range Effective(LoopRange? range, long duration)
    {
        if (range?.IsValid != true || duration <= 0) return default;
        double first = Math.Clamp(range.StartSeconds * 48000, 0, duration);
        double last = Math.Clamp(range.EndSeconds * 48000, 0, duration);
        long start = (long)Math.Round(first), end = (long)Math.Round(last);
        return end > start ? new(start, end) : default;
    }
}

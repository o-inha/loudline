namespace Multiloud.Core;

public static partial class EnvelopeDesigner
{
    /// <summary>
    /// Applies only a change in suggestions: authored + next - previous, exactly
    /// as piecewise-linear dB curves. Manual offsets and their knot times survive.
    /// The caller owns any application-specific gain limit.
    /// </summary>
    public static List<EnvelopePoint> Retarget(IReadOnlyList<EnvelopePoint> authored,
        IReadOnlyList<EnvelopePoint> previousSuggestion, IReadOnlyList<EnvelopePoint> nextSuggestion, double duration)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(previousSuggestion);
        ArgumentNullException.ThrowIfNull(nextSuggestion);
        if (!double.IsFinite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        var before = EnvelopeMath.Canonicalize(previousSuggestion);
        var after = EnvelopeMath.Canonicalize(nextSuggestion);
        if (before.SequenceEqual(after)) return authored.ToList();
        var manual = EnvelopeMath.Canonicalize(authored);
        var times = authored.Where(point => double.IsFinite(point.Time) && double.IsFinite(point.GainDb)).Select(point => point.Time)
            .Concat(before.Select(point => point.Time)).Concat(after.Select(point => point.Time))
            .Append(0).Append(duration).Where(time => time >= 0 && time <= duration).Distinct().Order();
        return times.Select(time => new EnvelopePoint(time, AudioProcessor.EnvelopeGainAt(manual, time) +
            (AudioProcessor.EnvelopeGainAt(after, time) - AudioProcessor.EnvelopeGainAt(before, time)))).ToList();
    }
}

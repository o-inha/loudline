namespace Multiloud.Core;

/// <summary>An authored audition range on the project clock; exports ignore it.</summary>
public sealed record LoopRange(double StartSeconds, double EndSeconds)
{
    public bool IsValid => double.IsFinite(StartSeconds) && double.IsFinite(EndSeconds) &&
        StartSeconds >= 0 && EndSeconds > StartSeconds;
}

/// <summary>Independent switches retain their ranges when hidden or disabled.</summary>
public sealed record PlaybackLoopState(bool AEnabled = false, LoopRange? A = null,
    bool BEnabled = false, LoopRange? B = null)
{
    public static PlaybackLoopState Empty { get; } = new();
}

namespace Multiloud.Core;

/// <summary>A non-looping source positioned on the album timeline.</summary>
public sealed record BackgroundClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AudioTrack Source { get; init; } = new();
    public double StartSeconds { get; init; }
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    public double EndSeconds => StartSeconds + Source.Duration;
}

/// <summary>Fixed background audio; processing changes the music, never this bed.</summary>
public sealed record BackgroundLayer
{
    public bool Enabled { get; init; } = true;
    public double GainDb { get; init; }
    public IReadOnlyList<BackgroundClip> Clips { get; init; } = Array.Empty<BackgroundClip>();
}

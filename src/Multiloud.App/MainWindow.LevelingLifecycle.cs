using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private static bool SameLevelingDesign(ProcessingSettings first, ProcessingSettings second) =>
        first.BassSensitivity == second.BassSensitivity && first.MaxLevelingDb == second.MaxLevelingDb &&
        first.TransitionSeconds == second.TransitionSeconds;

    // Preserve the user's displacement from the automatic suggestion, including
    // inserted points, rather than replacing the authored curve on Apply.
    private static List<EnvelopePoint> RetargetLevelingEnvelope(IReadOnlyList<EnvelopePoint> authored,
        AnalysisResult analysis, double duration, ProcessingSettings previous, ProcessingSettings updated)
    {
        if (SameLevelingDesign(previous, updated)) return authored.ToList();
        var oldSuggestion = EnvelopeDesigner.Design(analysis, duration, previous);
        var nextSuggestion = EnvelopeDesigner.Design(analysis, duration, updated);
        var result = EnvelopeDesigner.Retarget(authored, oldSuggestion, nextSuggestion, duration);
        return EnvelopeMath.Canonicalize(result.Select(point => point with { GainDb = Math.Clamp(point.GainDb, -24, 24) }).ToArray());
    }
}

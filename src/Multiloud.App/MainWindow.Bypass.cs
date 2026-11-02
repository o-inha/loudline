using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    // Bypassing all modules is a live source mix. Static faders and a bed do
    // not require an album render, a loudness scan, or a safety gain change.
    private static bool ModulesBypassed(ProcessingSettings settings) =>
        !settings.LevelingEnabled && !settings.MatchingEnabled && !settings.LimiterEnabled;

    private bool CanUseSourceBypass(AudioTrack track, ProcessingSettings settings) =>
        ModulesBypassed(settings) && track.Original.Length > 0 &&
        track.OriginalWaveform is { } waveform && track.OriginalPeaks.Count > 0 &&
        track.Channels is >= 1 and <= 2 && track.SampleRate is >= 8000 and <= 384000 &&
        track.Original.Length % track.Channels == 0 && track.OriginalFrameOffset >= 0 && track.OriginalFrameCount > 0 &&
        (long)track.OriginalFrameOffset + track.OriginalFrameCount <= track.Original.Length / track.Channels &&
        waveform.Channels == track.Channels && waveform.FrameCount == track.OriginalFrameCount;

    private LiveAudioSource LiveSourceFor(AudioTrack track, double gainDb = 0) =>
        new(track.Original, track.SampleRate, track.Channels, track.OriginalFrameOffset,
            track.OriginalFrameCount, BackgroundAlbumStart(track), gainDb,
            UsesBackgroundFormat ? _backgroundLayer : null);

    private bool IsCurrentSourceBypass(AudioTrack track, ProcessingSettings settings) =>
        CanUseSourceBypass(track, settings) && track.Render?.LiveSource is { } live &&
        ReferenceEquals(live.Samples, track.Original) && live.FrameOffset == track.OriginalFrameOffset &&
        live.FrameCount == track.OriginalFrameCount && PreviewCurrent(track, settings);

    private bool SourceAnalysisSatisfied(AudioTrack track, ProcessingSettings settings) =>
        track.Analysis != null || IsCurrentSourceBypass(track, settings);

    private bool TryPublishIdentityPreview(AudioTrack track, ProcessingSettings settings)
    {
        if (!CanUseSourceBypass(track, settings)) return false;
        var waveform = track.OriginalWaveform!;
        var analysis = track.Analysis;
        double gain = Math.Clamp(track.InputGainDb, -30, 30) + Math.Clamp(track.TrimDb, -36, 36);
        bool measuredMusicOnly = !UsesBackgroundFormat && analysis != null;
        double loudness = measuredMusicOnly ? analysis!.IntegratedLufs + gain : double.NaN;
        double peak = measuredMusicOnly ? analysis!.TruePeakDb + gain : double.NaN;
        var live = LiveSourceFor(track, gain);
        if (IsCurrentSourceBypass(track, settings) && track.Render is { } current &&
            current.LiveSource == live && current.OutputLufs.Equals(loudness) && current.TruePeakDb.Equals(peak))
        {
            track.WorkState = TrackWorkState.Idle; track.Status = current.Warning ?? "Ready";
            return true;
        }
        var rendered = new RenderResult
        {
            Samples = track.Original, Waveform = waveform, LiveSource = live,
            OutputLufs = loudness,
            PreMatchLufs = analysis?.IntegratedLufs ?? double.NaN,
            TruePeakDb = peak,
            RenderedSampleRate = PlaybackSampleRate(track), RenderedChannels = PlaybackChannels(track),
            RenderedInputGainDb = track.InputGainDb, RenderedTrimDb = track.TrimDb,
            RenderedLevelingStrength = 0, RenderedMatchingStrength = 0,
            RenderedTargetLufs = track.TargetLufs ?? settings.TargetLufs,
            RenderedLimiterEnabled = false, RenderedCeilingDb = Math.Clamp(settings.CeilingDb, -12, 0),
            RenderedMaximizerStyle = settings.MaximizerStyle,
            RenderedMaximizerCharacter = Math.Clamp(settings.MaximizerCharacter, 0, 1),
            RenderedTransientPreservation = Math.Clamp(settings.TransientPreservation, 0, 1),
            RenderedBassProtection = Math.Clamp(settings.BassProtection, 0, 1),
            RenderedDynamicsGuardEnabled = settings.DynamicsGuardEnabled,
            RenderedMaxSustainedReductionDb = Math.Clamp(settings.MaxSustainedReductionDb, 0, 12),
            RenderedEnvelope = Array.AsReadOnly(EnvelopeMath.Canonicalize(
                TransitionDesigner.CombinedEnvelope(track)).ToArray()),
            Work = new(false, 0, 0, 0, 0, 0, 0),
            Warning = !UsesBackgroundFormat && analysis is { IntegratedLufs: <= -69 }
                ? "No measurable programme loudness; silence was not amplified." : null
        };
        string key = RenderKey(track, settings);
        RememberActivePreview(track);
        track.Render = rendered; track.PreviewPeaks = track.OriginalPeaks;
        _renderKeys[track.Id] = key;
        RememberBackgroundSegment(track, rendered);
        track.WorkState = TrackWorkState.Idle;
        track.Status = rendered.Warning ?? "Ready";
        return true;
    }
}

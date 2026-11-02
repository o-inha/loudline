namespace Multiloud.App;

public partial class MainWindow
{
    private async Task WaitForSourceEditPreviewSmokeAsync(string phase)
    {
        try { await WaitForSmokePreviewAsync(); }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{phase}; gate={_renderGate.CurrentCount}, " +
                $"atomic={_atomicSourceOperation}, sourcePending={_sourceProcessingPending}, " +
                $"importToken={_importCts != null}, planned={_plannedRenderKeys?.Count ?? -1}, " +
                $"settings={_settings.LevelingEnabled}/{_settings.MatchingEnabled}/{_settings.LimiterEnabled}, " +
                $"project={_projectPath}; " + string.Join("; ", _tracks.Select(track =>
                    $"{track.Name}: range={track.SourceStartSeconds:F4}+{track.SourceDurationSeconds:F4}, " +
                    $"backgroundReady={BackgroundExportReady(track)}, block={BackgroundBlockReason(track) ?? "none"}")), error);
        }
    }
}

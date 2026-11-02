using Multiloud.App.Services;
using Multiloud.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyPeakDeltaSmoke()
    {
        bool? delta = DeltaCheck.IsChecked, solo = BackgroundSoloButton.IsChecked, match = AuditionMatchCheck.IsChecked;
        bool? original = OriginalRadio.IsChecked, compare = CompareRadio.IsChecked;
        bool dirty = _dirty, syncing = _syncing;
        int revision = _revision, executions = _renderExecutionCount;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var renders = _tracks.Select(track => track.Render).ToArray();
        var keys = _renderKeys.ToArray();
        var first = _tracks.FirstOrDefault(); var state = first?.WorkState;
        try
        {
            AuditionMatchCheck.IsChecked = true;
            DeltaCheck.IsChecked = true;
            DeltaCheck.ApplyTemplate();
            if (DeltaCheck.Content is not System.Windows.Shapes.Path ear || !ReferenceEquals(ear.Data, FindResource("SoloIconGeometry")) ||
                Descendants<TextBlock>(DeltaCheck).Any() || !Equals(DeltaCheck.ToolTip, "Delta") || !ToolTipService.GetIsEnabled(DeltaCheck) ||
                DeltaCheck.ActualHeight != 28 || AuditionMatchCheck.IsEnabled || IsBackgroundSolo ||
                DeltaCheck.Template.FindName("MonitorSurface", DeltaCheck) is not Border surface ||
                surface.Background is not SolidColorBrush color || color.Color != ((SolidColorBrush)FindResource(DeltaCheck.IsMouseOver ? "MonitorActiveHoverBrush" : "MonitorActiveBrush")).Color)
                throw new InvalidOperationException("Smoke test: Delta lost its always-icon ear, tooltip, amber state or exclusive monitoring controls.");
            foreach (var view in new[] { OriginalRadio, PreviewRadio, CompareRadio })
            {
                view.IsChecked = true;
                var states = CapturePlaybackTracks();
                if (states.Length != _tracks.Count || states.Any(track => !track.DeltaRequested || track.PeakDelta == null || !track.IsReady(true) || !track.IsReady(false) ||
                    track.OriginalAuditionGainDb != 0 || track.PreviewAuditionGainDb != 0))
                    throw new InvalidOperationException("Smoke test: Delta did not publish the same uncompensated protection-only route in all views.");
            }
            if (first != null)
            {
                first.WorkState = TrackWorkState.Processing;
                var pending = PlaybackStateFor(first);
                if (!pending.DeltaRequested || pending.PeakDelta != null || pending.IsReady(true) || pending.IsReady(false))
                    throw new InvalidOperationException("Smoke test: pending Delta fell back to unprocessed music.");
                first.WorkState = state!.Value;
            }
            BackgroundSoloButton.IsChecked = true;
            if (IsPeakDelta || !AuditionMatchCheck.IsEnabled || !IsBackgroundSolo)
                throw new InvalidOperationException("Smoke test: Background Solo did not replace Delta monitoring.");
            DeltaCheck.IsChecked = true;
            if (!IsPeakDelta || IsBackgroundSolo)
                throw new InvalidOperationException("Smoke test: Delta did not replace Background Solo monitoring.");
            DeltaCheck.IsChecked = false;
            if (!AuditionMatchCheck.IsEnabled || CapturePlaybackTracks().Any(track => track.DeltaRequested || track.PeakDelta != null))
                throw new InvalidOperationException("Smoke test: disabling Delta did not restore normal monitoring.");

            var options = CreateMaximizerOptionsContent(() => { });
            if (options.FindName("MaximizerTransparentButton") is not RadioButton { Style: not null } transparent ||
                options.FindName("MaximizerPunchyButton") is not RadioButton { Style: not null } punchy ||
                transparent.Tag as string != "First" || punchy.Tag as string != "Last" ||
                options.FindName("MaximizerBassProtectionSlider") is not Slider ||
                options.FindName("MaximizerDynamicsGuardCheck") is not CheckBox ||
                options.FindName("MaximizerSustainedReductionBox") is not TextBox { TextAlignment: TextAlignment.Center } ||
                !Descendants<TextBlock>(options).Any(text => text.Text == "Response") ||
                !Descendants<TextBlock>(options).Any(text => text.Text == "Fast") || !Descendants<TextBlock>(options).Any(text => text.Text == "Slow"))
                throw new InvalidOperationException("Smoke test: protection gear did not contain grouped styles, response endpoints and advanced protection controls.");
            if (_revision != revision || _renderExecutionCount != executions || _dirty != dirty || !_undo.SequenceEqual(undo) || !_redo.SequenceEqual(redo) ||
                !_renderKeys.SequenceEqual(keys) || _tracks.Where((track, index) => !ReferenceEquals(track.Render, renders[index])).Any())
                throw new InvalidOperationException("Smoke test: Delta monitoring or a draft protection panel changed processing, exports or edit history.");
        }
        finally
        {
            if (first != null && state.HasValue) first.WorkState = state.Value;
            _syncing = true;
            DeltaCheck.IsChecked = delta; BackgroundSoloButton.IsChecked = solo; AuditionMatchCheck.IsChecked = match;
            OriginalRadio.IsChecked = original; CompareRadio.IsChecked = compare;
            if (original != true && compare != true) PreviewRadio.IsChecked = true;
            _syncing = syncing; SyncPeakDeltaControls(); PublishPlaybackTracks();
        }
    }
}

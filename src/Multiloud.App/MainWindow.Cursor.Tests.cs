using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyCursorReadoutSmokeAsync(string? screenshot)
    {
        bool forwardingDetached = false;
        try
        {
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.ContextIdle);
            var signal = Timeline.SignalPlotArea;
            var point = new Point(signal.Left + signal.Width * .63, Timeline.SignalCenterY - Timeline.SignalAmplitude * .5);
            Timeline.SetHoveredTrack(point, force: true);
            Timeline.SetCrosshairPointer(point); Timeline.FlushPendingRendering();
            // Verify the real event subscription before isolating the paint check.
            if (Timeline.CursorInfo is not { } cursor || Math.Abs(cursor.AmplitudeDbfs + 6.0206) > .001 ||
                !CursorPositionText.Text.Contains("dBFS") || !CursorPositionText.Text.Contains("Envelope +"))
                throw new InvalidOperationException($"Smoke test: cursor measurements did not reach the transport readout. Info={Timeline.CursorInfo}; Text={CursorPositionText.Text}");

            // Remote desktop input can legitimately clear this synthetic hover
            // while WPF paints. Movement, leave and camera tracking are tested
            // separately; hold the already-delivered text only for native glyph QA.
            Timeline.CursorInfoChanged -= UpdateCursorPosition;
            forwardingDetached = true;
            await Dispatcher.InvokeAsync(() => UpdateLayout(), DispatcherPriority.ContextIdle);
            // Re-establish a non-null cursor state even without screenshots so
            // reattaching the real subscription always observes the clear below.
            Timeline.SetHoveredTrack(point, force: true);
            Timeline.SetCrosshairPointer(point); Timeline.FlushPendingRendering();
            if (screenshot != null)
            {
                var readout = CaptureVisual(CursorPositionText, Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-cursor-readout.png"));
                if (!ComparisonPixels(readout).Any(pixel => (pixel >> 24) != 0))
                    throw new InvalidOperationException("Smoke test: cursor readout text exists but its native glyphs are not visible.");
                CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot)!, "Loudline-crosshair.png"));
            }
            Timeline.CursorInfoChanged += UpdateCursorPosition;
            forwardingDetached = false;
            Timeline.SetCrosshairPointer(null); Timeline.FlushPendingRendering();
            if (CursorPositionText.Text.Length != 0)
                throw new InvalidOperationException("Smoke test: leaving the timeline retained the transport cursor readout.");
        }
        finally
        {
            if (forwardingDetached) Timeline.CursorInfoChanged += UpdateCursorPosition;
            Timeline.SetHoveredTrack(new Point(-10, -10), force: true);
            Timeline.SetCrosshairPointer(null); Timeline.FlushPendingRendering();
            UpdateCursorPosition(Timeline.CursorInfo);
        }
    }
}

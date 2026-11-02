using Multiloud.App.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyImportedOriginalDetailSmoke()
    {
        // Exercise the published tracks from the real async import, rather than
        // seeding pyramids directly as the renderer-only fixtures do.
        foreach (var track in _tracks)
        {
            var frames = track.Original.Length / track.Channels;
            if (track.OriginalWaveform is not { } pyramid || pyramid.FrameCount != frames || pyramid.Channels != track.Channels)
                throw new InvalidOperationException("Smoke test: importing discarded the Original waveform's full-resolution data.");
            var source = new WaveformTileSource(pyramid, pyramid, track.OriginalPeaks, track.SampleRate, track.Duration);
            var first = Math.Min(track.SampleRate * 2, frames - 128);
            var resolvedFineDetail = false;
            for (var frame = first; frame < first + 128; frame++)
            {
                // Fractional bounds stay strictly inside one frame, avoiding
                // floating-point ambiguity at exact sample boundaries.
                var start = (frame + .25) / track.SampleRate;
                var end = (frame + .75) / track.SampleRate;
                var fine = source.Query(start, end, coarse: false);
                float min = 0, max = 0;
                for (var channel = 0; channel < track.Channels; channel++)
                {
                    var sample = track.Original[frame * track.Channels + channel];
                    min = Math.Min(min, sample); max = Math.Max(max, sample);
                }
                if (fine != new WaveformExtrema(min, max))
                    throw new InvalidOperationException("Smoke test: a zoomed Original query did not resolve the imported samples exactly.");
                resolvedFineDetail |= fine != source.Query(start, end, coarse: true);
            }
            if (!resolvedFineDetail)
                throw new InvalidOperationException("Smoke test: Original retained only overview detail after importing.");
        }
    }
}

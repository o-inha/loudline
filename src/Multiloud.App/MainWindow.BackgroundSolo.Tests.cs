using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyBackgroundSoloSmokeAsync()
    {
        await WaitForSmokePreviewAsync();
        var tracks = _tracks.ToArray(); var layer = _backgroundLayer;
        bool? solo = BackgroundSoloButton.IsChecked, original = OriginalRadio.IsChecked, compare = CompareRadio.IsChecked;
        bool dirty = _dirty, syncing = _syncing;
        int revision = _revision, executions = _renderExecutionCount;
        var keys = _renderKeys.ToArray(); var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var segments = _backgroundSegments.ToArray(); var prepared = _backgroundOriginals.ToArray();
        try
        {
            const int rate = 48000;
            var bed = new float[rate * 2 * 3];
            for (int frame = 0; frame < rate * 3; frame++)
            { bed[frame * 2] = (float)(.025 * Math.Sin(frame * 2 * Math.PI * 137 / rate)); bed[frame * 2 + 1] = .008f; }
            _backgroundLayer = new BackgroundLayer
            {
                Enabled = false, GainDb = -6,
                Clips = [new BackgroundClip { Source = new AudioTrack { Original = bed, SampleRate = rate, Channels = 2 },
                    FadeInSeconds = .2, FadeOutSeconds = .4 }]
            };
            _tracks.Clear();
            _tracks.AddRange([new AudioTrack { SavedDuration = 1, WorkState = TrackWorkState.Queued },
                new AudioTrack { SavedDuration = 1, WorkState = TrackWorkState.Processing }]);
            BackgroundSoloButton.IsChecked = true;
            foreach (var mode in new[] { OriginalRadio, PreviewRadio, CompareRadio })
            {
                mode.IsChecked = true;
                var state = CapturePlaybackTracks().Single();
                RequireBackground(state.SoloBackground != null && state.IsReady(OriginalRadio.IsChecked == true) &&
                    state.OriginalSamples == null && state.PreviewSamples == null && state.Duration == 2,
                    "Solo depended on pending music processing or supplied a pretend music render.");
                var provider = PlaybackService.CreateSampleProvider(() => CapturePlaybackTracks(), OriginalRadio.IsChecked == true, .375);
                var heard = new float[258];
                RequireBackground(provider.Read(heard, 0, heard.Length) == heard.Length, "Solo could not audition while music was pending.");
                var expected = BackgroundRenderer.RenderSegment(_backgroundLayer with { Enabled = true }, .375, heard.Length / 2d / rate, rate, 2);
                RequireBackground(heard.Select(BitConverter.SingleToInt32Bits).SequenceEqual(expected.Select(BitConverter.SingleToInt32Bits)),
                    "Solo changed the fixed background gain/fades or played music/A-B monitor gain.");
            }
            _tracks.Clear();
            RequireBackground(CapturePlaybackTracks() is [{ Duration: 3, SoloBackground: not null }],
                "A background-only project could not be auditioned.");
            BackgroundSoloButton.IsChecked = false;
            RequireBackground(_revision == revision && _renderExecutionCount == executions && _dirty == dirty &&
                _renderKeys.SequenceEqual(keys) && _undo.SequenceEqual(undo) && _redo.SequenceEqual(redo) &&
                _backgroundSegments.SequenceEqual(segments) && _backgroundOriginals.SequenceEqual(prepared) && !_backgroundLayer.Enabled,
                "The monitoring-only Solo toggle changed history, render/cache identity, project state or export inclusion.");
        }
        finally
        {
            _syncing = true;
            _tracks.Clear(); _tracks.AddRange(tracks); _backgroundLayer = layer;
            BackgroundSoloButton.IsChecked = solo;
            OriginalRadio.IsChecked = original; CompareRadio.IsChecked = compare;
            if (original != true && compare != true) PreviewRadio.IsChecked = true;
            _syncing = syncing;
            RefreshBackground(); PublishPlaybackTracks();
        }
    }
}

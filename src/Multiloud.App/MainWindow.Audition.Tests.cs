using System.Windows;
using System.Windows.Controls;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyAuditionMatchSmoke()
    {
        bool? saved = AuditionMatchCheck.IsChecked;
        AuditionMatchCheck.IsChecked = false;
        int revision = _revision, renderedRevision = _renderedRevision, executions = _renderExecutionCount;
        int history = _undo.Count;
        bool dirty = _dirty;
        var settings = _settings;
        var originals = _tracks.Select(track => track.Original).ToArray();
        var renders = _tracks.Select(track => track.Render).ToArray();
        var keys = _tracks.Select(track => _renderKeys[track.Id]).ToArray();
        try
        {
            AuditionMatchCheck.IsChecked = true;
            var matched = CapturePlaybackTracks();
            for (int index = 0; index < _tracks.Count; index++)
            {
                var track = _tracks[index]; var state = matched[index];
                var sourceLufs = track.Analysis!.IntegratedLufs; var resultLufs = track.Render!.OutputLufs;
                var common = Math.Min(sourceLufs, resultLufs);
                if (state.OriginalAuditionGainDb > 0 || state.PreviewAuditionGainDb > 0 ||
                    Math.Abs(sourceLufs + state.OriginalAuditionGainDb - common) > 1e-9 ||
                    Math.Abs(resultLufs + state.PreviewAuditionGainDb - common) > 1e-9 ||
                    !ReferenceEquals(state.OriginalSamples, originals[index]) || !ReferenceEquals(state.PreviewSamples, renders[index]!.Samples))
                    throw new InvalidOperationException("Smoke test: level-matched audition did not attenuate both modes to the quieter reference without copying PCM.");
            }
            if (!matched.Any(track => track.OriginalAuditionGainDb < 0 || track.PreviewAuditionGainDb < 0))
                throw new InvalidOperationException("Smoke test: level-matched audition fixture did not exercise attenuation.");
            AuditionMatchCheck.IsChecked = false;
            if (CapturePlaybackTracks().Any(track => track.OriginalAuditionGainDb != 0 || track.PreviewAuditionGainDb != 0) ||
                _revision != revision || _renderedRevision != renderedRevision || _renderExecutionCount != executions || _undo.Count != history || _dirty != dirty ||
                !ReferenceEquals(_settings, settings) || _tracks.Where((track, index) => !ReferenceEquals(track.Original, originals[index]) ||
                    !ReferenceEquals(track.Render, renders[index]) || _renderKeys[track.Id] != keys[index]).Any())
                throw new InvalidOperationException("Smoke test: monitor-only A/B changes affected rendering, settings, exports or undo history.");

            FileList.UpdateLayout();
            foreach (var item in Descendants<ListBoxItem>(FileList))
                if (item.Template.FindName("TrackGrip", item) is FrameworkElement grip && grip.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Smoke test: a file-list reorder grip still depends on hovering.");
        }
        finally { AuditionMatchCheck.IsChecked = saved; }
    }
}

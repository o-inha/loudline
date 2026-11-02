using System.Windows;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private Task VerifyProjectEnvelopeSmokeAsync()
    {
        var points = new List<EnvelopePoint> { new(0, 0), new(4, 3), new(16, 0), new(24, 0) };
        var settings = new ProcessingSettings { Strength = 1, MatchingEnabled = false, LimiterEnabled = false };
        var tracks = Enumerable.Range(0, 3).Select(index => new AudioTrack
        {
            FilePath = $"Project envelope {index}.wav", SampleRate = 1024, Channels = 1,
            Original = Enumerable.Range(0, 8192).Select(frame => (float)(.1 * Math.Sin(frame * .2))).ToArray(),
            Analysis = new AnalysisResult { IntegratedLufs = -20 }
        }).ToArray();
        void Project()
        {
            for (int index = 0; index < tracks.Length; index++)
                tracks[index].Envelope = ProjectEnvelopeDesigner.Crop(points, index * 8, 8);
        }
        Project();
        foreach (var track in tracks)
        {
            track.OriginalWaveform = WaveformPyramid.Build(track.Original, 1);
            track.OriginalPeaks = BuildDisplayPeaks(track.OriginalWaveform);
        }
        var timeline = new TimelineView
        {
            Tracks = tracks, Settings = settings, ProjectEnvelope = points, ShowGainLines = false,
            ViewMode = WaveformViewMode.Original
        };
        int starts = 0, commits = 0;
        timeline.ProjectEnvelopeEdited += Project;
        timeline.EditStarted += () => starts++;
        timeline.EditCommitted += _ => commits++;
        var size = new Size(960, 600);
        void Layout() { timeline.Measure(size); timeline.Arrange(new Rect(size)); timeline.UpdateLayout(); timeline.FlushPendingRendering(); }
        Layout(); timeline.FitAll(); Layout();
        string thirdKey = PreviewCache.CreateKey(tracks[2], settings);
        var origin = timeline.EnvelopePointPosition(tracks[0], 1);
        timeline.HandleWaveformPointerDown(tracks[0], origin, 1);
        timeline.HandleWaveformPointerMove(new Point(origin.X + timeline.PlotArea.Width * 6 / 24, origin.Y - 10));
        timeline.CompletePointerInteraction(); Layout();
        if (Math.Abs(points[1].Time - 10) > 1e-7 || points[1].GainDb <= 3 || starts != 1 || commits != 1)
            throw new InvalidOperationException("Smoke test: project envelope point could not be dragged freely across a music file boundary.");
        if (Math.Abs(tracks[0].Envelope[^1].GainDb - tracks[1].Envelope[0].GainDb) > 1e-10 ||
            tracks.Any(track => track.TransitionEnvelope.Count != 0) || PreviewCache.CreateKey(tracks[2], settings) != thirdKey)
            throw new InvalidOperationException("Smoke test: shared envelope editing reset its gain at an edge or changed an unrelated track's render shape.");
        var keys = tracks.Select(track => PreviewCache.CreateKey(track, settings)).ToArray();
        timeline.InsertEnvelopePointOnCurve(tracks[1], 4); timeline.CompletePointerInteraction();
        if (!keys.SequenceEqual(tracks.Select(track => PreviewCache.CreateKey(track, settings))))
            throw new InvalidOperationException("Smoke test: adding a UI point on the project envelope changed a local audio render key.");
        var bPosition = timeline.EnvelopePointPosition(tracks[1], points.FindIndex(point => Math.Abs(point.Time - 12) < 1e-7));
        timeline.SliceMode = true;
        AudioTrack? sliced = null; double sliceTime = -1;
        timeline.SliceRequested += (track, local) => { sliced = track; sliceTime = local; };
        timeline.HandleWaveformPointerDown(tracks[1], bPosition, 1);
        if (!ReferenceEquals(sliced, tracks[1]) || Math.Abs(sliceTime - 4) > 1e-7)
            throw new InvalidOperationException("Smoke test: Slice tool did not take priority over an envelope point or report source-local file time.");
        timeline.SliceMode = false;
        return Task.CompletedTask;
    }
}

using System.Windows;
using Multiloud.App.Controls;
using Multiloud.App.Services;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyScrubbingSmokeAsync()
    {
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Scrubbing smoke: " + message); }
        var samples = Enumerable.Repeat(.2f, 48000 * 2 * 2).ToArray();
        var source = new AudioTrack { Original = samples, SampleRate = 48000, Channels = 2 };
        var timeline = new TimelineView { Tracks = new[] { source }, ShowEnvelopeOnWaveform = false, ShowGainLines = false };
        timeline.Measure(new Size(1600, 500)); timeline.Arrange(new Rect(0, 0, 1600, 500)); timeline.FitAll();
        var events = new List<(string Name, double Time)>();
        timeline.ScrubStarted += time => events.Add(("begin", time));
        timeline.ScrubPositionRequested += time => events.Add(("position", time));
        timeline.ScrubCompleted += time => events.Add(("end", time));
        int cuts = 0; timeline.SliceRequested += (_, _) => ++cuts;
        foreach (var mode in new[] { WaveformViewMode.Original, WaveformViewMode.Preview, WaveformViewMode.Compare })
        {
            timeline.ViewMode = mode;
            foreach (bool ruler in new[] { false, true })
            {
                events.Clear(); timeline.SliceMode = ruler;
                var area = timeline.PlotArea;
                var first = new Point(area.Left + area.Width * .25, ruler ? area.Top - 6 : timeline.SignalPlotArea.Top + 16);
                var last = new Point(area.Left + area.Width * .75, first.Y);
                if (ruler) Require(timeline.HandleNavigationPointerDown(first, 1), "Ruler click was not handled with Slice active");
                else timeline.HandleWaveformPointerDown(source, first, 1);
                Require(timeline.HandleNavigationPointerMove(last), "Playhead drag did not produce scrub movement");
                timeline.CompletePointerInteraction(); timeline.CompletePointerInteraction();
                Require(events.Count == 4 && events[0].Name == "begin" && events[1].Name == "position" &&
                    events[2].Name == "position" && events[3].Name == "end" &&
                    Math.Abs(events[0].Time - .5) < .00001 && Math.Abs(events[^1].Time - 1.5) < .00001,
                    $"The {mode} {(ruler ? "ruler" : "waveform")} lost or duplicated a scrub event");
            }
        }
        Require(cuts == 0, "Dragging the ruler while Slice was active cut music");
        events.Clear(); timeline.SliceMode = false;
        timeline.HandleWaveformPointerDown(source, new Point(timeline.PlotArea.Left + 20, timeline.SignalPlotArea.Top + 16), 1);
        timeline.ResetProjectInteractions();
        Require(events.Count(item => item.Name == "end") == 1, "Reset/capture loss left a scrub gesture open");

        var bed = new BackgroundLayer { Clips = [new BackgroundClip { Source = new AudioTrack
        { Original = Enumerable.Repeat(.1f, samples.Length).ToArray(), SampleRate = 48000, Channels = 2 } }] };
        var live = new LiveAudioSource(samples, 48000, 2, 0, samples.Length / 2, Background: bed);
        var state = new PlaybackTrackState(Guid.NewGuid(), 48000, 2, 2, samples, samples, LiveOriginal: live, LiveResult: live);
        foreach (bool original in new[] { true, false })
        {
            var grain = PlaybackService.CreateScrubGrain([state], original, .4);
            Require(grain.Length == 7680 && Math.Abs(grain[2000] - .3) < .000001 && grain[0] == 0 && grain[^1] == 0,
                "The scrub grain did not include both live lanes with bounded, faded audio");
        }
        timeline.Tracks = [];
        await VerifyScrubWorkerOrderingSmokeAsync();
    }

    private async Task VerifyScrubWorkerOrderingSmokeAsync()
    {
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Scrub worker smoke: " + message); }
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnd = new ManualResetEventSlim();
        int begins = 0, commits = 0;
        bool requested = _playbackRequested;
        var old = new ScrubGesture { Original = true, Target = new(.25, 0) };
        var latest = new ScrubGesture { Original = true, Target = new(1.5, 1), Released = true };
        latest.Cancellation.Cancel();
        var backend = new ScrubAudioBackend(
            (_, _, _) => { Interlocked.Increment(ref begins); log.Enqueue("begin"); began.TrySetResult(); return 17; },
            (_, _, _, _) => true,
            (_, _, resume) =>
            {
                ending.TrySetResult();
                if (!releaseEnd.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The deterministic EndScrub gate was not released.");
                log.Enqueue(resume ? "end/resume" : "end/stop");
            },
            position => { Interlocked.Increment(ref commits); log.Enqueue("seek/" + position.ToString("G", System.Globalization.CultureInfo.InvariantCulture)); });
        Task? olderWork = null, latestWork = null;
        try
        {
            // Hold an older, active EndScrub. The latest gesture is a quick click
            // released before its worker starts, and must seek after that End.
            _scrubGesture = old;
            olderWork = RunScrubGestureAsync(old, backend);
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            old.Released = true; old.Cancellation.Cancel();
            await ending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _scrubGesture = latest;
            latestWork = RunScrubGestureAsync(latest, backend);
            Require(!latestWork.IsCompleted && Volatile.Read(ref commits) == 0,
                "The latest quick click committed before the older transport had finished.");
            releaseEnd.Set();
            await Task.WhenAll(olderWork, latestWork).WaitAsync(TimeSpan.FromSeconds(5));
            Require(log.SequenceEqual(new[] { "begin", "end/resume", "seek/1.5" }) && begins == 1 && commits == 1 && _scrubGesture == null,
                "A quick click reopened hardware, lost its final seek, or was overwritten by an older EndScrub.");

            // An older released worker scheduled after a newer gesture must not
            // acquire a session or clear that newer gesture's ownership.
            var delayed = new ScrubGesture { Original = true, Target = new(.1, 0), Released = true };
            delayed.Cancellation.Cancel();
            var newer = new ScrubGesture { Original = false, Target = new(1.75, 0) };
            _scrubGesture = newer;
            try
            {
                await RunScrubGestureAsync(delayed, backend);
                Require(begins == 1 && commits == 1 && ReferenceEquals(_scrubGesture, newer),
                    "An older released worker stole the latest gesture or committed its stale position.");
            }
            finally { newer.Cancellation.Dispose(); _scrubGesture = null; }

            var cancelled = new ScrubGesture { Original = true, Released = true, Cancelled = true, Target = new(.75, 0) };
            cancelled.Cancellation.Cancel(); _scrubGesture = cancelled;
            await RunScrubGestureAsync(cancelled, backend);
            Require(begins == 1 && commits == 1 && _scrubGesture == null,
                "An explicitly cancelled quick click opened hardware or sought after cancellation.");

            // Exercise the real release handler as well: normal release cancels
            // queued work without requesting a transport stop.
            var released = new ScrubGesture { Original = true };
            var token = released.Cancellation.Token; _scrubGesture = released;
            EndPlayheadScrubbing(.6);
            Require(token.IsCancellationRequested && released.Released && !released.Cancelled && _scrubGesture == null,
                "Normal release did not cancel a delayed BeginScrub while retaining ordinary seek/resume intent.");
        }
        finally
        {
            releaseEnd.Set();
            if (olderWork != null) await olderWork;
            if (latestWork != null) await latestWork;
            _scrubGesture = null; _playbackRequested = requested;
            UpdatePlayButton();
        }
    }

}

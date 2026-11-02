using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private enum SilencePlacement { Start, NearestBoundary, AllBoundaries, End }
    private double _silenceDurationSeconds = 2;
    private SilencePlacement _silencePlacement = SilencePlacement.NearestBoundary;

    private void ResetInsertSilenceOptions()
    {
        _silenceDurationSeconds = 2;
        _silencePlacement = SilencePlacement.NearestBoundary;
    }

    private void InsertSilenceClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _closingProject || _importing || _exporting || sender is not FrameworkElement anchor) return;
        ToggleSettingsFlyout("silence", anchor, CreateInsertSilenceContent);
    }

    private FrameworkElement CreateInsertSilenceContent(Action close)
    {
        var panel = new StackPanel { Width = 390, Margin = new Thickness(16) };
        NameScope.SetNameScope(panel, new NameScope());
        var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        SilencePlacement selected = _tracks.Count == 0 ? SilencePlacement.Start : _silencePlacement;
        if (selected == SilencePlacement.AllBoundaries && _tracks.Count < 2) selected = SilencePlacement.NearestBoundary;
        foreach (var (placement, label, name) in new[]
        {
            (SilencePlacement.Start, "Start", "SilenceStartRadio"),
            (SilencePlacement.NearestBoundary, "Nearest boundary", "SilenceNearestRadio"),
            (SilencePlacement.AllBoundaries, "All boundaries", "SilenceAllRadio"),
            (SilencePlacement.End, "End", "SilenceEndRadio")
        })
        {
            var choice = new RadioButton
            {
                Name = name, Content = label, GroupName = "InsertSilencePlacement", Margin = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4), Style = (Style)FindResource("ViewSegment"),
                Tag = placement == SilencePlacement.Start ? "First" : placement == SilencePlacement.End ? "Last" : null,
                IsChecked = placement == selected,
                IsEnabled = placement != SilencePlacement.AllBoundaries || _tracks.Count >= 2
            };
            if (placement == SilencePlacement.NearestBoundary && _tracks.Count == 0) choice.IsEnabled = false;
            choice.Checked += (_, _) => selected = placement;
            panel.RegisterName(name, choice);
            if (group.Children.Count > 0)
            {
                var separator = new Border { Width = 1 };
                separator.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush"); group.Children.Add(separator);
            }
            group.Children.Add(choice);
        }
        panel.Children.Add(group);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = "Silence duration · s", VerticalAlignment = VerticalAlignment.Center });
        var duration = new TextBox
        {
            Name = "SilenceDurationBox", Text = _silenceDurationSeconds.ToString("0.###", CultureInfo.CurrentCulture),
            Width = 64, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        panel.RegisterName(duration.Name, duration); Grid.SetColumn(duration, 1); row.Children.Add(duration); panel.Children.Add(row);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "MissingTextBrush"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 78, Margin = new Thickness(0, 0, 6, 0) };
        var insert = new Button { Name = "InsertSilenceApplyButton", Content = "Insert", MinWidth = 78, IsDefault = true, Margin = new Thickness(0) };
        panel.RegisterName(insert.Name, insert); cancel.Click += (_, _) => close();
        insert.Click += async (_, _) =>
        {
            if (!double.TryParse(duration.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds is < GeneratedSilenceSource.MinimumSeconds or > GeneratedSilenceSource.MaximumSeconds)
            {
                error.Text = "Enter a duration from 0.001 to 600 seconds."; error.Visibility = Visibility.Visible;
                duration.Focus(); duration.SelectAll(); return;
            }
            _silenceDurationSeconds = seconds; _silencePlacement = selected;
            close();
            try { await InsertSilenceAsync(selected, seconds); }
            catch (Exception exception) { if (!_closed) ShowError("Could not insert silence", exception); }
        };
        actions.Children.Add(cancel); actions.Children.Add(insert); panel.Children.Add(actions);
        return panel;
    }

    private static int[] SilenceInsertionIndices(IReadOnlyList<AudioTrack> tracks, SilencePlacement placement, double position)
    {
        if (placement == SilencePlacement.Start) return [0];
        if (placement == SilencePlacement.End) return [tracks.Count];
        if (placement == SilencePlacement.AllBoundaries) return Enumerable.Range(1, Math.Max(0, tracks.Count - 1)).ToArray();
        if (tracks.Count == 0) return [0];
        if (tracks.Count == 1) return [position <= tracks[0].Duration * .5 ? 0 : 1];
        double offset = 0, nearestDistance = double.PositiveInfinity;
        int nearest = 1;
        for (int index = 1; index < tracks.Count; index++)
        {
            offset += tracks[index - 1].Duration;
            double distance = Math.Abs(offset - position);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = index; }
        }
        return [nearest];
    }

    private static List<EnvelopePoint> InsertSilenceInCurve(IReadOnlyList<EnvelopePoint> curve,
        IReadOnlyList<double> boundaries, double seconds)
    {
        if (curve.Count == 0) return [];
        double Shift(double time) => time + boundaries.Count(boundary => boundary <= time) * seconds;
        var result = curve.Select(point => point with { Time = Shift(point.Time) }).ToList();
        double preceding = 0;
        foreach (double boundary in boundaries)
        {
            double gain = AudioProcessor.EnvelopeGainAt(curve, boundary);
            result.Add(new(boundary + preceding, gain));
            result.Add(new(boundary + preceding + seconds, gain));
            preceding += seconds;
        }
        return result.OrderBy(point => point.Time).GroupBy(point => point.Time).Select(group => group.Last()).ToList();
    }

    private async Task<bool> InsertSilenceAsync(SilencePlacement placement, double seconds, Task? beforeCommit = null)
    {
        if (_closed || _closingProject || _importing || _exporting) return false;
        if (!double.IsFinite(seconds) || seconds is < GeneratedSilenceSource.MinimumSeconds or > GeneratedSilenceSource.MaximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Silence duration must be between 0.001 and 600 seconds.");
        FinishStrengthGesture(false);
        var previous = _tracks.ToArray();
        var captured = CaptureRollbackPlaybackIntent();
        SlicePlaybackIntent? listening = captured == null ? null : new(captured.PositionSeconds, !captured.Paused,
            captured.Paused, captured.Original, _importNavigationVersion);
        double initialPosition = captured?.PositionSeconds ?? Timeline.Playhead;
        var indices = SilenceInsertionIndices(previous, placement, initialPosition);
        if (indices.Length == 0) return false;
        var cts = new CancellationTokenSource(); var ct = cts.Token;
        _importCts = cts; _importing = true; _atomicSourceOperation = true;
        bool gateHeld = false, committed = false;
        try
        {
            CancelSourceWorkForReplacement(ref listening);
            ExportButton.IsEnabled = false; SetBusy("Preparing silence…", true);
            await _renderGate.WaitAsync(ct); gateHeld = true;
            var silence = await Task.Run(() => GeneratedSilenceSource.Create(seconds, ct: ct), ct);
            var additions = indices.Select(_ => GeneratedSilenceSource.Create(silence.Duration, ct: ct)).ToArray();
            if (EstimateWorkingAudioBytes(previous.Concat(additions), _backgroundLayer.Clips.Select(clip => clip.Source),
                !ModulesBypassed(_settings)) > 12L * 1024 * 1024 * 1024)
                throw new InvalidOperationException("Inserting silence would exceed the 12 GB working audio budget.");
            if (beforeCommit != null) await beforeCommit.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (_closed || _closingProject || !_tracks.SequenceEqual(previous)) return false;
            var latestListening = CaptureRollbackPlaybackIntent();
            if (latestListening != null)
                listening = new(latestListening.PositionSeconds, !latestListening.Paused, latestListening.Paused,
                    latestListening.Original, _importNavigationVersion);
            else if (listening?.NavigationVersion != _importNavigationVersion) listening = null;
            double position = latestListening?.PositionSeconds ?? (listening?.Position ?? Timeline.Playhead);
            var boundaries = indices.Select(index => previous.Take(index).Sum(track => track.Duration)).ToArray();
            var authored = InsertSilenceInCurve(_projectEnvelope, boundaries, silence.Duration);
            var suggested = InsertSilenceInCurve(_projectSuggestion, boundaries, silence.Duration);
            FinishStrengthGesture(false); PushUndo();
            CancelSourceWorkForReplacement(ref listening);
            for (int index = indices.Length - 1; index >= 0; index--) _tracks.Insert(indices[index], additions[index]);
            RestoreProjectEnvelope(authored, suggested);
            _projectEnvelopeLoaded = _tracks.Any(track => track.Analysis == null);
            double mappedPosition = position + boundaries.Count(boundary => listening == null ? boundary < position : boundary <= position) * silence.Duration;
            if (listening != null) listening = listening with { Position = mappedPosition };
            Timeline.Playhead = mappedPosition; SelectTrack(additions[0]);
            _dirty = true; committed = true;
            RefreshBackground(); RefreshList(); UpdateTime();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        finally
        {
            if (gateHeld) _renderGate.Release();
            _importing = false; _atomicSourceOperation = false;
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            cts.Dispose();
            if (!_closed && !_closingProject)
            {
                if (committed) BeginProcessingTransaction();
                QueueRender(); RearmSlicePlaybackAfterPreparation(listening);
                RefreshBackground(); RefreshList(); UpdateTime();
            }
        }
    }
}

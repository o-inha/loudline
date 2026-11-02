using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using Multiloud.Core;

namespace Multiloud.App;

/// <summary>Persistent row data; recycled list containers update only changed labels.</summary>
public sealed class TrackListEntry(AudioTrack track) : INotifyPropertyChanged
{
    public AudioTrack Track { get; } = track;
    private string heading = "", format = "", loudness = "", status = "";
    private bool processing, missing;
    private Visibility missingVisibility = Visibility.Collapsed;
    private Visibility loudnessVisibility = Visibility.Collapsed, statusVisibility = Visibility.Collapsed;
    public string Heading { get => heading; private set => Set(ref heading, value); }
    public string Format { get => format; private set => Set(ref format, value); }
    public string Loudness { get => loudness; private set => Set(ref loudness, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Processing { get => processing; private set => Set(ref processing, value); }
    public bool Missing { get => missing; private set => Set(ref missing, value); }
    public Visibility MissingVisibility { get => missingVisibility; private set => Set(ref missingVisibility, value); }
    public Visibility LoudnessVisibility { get => loudnessVisibility; private set => Set(ref loudnessVisibility, value); }
    public Visibility StatusVisibility { get => statusVisibility; private set => Set(ref statusVisibility, value); }

    public void Update(int index)
    {
        static string Level(double value) => double.IsFinite(value) && value > -100 ? value.ToString("0.0", CultureInfo.InvariantCulture) : "−∞";
        Heading = $"{index + 1:00}   {Track.Name}";
        Format = $"{TimeSpan.FromSeconds(Track.Duration):mm\\:ss}  ·  {Track.SampleRate / 1000.0:0.#} kHz  ·  {(Track.Channels == 2 ? "Stereo" : "Mono")}";
        LoudnessVisibility = Track.Analysis == null ? Visibility.Collapsed : Visibility.Visible;
        Loudness = Track.Analysis == null ? "" : Track.Render == null || !double.IsFinite(Track.Render.OutputLufs)
            ? $"{Level(Track.Analysis.IntegratedLufs)} LUFS"
            : $"{Level(Track.Analysis.IntegratedLufs)} → {Level(Track.Render.OutputLufs)} LUFS";
        Processing = Track.WorkState is TrackWorkState.Analyzing or TrackWorkState.Processing;
        Missing = Track.WorkState == TrackWorkState.Missing;
        MissingVisibility = Missing ? Visibility.Visible : Visibility.Collapsed;
        StatusVisibility = Track.WorkState is TrackWorkState.Analyzing or TrackWorkState.Processing or TrackWorkState.Queued ? Visibility.Collapsed : Visibility.Visible;
        Status = Track.Status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

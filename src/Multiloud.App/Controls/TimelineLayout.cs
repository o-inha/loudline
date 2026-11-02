using System.Windows;

namespace Multiloud.App.Controls;

/// <summary>Shared signal geometry for waveform tiles, edit curves and both dB axes.</summary>
internal static class TimelineLayout
{
    internal const double FilenameHeight = 22;

    internal static Rect SignalArea(Rect plot)
        => new(plot.Left, plot.Top + FilenameHeight, plot.Width, Math.Max(1, plot.Height - FilenameHeight));

    internal static double CenterY(Rect signal) => signal.Top + signal.Height * .5;
    internal static double WaveformAmplitude(Rect signal) => signal.Height * .48;
    internal static double GainAmplitude(Rect signal) => signal.Height * .44;
    internal static double WaveformY(Rect signal, double amplitude)
        => CenterY(signal) - amplitude * WaveformAmplitude(signal);
    internal static double GainY(Rect signal, double gain, double range)
        => CenterY(signal) - gain / range * GainAmplitude(signal);
    internal static double GainAtY(Rect signal, double y, double range)
        => (CenterY(signal) - y) / GainAmplitude(signal) * range;
}

using System.Globalization;
using Multiloud.App.Controls;

namespace Multiloud.App;

public partial class MainWindow
{
    private void UpdateCursorPosition(TimelineCursorInfo? cursor)
    {
        if (cursor is not { } value)
        {
            CursorPositionText.Text = "";
            return;
        }
        var milliseconds = (long)Math.Round(Math.Max(0, value.TimeSeconds) * 1000);
        string time = $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}";
        string amplitude = double.IsNegativeInfinity(value.AmplitudeDbfs) ? "−∞" : value.AmplitudeDbfs.ToString("0.0", CultureInfo.InvariantCulture);
        string envelope = value.EnvelopeGainDb.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture);
        CursorPositionText.Text = $"{time}  ·  {amplitude} dBFS\nEnvelope {envelope} dB";
    }
}

namespace Multiloud.App.Services;

internal readonly record struct HorizontalWheelMessage(int Delta, int ScreenX, int ScreenY);

internal static class NativeWheelInput
{
    public const int HorizontalWheel = 0x020E;
    public static bool TryDecode(int message, nint wParam, nint lParam, out HorizontalWheelMessage wheel)
    {
        wheel = default;
        if (message != HorizontalWheel) return false;
        int delta = unchecked((short)(wParam.ToInt64() >> 16));
        if (delta == 0) return false;
        wheel = new(delta, unchecked((short)lParam.ToInt64()), unchecked((short)(lParam.ToInt64() >> 16)));
        return true;
    }
}

/// <summary>Preserves tiny deltas while ScrollViewer's queued offsets await layout.</summary>
internal sealed class HorizontalScrollPlan
{
    private bool initialized;
    private double planned, previousMaximum;
    private long lastInput;

    public double Apply(double actual, double maximum, double viewport, double delta,
        uint characters, double fontSize, long timestamp, bool reset = false)
    {
        maximum = double.IsFinite(maximum) ? Math.Max(0, maximum) : 0;
        actual = double.IsFinite(actual) ? Math.Clamp(actual, 0, maximum) : 0;
        if (!initialized || reset || timestamp - lastInput > 250 || timestamp < lastInput || maximum != previousMaximum || Math.Abs(actual - planned) < .000001)
            planned = actual;
        initialized = true; previousMaximum = maximum; lastInput = timestamp;
        if (!double.IsFinite(delta) || characters == 0) return planned;
        double characterWidth = double.IsFinite(fontSize) && fontSize > 0 ? fontSize * .6 : 7.2;
        double step = characters == uint.MaxValue ? Math.Max(1, viewport) : characterWidth * Math.Min(characters, 100u);
        planned = Math.Clamp(planned + delta / 120.0 * step, 0, maximum);
        return planned;
    }
}

using Multiloud.App.Services;

internal static class HorizontalScrollTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Native horizontal wheel preserves signed direction and multimonitor coordinates", Decode),
        ("Precision horizontal deltas accumulate before queued layout completes", Precision),
        ("Horizontal plans survive intermediate offsets and rebase after other scrolling", PendingOffsets),
        ("Horizontal scrolling respects boundaries and Windows scroll preferences", Boundaries)
    ];

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < 1e-8, $"Expected {expected}, got {actual}");
    private static nint WParam(int delta) => (nint)((uint)(ushort)(short)delta << 16 | 4);
    private static nint Position(int x, int y) => (nint)((uint)(ushort)(short)x | (uint)(ushort)(short)y << 16);

    private static void Decode()
    {
        foreach (var delta in new[] { -120, -1, 1, 40, 120 })
        {
            Require(NativeWheelInput.TryDecode(0x20E, WParam(delta), Position(-2200, -600), out var wheel), "Horizontal message was ignored");
            Require(wheel.Delta == delta && wheel.ScreenX == -2200 && wheel.ScreenY == -600, "Signed horizontal input was lost");
        }
        Require(!NativeWheelInput.TryDecode(0x20A, WParam(120), Position(5, 6), out _), "Vertical input was interpreted horizontally");
        Require(!NativeWheelInput.TryDecode(0x20E, WParam(0), Position(5, 6), out _), "Zero delta produced a gesture");
    }

    private static void Precision()
    {
        var plan = new HorizontalScrollPlan();
        double target = 0;
        for (int i = 0; i < 120; i++) target = plan.Apply(0, 1000, 200, 1, 3, 12, i);
        Near(target, 21.6);
        for (int i = 0; i < 120; i++) target = plan.Apply(0, 1000, 200, -1, 3, 12, 120 + i);
        Near(target, 0);
    }

    private static void PendingOffsets()
    {
        var plan = new HorizontalScrollPlan();
        Near(plan.Apply(0, 1000, 200, 80, 3, 12, 0), 14.4);
        Near(plan.Apply(7.2, 1000, 200, 40, 3, 12, 10), 21.6);
        Near(plan.Apply(21.6, 1000, 200, 120, 3, 12, 20), 43.2);
        Near(plan.Apply(50, 1000, 200, 1, 3, 12, 500), 50.18);
        Near(plan.Apply(500, 1000, 200, 1, 3, 12, 510, true), 500.18);
    }

    private static void Boundaries()
    {
        var plan = new HorizontalScrollPlan();
        Near(plan.Apply(95, 100, 30, 120, 3, 12, 0), 100);
        Near(plan.Apply(100, 100, 30, -1200, 3, 12, 1), 0);
        Near(new HorizontalScrollPlan().Apply(20, 200, 60, 120, uint.MaxValue, 12, 0), 80);
        Near(new HorizontalScrollPlan().Apply(20, 200, 60, 120, 0, 12, 0), 20);
        Near(new HorizontalScrollPlan().Apply(20, 200, 60, double.NaN, 3, 12, 0), 20);
        Near(new HorizontalScrollPlan().Apply(20, 0, 60, 120, 3, 12, 0), 0);
    }
}

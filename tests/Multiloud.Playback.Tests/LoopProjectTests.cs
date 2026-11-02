using System.Text.Json;
using Multiloud.App.Services;
using Multiloud.Core;

internal static class LoopProjectTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Projects retain both audition ranges including a hidden loop", Roundtrip),
        ("Earlier project formats migrate without inventing audition loops", Legacy),
        ("Projects reject invalid remembered loop ranges even when switched off", Invalid),
    ];

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Roundtrip()
    {
        var loops = new PlaybackLoopState(true, new(12.125, 24.75), false, new(3570.025, 3599.99));
        var project = new ProjectData { Loops = loops };
        var restored = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project));
        Require(restored.Version == 9 && restored.Loops == loops,
            "Saving discarded a disabled loop, its precision or the independent enable states.");
        var hidden = restored.Loops with { AEnabled = false, BEnabled = true };
        var again = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new ProjectData { Loops = hidden }));
        Require(again.Loops == hidden && again.Loops.A == loops.A && again.Loops.B == loops.B,
            "Changing the visible loop lost the previously authored range.");
    }

    private static void Legacy()
    {
        for (int version = 1; version <= 8; version++)
        {
            var restored = ProjectFileService.DeserializeProject($"{{\"Version\":{version}}}");
            Require(restored.Version == 9 && restored.Loops == PlaybackLoopState.Empty,
                "A previous project format acquired an enabled or fabricated loop.");
        }
        Require(ProjectFileService.DeserializeProject("{\"Version\":9}").Loops == PlaybackLoopState.Empty,
            "An omitted optional loop state did not retain the empty default.");
    }

    private static void Invalid()
    {
        foreach (string state in new[]
        {
            "null", "{\"A\":{\"StartSeconds\":-1,\"EndSeconds\":2}}",
            "{\"AEnabled\":false,\"A\":{\"StartSeconds\":2,\"EndSeconds\":1}}",
            "{\"B\":{\"StartSeconds\":2,\"EndSeconds\":2}}",
            "{\"B\":{\"StartSeconds\":0,\"EndSeconds\":2147483648}}",
            "{\"A\":{\"StartSeconds\":0,\"EndSeconds\":1e400}}"
        })
        {
            bool rejected = false;
            try { ProjectFileService.DeserializeProject("{\"Version\":9,\"Loops\":" + state + "}"); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            Require(rejected, "A malformed saved loop range was accepted.");
        }
    }
}

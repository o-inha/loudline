using System.Globalization;
using Multiloud.App.Services;
using Multiloud.Core;

internal static class TrackOrderingTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Imported numeric filenames use natural track order", NumericNames),
        ("Numbers embedded in filenames compare naturally without overflow", MixedNamesAndLargeNumbers),
        ("Equal filenames and leading-zero numbers preserve input order", StableTies),
        ("Filename prefixes and case use culture-independent ordering", PrefixAndCase),
        ("Moving tracks forward and backward uses original insertion boundaries", ForwardAndBackward),
        ("Adjacent and end-position drops are harmless no-ops", NoOpMoves),
        ("Reordering preserves every track and its measured/rendered buffers", PreserveTrackData),
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void SameOrder(IEnumerable<string> actual, params string[] expected)
    {
        var values = actual.ToArray();
        Require(values.SequenceEqual(expected, StringComparer.Ordinal),
            $"Wrong file order. Expected [{string.Join(", ", expected)}], got [{string.Join(", ", values)}]");
    }
    private static AudioTrack Track(string name) => new()
    {
        FilePath = name,
        Original = [.1f, -.1f, .2f, -.2f],
        Envelope = [new(0, -2), new(1, 2)],
        Analysis = new AnalysisResult { IntegratedLufs = -18 },
        Render = new RenderResult { Samples = [.2f, -.2f, .3f, -.3f], OutputLufs = -14 },
        OriginalPeaks = [new(-.2f, .2f)], PreviewPeaks = [new(-.3f, .3f)],
    };
    private static void TrackOrder(IList<AudioTrack> actual, params AudioTrack[] expected)
    {
        Require(actual.Count == expected.Length && actual.Zip(expected).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "Track insertion produced the wrong object order or replaced an existing track");
    }

    private static void NumericNames()
    {
        string[] input = ["012.wav", "002.wav", "k.wav", "010.wav", "a.wav", "001.wav", "c.wav"];
        var original = input.ToArray();
        SameOrder(TrackOrdering.SortPaths(input), "001.wav", "002.wav", "010.wav", "012.wav", "a.wav", "c.wav", "k.wav");
        Require(input.SequenceEqual(original), "Sorting modified the caller's file-selection array");
    }

    private static void MixedNamesAndLargeNumbers()
    {
        SameOrder(TrackOrdering.SortPaths(["part10_take1.wav", "part2_take12.wav", "part1_take9.wav", "part2_take3.wav"]),
            "part1_take9.wav", "part2_take3.wav", "part2_take12.wav", "part10_take1.wav");
        SameOrder(TrackOrdering.SortPaths(["track1000000000000000000000000.wav", "track999999999999999999999999.wav", "track9.wav"]),
            "track9.wav", "track999999999999999999999999.wav", "track1000000000000000000000000.wav");
    }

    private static void StableTies()
    {
        // Directory names and case must not reorder tracks whose filenames tie.
        string[] paths = ["/z/takeA.wav", "/b/TAKEA.WAV", "/a/Takea.wav", "/b/TAKEA.WAV"];
        SameOrder(TrackOrdering.SortPaths(paths), paths);
        SameOrder(TrackOrdering.SortPaths(["track001.wav", "track1.wav", "track0001.wav", "track2.wav"]),
            "track001.wav", "track1.wav", "track0001.wav", "track2.wav");
        SameOrder(TrackOrdering.SortPaths(["track000.wav", "track0.wav", "track00.wav"]),
            "track000.wav", "track0.wav", "track00.wav");
    }

    private static void PrefixAndCase()
    {
        SameOrder(TrackOrdering.SortPaths(["itemA10", "item2", "item", "itemA2", "item10", "itemA", "item1"]),
            "item", "item1", "item2", "item10", "itemA", "itemA2", "itemA10");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            SameOrder(TrackOrdering.SortPaths(["Ä.wav", "z.wav", "A.wav"]), "A.wav", "z.wav", "Ä.wav");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private static void ForwardAndBackward()
    {
        var a = Track("A"); var b = Track("B"); var c = Track("C"); var d = Track("D");
        IList<AudioTrack> tracks = new List<AudioTrack> { a, b, c, d };
        Require(TrackOrdering.Move(tracks, a, 3), "Forward move should report a change");
        TrackOrder(tracks, b, c, a, d); // Insertion is before original index 3 (D).
        Require(TrackOrdering.Move(tracks, d, 0), "Backward move should report a change");
        TrackOrder(tracks, d, b, c, a);
        Require(TrackOrdering.Move(tracks, d, tracks.Count), "Move to end should report a change");
        TrackOrder(tracks, b, c, a, d);
    }

    private static void NoOpMoves()
    {
        var a = Track("A"); var b = Track("B"); var c = Track("C");
        IList<AudioTrack> tracks = new List<AudioTrack> { a, b, c };
        Require(!TrackOrdering.Move(tracks, b, 1), "Dropping before the same track should be a no-op");
        Require(!TrackOrdering.Move(tracks, b, 2), "Dropping immediately after the same track should be a no-op");
        Require(!TrackOrdering.Move(tracks, c, tracks.Count), "Moving the last track to the end should be a no-op");
        Require(!TrackOrdering.Move(tracks, a, 0), "Moving the first track to the start should be a no-op");
        TrackOrder(tracks, a, b, c);
    }

    private static void PreserveTrackData()
    {
        var a = Track("A"); var b = Track("B"); var c = Track("C");
        IList<AudioTrack> tracks = new List<AudioTrack> { a, b, c };
        var snapshots = tracks.Select(track => new
        {
            Track = track, track.Id, track.Original, track.Analysis, track.Render,
            track.Envelope, track.OriginalPeaks, track.PreviewPeaks,
        }).ToArray();
        Require(TrackOrdering.Move(tracks, b, 0), "Reorder did not move the selected track");
        TrackOrder(tracks, b, a, c);
        foreach (var before in snapshots)
        {
            var track = tracks.Single(item => item.Id == before.Id);
            Require(ReferenceEquals(track, before.Track) && ReferenceEquals(track.Original, before.Original) &&
                ReferenceEquals(track.Analysis, before.Analysis) && ReferenceEquals(track.Render, before.Render) &&
                ReferenceEquals(track.Envelope, before.Envelope) && ReferenceEquals(track.OriginalPeaks, before.OriginalPeaks) &&
                ReferenceEquals(track.PreviewPeaks, before.PreviewPeaks),
                "Reordering discarded analysis, render, or editable waveform data instead of moving the existing track");
        }
    }
}

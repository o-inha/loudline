using Multiloud.Core;

internal static class ProjectEnvelopeChecks
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Project envelope crops preserve exact cross-file interpolation", Crop),
        ("Project envelope on-line UI knots leave all render shapes unchanged", Insert),
        ("Project envelope section detector sees sustained changes across file boundaries", AcrossJoin),
        ("Project envelope detector is independent of music file partitioning", Partition),
        ("Legacy project envelope migration preserves interior authored knots", Legacy),
        ("Project envelope local edit affects only neighboring knot interval across tracks", LocalEdit)
    ];

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} vs {expected}");

    private static void Crop()
    {
        EnvelopePoint[] points = [new(0, 1), new(3, 5), new(17, -2), new(30, 0)];
        var a = ProjectEnvelopeDesigner.Crop(points, 0, 10);
        var b = ProjectEnvelopeDesigner.Crop(points, 10, 20);
        Near(a[^1].GainDb, b[0].GainDb, 1e-12, "File boundary reset gain");
        for (double time = 0; time <= 30; time += .037)
            Near(AudioProcessor.EnvelopeGainAt(time < 10 ? a : b, time < 10 ? time : time - 10),
                AudioProcessor.EnvelopeGainAt(points, time), 1e-10, "Local crop changed curve");
        Require(b[0].Time == 0 && b[^1].Time == 20 && !b.Any(point => point.Time < 0 || point.Time > 20), "Crop has invalid local knots");
    }

    private static void Insert()
    {
        List<EnvelopePoint> points = [new(0, -2), new(30, 6)];
        var before = Enumerable.Range(0, 3).Select(index => EnvelopeMath.Canonicalize(ProjectEnvelopeDesigner.Crop(points, index * 10, 10))).ToArray();
        points.Insert(1, new(13, AudioProcessor.EnvelopeGainAt(points, 13)));
        for (int index = 0; index < 3; index++)
            Require(before[index].SequenceEqual(EnvelopeMath.Canonicalize(ProjectEnvelopeDesigner.Crop(points, index * 10, 10))), "On-line insertion invalidated a track's audio shape");
    }

    private static float[] Music(double seconds = 40)
    {
        const int rate = 8000;
        var samples = new float[(int)(seconds * rate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)((i < samples.Length / 2 ? .3 : .035) * Math.Sin(2 * Math.PI * 700 * i / rate));
        return samples;
    }
    private static AudioTrack Track(float[] samples) => new()
    {
        Original = samples, SampleRate = 8000, Channels = 1,
        Analysis = LoudnessAnalyzer.Analyze(samples, 8000, 1)
    };
    private static readonly ProcessingSettings Settings = new() { TransitionSeconds = 8, MaxLevelingDb = 6, BassSensitivity = .25 };

    private static void AcrossJoin()
    {
        var samples = Music(); int cut = samples.Length / 2;
        var a = Track(samples[..cut]); var b = Track(samples[cut..]);
        var points = ProjectEnvelopeDesigner.Design([a, b], Settings);
        double before = AudioProcessor.EnvelopeGainAt(points, 16);
        double after = AudioProcessor.EnvelopeGainAt(points, 24);
        Require(after - before > 3, "A sustained loud-to-quiet change at the file boundary was invisible to the project detector");
        var left = ProjectEnvelopeDesigner.Crop(points, 0, a.Duration);
        var right = ProjectEnvelopeDesigner.Crop(points, a.Duration, b.Duration);
        Near(left[^1].GainDb, right[0].GainDb, 1e-12, "Generated file edge is discontinuous");
        Require(!points.Any(point => point.Time == a.Duration && points.Any(other => other != point && other.Time == point.Time)), "Join introduced duplicate project points");
    }

    private static void Partition()
    {
        var samples = Music(); var whole = Track(samples);
        var split = new[] { Track(samples[..80000]), Track(samples[80000..160000]), Track(samples[160000..240000]), Track(samples[240000..]) };
        var a = ProjectEnvelopeDesigner.Design([whole], Settings);
        var b = ProjectEnvelopeDesigner.Design(split, Settings);
        for (double time = 2; time < 38; time += .5)
            Near(AudioProcessor.EnvelopeGainAt(a, time), AudioProcessor.EnvelopeGainAt(b, time), .3, "Merely slicing audio changed its project envelope");
    }

    private static void Legacy()
    {
        var a = new AudioTrack { SavedDuration = 12, Envelope = [new(0, 2), new(4, -3), new(12, 1)] };
        var b = new AudioTrack { SavedDuration = 9, Envelope = [new(0, -1), new(3, 4), new(9, 0)] };
        var migrated = ProjectEnvelopeDesigner.FromTracks([a, b]);
        Require(migrated.Contains(new EnvelopePoint(4, -3)) && migrated.Contains(new EnvelopePoint(15, 4)), "Manual interior knots lost during migration");
        for (double time = .1; time < 8.9; time += .1)
            Near(AudioProcessor.EnvelopeGainAt(migrated, 12 + time), AudioProcessor.EnvelopeGainAt(b.Envelope, time), .00002, "Migration changed legacy interior gain");
    }

    private static void LocalEdit()
    {
        EnvelopePoint[] before = [new(0, 0), new(5, 0), new(15, 0), new(30, 0)];
        EnvelopePoint[] after = [new(0, 0), new(5, 4), new(15, 0), new(30, 0)];
        for (int track = 0; track < 3; track++)
        {
            var oldCurve = ProjectEnvelopeDesigner.Crop(before, track * 10, 10);
            var nextCurve = ProjectEnvelopeDesigner.Crop(after, track * 10, 10);
            for (double local = 0; local < 10; local += .031)
            {
                double time = track * 10 + local;
                if (time >= 15) Near(AudioProcessor.EnvelopeGainAt(nextCurve, local), AudioProcessor.EnvelopeGainAt(oldCurve, local), 1e-12, "Local edit changed audio beyond neighboring project point");
                else if (time > 0) Require(AudioProcessor.EnvelopeGainAt(nextCurve, local) > AudioProcessor.EnvelopeGainAt(oldCurve, local), "Cross-file segment did not receive envelope edit");
            }
        }
    }
}

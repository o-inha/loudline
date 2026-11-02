using System.Text.Json;
using Multiloud.App.Services;
using Multiloud.Core;

internal static class ProjectMergedTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Merged-source recipes retain native leaf ranges, manual gains and shared identity across project slices", RecipeRoundtrip),
        ("Merged-source components retain independent relative-first and absolute fallback resolution", ComponentPaths),
        ("Folder relinking repairs missing merged components and updates every affected parent", MissingComponents),
        ("Merged recipes can reconstruct generated silence without a synthetic WAV dependency", GeneratedComponent),
        ("Merged-source recipes reject malformed parts and parent ranges outside the reconstructed source", InvalidRecipes),
    ];

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static ProjectTrack Leaf(string path, double start = 0, double duration = 3) => new()
    {
        Path = path, SourceStartSeconds = start, SourceDurationSeconds = duration, Duration = duration,
        SampleRate = 44100, Channels = 1,
    };

    private static ProjectTrack Recipe(Guid? identity = null) => new()
    {
        Path = "Combined.wav", MergedSourceId = identity ?? Guid.NewGuid(), Duration = 7,
        SourceDurationSeconds = 7, SampleRate = 48000, Channels = 2,
        MergedSources = [new() { Source = Leaf("First.wav", 12, 3), GainDb = 2.5 },
            new() { Source = Leaf("Second.wav", 4, 4), GainDb = -1.75 }],
    };

    private static void RecipeRoundtrip()
    {
        var root = Recipe();
        root.SourceStartSeconds = 1; root.SourceDurationSeconds = 2; root.Duration = 2;
        root.InputGainDb = 1; root.TrimDb = -.5; root.TargetLufs = -18;
        root.Envelope = [new(0, 1), new(2, -1)];
        var sibling = Recipe(root.MergedSourceId);
        sibling.SourceStartSeconds = 3; sibling.SourceDurationSeconds = 4; sibling.Duration = 4;
        var project = new ProjectData
        {
            Tracks = [root, sibling], ProjectEnvelope = [new(0, 1), new(6, -1)],
            ProjectEnvelopeSuggestion = [new(0, 0), new(6, 0)], CustomTrackBoundaries = [2.5],
            Loops = new() { AEnabled = true, A = new(1, 2), BEnabled = true, B = new(4, 5) },
        };
        string json = JsonSerializer.Serialize(project);
        var loaded = ProjectFileService.DeserializeProject(json);
        var first = loaded.Tracks[0]; var second = loaded.Tracks[1];
        Require(first.MergedSourceId == root.MergedSourceId && second.MergedSourceId == root.MergedSourceId &&
            first.SourceStartSeconds == 1 && first.SourceDurationSeconds == 2 && second.SourceStartSeconds == 3 &&
            second.SourceDurationSeconds == 4 && first.InputGainDb == 1 && first.TrimDb == -.5 && first.TargetLufs == -18 &&
            first.Envelope.SequenceEqual(root.Envelope), "A merged parent slice lost its root identity, authored controls or source range.");
        var parts = first.MergedSources!;
        Require(parts.Count == 2 && parts[0].Source.Path == "First.wav" && parts[0].Source.SourceStartSeconds == 12 &&
            parts[0].Source.SourceDurationSeconds == 3 && parts[0].Source.SampleRate == 44100 && parts[0].Source.Channels == 1 &&
            parts[0].GainDb == 2.5 && parts[1].Source.Path == "Second.wav" && parts[1].GainDb == -1.75 &&
            first.SampleRate == 48000 && first.Channels == 2, "Native component ranges, gain compensation or output format changed.");
        Require(loaded.ProjectEnvelope!.SequenceEqual(project.ProjectEnvelope!) &&
            loaded.ProjectEnvelopeSuggestion!.SequenceEqual(project.ProjectEnvelopeSuggestion!) &&
            loaded.CustomTrackBoundaries.SequenceEqual(project.CustomTrackBoundaries) && loaded.Loops == project.Loops &&
            !json.Contains("ResolvedSourcePath", StringComparison.Ordinal),
            "A merged recipe affected the global envelope, custom markers, loops or persisted a transient resolution path.");
        var fractional = Recipe();
        fractional.MergedSources![0].Source.SampleRate = 44100;
        fractional.MergedSources[0].Source.SourceStartSeconds = 0;
        fractional.MergedSources[0].Source.SourceDurationSeconds = 20d / 44100;
        fractional.MergedSources[1].Source.SampleRate = 48000;
        fractional.MergedSources[1].Source.SourceStartSeconds = 0;
        fractional.MergedSources[1].Source.SourceDurationSeconds = 1d / 48000;
        fractional.SourceStartSeconds = 10d / 48000; fractional.SourceDurationSeconds = 13d / 48000;
        fractional.Duration = fractional.SourceDurationSeconds.Value;
        ProjectFileService.ValidateSourceRange(fractional);
    }

    private static void ComponentPaths()
    {
        using var scope = new Fixture();
        string relative = scope.Write("session/First.wav"), staleAbsolute = scope.Write("elsewhere/First.wav");
        string fallback = scope.Write("original/Second.wav");
        var saved = Recipe();
        saved.MergedSources![0].Source.AbsolutePath = staleAbsolute;
        saved.MergedSources[1].Source.AbsolutePath = fallback;
        string authored = JsonSerializer.Serialize(saved);
        var source = ProjectFileService.Resolve(saved, scope.ProjectPath);
        var leaves = ProjectFileService.LeafSources(source).ToArray();
        Require(source.IsResolved && leaves.Length == 2 && leaves[0].ResolvedPath == relative &&
            leaves[1].ResolvedPath == fallback && !File.Exists(source.ResolvedPath),
            "A merged source required a combined WAV or failed per-component relative/absolute resolution.");
        Require(JsonSerializer.Serialize(saved) == authored, "Component resolution rewrote authored relative or absolute paths.");
        string movedProject = Path.Combine(scope.Root, "moved", "Album.loudline");
        Directory.CreateDirectory(Path.GetDirectoryName(movedProject)!);
        string movedFirst = scope.Write("moved/First.wav");
        // Opening a fresh serialized project must not retain the previous operation's transient choices.
        var reopened = JsonSerializer.Deserialize<ProjectTrack>(authored)!;
        var moved = ProjectFileService.Resolve(reopened, movedProject);
        var movedLeaves = ProjectFileService.LeafSources(moved).ToArray();
        Require(moved.IsResolved && movedLeaves[0].ResolvedPath == movedFirst && movedLeaves[1].ResolvedPath == fallback,
            "A moved merged project did not prefer its local components independently of a valid absolute fallback.");
    }

    private static void MissingComponents()
    {
        using var scope = new Fixture();
        var saved = Recipe(); var otherSlice = Recipe(saved.MergedSourceId);
        var first = ProjectFileService.Resolve(saved, scope.ProjectPath);
        var second = ProjectFileService.Resolve(otherSlice, scope.ProjectPath);
        var leaves = new[] { first, second }.SelectMany(ProjectFileService.LeafSources).ToArray();
        Require(first.IsMissing && second.IsMissing && leaves.All(source => source.IsMissing),
            "Missing merged components were classified as available because the virtual parent path exists.");
        var notifications = new HashSet<string?>();
        first.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
        string foundFirst = scope.Write("recovery/disc/First.wav");
        var result = ProjectFileService.SearchFolder(leaves, Path.Combine(scope.Root, "recovery")).GetAwaiter().GetResult();
        Require(result.Matched == 2 && result.Unmatched == 2 && result.Ambiguous == 0 && first.IsMissing && second.IsMissing &&
            leaves.Where(source => source.FileName == "First.wav").All(source => source.ResolvedPath == foundFirst),
            "A partial folder search did not repair repeated leaf rows while retaining missing parent state.");
        string chosenSecond = scope.Write("replacement/Renamed second.wav");
        foreach (var leaf in leaves.Where(source => source.IsMissing)) leaf.Relink(chosenSecond);
        Require(first.IsResolved && second.IsResolved && notifications.Contains(nameof(ProjectSourceLink.IsResolved)) &&
            notifications.Contains(nameof(ProjectSourceLink.ResolvedPath)) &&
            leaves.All(source => ProjectFileService.Resolve(source.Saved, scope.ProjectPath).IsResolved),
            "Explicit renamed-file relocation did not update every parent or survive the decode operation's resolution step.");
        Require(saved.MergedSources![0].Source.Path == "First.wav" && saved.MergedSources[1].Source.Path == "Second.wav",
            "Relinking mutated the authored recipe before the project was saved again.");
    }

    private static void GeneratedComponent()
    {
        using var scope = new Fixture();
        scope.Write("session/First.wav");
        var recipe = Recipe();
        recipe.MergedSources![1].Source = new()
        {
            Path = "Silence.wav", GeneratedSilenceSeconds = 5, SourceStartSeconds = 1,
            SourceDurationSeconds = 4, Duration = 4, SampleRate = 48000, Channels = 2,
        };
        var restored = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new ProjectData { Tracks = [recipe] }));
        var source = ProjectFileService.Resolve(restored.Tracks[0], scope.ProjectPath);
        var synthetic = ProjectFileService.LeafSources(source).Single(leaf => leaf.Saved.GeneratedSilenceSeconds.HasValue);
        Require(source.IsResolved && synthetic.IsResolved && !File.Exists(synthetic.ResolvedPath) &&
            synthetic.Saved.SourceStartSeconds == 1 && synthetic.Saved.SourceDurationSeconds == 4,
            "A merged silent component lost its rooted range or acquired an external audio dependency.");
        var search = ProjectFileService.SearchFolder(ProjectFileService.LeafSources(source), scope.Root).GetAwaiter().GetResult();
        Require(search.Matched == 0 && search.Unmatched == 0 && search.Ambiguous == 0,
            "Folder recovery searched for a generated component that can be reconstructed internally.");
    }

    private static void InvalidRecipes()
    {
        var cases = new List<ProjectTrack>();
        var noId = Recipe(); noId.MergedSourceId = null; cases.Add(noId);
        var empty = Recipe(); empty.MergedSources = []; cases.Add(empty);
        var single = Recipe(); single.MergedSources!.RemoveAt(1); cases.Add(single);
        var nested = Recipe(); nested.MergedSources![0].Source = Recipe(); cases.Add(nested);
        var nullPart = Recipe(); nullPart.MergedSources![0] = null!; cases.Add(nullPart);
        var nullSource = Recipe(); nullSource.MergedSources![0].Source = null!; cases.Add(nullSource);
        var badGain = Recipe(); badGain.MergedSources![0].GainDb = 133; cases.Add(badGain);
        var notFinite = Recipe(); notFinite.MergedSources![0].GainDb = double.NaN; cases.Add(notFinite);
        var outside = Recipe(); outside.SourceStartSeconds = 6; outside.SourceDurationSeconds = 2; cases.Add(outside);
        var atEnd = Recipe(); atEnd.SourceStartSeconds = 7; atEnd.SourceDurationSeconds = null; cases.Add(atEnd);
        var badLeaf = Recipe(); badLeaf.MergedSources![1].Source.SourceDurationSeconds = -1; cases.Add(badLeaf);
        var emptyLeaf = Recipe(); emptyLeaf.MergedSources![0].Source.SourceDurationSeconds = null;
        emptyLeaf.MergedSources[0].Source.Duration = 0; cases.Add(emptyLeaf);
        var tooMany = Recipe(); tooMany.MergedSources = Enumerable.Range(0, 10001).Select(_ => new ProjectMergedPart { Source = Leaf("a.wav") }).ToList(); cases.Add(tooMany);
        foreach (var source in cases)
        {
            bool rejected = false;
            try { ProjectFileService.ValidateSourceRange(source); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A malformed merged recipe or out-of-root parent range was accepted.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Loudline-merged-project-" + Guid.NewGuid().ToString("N"));
        public string ProjectPath => Path.Combine(Root, "session", "Album.loudline");
        public Fixture() => Directory.CreateDirectory(Path.Combine(Root, "session"));
        public string Write(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Source fixture"); return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}

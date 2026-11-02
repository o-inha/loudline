using System.ComponentModel;
using System.Text.Json;
using Multiloud.App.Services;
using Multiloud.Core;

internal static class ProjectFileTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Generated silence round-trips its exact source regions without needing audio files", GeneratedSilenceRoundtrip),
        ("Generated silence rejects malformed durations and missing-source substitution", GeneratedSilenceBounds),
        ("Custom export boundaries and output preferences round-trip independently from music processing", CustomExportRoundtrip),
        ("Formats one through seven migrate to gapless original export without rewriting audio data", CustomExportLegacy),
        ("Custom markers and export preferences reject malformed values and normalize ordering", CustomExportBounds),
        ("Slice ranges and global envelopes round-trip without merging repeated source files", SliceProjectRoundtrip),
        ("Legacy project envelopes retain migration data and whole-source range defaults", SliceLegacy),
        ("Malformed slice ranges and project envelope values are rejected", SliceBounds),
        ("Project sources prefer their relative path over an absolute fallback", RelativePathPrecedence),
        ("Moved projects resolve beside the project and can use an absolute fallback", MovedProjectAndFallback),
        ("Legacy projects retain bass behavior and migrate to an empty background layer", LegacyJson),
        ("Advanced peak-protection preferences round-trip with authored project edits", ProtectionRoundtrip),
        ("Older projects retain their protection behavior while new projects enable bass protection", ProtectionLegacy),
        ("Background project data round-trips layer settings, fixed placement and fades", BackgroundRoundtrip),
        ("Background sources keep relative-first resolution and absolute fallback", BackgroundPaths),
        ("Music and background missing sources can share folder recovery without merging roles", BackgroundMissingFolder),
        ("Background project bounds reject malformed or unsupported placement and fades", BackgroundBounds),
        ("Missing sources retain a canonical expected path without editing saved data", MissingSource),
        ("Manual relinking validates a file and notifies source status changes", ManualRelink),
        ("Folder relinking finds a unique filename recursively ignoring case", RecursiveUnique),
        ("Folder relinking leaves duplicate recursive filenames ambiguous", AmbiguousDuplicates),
        ("An exact root filename takes precedence over nested duplicates", RootFilePriority),
        ("Folder relinking handles repeated rows and preserves resolved sources", RepeatedRowsAndSearch),
        ("Cancelled folder relinking leaves every source unchanged", PreCancelledSearch),
        ("Folder relinking skips symbolic links when the platform supports them", SkipSymbolicLinks),
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SamePath(string? actual, string expected, string message)
        => Require(actual == Path.GetFullPath(expected), $"{message}: expected '{Path.GetFullPath(expected)}', got '{actual}'.");

    private static ProjectFolderSearchResult Search(IEnumerable<ProjectSourceLink> links, string folder,
        CancellationToken cancellationToken = default)
        => ProjectFileService.SearchFolder(links, folder, cancellationToken).GetAwaiter().GetResult();

    private static void Counts(ProjectFolderSearchResult result, int matched, int ambiguous, int unmatched)
        => Require(result.Matched == matched && result.Ambiguous == ambiguous && result.Unmatched == unmatched,
            $"Unexpected folder result: matched={result.Matched}, ambiguous={result.Ambiguous}, unmatched={result.Unmatched}.");

    private static void GeneratedSilenceRoundtrip()
    {
        using var scope = new Fixture();
        string projectPath = Path.Combine(scope.Root, "Album.loudline");
        var generated = new ProjectTrack
        {
            Path = "Silence.wav", GeneratedSilenceSeconds = 1.05, Duration = .4,
            SourceStartSeconds = .65, SourceDurationSeconds = .4, SampleRate = 8000, Channels = 2,
            Envelope = [new(0, 1), new(.4, -1)],
        };
        var project = new ProjectData { Tracks = [generated] };
        var restored = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project));
        var saved = restored.Tracks.Single();
        Require(saved.GeneratedSilenceSeconds == 1.05 && saved.SourceStartSeconds == .65 && saved.SourceDurationSeconds == .4 &&
            saved.Duration == .4 && saved.Envelope.SequenceEqual(generated.Envelope), "Generated silence lost its duration, range or authored controls.");
        var source = ProjectFileService.Resolve(saved, projectPath);
        Require(source.IsResolved && !source.IsMissing && !File.Exists(source.ResolvedPath),
            "A generated source was classified as missing or required a real WAV.");
        var search = ProjectFileService.SearchFolder([source], scope.Root).GetAwaiter().GetResult();
        Require(search.Matched == 0 && search.Ambiguous == 0 && search.Unmatched == 0,
            "Folder recovery treated generated silence as a missing audio file.");
    }

    private static void GeneratedSilenceBounds()
    {
        foreach (double silence in new[] { 0, -.1, .0001, 601, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try { ProjectFileService.ValidateSourceRange(new ProjectTrack { GeneratedSilenceSeconds = silence, Duration = 2 }); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Invalid generated silence duration was accepted.");
        }
        foreach (var source in new[]
        {
            new ProjectTrack { GeneratedSilenceSeconds = 2, SourceStartSeconds = 2 },
            new ProjectTrack { GeneratedSilenceSeconds = 2, SourceStartSeconds = 1.5, SourceDurationSeconds = 1 },
        })
        {
            bool rejected = false;
            try { ProjectFileService.ValidateSourceRange(source); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A generated silence slice outside its root was accepted.");
        }
    }

    private static void CustomExportRoundtrip()
    {
        var project = new ProjectData
        {
            Settings = new ProcessingSettings { TargetLufs = -17, Strength = .42 }, FloatExport = true,
            Tracks = [new ProjectTrack { Path = "one.wav", Duration = 42, SourceStartSeconds = 3.5,
                SourceDurationSeconds = 42, InputGainDb = 2, TargetLufs = -19,
                Envelope = [new(0, 1), new(42, -2)] }],
            CustomTrackBoundaries = [1.25, 12.375, 90],
            ExportOptions = new ProjectExportOptions { AsOneFile = true, UseCustomBoundaries = false, BoundaryFadeMs = 2.5 },
            ProjectEnvelope = [new(0, 1), new(42, -2)]
        };
        var loaded = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project));
        Require(loaded.Version == 9 && loaded.FloatExport && loaded.CustomTrackBoundaries.SequenceEqual(project.CustomTrackBoundaries) &&
            loaded.ExportOptions.AsOneFile && !loaded.ExportOptions.UseCustomBoundaries && loaded.ExportOptions.BoundaryFadeMs == 2.5,
            "Custom export settings or absolute marker times were lost.");
        Require(loaded.Settings == project.Settings && loaded.ProjectEnvelope!.SequenceEqual(project.ProjectEnvelope) &&
            JsonSerializer.Serialize(loaded.Tracks) == JsonSerializer.Serialize(project.Tracks),
            "Export-only data changed processing controls, authored automation or sliced media.");
        var empty = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new ProjectData
        {
            ExportOptions = new ProjectExportOptions { UseCustomBoundaries = true, BoundaryFadeMs = 0 }
        }));
        Require(empty.CustomTrackBoundaries.Count == 0 && empty.ExportOptions.UseCustomBoundaries && !empty.ExportOptions.AsOneFile &&
            empty.ExportOptions.BoundaryFadeMs == 0, "An empty project lost its preferred custom mode or gapless defaults.");
    }

    private static void CustomExportLegacy()
    {
        foreach (int version in Enumerable.Range(1, 7))
        {
            var loaded = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new
            {
                Version = version, FloatExport = true,
                Settings = new { TargetLufs = -16, BassSensitivity = .6 },
                Tracks = new[] { new { Path = "source.wav", Duration = 12,
                    Envelope = new[] { new EnvelopePoint(0, 1), new EnvelopePoint(12, -1) } } },
                // Earlier schemas did not author these fields; stray data must
                // not silently turn a legacy one-file-per-song export into a new layout.
                CustomTrackBoundaries = new[] { 3d, 5d },
                ExportOptions = new { AsOneFile = true, UseCustomBoundaries = false, BoundaryFadeMs = 20 }
            }));
            Require(loaded.Version == 9 && loaded.CustomTrackBoundaries.Count == 0 && !loaded.ExportOptions.AsOneFile &&
                loaded.ExportOptions.UseCustomBoundaries && loaded.ExportOptions.BoundaryFadeMs == 0 && loaded.FloatExport &&
                loaded.Settings.TargetLufs == -16 && loaded.Settings.BassSensitivity == .6 &&
                loaded.Tracks[0].Envelope.SequenceEqual(new[] { new EnvelopePoint(0, 1), new EnvelopePoint(12, -1) }),
                "Legacy migration changed authored processing, float format, source curves or effective original-boundary export.");
        }
    }

    private static void CustomExportBounds()
    {
        var normalized = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new ProjectData
        { CustomTrackBoundaries = [90, 2, 2 + 1e-10, 12, 2] }));
        Require(normalized.CustomTrackBoundaries.SequenceEqual(new[] { 2d, 12d, 90d }),
            "Custom markers did not normalize duplicate and out-of-order positions without clipping out-of-range markers.");
        foreach (string json in new[]
        {
            "{\"Version\":8,\"CustomTrackBoundaries\":null}",
            "{\"Version\":8,\"CustomTrackBoundaries\":[-1]}",
            "{\"Version\":8,\"CustomTrackBoundaries\":[0]}",
            "{\"Version\":8,\"CustomTrackBoundaries\":[2147483648]}",
            "{\"Version\":8,\"ExportOptions\":null}",
            "{\"Version\":8,\"ExportOptions\":{\"BoundaryFadeMs\":-1}}",
            "{\"Version\":8,\"ExportOptions\":{\"BoundaryFadeMs\":1001}}"
        })
        {
            bool rejected = false;
            try { ProjectFileService.DeserializeProject(json); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Malformed custom export data was accepted: " + json);
        }
        bool invalid = false;
        try { ProjectFileService.NormalizeCustomTrackBoundaries(new[] { double.NaN }); }
        catch (InvalidDataException) { invalid = true; }
        Require(invalid, "A non-finite custom marker was accepted.");
        invalid = false;
        try { ProjectFileService.ValidateExportOptions(new ProjectExportOptions { BoundaryFadeMs = double.PositiveInfinity }); }
        catch (InvalidDataException) { invalid = true; }
        Require(invalid, "A non-finite boundary fade was accepted.");
        invalid = false;
        try { ProjectFileService.NormalizeCustomTrackBoundaries(Enumerable.Repeat(1d, 100001).ToArray()); }
        catch (InvalidDataException) { invalid = true; }
        Require(invalid, "An excessive marker collection was accepted.");
    }

    private static void SliceProjectRoundtrip()
    {
        var project = new ProjectData
        {
            Tracks = [
                new ProjectTrack { Path = "same.wav", SourceStartSeconds = 0, SourceDurationSeconds = 2.25, Duration = 2.25 },
                new ProjectTrack { Path = "same.wav", SourceStartSeconds = 2.25, SourceDurationSeconds = 4.75, Duration = 4.75, TargetLufs = -17 }
            ],
            ProjectEnvelope = [new(0, 1), new(1, -2), new(4, 3), new(7, 0)],
            ProjectEnvelopeSuggestion = [new(0, 0), new(7, 0)]
        };
        var loaded = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project));
        Require(loaded.Version == 9 && loaded.Tracks.Count == 2 && loaded.Tracks[1].SourceStartSeconds == 2.25 &&
            loaded.Tracks[1].SourceDurationSeconds == 4.75 && loaded.Tracks[1].TargetLufs == -17 &&
            loaded.ProjectEnvelope!.SequenceEqual(project.ProjectEnvelope) &&
            loaded.ProjectEnvelopeSuggestion!.SequenceEqual(project.ProjectEnvelopeSuggestion),
            "Slice metadata or the independent project envelope was lost during serialization.");
    }

    private static void SliceLegacy()
    {
        foreach (int version in Enumerable.Range(1, 6))
        {
            var loaded = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new
            {
                Version = version,
                Tracks = new[] { new { Path = "source.wav", Envelope = new[] { new EnvelopePoint(0, 1), new EnvelopePoint(5, -1) } } }
            }));
            Require(loaded.Version == 9 && loaded.ProjectEnvelope == null && loaded.ProjectEnvelopeSuggestion == null &&
                loaded.Tracks[0].SourceStartSeconds == 0 && loaded.Tracks[0].SourceDurationSeconds == null &&
                loaded.Tracks[0].Envelope.SequenceEqual(new[] { new EnvelopePoint(0, 1), new EnvelopePoint(5, -1) }),
                "Legacy migration rewrote authored local curves or interpreted a whole source as a slice.");
        }
    }

    private static void SliceBounds()
    {
        foreach (var range in new[] { (-.1, (double?)1), (0d, (double?)0), (0d, (double?)(-1)), (86400d, (double?)1) })
        {
            bool rejected = false;
            try { ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new ProjectData { Tracks =
                [new ProjectTrack { Path = "source.wav", SourceStartSeconds = range.Item1, SourceDurationSeconds = range.Item2 }] })); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "An invalid saved slice range was accepted.");
        }
        foreach (var point in new[] { new EnvelopePoint(-1, 0), new EnvelopePoint(0, 25) })
        {
            bool rejected = false;
            try { ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new ProjectData { ProjectEnvelope = [point] })); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "An invalid global envelope point was accepted.");
        }
    }

    private static void RelativePathPrecedence()
    {
        using var files = new Fixture();
        string relative = Path.Combine("audio", "take.wav");
        string primary = files.File(Path.Combine("session", relative));
        string fallback = files.File(Path.Combine("original", "take.wav"));
        var saved = new ProjectTrack { Path = relative, AbsolutePath = fallback, TrimDb = -1.5 };
        string before = JsonSerializer.Serialize(saved);
        var source = ProjectFileService.Resolve(saved, files.ProjectFile);
        Require(ReferenceEquals(source.Saved, saved), "Resolution replaced the saved track object.");
        SamePath(source.ExpectedPath, primary, "Relative source was not based on the project directory");
        SamePath(source.ResolvedPath, primary, "An absolute fallback overrode an existing relative source");
        Require(source.FileName == "take.wav" && source.IsResolved, "Existing source metadata is incorrect.");
        Require(JsonSerializer.Serialize(saved) == before, "Resolving a source changed persisted project values.");
    }

    private static void MovedProjectAndFallback()
    {
        using var files = new Fixture();
        string original = files.File(Path.Combine("old-location", "audio", "take.wav"));
        string moved = files.File(Path.Combine("session", "audio", "take.wav"));
        var saved = new ProjectTrack { Path = Path.Combine("audio", "take.wav"), AbsolutePath = original };
        SamePath(ProjectFileService.Resolve(saved, files.ProjectFile).ResolvedPath, moved,
            "A moved project did not follow its relative media");
        System.IO.File.Delete(moved);
        var fallback = ProjectFileService.Resolve(saved, files.ProjectFile);
        SamePath(fallback.ExpectedPath, moved, "Fallback changed the primary expected location");
        SamePath(fallback.ResolvedPath, original, "Existing absolute fallback was not used");
        Require(saved.Path == Path.Combine("audio", "take.wav") && saved.AbsolutePath == original,
            "Fallback resolution rewrote the saved source paths.");
    }

    private static void LegacyJson()
    {
        using var files = new Fixture();
        string rooted = files.File(Path.Combine("legacy", "old.wav"));
        foreach (int version in new[] { 1, 2, 3, 4 })
        {
            string json = JsonSerializer.Serialize(new { Version = version, Tracks = new[] { new { Path = rooted } } });
            var project = ProjectFileService.DeserializeProject(json);
            Require(project.Version == 9 && project.Settings.BassSensitivity == 1 && !project.FloatExport,
                "Legacy projects did not migrate to version 8 with Normal bass sensitivity.");
            var saved = project.Tracks.Single();
            Require(saved.AbsolutePath == null && saved.Duration == 0 && saved.SampleRate == 48000 && saved.Channels == 2,
                "Legacy project source metadata did not receive compatible defaults.");
            Require(saved.TrimDb == 0 && saved.InputGainDb == 0 && saved.TargetLufs == null &&
                saved.TransitionEnvelope == null && saved.Envelope.Count == 0,
                "Legacy project editing values did not receive compatible defaults.");
            var source = ProjectFileService.Resolve(saved, files.ProjectFile);
            SamePath(source.ExpectedPath, rooted, "Native rooted legacy path was treated as relative");
            SamePath(source.ResolvedPath, rooted, "Native rooted legacy source did not resolve");
            var withSettings = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new
            {
                Version = version, Settings = new { TargetLufs = -17 },
                Tracks = new[] { new { Path = rooted, Envelope = new[] { new EnvelopePoint(0, 1), new EnvelopePoint(2, -1) } } }
            }));
            Require(withSettings.Settings.BassSensitivity == 1 && withSettings.Settings.TargetLufs == -17 &&
                withSettings.Tracks[0].Envelope.SequenceEqual(new[] { new EnvelopePoint(0, 1), new EnvelopePoint(2, -1) }),
                "Migrating a legacy settings object changed authored envelopes or other settings.");
        }
        var empty = JsonSerializer.Deserialize<ProjectData>("{}")!;
        Require(empty.Version == 9 && empty.Settings.BassSensitivity == .25 && empty.Tracks.Count == 0,
            "New project defaults are not version 8 with reduced bass sensitivity.");
        var explicitLegacy = ProjectFileService.DeserializeProject("{\"Version\":4,\"Settings\":{\"BassSensitivity\":0.4}}");
        Require(explicitLegacy.Settings.BassSensitivity == .4, "An explicitly authored legacy bass preference was overwritten.");
        for (int version = 1; version <= 5; version++)
        {
            var migrated = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new
            {
                Version = version, Settings = new { BassSensitivity = .6 },
                Background = new { Enabled = false, GainDb = 12, Clips = new[] { new { Source = new { Path = rooted, Duration = 2 }, StartSeconds = 1 } } }
            }));
            Require(migrated.Version == 9 && migrated.Settings.BassSensitivity == .6 && migrated.Background.Enabled &&
                migrated.Background.GainDb == 0 && migrated.Background.Clips.Count == 0,
                "A pre-background format retained a stray layer or changed its explicit detector preference.");
        }
        var current = new ProjectData { Settings = new ProcessingSettings { BassSensitivity = .55 },
            Tracks = [new ProjectTrack { Path = rooted, InputGainDb = 2, Envelope = [new(0, 1), new(2, -1)] }] };
        var roundtrip = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(current));
        Require(roundtrip.Version == 9 && roundtrip.Settings.BassSensitivity == .55 &&
            roundtrip.Tracks[0].InputGainDb == 2 && roundtrip.Tracks[0].Envelope.SequenceEqual(current.Tracks[0].Envelope),
            "Version 8 did not preserve its chosen bass preference and authored track settings.");
    }

    private static void BackgroundRoundtrip()
    {
        var project = new ProjectData
        {
            Settings = new ProcessingSettings { BassSensitivity = .55 }, FloatExport = true,
            Tracks = [new ProjectTrack { Path = "music.wav", Duration = 42 }],
            Background = new ProjectBackgroundLayer
            {
                Enabled = false, GainDb = -18.25,
                Clips =
                [
                    new ProjectBackgroundClip { Source = new ProjectTrack { Path = Path.Combine("audio", "rain.wav"), AbsolutePath = Path.GetFullPath("old-rain.wav"),
                        Duration = 8.5, SampleRate = 44100, Channels = 1 }, StartSeconds = -2.25, FadeInSeconds = 3.5, FadeOutSeconds = 2 },
                    new ProjectBackgroundClip { Source = new ProjectTrack { Path = "room.wav", Duration = 30, SampleRate = 48000, Channels = 2 },
                        StartSeconds = 45, FadeInSeconds = 1, FadeOutSeconds = 7 }
                ]
            }
        };
        var loaded = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project));
        Require(loaded.Version == 9 && loaded.FloatExport && loaded.Settings.BassSensitivity == .55 && loaded.Tracks.Count == 1 &&
            loaded.Background.Clips.Count == 2 && !loaded.Background.Enabled && loaded.Background.GainDb == -18.25,
            "Background serialization changed settings, role counts or the layer switch/gain.");
        for (int index = 0; index < project.Background.Clips.Count; index++)
            Require(JsonSerializer.Serialize(loaded.Background.Clips[index]) == JsonSerializer.Serialize(project.Background.Clips[index]),
                "A background clip lost source paths, missing duration/format, fixed start or fade settings.");
    }

    private static void ProtectionRoundtrip()
    {
        foreach (double limit in new[] { 0d, 2.5, 12d })
        {
            var settings = new ProcessingSettings
            {
                BassProtection = .77, DynamicsGuardEnabled = true, MaxSustainedReductionDb = limit,
                MaximizerStyle = MaximizerStyle.Punchy, MaximizerCharacter = .31, TransientPreservation = .82,
                TargetLufs = -17, Strength = .42
            };
            var project = new ProjectData
            {
                Settings = settings,
                Tracks = [new ProjectTrack
                {
                    Path = "album.wav", Duration = 12, SourceStartSeconds = 31, SourceDurationSeconds = 12,
                    InputGainDb = 2, TargetLufs = -18, TrimDb = -.5,
                    Envelope = [new(0, 1), new(12, -1)]
                }],
                ProjectEnvelope = [new(0, 1), new(12, -1)],
                ProjectEnvelopeSuggestion = [new(0, .2), new(12, -.3)],
                CustomTrackBoundaries = [3.25, 7.5],
                Background = new ProjectBackgroundLayer { GainDb = -24, Clips = [new ProjectBackgroundClip
                {
                    Source = new ProjectTrack { Path = "bed.wav", Duration = 90 }, StartSeconds = -2, FadeInSeconds = .5
                }] }
            };
            string saved = JsonSerializer.Serialize(project);
            var loaded = ProjectFileService.DeserializeProject(saved);
            Require(loaded.Settings == settings && JsonSerializer.Serialize(loaded) == saved,
                "Saving advanced peak protection changed its controls, slice ranges, curves, background or export boundaries.");
        }
    }

    private static void ProtectionLegacy()
    {
        var fresh = new ProjectData();
        Require(fresh.Settings.BassProtection == .5 && !fresh.Settings.DynamicsGuardEnabled && fresh.Settings.MaxSustainedReductionDb == 3,
            "New projects lost their intended bass-protection and optional safeguard defaults.");
        foreach (int version in Enumerable.Range(1, 8))
        {
            var legacy = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new
            {
                Version = version,
                Settings = new { TargetLufs = -19, MaximizerStyle = 1, MaximizerCharacter = .23, TransientPreservation = .71 }
            }));
            Require(legacy.Settings.BassProtection == 0 && !legacy.Settings.DynamicsGuardEnabled && legacy.Settings.MaxSustainedReductionDb == 3 &&
                legacy.Settings.TargetLufs == -19 && legacy.Settings.MaximizerStyle == MaximizerStyle.Punchy &&
                legacy.Settings.MaximizerCharacter == .23 && legacy.Settings.TransientPreservation == .71,
                "Opening an older project silently enabled new processing or changed its existing limiter controls.");

            var explicitPreference = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(new
            {
                Version = version,
                Settings = new { BassProtection = .4, DynamicsGuardEnabled = true, MaxSustainedReductionDb = 1.5 }
            }));
            Require(explicitPreference.Settings.BassProtection == .4 && explicitPreference.Settings.DynamicsGuardEnabled &&
                explicitPreference.Settings.MaxSustainedReductionDb == 1.5,
                "Project migration replaced an explicitly saved advanced peak-protection preference.");
        }
        var omittedSettings = ProjectFileService.DeserializeProject("{\"Version\":8}");
        Require(omittedSettings.Settings.BassProtection == 0 && !omittedSettings.Settings.DynamicsGuardEnabled,
            "An older project without a settings object inherited new bass processing.");
    }

    private static void BackgroundPaths()
    {
        using var files = new Fixture();
        string primary = files.File(Path.Combine("session", "audio", "bed.wav"));
        string old = files.File(Path.Combine("old-location", "bed.wav"));
        var project = new ProjectData { Background = new ProjectBackgroundLayer { Clips =
            [new ProjectBackgroundClip { Source = new ProjectTrack { Path = Path.Combine("audio", "bed.wav"), AbsolutePath = old, Duration = 12 }, StartSeconds = 4, FadeOutSeconds = 2 }] } };
        var clip = ProjectFileService.DeserializeProject(JsonSerializer.Serialize(project)).Background.Clips.Single();
        string before = JsonSerializer.Serialize(clip);
        SamePath(ProjectFileService.Resolve(clip.Source, files.ProjectFile).ResolvedPath, primary, "Moved background did not prefer adjacent relative media");
        File.Delete(primary);
        SamePath(ProjectFileService.Resolve(clip.Source, files.ProjectFile).ResolvedPath, old, "Background absolute fallback was not used");
        File.Delete(old);
        var missing = ProjectFileService.Resolve(clip.Source, files.ProjectFile);
        Require(!missing.IsResolved && missing.FileName == "bed.wav" && clip.Source.Duration == 12 && clip.StartSeconds == 4 && clip.FadeOutSeconds == 2,
            "A missing background lost its duration, role or placement/fade settings.");
        SamePath(missing.ExpectedPath, primary, "Missing background expected location changed to the fallback");
        Require(JsonSerializer.Serialize(clip) == before, "Background resolution mutated persisted paths or clip settings.");
    }

    private static void BackgroundMissingFolder()
    {
        using var files = new Fixture();
        string musicPath = files.File(Path.Combine("search", "disc", "music.wav"));
        string backgroundPath = files.File(Path.Combine("search", "ambience", "bed.wav"));
        var project = new ProjectData
        {
            Tracks = [new ProjectTrack { Path = Path.Combine("missing", "music.wav") }],
            Background = new ProjectBackgroundLayer { Clips = [new ProjectBackgroundClip { Source = new ProjectTrack { Path = Path.Combine("missing", "bed.wav"), Duration = 4 }, StartSeconds = 2, FadeInSeconds = 1 }] }
        };
        string before = JsonSerializer.Serialize(project);
        var music = ProjectFileService.Resolve(project.Tracks.Single(), files.ProjectFile);
        var background = ProjectFileService.Resolve(project.Background.Clips.Single().Source, files.ProjectFile);
        Counts(Search([music, background], files.PathFor("search")), 2, 0, 0);
        SamePath(music.ResolvedPath, musicPath, "Mixed recovery assigned the wrong music source");
        SamePath(background.ResolvedPath, backgroundPath, "Mixed recovery assigned the wrong background source");
        Require(project.Tracks.Count == 1 && project.Background.Clips.Count == 1 && JsonSerializer.Serialize(project) == before,
            "Missing-source recovery merged music/background counts or rewrote authored background data.");
    }

    private static void BackgroundBounds()
    {
        static ProjectBackgroundLayer Valid() => new() { Clips = [new ProjectBackgroundClip
            { Source = new ProjectTrack { Path = "bed.wav", Duration = 8 }, StartSeconds = -2, FadeInSeconds = 8, FadeOutSeconds = 8 }] };
        ProjectFileService.ValidateBackground(Valid()); // Overlapping edge fades are valid.
        foreach (double gain in new[] { -60d, 12d }) { var layer = Valid(); layer.GainDb = gain; ProjectFileService.ValidateBackground(layer); }
        foreach (var corrupt in new Action<ProjectBackgroundLayer>[]
        {
            layer => layer.GainDb = 12.01, layer => layer.GainDb = double.NaN,
            layer => layer.Clips = null!, layer => layer.Clips[0] = null!, layer => layer.Clips[0].Source = null!,
            layer => layer.Clips[0].StartSeconds = 86401, layer => layer.Clips[0].StartSeconds = double.NegativeInfinity,
            layer => layer.Clips[0].FadeInSeconds = -.1, layer => layer.Clips[0].FadeOutSeconds = 8.01,
            layer => layer.Clips[0].Source.Duration = -1
        })
        {
            var layer = Valid(); corrupt(layer); bool rejected = false;
            try { ProjectFileService.ValidateBackground(layer); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Malformed background layer/clip data was accepted.");
        }
        bool rejectedNull = false;
        try { ProjectFileService.DeserializeProject("{\"Version\":6,\"Background\":null}"); } catch (InvalidDataException) { rejectedNull = true; }
        Require(rejectedNull, "An explicit null version-6 background layer was accepted.");
    }

    private static void MissingSource()
    {
        using var files = new Fixture();
        string relative = Path.Combine("audio", "..", "absent.wav");
        var saved = new ProjectTrack { Path = relative, AbsolutePath = files.PathFor("also-absent.wav") };
        string before = JsonSerializer.Serialize(saved);
        var source = ProjectFileService.Resolve(saved, files.ProjectFile);
        SamePath(source.ExpectedPath, files.PathFor(Path.Combine("session", "absent.wav")),
            "Missing source did not keep a canonical primary path");
        Require(source.ResolvedPath == null && !source.IsResolved && source.FileName == "absent.wav",
            "Missing source was marked resolved or lost its basename.");
        Require(!string.IsNullOrWhiteSpace(source.StatusText) && !string.IsNullOrWhiteSpace(source.DisplayPath),
            "Missing source has no user-visible status or path.");
        Require(JsonSerializer.Serialize(saved) == before, "Missing-source resolution changed saved values.");
    }

    private static void ManualRelink()
    {
        using var files = new Fixture();
        var saved = new ProjectTrack { Path = Path.Combine("missing", "take.wav"), TrimDb = -3, InputGainDb = 2 };
        var source = ProjectFileService.Resolve(saved, files.ProjectFile);
        string before = JsonSerializer.Serialize(saved);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)source).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        bool rejected = false;
        try { source.Relink(files.PathFor("does-not-exist.wav")); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { rejected = true; }
        Require(rejected && source.ResolvedPath == null && changed.Count == 0,
            "An invalid manual relink changed the source or failed to reject a missing file.");
        string replacement = files.File(Path.Combine("replacement", "chosen.wav"));
        files.Directory("detour");
        source.Relink(Path.Combine(files.Root, "detour", "..", "replacement", "chosen.wav"));
        SamePath(source.ResolvedPath, replacement, "Manual replacement was not canonicalized");
        Require(source.IsResolved, "Manual replacement did not resolve the row.");
        foreach (string property in new[] { "ResolvedPath", "IsResolved", "StatusText", "DisplayPath" })
            Require(changed.Contains(property), $"Manual relink did not notify {property}.");
        Require(ReferenceEquals(source.Saved, saved) && JsonSerializer.Serialize(saved) == before,
            "Manual relinking changed the saved track before project save.");
    }

    private static void RecursiveUnique()
    {
        using var files = new Fixture();
        string candidate = files.File(Path.Combine("search", "disc", "audio", "TAKE.WAV"));
        var source = ProjectFileService.Resolve(new ProjectTrack { Path = Path.Combine("gone", "take.wav") }, files.ProjectFile);
        Counts(Search([source], files.PathFor("search")), 1, 0, 0);
        SamePath(source.ResolvedPath, candidate, "Unique nested filename did not relink ignoring case");
        Require(source.Saved.Path == Path.Combine("gone", "take.wav"), "Folder search rewrote persisted paths.");
    }

    private static void AmbiguousDuplicates()
    {
        using var files = new Fixture();
        files.File(Path.Combine("search", "disc1", "take.wav"));
        files.File(Path.Combine("search", "disc2", "TAKE.WAV"));
        var source = ProjectFileService.Resolve(new ProjectTrack { Path = "take.wav" }, files.ProjectFile);
        var absent = ProjectFileService.Resolve(new ProjectTrack { Path = "absent.wav" }, files.ProjectFile);
        Counts(Search([source, absent], files.PathFor("search")), 0, 1, 1);
        Require(!source.IsResolved && source.ResolvedPath == null && !absent.IsResolved,
            "Folder search selected an arbitrary duplicate or a missing candidate.");
    }

    private static void RootFilePriority()
    {
        using var files = new Fixture();
        string candidate = files.File(Path.Combine("search", "TAKE.WAV"));
        files.File(Path.Combine("search", "disc1", "take.wav"));
        files.File(Path.Combine("search", "disc2", "take.wav"));
        var source = ProjectFileService.Resolve(new ProjectTrack { Path = "take.wav" }, files.ProjectFile);
        Counts(Search([source], files.PathFor("search")), 1, 0, 0);
        SamePath(source.ResolvedPath, candidate, "Root filename did not take priority over recursive duplicates");
    }

    private static void RepeatedRowsAndSearch()
    {
        using var files = new Fixture();
        string existing = files.File(Path.Combine("session", "already.wav"));
        string candidate = files.File(Path.Combine("search", "disc", "repeat.wav"));
        files.File(Path.Combine("search", "already.wav"));
        var first = ProjectFileService.Resolve(new ProjectTrack { Path = Path.Combine("old", "repeat.wav") }, files.ProjectFile);
        var second = ProjectFileService.Resolve(new ProjectTrack { Path = Path.Combine("other", "repeat.wav") }, files.ProjectFile);
        var resolved = ProjectFileService.Resolve(new ProjectTrack { Path = "already.wav" }, files.ProjectFile);
        var missing = ProjectFileService.Resolve(new ProjectTrack { Path = "absent.wav" }, files.ProjectFile);
        Counts(Search([first, second, resolved, missing], files.PathFor("search")), 2, 0, 1);
        SamePath(first.ResolvedPath, candidate, "First repeated row did not find the unique file");
        SamePath(second.ResolvedPath, candidate, "Repeated rows could not share the same physical file");
        SamePath(resolved.ResolvedPath, existing, "Folder search replaced an already resolved source");
        Counts(Search([first, second, resolved, missing], files.PathFor("search")), 0, 0, 1);
        SamePath(resolved.ResolvedPath, existing, "Repeated folder search changed an existing resolution");
    }

    private static void PreCancelledSearch()
    {
        using var files = new Fixture();
        files.File(Path.Combine("search", "take.wav"));
        var source = ProjectFileService.Resolve(new ProjectTrack { Path = "take.wav" }, files.ProjectFile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { Search([source], files.PathFor("search"), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && source.ResolvedPath == null && !source.IsResolved,
            "Pre-cancelled search failed to cancel before applying file matches.");
    }

    private static void SkipSymbolicLinks()
    {
        using var files = new Fixture();
        string outside = files.File(Path.Combine("outside", "linked.wav"));
        string search = files.Directory("search");
        try
        {
            System.IO.Directory.CreateSymbolicLink(Path.Combine(search, "linked-directory"), Path.GetDirectoryName(outside)!);
            System.IO.File.CreateSymbolicLink(Path.Combine(search, "linked.wav"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows can require a privilege unavailable to the test process.
        }
        var source = ProjectFileService.Resolve(new ProjectTrack { Path = "linked.wav" }, files.ProjectFile);
        Counts(Search([source], search), 0, 0, 1);
        Require(!source.IsResolved, "Folder search traversed or selected a symbolic link.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "multiloud-project-tests-" + Guid.NewGuid().ToString("N"));
        public string ProjectFile { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Root);
            ProjectFile = Path.Combine(Directory("session"), "album.multiloud");
        }

        public string PathFor(string relative) => Path.Combine(Root, relative);
        public string Directory(string relative)
        {
            string path = PathFor(relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        public string File(string relative)
        {
            string path = PathFor(relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "source fixture");
            return path;
        }
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}

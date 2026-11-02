using Multiloud.Core;

namespace Multiloud.App;

public partial class MainWindow
{
    private List<EnvelopePoint> _projectEnvelope = [];
    private List<EnvelopePoint> _projectSuggestion = [];
    private sealed record ProjectEnvelopePart(Guid Id, double Duration, AnalysisResult? Analysis);
    private List<ProjectEnvelopePart> _projectEnvelopeLayout = [];
    private ProcessingSettings? _projectEnvelopeSettings;
    private bool _projectEnvelopeLoaded;
    private sealed record ProjectEnvelopeVariant(List<ProjectEnvelopePart> Layout, ProcessingSettings Settings,
        List<EnvelopePoint> Authored, List<EnvelopePoint> Suggestion);
    private readonly List<ProjectEnvelopeVariant> _projectEnvelopeVariants = [];

    private void RememberProjectEnvelopeLayout()
    {
        if (_projectEnvelopeSettings == null || _projectEnvelopeLayout.Count == 0) return;
        _projectEnvelopeVariants.RemoveAll(variant => SameLevelingDesign(variant.Settings, _projectEnvelopeSettings) &&
            variant.Layout.SequenceEqual(_projectEnvelopeLayout));
        _projectEnvelopeVariants.Insert(0, new(_projectEnvelopeLayout.ToList(), _projectEnvelopeSettings,
            _projectEnvelope.ToList(), _projectSuggestion.ToList()));
        if (_projectEnvelopeVariants.Count > 12) _projectEnvelopeVariants.RemoveAt(12);
    }

    private void InitializeProjectEnvelope()
    {
        Timeline.ProjectEnvelope = _projectEnvelope;
        Timeline.ProjectEnvelopeEdited += ProjectEnvelopeEdited;
    }

    private List<ProjectEnvelopePart> CurrentProjectEnvelopeLayout() =>
        _tracks.Select(track => new ProjectEnvelopePart(track.Id, track.Duration, track.Analysis)).ToList();

    private void PreserveProjectEnvelopeStructure() => _projectEnvelopeLayout = CurrentProjectEnvelopeLayout();

    private void LoadProjectEnvelope(IReadOnlyList<EnvelopePoint>? authored, IReadOnlyList<EnvelopePoint>? suggestion)
    {
        _projectEnvelopeVariants.Clear();
        double duration = _tracks.Sum(track => track.Duration);
        _projectEnvelope = authored == null ? ProjectEnvelopeDesigner.FromTracks(_tracks) : ValidateEnvelope(authored, duration);
        _projectSuggestion = suggestion?.ToList() ?? [];
        _projectEnvelopeLayout = CurrentProjectEnvelopeLayout(); _projectEnvelopeSettings = _settings;
        _projectEnvelopeLoaded = _tracks.Any(track => track.Analysis == null);
        PublishProjectEnvelope();
    }

    private void RestoreProjectEnvelope(IReadOnlyList<EnvelopePoint> authored, IReadOnlyList<EnvelopePoint> suggestion, IReadOnlyCollection<Guid>? pendingLoadedIds = null)
    {
        _projectEnvelopeVariants.Clear();
        _projectEnvelope = authored.ToList(); _projectSuggestion = suggestion.ToList();
        _projectEnvelopeLayout = CurrentProjectEnvelopeLayout(); _projectEnvelopeSettings = _settings;
        _projectEnvelopeLoaded = false;
        if (pendingLoadedIds is { Count: > 0 })
        {
            _projectEnvelopeLayout = _projectEnvelopeLayout.Select(part => pendingLoadedIds.Contains(part.Id)
                ? part with { Duration = 0, Analysis = null } : part).ToList();
            SyncProjectEnvelope();
        }
        else PublishProjectEnvelope();
    }

    private void SyncProjectEnvelope(bool preserveShape = false, bool forceSuggestion = false)
    {
        var nextLayout = CurrentProjectEnvelopeLayout();
        bool shapeChanged = nextLayout.Count != _projectEnvelopeLayout.Count || nextLayout.Where((part, index) =>
            index >= _projectEnvelopeLayout.Count || part.Id != _projectEnvelopeLayout[index].Id || part.Duration != _projectEnvelopeLayout[index].Duration).Any();
        bool analysisChanged = shapeChanged || nextLayout.Where((part, index) =>
            index >= _projectEnvelopeLayout.Count || !ReferenceEquals(part.Analysis, _projectEnvelopeLayout[index].Analysis)).Any();
        bool settingsChanged = _projectEnvelopeSettings == null || !SameLevelingDesign(_projectEnvelopeSettings, _settings);
        if (!forceSuggestion && !preserveShape && !analysisChanged && !settingsChanged) return;
        // Structural edits have derived continuity ramps. Keep bounded immutable
        // layouts so a quick move away and back restores the exact authored UI
        // curve and existing DSP keys instead of accumulating edge smoothing.
        if (shapeChanged && !preserveShape && !forceSuggestion && !_projectEnvelopeLoaded && _projectEnvelopeSettings != null)
        {
            RememberProjectEnvelopeLayout();
            var cached = _projectEnvelopeVariants.FirstOrDefault(variant =>
                SameLevelingDesign(variant.Settings, _settings) && variant.Layout.SequenceEqual(nextLayout));
            if (cached != null)
            {
                _projectEnvelope = cached.Authored.ToList(); _projectSuggestion = cached.Suggestion.ToList();
                _projectEnvelopeLayout = nextLayout; _projectEnvelopeSettings = _settings;
                PublishProjectEnvelope(); return;
            }
        }
        if (forceSuggestion) _projectEnvelopeVariants.Clear();
        if (nextLayout.Count == 0)
        {
            _projectEnvelope = []; _projectSuggestion = []; _projectEnvelopeLayout = []; _projectEnvelopeSettings = _settings;
            _projectEnvelopeVariants.Clear();
            _projectEnvelopeLoaded = false; PublishProjectEnvelope(); return;
        }
        bool remappedLoadedCurve = _projectEnvelopeLoaded && shapeChanged && !preserveShape;
        if (remappedLoadedCurve)
        {
            // A relink can change a missing song's duration before every source
            // is available. Later songs' authored knots follow their own content.
            _projectEnvelope = RemapProjectCurve(_projectEnvelope, _projectEnvelopeLayout, nextLayout, _settings.TransitionSeconds);
            _projectSuggestion = RemapProjectCurve(_projectSuggestion, _projectEnvelopeLayout, nextLayout, _settings.TransitionSeconds);
        }
        // Loading a saved authored project must not successively overwrite its
        // curve with suggestions made from only its first decoded songs.
        if (_projectEnvelopeLoaded && !forceSuggestion && !settingsChanged && _tracks.Any(track => track.Analysis == null))
        {
            _projectEnvelopeLayout = nextLayout; PublishProjectEnvelope(); return;
        }
        if (_projectEnvelopeLoaded && settingsChanged && _projectSuggestion.Count == 0 && _projectEnvelopeSettings != null)
            _projectSuggestion = ProjectEnvelopeDesigner.Design(_tracks, _projectEnvelopeSettings);
        var nextSuggestion = ProjectEnvelopeDesigner.Design(_tracks, _settings);
        double duration = nextLayout.Sum(part => part.Duration);
        if (forceSuggestion)
            _projectEnvelope = nextSuggestion.ToList();
        else if (preserveShape || _projectEnvelopeLoaded && !settingsChanged)
        {
            // Slicing only changes matching partitions. It does not rewrite the
            // sound's already-authored project-time volume automation.
            _projectEnvelope = ValidateEnvelope(_projectEnvelope, duration);
        }
        else
        {
            var manual = Difference(_projectEnvelope, _projectSuggestion);
            if (shapeChanged && !remappedLoadedCurve) manual = RemapProjectCurve(manual, _projectEnvelopeLayout, nextLayout, _settings.TransitionSeconds);
            _projectEnvelope = TransitionDesigner.Combine(nextSuggestion, manual)
                .Select(point => point with { GainDb = Math.Clamp(point.GainDb, -24, 24) }).ToList();
            _projectEnvelope = ValidateEnvelope(_projectEnvelope, duration);
        }
        _projectSuggestion = nextSuggestion; _projectEnvelopeLayout = nextLayout; _projectEnvelopeSettings = _settings;
        _projectEnvelopeLoaded = false;
        PublishProjectEnvelope();
    }

    private static List<EnvelopePoint> Difference(IReadOnlyList<EnvelopePoint> first, IReadOnlyList<EnvelopePoint> second)
        => TransitionDesigner.Combine(first, second.Select(point => point with { GainDb = -point.GainDb }).ToArray());

    private static List<EnvelopePoint> RemapProjectCurve(IReadOnlyList<EnvelopePoint> curve,
        IReadOnlyList<ProjectEnvelopePart> before, IReadOnlyList<ProjectEnvelopePart> after, double transitionSeconds)
    {
        var old = new Dictionary<Guid, (double Start, double Duration)>(); double start = 0;
        foreach (var part in before) { old[part.Id] = (start, part.Duration); start += part.Duration; }
        var points = new List<EnvelopePoint>(); double offset = 0;
        foreach (var part in after)
        {
            var local = old.TryGetValue(part.Id, out var source)
                ? ProjectEnvelopeDesigner.Crop(curve, source.Start, Math.Min(source.Duration, part.Duration))
                : new List<EnvelopePoint> { new(0, 0), new(part.Duration, 0) };
            double edge = Math.Min(Math.Clamp(transitionSeconds, 2, 30), part.Duration * .5);
            if (edge > 0)
            {
                // Restrict a new join's continuity correction to its edges even
                // when the next authored knot is minutes away inside the song.
                local.Add(new(edge, AudioProcessor.EnvelopeGainAt(local, edge)));
                local.Sort((a, b) => a.Time.CompareTo(b.Time));
                local.Add(new(part.Duration - edge, AudioProcessor.EnvelopeGainAt(local, part.Duration - edge)));
                local = local.GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
            }
            foreach (var point in local)
            {
                double time = offset + point.Time;
                if (points.Count > 0 && Math.Abs(time - points[^1].Time) < 1e-9)
                {
                    // A newly adjacent pair shares one boundary value. Interior
                    // authored points keep their source-local times and values.
                    points[^1] = points[^1] with { GainDb = (points[^1].GainDb + point.GainDb) * .5 };
                }
                else points.Add(point with { Time = time });
            }
            offset += part.Duration;
            if (points.Count == 0 || points[^1].Time < offset)
                points.Add(new(offset, local.Count == 0 ? 0 : local[^1].GainDb));
        }
        return points;
    }

    private void ResetProjectEnvelopeForTrack(AudioTrack track)
    {
        _projectEnvelopeVariants.Clear();
        if (!_tracks.Contains(track)) return;
        SyncProjectEnvelope();
        double start = _tracks.TakeWhile(item => !ReferenceEquals(item, track)).Sum(item => item.Duration);
        double end = start + track.Duration;
        var suggestion = ProjectEnvelopeDesigner.Design(_tracks, _settings);
        _projectEnvelope = _projectEnvelope.Where(point => point.Time < start || point.Time > end)
            .Concat(ProjectEnvelopeDesigner.Crop(suggestion, start, track.Duration).Select(point => point with { Time = point.Time + start }))
            .GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time).ToList();
        PublishProjectEnvelope();
    }

    private void ProjectEnvelopeEdited()
    {
        _projectEnvelopeVariants.Clear();
        PublishProjectEnvelope();
        _dirty = true;
    }

    private void PublishProjectEnvelope()
    {
        double offset = 0;
        var audioCurve = EnvelopeMath.Canonicalize(_projectEnvelope);
        foreach (var track in _tracks)
        {
            // An undecoded new import has no known timeline span yet. Keep its
            // empty authored cache so history can recognize a pending source.
            if (track.Duration <= 0 && track.Analysis == null && track.Envelope.Count == 0) continue;
            var local = ProjectEnvelopeDesigner.Crop(audioCurve, offset, track.Duration, canonicalize: false);
            if (!track.Envelope.SequenceEqual(local)) track.Envelope = local;
            if (track.TransitionEnvelope.Count != 0) track.TransitionEnvelope = [];
            offset += track.Duration;
        }
        Timeline.ProjectEnvelope = _projectEnvelope;
    }
}

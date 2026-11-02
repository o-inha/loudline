using System.IO;
using Multiloud.Core;

namespace Multiloud.App.Services;

/// <summary>One album order shared by the file list, timeline, playback and export.</summary>
public static class TrackOrdering
{
    public static IEnumerable<string> SortPaths(IEnumerable<string> paths)
        => paths.OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance);

    /// <summary>Insertion is a boundary in the original list, before removal.</summary>
    public static bool Move(IList<AudioTrack> tracks, AudioTrack track, int insertionIndex)
    {
        int from = tracks.IndexOf(track);
        if (from < 0) return false;
        int to = Math.Clamp(insertionIndex, 0, tracks.Count);
        if (to > from) to--;
        if (to == from) return false;
        tracks.RemoveAt(from); tracks.Insert(to, track);
        return true;
    }

    private sealed class NaturalFileNameComparer : IComparer<string?>
    {
        public static readonly NaturalFileNameComparer Instance = new();
        public int Compare(string? left, string? right)
        {
            left ??= ""; right ??= "";
            int a = 0, b = 0;
            while (a < left.Length && b < right.Length)
            {
                if (IsDigit(left[a]) && IsDigit(right[b]))
                {
                    int endA = a, endB = b;
                    while (endA < left.Length && IsDigit(left[endA])) endA++;
                    while (endB < right.Length && IsDigit(right[endB])) endB++;
                    while (a < endA && left[a] == '0') a++;
                    while (b < endB && right[b] == '0') b++;
                    int order = (endA - a).CompareTo(endB - b);
                    if (order != 0) return order;
                    order = string.Compare(left, a, right, b, endA - a, StringComparison.Ordinal);
                    if (order != 0) return order;
                    a = endA; b = endB;
                }
                else
                {
                    int order = char.ToUpperInvariant(left[a]).CompareTo(char.ToUpperInvariant(right[b]));
                    if (order != 0) return order;
                    a++; b++;
                }
            }
            return (left.Length - a).CompareTo(right.Length - b);
        }
        private static bool IsDigit(char value) => value is >= '0' and <= '9';
    }
}

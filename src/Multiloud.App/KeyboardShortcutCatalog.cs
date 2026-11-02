namespace Multiloud.App;

internal sealed record KeyboardShortcut(string Action, string Keys, string Context = "Workspace");

/// <summary>Shared names and keys for shortcut help and persistent button tooltips.</summary>
internal static class KeyboardShortcutCatalog
{
    internal static IReadOnlyList<KeyboardShortcut> Entries { get; } = new KeyboardShortcut[]
    {
        new("Play / pause", "Space"),
        new("Original", "1"), new("Result", "2"), new("Comparison", "3"),
        new("Envelope", "E"), new("Gains", "G"), new("Background track", "B"),
        new("Enable background", "Shift+B"), new("Background Solo", "Shift+S"),
        new("Custom export boundaries", "C"), new("File list", "F"), new("Fit all", "A / Home"),
        new("Slice at playhead position", "S"), new("Merge at nearest boundary", "M"), new("Loop A", "Z"), new("Loop B", "X"),
        new("Add audio files", "Ctrl+O"), new("Open Loudline project...", "Ctrl+Shift+O"),
        new("Save project", "Ctrl+S"), new("Close project", "Ctrl+W"), new("Export all", "Ctrl+E"),
        new("Undo", "Ctrl+Z"), new("Redo", "Ctrl+Y"), new("Remove selected main-track file", "Delete"),
        new("Pan left / right", "← / →", "Waveform focused"),
        new("Adjust by 1 percentage point", "Arrows", "Strength slider"),
        new("Adjust by 0.1 percentage point", "Shift+arrows", "Strength slider"),
        new("Adjust by 10 percentage points", "Page Down / Page Up", "Strength slider"),
        new("Adjust by 1 percentage point", "Shift+Page Down / Page Up", "Strength slider"),
        new("Set 0% / 100%", "Home / End", "Strength slider"),
        new("Apply settings", "Enter", "Gear panel"), new("Cancel settings", "Esc", "Gear panel"),
        new("Apply value", "Enter", "Number editor"),
        new("Cancel value edit", "Esc", "Track / background editor"),
        new("Close / cancel dialog", "Esc", "Dialogs"),
        new("Cancel drag", "Esc", "Background clip / gain"),
        new("Cancel loop edit", "Esc", "Loop range / handles"),
    };

    internal static string Tooltip(string action)
    {
        var entry = Entries.First(item => item.Action == action && item.Context == "Workspace");
        return $"{entry.Action} ({entry.Keys})";
    }
}

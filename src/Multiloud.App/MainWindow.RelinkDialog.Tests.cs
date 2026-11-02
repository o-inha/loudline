using Multiloud.App.Controls;
using Multiloud.App.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SavedProjectTrack = Multiloud.App.Services.ProjectTrack;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyRelinkDialogSmokeAsync(string? screenshot)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "Loudline-relink-smoke-" + Guid.NewGuid().ToString("N"));
        MissingFilesDialog? dialog = null, unresolvedDialog = null;
        try
        {
            string firstFolder = Path.Combine(temporary, "first-folder");
            string secondFolder = Path.Combine(temporary, "second-folder");
            string nested = Path.Combine(secondFolder, "disc-two");
            Directory.CreateDirectory(firstFolder);
            Directory.CreateDirectory(nested);
            string first = Path.Combine(firstFolder, "01 Opening.wav");
            string second = Path.Combine(nested, "02 Middle.wav");
            string renamed = Path.Combine(temporary, "Renamed ending.wav");
            // Resolution checks source existence only; decoding belongs to import.
            foreach (string path in new[] { first, second, renamed }) File.WriteAllBytes(path, [0, 1, 2, 3]);
            string project = Path.Combine(temporary, "Release.loudline");
            ProjectSourceLink Missing(string name) => ProjectFileService.Resolve(
                new SavedProjectTrack { Path = Path.Combine("old-location", name) }, project);
            var links = new[] { Missing("01 Opening.wav"), Missing("02 Middle.wav"), Missing("03 Ending.wav") };
            if (links.Any(link => link.IsResolved)) throw new InvalidOperationException("Smoke test: missing-file fixture unexpectedly resolved.");

            dialog = new MissingFilesDialog(links) { Owner = this };
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            dialog.Show();
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Render);
            if (dialog.Rows.Items.Count != 3 || dialog.ResolvedCount != 0)
                throw new InvalidOperationException("Smoke test: missing-files dialog did not retain every initially missing row.");

            await dialog.LocateFolderAsync(firstFolder);
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Render);
            if (!links[0].IsResolved || links[1].IsResolved || links[2].IsResolved || !dialog.IsVisible || dialog.IsSearching)
                throw new InvalidOperationException("Smoke test: first folder search did not resolve only its matching source and leave the dialog open.");
            VerifyRelinkRowsSmoke(dialog, links, 1);

            await dialog.LocateFolderAsync(secondFolder);
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Render);
            if (!links[0].IsResolved || !links[1].IsResolved || links[2].IsResolved || !dialog.IsVisible)
                throw new InvalidOperationException("Smoke test: repeated folder search lost earlier matches, failed recursive lookup, or closed the dialog.");
            VerifyRelinkRowsSmoke(dialog, links, 2);
            if (screenshot is not null)
                CaptureVisual(dialog, Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-missing-files.png"));

            if (!dialog.RelinkFile(links[2], renamed) || links[2].ResolvedPath != renamed || dialog.ResolvedCount != 3 ||
                links[2].Saved.Path != Path.Combine("old-location", "03 Ending.wav"))
                throw new InvalidOperationException("Smoke test: explicit renamed-file relink failed or mutated the original saved source path.");
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Render);
            VerifyRelinkRowsSmoke(dialog, links, 3);
            if (dialog.FindName("MissingFilesDoneButton") is not Button done)
                throw new InvalidOperationException("Smoke test: missing-files Done button is unavailable.");
            done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!closed || dialog.IsVisible) throw new InvalidOperationException("Smoke test: Done did not close the missing-files dialog.");

            var unresolved = Missing("Unfound.wav");
            unresolvedDialog = new MissingFilesDialog([unresolved]) { Owner = this };
            unresolvedDialog.Show();
            await Dispatcher.InvokeAsync(unresolvedDialog.UpdateLayout, DispatcherPriority.Render);
            ((Button)unresolvedDialog.FindName("MissingFilesDoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (unresolvedDialog.IsVisible || unresolved.IsResolved)
                throw new InvalidOperationException("Smoke test: missing-files dialog could not close with an unresolved row.");
        }
        finally
        {
            if (dialog?.IsVisible == true) dialog.Close();
            if (unresolvedDialog?.IsVisible == true) unresolvedDialog.Close();
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void VerifyRelinkRowsSmoke(MissingFilesDialog dialog, IReadOnlyList<ProjectSourceLink> links, int expectedFound)
    {
        if (dialog.Rows.Items.Count != links.Count || dialog.ResolvedCount != expectedFound ||
            dialog.Rows.Items.Cast<ProjectSourceLink>().Where((link, index) => !ReferenceEquals(link, links[index])).Any())
            throw new InvalidOperationException("Smoke test: found sources disappeared from or reordered the missing-files list.");
        var accent = ((SolidColorBrush)dialog.FindResource("AccentBrush")).Color;
        for (int index = 0; index < links.Count; index++)
        {
            if (dialog.Rows.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
                throw new InvalidOperationException("Smoke test: missing-file row was not materialized in the visible native list.");
            var text = Descendants<TextBlock>(container).ToArray();
            var check = text.SingleOrDefault(block => block.Text == (links[index].IsResolved ? "✓" : "○"));
            if (check is null || links[index].IsResolved && (check.Foreground is not SolidColorBrush foreground || foreground.Color != accent) ||
                !text.Any(block => block.Text == links[index].FileName) || !text.Any(block => block.Text == links[index].ExpectedPath))
                throw new InvalidOperationException("Smoke test: missing-file row did not show its old location and resolved green check.");
        }
    }
}

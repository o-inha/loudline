using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Multiloud.App;

public partial class MainWindow
{
    private async Task VerifyAboutDialogSmokeAsync(string? screenshot)
    {
        await WaitForSmokePreviewAsync();
        string theme = ThemeService.Mode;
        int revision = _revision, executions = _renderExecutionCount, history = _undo.Count;
        bool dirty = _dirty;
        var settings = _settings;
        Window? dialog = null;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("About dialog smoke: " + message); }
        static string InlineText(InlineCollection inlines) => string.Concat(inlines.Cast<Inline>().Select(inline => inline switch
        {
            Run run => run.Text,
            Span span => InlineText(span.Inlines),
            LineBreak => "\n",
            _ => throw new InvalidOperationException($"Unexpected About inline: {inline.GetType().Name}")
        }));
        try
        {
            foreach (string mode in new[] { "Dark", "Light" })
            {
                ThemeService.Apply(mode);
                dialog = CreateAboutDialog(); dialog.Owner = this; dialog.Show();
                await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ContextIdle);
                if (screenshot != null)
                    CaptureVisual(dialog, Path.Combine(Path.GetDirectoryName(screenshot)!, mode == "Dark" ? "Loudline-about.png" : "Loudline-about-light.png"));
                VerifyDialogThemeSmoke(dialog);
                Require(dialog.Title == "About Loudline" && dialog.ShowInTaskbar == false &&
                    dialog.SizeToContent == SizeToContent.Height && dialog.ResizeMode == ResizeMode.NoResize &&
                    dialog.Style == FindResource(typeof(Window)) && ReferenceEquals(dialog.Owner, this),
                    "The app information did not use the existing compact themed window style.");
                var attribution = (TextBlock)dialog.FindName("AboutAttribution");
                var source = (TextBlock)dialog.FindName("AboutSource");
                // Inspect the authored text rather than serializing the WPF
                // TextContainer, which also encodes inline element boundaries.
                string attributionText = InlineText(attribution.Inlines);
                Require(attributionText == "Originally designed & vibe-coded by O. Inha / https://inha.jp",
                    $"The designer attribution changed. Actual: {System.Text.Json.JsonSerializer.Serialize(attributionText)}.");
                var attributionLink = attribution.Inlines.OfType<Hyperlink>().SingleOrDefault();
                Require(attributionLink?.NavigateUri == new Uri("https://inha.jp"),
                    $"The designer link changed. Actual: {attributionLink?.NavigateUri?.OriginalString ?? "<missing>"}.");
                var sourceLink = source.Inlines.OfType<Hyperlink>().SingleOrDefault();
                Require(sourceLink?.NavigateUri == new Uri("https://github.com/o-inha/loudline"),
                    $"The source link changed. Actual: {sourceLink?.NavigateUri?.OriginalString ?? "<missing>"}.");
                string displayedVersion = ((TextBlock)dialog.FindName("AboutVersion")).Text;
                Require(displayedVersion == AppVersion.Display,
                    $"The current version is incorrect. Actual: {displayedVersion}; expected: {AppVersion.Display}.");
                string licenceText = ((TextBlock)dialog.FindName("AboutLicence")).Text;
                Require(licenceText == "MIT License · Free to use, modify and redistribute.",
                    $"The licence information changed. Actual: {System.Text.Json.JsonSerializer.Serialize(licenceText)}.");
                Require(attribution.Inlines.OfType<Hyperlink>().Single().Foreground is SolidColorBrush foreground &&
                    foreground.Color == ((SolidColorBrush)dialog.FindResource("AccentBrush")).Color,
                    "The attribution link did not follow the app's current accent color.");
                var close = (Button)dialog.FindName("AboutCloseButton");
                Require(close.IsDefault && close.IsCancel && close.Style != null && Math.Abs(close.ActualHeight - 28) <= 1 &&
                    Equals(close.ToolTip, "Close (Enter / Esc)"), "Close did not retain the normal button style and keyboard dismissal.");
                if (mode == "Dark") close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else EscapeDialogSmoke(dialog);
                Require(!dialog.IsVisible, "Closing the About window left it visible.");
                dialog = null;
                Require(ReferenceEquals(settings, _settings) && _revision == revision && _renderExecutionCount == executions &&
                    _undo.Count == history && _dirty == dirty, "Opening or closing About changed the audio state or edit history.");
            }
        }
        finally
        {
            dialog?.Close(); ThemeService.Apply(theme); UpdateThemeChecks();
        }
    }
}

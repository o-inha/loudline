using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace Multiloud.App;

public partial class MainWindow
{
    private void AboutClick(object sender, RoutedEventArgs e)
    {
        var dialog = CreateAboutDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private Window CreateAboutDialog()
    {
        var dialog = new Window
        {
            Title = "About Loudline", Width = 540, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, UseLayoutRounding = true
        };
        dialog.SetResourceReference(StyleProperty, typeof(Window));
        NameScope.SetNameScope(dialog, new NameScope());
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = panel;

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var symbol = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0) };
        symbol.SetBinding(Image.SourceProperty, new Binding(nameof(Window.Icon)) { Source = dialog });
        heading.Children.Add(symbol);
        var title = new StackPanel(); heading.Children.Add(title);
        title.Children.Add(new TextBlock { Text = "Loudline", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var version = new TextBlock { Name = "AboutVersion", Text = AppVersion.Display };
        version.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        dialog.RegisterName(version.Name, version); title.Children.Add(version);
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = "Music loudness matching and dynamic leveling for Windows 11 x64.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });

        Hyperlink Link(string text, string address)
        {
            var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(address) };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.RequestNavigate += OpenAboutLink;
            return link;
        }
        var attribution = new TextBlock
        {
            Name = "AboutAttribution", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        attribution.Inlines.Add(new Run("Originally designed & vibe-coded by O. Inha / "));
        attribution.Inlines.Add(Link("https://inha.jp", "https://inha.jp"));
        dialog.RegisterName(attribution.Name, attribution); panel.Children.Add(attribution);

        var source = new TextBlock { Name = "AboutSource", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        source.Inlines.Add(Link("https://github.com/o-inha/loudline", "https://github.com/o-inha/loudline"));
        dialog.RegisterName(source.Name, source); panel.Children.Add(source);
        var licence = new TextBlock
        {
            Name = "AboutLicence",
            Text = "MIT License · Free to use, modify and redistribute.", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        dialog.RegisterName(licence.Name, licence); panel.Children.Add(licence);
        var dependencies = new TextBlock
        {
            Text = "Uses NAudio and .NET. See THIRD-PARTY-NOTICES.md for their terms.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        };
        dependencies.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(dependencies);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button
        {
            Name = "AboutCloseButton", Content = "Close", IsDefault = true, IsCancel = true,
            MinWidth = 78, Margin = new Thickness(0), ToolTip = "Close (Enter / Esc)"
        };
        dialog.RegisterName(close.Name, close);
        ToolTipService.SetIsEnabled(close, true);
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close); panel.Children.Add(actions);
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true; dialog.Close();
        };
        dialog.SourceInitialized += (_, _) => ThemeService.Apply(ThemeService.Mode);
        return dialog;
    }

    private void OpenAboutLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { ShowError("Could not open website", error); }
    }
}

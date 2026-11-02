using Microsoft.Win32;
using Multiloud.App.Services;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Multiloud.App.Controls;

/// <summary>Collects replacement source locations without changing the saved project.</summary>
public sealed class MissingFilesDialog : Window
{
    private static readonly DependencyPropertyKey IsSearchingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsSearching), typeof(bool), typeof(MissingFilesDialog), new PropertyMetadata(false));
    public static readonly DependencyProperty IsSearchingProperty = IsSearchingPropertyKey.DependencyProperty;

    private readonly IReadOnlyList<ProjectSourceLink> _links;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Button _locateButton;
    private readonly ProcessingSpinner _spinner;
    private readonly TextBlock _status;
    private bool _closed;

    public MissingFilesDialog(IReadOnlyList<ProjectSourceLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        _links = links.ToArray();
        Title = "Missing audio files";
        Width = 800;
        Height = 540;
        MinWidth = 540;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        UseLayoutRounding = true;
        NameScope.SetNameScope(this, new NameScope());
        AutomationProperties.SetName(this, Title);

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;

        var header = new TextBlock
        {
            Text = "Missing audio files", FontSize = 19, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        layout.Children.Add(header);

        Rows = new ListBox
        {
            Name = "MissingFilesList", ItemsSource = _links,
            ItemTemplate = CreateRowTemplate(), ItemContainerStyle = CreateRowContainerStyle(),
            Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 14)
        };
        VirtualizingPanel.SetIsVirtualizing(Rows, true);
        VirtualizingPanel.SetVirtualizationMode(Rows, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(Rows, true);
        ScrollViewer.SetVerticalScrollBarVisibility(Rows, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(Rows, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(Rows, "Missing audio files");
        RegisterName(Rows.Name, Rows);
        Grid.SetRow(Rows, 1);
        layout.Children.Add(Rows);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        _locateButton = new Button
        {
            Name = "LocateFolderButton", Content = "Locate folder…", MinWidth = 120,
            Margin = new Thickness(0, 0, 16, 0)
        };
        _locateButton.Click += LocateFolderClick;
        RegisterName(_locateButton.Name, _locateButton);
        footer.Children.Add(_locateButton);

        var progress = new Grid { VerticalAlignment = VerticalAlignment.Center };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(progress, 1);
        footer.Children.Add(progress);
        _spinner = new ProcessingSpinner
        {
            Name = "FolderSearchSpinner", Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };
        RegisterName(_spinner.Name, _spinner);
        progress.Children.Add(_spinner);
        _status = new TextBlock { Name = "MissingFilesStatus", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        RegisterName(_status.Name, _status);
        Grid.SetColumn(_status, 1);
        progress.Children.Add(_status);

        var done = new Button
        {
            Name = "MissingFilesDoneButton", Content = "Done", MinWidth = 84,
            IsDefault = true, Margin = new Thickness(16, 0, 0, 0)
        };
        done.Click += (_, _) => Close();
        RegisterName(done.Name, done);
        Grid.SetColumn(done, 2);
        footer.Children.Add(done);

        foreach (var link in _links) link.PropertyChanged += SourceChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        UpdateSummary();
    }

    public bool IsSearching => (bool)GetValue(IsSearchingProperty);
    internal IReadOnlyList<ProjectSourceLink> SourceLinks => _links;
    internal int ResolvedCount => _links.Count(link => link.IsResolved);
    internal ListBox Rows { get; }

    private DataTemplate CreateRowTemplate()
    {
        var row = new FrameworkElementFactory(typeof(Border));
        row.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        row.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        row.SetValue(Border.PaddingProperty, new Thickness(12, 11, 12, 11));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 8));
        row.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ProjectSourceLink.FileName)));

        var panel = new FrameworkElementFactory(typeof(DockPanel));
        panel.SetValue(DockPanel.LastChildFillProperty, true);
        row.AppendChild(panel);

        var state = new FrameworkElementFactory(typeof(TextBlock));
        state.SetValue(DockPanel.DockProperty, Dock.Left);
        state.SetValue(FrameworkElement.WidthProperty, 28.0);
        state.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        state.SetValue(TextBlock.FontSizeProperty, 19.0);
        var stateStyle = new Style(typeof(TextBlock));
        stateStyle.Setters.Add(new Setter(TextBlock.TextProperty, "○"));
        stateStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("MutedBrush")));
        var found = new DataTrigger { Binding = new Binding(nameof(ProjectSourceLink.IsResolved)), Value = true };
        found.Setters.Add(new Setter(TextBlock.TextProperty, "✓"));
        found.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentBrush")));
        stateStyle.Triggers.Add(found);
        state.SetValue(FrameworkElement.StyleProperty, stateStyle);
        state.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ProjectSourceLink.StatusText)));
        panel.AppendChild(state);

        var relink = new FrameworkElementFactory(typeof(Button));
        relink.SetValue(DockPanel.DockProperty, Dock.Right);
        relink.SetValue(ContentControl.ContentProperty, "Relink…");
        relink.SetValue(FrameworkElement.WidthProperty, 88.0);
        relink.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 0, 0, 0));
        relink.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var actionStyle = new Style(typeof(Button), TryFindResource(typeof(Button)) as Style);
        var busy = new DataTrigger { Binding = new Binding(nameof(IsSearching)) { Source = this }, Value = true };
        busy.Setters.Add(new Setter(IsEnabledProperty, false));
        actionStyle.Triggers.Add(busy);
        relink.SetValue(FrameworkElement.StyleProperty, actionStyle);
        relink.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ProjectSourceLink.FileName)) { StringFormat = "Relink {0}" });
        relink.AddHandler(Button.ClickEvent, new RoutedEventHandler(RelinkClick));
        panel.AppendChild(relink);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(text);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProjectSourceLink.FileName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.AppendChild(name);
        var location = new FrameworkElementFactory(typeof(TextBlock));
        location.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProjectSourceLink.ExpectedPath)));
        location.SetValue(TextBlock.FontSizeProperty, 11.0);
        location.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        location.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        location.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.AppendChild(location);
        return new DataTemplate(typeof(ProjectSourceLink)) { VisualTree = row };
    }

    private static Style CreateRowContainerStyle()
    {
        // This list has no track-reordering/removal affordances from the main file list.
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }));
        return style;
    }

    private async void LocateFolderClick(object sender, RoutedEventArgs e)
    {
        if (IsSearching || _closed) return;
        var picker = new OpenFolderDialog { Title = "Locate missing audio files" };
        if (picker.ShowDialog(this) == true) await LocateFolderAsync(picker.FolderName);
    }

    internal async Task LocateFolderAsync(string folder)
    {
        Dispatcher.VerifyAccess();
        if (IsSearching || _closed) return;
        SetSearching(true);
        _status.Text = "Searching…";
        try
        {
            var result = await ProjectFileService.SearchFolder(_links, folder, _lifetime.Token);
            if (!_closed) UpdateSummary(result.Ambiguous);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = ex.Message;
        }
        finally
        {
            SetSearching(false);
            if (_closed) _lifetime.Dispose();
        }
    }

    private void RelinkClick(object sender, RoutedEventArgs e)
    {
        if (IsSearching || _closed || sender is not FrameworkElement { DataContext: ProjectSourceLink link }) return;
        var picker = new OpenFileDialog
        {
            Title = $"Relink {link.FileName}", Filter = AudioFileService.FileFilter, Multiselect = false,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true) RelinkFile(link, picker.FileName);
    }

    internal bool RelinkFile(ProjectSourceLink row, string path)
    {
        Dispatcher.VerifyAccess();
        if (_closed || IsSearching || !_links.Contains(row)) return false;
        try
        {
            row.Relink(path);
            UpdateSummary();
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return false;
        }
    }

    private void SetSearching(bool value)
    {
        SetValue(IsSearchingPropertyKey, value);
        _locateButton.IsEnabled = !value;
        _spinner.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        _spinner.IsActive = value;
    }

    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_closed && !IsSearching && (e.PropertyName is nameof(ProjectSourceLink.IsResolved) or null or "")) UpdateSummary();
    }

    private void UpdateSummary(int ambiguous = 0)
    {
        var missing = _links.Count - ResolvedCount;
        _status.Text = $"{ResolvedCount} found · {missing} missing" + (ambiguous > 0 ? $" · {ambiguous} ambiguous" : "");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Icon = Owner?.Icon;
        if (TryFindResource("BackgroundBrush") is SolidColorBrush background)
        {
            var c = background.Color;
            var dark = c.R * .2126 + c.G * .7152 + c.B * .0722 < 128 ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        foreach (var link in _links) link.PropertyChanged -= SourceChanged;
        _lifetime.Cancel();
        _spinner.IsActive = false;
        if (!IsSearching) _lifetime.Dispose();
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

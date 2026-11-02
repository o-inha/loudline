using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Multiloud.App.Controls;

internal enum UnsavedProjectChoice { Cancel, Save, Discard }

/// <summary>Only an explicit discard or a successful save permits replacing the open project.</summary>
internal sealed class UnsavedProjectDialog : Window
{
    internal UnsavedProjectChoice Choice { get; private set; } = UnsavedProjectChoice.Cancel;

    internal UnsavedProjectDialog()
    {
        Title = "Unsaved project";
        Width = 410;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SetResourceReference(StyleProperty, typeof(Window));
        NameScope.SetNameScope(this, new NameScope());
        AutomationProperties.SetName(this, Title);

        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = panel;
        panel.Children.Add(new TextBlock
        {
            Text = "Save your changes to a Loudline project?",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });
        var actions = new StackPanel
        {
            Name = "UnsavedProjectActions", Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        RegisterName(actions.Name, actions);
        panel.Children.Add(actions);
        Button Action(string name, string text, UnsavedProjectChoice choice, bool last = false)
        {
            var button = new Button
            {
                Name = name, Content = text, MinWidth = 78,
                Margin = new Thickness(0, 0, last ? 0 : 6, 0)
            };
            button.Click += (_, _) => { Choice = choice; Close(); };
            RegisterName(name, button); actions.Children.Add(button);
            return button;
        }
        var save = Action("UnsavedSaveButton", "Save", UnsavedProjectChoice.Save);
        save.IsDefault = true;
        Action("UnsavedDiscardButton", "Don't save", UnsavedProjectChoice.Discard);
        Action("UnsavedCancelButton", "Cancel", UnsavedProjectChoice.Cancel, last: true);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Choice = UnsavedProjectChoice.Cancel;
            Close();
        };
        Loaded += (_, _) => save.Focus();
        SourceInitialized += (_, _) => ThemeService.Apply(ThemeService.Mode);
    }
}

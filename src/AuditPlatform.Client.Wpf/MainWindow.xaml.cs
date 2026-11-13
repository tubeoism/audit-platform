using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AuditPlatform.Client.Core.ViewModels;

namespace AuditPlatform.Client.Wpf;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private bool _closing;

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>A box is saved when the user leaves it: no save button, nothing to forget.</summary>
    private void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FieldViewModel field } && _viewModel.Engagement?.Paper is { } paper)
        {
            paper.SaveFieldCommand.Execute(field);
        }
    }

    /// <summary>Closing the window with the cursor still in a box must not lose what was typed there.</summary>
    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_closing || Keyboard.FocusedElement is not FrameworkElement { DataContext: FieldViewModel { IsDirty: true } field }
            || _viewModel.Engagement?.Paper is not { } paper)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        await paper.SaveFieldCommand.ExecuteAsync(field);
        Close();
    }
}

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

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Chọn file bằng chứng" };
        if (dialog.ShowDialog(this) == true && _viewModel.Engagement?.Paper is { } paper)
        {
            paper.AttachCommand.Execute(dialog.FileNames);
        }
    }

    private void Paper_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.Engagement?.Paper is { CanEdit: true } && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Paper_Drop(object sender, DragEventArgs e)
    {
        if (_viewModel.Engagement?.Paper is { CanEdit: true } paper && e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            // Folders are not evidence files; only the files themselves are taken.
            paper.AttachCommand.Execute(dropped.Where(System.IO.File.Exists).ToArray());
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

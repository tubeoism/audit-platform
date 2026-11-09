using System.Windows;
using AuditPlatform.Client.Core.ViewModels;

namespace AuditPlatform.Client.Wpf;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

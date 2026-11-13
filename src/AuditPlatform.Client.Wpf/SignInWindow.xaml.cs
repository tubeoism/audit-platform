using System.Windows;
using AuditPlatform.Client.Core.ViewModels;

namespace AuditPlatform.Client.Wpf;

public partial class SignInWindow : Window
{
    private readonly SignInViewModel _viewModel;

    public SignInWindow(SignInViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => EmailBox.Focus();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        // The password goes from the box straight into the request; it is not bound and not kept.
        _viewModel.Password = PasswordBox.Password;
        PasswordBox.Clear();
        _viewModel.SignInCommand.Execute(null);
    }
}

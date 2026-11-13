using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Contracts.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AuditPlatform.Client.Core.ViewModels;

/// <summary>Sign-in with password and authenticator code (ADR-0005). A recovery code replaces the authenticator code when the phone is lost.</summary>
public sealed partial class SignInViewModel(AuthSession session) : ObservableObject
{
    public event EventHandler<UserInfo>? SignedIn;

    [ObservableProperty]
    private string _email = "";

    /// <summary>Set by the window from its password box; never bound, never stored.</summary>
    public string Password { get; set; } = "";

    [ObservableProperty]
    private string _code = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeLabel))]
    private bool _useRecoveryCode;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public string CodeLabel => UseRecoveryCode ? "Mã khôi phục" : "Mã 6 số trên ứng dụng xác thực";

    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Email) || Password.Length == 0 || string.IsNullOrWhiteSpace(Code))
        {
            Error = "Nhập e-mail, mật khẩu và mã xác thực.";
            return;
        }

        IsBusy = true;
        try
        {
            var code = Code.Trim();
            var result = await session.SignInAsync(Email.Trim(), Password, UseRecoveryCode ? null : code, UseRecoveryCode ? code : null, cancellationToken);
            if (result.Value is { } user)
            {
                Code = "";
                SignedIn?.Invoke(this, user);
            }
            else
            {
                Error = result.Error?.Message ?? "Đăng nhập không thành công.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}

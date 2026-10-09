using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Desktop.Services;

namespace Cuelify.Desktop.ViewModels;

public enum PasswordPurpose { Create, Unlock, Change }

public partial class MasterPasswordViewModel(PasswordPurpose purpose, Func<string, string, Task> submit) : ObservableObject
{
    [ObservableProperty] private string currentPassword = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string confirmation = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PasswordChar))] private bool showPassword;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool isBusy;
    public string Title => purpose switch { PasswordPurpose.Create => "设置主密码", PasswordPurpose.Change => "更换主密码", _ => "解锁密钥" };
    public string SubmitText => IsBusy ? "正在处理…" : purpose switch { PasswordPurpose.Create => "设置并解锁", PasswordPurpose.Change => "更换主密码", _ => "解锁" };
    public bool IsChange => purpose == PasswordPurpose.Change;
    public string PasswordLabel => IsChange ? "新主密码" : "主密码";
    public bool NeedsConfirmation => purpose != PasswordPurpose.Unlock;
    public bool CanEdit => !IsBusy;
    public char PasswordChar => ShowPassword ? '\0' : '●';
    public event Action? Completed;
    public event Action? Cancelled;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(SubmitText)); SubmitCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SubmitAsync()
    {
        if (!CanEdit) return;
        Error = "";
        if (string.IsNullOrEmpty(Password)) { Error = "请输入主密码。"; return; }
        if (NeedsConfirmation && Password != Confirmation) { Error = "两次输入的主密码不一致。"; return; }
        if (IsChange && string.IsNullOrEmpty(CurrentPassword)) { Error = "请输入当前主密码。"; return; }
        IsBusy = true;
        try { await submit(IsChange ? CurrentPassword : Password, Password); IsBusy = false; Completed?.Invoke(); }
        catch (OperationCanceledException) { IsBusy = false; Cancelled?.Invoke(); }
        catch (Exception exception) { Error = CredentialErrorMessages.FromException(exception); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void Cancel() => Cancelled?.Invoke();
    public void Clear() { CurrentPassword = ""; Password = ""; Confirmation = ""; }
}

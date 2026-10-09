using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly CredentialStore _credentials;
    private readonly CancellationTokenSource _credentialCancellation = new();
    private Task? _credentialOperation;
    private string _compatibleKey = "";
    private string _deepSeekKey = "";
    private string _compatibleAddress = "";
    [ObservableProperty] private bool isCredentialBusy;
    [ObservableProperty] private string credentialStatus = "";
    public bool HasMasterPassword => _credentials.HasMasterPassword;
    public bool IsUnlocked => _credentials.IsUnlocked;
    public bool CanManageCredentials => CanConfigure;
    public bool CanEditCredentials => CanConfigure && IsUnlocked;
    public string CredentialActionText => HasMasterPassword ? "解锁" : "设置主密码";
    public string CredentialHint => !HasMasterPassword ? "尚未设置主密码，设置后可加密保存 API 密钥。" :
        !IsUnlocked ? "密钥已锁定，请输入主密码解锁。" : "本次使用已解锁。";
    public string TranslationKey
    {
        get => Settings.Provider == TranslationProvider.Compatible ? _compatibleKey : Settings.Provider == TranslationProvider.DeepSeek ? _deepSeekKey : "";
        set
        {
            if (Settings.Provider == TranslationProvider.Compatible) { _compatibleKey = value; _compatibleAddress = Address(Settings.BaseUrl); }
            else if (Settings.Provider == TranslationProvider.DeepSeek) _deepSeekKey = value;
            ResetEngineStatus(); CredentialStatus = "有未保存的密钥修改。"; OnPropertyChanged(); RefreshCommands();
        }
    }
    public bool NeedsAddressConfirmation => IsCompatible && !string.IsNullOrWhiteSpace(_compatibleKey) && _compatibleAddress != Address(Settings.BaseUrl);
    public bool CanConfirmAddress => CanEditCredentials && NeedsAddressConfirmation;
    public string EngineCredentialHint => Settings.Provider == TranslationProvider.Local ? "" : TranslationCredentialHint(Settings.Provider, Settings.BaseUrl);
    public string TaskCredentialHint => IsFileStep ? SpeechCredentialHint : RunCredentialHint;
    public bool HasTaskCredentialHint => !string.IsNullOrEmpty(TaskCredentialHint);
    public bool NeedsMasterPassword => !IsUnlocked;
    public bool NeedsSpeechKey => IsUnlocked && string.IsNullOrWhiteSpace(ElevenLabsKey);
    public bool NeedsTranslationKey => IsUnlocked && !IsFileStep && !string.IsNullOrEmpty(TranslationCredentialHint((TranslationProvider)TaskProviderIndex, _savedSettings.BaseUrl));
    public bool CanNext => CanConfigure && !string.IsNullOrWhiteSpace(InputPath) && string.IsNullOrEmpty(SpeechCredentialHint);
    public bool CanTestEngine => CanConfigure && string.IsNullOrEmpty(EngineCredentialHint);
    public bool ShowResume => CanContinue && IsResultsStep && !IsBusy;
    private string SpeechCredentialHint => !HasMasterPassword ? "请先设置主密码。" : !IsUnlocked ? "请先解锁已保存的密钥。" :
        string.IsNullOrWhiteSpace(ElevenLabsKey) ? "尚未填写 ElevenLabs API 密钥。" : "";
    private string RunCredentialHint => string.Join(" ", new[] { SpeechCredentialHint,
        IsUnlocked ? TranslationCredentialHint((TranslationProvider)TaskProviderIndex, _savedSettings.BaseUrl) : "" }.Where(value => value.Length > 0));
    private string TranslationCredentialHint(TranslationProvider provider, string address)
    {
        if (provider == TranslationProvider.Local) return "";
        if (!IsUnlocked) return !HasMasterPassword ? "请先设置主密码。" : "请先解锁已保存的密钥。";
        if (provider == TranslationProvider.DeepSeek) return string.IsNullOrWhiteSpace(_deepSeekKey) ? "尚未填写 DeepSeek API 密钥。" : "";
        if (string.IsNullOrWhiteSpace(_compatibleKey)) return "尚未填写兼容服务 API 密钥。";
        return _compatibleAddress == Address(address) ? "" : "兼容服务地址已变化，请在服务设置中确认密钥对应的地址，并保存设置。";
    }
    private static string Address(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : value.Trim().TrimEnd('/');
    private string KeyFor(TranslationProvider provider) => provider switch { TranslationProvider.Compatible => _compatibleKey, TranslationProvider.DeepSeek => _deepSeekKey, _ => "" };
    private IEnumerable<string> Secrets => new[] { ElevenLabsKey, _compatibleKey, _deepSeekKey, _credentials.Values.ElevenLabs, _credentials.Values.Compatible, _credentials.Values.DeepSeek };
    partial void OnElevenLabsKeyChanged(string value) { CredentialStatus = "有未保存的密钥修改。"; RefreshCommands(); }
    partial void OnIsCredentialBusyChanged(bool value) => RefreshCommands();

    private void RefreshCredentialCommands()
    {
        foreach (var name in new[] { nameof(HasMasterPassword), nameof(IsUnlocked), nameof(CanManageCredentials), nameof(CanEditCredentials), nameof(CredentialActionText), nameof(CredentialHint), nameof(TranslationKey), nameof(NeedsAddressConfirmation), nameof(CanConfirmAddress), nameof(EngineCredentialHint), nameof(TaskCredentialHint), nameof(HasTaskCredentialHint), nameof(NeedsMasterPassword), nameof(NeedsSpeechKey), nameof(NeedsTranslationKey), nameof(CanNext), nameof(CanTestEngine), nameof(ShowResume) }) OnPropertyChanged(name);
        ManageMasterPasswordCommand.NotifyCanExecuteChanged(); LockCredentialsCommand.NotifyCanExecuteChanged(); SaveCredentialsCommand.NotifyCanExecuteChanged();
        ChangeMasterPasswordCommand.NotifyCanExecuteChanged(); ResetCredentialsCommand.NotifyCanExecuteChanged(); ConfirmCompatibleAddressCommand.NotifyCanExecuteChanged();
    }
    private void LoadCredentials()
    {
        var values = _credentials.Values;
        ElevenLabsKey = values.ElevenLabs; _compatibleKey = values.Compatible; _deepSeekKey = values.DeepSeek; _compatibleAddress = values.CompatibleAddress;
        ResetEngineStatus(); RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanManageCredentials))]
    private Task ManageMasterPasswordAsync() => CredentialOperationAsync(async token =>
    {
        if (IsUnlocked) return;
        var purpose = HasMasterPassword ? PasswordPurpose.Unlock : PasswordPurpose.Create;
        if (await _dialogs.PasswordAsync(purpose, async (password, _) =>
        {
            if (purpose == PasswordPurpose.Create) await _credentials.CreateAsync(password, token);
            else await _credentials.UnlockAsync(password, token);
        }, token)) { LoadCredentials(); CredentialStatus = "密钥已解锁。"; }
    });

    [RelayCommand(CanExecute = nameof(CanEditCredentials))]
    private void LockCredentials()
    { if (!CanEditCredentials) return; _credentials.Lock(); LoadCredentials(); CredentialStatus = "密钥已锁定。"; }

    [RelayCommand(CanExecute = nameof(CanEditCredentials))]
    private Task SaveCredentialsAsync() => CredentialOperationAsync(async token =>
    {
        if (NeedsAddressConfirmation) throw new InvalidOperationException("请先确认兼容服务密钥对应的新地址。");
        await _credentials.SaveAsync(new(ElevenLabsKey, _compatibleKey, _deepSeekKey, _compatibleAddress), token);
        CredentialStatus = "密钥已加密保存。";
    });

    [RelayCommand(CanExecute = nameof(CanConfirmAddress))]
    private async Task ConfirmCompatibleAddressAsync()
    {
        if (await _dialogs.ConfirmAsync("确认将已有兼容服务密钥用于当前服务地址？后续请求会将密钥发送至该地址。") && CanConfirmAddress)
        { _compatibleAddress = Address(Settings.BaseUrl); CredentialStatus = "有未保存的密钥修改。"; ResetEngineStatus(); RefreshCommands(); }
    }

    [RelayCommand(CanExecute = nameof(CanEditCredentials))]
    private Task ChangeMasterPasswordAsync() => CredentialOperationAsync(async token =>
    {
        if (await _dialogs.PasswordAsync(PasswordPurpose.Change, (current, next) => _credentials.ChangePasswordAsync(current, next, token), token))
            CredentialStatus = "主密码已更换。";
    });

    [RelayCommand(CanExecute = nameof(CanManageCredentials))]
    private Task ResetCredentialsAsync() => CredentialOperationAsync(async token =>
    {
        if (!await _dialogs.ConfirmAsync("重置凭证将清除全部已保存的 API 密钥，需要重新设置主密码并填写密钥。普通设置和字幕缓存会保留。确认重置？", token)) return;
        await _credentials.ResetAsync(token); LoadCredentials(); CredentialStatus = "凭证已重置，请重新设置主密码。";
    });

    private Task CredentialOperationAsync(Func<CancellationToken, Task> action)
    {
        if (!CanManageCredentials) return Task.CompletedTask;
        IsCredentialBusy = true; CredentialStatus = "";
        return _credentialOperation = GuardCredentialAsync(action);
    }
    private async Task GuardCredentialAsync(Func<CancellationToken, Task> action)
    {
        try { await action(_credentialCancellation.Token); }
        catch (Exception exception) { CredentialStatus = CredentialErrorMessages.FromException(exception); }
        finally { IsCredentialBusy = false; }
    }
}

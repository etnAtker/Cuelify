using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class CredentialWorkflowTests
{
    [AvaloniaFact]
    public async Task SavingServiceAddressImmediatelyRefreshesRunButton()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model }; window.Show(); await model.InitializeCommand.ExecuteAsync(null);
        model.InputPath = "fixture.mp4"; await model.NextStepCommand.ExecuteAsync(null); model.TaskProviderIndex = 0; model.ProviderIndex = 0;
        model.Settings.BaseUrl = "https://new.example/v1"; await model.ConfirmCompatibleAddressCommand.ExecuteAsync(null);
        var button = window.FindControl<WorkspaceView>("WorkspacePage")!.FindControl<Button>("RunButton")!;
        Assert.False(button.IsEffectivelyEnabled);
        await model.SaveSettingsCommand.ExecuteAsync(null); Assert.True(button.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(PasswordPurpose.Create)]
    [InlineData(PasswordPurpose.Unlock)]
    [InlineData(PasswordPurpose.Change)]
    public async Task ActualPasswordDialogKeepsFailedInputAndClearsAfterSuccessfulSubmit(PasswordPurpose purpose)
    {
        var owner = new Window(); owner.Show(); var dialogs = new WindowDialogs(owner); var fail = true; string? received = null;
        var result = dialogs.PasswordAsync(purpose, (current, _) =>
        {
            if (fail) throw new InvalidDataException("主密码不正确或凭证文件已损坏。");
            received = current; return Task.CompletedTask;
        });
        var dialog = Assert.IsType<MasterPasswordDialog>(Assert.Single(owner.OwnedWindows));
        var model = Assert.IsType<MasterPasswordViewModel>(dialog.DataContext);
        model.CurrentPassword = "中"; model.Password = "!"; model.Confirmation = model.Password;
        await model.SubmitCommand.ExecuteAsync(null);
        Assert.True(dialog.IsVisible); Assert.Contains("主密码不正确", model.Error); Assert.Equal("!", model.Password);
        fail = false; await model.SubmitCommand.ExecuteAsync(null); Assert.True(await result);
        Assert.Equal(purpose == PasswordPurpose.Change ? "中" : "!", received);
        Assert.Empty(model.CurrentPassword); Assert.Empty(model.Password); Assert.Empty(model.Confirmation); owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordDialogCancellationClosesIdleAndBusyDialogs(bool busy)
    {
        var owner = new Window(); owner.Show(); var dialogs = new WindowDialogs(owner);
        using var cancellation = new CancellationTokenSource();
        var result = dialogs.PasswordAsync(PasswordPurpose.Unlock, async (_, _) => await Task.Delay(Timeout.Infinite, cancellation.Token), cancellation.Token);
        var dialog = Assert.IsType<MasterPasswordDialog>(Assert.Single(owner.OwnedWindows));
        var model = Assert.IsType<MasterPasswordViewModel>(dialog.DataContext); model.Password = "test-master-password";
        var submitted = busy ? model.SubmitCommand.ExecuteAsync(null) : Task.CompletedTask;
        cancellation.Cancel(); Assert.False(await result); await submitted;
        Assert.Empty(model.Password); Assert.False(dialog.IsVisible); owner.Close();
    }

    [AvaloniaFact]
    public async Task FirstUseRequiresPasswordAndSpeechKeyBeforeNextStep()
    {
        using var fixture = new Fixture();
        await using var model = new MainWindowViewModel(fixture.Jobs, fixture.Store, fixture.Dialogs);
        model.InputPath = "fixture.mp4";
        Assert.False(model.HasMasterPassword); Assert.False(model.CanNext); Assert.Contains("设置主密码", model.TaskCredentialHint);
        fixture.Dialogs.AcceptPassword = false; await model.ManageMasterPasswordCommand.ExecuteAsync(null);
        Assert.False(model.HasMasterPassword); Assert.Equal("fixture.mp4", model.InputPath);
        fixture.Dialogs.AcceptPassword = true; await model.ManageMasterPasswordCommand.ExecuteAsync(null);
        Assert.True(model.IsUnlocked); Assert.False(model.CanNext); Assert.Contains("ElevenLabs", model.TaskCredentialHint);
        model.ElevenLabsKey = "speech-secret"; Assert.True(model.CanNext); await model.NextStepCommand.ExecuteAsync(null);
        Assert.True(model.IsOptionsStep); Assert.False(model.CanRun); Assert.Contains("DeepSeek", model.TaskCredentialHint);
        model.TaskProviderIndex = 2; Assert.True(model.CanRun);
        model.TaskProviderIndex = 0; Assert.False(model.CanRun); Assert.Contains("兼容服务", model.TaskCredentialHint);
        model.ConfigureTranslationCommand.Execute(null); model.TranslationKey = "compatible-secret";
        await model.SaveCredentialsCommand.ExecuteAsync(null); model.ShowWorkspaceCommand.Execute(null);
        Assert.True(model.CanRun); Assert.Equal("fixture.mp4", model.InputPath); Assert.True(model.IsOptionsStep);
    }

    [AvaloniaFact]
    public async Task LockedCommandsCannotAdvanceAndUnlockRestoresPersistedKeysOnly()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.NextStepCommand.ExecuteAsync(null);
        model.TranslationKey = "unsaved-draft";
        model.LockCredentialsCommand.Execute(null);
        Assert.False(model.CanRun); Assert.False(model.CanNext); Assert.False(model.CanGoToOptions); Assert.False(model.CanTestEngine);
        Assert.Empty(model.ElevenLabsKey); Assert.Empty(model.TranslationKey);
        model.FileStepCommand.Execute(null); model.OptionsStepCommand.Execute(null); Assert.True(model.IsFileStep);
        await model.RunCommand.ExecuteAsync(null); Assert.Equal(0, fixture.Jobs.Transcriptions);
        fixture.Dialogs.Password = "wrong-password"; await model.ManageMasterPasswordCommand.ExecuteAsync(null);
        Assert.False(model.IsUnlocked); Assert.Contains("主密码不正确", model.CredentialStatus);
        fixture.Dialogs.Password = "test-master-password"; await model.ManageMasterPasswordCommand.ExecuteAsync(null);
        Assert.True(model.CanNext); Assert.Equal("fake-translation", model.TranslationKey);
        await using var fresh = new MainWindowViewModel(new FakeJobs(), fixture.Store, fixture.Dialogs);
        Assert.True(fresh.HasMasterPassword); Assert.False(fresh.IsUnlocked); Assert.Empty(fresh.ElevenLabsKey);
    }

    [AvaloniaFact]
    public async Task ProviderKeysStaySeparateAndRetryUsesOriginalTaskProvider()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.TranslationKey = "deepseek-secret"; model.ProviderIndex = 0; model.TranslationKey = "compatible-secret";
        model.ProviderIndex = 1; Assert.Equal("deepseek-secret", model.TranslationKey);
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        Assert.Equal("deepseek-secret", fixture.Jobs.TranslationCredential);
        model.ProviderIndex = 0; model.CueSelection.Clear(); model.CueSelection.Select(0); await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal("deepseek-secret", fixture.Jobs.TranslationCredential);
        model.TaskProviderIndex = 0; await model.RunCommand.ExecuteAsync(null);
        Assert.Equal("compatible-secret", fixture.Jobs.TranslationCredential);
    }

    [AvaloniaFact]
    public async Task AddressChangeRequiresConfirmationAndSavedSettingsBeforeSendingKey()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); model.ProviderIndex = 0;
        model.Settings.BaseUrl = "https://other.example/v1";
        Assert.True(model.NeedsAddressConfirmation); Assert.False(model.CanTestEngine);
        fixture.Dialogs.Confirm = false; await model.ConfirmCompatibleAddressCommand.ExecuteAsync(null); Assert.False(model.CanTestEngine);
        fixture.Dialogs.Confirm = true; await model.ConfirmCompatibleAddressCommand.ExecuteAsync(null); Assert.True(model.CanTestEngine);
        model.TaskProviderIndex = 0; model.InputPath = "fixture.mp4"; Assert.False(model.CanRun);
        await model.SaveSettingsCommand.ExecuteAsync(null); Assert.True(model.CanRun);
        await model.SaveCredentialsCommand.ExecuteAsync(null); model.LockCredentialsCommand.Execute(null);
        await model.ManageMasterPasswordCommand.ExecuteAsync(null); Assert.True(model.CanTestEngine);
    }

    [AvaloniaFact]
    public async Task LockKeepsCompletedResultsExportableAndBusyTaskRejectsCredentialChanges()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        fixture.Jobs.BlockTranslation = true; model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task;
        Assert.False(model.LockCredentialsCommand.CanExecute(null)); Assert.False(model.SaveCredentialsCommand.CanExecute(null));
        Assert.False(model.ChangeMasterPasswordCommand.CanExecute(null)); Assert.False(model.ResetCredentialsCommand.CanExecute(null));
        model.LockCredentialsCommand.Execute(null); Assert.True(model.IsUnlocked);
        fixture.Jobs.ReleaseTranslation.TrySetResult(); await run;
        model.CueSelection.Clear(); model.CueSelection.Select(0); model.LockCredentialsCommand.Execute(null);
        Assert.True(model.CanExport); Assert.False(model.CanRetryCue); Assert.Equal(2, model.Rows.Count);
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "locked-export.srt"); await model.ExportCommand.ExecuteAsync(null);
        Assert.Contains("中文译文", await File.ReadAllTextAsync(fixture.Dialogs.SavePath));
    }

    [AvaloniaFact]
    public async Task AllProviderKeysAreRedactedAndCannotBeSavedInsideNormalSettings()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.TranslationKey = "hidden-provider-secret"; model.ProviderIndex = 0;
        model.CopyPromptCommand.Execute(null); model.SystemTemplate += "hidden-provider-secret"; await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("包含 API 密钥", model.Error); Assert.False(File.Exists(Path.Combine(fixture.Root, "settings.json")));
        fixture.Jobs.Failure = new ArgumentException("hidden-provider-secret"); model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.DoesNotContain("hidden-provider-secret", model.Error); Assert.DoesNotContain("hidden-provider-secret", model.Diagnostic);
        Assert.All(model.Log, value => Assert.DoesNotContain("hidden-provider-secret", value));
    }

    [AvaloniaFact]
    public async Task ResetCanBeDeclinedAndNewPasswordDoesNotLoseSavedCredentials()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        fixture.Dialogs.Confirm = false; await model.ResetCredentialsCommand.ExecuteAsync(null); Assert.True(model.HasMasterPassword);
        await model.ChangeMasterPasswordCommand.ExecuteAsync(null); model.LockCredentialsCommand.Execute(null);
        await model.ManageMasterPasswordCommand.ExecuteAsync(null); Assert.False(model.IsUnlocked);
        fixture.Dialogs.Password = fixture.Dialogs.NewPassword; await model.ManageMasterPasswordCommand.ExecuteAsync(null);
        Assert.True(model.IsUnlocked); Assert.Equal("fake-eleven", model.ElevenLabsKey);
        fixture.Dialogs.Confirm = true; await model.ResetCredentialsCommand.ExecuteAsync(null);
        Assert.False(model.HasMasterPassword); Assert.False(model.IsUnlocked); Assert.Empty(model.ElevenLabsKey);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task LockedPageShowsDisabledNextAndPasswordDialogRenders(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = new MainWindowViewModel(fixture.Jobs, fixture.Store, fixture.Dialogs);
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show(); await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme; model.InputPath = "fixture.mp4";
        var workspace = window.FindControl<WorkspaceView>("WorkspacePage")!;
        Assert.False(workspace.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
        await Capture(window, "凭证未设置");
        model.ShowSettingsCommand.Execute(null); await Capture(window, "密钥设置锁定");
        foreach (var purpose in Enum.GetValues<PasswordPurpose>())
        {
            var passwordModel = new MasterPasswordViewModel(purpose, (_, _) => Task.CompletedTask);
            var dialog = new MasterPasswordDialog { DataContext = passwordModel, RequestedThemeVariant = theme == "深色" ? ThemeVariant.Dark : ThemeVariant.Light };
            dialog.Show(window); await Capture(dialog, "主密码-" + purpose);
            Assert.True(dialog.FindControl<TextBox>("PasswordBox")!.Bounds.Width > 0);
            passwordModel.CurrentPassword = "中"; passwordModel.Password = "!"; passwordModel.Confirmation = "!";
            await passwordModel.SubmitCommand.ExecuteAsync(null); Assert.Empty(passwordModel.Error);
            dialog.Close(); passwordModel.Clear(); Assert.Empty(passwordModel.Password);
        }
        window.Close();

        static async Task Capture(Window target, string name)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            using var frame = target.CaptureRenderedFrame(); Assert.NotNull(frame);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"{name}-{target.Width}-{target.ActualThemeVariant}.png")); }
        }
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class CredentialAutoSaveTests
{
    [AvaloniaFact]
    public async Task ActualLostFocusSavesAndImmediateSettingsSaveKeepsTheLatestEdit()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model }; window.Show();
        try
        {
            await model.InitializeCommand.ExecuteAsync(null); model.ShowSettingsCommand.Execute(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.PasswordChar == '●');
            var save = view.FindControl<Button>("SettingsSaveButton")!;
            var path = Path.Combine(fixture.Root, "credentials.json"); var before = await File.ReadAllTextAsync(path);
            input.Focus(); input.SetCurrentValue(TextBox.TextProperty, "first-speech-secret"); save.Focus();
            Assert.True(model.SaveSettingsCommand.CanExecute(null));
            for (var attempt = 0; attempt < 100 && await File.ReadAllTextAsync(path) == before; attempt++) await Task.Delay(10);
            using var stored = new CredentialStore(fixture.Root); await stored.UnlockAsync("test-master-password");
            Assert.Equal("first-speech-secret", stored.Values.ElevenLabs);
            input.Focus(); input.SetCurrentValue(TextBox.TextProperty, "second-speech-secret"); save.Focus();
            model.ElevenLabsKey = "latest-speech-secret";
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Empty(model.Error); Assert.Equal("设置已保存", model.SettingsStatus);
            stored.Lock(); await stored.UnlockAsync("test-master-password"); Assert.Equal("latest-speech-secret", stored.Values.ElevenLabs);
            Assert.DoesNotContain("latest-speech-secret", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "settings.json")));
            Assert.DoesNotContain("latest-speech-secret", await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SavingSettingsPersistsAllProviderKeysAndRestartRequiresUnlock()
    {
        using var fixture = new Fixture();
        await using (var model = fixture.Model())
        {
            await model.InitializeCommand.ExecuteAsync(null);
            model.ElevenLabsKey = "new-speech"; model.TranslationKey = "new-deepseek";
            model.ProviderIndex = 0; model.TranslationKey = "new-compatible";
            await model.SaveSettingsCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        }
        await using var fresh = new MainWindowViewModel(fixture.Jobs, fixture.Store, fixture.Dialogs);
        await fresh.InitializeCommand.ExecuteAsync(null); Assert.False(fresh.IsUnlocked); Assert.Empty(fresh.ElevenLabsKey);
        var before = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "credentials.json"));
        await fresh.ManageMasterPasswordCommand.ExecuteAsync(null);
        Assert.Equal("new-speech", fresh.ElevenLabsKey); Assert.Equal("new-compatible", fresh.TranslationKey);
        fresh.ProviderIndex = 1; Assert.Equal("new-deepseek", fresh.TranslationKey);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(fixture.Root, "credentials.json")));
    }

    [AvaloniaFact]
    public async Task FailedCredentialWriteKeepsDraftAndCannotReportSettingsSaved()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        model.TranslationKey = "retryable-secret";
        using (var fileLock = new FileStream(Path.Combine(fixture.Root, "credentials.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.NotEmpty(model.Error); Assert.Equal("retryable-secret", model.TranslationKey); Assert.NotEqual("设置已保存", model.SettingsStatus);
        }
        await model.SaveSettingsCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        using var stored = new CredentialStore(fixture.Root); await stored.UnlockAsync("test-master-password");
        Assert.Equal("retryable-secret", stored.Values.DeepSeek);
    }

    [AvaloniaFact]
    public async Task ModelChoicesAreIndependentOfKeysAndRefreshImmediatelyAfterServiceSave()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        var path = Path.Combine(fixture.Root, "media.mp4"); await File.WriteAllTextAsync(path, "fixture"); await model.AddMediaPathsAsync([path]);
        model.Workspace.SelectedStep = model.Workspace.Steps.Last(); await model.WaitForWorkspaceAsync();
        var selected = model.Workspace.StepModel!.Settings.ModelKey();
        Assert.Contains(model.Workspace.Models, m => m.Settings.Provider == TranslationProvider.DeepSeek);
        model.ProviderIndex = 0; model.Settings.BaseUrl = "https://models.example/v1"; model.Settings.CloudModel = "saved-model";
        model.TranslationKey = "";
        var id = model.Settings.ModelKey(); Assert.DoesNotContain(model.Workspace.Models, m => m.Settings.ModelKey() == id);
        await model.SaveSettingsCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        Assert.Contains(model.Workspace.Models, m => m.Settings.ModelKey() == id);
        Assert.Contains(model.PipelineModels, m => m.Settings.ModelKey() == id);
        Assert.Equal(selected, model.Workspace.StepModel!.Settings.ModelKey());
        await model.LockCredentialsCommand.ExecuteAsync(null);
        Assert.Contains(model.Workspace.Models, m => m.Settings.ModelKey() == id);
        await using var fresh = new MainWindowViewModel(fixture.Jobs, fixture.Store, fixture.Dialogs);
        await fresh.InitializeCommand.ExecuteAsync(null);
        Assert.Empty(fresh.TranslationKey); Assert.Contains(fresh.Workspace.Models, m => m.Settings.ModelKey() == id);
    }
}

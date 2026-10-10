using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class EmbeddedModelWorkflowTests
{
    [AvaloniaFact]
    public async Task ManualModelSelectionRecordsAnyReadableFileWithoutDownloadOrIdentityCheck()
    {
        using var fixture = new Fixture(); var downloads = new Downloads();
        var path = Path.Combine(fixture.Root, "手动任意模型.gguf"); await File.WriteAllTextAsync(path, "不是 GGUF，也没有官方身份");
        fixture.Dialogs.OpenPath = path;
        await using var model = fixture.Model(downloads); model.Settings.Provider = TranslationProvider.Local;
        model.Settings.ModelSha256 = "旧哈希";
        await model.ChooseModelCommand.ExecuteAsync(null);
        Assert.Equal(path, model.Settings.ModelPath); Assert.Empty(model.Settings.ModelSha256);
        Assert.Contains("已选择", model.ModelDownloadStatus); Assert.Empty(model.Error);
        Assert.False(downloads.Started.Task.IsCompleted); Assert.False(model.IsBusy);
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(path, (await fixture.Store.LoadAsync())!.ModelPath);
    }

    [Fact]
    public async Task DefaultsUseConfigurationFolderAndOldSchemaIsBackedUpWithoutMigration()
    {
        using var fixture = new Fixture();
        var fresh = fixture.Store.CreateDefaults();
        Assert.Equal(fixture.Store.DefaultModelPath(EmbeddedModelCatalog.Default), fresh.ModelPath);
        var oldPath = Path.Combine(fixture.Root, "旧模型.gguf"); await File.WriteAllTextAsync(oldPath, "保留模型");
        const string previous = "{\"ModelPath\":\"old.gguf\",\"LocalProfile\":{},\"TranslationConcurrency\":1}";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "settings.json"), previous);
        var loaded = (await fixture.Store.LoadAsync())!;
        Assert.Equal(fresh.ModelPath, loaded.ModelPath); Assert.Equal(4, loaded.TranslationConcurrency);
        Assert.Equal(previous, await File.ReadAllTextAsync(Directory.GetFiles(fixture.Root, "settings.previous-*.json").Single()));
        Assert.Equal("保留模型", await File.ReadAllTextAsync(oldPath));
        Assert.Contains("备份", fixture.Store.LastLoadNotice);
        Assert.Empty(loaded.PromptLibrary); Assert.Equal(2, loaded.SchemaVersion);
    }

    [AvaloniaFact]
    public async Task SwitchingModelsRestoresEachPathAndHashAndSavingDoesNotAlterCompletedJob()
    {
        using var fixture = new Fixture(); var downloads = new Downloads();
        await using var model = fixture.Model(downloads); await model.InitializeCommand.ExecuteAsync(null);
        model.Settings.Provider = TranslationProvider.Local;
        await model.DownloadModelCommand.ExecuteAsync(null);
        var firstPath = model.Settings.ModelPath; var firstHash = model.Settings.ModelSha256;
        Assert.Equal(Path.Combine(fixture.Root, EmbeddedModelCatalog.Default.DefaultFileName), firstPath);
        Assert.True(model.HasUnsavedSettings); Assert.Contains("校验", model.ModelDownloadStatus);
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = (int)TranslationProvider.Local; model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null); Assert.True(model.CanExport);
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Presets[1];
        await model.DownloadModelCommand.ExecuteAsync(null); var secondPath = model.Settings.ModelPath;
        await model.SaveSettingsCommand.ExecuteAsync(null);
        model.CueSelection.Clear(); model.CueSelection.Select(0); await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal(EmbeddedModelCatalog.Default.Id, fixture.Jobs.LastSettings!.LocalModelId); Assert.Equal(firstPath, fixture.Jobs.LastSettings.ModelPath);
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Default;
        Assert.Equal(firstPath, model.Settings.ModelPath); Assert.Equal(firstHash, model.Settings.ModelSha256);
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Presets[1]; Assert.Equal(secondPath, model.Settings.ModelPath);
        await model.SaveSettingsCommand.ExecuteAsync(null);
        var reloaded = await fixture.Store.LoadAsync();
        Assert.Equal(2, reloaded!.ModelFiles.Count); Assert.Equal(secondPath, reloaded.ModelPath);
        model.Settings.ModelPath = "new-path.gguf"; Assert.Empty(model.Settings.ModelSha256);
    }

    [AvaloniaFact]
    public async Task DownloadLocksConfigurationSupportsNavigationAndCancellationAndKeepsEngineStatus()
    {
        using var fixture = new Fixture(); var downloads = new Downloads { Hold = true };
        await using var model = fixture.Model(downloads); model.Settings.Provider = TranslationProvider.Local;
        var status = model.EngineStatus; var run = model.DownloadModelCommand.ExecuteAsync(null);
        await downloads.Started.Task;
        Assert.True(model.IsBusy); Assert.True(model.IsModelDownloading); Assert.False(model.CanConfigure);
        Assert.False(model.DownloadModelCommand.CanExecute(null)); Assert.False(model.TestEngineCommand.CanExecute(null));
        model.ShowLogsCommand.Execute(null); Assert.True(model.IsLogs);
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Presets[1]; Assert.Equal(EmbeddedModelCatalog.Default, model.SelectedEmbeddedModel);
        model.CancelCommand.Execute(null); await run;
        Assert.False(model.IsBusy); Assert.False(model.IsModelDownloading); Assert.Contains("已取消", model.ModelDownloadStatus);
        Assert.Equal(status, model.EngineStatus); Assert.True(model.DownloadModelCommand.CanExecute(null));
        downloads.Hold = false; await model.DownloadModelCommand.ExecuteAsync(null); Assert.Contains("已下载", model.ModelDownloadStatus);
    }

    [AvaloniaFact]
    public async Task ClosingCancelsDownloadBeforeDisposingServices()
    {
        using var fixture = new Fixture(); var downloads = new Downloads { Hold = true };
        var model = fixture.Model(downloads); var run = model.DownloadModelCommand.ExecuteAsync(null);
        await downloads.Started.Task; await model.DisposeAsync(); await run;
        Assert.True(downloads.Cancelled); Assert.True(fixture.Jobs.Disposed); Assert.False(model.CanConfigure);
    }

    [AvaloniaFact]
    public async Task DownloadFailurePreservesSelectionAndShowsActionableError()
    {
        using var fixture = new Fixture(); var downloads = new Downloads { Failure = new HttpRequestException("network") };
        await using var model = fixture.Model(downloads); var path = model.Settings.ModelPath;
        await model.DownloadModelCommand.ExecuteAsync(null);
        Assert.Equal(path, model.Settings.ModelPath); Assert.False(model.HasUnsavedSettings);
        Assert.Contains("网络", model.Error); Assert.DoesNotContain("已下载", model.ModelDownloadStatus);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task ModelPickerAndDownloadProgressRenderInBothThemes(int width, int height, string theme)
    {
        using var fixture = new Fixture(); var downloads = new Downloads { Hold = true };
        await using var model = fixture.Model(downloads);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme; model.Settings.Provider = TranslationProvider.Local;
        model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 1;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        var view = window.FindControl<SettingsView>("SettingsPage")!;
        var selector = view.FindControl<ComboBox>("EmbeddedModelSelector")!;
        Assert.Equal(2, selector.ItemCount); Assert.Equal(EmbeddedModelCatalog.Default, selector.SelectedItem);
        var button = view.FindControl<Button>("DownloadModelButton")!;
        Assert.True(button.IsEffectivelyVisible); Assert.True(button.IsEffectivelyEnabled); Assert.True(button.Bounds.Width > 0);
        var download = model.DownloadModelCommand.ExecuteAsync(null); await downloads.Started.Task;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        var bar = view.FindControl<ProgressBar>("ModelDownloadProgress")!;
        Assert.True(bar.IsEffectivelyVisible); Assert.False(button.IsEffectivelyEnabled);
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.True(bar.Bounds.Height > 0);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"embedded-models-{width}-{theme}.png")); }
        model.CancelCommand.Execute(null); await download; window.Close();
    }

    private sealed class Downloads : IModelDownloadService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold; public bool Cancelled; public Exception? Failure;
        public async Task<EmbeddedModelFile> DownloadAsync(EmbeddedModel model, string root, IProgress<ModelDownloadProgress> progress, CancellationToken token)
        {
            Started.TrySetResult(); progress.Report(new("下载中", 50, 100));
            if (Failure is not null) throw Failure;
            if (Hold)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            var path = Path.Combine(root, model.DefaultFileName); await File.WriteAllTextAsync(path, "GGUF模拟模型", token);
            return new(path, await AtomicFile.HashFileAsync(path, token));
        }
    }
}

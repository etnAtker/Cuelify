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

public sealed class LlamaWorkflowTests
{
    [Fact]
    public async Task NewSettingsKeepIndependentCloudAndLocalConcurrency()
    {
        using var fixture = new Fixture();
        var settings = fixture.Store.CreateDefaults();
        Assert.Equal(4, settings.TranslationConcurrency); Assert.Equal(2, settings.LocalConcurrency);
        Assert.Equal(fixture.Store.DefaultLlamaServerPath, settings.LlamaServerPath);
        settings.Provider = TranslationProvider.Local;
        Assert.Equal(1, settings.TranslationSettings().BatchSize); Assert.Equal(2, settings.TranslationSettings().Concurrency);
        settings.TranslationConcurrency = 1;
        await fixture.Store.SaveAsync(settings, []);
        var loaded = await fixture.Store.LoadAsync();
        Assert.Equal(1, loaded!.TranslationConcurrency); Assert.Equal(2, loaded.LocalConcurrency);
        Assert.Equal(PromptPresets.SingleSimple, loaded.GetProfile());
        Assert.Equal(fixture.Store.DefaultLlamaServerPath, loaded.LlamaServerPath);
    }

    [AvaloniaFact]
    public async Task ManualSelectionIsInspectedAndSavedWithoutDownloading()
    {
        using var fixture = new Fixture(); var packages = new Packages();
        fixture.Dialogs.OpenPath = Path.Combine(fixture.Root, "手动 程序", "llama-server.exe");
        await using var model = fixture.Model(packages: packages);
        await model.ChooseLlamaServerCommand.ExecuteAsync(null);
        Assert.Equal(fixture.Dialogs.OpenPath, model.Settings.LlamaServerPath);
        Assert.Equal("test-version", model.Settings.LlamaServerVersion); Assert.False(packages.Started.Task.IsCompleted);
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(fixture.Dialogs.OpenPath, (await fixture.Store.LoadAsync())!.LlamaServerPath);
    }

    [AvaloniaFact]
    public async Task DownloadUpdatesDraftAndSavePersistsRuntimeWithSnapshotsIndependent()
    {
        using var fixture = new Fixture(); var packages = new Packages();
        await using var model = fixture.Model(packages: packages);
        model.Settings.Provider = TranslationProvider.Local;
        var snapshot = ConfigurationStore.Snapshot(model.Settings);
        await model.DownloadLlamaCommand.ExecuteAsync(null);
        Assert.Equal(packages.InstalledPath, model.Settings.LlamaServerPath);
        Assert.Equal("test-version", model.Settings.LlamaServerVersion); Assert.True(model.HasUnsavedSettings);
        Assert.Equal(fixture.Store.DefaultLlamaServerPath, snapshot.LlamaServerPath);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "settings.json")));
        model.Settings.TranslationConcurrency = 4; model.Settings.LocalConcurrency = 3;
        await model.SaveSettingsCommand.ExecuteAsync(null);
        var loaded = await fixture.Store.LoadAsync();
        Assert.Equal(packages.InstalledPath, loaded!.LlamaServerPath);
        Assert.Equal(4, loaded.TranslationConcurrency); Assert.Equal(3, loaded.LocalConcurrency);
    }

    [AvaloniaFact]
    public async Task DownloadCanCancelNavigateAndCloseWithoutChangingRuntime()
    {
        using var fixture = new Fixture(); var packages = new Packages { Hold = true };
        await using var model = fixture.Model(packages: packages);
        var originalPath = model.Settings.LlamaServerPath; var originalStatus = model.EngineStatus;
        var download = model.DownloadLlamaCommand.ExecuteAsync(null);
        await packages.Started.Task;
        Assert.True(model.IsLlamaInstalling); Assert.False(model.CanConfigure); Assert.False(model.TestEngineCommand.CanExecute(null));
        model.ShowLogsCommand.Execute(null); Assert.True(model.IsLogs);
        model.CancelCommand.Execute(null); await download;
        Assert.Equal(originalPath, model.Settings.LlamaServerPath); Assert.Equal(originalStatus, model.EngineStatus);
        Assert.Contains("已取消", model.LlamaInstallStatus); Assert.True(model.CanConfigure);
        download = model.DownloadLlamaCommand.ExecuteAsync(null);
        await model.DisposeAsync(); await download;
        Assert.True(packages.Cancelled); Assert.True(fixture.Jobs.Disposed);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task RuntimeControlsAndProgressRenderWithExistingModelPicker(int width, int height, string theme)
    {
        using var fixture = new Fixture(); var packages = new Packages { Hold = true };
        await using var model = fixture.Model(packages: packages);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        await model.InitializeCommand.ExecuteAsync(null);
        model.Settings.Provider = TranslationProvider.Local; model.Settings.Theme = theme;
        model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 1;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        var view = window.FindControl<SettingsView>("SettingsPage")!;
        Assert.True(view.FindControl<Button>("DownloadLlamaButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<ComboBox>("EmbeddedModelSelector")!.IsEffectivelyVisible);
        Assert.Equal(2m, view.FindControl<NumericUpDown>("InferenceConcurrency")!.Value);
        var download = model.DownloadLlamaCommand.ExecuteAsync(null); await packages.Started.Task;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.True(view.FindControl<ProgressBar>("LlamaInstallProgress")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("DownloadLlamaButton")!.IsEffectivelyEnabled);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"llama-runtime-{width}-{theme}.png")); }
        model.CancelCommand.Execute(null); await download; window.Close();
    }

    [AvaloniaFact]
    public async Task FailedDownloadPreservesRuntimeAndShowsNetworkError()
    {
        using var fixture = new Fixture(); var packages = new Packages { Failure = new HttpRequestException("network") };
        await using var model = fixture.Model(packages: packages); var original = model.Settings.LlamaServerPath;
        await model.DownloadLlamaCommand.ExecuteAsync(null);
        Assert.Equal(original, model.Settings.LlamaServerPath); Assert.Contains("网络", model.Error); Assert.False(model.HasUnsavedSettings);
    }

    private sealed class Packages : ILlamaPackageService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold; public bool Cancelled; public Exception? Failure; public string InstalledPath = "";
        public async Task<LlamaPackageInstallation> DownloadAsync(string root, IProgress<ModelDownloadProgress> progress, CancellationToken token)
        {
            Started.TrySetResult(); progress.Report(new("下载中", 50, 100));
            if (Failure is not null) throw Failure;
            if (Hold)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            InstalledPath = Path.Combine(root, "llama.cpp", "test-version", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledPath)!); await File.WriteAllTextAsync(InstalledPath, "test-package", token);
            return new(InstalledPath, "test-version", "test-hash");
        }
        public Task<LlamaServerBinary> InspectAsync(string path, CancellationToken token) => Task.FromResult(new LlamaServerBinary(path, "test-version", "test-runtime", "Vulkan0"));
    }
}

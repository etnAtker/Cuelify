using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class InferenceUiTests
{
    [AvaloniaFact]
    public async Task LlamaModelLoadFailureShowsActionableErrorAndPreservesNativeDiagnostic()
    {
        using var fixture = new Fixture();
        fixture.Jobs.PreparationFailure = new LocalTranslationException("ModelLoad", "加载失败", "llama_model_loader: invalid magic\nfailed to load model fake-translation");
        await using var model = fixture.Model(); model.Settings.Provider = TranslationProvider.Local;
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = 2; model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.Contains("llama.cpp 无法加载模型", model.Error);
        Assert.DoesNotContain("invalid magic", model.Error);
        Assert.Contains(model.Log, line => line.Contains("invalid magic"));
        Assert.DoesNotContain(model.Log, line => line.Contains("fake-translation"));
        Assert.DoesNotContain(model.Log, line => line.Contains("校验模型"));
        Assert.Equal(0, fixture.Jobs.Transcriptions);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task UnifiedControlsKeepIndependentValuesAndOptionalCloudOutput(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme;
        model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 1;
        var view = window.FindControl<SettingsView>("SettingsPage")!;
        var concurrency = view.FindControl<NumericUpDown>("InferenceConcurrency")!;
        var cloudTokens = view.FindControl<NumericUpDown>("CloudOutputTokens")!;
        var localTokens = view.FindControl<NumericUpDown>("LocalOutputTokens")!;
        await Flush(); Assert.Equal(4m, concurrency.Value); Assert.Equal(2048m, cloudTokens.Value);
        concurrency.Value = 6; cloudTokens.Text = ""; await Flush();
        Assert.Equal(6, model.Settings.TranslationConcurrency); Assert.Equal("", model.Settings.MaximumTokens);
        Assert.Null(model.Settings.CloudOptions().MaximumTokens);
        model.ProviderIndex = 2; await Flush();
        Assert.Equal(2m, concurrency.Value); Assert.Equal(256m, localTokens.Value);
        Assert.False(cloudTokens.IsEffectivelyVisible); Assert.True(localTokens.IsEffectivelyVisible);
        concurrency.Value = 3; localTokens.Value = 2000;
        model.Settings.ContextSize = 512; await Flush();
        Assert.Equal(256m, localTokens.Maximum); Assert.Equal(256m, localTokens.Value);
        model.Settings.ContextSize = 32768; await Flush(); Assert.Equal(16384m, localTokens.Maximum);
        localTokens.Value = 8000;
        model.ProviderIndex = 1; await Flush(); Assert.Equal(6m, concurrency.Value); Assert.Null(cloudTokens.Value);
        cloudTokens.Value = 4096;
        await model.SaveSettingsCommand.ExecuteAsync(null);
        var loaded = (await fixture.Store.LoadAsync())!;
        Assert.Equal(6, loaded.TranslationConcurrency); Assert.Equal(3, loaded.LocalConcurrency);
        Assert.Equal("4096", loaded.MaximumTokens); Assert.Equal(8000, loaded.LocalMaximumTokens);
        Assert.Equal(4096, loaded.CloudOptions().MaximumTokens);
        Assert.Contains("限流", model.ConcurrencyHint);
        cloudTokens.BringIntoView(); await Flush(); Capture(window, $"inference-cloud-{width}-{theme}.png");
        model.ProviderIndex = 2; await Flush(); Assert.Equal(3m, concurrency.Value); Assert.Equal(8000m, localTokens.Value);
        Assert.Contains("显存", model.ConcurrencyHint); Assert.Contains("5 分钟", model.TimeoutHint);
        localTokens.BringIntoView(); await Flush(); Capture(window, $"inference-local-{width}-{theme}.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task PreparingIsVisibleOnOptionsAndCancellingStopsBeforeRecognition(int width, int height, string theme)
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockPreparation = true;
        await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        model.Settings.Provider = TranslationProvider.Local; model.Settings.Theme = theme;
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = 2;
        model.InputPath = "fixture.mp4"; await model.NextStepCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.PreparationStarted.Task; await Flush();
        Assert.True(model.IsOptionsStep); Assert.True(model.IsPreparing); Assert.True(model.IsBusy); Assert.False(model.CanConfigure);
        Assert.Equal("正在加载本地模型…", model.PreparationStatus); Assert.StartsWith("已用时", model.PreparationElapsed);
        Assert.Contains(model.Log, line => line.Contains("正在检查 llama.cpp 运行程序：已完成（耗时"));
        var panel = window.FindControl<WorkspaceView>("WorkspacePage")!.FindControl<PreparationStatusView>("WorkspacePreparation")!;
        var message = panel.FindControl<TextBlock>("PreparationMessage")!;
        Assert.True(message.IsEffectivelyVisible); Assert.True(message.Bounds.Height > 0);
        Capture(window, $"preparing-{width}-{theme}.png");
        model.ShowLogsCommand.Execute(null); Assert.True(model.IsPreparing);
        model.CancelCommand.Execute(null); Assert.Equal("正在取消…", model.PreparationStatus);
        await run; Assert.False(model.IsPreparing); Assert.False(model.IsBusy); Assert.False(model.CanContinue);
        Assert.Equal(0, fixture.Jobs.Transcriptions); Assert.Equal("已取消", model.Status);
        fixture.Jobs.PreparationProgress!.Report(new(LocalPreparationStage.Ready)); await Flush();
        Assert.Equal("已取消", model.Status); Assert.Contains(model.Log, line => line.Contains("准备已取消"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PreparationCompletionCannotOverwriteRecognitionOrFinalStatus()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockPreparation = true;
        await using var model = fixture.Model(); model.Settings.Provider = TranslationProvider.Local;
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = 2; model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.PreparationStarted.Task; await Flush();
        fixture.Jobs.ReleasePreparation.TrySetResult(); await run; await Flush();
        Assert.True(model.CanExport); Assert.Equal("翻译完成，可以导出字幕", model.Status);
        Assert.False(model.IsPreparing); Assert.Equal(1, fixture.Jobs.Transcriptions);
        Assert.Contains(model.Log, line => line.Contains("本地服务已就绪"));
        Assert.Contains(model.Log, line => line.Contains("正在检查媒体"));
        Assert.Contains(model.Log, line => line.Contains("开始识别语音"));
        fixture.Jobs.PreparationProgress!.Report(new(LocalPreparationStage.LoadingModel)); await Flush();
        Assert.Equal("翻译完成，可以导出字幕", model.Status);
    }

    [AvaloniaFact]
    public async Task PreparationFailureKeepsOptionsAndAllowsRetry()
    {
        using var fixture = new Fixture(); fixture.Jobs.PreparationFailure = new IOException("模拟加载失败");
        await using var model = fixture.Model(); model.Settings.Provider = TranslationProvider.Local;
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = 2;
        model.InputPath = "fixture.mp4"; await model.NextStepCommand.ExecuteAsync(null); await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.IsOptionsStep); Assert.False(model.IsPreparing); Assert.True(model.CanConfigure);
        Assert.False(model.CanContinue); Assert.Equal(0, fixture.Jobs.Transcriptions); Assert.NotEmpty(model.Error);
        Assert.Contains(model.Log, line => line.Contains("准备失败"));
        fixture.Jobs.PreparationFailure = null; await model.RunCommand.ExecuteAsync(null); Assert.True(model.CanExport);
    }

    [AvaloniaFact]
    public async Task LocalTestSharesPreparationFeedbackAndKeepsFinalResult()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockPreparation = true;
        await using var model = fixture.Model(); model.Settings.Provider = TranslationProvider.Local;
        model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 1;
        var run = model.TestEngineCommand.ExecuteAsync(null); await fixture.Jobs.PreparationStarted.Task; await Flush();
        Assert.True(model.IsPreparing); Assert.Equal("正在加载本地模型…", model.EngineStatus);
        fixture.Jobs.ReleasePreparation.TrySetResult(); await run; await Flush();
        Assert.False(model.IsPreparing); Assert.Equal("连接成功，翻译测试通过", model.EngineStatus);
        Assert.Contains(model.Log, line => line.Contains("正在试译测试短句"));
        fixture.Jobs.PreparationProgress!.Report(new(LocalPreparationStage.Ready)); await Flush();
        Assert.Equal("连接成功，翻译测试通过", model.EngineStatus);
    }

    [AvaloniaFact]
    public async Task MediaCheckingHasFeedbackAndCanBeCancelledOnFirstStep()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockProbe = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        var next = model.NextStepCommand.ExecuteAsync(null); await fixture.Jobs.ProbeStarted.Task;
        Assert.True(model.IsPreparing); Assert.Equal("正在检查媒体…", model.PreparationStatus);
        model.CancelCommand.Execute(null); await next;
        Assert.True(model.IsFileStep); Assert.False(model.IsPreparing); Assert.True(model.CanNext);
        Assert.Contains(model.Log, line => line.Contains("准备已取消"));
    }

    [AvaloniaFact]
    public async Task ClosingDuringPreparationStopsFeedbackAndDisposesServices()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockPreparation = true;
        var model = fixture.Model(); model.Settings.Provider = TranslationProvider.Local;
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = 2; model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.PreparationStarted.Task;
        await model.DisposeAsync(); await run;
        Assert.False(model.IsPreparing); Assert.False(model.IsBusy); Assert.True(fixture.Jobs.Disposed);
        Assert.Equal(0, fixture.Jobs.Transcriptions);
    }

    private static async Task Flush() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var root = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(root)) { Directory.CreateDirectory(root); frame!.Save(Path.Combine(root, name)); }
    }
}

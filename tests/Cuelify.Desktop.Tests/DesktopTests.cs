using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Cuelify.Core.Media;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Cuelify.Desktop.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cuelify.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task LogSelectionCopiesSingleMultipleAndAllEntriesInDisplayOrder()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = 1240, Height = 860 };
        window.Show(); await model.InitializeCommand.ExecuteAsync(null); model.Log.Clear(); model.ShowLogsCommand.Execute(null);
        model.Log.Add("第一条\n调用栈"); model.Log.Add("第二条"); model.Log.Add("第三条");
        var list = window.FindControl<LogsView>("LogsPage")!.FindControl<ListBox>("LogList")!;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.False(model.CopyLogsCommand.CanExecute(null));
        list.Selection.Select(2);
        await model.CopyLogsCommand.ExecuteAsync(null);
        Assert.Equal("第三条", fixture.Dialogs.CopiedText);
        list.Selection.Select(0);
        Assert.Equal(2, model.LogSelection.Count);
        Assert.True(list.Focus()); window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        if (model.CopyLogsCommand.ExecutionTask is { } copying) await copying;
        Assert.Equal("第一条\n调用栈" + Environment.NewLine + "第三条", fixture.Dialogs.CopiedText);
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Equal(3, model.LogSelection.Count);
        await model.CopyLogsCommand.ExecuteAsync(null);
        Assert.Equal(string.Join(Environment.NewLine, model.Log), fixture.Dialogs.CopiedText);
        model.LogSelection.Clear(); Assert.False(model.CopyLogsCommand.CanExecute(null));
        model.Log.Add("第二条"); list.Selection.Select(1);
        await model.CopyLogsCommand.ExecuteAsync(null);
        Assert.Equal("第二条", fixture.Dialogs.CopiedText);
        model.Log.RemoveAt(0); await model.CopyLogsCommand.ExecuteAsync(null);
        Assert.Equal("第二条", fixture.Dialogs.CopiedText);
        model.LogSelection.Clear();
        using (var frame = window.CaptureRenderedFrame()) Assert.NotNull(frame);
        Click(0, RawInputModifiers.None); Click(2, RawInputModifiers.Control);
        Assert.Equal(new[] { 0, 2 }, model.LogSelection.SelectedIndexes);
        Click(1, RawInputModifiers.Shift);
        Assert.Equal(new[] { 1, 2 }, model.LogSelection.SelectedIndexes);
        window.Close();

        void Click(int index, RawInputModifiers modifiers)
        {
            var item = list.ContainerFromIndex(index)!;
            var point = item.TranslatePoint(new Point(8, 8), window)!.Value;
            window.MouseDown(point, MouseButton.Left, modifiers);
            window.MouseUp(point, MouseButton.Left, modifiers);
        }
    }

    [AvaloniaTheory]
    [InlineData("日语", "jpn", "日语")]
    [InlineData("ja-JP", "jpn", "日语")]
    [InlineData("自动识别", "", "自动识别")]
    public async Task OneSourceLanguageDrivesRecognitionAndTranslation(string input, string code, string name)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; model.TaskSourceLanguage = input;
        await model.RunCommand.ExecuteAsync(null);
        Assert.Equal(code, fixture.Jobs.RecognitionSettings!.SourceCode);
        Assert.Equal(name, fixture.Jobs.LastSettings!.SourceLanguage);
        Assert.True(model.CanExport);
        model.Settings.SourceLanguage = "英语"; await model.SaveSettingsCommand.ExecuteAsync(null);
        model.SelectedCue = model.Rows[0]; await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal(name, fixture.Jobs.LastSettings!.SourceLanguage);
    }

    [AvaloniaFact]
    public async Task InvalidSourceLanguageDoesNotStartPaidRecognition()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; model.TaskSourceLanguage = "无效语言";
        await model.RunCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Jobs.Transcriptions); Assert.Contains("源语言无效", model.Error);
    }

    [AvaloniaFact]
    public async Task LegacyLanguageCodeMigratesWithoutChangingPaidCacheIdentity()
    {
        using var fixture = new Fixture();
        await AtomicFile.WriteJsonAsync(Path.Combine(fixture.Root, "settings.json"),
            new AppSettings { SourceCode = "ja", SourceLanguage = "英语" }, CancellationToken.None);
        await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        Assert.Equal("日语", model.DefaultSourceLanguage); Assert.Equal("日语", model.TaskSourceLanguage);
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        Assert.Equal("ja", fixture.Jobs.RecognitionSettings!.SourceCode);
        Assert.Equal("日语", fixture.Jobs.LastSettings!.SourceLanguage);
        model.DefaultSourceLanguage = "英语"; await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal("eng", model.Settings.SourceCode);
        model.NewTaskCommand.Execute(null); Assert.Equal("英语", model.TaskSourceLanguage);
    }

    [AvaloniaFact]
    public async Task FileProbeAndSettingsRoundTripPreserveTaskChoicesWithoutStartingRecognition()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        Assert.False(model.CanGoToOptions); Assert.True(model.CanAcceptDrop);
        model.InputPath = "fixture.mp4";
        await model.NextStepCommand.ExecuteAsync(null);
        Assert.True(model.IsOptionsStep); Assert.Equal(0, fixture.Jobs.Transcriptions);
        Assert.False(model.CanGoToResults); Assert.False(model.CanAcceptDrop);
        model.TaskTargetLanguage = "日文"; model.TaskProviderIndex = 0;
        model.ConfigurePromptCommand.Execute(null);
        Assert.True(model.IsSettings); Assert.Equal(2, model.SettingsSectionIndex);
        Assert.Equal(0, model.ProviderIndex);
        model.ShowWorkspaceCommand.Execute(null);
        Assert.True(model.IsOptionsStep); Assert.Equal("日文", model.TaskTargetLanguage);
        Assert.Equal("fixture.mp4", model.InputPath); Assert.Equal(0, model.TaskProviderIndex);
        model.FileStepCommand.Execute(null); Assert.True(model.CanAcceptDrop);
        model.OptionsStepCommand.Execute(null); Assert.True(model.IsOptionsStep);
    }

    [AvaloniaFact]
    public async Task NavigationDuringProcessingKeepsTaskAliveAndLocksOptions()
    {
        using var fixture = new Fixture(); fixture.Jobs.Block = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.Started.Task;
        Assert.True(model.IsResultsStep); Assert.True(model.IsProcessing);
        Assert.False(model.OptionsStepCommand.CanExecute(null)); Assert.False(model.CanAcceptDrop);
        model.ShowSettingsCommand.Execute(null); Assert.True(model.IsSettings); Assert.False(model.CanConfigure);
        model.ShowLogsCommand.Execute(null); Assert.True(model.IsLogs); Assert.True(model.IsBusy);
        model.ShowWorkspaceCommand.Execute(null); Assert.True(model.IsResultsStep);
        model.CancelCommand.Execute(null); await run;
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.True(model.CanConfigure);
    }

    [AvaloniaFact]
    public async Task InvalidMediaStaysOnFileStepAndMissingProbeToolOpensSpeechSettings()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4";
        fixture.Jobs.ProbeFailure = new InvalidDataException("无法读取媒体信息，请检查文件。");
        await model.NextStepCommand.ExecuteAsync(null);
        Assert.True(model.IsWorkspace); Assert.True(model.IsFileStep); Assert.False(model.CanGoToOptions);
        Assert.Contains("媒体信息", model.Error); Assert.Equal(0, fixture.Jobs.Transcriptions);
        fixture.Jobs.ProbeFailure = new System.ComponentModel.Win32Exception(2);
        await model.NextStepCommand.ExecuteAsync(null);
        Assert.True(model.IsSettings); Assert.Equal(0, model.SettingsSectionIndex);
        model.ShowWorkspaceCommand.Execute(null); Assert.True(model.IsFileStep);
    }

    [AvaloniaTheory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task MissingCredentialDisablesRunAndConfigurationPreservesOptions(bool missingSpeech, int section)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.NextStepCommand.ExecuteAsync(null);
        model.TaskProviderIndex = 0; model.ProviderIndex = 0;
        if (missingSpeech) model.ElevenLabsKey = ""; else model.TranslationKey = "";
        await model.RunCommand.ExecuteAsync(null);
        Assert.False(model.RunCommand.CanExecute(null)); Assert.False(model.IsSettings);
        Assert.Equal(0, fixture.Jobs.Transcriptions); Assert.Contains("API 密钥", model.TaskCredentialHint);
        if (missingSpeech) model.ConfigureSpeechCommand.Execute(null); else model.ConfigureTranslationCommand.Execute(null);
        Assert.True(model.IsSettings); Assert.Equal(section, model.SettingsSectionIndex);
        if (!missingSpeech) Assert.Equal(0, model.ProviderIndex);
        model.ShowWorkspaceCommand.Execute(null); Assert.True(model.IsOptionsStep);
    }

    [AvaloniaFact]
    public async Task ExportAndPreviewUseCompletedTaskSnapshotAfterSettingsChange()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        model.Settings.TargetLanguage = "日文"; model.Settings.CloudModel = "changed-model";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "output.srt");
        await model.ExportCommand.ExecuteAsync(null);
        Assert.Equal("fixture.中文.srt", fixture.Dialogs.SuggestedName);
        model.PreviewRequestCommand.Execute(null);
        Assert.Equal("中文", fixture.Jobs.PreviewSettings!.TargetLanguage);
        Assert.Equal("deepseek-flash", fixture.Jobs.PreviewSettings.CloudModel);
        model.NewTaskCommand.Execute(null);
        Assert.Empty(model.Rows); Assert.True(model.IsFileStep); Assert.False(model.CanExport);
        Assert.Equal("日文", model.TaskTargetLanguage);
    }

    [AvaloniaFact]
    public async Task CompletedBatchDisplaysWhileNextBatchRunsAndSurvivesCancellation()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockTranslation = true; fixture.Jobs.ReportCompletedBatch = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(model.IsBusy); Assert.Equal("首批译文", model.Rows[0].Translation);
        Assert.Equal("翻译中…", model.Rows[1].Translation);
        model.CancelCommand.Execute(null); await run;
        Assert.Equal("首批译文", model.Rows[0].Translation); Assert.Equal("已取消", model.Rows[1].Translation);
        Assert.False(model.CanExport); Assert.True(model.CanResume);
    }

    [AvaloniaFact]
    public async Task InterleavedBatchProgressCannotEraseAnAlreadyDisplayedTranslation()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockTranslation = true;
        fixture.Jobs.ReportCompletedBatch = true; fixture.Jobs.ReportInterleavedBatch = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Equal("首批译文", model.Rows[0].Translation); Assert.Equal("后批译文", model.Rows[1].Translation);
        Assert.False(model.CanExport);
        model.CancelCommand.Execute(null); await run;
        Assert.Equal("首批译文", model.Rows[0].Translation); Assert.Equal("后批译文", model.Rows[1].Translation);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task WorkflowAndSettingsPagesRenderAtSupportedSizes(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show(); await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme;
        var workspace = window.FindControl<WorkspaceView>("WorkspacePage")!;
        await Capture("选择文件");
        var next = workspace.FindControl<Button>("NextButton")!;
        Assert.True(next.IsEffectivelyVisible); Assert.False(next.IsEffectivelyEnabled);
        model.InputPath = "fixture.mp4"; await model.NextStepCommand.ExecuteAsync(null);
        await Capture("翻译选项");
        Assert.Equal("自动识别", workspace.FindControl<ComboBox>("SourceLanguageBox")!.SelectedItem);
        Assert.True(workspace.FindControl<Button>("RunButton")!.IsEffectivelyVisible);
        model.ShowSettingsCommand.Execute(null);
        for (var section = 0; section < 4; section++) { model.SettingsSectionIndex = section; await Capture("设置-" + section); }
        model.ShowLogsCommand.Execute(null); await Capture("日志");
        window.Close();

        async Task Capture(string name)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var page = model.IsWorkspace ? (Control)workspace : model.IsSettings ? window.FindControl<SettingsView>("SettingsPage")! : window.FindControl<LogsView>("LogsPage")!;
            Assert.True(page.IsEffectivelyVisible); Assert.Equal(width - 80, page.Bounds.Width);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"{name}-{width}-{theme}.png")); }
        }
    }
    [Fact]
    public async Task ConfigurationPersistsProfilesWithoutKeys()
    {
        using var fixture = new Fixture();
        var settings = new AppSettings { LocalProfile = PromptPresets.Local with { Name = "我的本地模板" }, TargetLanguage = "日文" };
        await fixture.Store.SaveAsync(settings, ["secret-eleven", "secret-ds"]);
        var loaded = await fixture.Store.LoadAsync();
        Assert.Equal("日文", loaded!.TargetLanguage);
        Assert.Equal("我的本地模板", loaded.LocalProfile.Name);
        var text = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "settings.json"));
        Assert.DoesNotContain("secret-eleven", text); Assert.DoesNotContain("secret-ds", text);
        Assert.DoesNotContain("ApiKey", text); Assert.DoesNotContain("TranslationKey", text);
    }
    [Theory]
    [InlineData("{\"api_key\":\"hidden\"}")]
    [InlineData("{\"extra\":{\"Authorization\":\"hidden\"}}")]
    public async Task CredentialsInsideAdvancedSettingsAreRejectedEvenForLocal(string json)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(new AppSettings { Provider = TranslationProvider.Local, AdvancedJson = json }, []));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "settings.json")));
    }
    [Fact]
    public async Task AccidentallyPastedCurrentKeyCannotBeSavedInPrompt()
    {
        using var fixture = new Fixture();
        var settings = new AppSettings { DeepSeekProfile = PromptPresets.Cloud with { SystemTemplate = "secret-from-session" } };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(settings, ["secret-from-session"]));
    }
    [AvaloniaFact]
    public async Task SavedDefaultsDoNotInvalidateCompletedJobAndNewTaskUsesSelectedLanguage()
    {
        using var fixture = new Fixture();
        await using var model = fixture.Model();
        model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport); Assert.Equal(2, model.Rows.Count);
        Assert.Equal(FakeJobs.Cues[1].Start, model.Rows[1].Cue.Start);
        model.Settings.TargetLanguage = "日文";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(model.CanExport);
        model.SelectedCue = model.Rows[1]; Assert.True(model.CanRetryCue);
        await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal("中文", fixture.Jobs.LastSettings!.TargetLanguage);
        model.TaskTargetLanguage = "日文";
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport); Assert.Equal("日文", fixture.Jobs.LastSettings!.TargetLanguage);
    }
    [AvaloniaFact]
    public async Task UnsavedPromptAndInvalidSettingsLeaveCompletedTranslationExportable()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        model.UserTemplate += "\n新的风格";
        Assert.True(model.CanExport); Assert.True(model.HasUnsavedSettings);
        model.Settings.Temperature = "invalid";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("数值格式", model.Error);
        Assert.False(model.IsBusy); Assert.True(model.CanExport);
        await model.RunCommand.ExecuteAsync(null);
        Assert.Empty(model.Error); Assert.Equal("", fixture.Jobs.LastSettings!.Temperature);
    }
    [AvaloniaFact]
    public async Task ConcurrentRunIsIgnoredAndCancelLeavesRecoveryAvailable()
    {
        using var fixture = new Fixture(); fixture.Jobs.Block = true; await using var model = fixture.Model();
        model.InputPath = "fixture.mp4";
        var first = model.RunCommand.ExecuteAsync(null);
        await fixture.Jobs.Started.Task;
        Assert.True(model.IsBusy); Assert.False(model.CanExport);
        await model.RunCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Jobs.Transcriptions);
        model.CancelCommand.Execute(null); await first;
        Assert.Contains("已取消", model.Status); Assert.True(model.CanRun);
        fixture.Jobs.Block = false;
        await model.RunCommand.ExecuteAsync(null); Assert.True(model.CanExport);
    }
    [AvaloniaFact]
    public async Task PartialTranslationDisablesExportAndTargetedRetryUsesOnlySelectedId()
    {
        using var fixture = new Fixture(); fixture.Jobs.Partial = true; await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        Assert.False(model.CanExport); Assert.Contains("1 条字幕翻译失败", model.Error);
        Assert.DoesNotContain("cue-2", model.Error); Assert.Equal("翻译失败", model.Rows[1].Translation);
        Assert.Equal("继续处理", model.RunButtonText); Assert.Equal(2, model.Rows[1].Number);
        model.SelectedCue = model.Rows[1]; Assert.True(model.CanRetryCue);
        fixture.Jobs.Partial = false;
        await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "cue-2" }, fixture.Jobs.Forced!.ToArray());
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.True(model.CanExport);
        Assert.Equal("开始处理", model.RunButtonText);
    }

    [AvaloniaFact]
    public async Task EngineStatusFollowsProviderAndChangedSettingsInvalidateTestResults()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        Assert.Equal("尚未测试连接", model.EngineStatus);
        await model.TestEngineCommand.ExecuteAsync(null);
        Assert.Equal("连接成功，翻译测试通过", model.EngineStatus);
        model.Settings.CloudModel = "another-model";
        Assert.Equal("尚未测试连接", model.EngineStatus);
        model.ProviderIndex = 2;
        Assert.Equal("尚未测试本地翻译", model.EngineStatus);
        Assert.Equal("测试本地翻译", model.TestEngineButtonText);
        model.ProviderIndex = 0;
        Assert.Equal("尚未测试连接", model.EngineStatus);
        await model.TestEngineCommand.ExecuteAsync(null);
        model.TranslationKey = "changed-key";
        Assert.Equal("尚未测试连接", model.EngineStatus);
    }

    [AvaloniaTheory]
    [InlineData("HTTP 401", "认证失败")]
    [InlineData("HTTP 403", "没有权限")]
    [InlineData("HTTP 429", "额度")]
    [InlineData("HTTP 503", "暂时不可用")]
    [InlineData("Local:GpuMemory", "显存不足")]
    [InlineData("Local:VulkanOffload", "显卡驱动")]
    public async Task PartialTranslationReportsTheActualFailureCategory(string reason, string expected)
    {
        using var fixture = new Fixture(); fixture.Jobs.Partial = true; fixture.Jobs.FailureReason = reason;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.Contains(expected, model.Error); Assert.DoesNotContain("cue-2", model.Error);
        Assert.Equal("翻译失败", model.Rows[1].Translation); Assert.Equal("中文译文", model.Rows[0].Translation);
    }

    [AvaloniaFact]
    public async Task MissingAsrKeyAndInvalidSettingsDoNotSuggestCacheRecovery()
    {
        using var fixture = new Fixture();
        fixture.Jobs.Failure = new TranscriptionFailedException([0]) { Categories = new Dictionary<int, string> { [0] = "请填写有效的ElevenLabs API 密钥。" } };
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.Contains("ElevenLabs API 密钥", model.Error);
        Assert.DoesNotContain("InvalidOperationException", model.Error);
        Assert.DoesNotContain("从缓存恢复", model.Status);
        model.Settings.Temperature = "bad";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("温度数值格式", model.Error);
        Assert.True(model.HasUnsavedSettings);
    }

    [AvaloniaFact]
    public async Task ActiveSubtitleStateBecomesCancelledAndCanContinue()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockTranslation = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null);
        await fixture.Jobs.TranslationStarted.Task;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Equal("翻译中…", model.Rows[0].Translation);
        Assert.Equal("等待翻译", model.Rows[1].Translation);
        model.CancelCommand.Execute(null); await run;
        Assert.Equal("已取消", model.Rows[0].Translation);
        Assert.Equal("等待翻译", model.Rows[1].Translation);
        Assert.Equal("继续处理", model.RunButtonText);
        fixture.Jobs.BlockTranslation = false;
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport);
    }

    [AvaloniaFact]
    public async Task ActiveSubtitleStateBecomesFailedWhenTranslationThrows()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockTranslation = true;
        fixture.Jobs.TranslationFailure = new HttpRequestException("connection failed");
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        var run = model.RunCommand.ExecuteAsync(null);
        await fixture.Jobs.TranslationStarted.Task;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        fixture.Jobs.ReleaseTranslation.TrySetResult(); await run;
        Assert.Equal("翻译失败", model.Rows[0].Translation);
        Assert.Equal("等待翻译", model.Rows[1].Translation);
        Assert.Contains("检查网络", model.Error); Assert.DoesNotContain("HttpRequestException", model.Error);
        Assert.Equal("继续处理", model.RunButtonText);
    }

    [AvaloniaFact]
    public async Task FailedEngineTestClearsPreviousSuccessfulStatus()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        await model.TestEngineCommand.ExecuteAsync(null);
        fixture.Jobs.EngineFailure = new TranslationServiceException(System.Net.HttpStatusCode.Unauthorized, null);
        await model.TestEngineCommand.ExecuteAsync(null);
        Assert.Equal("测试失败", model.EngineStatus);
        Assert.Contains("API 密钥", model.Error);
    }
    [AvaloniaFact]
    public async Task ServiceTestsKeepCompletedTaskStatusAndExportAvailable()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        var completedStatus = model.Status;
        await model.TestEngineCommand.ExecuteAsync(null);
        Assert.Equal(completedStatus, model.Status); Assert.True(model.CanExport);
        fixture.Jobs.EngineFailure = new HttpRequestException("test connection failed");
        await model.TestEngineCommand.ExecuteAsync(null);
        Assert.Equal(completedStatus, model.Status); Assert.True(model.CanExport);
    }
    [AvaloniaFact]
    public async Task DisposeCancelsRunningWorkAndDisposesService()
    {
        using var fixture = new Fixture(); fixture.Jobs.Block = true; var model = fixture.Model();
        model.ElevenLabsKey = "session-ele"; model.TranslationKey = "session-ds";
        model.InputPath = "fixture.mp4"; var run = model.RunCommand.ExecuteAsync(null);
        await fixture.Jobs.Started.Task; await model.DisposeAsync(); await run;
        Assert.True(fixture.Jobs.Disposed); Assert.Empty(model.ElevenLabsKey); Assert.Empty(model.TranslationKey);
    }
    [AvaloniaFact]
    public async Task IdleWindowClosesAfterSynchronousServiceDisposal()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.InitializeCommand.ExecuteAsync(null);
        window.Close();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.False(window.IsVisible); Assert.True(fixture.Jobs.Disposed);
    }
    [AvaloniaFact]
    public async Task ExportUsesStrictSrtAndDeclinedOverwriteLeavesExistingFile()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "中文 输出.srt");
        await model.ExportCommand.ExecuteAsync(null);
        var text = await File.ReadAllTextAsync(fixture.Dialogs.SavePath);
        Assert.Contains("00:00:02,000 --> 00:00:04,000", text); Assert.Contains("中文译文", text);
        await File.WriteAllTextAsync(fixture.Dialogs.SavePath, "保留内容"); fixture.Dialogs.Confirm = false;
        await model.ExportCommand.ExecuteAsync(null);
        Assert.Equal("保留内容", await File.ReadAllTextAsync(fixture.Dialogs.SavePath));
    }
    [AvaloniaFact]
    public async Task ApiKeysNeverAppearInErrorLogs()
    {
        using var fixture = new Fixture(); fixture.Jobs.Failure = new ArgumentException("bad session-secret"); await using var model = fixture.Model();
        model.TranslationKey = "session-secret"; model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.DoesNotContain("session-secret", model.Error); Assert.All(model.Log, message => Assert.DoesNotContain("session-secret", message));
    }
    [AvaloniaFact]
    public async Task SwitchingProvidersRetainsIndependentPromptEdits()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.ProfileName = "DS 模板";
        model.ProviderIndex = 2; Assert.Empty(model.SystemTemplate); Assert.Contains("{source_text}", model.UserTemplate);
        model.ProviderIndex = 1; Assert.Equal("DS 模板", model.ProfileName);
    }
    [AvaloniaFact]
    public async Task RepeatedInitializationWaitsForSameConfigurationReadBeforeSaving()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(new AppSettings { TargetLanguage = "日文" }, []);
        await using var model = fixture.Model();
        var first = model.InitializeCommand.ExecuteAsync(null);
        var second = model.InitializeCommand.ExecuteAsync(null);
        await second;
        Assert.Equal("日文", model.Settings.TargetLanguage);
        Assert.False(model.IsInitializing);
        await model.SaveSettingsCommand.ExecuteAsync(null);
        await first;
        Assert.Empty(model.Error);
    }
    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task WindowRendersRealXamlAndCommandsStayInsideBounds(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.Settings.Theme = theme;
        var window = new MainWindow { DataContext = model, Width = width, Height = height };
        window.Show(); await model.InitializeCommand.ExecutionTask!;
        var workspace = window.FindControl<WorkspaceView>("WorkspacePage")!;
        Assert.False(workspace.FindControl<Button>("ExportButton")!.IsEffectivelyEnabled);
        model.InputPath = "fixture.mp4"; await model.RunCommand.ExecuteAsync(null);
        Assert.True(workspace.FindControl<Button>("ExportButton")!.IsEffectivelyEnabled);
        Assert.Equal(2, workspace.FindControl<ListBox>("CueList")!.ItemCount);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var button = workspace.FindControl<Button>("ExportButton")!;
        Assert.True(button.Bounds.Width > 0);
        var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"window-{width}-{theme}.png")); }
        window.Close();
    }
}

internal sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "CuelifyDesktopTests", Guid.NewGuid().ToString("N"));
    public ConfigurationStore Store { get; }
    public FakeJobs Jobs { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public Fixture() { Directory.CreateDirectory(Root); Store = new(Root); }
    public MainWindowViewModel Model()
    {
        var credentials = new CredentialStore(Root);
        // 测试夹具同步建立凭证时放到后台，避免文件 I/O 续体等待已被阻塞的 UI 上下文。
        Task.Run(async () =>
        {
            if (credentials.HasMasterPassword) await credentials.UnlockAsync("test-master-password");
            else await credentials.CreateAsync("test-master-password");
            await credentials.SaveAsync(new("fake-eleven", "fake-translation", "fake-translation", "https://api.deepseek.com"));
        }).GetAwaiter().GetResult();
        return new(Jobs, Store, Dialogs, credentials);
    }
    public void Dispose() => Directory.Delete(Root, true);
}
internal sealed class FakeDialogs : IWindowDialogs
{
    public string Password { get; set; } = "test-master-password";
    public string NewPassword { get; set; } = "test-new-master-password";
    public bool AcceptPassword { get; set; } = true;
    public async Task<bool> PasswordAsync(PasswordPurpose purpose, Func<string, string, Task> submit, CancellationToken token = default)
    {
        if (!AcceptPassword) return false;
        await submit(Password, purpose == PasswordPurpose.Change ? NewPassword : Password); return true;
    }
    public string? CopiedText { get; private set; }
    public Task CopyTextAsync(string text) { CopiedText = text; return Task.CompletedTask; }
    public bool Confirm { get; set; } = true;
    public string? SavePath { get; set; }
    public string? SuggestedName { get; private set; }
    public Task<bool> ConfirmAsync(string message, CancellationToken token = default) => Task.FromResult(Confirm);
    public Task<string?> OpenAsync(string title, string[] patterns) => Task.FromResult<string?>(null);
    public Task<string?> SaveSrtAsync(string suggestedName) { SuggestedName = suggestedName; return Task.FromResult(SavePath); }
}
internal sealed class FakeJobs : IDesktopJobService
{
    public static SubtitleCue[] Cues { get; } = [new("cue-1", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null), new("cue-2", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "Goodbye.", null)];
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TranslationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseTranslation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Block, BlockTranslation, Partial, Disposed;
    public bool ReportCompletedBatch;
    public bool ReportInterleavedBatch;
    public int Transcriptions;
    public Exception? Failure;
    public Exception? ProbeFailure;
    public Exception? EngineFailure;
    public Exception? TranslationFailure;
    public string? FailureReason;
    public string? TranslationCredential;
    public AppSettings? LastSettings;
    public AppSettings? RecognitionSettings;
    public AppSettings? PreviewSettings;
    public IReadOnlySet<string>? Forced;
    public Task<MediaInfo> ProbeAsync(string input, AppSettings settings, CancellationToken token) => ProbeFailure is null ? Task.FromResult(new MediaInfo(TimeSpan.FromSeconds(5), 0)) : Task.FromException<MediaInfo>(ProbeFailure);
    public async Task<TranscriptionResult> TranscribeAsync(string input, AppSettings settings, string key, IProgress<TranscriptionProgress> progress, CancellationToken token)
    {
        RecognitionSettings = settings;
        Transcriptions++; Started.TrySetResult();
        if (Block) await Task.Delay(Timeout.Infinite, token);
        if (Failure is not null) throw Failure;
        return new("fixture", TimeSpan.FromSeconds(5), [], [], Cues, 0, 0, "fixture.srt");
    }
    public async Task<TranslationResult> TranslateAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, string key, IReadOnlySet<string>? force, IProgress<TranslationProgress> progress, CancellationToken token)
    {
        LastSettings = settings; Forced = force; TranslationCredential = key;
        if (BlockTranslation)
        {
            if (ReportCompletedBatch) progress.Report(new("Translating", [cues[1].Id]) { Cues = cues.Select((cue, index) => cue with { TranslatedText = index == 0 ? "首批译文" : null }).ToArray() });
            else progress.Report(new("Translating", [cues[0].Id]));
            if (ReportInterleavedBatch) progress.Report(new("Translating", []) { Cues = cues.Select((cue, index) => cue with { TranslatedText = index == 1 ? "后批译文" : null }).ToArray() });
            TranslationStarted.TrySetResult(); await ReleaseTranslation.Task.WaitAsync(token);
        }
        if (TranslationFailure is not null) throw TranslationFailure;
        return new TranslationResult(cues.Select(cue => cue with { TranslatedText = Partial && cue.Id == "cue-2" ? null : "中文译文" }).ToArray(), Partial ? ["cue-2"] : [], 0, 0)
        { FailureReasons = FailureReason is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["cue-2"] = FailureReason } };
    }
    public Task<string> TestEngineAsync(AppSettings settings, string key, CancellationToken token) => EngineFailure is null ? Task.FromResult("连接成功，翻译测试通过") : Task.FromException<string>(EngineFailure);
    public string Preview(IReadOnlyList<SubtitleCue> cues, AppSettings settings) { PreviewSettings = settings; return "fixture preview"; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

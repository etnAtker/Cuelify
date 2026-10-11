using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class ProjectWorkflowTests
{
    private static async Task Flush() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    [AvaloniaFact]
    public async Task ProjectBatchPersistsIndependentMediaAndRestoresWithoutCredentialsOrRequests()
    {
        using var fixture = new Fixture(); string projectId;
        await using (var model = fixture.Model())
        {
            await model.InitializeCommand.ExecuteAsync(null); model.ProjectName = "多个媒体";
            await model.CreateProjectCommand.ExecuteAsync(null);
            await model.AddMediaPathsAsync(["one.mp4", "two.wav"]);
            projectId = model.SelectedProject!.Id;
            await model.RunProjectCommand.ExecuteAsync(null);
            Assert.Equal(2, fixture.Jobs.Transcriptions); Assert.Equal(2, model.MediaTasks.Count);
            Assert.All(model.MediaTasks, m => Assert.True(m.Document.Translation.IsReady));
            model.SelectedMedia = model.MediaTasks[1]; await Flush();
        }
        var requests = fixture.Jobs.Transcriptions;
        await using var restored = fixture.Model(); await restored.InitializeCommand.ExecuteAsync(null); await Flush();
        Assert.Equal(projectId, restored.SelectedProject!.Id); Assert.Equal(2, restored.MediaTasks.Count);
        Assert.Equal("two.wav", restored.SelectedMedia!.Name); Assert.True(restored.CanExport);
        Assert.Equal(requests, fixture.Jobs.Transcriptions);
        var manifest = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Projects", projectId, "project.json"));
        Assert.DoesNotContain("fake-eleven", manifest); Assert.DoesNotContain("fake-translation", manifest);
        restored.LockCredentialsCommand.Execute(null); Assert.True(restored.CanExport);
    }
    [AvaloniaFact]
    public async Task ChangedAsrRetainsTranslationButRequiresRegenerationAndUsesLatestWords()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); model.InputPath = "one.mp4";
        fixture.Jobs.SourceWords = [new("Old", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)];
        await model.RunCommand.ExecuteAsync(null); var task = model.SelectedMedia!.Document;
        var oldTranslation = task.Translation.ResultPath; var oldState = task.Translation.State;
        fixture.Jobs.SourceWords = [new("New", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)];
        await model.RunStageCommand.ExecuteAsync("Asr:Restart");
        Assert.Empty(task.Translation.ResultPath); Assert.Equal(ProcessingState.NotStarted, task.Translation.State);
        Assert.False(model.CanExport);
        var store = new ProjectStore(fixture.Root);
        var history = await store.VersionsAsync(model.SelectedProject!.Id, task, TaskStage.Translation);
        Assert.Contains(history, v => v.ResultPath == oldTranslation && v.State == oldState);
        await model.RunStageCommand.ExecuteAsync("Subtitles:Restart");
        await model.RunStageCommand.ExecuteAsync("Translation:Restart");
        Assert.True(task.Translation.IsReady); Assert.Equal(task.Subtitles.Version, task.Translation.UpstreamVersionId);
        Assert.Equal(1, fixture.Jobs.Preparations);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MethodChangeAfterFailureKeepsStepSnapshotAndDoesNotRepeatAsr(bool failGeneration)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); model.InputPath = "one.mp4";
        if (failGeneration) fixture.Jobs.GenerationFailure = new InvalidDataException("模拟联合失败"); else fixture.Jobs.Partial = true;
        await model.RunCommand.ExecuteAsync(null);
        fixture.Jobs.GenerationFailure = null; fixture.Jobs.Partial = false;
        model.TaskPipeline = SubtitlePipelinePresets.Direct;
        Assert.True(model.TaskOptionsChanged); Assert.Equal("应用选项并处理", model.RunButtonText);
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport); Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.Equal(1, fixture.Jobs.Preparations);
        Assert.Equal(failGeneration ? SubtitlePipelinePresets.Direct.Id : "snapshot:" + SubtitlePipelinePresets.TwoStage.Id, fixture.Jobs.LastSettings!.GetPipeline().Id);
    }
    [AvaloniaFact]
    public async Task RetryFailedTranslationOnlyTargetsFailuresAndRestartKeepsPreviousVersionAndTimeline()
    {
        using var fixture = new Fixture(); fixture.Jobs.Partial = true;
        await using var model = fixture.Model(); model.InputPath = "one.mp4"; await model.RunCommand.ExecuteAsync(null);
        var task = model.SelectedMedia!.Document; var timeline = model.Rows.Select(r => (r.Id, r.Cue.Start, r.Cue.End)).ToArray();
        fixture.Jobs.Partial = false; await model.RunStageCommand.ExecuteAsync("Translation:RetryFailed");
        Assert.Equal(new[] { "cue-2" }, fixture.Jobs.Forced!.ToArray()); Assert.True(model.CanExport);
        var old = task.Translation.ResultPath;
        await model.RunStageCommand.ExecuteAsync("Translation:Restart");
        Assert.Equal(old, task.Translation.PreviousResultPath); Assert.NotEqual(old, task.Translation.ResultPath);
        Assert.Equal(timeline, model.Rows.Select(r => (r.Id, r.Cue.Start, r.Cue.End)).ToArray());
        Assert.Equal(1, fixture.Jobs.Generations); Assert.Equal(1, fixture.Jobs.Transcriptions);
    }
    [AvaloniaFact]
    public async Task ContinueTranslationDoesNotRequireSpeechKeyAndMediaSwitchDuringRunIsIsolated()
    {
        using var fixture = new Fixture(); fixture.Jobs.Partial = true;
        await using var model = fixture.Model(); await model.AddMediaPathsAsync(["one.mp4", "two.mp4"]);
        await model.RunCommand.ExecuteAsync(null); var first = model.SelectedMedia!;
        model.ElevenLabsKey = ""; fixture.Jobs.Partial = false; fixture.Jobs.BlockTranslation = true;
        var run = model.RunCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task;
        model.SelectedMedia = model.MediaTasks[1]; await Flush();
        fixture.Jobs.ReleaseTranslation.TrySetResult(); await run; await Flush();
        Assert.True(first.Document.Translation.IsReady); Assert.Empty(model.Rows);
        Assert.Equal(ProcessingState.NotStarted, model.SelectedMedia!.Document.Translation.State);
        Assert.Equal(1, fixture.Jobs.Preparations); Assert.Equal(1, fixture.Jobs.Transcriptions);
    }
    [AvaloniaFact]
    public async Task CancelBatchKeepsCurrentSuccessAndDoesNotStartNextMedia()
    {
        using var fixture = new Fixture(); fixture.Jobs.BlockTranslation = true; fixture.Jobs.ReportCompletedBatch = true;
        await using var model = fixture.Model(); await model.AddMediaPathsAsync(["one.mp4", "two.mp4"]);
        var run = model.RunProjectCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task; await Flush();
        model.CancelCommand.Execute(null); await run;
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.Equal(ProcessingState.Cancelled, model.MediaTasks[0].Document.Translation.State);
        Assert.Equal(ProcessingState.NotStarted, model.MediaTasks[1].Document.Asr.State);
        Assert.Equal("首批译文", model.Rows[0].Translation);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterMediaSwitchDoesNotChangeViewedResult(bool cancel)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        await model.AddMediaPathsAsync(["one.mp4", "two.mp4"]); await model.RunProjectCommand.ExecuteAsync(null);
        fixture.Jobs.BlockTranslation = true;
        var run = model.RunStageCommand.ExecuteAsync("Translation:Restart"); await fixture.Jobs.TranslationStarted.Task;
        model.SelectedMedia = model.MediaTasks[1]; await Flush();
        if (cancel) model.CancelCommand.Execute(null);
        else { fixture.Jobs.TranslationFailure = new HttpRequestException("模拟网络异常"); fixture.Jobs.ReleaseTranslation.TrySetResult(); }
        await run; await Flush();
        Assert.Equal("two.mp4", model.SelectedMedia!.Name);
        Assert.True(model.CanExport, $"状态：{model.Status}，错误：{model.Error}，行数：{model.Rows.Count}，就绪：{model.SelectedMedia.Document.Translation.IsReady}，忙碌：{model.IsBusy}");
        Assert.Equal("", model.Error);
        Assert.Equal("翻译完成，可以导出字幕", model.Status);
        Assert.All(model.Rows, row => Assert.Equal("中文译文", row.Translation));
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableTranslationBlocksExportAndExplicitRestartRepairsIt(bool corrupted)
    {
        using var fixture = new Fixture(); string projectId, mediaId, resultPath;
        await using (var model = fixture.Model())
        {
            model.InputPath = "one.mp4"; await model.RunCommand.ExecuteAsync(null);
            projectId = model.SelectedProject!.Id; mediaId = model.SelectedMedia!.Document.Id;
            resultPath = new ProjectStore(fixture.Root).ResolveResult(projectId, mediaId, model.SelectedMedia.Document.Translation.ResultPath);
        }
        if (corrupted) await File.WriteAllTextAsync(resultPath, "invalid-json"); else File.Delete(resultPath);
        var recognitions = fixture.Jobs.Transcriptions; var generations = fixture.Jobs.Generations;
        await using var restored = fixture.Model(); await restored.InitializeCommand.ExecuteAsync(null);
        Assert.False(restored.CanExport); Assert.True(restored.SelectedMedia!.Document.Translation.ResultUnavailable);
        Assert.Equal(recognitions, fixture.Jobs.Transcriptions);
        await restored.RunStageCommand.ExecuteAsync("Translation:Restart");
        Assert.True(restored.CanExport); Assert.False(restored.SelectedMedia.Document.Translation.ResultUnavailable);
        Assert.Equal(recognitions, fixture.Jobs.Transcriptions); Assert.Equal(generations, fixture.Jobs.Generations);
    }
    [AvaloniaFact]
    public async Task TargetLanguageChangeUsesFrozenCuesAndLatestTranslationSettings()
    {
        using var fixture = new Fixture();
        await using var model = fixture.Model(); model.InputPath = "one.mp4";
        await model.RunCommand.ExecuteAsync(null);
        var timeline = model.Rows.Select(row => (row.Id, row.Cue.Start, row.Cue.End)).ToArray();
        model.Workspace.SettingsCommand.Execute(null); model.Workspace.TargetDraft = "英语"; await model.Workspace.SubmitDialogCommand.ExecuteAsync(null);
        await WorkspaceTestActions.ExecuteAsync(model, "Translation:Retranslate");
        Assert.Equal("英语", fixture.Jobs.LastSettings!.TargetLanguage); Assert.Equal(1, fixture.Jobs.Generations);
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.Equal(timeline, model.Rows.Select(row => (row.Id, row.Cue.Start, row.Cue.End)).ToArray());
        Assert.True(model.CanExport);
    }
    [AvaloniaFact]
    public async Task RapidProjectSwitchRestoresOnlyTheFinalProjectAndMedia()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        await model.AddMediaPathsAsync(["one.mp4"]); var first = model.SelectedProject!;
        model.ProjectName = "第二项目"; await model.CreateProjectCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(["two.mp4"]); var second = model.SelectedProject!;
        model.SelectedProject = first; model.SelectedProject = second; model.SelectedProject = first;
        // 添加入口等待所有项目导航完成，也验证导航后的成员属于最终项目。
        await model.AddMediaPathsAsync(["three.mp4"]);
        Assert.Equal(first, model.SelectedProject); Assert.Equal(new[] { "one.mp4", "three.mp4" }, model.MediaTasks.Select(item => item.Name).ToArray());
        Assert.Equal("three.mp4", model.SelectedMedia!.Name);
    }
    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task ProjectAndStagesRenderAtSupportedSizes(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme;
        await model.AddMediaPathsAsync(["一个很长的中文媒体名称.mp4", "two.mp4"]); await model.RunCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show(); await Flush(); window.UpdateLayout();
        try
        {
            var workspace = window.FindControl<WorkspaceView>("WorkspacePage")!;
            var list = workspace.FindControl<WorkspaceResultsView>("Results")!.FindControl<ListBox>("CueList")!;
            Assert.True(list.Bounds.Height >= 100);
            var export = workspace.FindControl<WorkspaceResultsView>("Results")!.FindControl<Button>("ExportButton")!;
            Assert.True(export.IsEffectivelyVisible); Assert.True(export.TranslatePoint(default, window)!.Value.Y + export.Bounds.Height <= height);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var artifacts = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(artifacts)) { Directory.CreateDirectory(artifacts); frame!.Save(Path.Combine(artifacts, $"project-{width}-{theme}.png")); }
            Assert.True(workspace.FindControl<Button>("MediaSettingsButton")!.IsEffectivelyVisible);

        }
        finally { window.Close(); }
    }
}

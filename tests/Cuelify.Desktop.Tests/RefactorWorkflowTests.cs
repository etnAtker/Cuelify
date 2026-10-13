using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class RefactorWorkflowTests
{
    private static async Task Import(Fixture fixture, MainWindowViewModel model)
    {
        await model.InitializeCommand.ExecuteAsync(null);
        var path = Path.Combine(fixture.Root, "media.mp4"); await File.WriteAllTextAsync(path, "fixture");
        await model.AddMediaPathsAsync([path]);
    }
    private static async Task Tab(MainWindowViewModel model, TaskStage kind)
    { model.Workspace.SelectedStep = model.Workspace.Steps.Single(s => s.Kind == kind); await model.WaitForWorkspaceAsync(); }
    private static async Task All(MainWindowViewModel model)
    { foreach (var step in TaskStagePlan.Steps()) await WorkspaceTestActions.ExecuteAsync(model, $"{step}:Complete"); }
    private static JsonElement Legacy(JsonElement snapshot, bool missing)
    {
        var node = JsonNode.Parse(snapshot.GetRawText())!;
        var pipeline = node["PipelineLibrary"]![node["TaskPipelineId"]!.GetValue<string>()]!.AsObject();
        if (missing) pipeline.Remove("SegmentationFormat"); else pipeline["SegmentationFormat"] = 0;
        return JsonSerializer.SerializeToElement(node);
    }

    [Theory]
    [InlineData(TaskStage.Subtitles)]
    [InlineData(TaskStage.Translation)]
    public void LocalTimeoutSurvivesStageSnapshotAndChangesAreDetected(TaskStage kind)
    {
        var settings = new ConfigurationStore().CreateDefaults(); settings.Provider = TranslationProvider.Local; settings.TimeoutSeconds = 37;
        var snapshot = StageSettingsSnapshot.Capture(kind, settings);
        var restored = ConfigurationStore.ReadSnapshot(snapshot)!;
        Assert.Equal(TimeSpan.FromSeconds(37), restored.LocalOptions().InferenceTimeout);
        Assert.Equal(37, restored.Models.Single().Value.Parameters[nameof(AppSettings.TimeoutSeconds)].GetInt32());
        var stage = new StageRecord { Settings = snapshot };
        Assert.False(ProjectTaskService.ConfigurationChanged(stage, kind, settings));
        settings.TimeoutSeconds = 91;
        Assert.True(ProjectTaskService.ConfigurationChanged(stage, kind, settings));
        Assert.Equal(37, ConfigurationStore.ReadSnapshot(stage.Settings)!.TimeoutSeconds);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedLegacyBranchCanTranslateExportAndBeRestored(bool missing)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model); await All(model);
        var project = model.SelectedProject!; var task = model.SelectedMedia!.Document; var store = new ProjectStore(fixture.Root);
        task.Subtitles.Settings = Legacy(task.Subtitles.Settings!.Value, missing);
        var oldSnapshot = task.Subtitles.Settings.Value.GetRawText(); var oldVersion = task.Subtitles.Version;
        var generated = fixture.Jobs.Generations; var original = model.Rows.Select(r => r.Cue).ToArray();
        await WorkspaceTestActions.ExecuteAsync(model, "Translation:Restart");
        var oldTranslation = task.Translation.Version;
        Assert.Equal(generated, fixture.Jobs.Generations); Assert.Equal(oldVersion, task.Subtitles.Version);
        Assert.Equal(original.Select(c => (c.Id, c.Start, c.End)), model.Rows.Select(r => (r.Id, r.Cue.Start, r.Cue.End)));
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "old.srt");
        await model.Workspace.ExportCommand.ExecuteAsync(null); Assert.True(File.Exists(fixture.Dialogs.SavePath));
        await WorkspaceTestActions.ExecuteAsync(model, "Subtitles:Restart");
        Assert.NotEqual(oldVersion, task.Subtitles.Version); Assert.Empty(task.Translation.Version);
        model.Workspace.Version = model.Workspace.Versions.Single(v => v.Record.Version == oldVersion); await model.WaitForWorkspaceAsync();
        await model.Workspace.SetCurrentCommand.ExecuteAsync(null);
        Assert.Equal(oldTranslation, task.Translation.Version); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(oldSnapshot), JsonNode.Parse(task.Subtitles.Settings!.Value.GetRawText())));
        await Tab(model, TaskStage.Translation); Assert.True(model.Workspace.CanExport);
        var saved = await store.ReadTaskAsync(project.Id, task.Id);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(oldSnapshot), JsonNode.Parse(saved.Subtitles.Settings!.Value.GetRawText())));
    }

    [AvaloniaTheory]
    [InlineData(ProcessingState.Failed, false)]
    [InlineData(ProcessingState.Cancelled, true)]
    public async Task IncompleteLegacyCannotResumeThroughStepOrBatchAndRestartCreatesTexts(ProcessingState state, bool missing)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model); await All(model);
        var task = model.SelectedMedia!.Document; var project = model.SelectedProject!; var store = new ProjectStore(fixture.Root);
        task.Subtitles.Settings = Legacy(task.Subtitles.Settings!.Value, missing); task.Subtitles.State = state;
        task.Subtitles.FailedUnits = ["old-block.json"]; task.Translation = new();
        await Tab(model, TaskStage.Asr); await Tab(model, TaskStage.Subtitles);
        Assert.DoesNotContain(model.Workspace.StepOperations, c => c.Operation is "Subtitles:Complete" or "Subtitles:RetryFailed");
        Assert.Contains("请重新分句", model.Workspace.ResultError);
        var executor = new ProjectTaskService(fixture.Jobs, store, () => "fake-eleven", _ => "fake-translation");
        var settings = ConfigurationStore.ReadSnapshot(task.Options)!; var progress = new InlineProgress<ProjectTaskProgress>(_ => { });
        var snapshot = task.Subtitles.Settings.Value.GetRawText(); var version = task.Subtitles.Version; var calls = fixture.Jobs.Generations;
        foreach (var mode in new[] { ProcessingMode.Complete, ProcessingMode.RetryFailed })
            await Assert.ThrowsAsync<ArgumentException>(() => executor.RunStageAsync(project, task, TaskStage.Subtitles, mode, settings, progress, default));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.RunAllAsync(project, task, settings, progress, default));
        Assert.Equal(calls, fixture.Jobs.Generations); Assert.Equal(snapshot, task.Subtitles.Settings.Value.GetRawText());
        await WorkspaceTestActions.ExecuteAsync(model, "Subtitles:Restart");
        Assert.NotEqual(version, task.Subtitles.Version); Assert.False(PipelineSettingsMigration.IsLegacySegmentation(task.Subtitles.Settings));
        Assert.Contains(model.Workspace.Versions, v => v.Record.Version == version && JsonNode.DeepEquals(JsonNode.Parse(snapshot), JsonNode.Parse(v.Record.Settings!.Value.GetRawText())));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PureLocalBatchDoesNotUnlockCredentials(bool program)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await WorkspaceTestActions.ExecuteAsync(model, "Chunking:Complete"); await WorkspaceTestActions.ExecuteAsync(model, "Asr:Complete");
        if (program)
        {
            model.Workspace.SettingsCommand.Execute(null); model.Workspace.PipelineDraft = SubtitlePipelinePresets.Direct;
            await model.Workspace.SubmitDialogCommand.ExecuteAsync(null);
        }
        foreach (var kind in program ? new[] { TaskStage.Translation } : new[] { TaskStage.Subtitles, TaskStage.Translation })
        {
            await Tab(model, kind); model.Workspace.StepModel = model.Workspace.Models.First(m => m.Settings.Provider == TranslationProvider.Local);
            await model.Workspace.SaveStepModelCommand.ExecuteAsync(null);
        }
        model.LockCredentialsCommand.Execute(null); fixture.Dialogs.AcceptPassword = false;
        model.Workspace.BatchCommand.Execute(null); await model.Workspace.SubmitDialogCommand.ExecuteAsync(null);
        Assert.False(model.IsUnlocked); Assert.True(model.SelectedMedia!.Document.Translation.IsReady);
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.Equal(TranslationProvider.Local, fixture.Jobs.LastSettings!.Provider);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultWriteFailurePersistsFailedStateAndRetainsOriginalFailure(bool engineFails)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        foreach (var kind in new[] { TaskStage.Chunking, TaskStage.Asr, TaskStage.Subtitles }) await WorkspaceTestActions.ExecuteAsync(model, $"{kind}:Complete");
        var task = model.SelectedMedia!.Document; var project = model.SelectedProject!; var store = new ProjectStore(fixture.Root);
        fixture.Jobs.TranslationProgress = progress =>
        {
            var path = store.ResolveResult(project.Id, task.Id, task.Translation.ResultPath);
            File.Delete(path); Directory.CreateDirectory(path); // 本夹具的 result.json 无法原子替换。
            progress.Report(new("Translating", []) { Cues = FakeJobs.Cues.Select(c => c with { TranslatedText = "已验收译文" }).ToArray() });
        };
        if (engineFails) fixture.Jobs.TranslationFailure = new InvalidDataException("原始执行失败");
        var executor = new ProjectTaskService(fixture.Jobs, store, () => "fake-eleven", _ => "fake-translation");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => executor.RunStageAsync(project, task, TaskStage.Translation,
            ProcessingMode.Complete, ConfigurationStore.ReadSnapshot(task.Options)!, new InlineProgress<ProjectTaskProgress>(_ => { }), default));
        Assert.Equal(ProcessingState.Failed, task.Translation.State);
        Assert.Equal(ProcessingState.Failed, (await store.ReadTaskAsync(project.Id, task.Id)).Translation.State);
        var record = await AtomicFile.ReadJsonAsync<StageRecord>(Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Translation, task.Translation.Version), "stage.json"), default);
        Assert.Equal(ProcessingState.Failed, record!.State);
        if (engineFails) Assert.Contains("原始执行失败", error.ToString());
    }

    [AvaloniaFact]
    public async Task BatchContinuationUsesExistingSnapshotEvenWhenDraftModelIsMissing()
    {
        using var fixture = new Fixture(); fixture.Jobs.Partial = true;
        await using var model = fixture.Model(); await Import(fixture, model); await All(model);
        var task = model.SelectedMedia!.Document; var version = task.Translation.Version;
        task.ModelOverrides[TaskStage.Translation] = "missing-model";
        fixture.Jobs.Partial = false;
        model.Workspace.BatchCommand.Execute(null); await model.Workspace.SubmitDialogCommand.ExecuteAsync(null);
        Assert.Equal(version, task.Translation.Version); Assert.True(task.Translation.IsReady);
        Assert.Equal(TranslationProvider.DeepSeek, fixture.Jobs.LastSettings!.Provider);
    }

    [AvaloniaFact]
    public async Task CredentialFailureCombinedWithPersistenceFailureStillStopsBatch()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        var second = Path.Combine(fixture.Root, "second.mp4"); await File.WriteAllTextAsync(second, "fixture"); await model.AddMediaPathsAsync([second]);
        var project = model.SelectedProject!; var task = model.MediaTasks[0].Document; var store = new ProjectStore(fixture.Root);
        fixture.Jobs.TranslationFailure = new Cuelify.Infrastructure.ServiceCredentialException("翻译服务");
        fixture.Jobs.TranslationProgress = _ => Directory.CreateDirectory(Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Translation, task.Translation.Version), "stage.json"));
        await model.RunProjectCommand.ExecuteAsync(null);
        Assert.Equal(ProcessingState.Failed, project.Batch!.State);
        Assert.Equal(ProcessingState.NotStarted, model.MediaTasks[1].Document.Asr.State);
        Assert.Equal(1, fixture.Jobs.Transcriptions);
    }

    [AvaloniaFact]
    public async Task MetadataWriteFailureStillPersistsFailedTask()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        foreach (var kind in new[] { TaskStage.Chunking, TaskStage.Asr, TaskStage.Subtitles }) await WorkspaceTestActions.ExecuteAsync(model, $"{kind}:Complete");
        var task = model.SelectedMedia!.Document; var project = model.SelectedProject!; var store = new ProjectStore(fixture.Root);
        fixture.Jobs.TranslationProgress = _ => Directory.CreateDirectory(Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Translation, task.Translation.Version), "stage.json"));
        var executor = new ProjectTaskService(fixture.Jobs, store, () => "fake-eleven", _ => "fake-translation");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => executor.RunStageAsync(project, task, TaskStage.Translation,
            ProcessingMode.Complete, ConfigurationStore.ReadSnapshot(task.Options)!, new InlineProgress<ProjectTaskProgress>(_ => { }), default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(ProcessingState.Failed, task.Translation.State);
        Assert.Equal(ProcessingState.Failed, (await store.ReadTaskAsync(project.Id, task.Id)).Translation.State);
    }

    [AvaloniaFact]
    public async Task WorkspaceProgressMergesAcceptedTranslationsAndRetranslationClearsOnlySelected()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        foreach (var kind in new[] { TaskStage.Chunking, TaskStage.Asr, TaskStage.Subtitles }) await WorkspaceTestActions.ExecuteAsync(model, $"{kind}:Complete");
        fixture.Jobs.BlockTranslation = true; fixture.Jobs.ReportCompletedBatch = true; fixture.Jobs.ReportInterleavedBatch = true;
        var running = WorkspaceTestActions.ExecuteAsync(model, "Translation:Complete"); await fixture.Jobs.TranslationStarted.Task;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(new[] { "首批译文", "后批译文" }, model.Workspace.Cues.Select(c => c.Translation));
        model.CancelCommand.Execute(null); await running;
        fixture.Jobs.ReportInterleavedBatch = false; fixture.Jobs.ReportCompletedBatch = false; fixture.Jobs.ReportTargetedRetry = true;
        model.Workspace.Selection.Select(1);
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Jobs.TranslationProgress = _ => retryStarted.TrySetResult();
        var retry = WorkspaceTestActions.ExecuteAsync(model, "TranslationSelected"); await retryStarted.Task;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal("首批译文", model.Workspace.Cues[0].Translation); Assert.Null(model.Workspace.Cues[1].Cue.TranslatedText);
        model.CancelCommand.Execute(null); await retry;
    }
}

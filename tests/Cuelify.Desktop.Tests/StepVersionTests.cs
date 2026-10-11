using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class StepVersionTests
{
    [AvaloniaFact]
    public async Task MediaFailureStaysInItsStepAndSettingsShowOnlySettingsErrors()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await Run(model, "Chunking:Complete"); await Run(model, "Asr:Complete");
        fixture.Jobs.GenerationFailure = new InvalidDataException("测试分句失败");
        model.ShowSettingsCommand.Execute(null);
        await Run(model, "Subtitles:Complete");
        Assert.Contains("测试分句失败", model.SelectedMedia!.Document.Subtitles.Error);
        Assert.Empty(model.SettingsError);
        model.ShowSettingsCommand.Execute(null); model.Settings.CloudModel = "";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("模型", model.SettingsError);
    }

    [AvaloniaFact]
    public async Task ArchivedJointResultsArePreservedWithoutWorkspaceEntry()
    {
        using var fixture = new Fixture();
        string oldVersion;
        await using (var initial = fixture.Model())
        {
            await Import(fixture, initial); await All(initial);
            var task = initial.SelectedMedia!.Document; var project = initial.SelectedProject!;
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(task.Options!.Value.GetRawText())!;
            legacy["SchemaVersion"] = 4; legacy["TaskPipelineId"] = "pipeline-joint";
            task.Options = JsonSerializer.SerializeToElement(legacy); task.SchemaVersion = 2;
            task.Translation.Settings = task.Options; task.Translation.UpstreamStage = TaskStage.Asr;
            task.Translation.UpstreamVersionId = task.Asr.Version; task.Translation.InputResultPath = task.Asr.ResultPath;
            oldVersion = task.Translation.Version;
            var store = new ProjectStore(fixture.Root);
            await AtomicFile.WriteJsonAsync(Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Translation, oldVersion), "stage.json"), task.Translation, default);
            await store.SaveTaskAsync(project.Id, task);
        }
        await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await Tab(model, TaskStage.Translation);
        Assert.Empty(model.Workspace.Versions);
        Assert.False(model.Workspace.CanExport); Assert.False(model.Workspace.CanSetCurrent);
        Assert.Equal(4, model.Workspace.Steps.Count); Assert.Empty(model.SelectedMedia!.Document.Translation.Version);
        var storeAfter = new ProjectStore(fixture.Root);
        Assert.Contains(await storeAfter.VersionsAsync(model.SelectedProject!.Id, model.SelectedMedia.Document, TaskStage.Translation),
            record => record.Version == oldVersion && record.Archived && File.Exists(storeAfter.ResolveResult(model.SelectedProject.Id, model.SelectedMedia.Document.Id, record.ResultPath)));
        var window = new MainWindow { DataContext = model }; window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Content is string content && content.Contains("旧联合"));
        }
        finally { window.Close(); }
    }

    private static async Task Import(Fixture fixture, MainWindowViewModel model)
    {
        await model.InitializeCommand.ExecuteAsync(null);
        var path = Path.Combine(fixture.Root, "media.mp4"); await File.WriteAllTextAsync(path, "fixture");
        await model.AddMediaPathsAsync([path]);
    }
    private static async Task Pipeline(MainWindowViewModel model, SubtitlePipelineProfile pipeline)
    {
        model.Workspace.SettingsCommand.Execute(null); model.Workspace.PipelineDraft = pipeline;
        await model.Workspace.SubmitDialogCommand.ExecuteAsync(null); await model.WaitForWorkspaceAsync();
    }
    private static async Task Run(MainWindowViewModel model, string operation)
    {
        await WorkspaceTestActions.ExecuteAsync(model, operation);
    }
    private static async Task All(MainWindowViewModel model)
    {
        foreach (var step in model.Workspace.Steps.ToArray()) await Run(model, $"{step.Kind}:Complete");
    }
    private static async Task Tab(MainWindowViewModel model, TaskStage kind)
    { model.Workspace.SelectedStep = model.Workspace.Steps.Single(s => s.Kind == kind); await model.WaitForWorkspaceAsync(); }

    [AvaloniaFact]
    public async Task SwitchingMethodsKeepsFourTabsAndFailedVersionsSurviveNavigation()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await Pipeline(model, SubtitlePipelinePresets.TwoStage); await All(model);
        var task = model.SelectedMedia!.Document; var asr = task.Asr.Version; var segmented = task.Subtitles.Version;
        var firstResult = task.Translation.Version;
        await Pipeline(model, SubtitlePipelinePresets.Direct);
        Assert.Equal(new[] { TaskStage.Chunking, TaskStage.Asr, TaskStage.Subtitles, TaskStage.Translation }, model.Workspace.Steps.Select(s => s.Kind));
        Assert.Equal(asr, task.Asr.Version); Assert.Equal(segmented, task.Subtitles.Version); Assert.Equal(firstResult, task.Translation.Version);
        fixture.Jobs.Partial = true; await Run(model, "Translation:Restart"); var failedResult = task.Translation.Version;
        Assert.Equal(TaskStage.Subtitles, task.Translation.UpstreamStage); Assert.Equal(segmented, task.Translation.UpstreamVersionId);
        await Tab(model, TaskStage.Asr); await Tab(model, TaskStage.Translation);
        Assert.Equal(2, model.Workspace.Versions.Count); Assert.Equal(failedResult, model.Workspace.Version!.Record.Version);
        await Pipeline(model, SubtitlePipelinePresets.TwoStage);
        Assert.Equal(failedResult, task.Translation.Version); Assert.Equal(4, model.Workspace.Steps.Count);
        model.Workspace.Version = model.Workspace.Versions.Single(v => v.Record.Version == firstResult); await model.WaitForWorkspaceAsync();
        Assert.Equal(failedResult, task.Translation.Version); Assert.True(model.Workspace.CanSetCurrent);
        await model.Workspace.SetCurrentCommand.ExecuteAsync(null); Assert.Equal(firstResult, task.Translation.Version);
        await Tab(model, TaskStage.Subtitles); Assert.Single(model.Workspace.Versions);
        await Tab(model, TaskStage.Translation); Assert.Equal(2, model.Workspace.Versions.Count);
    }

    [AvaloniaFact]
    public async Task NewUpstreamVersionClearsUnmatchedDescendantsAndOldBranchCanBeRestoredAfterReopening()
    {
        using var fixture = new Fixture(); string asr, segmented, result;
        await using (var model = fixture.Model())
        {
            await Import(fixture, model); await Pipeline(model, SubtitlePipelinePresets.TwoStage); await All(model);
            var task = model.SelectedMedia!.Document; asr = task.Asr.Version; segmented = task.Subtitles.Version; result = task.Translation.Version;
            await Run(model, "Asr:Restart"); Assert.Empty(task.Subtitles.Version); Assert.Empty(task.Translation.Version);
            await Tab(model, TaskStage.Translation); Assert.Empty(model.Workspace.Versions);
        }
        var calls = fixture.Jobs.Transcriptions;
        await using var restored = fixture.Model(); await restored.InitializeCommand.ExecuteAsync(null); await Tab(restored, TaskStage.Asr);
        restored.Workspace.Version = restored.Workspace.Versions.Single(v => v.Record.Version == asr); await restored.WaitForWorkspaceAsync();
        await restored.Workspace.SetCurrentCommand.ExecuteAsync(null);
        Assert.Equal(segmented, restored.SelectedMedia!.Document.Subtitles.Version); Assert.Equal(result, restored.SelectedMedia.Document.Translation.Version);
        Assert.Equal(calls, fixture.Jobs.Transcriptions);
        await Tab(restored, TaskStage.Translation); Assert.True(restored.Workspace.CanExport);
    }

    [AvaloniaFact]
    public async Task ModelOverridesArePerStepAndRetryUsesTheSavedModelSnapshot()
    {
        using var fixture = new Fixture(); fixture.Jobs.Partial = true;
        await using var model = fixture.Model(); await Import(fixture, model); await Pipeline(model, SubtitlePipelinePresets.TwoStage);
        await Run(model, "Chunking:Complete"); await Run(model, "Asr:Complete"); await Run(model, "Subtitles:Complete"); await Run(model, "Translation:Complete");
        var task = model.SelectedMedia!.Document; var version = task.Translation.Version;
        var saved = task.Translation.Settings!.Value.Deserialize<AppSettings>()!;
        model.Workspace.StepModel = model.Workspace.Models.First(m => m.Settings.Provider == TranslationProvider.Local);
        await model.Workspace.SaveStepModelCommand.ExecuteAsync(null);
        Assert.Single(task.ModelOverrides); Assert.True(task.ModelOverrides.ContainsKey(TaskStage.Translation));
        fixture.Jobs.Partial = false; await Run(model, "Translation:RetryFailed");
        Assert.Equal(version, task.Translation.Version); Assert.Equal(saved.ModelKey(), fixture.Jobs.LastSettings!.ModelKey());
        Assert.Equal(new[] { "cue-2" }, fixture.Jobs.Forced);
        await Run(model, "Translation:Retranslate");
        Assert.Equal(TranslationProvider.Local, fixture.Jobs.LastSettings!.Provider);
        Assert.Equal(version, task.Translation.BaseResultVersionId); Assert.Equal(StageOperation.Retranslate, task.Translation.Operation);
        Assert.Equal(saved.ModelKey(), task.Subtitles.Settings!.Value.Deserialize<AppSettings>()!.ModelKey());
    }

    [AvaloniaFact]
    public async Task ProgramAndModelSegmentationShareTheSameVersionCatalogue()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await Pipeline(model, SubtitlePipelinePresets.Direct); await All(model); var task = model.SelectedMedia!.Document;
        Assert.Null(fixture.Jobs.Forced);
        var program = task.Subtitles.Version; var result = task.Translation.Version;
        await Pipeline(model, SubtitlePipelinePresets.TwoStage);
        Assert.Equal(program, task.Subtitles.Version); Assert.Equal(result, task.Translation.Version);
        await Run(model, "Subtitles:Restart"); var modelSegmentation = task.Subtitles.Version;
        Assert.Empty(task.Translation.Version); Assert.Equal(2, model.Workspace.Versions.Count);
        model.Workspace.Version = model.Workspace.Versions.Single(v => v.Record.Version == program); await model.WaitForWorkspaceAsync();
        Assert.Equal(modelSegmentation, task.Subtitles.Version);
        await model.Workspace.SetCurrentCommand.ExecuteAsync(null); Assert.Equal(result, task.Translation.Version);
    }

    [AvaloniaFact]
    public async Task LocalPreparationFailureIsPersistedAsAnIndependentStepVersion()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await Pipeline(model, SubtitlePipelinePresets.TwoStage);
        await Run(model, "Chunking:Complete"); await Run(model, "Asr:Complete"); await Tab(model, TaskStage.Subtitles);
        model.Workspace.StepModel = model.Workspace.Models.First(m => m.Settings.Provider == TranslationProvider.Local);
        await model.Workspace.SaveStepModelCommand.ExecuteAsync(null);
        fixture.Jobs.PreparationFailure = new InvalidOperationException("模型准备失败");
        await Run(model, "Subtitles:Complete");
        var task = model.SelectedMedia!.Document;
        Assert.Equal(ProcessingState.Failed, task.Subtitles.State); Assert.NotEmpty(task.Subtitles.Version);
        Assert.Equal(task.Asr.Version, task.Subtitles.UpstreamVersionId); Assert.Empty(task.Translation.Version);
        Assert.Single(model.Workspace.Versions); Assert.NotEmpty(model.Workspace.ResultError);
        fixture.Jobs.PreparationFailure = null; var failed = task.Subtitles.Version;
        await Run(model, "Subtitles:Complete"); Assert.Equal(failed, task.Subtitles.Version); Assert.True(task.Subtitles.IsReady);
    }

    [AvaloniaFact]
    public async Task CompletedVersionCannotBeOverwrittenByContinueWhenItsResultIsUnavailable()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model); await All(model);
        var task = model.SelectedMedia!.Document; var previous = task.Translation.Version;
        var store = new ProjectStore(fixture.Root);
        File.Delete(store.ResolveResult(model.SelectedProject!.Id, task.Id, task.Translation.ResultPath));
        task.Translation.ResultUnavailable = true;
        var executor = new ProjectTaskService(fixture.Jobs, store, () => "fake-eleven", _ => "fake-translation");
        await Assert.ThrowsAsync<ArgumentException>(() => executor.RunStageAsync(model.SelectedProject, task, TaskStage.Translation,
            ProcessingMode.Complete, task.Options!.Value.Deserialize<AppSettings>()!, new Progress<ProjectTaskProgress>(), CancellationToken.None));
        Assert.Equal(previous, task.Translation.Version); Assert.Equal(ProcessingState.Completed, task.Translation.State);
        await Run(model, "Translation:Restart"); Assert.NotEqual(previous, task.Translation.Version); Assert.True(task.Translation.IsReady);
    }

    [AvaloniaFact]
    public async Task StepMenuTracksTheCurrentSubtitleSelection()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model); await All(model);
        await Tab(model, TaskStage.Translation);
        Assert.False(model.Workspace.StepOperations.Single(o => o.Operation == "TranslationSelected").Available);
        model.Workspace.Selection.Select(0);
        Assert.True(model.Workspace.StepOperations.Single(o => o.Operation == "TranslationSelected").Available);
        model.Workspace.Selection.Clear();
        Assert.False(model.Workspace.StepOperations.Single(o => o.Operation == "TranslationSelected").Available);
    }

    [AvaloniaFact]
    public async Task ContinueAndRetryAreExclusiveAndPersistedCoverageGuardsExecution()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await Pipeline(model, SubtitlePipelinePresets.TwoStage); await Run(model, "Chunking:Complete"); await Run(model, "Asr:Complete");
        fixture.Jobs.GenerationFailure = new InvalidDataException("测试分句失败"); await Run(model, "Subtitles:Complete");
        var project = model.SelectedProject!; var task = model.SelectedMedia!.Document; var record = task.Subtitles;
        record.WorkPlanKnown = true; record.FailedUnits = ["block-first"];
        record.UnitStates = new() { ["block-first"] = ProcessingState.Failed, ["block-next"] = ProcessingState.NotStarted };
        var store = new ProjectStore(fixture.Root); await store.SaveTaskAsync(project.Id, task);
        await Tab(model, TaskStage.Asr); await Tab(model, TaskStage.Subtitles);
        Assert.Contains(model.Workspace.StepOperations, c => c.Operation == "Subtitles:Complete");
        Assert.DoesNotContain(model.Workspace.StepOperations, c => c.Operation.EndsWith("RetryFailed"));
        var restored = await store.ReadTaskAsync(project.Id, task.Id); Assert.True(restored.Subtitles.HasPendingWork);
        var executor = new ProjectTaskService(fixture.Jobs, store, () => "fake-eleven", _ => "fake-translation");
        await Assert.ThrowsAsync<ArgumentException>(() => executor.RunStageAsync(project, restored, TaskStage.Subtitles, ProcessingMode.RetryFailed,
            task.Options!.Value.Deserialize<AppSettings>()!, new Progress<ProjectTaskProgress>(), CancellationToken.None));
        record.UnitStates["block-next"] = ProcessingState.Completed;
        await Tab(model, TaskStage.Asr); await Tab(model, TaskStage.Subtitles);
        Assert.Contains(model.Workspace.StepOperations, c => c.Operation == "Subtitles:RetryFailed");
        Assert.DoesNotContain(model.Workspace.StepOperations, c => c.Operation.EndsWith(":Complete"));
        fixture.Jobs.GenerationFailure = null; await Run(model, "Subtitles:Restart");
        Assert.DoesNotContain(model.Workspace.StepOperations, c => c.Operation.EndsWith(":Complete") || c.Operation.EndsWith("RetryFailed"));
    }

    [AvaloniaFact]
    public async Task StepControlsBecomeEnabledAfterStartupCompletes()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.IsInitializing = true;
        var view = new WorkspaceView { DataContext = model };
        var controls = view.FindControl<WorkspaceResultsView>("Results")!;
        var button = controls.FindControl<ItemsControl>("StepActions")!;
        Assert.False(button.IsEnabled);
        await model.InitializeCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(model.Workspace.CanProcess); Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public async Task PipelineDefaultModelChangesApplyToExistingMediaWithoutChangingHistoricalSnapshots()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model);
        await Pipeline(model, SubtitlePipelinePresets.TwoStage); await All(model);
        var task = model.SelectedMedia!.Document; var oldSettings = task.Subtitles.Settings!.Value.Deserialize<AppSettings>()!;
        var local = model.Workspace.Models.First(m => m.Settings.Provider == TranslationProvider.Local).Settings.ModelKey();
        model.Settings.PipelineModelDefaults[SubtitlePipelinePresets.TwoStage.Id][TaskStage.Subtitles] = local;
        await model.SaveSettingsCommand.ExecuteAsync(null);
        await Run(model, "Subtitles:Restart");
        Assert.Equal(local, fixture.Jobs.GenerationSettings!.ModelKey());
        var versions = await new ProjectStore(fixture.Root).VersionsAsync(model.SelectedProject!.Id, task, TaskStage.Subtitles);
        Assert.Contains(versions, v => v.Settings!.Value.Deserialize<AppSettings>()!.ModelKey() == oldSettings.ModelKey());
        Assert.Empty(task.ModelOverrides);
    }

    [AvaloniaFact]
    public async Task ClickingStepOperationExecutesImmediatelyWithoutOpeningADialog()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await Import(fixture, model); await All(model);
        await Tab(model, TaskStage.Translation); model.ShowWorkspaceCommand.Execute(null);
        var window = new MainWindow { DataContext = model, Width = 1240, Height = 860 }; window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
            var view = window.FindControl<WorkspaceView>("WorkspacePage")!.FindControl<WorkspaceResultsView>("Results")!;
            var list = view.FindControl<ItemsControl>("StepActions")!;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var button = list.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is ProcessChoice { Operation: "Translation:Restart" });
            var point = button.TranslatePoint(new Point(8, 8), window)!.Value;
            var calls = fixture.Jobs.Generations; var previous = model.SelectedMedia!.Document.Translation.Version;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await model.Workspace.StepOperationCommand.ExecutionTask!; await model.WaitForWorkspaceAsync();
            Assert.False(model.Workspace.HasDialog); Assert.Equal(calls, fixture.Jobs.Generations);
            Assert.NotEqual(previous, model.SelectedMedia.Document.Translation.Version);
        }
        finally { window.Close(); }
    }
}
